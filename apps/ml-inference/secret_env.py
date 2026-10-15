"""Sirlarin dosyadan okunmasi (Docker secrets: /run/secrets/...).

.NET servislerindeki Security/SecretEnv.cs ile ayni kural: bilinen bir X degiskeni bossa ve
X_FILE tanimliysa dosyanin icerigi ortama X olarak yazilir; X doluysa hicbir sey degismez.
main.py'nin en basinda, baska moduller ortami okumadan once cagrilir.
"""
import os

NAMES = (
    "INTERNAL_SERVICE_TOKEN", "INTERNAL_SERVICE_TOKEN_PREVIOUS", "KEYCLOAK_CLIENT_SECRET",
    "AWS_ACCESS_KEY_ID", "AWS_SECRET_ACCESS_KEY",
)


def load(env=None, read=None) -> list[str]:
    """Degisen degiskenlerin ADLARINI doner (degerler asla)."""
    env = os.environ if env is None else env

    def _read(path: str) -> str:
        with open(path, encoding="utf-8") as f:
            return f.read()

    read = read or _read
    changed = []
    for name in NAMES:
        path = env.get(name + "_FILE")
        if not path or env.get(name):
            continue
        try:
            value = read(path).rstrip("\r\n")
        except OSError as e:
            raise RuntimeError(f"{name}_FILE ile verilen dosya okunamadi ({path}): {type(e).__name__}") from None
        if not value:
            raise RuntimeError(f"{name}_FILE ile verilen dosya bos ({path}).")
        env[name] = value
        changed.append(name)
    return changed

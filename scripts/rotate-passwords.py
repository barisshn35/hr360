#!/usr/bin/env python3
"""Yönetim parolalarını yeniler (platform.admin, varsa demo.admin, istenirse Keycloak master yöneticisi).

Parola bir yerde açığa çıktığında (sohbet, ekran paylaşımı, e-posta) ya da düzenli parola
yenilemesinde kullanılır. Yeni parolalar rastgele üretilir ve EKRANA BASILMAZ; yalnızca şuralara
yazılır (hepsi gitignore'da):
  - .env                     PLATFORM_ADMIN_PASSWORD, DEMO_ADMIN_PASSWORD, --keycloak-admin ile
                             KEYCLOAK_ADMIN_PASSWORD (sır scripts/secrets-migrate.sh ile secrets/*.txt'ye
                             taşınmışsa o dosyaya)
  - tests/credentials.json   YALNIZCA dosya varsa (test ortamı); aynı kullanıcı adlı kayıtlar güncellenir
  - deploy/keycloak/realm-export.json  ilk kurulum içe aktarımı; eski parola dosyada kalmasın

Kullanım:
  python3 scripts/rotate-passwords.py                    # platform.admin + (varsa) demo.admin
  python3 scripts/rotate-passwords.py platform.admin     # yalnızca seçilen kullanıcı(lar)
  python3 scripts/rotate-passwords.py --keycloak-admin   # Keycloak master yöneticisi de (keycloak + tenant-service yeniden oluşturulur)
  python3 scripts/rotate-passwords.py --keycloak-admin-only   # yalnızca Keycloak master yöneticisi
  python3 scripts/rotate-passwords.py ayse mehmet        # test ortamı: tests/credentials.json anahtarları da kabul edilir

Ek compose dosyaları kullanılıyorsa (ör. test katmanı): COMPOSE_ARGS="-f docker-compose.yml -f deploy/testing/chat-mock.yml"
Yeni parolayı görmek için (yalnızca sunucuda): grep '^PLATFORM_ADMIN_PASSWORD=' .env
"""
import json
import os
import secrets
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
ENV = ROOT / ".env"
SECRETS_DIR = ROOT / "secrets"
CRED = ROOT / "tests" / "credentials.json"
REALM = ROOT / "deploy" / "keycloak" / "realm-export.json"
# .env'de parolası tutulan hr360 kullanıcıları.
ENV_KEYS = {"platform.admin": "PLATFORM_ADMIN_PASSWORD", "demo.admin": "DEMO_ADMIN_PASSWORD"}
DEFAULT_USERS = ["platform.admin", "demo.admin"]
COMPOSE = ["docker", "compose", *os.environ.get("COMPOSE_ARGS", "").split()]


def read_env():
    out = {}
    for line in ENV.read_text().splitlines():
        if "=" in line and not line.lstrip().startswith("#"):
            k, v = line.split("=", 1)
            out[k.strip()] = v.strip().strip("'")
    return out


def secret_file(key):
    return SECRETS_DIR / f"{key.lower()}.txt"


def secret_get(env, key):
    """Önce .env, boşsa secrets/<anahtar>.txt (scripts/lib/secrets.sh ile aynı kural)."""
    v = env.get(key, "")
    if not v and secret_file(key).is_file():
        v = secret_file(key).read_text().strip()
    return v


def secret_set(key, value):
    """Sır dosyaya taşınmışsa dosyaya (izinler korunur), değilse .env'e yazar."""
    f = secret_file(key)
    if f.is_file() and not read_env().get(key):
        f.write_text(value + "\n")
        return
    lines = ENV.read_text().splitlines()
    done = False
    for i, line in enumerate(lines):
        if line.split("=", 1)[0].strip() == key and not line.lstrip().startswith("#"):
            lines[i] = f"{key}={value}"
            done = True
    if not done:
        lines.append(f"{key}={value}")
    ENV.write_text("\n".join(lines) + "\n")
    os.chmod(ENV, 0o600)


def new_password():
    # Keycloak parola politikası: büyük/küçük harf, rakam ve özel karakter garanti edilir.
    return "Hr360-" + secrets.token_urlsafe(15) + "9a"


def keycloak_container():
    out = subprocess.run([*COMPOSE, "ps", "-q", "keycloak"], cwd=ROOT, capture_output=True, text=True)
    cid = out.stdout.strip().splitlines()
    if out.returncode != 0 or not cid:
        raise SystemExit("Keycloak konteyneri çalışmıyor (docker compose ps keycloak).")
    return cid[0]


def kcadm(cid, admin_user, admin_password, realm, *args):
    # Parolalar komut satırında görünmesin diye ortam değişkeniyle aktarılır.
    script = ('/opt/keycloak/bin/kcadm.sh config credentials --config /tmp/kc-rotate.cfg --server http://localhost:8080/auth '
              '--realm master --user "$RU" --password "$RP" >/dev/null 2>&1 || exit 97; '
              f'/opt/keycloak/bin/kcadm.sh "$@" --config /tmp/kc-rotate.cfg -r {realm}; rc=$?; rm -f /tmp/kc-rotate.cfg; exit $rc')
    out = subprocess.run(["docker", "exec", "-e", "RU", "-e", "RP", cid, "sh", "-c", script, "kcadm", *args],
                         capture_output=True, text=True, env={**os.environ, "RU": admin_user, "RP": admin_password})
    if out.returncode == 97:
        raise SystemExit("Keycloak yöneticisiyle oturum açılamadı (.env KEYCLOAK_ADMIN_USER/KEYCLOAK_ADMIN_PASSWORD).")
    if out.returncode != 0:
        raise SystemExit(f"kcadm {args[0]}: {out.stderr.strip()[:300]}")
    return out.stdout


def main(argv):
    if any(a in ("-h", "--help") for a in argv):
        print(__doc__)
        return
    if not ENV.exists():
        raise SystemExit(".env bulunamadı; depo kökünde, kurulu bir sunucuda çalıştırın.")
    master_only = "--keycloak-admin-only" in argv
    rotate_master = master_only or "--keycloak-admin" in argv
    unknown_opts = [a for a in argv if a.startswith("-") and a not in ("--keycloak-admin", "--keycloak-admin-only")]
    if unknown_opts:
        raise SystemExit("bilinmeyen seçenek: " + ", ".join(unknown_opts))
    wanted = [a for a in argv if not a.startswith("-")]

    env = read_env()
    admin_user = env.get("KEYCLOAK_ADMIN_USER") or "admin"
    admin_pw = secret_get(env, "KEYCLOAK_ADMIN_PASSWORD")
    if not admin_pw:
        raise SystemExit("KEYCLOAK_ADMIN_PASSWORD bulunamadı (.env ya da secrets/).")
    creds = json.loads(CRED.read_text()) if CRED.exists() else {}
    # credentials.json anahtarı (ör. "ayse") verilirse kullanıcı adına çevrilir.
    users = []
    for w in ([] if master_only else (wanted or DEFAULT_USERS)):
        username = creds[w][0] if w in creds else w
        if username not in ENV_KEYS and not any(v[0] == username for v in creds.values()):
            raise SystemExit(f"{w}: yeni parola yazılacak yer yok (.env'de yalnızca platform.admin ve demo.admin; "
                             "diğer kullanıcılar için tests/credentials.json gerekir).")
        users.append(username)

    cid = keycloak_container()
    realm_text = REALM.read_text() if REALM.exists() else None
    for username in dict.fromkeys(users):
        found = json.loads(kcadm(cid, admin_user, admin_pw, "hr360", "get", "users", "-q", f"username={username}",
                                 "-q", "exact=true", "--fields", "id") or "[]")
        if not found:
            print(f"  {username}: Keycloak'ta yok, atlandı")
            continue
        pw = new_password()
        kcadm(cid, admin_user, admin_pw, "hr360", "set-password", "--userid", found[0]["id"], "--new-password", pw)
        olds = []
        if username in ENV_KEYS:
            olds.append(env.get(ENV_KEYS[username], ""))
            secret_set(ENV_KEYS[username], pw)
        for key, (u, old) in list(creds.items()):
            if u == username:
                olds.append(old)
                creds[key] = [u, pw]
                Path(f"/tmp/hr360-tok-{key}.json").unlink(missing_ok=True)
        if creds and CRED.exists():
            CRED.write_text(json.dumps(creds, indent=2) + "\n")
            os.chmod(CRED, 0o600)
        if realm_text:
            for old in filter(None, olds):
                realm_text = realm_text.replace(json.dumps(old), json.dumps(pw))
        print(f"  {username}: parola yenilendi")
    if realm_text is not None:
        REALM.write_text(realm_text)

    if rotate_master:
        pw = new_password()
        kcadm(cid, admin_user, admin_pw, "master", "set-password", "--username", admin_user, "--new-password", pw)
        secret_set("KEYCLOAK_ADMIN_PASSWORD", pw)
        # Betikler ve testler kcadm'e konteynerin KEYCLOAK_ADMIN_PASSWORD ortam değişkeniyle girer; eski değerle
        # kalan konteyner tekrarlanan hatalı girişlerle master yöneticisini kilitler. Keycloak bu yüzden yeni
        # değerle yeniden oluşturulur. tenant-service de eski kurulumlarda (KEYCLOAK_TENANT_ADMIN_LEGACY_*
        # tanımsız) yönetim API'sine bu parolayla girer.
        subprocess.run([*COMPOSE, "up", "-d", "--no-deps", "--no-build", "keycloak", "tenant-service"], cwd=ROOT,
                       check=True, capture_output=True)
        print(f"  Keycloak master yöneticisi ({admin_user}): parola yenilendi; keycloak ve tenant-service yeni "
              "değerle yeniden oluşturuldu (giriş ekranı ~1 dk kesilebilir)")

    where = ".env ve tests/credentials.json dosyalarında" if CRED.exists() else ".env dosyasında"
    print(f"Yeni parolalar ekrana basılmadı: {where} (sır secrets/ altına taşındıysa oradaki dosyada).")


if __name__ == "__main__":
    main(sys.argv[1:])

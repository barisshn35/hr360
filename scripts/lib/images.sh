# shellcheck shell=bash
# Hazir imajlar: CI (.github/workflows/ci.yml, "Docker imajlari") main'deki her commit icin servis
# imajlarini derler, ghcr.io/barisshn35/hr360/<servis>:sha-<commit> olarak yayinlar ve cosign ile
# anahtarsiz imzalar (GitHub Actions kimligi). Sunucu derlemek yerine bunlari indirir; imza bu deponun
# main dalindaki CI is akisina ait degilse imaj kullanilmaz. Indirme ya da dogrulama olmazsa cagiran
# betik bugunku gibi sunucuda derler (ayni etiketle; compose imaj adi docker-compose.yml'de).
#
#   . scripts/lib/images.sh
#   images_use_tag                       # .env'e HR360_IMAGE_TAG yazar (sha-<HEAD> ya da local) ve disari aktarir
#   images_pull SERVIS... || docker compose build SERVIS...
#   images_prune ONCEKI_ETIKET           # gecerli ve onceki etiket disindaki eski imajlari siler
#
# .env: HR360_PREBUILT_IMAGES=0 hazir imaji kapatir (her zaman derlenir). HR360_REGISTRY ve
# HR360_IMAGE_SIGNER yalnizca depo catallandiginda degisir.

: "${ENV_FILE:=.env}"
IMAGES_COSIGN="ghcr.io/sigstore/cosign/cosign:v3.1.3@sha256:9e5c2f2edc34351160407ca3416c61855bdf9403c3c5936e0f0be7fc261611b8"
IMAGES_ISSUER="https://token.actions.githubusercontent.com"

images_env() { # images_env KEY -> .env'deki deger
  [ -f "$ENV_FILE" ] && grep "^$1=" "$ENV_FILE" | tail -1 | cut -d= -f2- || true
}

images_set_env() { # images_set_env KEY VALUE (deger komut satirina dusmez; dosya yerinde yazilir)
  if ! declare -F env_file_set >/dev/null; then
    # shellcheck source=scripts/lib/secrets.sh
    . "$(dirname "${BASH_SOURCE[0]}")/secrets.sh"
  fi
  env_file_set "$1" "$2"
}

# Hazir imaj kullanilacaksa "sha-<HEAD>", degilse "local". Kaydedilmemis degisiklik varken (gelistirme)
# ya da git yoksa imaj kodla ayni olmaz: local.
images_tag() {
  local head
  [ "$(images_env HR360_PREBUILT_IMAGES)" != 0 ] || { echo local; return; }
  head="$(git rev-parse -q --verify HEAD 2>/dev/null)" || { echo local; return; }
  [ -z "$(git status --porcelain --untracked-files=no 2>/dev/null)" ] || { echo local; return; }
  echo "sha-$head"
}

images_use_tag() {
  HR360_IMAGE_TAG="$(images_tag)"; export HR360_IMAGE_TAG
  [ -f "$ENV_FILE" ] && images_set_env HR360_IMAGE_TAG "$HR360_IMAGE_TAG"
  return 0
}

# compose'daki imaj adlari (SERVIS -> imaj); HR360_IMAGE_TAG ortamdan.
images_names() {
  docker compose config --format json 2>/dev/null | python3 -c '
import json, sys
want = set(sys.argv[1:])
try:
    services = json.load(sys.stdin)["services"]
except (ValueError, KeyError):
    sys.exit(0)
for name, svc in services.items():
    if name in want and svc.get("image"):
        print(svc["image"])' "$@"
}

# Servis imajlarini indirir ve imzalarini dogrular; hepsi tamamsa 0. Dogrulanamayan imaj yerelden
# silinir (cagiran ayni etiketle derler).
images_pull() {
  [ $# -gt 0 ] || return 0
  [ "${HR360_IMAGE_TAG:-local}" != local ] || return 1
  local signer imgs=() refs=() img ref
  signer="$(images_env HR360_IMAGE_SIGNER)"
  signer="${signer:-^https://github\.com/barisshn35/hr360/\.github/workflows/ci\.yml@refs/heads/main$}"
  while IFS= read -r img; do [ -n "$img" ] && imgs+=("$img"); done < <(images_names "$@")
  [ "${#imgs[@]}" -eq "$#" ] || return 1
  echo "==> Hazir imajlar indiriliyor (${HR360_IMAGE_TAG:0:16}): $*" >&2
  for img in "${imgs[@]}"; do
    docker pull -q "$img" >/dev/null 2>&1 || { echo "    Hazir imaj bulunamadi: $img; sunucuda derlenecek." >&2; return 1; }
    # Dogrulanan, indirilenin kendisi olsun: etiket degil ozet (digest).
    ref="$(docker image inspect --format '{{range .RepoDigests}}{{println .}}{{end}}' "$img" 2>/dev/null | grep -m1 "^${img%:*}@sha256:")"
    [ -n "$ref" ] || { echo "    Imaj ozeti okunamadi: $img" >&2; return 1; }
    refs+=("$ref")
  done
  if ! docker run --rm "$IMAGES_COSIGN" verify --certificate-oidc-issuer "$IMAGES_ISSUER" \
       --certificate-identity-regexp "$signer" "${refs[@]}" >/dev/null 2>&1; then
    echo "UYARI: hazir imajlarin imzasi dogrulanamadi; kullanilmiyor, sunucuda derlenecek." >&2
    docker image rm "${imgs[@]}" >/dev/null 2>&1 || true
    return 1
  fi
  echo "    ${#imgs[@]} imaj indirildi, imzalar dogrulandi." >&2
}

# Eski hazir/derlenmis imajlar: gecerli ve ONCEKI etiket (geri donus icin) disindakiler silinir.
# Calisan bir konteynerin imaji silinemez; hata yok sayilir. Eski adlandirmayla (<proje>-<servis>)
# kalan imajlar da temizlenir.
images_prune() {
  local keep_prev="${1:-}" reg old
  reg="$(images_env HR360_REGISTRY)"; reg="${reg:-ghcr.io/barisshn35/hr360}"
  old="$(docker image ls --format '{{.Repository}}:{{.Tag}}' 2>/dev/null | grep "^$reg/[a-z0-9-]*:sha-" \
    | grep -v -e ":${HR360_IMAGE_TAG:-local}\$" ${keep_prev:+-e ":$keep_prev\$"} || true)"
  # Eski adlandirma: compose'un proje adiyla verdigi <proje>-<derlenen servis>:latest.
  old="$old $(docker compose config --format json 2>/dev/null | python3 -c '
import json, sys
try:
    cfg = json.load(sys.stdin)
except ValueError:
    sys.exit(0)
name = cfg.get("name")
if name:
    print(" ".join(f"{name}-{n}:latest" for n, s in cfg.get("services", {}).items() if "build" in s))' 2>/dev/null \
    | tr ' ' '\n' | grep -Fx -f - <(docker image ls --format '{{.Repository}}:{{.Tag}}' 2>/dev/null) || true)"
  # shellcheck disable=SC2086
  [ -z "${old// /}" ] || docker image rm $old >/dev/null 2>&1 || true
}

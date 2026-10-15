# shellcheck shell=bash
# Sir okuma/yazma yardimcilari. Bir sir ya .env'dedir ya da scripts/secrets-migrate.sh ile
# secrets/<anahtar kucuk harf>.txt dosyasina tasinmistir (konteynere Docker secret olarak
# /run/secrets/<ad> yoluyla verilir, servis X_FILE ile okur). Degerler asla ekrana basilmaz.
#
#   . scripts/lib/secrets.sh
#   v="$(secret_get TENANT_SECRET_KEY)"        # once .env, bossa secrets/tenant_secret_key.txt
#   secret_in_file TENANT_SECRET_KEY && ...    # dosyaya tasinmis mi
#   secret_set TENANT_SECRET_KEYS "$yeni"      # dosyadaysa dosyaya, degilse .env'e yazar
#
# Ortam: ENV_FILE (varsayilan .env), SECRETS_DIR (varsayilan secrets). Depo kokunde calisir.

: "${ENV_FILE:=.env}"
: "${SECRETS_DIR:=secrets}"

# Uygulama konteynerlerinin grubu (.NET imajlarinda app kullanicisi 1654:1654). Sir dosyalari
# 0640 ve bu grupla yazilir; 0600 olsaydi root olmayan konteyner kullanicisi okuyamazdi.
: "${SECRETS_GID:=1654}"

secret_name() { printf '%s' "$1" | tr '[:upper:]' '[:lower:]'; }
secret_file() { printf '%s/%s.txt' "$SECRETS_DIR" "$(secret_name "$1")"; }
secret_in_file() { [ -f "$(secret_file "$1")" ]; }

env_file_get() { # env_file_get KEY -> .env'deki deger (yoksa bos)
  [ -f "$ENV_FILE" ] || return 0
  grep "^$1=" "$ENV_FILE" | tail -1 | cut -d= -f2- || true
}

secret_get() { # secret_get KEY
  local v
  v="$(env_file_get "$1")"
  if [ -z "$v" ] && secret_in_file "$1"; then v="$(tr -d '\r\n' < "$(secret_file "$1")")"; fi
  printf '%s' "$v"
}

# env_file_set KEY VALUE: deger komut satirina dusmesin diye awk'a ortamdan verilir; dosya yerinde
# yeniden yazilir (sahiplik ve izinler korunur). VALUE bos ve UNSET=1 ise satir silinir.
env_file_set() {
  local tmp
  tmp="$(mktemp "${TMPDIR:-/tmp}/hr360-env.XXXXXX")"
  chmod 600 "$tmp"
  K="$1" V="$2" U="${UNSET:-0}" awk '
    BEGIN { k = ENVIRON["K"]; v = ENVIRON["V"]; u = ENVIRON["U"]; done = 0 }
    index($0, k "=") == 1 { if (!done && u != "1") print k "=" v; done = 1; next }
    { print }
    END { if (!done && u != "1") print k "=" v }
  ' "$ENV_FILE" > "$tmp"
  cat "$tmp" > "$ENV_FILE"
  rm -f "$tmp"
}

# Sir dosyasini guvenli yazar: dizin 0700, dosya 0640 ve grubu SECRETS_GID (Docker secret dosyanin
# kendisini baglar; dizin izni konteyneri etkilemez). Grup degistirilemezse (root degil ve gruba uye
# degil) docker ile denenir; o da olmazsa uyarir.
secret_write_file() { # secret_write_file KEY VALUE
  local f
  f="$(secret_file "$1")"
  mkdir -p "$SECRETS_DIR"
  chmod 700 "$SECRETS_DIR"
  ( umask 077; printf '%s\n' "$2" > "$f" )
  chmod 640 "$f"
  chgrp "$SECRETS_GID" "$f" 2>/dev/null && return 0
  if command -v docker >/dev/null 2>&1 \
     && docker run --rm --network none -v "$(cd "$SECRETS_DIR" && pwd):/s" "${SECRETS_HELPER_IMAGE:-alpine:3.20}" \
          chgrp "$SECRETS_GID" "/s/$(basename "$f")" >/dev/null 2>&1; then
    return 0
  fi
  echo "UYARI: $f grubu $SECRETS_GID yapilamadi; konteyner okuyamayabilir (sudo chgrp $SECRETS_GID $f)." >&2
}

secret_set() { # secret_set KEY VALUE
  if secret_in_file "$1"; then secret_write_file "$1" "$2"; else env_file_set "$1" "$2"; fi
}

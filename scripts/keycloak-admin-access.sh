#!/usr/bin/env bash
# Keycloak yonetim paneline (ve master realm'e) erisimi ayarlar.
#
# Kullanim:
#   scripts/keycloak-admin-access.sh open
#       Varsayilan. Panel ana adreste herkese acik (PUBLIC_ORIGIN/auth/admin).
#
#   scripts/keycloak-admin-access.sh ip 203.0.113.10,10.20.0.0/16
#       Panel ana adreste yalnizca verilen IP/CIDR'lere acik (nginx allow/deny).
#       Digerleri 403 alir. Uygulama girisi (hr360 realm) etkilenmez.
#
#   scripts/keycloak-admin-access.sh port [IP/CIDR,...]
#       Panel ana adreste TAMAMEN kapali; yalnizca ayri porttan
#       (KEYCLOAK_ADMIN_PORT, varsayilan 8090; ana adres HTTPS ise ayni sertifikayla) yayinlanir. Bu portu firewall ile
#       yonetim IP'lerine acin (ornekler: docs/runbooks/keycloak-yonetim-paneli-erisimi.md).
#       IP listesi verilirse nginx ayrica o portta da kisitlar (iki katman).
#
#   scripts/keycloak-admin-access.sh status
#       Gecerli ayari gosterir.
#
# Ayar .env'e (KEYCLOAK_ADMIN_MODE, KEYCLOAK_ADMIN_ALLOWED_IPS, KEYCLOAK_ADMIN_BIND,
# KEYCLOAK_ADMIN_URL) yazilir; nginx kurali deploy/nginx/keycloak-admin/ altina
# uretilir ve gerekli konteynerler guncellenir.
#
# NOT: nginx istemci IP'sini dogrudan baglantidan ($remote_addr) alir. Gateway'in
# onunde baska bir yuk dengeleyici/ters vekil varsa kisit o cihazda yapilmali ya da
# nginx'te real_ip modulu yapilandirilmalidir; aksi halde herkes dengeleyicinin IP'si
# gibi gorunur.
set -euo pipefail

cd "$(dirname "$0")/.."
# Docker Compose kabuktaki degiskenleri .env'e tercih eder; kabukta eski degerler
# (orn. "set -a; . ./.env") kalmissa yeni ayar uygulanmaz. Yonetilen degiskenler temizlenir.
unset PUBLIC_URL PUBLIC_ORIGIN KEYCLOAK_ADMIN_MODE KEYCLOAK_ADMIN_ALLOWED_IPS KEYCLOAK_ADMIN_BIND KEYCLOAK_ADMIN_PORT KEYCLOAK_ADMIN_URL
ENV_FILE=.env
DIR=deploy/nginx/keycloak-admin
MAIN="$DIR/main-access.conf"
PORT_FILE="$DIR/port-access.conf"

# GNU sed (Linux) ve BSD sed (macOS) icin ortak "yerinde degistir".
sed_i() { if sed --version >/dev/null 2>&1; then sed -i "$@"; else sed -i '' "$@"; fi; }
die() { echo "HATA: $*" >&2; exit 1; }
[ -f "$ENV_FILE" ] || die ".env bulunamadi; once install.sh calistirin."

# Docker grup yetkisi bu oturumda henuz aktif degilse sudo ile devam edilir.
if ! declare -F docker >/dev/null && ! docker info >/dev/null 2>&1 && sudo docker info >/dev/null 2>&1; then
  docker() { command sudo docker "$@"; }
fi
mkdir -p "$DIR"

set_env() { # set_env KEY VALUE
  local k="$1" v="$2"
  if grep -q "^${k}=" "$ENV_FILE"; then
    sed_i "s#^${k}=.*#${k}=${v}#" "$ENV_FILE"
  else
    printf '%s=%s\n' "$k" "$v" >> "$ENV_FILE"
  fi
}
get_env() { grep "^$1=" "$ENV_FILE" | tail -1 | cut -d= -f2- || true; }

# IP/CIDR listesini dogrula ve nginx allow satirlarina cevir.
allow_lines() {
  local list="$1" out="" item
  IFS=',' read -ra items <<< "$list"
  for item in "${items[@]}"; do
    item="$(echo "$item" | tr -d '[:space:]')"
    [ -z "$item" ] && continue
    if [[ ! "$item" =~ ^[0-9]{1,3}(\.[0-9]{1,3}){3}(/[0-9]{1,2})?$ ]] \
       && [[ ! "$item" =~ ^[0-9a-fA-F:]+(/[0-9]{1,3})?$ ]]; then
      die "gecersiz IP/CIDR: '$item'"
    fi
    out+="allow ${item};"$'\n'
  done
  [ -n "$out" ] || die "en az bir IP/CIDR verin"
  printf '%s' "$out"
}

header="# scripts/keycloak-admin-access.sh tarafindan uretildi - elle degistirmeyin."

public_origin() {
  local o; o="$(get_env PUBLIC_ORIGIN)"
  [ -n "$o" ] || o="$(get_env PUBLIC_URL)"
  [ -n "$o" ] || o="http://localhost"
  echo "${o%/}"
}

# Port modunda ana adres HTTPS ise yonetim portu da ayni sertifikayla TLS'li sunulur (konteyner 8443).
# Sertifika satirlari scripts/tls.sh'in urettigi deploy/nginx/tls/listen.conf'tan alinir. Donus: 0 = TLS acik.
TLS_SERVER="$DIR/server-tls.conf"
write_tls_server() {
  local src=deploy/nginx/tls/listen.conf cert key
  rm -f "$TLS_SERVER"
  [ -f "$src" ] || return 1
  cert="$(sed -n 's/^ssl_certificate[[:space:]]\{1,\}\([^;]*\);.*/\1/p' "$src" | head -1)"
  key="$(sed -n 's/^ssl_certificate_key[[:space:]]\{1,\}\([^;]*\);.*/\1/p' "$src" | head -1)"
  [ -n "$cert" ] && [ -n "$key" ] || return 1
  cat > "$TLS_SERVER" <<CONF
$header
server {
    listen 8443 ssl;
    server_name _;
    server_tokens off;
    ssl_certificate     $cert;
    ssl_certificate_key $key;
    ssl_protocols       TLSv1.2 TLSv1.3;
    include /etc/nginx/keycloak-admin/port-*.conf;

    location = / {
        return 302 /auth/admin/;
    }

    location /auth/ {
        proxy_pass http://keycloak_backend;
        proxy_set_header Host \$http_host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$remote_addr;
        proxy_set_header X-Forwarded-Proto https;
        proxy_set_header X-Forwarded-Host \$http_host;
    }

    location / {
        return 404;
    }
}
CONF
  return 0
}

apply() {
  echo "Gateway ve Keycloak guncelleniyor..."
  docker compose up -d keycloak gateway >/dev/null 2>"${TMPDIR:-/tmp}/hr360-compose.err" \
    || die "docker compose up basarisiz: $(tail -3 "${TMPDIR:-/tmp}/hr360-compose.err")"
  # Kural dosyasi degistiginde gateway konteyneri yeniden olusmayabilir; nginx'e
  # yeni kurali okutmak icin once dogrula, sonra yeniden yukle.
  docker compose exec -T gateway nginx -t >/dev/null 2>&1 || die "nginx kurali gecersiz; degisiklik uygulanmadi"
  docker compose exec -T gateway nginx -s reload >/dev/null 2>&1
  echo "Tamam. Keycloak yeniden basladiktan sonra (yaklasik 30 sn) gecerli olur."
}

# master realm (yonetici girisi) hangi adresten sunulsun. Port modunda yonetim
# portuna cevrilir; aksi halde yonetim konsolu girisi ana adresteki master realm'e
# gider ve orasi kapali oldugu icin giris ekrani hic acilmaz. Bos = ana adres.
set_master_frontend() {
  local url="$1"
  # Yonetim girisi (master realm) icin SSL kurali: ayri port (8090) duz HTTP
  # oldugundan port modunda "none" (erisim firewall/IP listesiyle kisitli olmali);
  # diger modlarda ana adresin durumuna uyar (HTTPS aciksa "external").
  local ssl=none
  if [ -z "$url" ] && [ -f deploy/nginx/tls/listen.conf ]; then ssl=external; fi
  case "$url" in https://*) ssl=external ;; esac
  echo "Keycloak hazir olana kadar bekleniyor..."
  for _ in $(seq 1 60); do
    docker compose exec -T keycloak sh -c 'exec 3<>/dev/tcp/127.0.0.1/8080' >/dev/null 2>&1 && break
    sleep 3
  done
  # Master realm'de kaba kuvvet korumasi yalnizca tenant-service yonetim API'sine kendi servis
  # hesabiyla (hr360 realm'i, client_credentials) giriyorsa acilir. Eskiden tenant-service ayni
  # master yonetici hesabiyla sik ve eszamanli parola girisi yapiyordu; koruma bunu saldiri sayip
  # hesabi gecici olarak kilitliyordu (user_temporarily_disabled, guvenlik ekrani 500 veriyordu).
  # Servis hesabi anahtari .env'de yoksa (scripts/keycloak-service-account.sh calistirilmamis)
  # koruma acilmaz; o durumda panel erisimini IP listesiyle kisitlayin.
  local bf=false
  [ -n "$(get_env KEYCLOAK_TENANT_ADMIN_CLIENT_SECRET)" ] && bf=true
  docker compose exec -T -e URL="$url" -e SSL="$ssl" -e BF="$bf" keycloak sh -c '
    K=/opt/keycloak/bin/kcadm.sh; C=/tmp/kcadm-access.config
    for i in $(seq 1 30); do
      $K config credentials --config $C --server http://127.0.0.1:8080/auth --realm master \
        --user "$KEYCLOAK_ADMIN" --password "$KEYCLOAK_ADMIN_PASSWORD" >/dev/null 2>&1 && break
      sleep 3
    done
    # Anahtar tanimli olsa da istemci Keycloak uzerinde yoksa (betik yarida kalmis) tenant-service eski
    # yola (master parola girisi) duser; o durumda da koruma acilmaz.
    if [ "$BF" = true ] && [ -z "$($K get clients -r hr360 -q clientId=hr360-tenant-admin --fields id --format csv --noquotes --config $C 2>/dev/null)" ]; then
      BF=false
      echo "UYARI: hr360-tenant-admin istemcisi yok; master kaba kuvvet korumasi acilmadi (scripts/keycloak-service-account.sh)." >&2
    fi
    # Yonetim/giris olaylarinin 90 gun kaydi (kim neyi degistirdi; master realm yalnizca yoneticiler).
    # Kaba kuvvet: 10 hatali denemede artan bekleme (en fazla 15 dk), kalici kilit yok.
    $K update realms/master --config $C -s "attributes.frontendUrl=$URL" -s "sslRequired=$SSL" \
      -s eventsEnabled=true -s eventsExpiration=7776000 -s adminEventsEnabled=true -s adminEventsDetailsEnabled=false \
      -s "bruteForceProtected=$BF" -s permanentLockout=false -s failureFactor=10 -s waitIncrementSeconds=60 \
      -s maxFailureWaitSeconds=900 -s maxDeltaTimeSeconds=43200 && rm -f $C
    [ "$BF" = true ] && echo "Master realm kaba kuvvet korumasi: acik" || echo "Master realm kaba kuvvet korumasi: kapali (servis hesabi yok)"
  ' || die "master realm adresi guncellenemedi"
}

mode="${1:-status}"
case "$mode" in
  open)
    rm -f "$MAIN"
    printf '%s\ndeny all;\n' "$header" > "$PORT_FILE"
    set_env KEYCLOAK_ADMIN_MODE open
    rm -f "$TLS_SERVER"
    set_env KEYCLOAK_ADMIN_CONTAINER_PORT 8090
    set_env KEYCLOAK_ADMIN_ALLOWED_IPS ""
    set_env KEYCLOAK_ADMIN_BIND 127.0.0.1
    set_env KEYCLOAK_ADMIN_URL ""
    apply
    set_master_frontend ""
    echo "Yonetim paneli: $(public_origin)/auth/admin/ (herkese acik)"
    ;;
  ip)
    ips="${2:-}"; [ -n "$ips" ] || die "kullanim: $0 ip <IP/CIDR,...>"
    lines="$(allow_lines "$ips")"
    printf '%s\n%sdeny all;\n' "$header" "$lines" > "$MAIN"
    printf '%s\ndeny all;\n' "$header" > "$PORT_FILE"
    set_env KEYCLOAK_ADMIN_MODE ip
    rm -f "$TLS_SERVER"
    set_env KEYCLOAK_ADMIN_CONTAINER_PORT 8090
    set_env KEYCLOAK_ADMIN_ALLOWED_IPS "$ips"
    set_env KEYCLOAK_ADMIN_BIND 127.0.0.1
    set_env KEYCLOAK_ADMIN_URL ""
    apply
    set_master_frontend ""
    echo "Yonetim paneli: $(public_origin)/auth/admin/ (yalnizca: $ips)"
    ;;
  port)
    ips="${2:-}"
    printf '%s\ndeny all;\n' "$header" > "$MAIN"
    if [ -n "$ips" ]; then
      lines="$(allow_lines "$ips")"
      printf '%s\n%sdeny all;\n' "$header" "$lines" > "$PORT_FILE"
    else
      printf '%s\n# IP kisiti firewall ile yapiliyor.\n' "$header" > "$PORT_FILE"
    fi
    port="$(get_env KEYCLOAK_ADMIN_PORT)"; [ -n "$port" ] || port=8090
    origin="$(public_origin)"
    scheme=http; container_port=8090
    if write_tls_server; then scheme=https; container_port=8443; fi
    # Yonetim konsolunun adresi: ana adresin host'u + yonetim portu. Bu port nginx'te
    # duz HTTP dinler (TLS yok); ana adres HTTPS olsa bile sema http'dir. Onceden
    # semayi ana adresten aldigi icin HTTPS acikken https://host:8090 uretiyordu ve
    # yonetim konsolu hic acilmiyordu.
    admin_origin="${scheme}://$(echo "$origin" | sed -E 's#^https?://([^/:]+).*$#\1#'):${port}"
    set_env KEYCLOAK_ADMIN_MODE port
    set_env KEYCLOAK_ADMIN_ALLOWED_IPS "$ips"
    set_env KEYCLOAK_ADMIN_PORT "$port"
    set_env KEYCLOAK_ADMIN_BIND 0.0.0.0
    set_env KEYCLOAK_ADMIN_CONTAINER_PORT "$container_port"
    set_env KEYCLOAK_ADMIN_URL "${admin_origin}/auth"
    apply
    set_master_frontend "${admin_origin}/auth"
    echo "Yonetim paneli: ${admin_origin}/auth/admin/  (ana adreste kapali)"
    echo "Simdi ${port}/tcp portunu firewall'da yalnizca yonetim IP'lerine acin."
    ;;
  status)
    echo "Mod:        $(get_env KEYCLOAK_ADMIN_MODE || true) (bos = open)"
    echo "Izinli IP:  $(get_env KEYCLOAK_ADMIN_ALLOWED_IPS || true)"
    p="$(get_env KEYCLOAK_ADMIN_PORT)"; b="$(get_env KEYCLOAK_ADMIN_BIND)"
    echo "Yonetim portu: ${b:-127.0.0.1}:${p:-8090}"
    echo "Konsol adresi: $(get_env KEYCLOAK_ADMIN_URL || true)"
    ;;
  *)
    die "bilinmeyen mod '$mode' (open | ip | port | status)"
    ;;
esac

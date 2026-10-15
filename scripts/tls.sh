#!/usr/bin/env bash
# Gateway'de HTTPS (TLS) acar/kapatir.
#
# Kullanim:
#   scripts/tls.sh enable --cert fullchain.pem --key privkey.pem --host hr.sirket.com [--port 443]
#       Verilen sertifikayla 443'te HTTPS acar, HTTP istekleri HTTPS'e yonlendirilir.
#       Uygulamanin adresi (PUBLIC_URL/PUBLIC_ORIGIN) https://<host> olur; Keycloak'in
#       genel adresi, uygulama istemcisinin yonlendirme adresleri ve servislerin
#       beklenen jeton ureticisi (issuer) buna gore guncellenir. Realm'de "SSL gerekli:
#       dis istekler" (sslRequired=external) acilir.
#
#   scripts/tls.sh enable --letsencrypt --host hr.sirket.com [--email it@sirket.com] [--staging]
#       Let's Encrypt'ten ucretsiz sertifika alir (HTTP-01, gateway uzerinden) ve
#       otomatik yenilemeyi acar (certbot servisi, 12 saatte bir kontrol; gateway
#       yeni sertifikayi en gec 6 saatte okur). Kosullar: alan adinin DNS kaydi bu
#       sunucuyu gostermeli, 80/tcp internetten erisilebilir olmali, GATEWAY_PORT=80.
#       --staging: Let's Encrypt test ortami (tarayici guvenmez; limitlere takilmadan deneme).
#
#   scripts/tls.sh enable --self-signed --host hr.sirket.local
#       Test icin kendinden imzali sertifika uretir (tarayici uyari verir).
#
#   scripts/tls.sh disable
#       HTTPS'i kapatir, adres http://<host>[:GATEWAY_PORT] olur.
#
#   scripts/tls.sh external --host hr.sirket.com [--port 443]
#       TLS'i gateway'in ONUNDEKI bir yuk dengeleyici / ters vekil sonlandiriyorsa:
#       gateway HTTP kalir, ama uygulama adresi ve Keycloak ayarlari https://<host>
#       olarak guncellenir. Dengeleyici X-Forwarded-Proto: https gondermelidir.
#
#   scripts/tls.sh auto --host hr.sirket.com [--email it@sirket.com]
#       Kurulumun kullandigi yol: Let's Encrypt'i dener (once DNS kaydini denetler;
#       sunucunun kendi guvenlik duvarina dokunmaz, izinler dis guvenlik duvarinda verilir). Olmazsa gecici olarak kendinden imzali
#       sertifikayla HTTPS acar ve Let's Encrypt'i saatte bir kendiliginden yeniden dener
#       (systemd zamanlayici ya da cron). DNS duzeldiginde gercek sertifikaya kendisi gecer.
#
#   scripts/tls.sh retry
#       Bekleyen Let's Encrypt denemesini simdi calistirir (zamanlayicinin cagirdigi komut).
#
#   scripts/tls.sh check [--host hr.sirket.com]
#       On kosullari denetler: DNS kaydi bu sunucuyu gosteriyor mu, 80/443 baska bir
#       surecte mi.
#
#   scripts/tls.sh verify
#       Gateway'e baglanip HTTPS'i, sertifikayi, HTTP->HTTPS yonlendirmesini ve giris
#       (Keycloak) adresini dogrular.
#
#   scripts/tls.sh status
#
# NOT: Gateway'in onunde TLS'i zaten sonlandiran bir yuk dengeleyici varsa "enable"
# KULLANMAYIN (HTTP->HTTPS yonlendirmesi donguye girer); "external" kullanin.
set -euo pipefail
cd "$(dirname "$0")/.."
# shellcheck source=lib/net.sh
. scripts/lib/net.sh
# Docker Compose kabuktaki degiskenleri .env'e tercih eder; kabukta eski degerler
# (orn. "set -a; . ./.env") kalmissa yeni ayar uygulanmaz. Yonetilen degiskenler temizlenir.
unset PUBLIC_URL PUBLIC_ORIGIN GATEWAY_TLS_BIND GATEWAY_TLS_PORT GATEWAY_PORT KEYCLOAK_ADMIN_MODE KEYCLOAK_ADMIN_ALLOWED_IPS KEYCLOAK_ADMIN_BIND KEYCLOAK_ADMIN_PORT KEYCLOAK_ADMIN_URL COMPOSE_PROFILES TLS_MODE

ENV_FILE=.env
DIR=deploy/nginx/tls
LE_DIR=deploy/letsencrypt
ACME_DIR=deploy/nginx/acme
# "enable" degisiklik yaparken hata olursa yarim kalan ayarlar geri alinir
# (onceden nginx'e TLS ayari yazilip sonraki adim basarisiz olunca site
# tamamen erisilemez ya da giris bozuk kalabiliyordu).
# GNU sed (Linux) ve BSD sed (macOS) icin ortak "yerinde degistir".
sed_i() { if sed --version >/dev/null 2>&1; then sed -i "$@"; else sed -i '' "$@"; fi; }
ROLLBACK=""
die() {
  echo "HATA: $*" >&2
  if [ -n "$ROLLBACK" ]; then
    echo "Degisiklikler geri aliniyor..." >&2
    rm -f "$DIR/listen.conf" "$DIR/redirect.conf" "$DIR/fullchain.pem" "$DIR/privkey.pem"
    [ -d "$ROLLBACK/tls" ] && cp -a "$ROLLBACK/tls/." "$DIR/" 2>/dev/null
    [ -f "$ROLLBACK/env" ] && cp "$ROLLBACK/env" "$ENV_FILE"
    ROLLBACK=""
    docker compose up -d >/dev/null 2>&1 || true
    docker compose exec -T gateway nginx -s reload >/dev/null 2>&1 || true
  fi
  exit 1
}
[ -f "$ENV_FILE" ] || die ".env bulunamadi; once install.sh calistirin."

# Docker grup yetkisi bu oturumda henuz aktif degilse (yeni kurulumdan hemen sonra)
# sudo ile devam edilir. install.sh kendi sarmalayicisini zaten disari aktarir.
if ! declare -F docker >/dev/null && ! docker info >/dev/null 2>&1 && sudo docker info >/dev/null 2>&1; then
  docker() { command sudo docker "$@"; }
fi

begin_change() {
  ROLLBACK="$(mktemp -d)"
  cp "$ENV_FILE" "$ROLLBACK/env"
  mkdir -p "$ROLLBACK/tls"
  cp -a "$DIR/." "$ROLLBACK/tls/" 2>/dev/null || true
}
end_change() { [ -n "$ROLLBACK" ] && rm -rf "$ROLLBACK"; ROLLBACK=""; }
mkdir -p "$DIR" "$LE_DIR" "$ACME_DIR"

set_env() {
  if grep -q "^$1=" "$ENV_FILE"; then sed_i "s#^$1=.*#$1=$2#" "$ENV_FILE"
  else printf '%s=%s\n' "$1" "$2" >> "$ENV_FILE"; fi
}
get_env() { grep "^$1=" "$ENV_FILE" | tail -1 | cut -d= -f2- || true; }
# COMPOSE_PROFILES virgullu bir listedir (orn. "letsencrypt,monitoring"); yalnizca
# kendi profilimizi ekleyip cikaririz ki scripts/monitoring.sh'in ayari silinmesin.
profile_set() { # profile_set NAME on|off
  local cur out=""
  cur="$(get_env COMPOSE_PROFILES)"
  for p in ${cur//,/ }; do [ "$p" = "$1" ] || out="${out:+$out,}$p"; done
  [ "$2" = on ] && out="${out:+$out,}$1"
  set_env COMPOSE_PROFILES "$out"
}

wait_keycloak() {
  echo "Keycloak hazir olana kadar bekleniyor..."
  for _ in $(seq 1 80); do
    docker compose exec -T keycloak sh -c 'exec 3<>/dev/tcp/127.0.0.1/8080' >/dev/null 2>&1 && return 0
    sleep 3
  done
  die "Keycloak ayaga kalkmadi"
}

# Keycloak realm ayarlari: istemci adresleri ve sslRequired.
configure_keycloak() {
  local origin="$1" ssl="$2"
  docker compose exec -T -e ORIGIN="$origin" -e SSL="$ssl" keycloak sh -c '
    K=/opt/keycloak/bin/kcadm.sh; C=/tmp/kcadm-tls.config
    for i in $(seq 1 30); do
      $K config credentials --config $C --server http://127.0.0.1:8080/auth --realm master \
        --user "$KEYCLOAK_ADMIN" --password "$KEYCLOAK_ADMIN_PASSWORD" >/dev/null 2>&1 && break
      sleep 3
    done
    CID=$($K get clients -r hr360 -q clientId=hr360-web --fields id --format csv --noquotes --config $C)
    $K update clients/$CID -r hr360 --config $C \
      -s "redirectUris=[\"$ORIGIN/*\"]" -s "webOrigins=[\"$ORIGIN\"]" \
      -s "attributes.\"post.logout.redirect.uris\"=$ORIGIN/*" || exit 1
    $K update realms/hr360 --config $C -s "sslRequired=$SSL" || exit 1
    # Yonetim realm i (yonetici girisi) de ayni kurala uyar; aksi halde HTTPS
    # yokken genel IP den yonetim paneli "HTTPS required" der.
    $K update realms/master --config $C -s "sslRequired=$SSL" || exit 1
    rm -f $C
  ' || die "Keycloak ayarlari guncellenemedi"
}

apply_all() {
  echo "Servisler yeni adresle guncelleniyor..."
  docker compose up -d >/dev/null 2>"${TMPDIR:-/tmp}/hr360-compose.err" \
    || die "docker compose up basarisiz: $(tail -3 "${TMPDIR:-/tmp}/hr360-compose.err")"
  docker compose exec -T gateway nginx -t >/dev/null 2>&1 || die "nginx yapilandirmasi gecersiz"
  docker compose exec -T gateway nginx -s reload >/dev/null 2>&1
  wait_keycloak
}

# Yonetim paneli ayri porttaysa (port modu) onun adresi de yeni kokene gore uretilir.
refresh_admin_access() {
  local mode; mode="$(get_env KEYCLOAK_ADMIN_MODE)"
  if [ "$mode" = "port" ]; then
    scripts/keycloak-admin-access.sh port "$(get_env KEYCLOAK_ADMIN_ALLOWED_IPS)"
  fi
}

# Duz HTTP istekleri HTTPS'e yonlendirilir; Let's Encrypt dogrulama yolu haric
# (yenileme sirasinda da HTTP'den sunulabilsin).
write_redirect() {
  # HTTPS 443 disinda bir porttaysa yonlendirme o porta yapilir.
  local target='https://$host$request_uri'
  [ "${1:-443}" = 443 ] || target="https://\$host:${1}\$request_uri"
  cat > "$DIR/redirect.conf" <<EOF
# scripts/tls.sh tarafindan uretildi: duz HTTP istekleri HTTPS'e yonlendirilir.
set \$hr360_to_https "";
if (\$scheme = http) { set \$hr360_to_https 1; }
if (\$uri ~ ^/\\.well-known/acme-challenge/) { set \$hr360_to_https ""; }
if (\$hr360_to_https = 1) {
    return 301 ${target};
}
EOF
}

# Let's Encrypt: gateway 80'de dogrulama yolunu sunarken certbot ile sertifika alir.
# Sertifika deploy/letsencrypt/live/<host>/ altina yazilir.
letsencrypt_issue() {
  local host="$1" email="$2" staging="$3"
  if [[ ! "$host" =~ [A-Za-z] ]] || [[ "$host" != *.* ]] || [ "$host" = localhost ]; then
    die "Let's Encrypt icin gercek bir alan adi gerekli (IP/localhost olmaz): $host"
  fi
  local gport; gport="$(get_env GATEWAY_PORT)"
  if [ -n "$gport" ] && [ "$gport" != 80 ]; then
    die "Let's Encrypt dogrulamasi 80. porttan yapilir; GATEWAY_PORT=$gport. .env'de GATEWAY_PORT=80 yapin."
  fi

  # Sunucunun kendi guvenlik duvarina dokunulmaz; yalnizca 80/443 kapaliysa uyarilir.
  warn_host_firewall 80 443
  # DNS bu sunucuyu gostermiyorsa certbot hic calistirilmaz: her basarisiz deneme Let's
  # Encrypt'in saatlik limitinden (alan adi basina 5) duser.
  if [ "${TLS_SKIP_DNS_CHECK:-0}" != 1 ]; then
    local rc=0; dns_points_here "$host" || rc=$?
    case "$rc" in
      0) echo "DNS: $DNS_CHECK_MSG" ;;
      1) die "DNS: $DNS_CHECK_MSG (DNS'i bolunmus ufukla yonetiyorsaniz TLS_SKIP_DNS_CHECK=1 ile atlayin)" ;;
      *) echo "UYARI: DNS denetlenemedi ($DNS_CHECK_MSG); yine de deneniyor." >&2 ;;
    esac
  fi

  echo "Gateway dogrulama yolu hazirlaniyor..."
  docker compose up -d gateway >/dev/null 2>&1
  docker compose exec -T gateway nginx -s reload >/dev/null 2>&1 || true
  local probe="hr360-probe-$$"
  mkdir -p "$ACME_DIR/.well-known/acme-challenge"
  echo ok > "$ACME_DIR/.well-known/acme-challenge/$probe"
  local got=""
  for _ in $(seq 1 10); do
    got="$(curl -s "http://127.0.0.1/.well-known/acme-challenge/$probe" || true)"
    [ "$got" = ok ] && break
    sleep 2
  done
  rm -f "$ACME_DIR/.well-known/acme-challenge/$probe"
  [ "$got" = ok ] || die "gateway dogrulama dosyasini sunamiyor (http://127.0.0.1/.well-known/acme-challenge/)"

  local args=(certonly --webroot -w /var/www/certbot -d "$host" --agree-tos --non-interactive
              --keep-until-expiring --cert-name "$host")
  if [ -n "$email" ]; then args+=(--email "$email"); else args+=(--register-unsafely-without-email); fi
  if [ "$staging" = 1 ]; then args+=(--test-cert); fi
  # Ozel/kurumsal ACME sunucusu veya test icin ek certbot argumanlari
  # (orn. CERTBOT_EXTRA_ARGS="--server https://acme.sirket.local/directory").
  if [ -n "${CERTBOT_EXTRA_ARGS:-}" ]; then read -ra extra <<< "$CERTBOT_EXTRA_ARGS"; args+=("${extra[@]}"); fi
  echo "Let's Encrypt'ten sertifika isteniyor ($host)..."
  if ! docker compose --profile letsencrypt run --rm --no-deps --entrypoint certbot certbot "${args[@]}"; then
    die "Let's Encrypt sertifikasi alinamadi. Kontrol edin: $host DNS kaydi bu sunucunun genel IP'sini gosteriyor mu, 80/tcp internetten (firewall/guvenlik grubu) acik mi?"
  fi
  le_cert "$host" >/dev/null || die "sertifika dosyasi bulunamadi: $LE_DIR/live/$host/"
}

# Let's Encrypt sertifikasini (fullchain) basar. certbot dizinleri root'a ait ve 700 izinli
# olusturur; betik docker grubundaki root olmayan bir kullaniciyla calisinca host'tan
# okunamaz. Ozel anahtarin iznini gevsetmemek icin certbot konteyneri uzerinden okunur.
le_cert() {
  docker compose --profile letsencrypt run --rm --no-deps -T --entrypoint cat certbot \
    "/etc/letsencrypt/live/$1/fullchain.pem" 2>/dev/null
}

# --- Let's Encrypt'i sonra yeniden deneme (auto) ---------------------------------
RETRY_UNIT=hr360-tls-retry
SYSTEMD_DIR="${HR360_SYSTEMD_DIR:-/etc/systemd/system}"
schedule_retry() {
  local root; root="$(pwd)"
  if [ -d /run/systemd/system ] && command -v systemctl >/dev/null 2>&1 && as_root true; then
    printf '%s\n' "[Unit]" "Description=HR360: Let's Encrypt sertifikasini yeniden dene" \
      "After=docker.service network-online.target" "Wants=network-online.target" "" \
      "[Service]" "Type=oneshot" "WorkingDirectory=$root" "ExecStart=$root/scripts/tls.sh retry" \
      | as_root tee "$SYSTEMD_DIR/$RETRY_UNIT.service" >/dev/null
    printf '%s\n' "[Unit]" "Description=HR360: Let's Encrypt sertifikasini saatte bir yeniden dene" "" \
      "[Timer]" "OnBootSec=10min" "OnUnitActiveSec=1h" "RandomizedDelaySec=5min" "" \
      "[Install]" "WantedBy=timers.target" \
      | as_root tee "$SYSTEMD_DIR/$RETRY_UNIT.timer" >/dev/null
    if as_root systemctl daemon-reload && as_root systemctl enable --now "$RETRY_UNIT.timer" >/dev/null 2>&1; then
      echo "Yeniden deneme: systemd zamanlayici ($RETRY_UNIT.timer, saatte bir; gunluk: journalctl -u $RETRY_UNIT)"
      return 0
    fi
  fi
  if command -v crontab >/dev/null 2>&1; then
    if { crontab -l 2>/dev/null | grep -v "# $RETRY_UNIT\$" || true
         echo "$((RANDOM % 60)) * * * * cd '$root' && scripts/tls.sh retry >> $LE_DIR/retry.log 2>&1 # $RETRY_UNIT"
       } | crontab -; then
      echo "Yeniden deneme: cron (saatte bir; gunluk: $LE_DIR/retry.log)"
      return 0
    fi
  fi
  echo "UYARI: zamanlayici kurulamadi (systemd/cron yok); DNS duzeldiginde elle calistirin: scripts/tls.sh retry" >&2
  return 1
}
unschedule_retry() {
  if [ -f "$SYSTEMD_DIR/$RETRY_UNIT.timer" ]; then
    as_root systemctl disable --now "$RETRY_UNIT.timer" >/dev/null 2>&1 || true
    as_root rm -f "$SYSTEMD_DIR/$RETRY_UNIT.timer" "$SYSTEMD_DIR/$RETRY_UNIT.service" || true
    as_root systemctl daemon-reload >/dev/null 2>&1 || true
  fi
  if command -v crontab >/dev/null 2>&1 && crontab -l 2>/dev/null | grep -q "# $RETRY_UNIT\$"; then
    crontab -l 2>/dev/null | grep -v "# $RETRY_UNIT\$" | crontab - || true
  fi
  return 0
}
# Elle yapilan her TLS degisikligi bekleyen otomatik denemeyi iptal eder.
clear_pending() {
  if [ -n "$(get_env TLS_LE_PENDING)" ]; then set_env TLS_LE_PENDING ""; set_env TLS_LE_EMAIL ""; fi
  unschedule_retry
}
host_of() { printf '%s' "$1" | sed -E 's#^https?://([^/:]+).*#\1#'; }

cmd="${1:-status}"; shift || true
case "$cmd" in
  enable)
    cert=""; key=""; host=""; port=443; self=0; le=0; email=""; staging=0
    while [ $# -gt 0 ]; do
      case "$1" in
        --cert) cert="$2"; shift 2 ;;
        --key) key="$2"; shift 2 ;;
        --host) host="$2"; shift 2 ;;
        --port) port="$2"; shift 2 ;;
        --self-signed) self=1; shift ;;
        --letsencrypt) le=1; shift ;;
        --email) email="$2"; shift 2 ;;
        --staging) staging=1; shift ;;
        *) die "bilinmeyen secenek: $1" ;;
      esac
    done
    [ -n "$host" ] || die "--host zorunlu (tarayicida kullanilacak alan adi)"
    [[ "$host" =~ ^[A-Za-z0-9.-]+$ ]] || die "gecersiz alan adi: $host"
    [[ "$port" =~ ^[0-9]+$ ]] || die "gecersiz port: $port"
    if [ -n "$email" ] && [[ ! "$email" =~ ^[^@[:space:]]+@[^@[:space:]]+[.][^@[:space:]]+$ ]]; then
      die "gecersiz e-posta: $email"
    fi
    begin_change
    if [ "$le" = 1 ]; then
      letsencrypt_issue "$host" "$email" "$staging"
      certfile="/etc/letsencrypt/live/$host/fullchain.pem"; keyfile="/etc/letsencrypt/live/$host/privkey.pem"
      # Kendi/kendinden imzali sertifikadan kalan dosyalar kullanilmaz.
      rm -f "$DIR/fullchain.pem" "$DIR/privkey.pem"
    elif [ "$self" = 1 ]; then
      san="DNS:$host"; [[ "$host" =~ ^[0-9.]+$ ]] && san="IP:$host"
      openssl req -x509 -newkey rsa:2048 -nodes -days 365 -subj "/CN=$host" \
        -addext "subjectAltName=$san" \
        -keyout "$DIR/privkey.pem" -out "$DIR/fullchain.pem" >/dev/null 2>&1 \
        || die "sertifika uretilemedi"
      echo "Kendinden imzali sertifika uretildi ($host, 365 gun)."
    else
      [ -f "$cert" ] && [ -f "$key" ] || die "--cert ve --key dosyalari bulunamadi"
      cp "$cert" "$DIR/fullchain.pem"; cp "$key" "$DIR/privkey.pem"
    fi
    if [ "$le" != 1 ]; then
      chmod 600 "$DIR/privkey.pem"
      openssl x509 -in "$DIR/fullchain.pem" -noout >/dev/null 2>&1 || die "sertifika okunamadi"
      # Anahtar sertifikaya ait degilse nginx hic acilmaz ve site tamamen kapanir.
      cert_pub="$(openssl x509 -in "$DIR/fullchain.pem" -noout -pubkey 2>/dev/null | openssl sha256)"
      key_pub="$(openssl pkey -in "$DIR/privkey.pem" -pubout 2>/dev/null | openssl sha256)"
      [ -n "$cert_pub" ] && [ "$cert_pub" = "$key_pub" ] || die "ozel anahtar bu sertifikaya ait degil"
      certfile=/etc/nginx/tls/fullchain.pem; keyfile=/etc/nginx/tls/privkey.pem
    fi

    cat > "$DIR/listen.conf" <<EOF
# scripts/tls.sh tarafindan uretildi.
listen 443 ssl;
ssl_certificate     ${certfile};
ssl_certificate_key ${keyfile};
ssl_protocols       TLSv1.2 TLSv1.3;
ssl_session_cache   shared:tls:10m;
EOF
    # HSTS yalnizca guvenilir sertifikada: kendinden imzalida tarayici uyariyi gecmeye
    # izin vermez ve site 6 ay boyunca acilamaz hale gelir. Alt alan adlari kapsanmaz.
    if [ "$self" != 1 ]; then
      echo 'add_header Strict-Transport-Security "max-age=15552000" always;' >> "$DIR/listen.conf"
    fi
    write_redirect "$port"
    if [ "$port" = "443" ]; then origin="https://$host"; else origin="https://$host:$port"; fi
    set_env GATEWAY_TLS_BIND 0.0.0.0
    set_env GATEWAY_TLS_PORT "$port"
    set_env PUBLIC_URL "https://$host"
    set_env PUBLIC_ORIGIN "$origin"
    if [ "$le" = 1 ]; then
      # Otomatik yenileme servisi (certbot) "docker compose up -d" ile birlikte kalkar.
      profile_set letsencrypt on
      set_env TLS_MODE letsencrypt
    else
      profile_set letsencrypt off
      set_env TLS_MODE "$([ "$self" = 1 ] && echo self-signed || echo certificate)"
      docker compose --profile letsencrypt rm -sf certbot >/dev/null 2>&1 || true
    fi
    apply_all
    configure_keycloak "$origin" external
    end_change
    refresh_admin_access
    [ "${TLS_KEEP_PENDING:-0}" = 1 ] || clear_pending
    echo "HTTPS acik: $origin"
    ;;
  external)
    host=""; port=443
    while [ $# -gt 0 ]; do
      case "$1" in
        --host) host="$2"; shift 2 ;;
        --port) port="$2"; shift 2 ;;
        *) die "bilinmeyen secenek: $1" ;;
      esac
    done
    [ -n "$host" ] || die "--host zorunlu (tarayicida kullanilacak alan adi)"
    [[ "$host" =~ ^[A-Za-z0-9.-]+$ ]] || die "gecersiz alan adi: $host"
    [[ "$port" =~ ^[0-9]+$ ]] || die "gecersiz port: $port"
    begin_change
    rm -f "$DIR/listen.conf" "$DIR/redirect.conf"
    if [ "$port" = "443" ]; then origin="https://$host"; else origin="https://$host:$port"; fi
    set_env GATEWAY_TLS_BIND 127.0.0.1
    set_env PUBLIC_URL "https://$host"
    set_env PUBLIC_ORIGIN "$origin"
    profile_set letsencrypt off
    set_env TLS_MODE external
    docker compose --profile letsencrypt rm -sf certbot >/dev/null 2>&1 || true
    apply_all
    configure_keycloak "$origin" external
    end_change
    refresh_admin_access
    clear_pending
    echo "Adres $origin olarak ayarlandi (TLS dengeleyicide sonlaniyor; gateway HTTP)."
    ;;
  disable)
    rm -f "$DIR/listen.conf" "$DIR/redirect.conf"
    profile_set letsencrypt off
    set_env TLS_MODE ""
    docker compose --profile letsencrypt rm -sf certbot >/dev/null 2>&1 || true
    host="$(get_env PUBLIC_URL | sed -E 's#^https?://([^/:]+).*#\1#')"; [ -n "$host" ] || host=localhost
    gport="$(get_env GATEWAY_PORT)"
    if [ -z "$gport" ] || [ "$gport" = 80 ]; then origin="http://$host"; else origin="http://$host:$gport"; fi
    set_env GATEWAY_TLS_BIND 127.0.0.1
    set_env PUBLIC_URL "http://$host"
    set_env PUBLIC_ORIGIN "$origin"
    apply_all
    # Duz HTTP'de disaridan (genel IP) giris icin sslRequired=none gerekir; yerel/ozel
    # ag adresleri "external" ile de calisir. Uretimde HTTPS kullanin.
    configure_keycloak "$origin" none
    refresh_admin_access
    clear_pending
    echo "HTTPS kapali: $origin"
    ;;
  auto)
    host=""; email=""
    while [ $# -gt 0 ]; do
      case "$1" in
        --host) host="$2"; shift 2 ;;
        --email) email="$2"; shift 2 ;;
        *) die "bilinmeyen secenek: $1" ;;
      esac
    done
    [ -n "$host" ] || die "--host zorunlu"
    le_args=(--letsencrypt --host "$host"); [ -n "$email" ] && le_args+=(--email "$email")
    if "$0" enable "${le_args[@]}"; then exit 0; fi
    echo ""
    echo "Let's Encrypt sertifikasi su an alinamadi. Gecici olarak kendinden imzali sertifikayla"
    echo "HTTPS aciliyor (tarayici uyari verir); Let's Encrypt saatte bir kendiliginden yeniden denenecek."
    TLS_KEEP_PENDING=1 "$0" enable --self-signed --host "$host" || die "kendinden imzali sertifikayla da HTTPS acilamadi"
    set_env TLS_LE_PENDING "$host"
    set_env TLS_LE_EMAIL "$email"
    schedule_retry || true
    # 3 = HTTPS acik ama gecici sertifikayla (cagiran betik bunu ayirt edebilsin).
    exit 3
    ;;
  retry)
    host="$(get_env TLS_LE_PENDING)"; email="$(get_env TLS_LE_EMAIL)"
    if [ -z "$host" ]; then unschedule_retry; echo "Bekleyen Let's Encrypt denemesi yok."; exit 0; fi
    echo "[$(date '+%F %T')] Let's Encrypt yeniden deneniyor: $host"
    rc=0; dns_points_here "$host" || rc=$?
    if [ "$rc" = 1 ]; then echo "Henuz degil: $DNS_CHECK_MSG"; exit 0; fi
    le_args=(--letsencrypt --host "$host"); [ -n "$email" ] && le_args+=(--email "$email")
    if "$0" enable "${le_args[@]}"; then
      echo "Let's Encrypt sertifikasi alindi; otomatik deneme kapatildi."
    else
      # Basarisiz "enable" onceki (kendinden imzali) ayari geri yukler; bekleyen kayit kalir.
      echo "Basarisiz; bir sonraki denemede tekrar denenecek."
    fi
    ;;
  check)
    host=""
    while [ $# -gt 0 ]; do
      case "$1" in --host) host="$2"; shift 2 ;; *) die "bilinmeyen secenek: $1" ;; esac
    done
    [ -n "$host" ] || host="$(host_of "$(get_env PUBLIC_URL)")"
    ok=1
    rc=0; dns_points_here "$host" || rc=$?
    case "$rc" in
      0) echo "DNS:      tamam - $DNS_CHECK_MSG" ;;
      1) echo "DNS:      SORUN - $DNS_CHECK_MSG"; ok=0 ;;
      *) echo "DNS:      denetlenemedi - $DNS_CHECK_MSG" ;;
    esac
    for p in 80 443; do
      if owner="$(port_owner "$p")"; then echo "Port $p:  SORUN - '$owner' bu portu kullaniyor"; ok=0
      else echo "Port $p:  tamam"; fi
    done
    [ "$ok" = 1 ]
    ;;
  verify)
    origin="$(get_env PUBLIC_ORIGIN)"; mode="$(get_env TLS_MODE)"; host="$(host_of "$origin")"
    gport="$(get_env GATEWAY_PORT)"; gport="${gport:-80}"
    ok=1
    if [ -f "$DIR/listen.conf" ]; then
      tport="$(get_env GATEWAY_TLS_PORT)"; tport="${tport:-443}"
      k=(); [ "$mode" = self-signed ] && k=(-k)
      code="$(curl -sS "${k[@]}" --max-time 15 --resolve "$host:$tport:127.0.0.1" -o /dev/null \
                -w '%{http_code}' "https://$host:$tport/" 2>&1 || true)"
      if [[ "$code" =~ ^[234][0-9][0-9]$ ]]; then
        if [ "$mode" = self-signed ]; then echo "HTTPS:    calisiyor (kendinden imzali sertifika; tarayici uyari verir)"
        else echo "HTTPS:    calisiyor, sertifika gecerli"; fi
      else
        echo "HTTPS:    SORUN - $code"; ok=0
      fi
      end="$(openssl s_client -connect "127.0.0.1:$tport" -servername "$host" </dev/null 2>/dev/null \
               | openssl x509 -noout -enddate 2>/dev/null | cut -d= -f2 || true)"
      [ -z "$end" ] || echo "          sertifika bitis: $end"
      redir="$(curl -s --max-time 10 -o /dev/null -w '%{http_code} %{redirect_url}' -H "Host: $host" "http://127.0.0.1:$gport/" || true)"
      case "$redir" in
        "301 https://"*) echo "HTTP:     HTTPS'e yonlendiriyor" ;;
        *) echo "HTTP:     SORUN - HTTPS'e yonlendirmiyor ($redir)"; ok=0 ;;
      esac
      base="https://$host:$tport"; cx=("${k[@]}" --resolve "$host:$tport:127.0.0.1")
    else
      base="http://127.0.0.1:$gport"; cx=(-H "Host: $host")
      if [ "$mode" = external ]; then echo "HTTPS:    dengeleyicide sonlaniyor (gateway HTTP)"; else echo "HTTPS:    kapali"; fi
    fi
    issuer="$(curl -s "${cx[@]}" --max-time 15 "$base/auth/realms/hr360/.well-known/openid-configuration" \
                | grep -o '"issuer":"[^"]*"' | cut -d'"' -f4 || true)"
    if [ "$issuer" = "$origin/auth/realms/hr360" ]; then echo "Giris:    tamam ($issuer)"
    else echo "Giris:    SORUN - beklenen $origin/auth/realms/hr360, gelen '${issuer:-yanit yok}'"; ok=0; fi
    if [ -n "$(get_env TLS_LE_PENDING)" ]; then
      echo "Not:      Let's Encrypt bekliyor ($(get_env TLS_LE_PENDING)); saatte bir yeniden deneniyor. Durum: scripts/tls.sh check"
    fi
    [ "$ok" = 1 ]
    ;;
  status)
    if [ "$(get_env TLS_MODE)" = external ]; then
      echo "HTTPS: dengeleyicide ($(get_env PUBLIC_ORIGIN)) - external"
    elif [ -f "$DIR/listen.conf" ]; then
      echo "HTTPS: acik ($(get_env PUBLIC_ORIGIN)) - $(get_env TLS_MODE)"
      crt="$DIR/fullchain.pem"
      if [ "$(get_env TLS_MODE)" = letsencrypt ]; then
        h="$(get_env PUBLIC_URL | sed -E 's#^https?://([^/:]+).*#\1#')"
        if docker compose ps --status running -q certbot 2>/dev/null | grep -q .; then
          echo "  Otomatik yenileme: calisiyor"
        else
          echo "  Otomatik yenileme: CALISMIYOR (docker compose up -d)"
        fi
      fi
      if [ "$(get_env TLS_MODE)" = letsencrypt ]; then le_cert "$h"; else cat "$crt" 2>/dev/null; fi \
        | openssl x509 -noout -subject -enddate 2>/dev/null | sed 's/^/  /'
    else
      echo "HTTPS: kapali ($(get_env PUBLIC_ORIGIN))"
    fi
    ;;
  *) die "kullanim: $0 enable|auto|retry|check|verify|external|disable|status" ;;
esac

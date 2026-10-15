#!/usr/bin/env bash
# E-posta (SMTP) sunucusunu kurulumdan sonra ayarlar / degistirir.
#
# Platformun e-postalari iki yerden gider ve ikisi de bu betikle birlikte guncellenir:
#   - Uygulama bildirimleri (hos geldiniz, izin/masraf/onay bildirimleri):
#     notification-service, .env'deki SMTP_* degiskenleri.
#   - Davet, parola belirleme ve parola sifirlama e-postalari: Keycloak'in
#     "hr360" realm'indeki e-posta ayari.
#
# Kullanim:
#   scripts/smtp.sh set
#       Sunucu bilgilerini sorar (host, port, kullanici, parola, gonderen) ve uygular.
#   scripts/smtp.sh set --host smtp.sirket.com --port 587 --user kullanici \
#                       --password 'parola' --from noreply@sirket.com [--name "HR360"]
#       Soru sormadan uygular. Port 465 = SSL, diger portlar = STARTTLS.
#       Kullanici verilmezse kimlik dogrulamasiz (ic ag relay) gonderilir.
#   scripts/smtp.sh set --from ik@sirket.com --password 'parola'
#       Sunucu, port ve kullanici adi gonderen adresten bulunur (Gmail/Google Workspace,
#       Microsoft 365, Outlook, Yandex, Zoho...; ozel alan adinda MX kaydindan).
#   scripts/smtp.sh mailpit
#       Paketteki test kutusuna (Mailpit) doner; e-postalar gercekten gonderilmez.
#   scripts/smtp.sh self [--from noreply@hr.sirket.com] [--yes]
#       (Gelismis) Sunucu kendisi gondersin: paketteki Postfix + DKIM (compose "mailer"
#       profili). 25/tcp cikisi ve PTR kaydini denetler, DNS kayitlarini yazar.
#   scripts/smtp.sh dns
#       Sunucunun kendi posta servisi icin SPF/DKIM/DMARC/PTR kayitlarini yazar.
#   scripts/smtp.sh status
#       Gecerli ayari (parola haric) gosterir.
#   scripts/smtp.sh test alici@sirket.com
#       .env'deki ayarla bu sunucudan bir deneme e-postasi gonderir (curl ile).
#
# Not: Her sirket, uygulama bildirimleri icin kendi SMTP
# sunucusunu uygulamadaki Ayarlar sayfasindan ayrica tanimlayabilir.
set -euo pipefail
cd "$(dirname "$0")/.."
# shellcheck source=lib/net.sh
. scripts/lib/net.sh
# shellcheck source=lib/mail.sh
. scripts/lib/mail.sh
unset COMPOSE_PROFILES MAIL_MODE MAIL_HOSTNAME MAIL_DOMAIN DKIM_SELECTOR SMTP_HOST SMTP_PORT SMTP_USER SMTP_PASSWORD SMTP_FROM_ADDRESS SMTP_FROM_NAME

ENV_FILE=.env
die() { echo "HATA: $*" >&2; exit 1; }
[ -f "$ENV_FILE" ] || die ".env bulunamadi; once install.sh calistirin."

# Docker grup yetkisi bu oturumda henuz aktif degilse sudo ile devam edilir.
if ! declare -F docker >/dev/null && ! docker info >/dev/null 2>&1 && sudo docker info >/dev/null 2>&1; then
  docker() { command sudo docker "$@"; }
fi

# .env'deki tek tirnakli degeri okur (install.sh SMTP_* degerlerini tirnakli yazar).
get_env() {
  local v; v="$(grep "^$1=" "$ENV_FILE" | tail -1 | cut -d= -f2- || true)"
  v="${v#\'}"; v="${v%\'}"; printf '%s' "$v"
}

# Degeri .env'e yazar. sed yerine awk + ortam degiskeni: parolada / & # \ gibi
# karakterler olsa da bozulmaz.
set_env() {
  local key="$1" val="$2" quote="${3:-1}" line tmp
  if [ "$quote" = 1 ]; then line="${key}='${val}'"; else line="${key}=${val}"; fi
  tmp="$(mktemp)"
  LINE="$line" KEY="$key" awk 'BEGIN{done=0}
    index($0, ENVIRON["KEY"] "=") == 1 { if (!done) { print ENVIRON["LINE"]; done=1 } ; next }
    { print }
    END { if (!done) print ENVIRON["LINE"] }' "$ENV_FILE" > "$tmp"
  cat "$tmp" > "$ENV_FILE"; rm -f "$tmp"
}

validate() {
  local v
  for v in "$@"; do
    case "$v" in *"'"*|*'$'*|*$'\n'*) die "SMTP bilgilerinde tek tirnak ('), \$ ve satir sonu kullanilamaz." ;; esac
  done
}

wait_keycloak() {
  for _ in $(seq 1 60); do
    docker compose exec -T keycloak sh -c 'exec 3<>/dev/tcp/127.0.0.1/8080' >/dev/null 2>&1 && return 0
    sleep 3
  done
  die "Keycloak'a ulasilamadi (docker compose ps keycloak)"
}

# Keycloak realm'inin e-posta ayari. Degerler ortam degiskeniyle aktarilir; kabuk
# kacislari ozel karakterleri bozmaz.
apply_keycloak() {
  wait_keycloak
  docker compose exec -T \
    -e S_HOST="$1" -e S_PORT="$2" -e S_FROM="$3" -e S_NAME="$4" -e S_AUTH="$5" \
    -e S_USER="$6" -e S_PASS="$7" -e S_SSL="$8" -e S_TLS="$9" keycloak sh -c '
    K=/opt/keycloak/bin/kcadm.sh; C=/tmp/kcadm-smtp.config
    for i in $(seq 1 30); do
      $K config credentials --config $C --server http://127.0.0.1:8080/auth --realm master \
        --user "$KEYCLOAK_ADMIN" --password "$KEYCLOAK_ADMIN_PASSWORD" >/dev/null 2>&1 && break
      sleep 3
    done
    $K update realms/hr360 --config $C \
      -s "smtpServer.host=$S_HOST" -s "smtpServer.port=$S_PORT" \
      -s "smtpServer.from=$S_FROM" -s "smtpServer.fromDisplayName=$S_NAME" \
      -s "smtpServer.auth=$S_AUTH" -s "smtpServer.user=$S_USER" -s "smtpServer.password=$S_PASS" \
      -s "smtpServer.ssl=$S_SSL" -s "smtpServer.starttls=$S_TLS"
    rc=$?; rm -f $C; exit $rc
  ' || die "Keycloak e-posta ayari guncellenemedi"
}

# COMPOSE_PROFILES virgullu listesinde yalnizca "mailer"i ekler/cikarir (digerleri korunur).
profile_set() { # profile_set NAME on|off
  local cur out=""
  cur="$(get_env COMPOSE_PROFILES)"
  for p in ${cur//,/ }; do [ "$p" = "$1" ] || out="${out:+$out,}$p"; done
  [ "$2" = on ] && out="${out:+$out,}$1"
  set_env COMPOSE_PROFILES "$out" 0
}

# Sunucunun kendi posta servisini kapatir (baska bir sunucuya / Mailpit'e geciste).
self_mailer_off() {
  [ "$(get_env MAIL_MODE)" = self ] || return 0
  set_env MAIL_MODE "" 0
  profile_set mailer off
  docker compose rm -sf mailer >/dev/null 2>&1 || true
  echo "Sunucunun kendi posta servisi kapatildi (DKIM anahtari mailer-dkim biriminde korunur)."
}

print_dns() {
  local domain host sel dkim
  domain="$(get_env MAIL_DOMAIN)"; host="$(get_env MAIL_HOSTNAME)"; sel="$(get_env DKIM_SELECTOR)"
  sel="${sel:-hr360}"
  dkim="$(mailer_dkim_txt "$sel")"
  mkdir -p "$(dirname "$MAILER_DNS_FILE")"
  mail_dns_records "$domain" "$host" "$(public_ipv4 || echo SUNUCU_IP)" "$sel" "$dkim" "$(get_env HR360_ADMIN_EMAIL)" \
    > "$MAILER_DNS_FILE"
  chmod 600 "$MAILER_DNS_FILE" 2>/dev/null || true
  cat "$MAILER_DNS_FILE"
  echo ""
  echo "(${MAILER_DNS_FILE} dosyasina da yazildi.)"
}

apply_all() {
  local host="$1" port="$2" user="$3" pass="$4" from="$5" name="$6" auth="$7" ssl="$8" tls="$9"
  set_env SMTP_HOST "$host"
  set_env SMTP_PORT "$port" 0
  set_env SMTP_USER "$user"
  set_env SMTP_PASSWORD "$pass"
  set_env SMTP_FROM_ADDRESS "$from"
  set_env SMTP_FROM_NAME "$name"
  echo "Bildirim servisi yeni ayarla yeniden baslatiliyor..."
  docker compose up -d notification-service >/dev/null 2>"${TMPDIR:-/tmp}/hr360-compose.err" \
    || die "notification-service baslatilamadi: $(tail -3 "${TMPDIR:-/tmp}/hr360-compose.err")"
  echo "Keycloak e-posta ayari guncelleniyor..."
  # Keycloak'ta kimlik dogrulamasiz sunucuda kullanici/parola bos birakilir.
  if [ "$auth" = true ]; then apply_keycloak "$host" "$port" "$from" "$name" true "$user" "$pass" "$ssl" "$tls"
  else apply_keycloak "$host" "$port" "$from" "$name" false "" "" "$ssl" "$tls"; fi
}

cmd="${1:-status}"; shift || true
case "$cmd" in
  set)
    host=""; port=""; user=""; pass=""; from=""; name=""; have_pass=0
    while [ $# -gt 0 ]; do
      case "$1" in
        --host) host="$2"; shift 2 ;;
        --port) port="$2"; shift 2 ;;
        --user) user="$2"; shift 2 ;;
        --password) pass="$2"; have_pass=1; shift 2 ;;
        --from) from="$2"; shift 2 ;;
        --name) name="$2"; shift 2 ;;
        *) die "bilinmeyen secenek: $1" ;;
      esac
    done
    if [ -z "$host" ] && [ -n "$from" ]; then
      # Gonderen adresten sunucu/port/kullanici: --from ik@sirket.com --password ...
      read -r host gport _ <<< "$(smtp_guess "$from")"
      port="${port:-$gport}"; user="${user:-$from}"
      echo "Sunucu gonderen adresten bulundu: $host:$port"
    elif [ -z "$host" ]; then
      read -r -p "Gonderen adres (orn. ik@sirket.com): " from || true
      is_email "$from" || die "gecerli bir gonderen adres girin"
      read -r ghost gport gkind <<< "$(smtp_guess "$from")"
      read -r -p "SMTP sunucusu [$ghost]: " host || true; host="${host:-$ghost}"
      [ "$host" = "$ghost" ] || gport=587
      read -r -p "Port (587 = STARTTLS, 465 = SSL) [$gport]: " port || true; port="${port:-$gport}"
      read -r -p "Kullanici adi [$from] ('-' = kimlik dogrulamasi yok): " user || true
      user="${user:-$from}"; [ "$user" = "-" ] && user=""
      if [ -n "$user" ]; then
        hint="$(smtp_hint "$gkind")"; [ -n "$hint" ] && echo "Not: $hint"
        read -r -s -p "Parola: " pass || true; echo ""; have_pass=1
      fi
      read -r -p "Gonderen adi [HR360]: " name || true
    fi
    port="${port:-587}"; name="${name:-HR360}"
    [ -n "$host" ] || die "sunucu (--host) zorunlu"
    [ "$host" != mailpit ] || die "Mailpit'e donmek icin: scripts/smtp.sh mailpit"
    [[ "$port" =~ ^[0-9]+$ ]] || die "gecersiz port: $port"
    [[ "$from" =~ ^[^@[:space:]]+@[^@[:space:]]+[.][^@[:space:]]+$ ]] || die "gecerli bir gonderen adres (--from) girin"
    [ -z "$user" ] || [ "$have_pass" = 1 ] || die "--user verildiyse --password de verilmeli"
    validate "$host" "$user" "$pass" "$from" "$name"
    auth=false; [ -n "$user" ] && auth=true
    if [ "$port" = 465 ]; then ssl=true; tls=false; else ssl=false; tls=true; fi
    self_mailer_off
    apply_all "$host" "$port" "$user" "$pass" "$from" "$name" "$auth" "$ssl" "$tls"
    echo "SMTP ayarlandi: $host:$port (gonderen: $name <$from>)."
    echo "Denemek icin: scripts/smtp.sh test sizin@adresiniz.com"
    ;;
  mailpit)
    self_mailer_off
    apply_all mailpit 1025 hr360 hr360 noreply@hr360.local HR360 false false false
    echo "Mailpit'e donuldu: e-postalar gonderilmez, sunucuda http://localhost:8025 adresinde gorunur."
    ;;
  self)
    from=""; name=""; yes=0
    while [ $# -gt 0 ]; do
      case "$1" in
        --from) from="$2"; shift 2 ;;
        --name) name="$2"; shift 2 ;;
        --yes|-y) yes=1; shift ;;
        *) die "bilinmeyen secenek: $1" ;;
      esac
    done
    origin="$(get_env PUBLIC_ORIGIN)"; [ -n "$origin" ] || origin="$(get_env PUBLIC_URL)"
    host="$(printf '%s' "$origin" | sed -E 's#^[a-zA-Z]+://([^/:]+).*#\1#')"
    if ! [[ "$host" =~ [A-Za-z] ]] || [[ "$host" != *.* ]] || [ "$host" = localhost ]; then
      die "sunucunun kendi posta servisi yalnizca alan adli kurulumda kullanilabilir (adres: ${origin:-yok})"
    fi
    from="${from:-noreply@${host}}"; name="${name:-$(get_env SMTP_FROM_NAME)}"; name="${name:-HR360}"
    is_email "$from" || die "gecerli bir gonderen adres (--from) girin"
    validate "$from" "$name"
    echo "On denetimler (${host}):"
    if ! mail_self_precheck "$host"; then
      echo "Bu denetimler gecmeden e-postalar alicilara ulasmayabilir ya da istenmeyen klasorune duser."
      if [ "$yes" != 1 ]; then
        read -r -p "Yine de devam edilsin mi? [e/H]: " go || true
        case "$go" in e|E|evet|y|Y) ;; *) echo "Iptal edildi; ayar degismedi."; exit 1 ;; esac
      fi
    fi
    set_env MAIL_MODE self 0
    set_env MAIL_HOSTNAME "$host" 0
    set_env MAIL_DOMAIN "${from##*@}" 0
    [ -n "$(get_env DKIM_SELECTOR)" ] || set_env DKIM_SELECTOR hr360 0
    profile_set mailer on
    echo "Posta servisi baslatiliyor..."
    docker compose up -d --build mailer >/dev/null 2>"${TMPDIR:-/tmp}/hr360-compose.err" \
      || die "posta servisi baslatilamadi: $(tail -3 "${TMPDIR:-/tmp}/hr360-compose.err")"
    apply_all "$MAILER_HOST" "$MAILER_PORT" "" "" "$from" "$name" false false false
    echo "Sunucunun kendi posta servisi ayarlandi (gonderen: $name <$from>)."
    echo ""
    print_dns
    ;;
  dns)
    [ "$(get_env MAIL_MODE)" = self ] || die "sunucunun kendi posta servisi kapali (acmak icin: scripts/smtp.sh self)"
    print_dns
    ;;
  status)
    host="$(get_env SMTP_HOST)"
    echo "Sunucu:   ${host}:$(get_env SMTP_PORT)"
    echo "Kullanici: $(get_env SMTP_USER)"
    echo "Gonderen: $(get_env SMTP_FROM_NAME) <$(get_env SMTP_FROM_ADDRESS)>"
    if [ "$host" = mailpit ]; then echo "Not: Mailpit test kutusu - e-postalar gercekten gonderilmez."; fi
    if [ "$host" = "$MAILER_HOST" ]; then echo "Not: sunucunun kendi posta servisi (Postfix + DKIM). DNS kayitlari: scripts/smtp.sh dns"; fi
    ;;
  test)
    to="${1:-}"
    [[ "$to" =~ ^[^@[:space:]]+@[^@[:space:]]+[.][^@[:space:]]+$ ]] || die "kullanim: $0 test alici@ornek.com"
    host="$(get_env SMTP_HOST)"; port="$(get_env SMTP_PORT)"
    [ "$host" != mailpit ] || die "Mailpit ayarli; once 'scripts/smtp.sh set' ile gercek sunucuyu girin."
    if [ "$host" = "$MAILER_HOST" ]; then
      # Ic agdaki posta servisine sunucudan ulasilamaz; e-posta konteynerin icinden birakilir.
      from="$(get_env SMTP_FROM_ADDRESS)"
      printf 'From: %s <%s>\r\nTo: <%s>\r\nSubject: HR360 SMTP denemesi\r\nDate: %s\r\nContent-Type: text/plain; charset=UTF-8\r\n\r\nBu e-posta HR360 posta servisini denemek icin gonderildi.\r\n' \
        "$(get_env SMTP_FROM_NAME)" "$from" "$to" "$(LC_ALL=C date -R 2>/dev/null || date)" \
        | docker compose exec -T mailer sendmail -f "$from" "$to" \
        || die "posta servisine birakilamadi (docker compose logs mailer)"
      echo "Deneme e-postasi kuyruga birakildi: $to (teslim durumu: docker compose logs mailer)"
      exit 0
    fi
    command -v curl >/dev/null 2>&1 || die "curl gerekli"
    user="$(get_env SMTP_USER)"; pass="$(get_env SMTP_PASSWORD)"; from="$(get_env SMTP_FROM_ADDRESS)"
    msg="$(mktemp)"
    printf 'From: %s <%s>\r\nTo: <%s>\r\nSubject: HR360 SMTP denemesi\r\nDate: %s\r\nContent-Type: text/plain; charset=UTF-8\r\n\r\nBu e-posta HR360 SMTP ayarini denemek icin gonderildi.\r\n' \
      "$(get_env SMTP_FROM_NAME)" "$from" "$to" "$(LC_ALL=C date -R 2>/dev/null || date)" > "$msg"
    if [ "$port" = 465 ]; then url="smtps://${host}:${port}"; tlsarg=(); else url="smtp://${host}:${port}"; tlsarg=(--ssl-reqd); fi
    authargs=(); [ -n "$user" ] && authargs=(--user "${user}:${pass}")
    if curl -sS --max-time 30 --url "$url" "${tlsarg[@]}" "${authargs[@]}" \
         --mail-from "$from" --mail-rcpt "$to" --upload-file "$msg"; then
      rm -f "$msg"; echo "Deneme e-postasi gonderildi: $to"
    else
      rm -f "$msg"; die "gonderilemedi. Sunucu/port, kullanici/parola ve bu sunucudan ${port}/tcp cikisina izin verildigini kontrol edin."
    fi
    ;;
  *) die "kullanim: $0 set|mailpit|self|dns|status|test" ;;
esac

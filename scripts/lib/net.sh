# shellcheck shell=bash
# install.sh ve scripts/tls.sh'in ortak ag yardimcilari (kaynak olarak yuklenir:
# ". scripts/lib/net.sh"). Hicbiri dis bir araca (dig, jq) bagli degildir: DNS
# sorgulari DNS-over-HTTPS ile curl uzerinden, olmazsa sistem cozucusuyla yapilir.

# Komutu root olarak calistirir: zaten root ise dogrudan, degilse parola sormayan sudo ile.
as_root() {
  if [ "$(id -u)" = 0 ]; then "$@"
  elif command -v sudo >/dev/null 2>&1 && sudo -n true 2>/dev/null; then sudo "$@"
  else return 1; fi
}

# Sunucunun internetten gorunen IPv4 adresi (NAT/bulut arkasinda da dogru adres).
# HR360_PUBLIC_IP ile elle verilebilir (ornegin cikis trafigi farkli bir IP'den gidiyorsa).
public_ipv4() {
  if [ -n "${HR360_PUBLIC_IP:-}" ]; then echo "$HR360_PUBLIC_IP"; return 0; fi
  local u ip
  for u in https://api.ipify.org https://ipv4.icanhazip.com https://ifconfig.me/ip; do
    ip="$(curl -4 -fsS --max-time 5 "$u" 2>/dev/null | tr -d '[:space:]')" || continue
    [[ "$ip" =~ ^[0-9]{1,3}(\.[0-9]{1,3}){3}$ ]] && { echo "$ip"; return 0; }
  done
  return 1
}

# DNS-over-HTTPS sorgusu: "data" alanlarini satir satir basar. Let's Encrypt'in
# gordugu genel DNS'i yansitir (yerel /etc/hosts ya da onbellek yaniltmaz).
doh_query() { # doh_query NAME TYPE
  local u out
  for u in "https://cloudflare-dns.com/dns-query?name=$1&type=$2" "https://dns.google/resolve?name=$1&type=$2"; do
    out="$(curl -fsS --max-time 5 -H 'accept: application/dns-json' "$u" 2>/dev/null)" || continue
    printf '%s' "$out" | grep -o '"data": *"[^"]*"' | sed -E 's/"data": *"([^"]*)"/\1/'
    return 0
  done
  return 1
}

# Alan adinin A kayitlari (IPv4), satir satir.
resolve_a() {
  local out
  if out="$(doh_query "$1" A)"; then
    printf '%s\n' "$out" | grep -E '^[0-9]{1,3}(\.[0-9]{1,3}){3}$' || true
    return 0
  fi
  getent ahostsv4 "$1" 2>/dev/null | awk '{print $1}' | sort -u
}

# Alan adinin MX sunuculari (oncelik sirasiyla, sonda nokta olmadan).
resolve_mx() {
  local out
  if out="$(doh_query "$1" MX)"; then
    printf '%s\n' "$out" | awk 'NF==2 {print $1, $2}' | sort -n | awk '{sub(/\.$/, "", $2); print tolower($2)}'
    return 0
  fi
  if command -v dig >/dev/null 2>&1; then
    dig +short MX "$1" 2>/dev/null | sort -n | awk '{sub(/\.$/, "", $2); print tolower($2)}'
  elif command -v host >/dev/null 2>&1; then
    host -t MX "$1" 2>/dev/null | awk '/mail is handled by/ {print $(NF-1), $NF}' | sort -n | awk '{sub(/\.$/, "", $2); print tolower($2)}'
  fi
}

# Alan adi bu sunucuyu mu gosteriyor?
#   0 = evet, 1 = hayir (ya da kayit yok), 2 = belirlenemedi (genel IP / DNS'e ulasilamadi)
# DNS_CHECK_MSG degiskenine okunabilir bir aciklama yazar.
DNS_CHECK_MSG=""
dns_points_here() {
  local host="$1" ip records
  if ! ip="$(public_ipv4)"; then
    DNS_CHECK_MSG="sunucunun genel IP adresi belirlenemedi (internet cikisi yok mu?)"; return 2
  fi
  records="$(resolve_a "$host" | tr '\n' ' ' | sed 's/ $//')"
  if [ -z "$records" ]; then
    DNS_CHECK_MSG="$host icin A kaydi yok. DNS'e ekleyin: $host  A  $ip"; return 1
  fi
  if printf '%s\n' $records | grep -qx "$ip"; then
    DNS_CHECK_MSG="$host -> $ip (bu sunucu)"; return 0
  fi
  DNS_CHECK_MSG="$host su adresi gosteriyor: $records; bu sunucu: $ip. DNS kaydini $ip olarak duzeltin."
  return 1
}

# Sunucunun kendi guvenlik duvari (ufw / firewalld) yalnizca DENETLENIR; betikler ona hicbir
# zaman kural eklemez, onu devreye almaz ya da kapatmaz. Erisim izinleri disaridaki guvenlik
# duvarinda (bulut guvenlik grubu, vCloud Edge Gateway vb.) verilir. Yerel guvenlik duvari
# calisiyor ve verilen portlara izin yoksa uyari basar.
warn_host_firewall() { # warn_host_firewall 80 443 ...
  local p blocked="" out
  if command -v ufw >/dev/null 2>&1 && out="$(as_root ufw status 2>/dev/null)" && printf '%s' "$out" | grep -q "Status: active"; then
    for p in "$@"; do
      printf '%s\n' "$out" | grep -Eq "^${p}(/tcp)?([[:space:]]|$).*ALLOW" || blocked="$blocked $p"
    done
    [ -z "$blocked" ] || echo "UYARI: sunucuda ufw calisiyor ve su portlara izin yok:$blocked/tcp. Betik ufw'ye dokunmaz; gerekiyorsa kurali siz ekleyin." >&2
  elif command -v firewall-cmd >/dev/null 2>&1 && as_root firewall-cmd --state >/dev/null 2>&1; then
    for p in "$@"; do
      as_root firewall-cmd -q --query-port="$p/tcp" 2>/dev/null && continue
      case "$p" in 80) as_root firewall-cmd -q --query-service=http 2>/dev/null && continue ;;
                   443) as_root firewall-cmd -q --query-service=https 2>/dev/null && continue ;; esac
      blocked="$blocked $p"
    done
    [ -z "$blocked" ] || echo "UYARI: sunucuda firewalld calisiyor ve su portlara izin yok:$blocked/tcp. Betik firewalld'ye dokunmaz; gerekiyorsa kurali siz ekleyin." >&2
  fi
  return 0
}

# Portu Docker disinda bir surec dinliyorsa surecin adini basar (orn. apache2, nginx).
port_owner() {
  command -v ss >/dev/null 2>&1 || return 1
  local line
  line="$(as_root ss -ltnpH "sport = :$1" 2>/dev/null || ss -ltnH "sport = :$1" 2>/dev/null)" || return 1
  [ -n "$line" ] || return 1
  if printf '%s' "$line" | grep -q 'docker-proxy\|com.docker'; then return 1; fi
  printf '%s' "$line" | grep -o 'users:(("[^"]*"' | head -1 | sed 's/users:(("//' | grep . || echo "bilinmeyen surec"
}

# Gonderen adresinden SMTP sunucusunu tahmin eder: "host port saglayici" basar.
# Once bilinen saglayicilar, sonra alan adinin MX kaydi (Google Workspace, Microsoft 365,
# Yandex, Zoho... ozel alan adlarinda da taninsin), en son smtp.<alan adi>.
smtp_guess() {
  local domain mx
  domain="$(printf '%s' "${1##*@}" | tr '[:upper:]' '[:lower:]')"
  case "$domain" in
    gmail.com|googlemail.com) echo "smtp.gmail.com 587 google"; return ;;
    outlook.com|hotmail.com|live.com|msn.com|outlook.com.tr|hotmail.com.tr) echo "smtp-mail.outlook.com 587 outlook"; return ;;
    yandex.com|yandex.com.tr|yandex.ru|ya.ru) echo "smtp.yandex.com 465 yandex"; return ;;
    yahoo.com|yahoo.com.tr|ymail.com) echo "smtp.mail.yahoo.com 465 yahoo"; return ;;
    icloud.com|me.com|mac.com) echo "smtp.mail.me.com 587 icloud"; return ;;
    zoho.com|zohomail.com) echo "smtp.zoho.com 465 zoho"; return ;;
    gmx.com|gmx.net|gmx.de) echo "mail.gmx.com 587 gmx"; return ;;
  esac
  mx="$(resolve_mx "$domain" | head -1)"
  case "$mx" in
    *.google.com|*.googlemail.com) echo "smtp.gmail.com 587 google" ;;
    *.outlook.com|*.office365.com) echo "smtp.office365.com 587 microsoft365" ;;
    *.yandex.net|*.yandex.ru) echo "smtp.yandex.com 465 yandex" ;;
    *.zoho.com|*.zoho.eu) echo "smtp.zoho.com 465 zoho" ;;
    *.icloud.com) echo "smtp.mail.me.com 587 icloud" ;;
    *.mailgun.org) echo "smtp.mailgun.org 587 mailgun" ;;
    "") echo "smtp.$domain 587 bilinmiyor" ;;
    *) echo "smtp.$domain 587 bilinmiyor" ;;
  esac
}

# Saglayiciya ozel kisa not (uygulama sifresi vb.).
smtp_hint() {
  case "$1" in
    google) echo "Gmail / Google Workspace: hesap parolasi DEGIL, 2 adimli dogrulama acikken uretilen 'Uygulama sifresi' girin (myaccount.google.com/apppasswords)." ;;
    microsoft365|outlook) echo "Microsoft 365 / Outlook: posta kutusunda 'Kimligi dogrulanmis SMTP' (SMTP AUTH) acik olmali; MFA aciksa uygulama parolasi gerekir." ;;
    yandex) echo "Yandex: Yandex ID > Guvenlik > Uygulama sifreleri'nden 'Posta' icin sifre uretin." ;;
    yahoo|icloud|zoho) echo "Bu saglayici SMTP icin uygulamaya ozel parola ister; hesap ayarlarindan uretin." ;;
  esac
}

# SMTP ile deneme e-postasi gonderir (curl). Basarisizsa hata metnini SMTP_TEST_ERR'e yazar.
SMTP_TEST_ERR=""
smtp_send_test() { # host port user pass from name to subject body
  local host="$1" port="$2" user="$3" pass="$4" from="$5" name="$6" to="$7" subject="$8" body="$9"
  local msg url tlsarg=() authargs=() err
  msg="$(mktemp)"; err="$(mktemp)"
  printf 'From: %s <%s>\r\nTo: <%s>\r\nSubject: %s\r\nDate: %s\r\nMIME-Version: 1.0\r\nContent-Type: text/plain; charset=UTF-8\r\n\r\n%s\r\n' \
    "$name" "$from" "$to" "$subject" "$(LC_ALL=C date -R 2>/dev/null || date)" "$body" > "$msg"
  if [ "$port" = 465 ]; then url="smtps://${host}:${port}"
  else url="smtp://${host}:${port}"; [ "$port" = 587 ] && tlsarg=(--ssl-reqd); fi
  [ -n "$user" ] && authargs=(--user "${user}:${pass}")
  if curl -sS --max-time 30 --url "$url" "${tlsarg[@]}" "${authargs[@]}" \
       --mail-from "$from" --mail-rcpt "$to" --upload-file "$msg" 2>"$err"; then
    rm -f "$msg" "$err"; SMTP_TEST_ERR=""; return 0
  fi
  SMTP_TEST_ERR="$(tr '\n' ' ' < "$err" | sed 's/^curl: ([0-9]*) //')"
  case "$SMTP_TEST_ERR" in
    *"Login denied"*|*535*|*"authentication"*) SMTP_TEST_ERR="$SMTP_TEST_ERR -> kullanici adi / parola (uygulama sifresi) hatali" ;;
    *"timed out"*|*"Connection refused"*|*"Couldn't connect"*) SMTP_TEST_ERR="$SMTP_TEST_ERR -> sunucuya ulasilamadi; bu sunucudan ${port}/tcp cikisi kapali olabilir" ;;
    *"resolve host"*) SMTP_TEST_ERR="$SMTP_TEST_ERR -> sunucu adi cozulemedi" ;;
  esac
  rm -f "$msg" "$err"; return 1
}

is_email() { [[ "$1" =~ ^[^@[:space:]]+@[^@[:space:]]+[.][^@[:space:]]+$ ]]; }

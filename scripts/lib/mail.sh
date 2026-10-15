# shellcheck shell=bash
# "Sunucu kendisi gondersin" posta kipi (install.sh --mail-mode=self, scripts/smtp.sh self)
# icin ortak yardimcilar. scripts/lib/net.sh'den sonra kaynak olarak yuklenir.
#
# Bu kipte paketteki Postfix (compose "mailer" profili) e-postalari alicilarin MX
# sunucularina dogrudan teslim eder ve OpenDKIM ile imzalar. Teslim edilebilirlik icin
# sunucudan 25/tcp cikisi, IP adresinin PTR kaydi ve alan adinda SPF/DKIM/DMARC gerekir.

MAILER_HOST=mailer
MAILER_PORT=25
MAILER_DNS_FILE=deploy/mailer/dns-kayitlari.txt

# Bir TCP baglantisi kurulabiliyor mu (kisa zaman asimi; dis araca gerek yok).
tcp_probe() { # tcp_probe HOST PORT
  timeout "${HR360_TCP_TIMEOUT:-6}" bash -c 'exec 3<>"/dev/tcp/$1/$2"' _ "$1" "$2" >/dev/null 2>&1
}

# 25/tcp disari cikis: bilinen buyuk posta saglayicilarinin MX'lerinden birine baglanti.
# Cogu bulut saglayicisi (AWS, GCP, Azure, Hetzner, DigitalOcean...) yeni hesaplarda 25'i engeller.
PORT25_MSG=""
port25_outbound() {
  local h tried=""
  for h in ${HR360_PORT25_HOSTS:-gmail-smtp-in.l.google.com hotmail-com.olc.protection.outlook.com mx.yandex.net}; do
    if tcp_probe "$h" 25; then PORT25_MSG="25/tcp cikisi acik (${h})"; return 0; fi
    tried="${tried:+$tried, }$h"
  done
  PORT25_MSG="25/tcp cikisi kapali gorunuyor (denenen: ${tried}). Bulut/barindirma saglayiciniz 25'i engelliyor olabilir; destek talebiyle actirin ya da gercek bir SMTP sunucusu kullanin."
  return 1
}

# IPv4 adresinin PTR (ters DNS) adlari, kucuk harf ve sonda nokta olmadan.
resolve_ptr() {
  local ip="$1" rev out
  rev="$(printf '%s' "$ip" | awk -F. '{print $4"."$3"."$2"."$1}').in-addr.arpa"
  if out="$(doh_query "$rev" PTR)"; then
    printf '%s\n' "$out" | sed 's/\.$//' | tr '[:upper:]' '[:lower:]' | grep . || true
    return 0
  fi
  getent hosts "$ip" 2>/dev/null | awk '{print tolower($2)}'
}

# Sunucunun genel IP'sinin PTR kaydi alan adini gosteriyor mu?
#   0 = evet, 1 = hayir (ya da kayit yok), 2 = belirlenemedi. PTR_CHECK_MSG'e aciklama yazar.
PTR_CHECK_MSG=""
ptr_matches() {
  local host ip names
  host="$(printf '%s' "$1" | tr '[:upper:]' '[:lower:]')"
  if ! ip="$(public_ipv4)"; then
    PTR_CHECK_MSG="sunucunun genel IP adresi belirlenemedi; PTR denetlenemedi"; return 2
  fi
  names="$(resolve_ptr "$ip" | tr '\n' ' ' | sed 's/ $//')"
  if [ -z "$names" ]; then
    PTR_CHECK_MSG="${ip} icin PTR kaydi yok. Saglayicinizin panelinden ${ip} -> ${host} ters DNS kaydi tanimlayin."; return 1
  fi
  if printf '%s\n' $names | grep -qx "$host"; then
    PTR_CHECK_MSG="PTR: ${ip} -> ${host}"; return 0
  fi
  PTR_CHECK_MSG="${ip} PTR kaydi '${names}' gosteriyor; '${host}' olmali (saglayicinizin panelinden ters DNS'i duzeltin)."
  return 1
}

# Ön denetimler: 25/tcp cikisi ve PTR. Sonuclari yazar; hepsi tamamsa 0.
MAIL_PRECHECK_ISSUES=0
mail_self_precheck() { # mail_self_precheck MAIL_HOSTNAME
  MAIL_PRECHECK_ISSUES=0
  if port25_outbound; then echo "   [tamam] ${PORT25_MSG}"
  else echo "   [SORUN] ${PORT25_MSG}"; MAIL_PRECHECK_ISSUES=$((MAIL_PRECHECK_ISSUES + 1)); fi
  if ptr_matches "$1"; then echo "   [tamam] ${PTR_CHECK_MSG}"
  else echo "   [SORUN] ${PTR_CHECK_MSG}"; MAIL_PRECHECK_ISSUES=$((MAIL_PRECHECK_ISSUES + 1)); fi
  [ "$MAIL_PRECHECK_ISSUES" = 0 ]
}

# Posta servisinin urettigi DKIM genel anahtari (TXT degeri, tek satir). Servis calismiyorsa bos.
mailer_dkim_txt() { # mailer_dkim_txt SELECTOR
  local sel="${1:-hr360}" out=""
  for _ in $(seq 1 "${HR360_DKIM_WAIT:-20}"); do
    out="$(docker compose exec -T mailer cat "/var/lib/dkim/${sel}.txt" 2>/dev/null)" && [ -n "$out" ] && break
    out=""; sleep 2
  done
  # opendkim-genkey BIND bicimi: ( "v=DKIM1; h=sha256; k=rsa; " "p=MIIB..." "..." ) -> tek dize
  printf '%s' "$out" | tr -d '\n' | { grep -o '"[^"]*"' || true; } | tr -d '"' | tr -d '\n' | sed 's/[[:space:]]*$//'
}

# SPF / DKIM / DMARC / PTR kayitlari (kullanicinin DNS'ine eklenecek).
mail_dns_records() { # mail_dns_records MAIL_DOMAIN MAIL_HOSTNAME IP SELECTOR DKIM_TXT [RAPOR_ADRESI]
  local domain="$1" host="$2" ip="${3:-SUNUCU_IP}" sel="$4" dkim="${5:-}" rua="${6:-}"
  [ -n "$rua" ] || rua="postmaster@${domain}"
  echo "HR360 - sunucunun kendi posta servisi icin DNS kayitlari"
  echo "Alan adi: ${domain}   Sunucu: ${host}   IP: ${ip}"
  echo ""
  echo "1) SPF (TXT, ad: ${domain})"
  echo "   v=spf1 ip4:${ip} ~all"
  echo "   Alan adinda zaten bir SPF kaydi varsa yeni kayit eklemeyin; mevcut kayda 'ip4:${ip}' ekleyin"
  echo "   (bir alan adinda yalnizca bir SPF kaydi olabilir)."
  echo ""
  echo "2) DKIM (TXT, ad: ${sel}._domainkey.${domain})"
  if [ -n "$dkim" ]; then echo "   ${dkim}"
  else echo "   (anahtar henuz okunamadi; posta servisi calisinca: scripts/smtp.sh dns)"; fi
  echo ""
  echo "3) DMARC (TXT, ad: _dmarc.${domain})"
  echo "   v=DMARC1; p=none; rua=mailto:${rua}; adkim=r; aspf=r"
  echo "   Birkac hafta raporlari izleyip sorun yoksa p=quarantine'e gecin."
  echo ""
  echo "4) PTR / ters DNS (DNS'te degil, sunucu saglayicinizin panelinde)"
  echo "   ${ip} -> ${host}"
  echo ""
  echo "5) A kaydi: ${host} -> ${ip} (HR360 adresi icin zaten gerekli)"
}

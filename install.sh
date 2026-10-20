#!/usr/bin/env bash
# HR360 Enterprise — tek sunucu kurulum scripti
#
# Ne yapar:
#   1. Docker / Docker Compose var mi kontrol eder; yoksa (apt/dnf/yum
#      tabanli sistemlerde) sizden onay alarak otomatik kurar
#   2. Adresi, Keycloak yonetim paneli erisimini, HTTPS yontemini ve SMTP
#      sunucusunu sorar
#   3. Sirlari (parola, anahtar) sorar — bos birakirsaniz guvenli, rastgele
#      bir deger uretir
#   4. .env dosyasini yazar, Keycloak realm sablonunu doldurur
#   5. Tum servisleri build edip ayaga kaldirir
#   6. Yonetim paneli erisimini ve HTTPS'i (scripts/tls.sh) ayarlar
#   7. Erisim adreslerini ve ilk giris bilgilerini ekrana basar
#
# Demo verisi istege baglidir (--demo): ornek "demo" sirketi ve demo.admin kullanicisi. Uretim
# kurulumunda yalnizca platform.admin olusur; ilk sirketi o olusturur (ya da herkese acik kayit).
#
# Mevcut bir kurulumda (.env varsa) tekrar calistirildiginda varsayilan yol
# GUNCELLEMEDIR: sirlar korunur, veritabani gocleri uygulanir, imajlar yeniden
# build edilir.
#
# Alan adiyla kurulumda zorunlu iki adim kendiliginden yapilir:
#   - HTTPS: DNS kaydinin bu sunucuyu gosterdigi
#     denetlenir, Let's Encrypt sertifikasi alinir. Alinamazsa (DNS henuz yayilmadi vb.)
#     gecici bir sertifikayla HTTPS acilir ve Let's Encrypt saatte bir kendiliginden
#     yeniden denenir; DNS duzeldiginde gercek sertifikaya gecilir.
#   - E-posta: gonderen adresten SMTP sunucusu ve portu bulunur (Gmail/Google Workspace,
#     Microsoft 365, Outlook, Yandex, Zoho... ozel alan adlarinda MX kaydindan), bir deneme
#     e-postasi gonderilerek dogrulanir. Yonetici e-postasi sistem uyarilarinin alicisi olur.
#
# Kullanim:
#   ./install.sh                      Sorularla kurulum
#   ./install.sh --domain hr.sirket.com --email it@sirket.com \
#                --smtp-from ik@sirket.com --smtp-password '...' --yes
#                                     Soru sormadan kurulum (parola HR360_SMTP_PASSWORD
#                                     ortam degiskeniyle de verilebilir)
#   ./install.sh --help               Tum secenekler
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"
# shellcheck source=scripts/lib/net.sh
. scripts/lib/net.sh
# shellcheck source=scripts/lib/mail.sh
. scripts/lib/mail.sh
# shellcheck source=scripts/lib/secrets.sh
. scripts/lib/secrets.sh
# Urun surumu (VERSION); web arayuzu derleme argumani olarak alir (docker-compose.yml).
HR360_VERSION="$(tr -d '[:space:]' < VERSION 2>/dev/null || true)"; export HR360_VERSION

BOLD=$(tput bold 2>/dev/null || echo "")
RESET=$(tput sgr0 2>/dev/null || echo "")
GREEN=$(tput setaf 2 2>/dev/null || echo "")
YELLOW=$(tput setaf 3 2>/dev/null || echo "")

info()  { echo "${BOLD}==>${RESET} $*"; }
# GNU sed (Linux) ve BSD sed (macOS) icin ortak "yerinde degistir".
sed_i() { if sed --version >/dev/null 2>&1; then sed -i "$@"; else sed -i '' "$@"; fi; }
warn()  { echo "${YELLOW}${BOLD}!!${RESET} $*"; }

# --- Komut satiri secenekleri ----------------------------------------------
usage() {
  cat <<'EOF'
Kullanim: ./install.sh [secenekler]

  --domain ALAN_ADI       Uygulamanin alan adi (orn. hr.sirket.com). Adres https://ALAN_ADI,
                          HTTP portu 80 ve HTTPS Let's Encrypt ile otomatik olur.
  --url ADRES             Alan adi yerine adres (orn. http://10.0.0.5); HTTPS sorulmaz/kapali.
  --http-port PORT        Gateway HTTP portu (varsayilan 80).
  --email ADRES           Yonetici e-postasi: sertifika bildirimleri, sistem uyarilari
                          (Alertmanager) ve SMTP deneme e-postasi bu adrese gider.
  --tls KIP               letsencrypt | self-signed | none | cert (cert icin --cert ve --key).
  --cert DOSYA --key DOSYA
                          Kendi sertifikaniz (fullchain + ozel anahtar).
  --smtp-from ADRES       Gonderen e-posta adresi. Sunucu ve port bu adresten bulunur.
  --smtp-host SUNUCU      Bulunan sunucu yerine bu kullanilir ("mailpit" = test kutusu).
  --smtp-port PORT        587 (STARTTLS) ya da 465 (SSL).
  --smtp-user KULLANICI   Varsayilan: gonderen adres. "-" = kimlik dogrulamasi yok.
  --smtp-password PAROLA  (ya da HR360_SMTP_PASSWORD ortam degiskeni)
  --smtp-name AD          Gonderen adi (varsayilan HR360).
  --no-smtp-test          Deneme e-postasi gonderme.
  --mail-mode self        (Gelismis, yalnizca alan adli kurulum) Sunucu kendisi gondersin:
                          paketteki Postfix e-postalari alicilara dogrudan teslim eder (DKIM
                          imzali). 25/tcp cikisi, PTR kaydi ve SPF/DKIM/DMARC DNS kayitlari
                          gerekir; kurulum bunlari denetler ve kayitlari yazar. Gonderen
                          adres --smtp-from ile (varsayilan noreply@ALAN-ADI).
  --keycloak-admin KIP    open | ip:CIDR,CIDR | port  (yonetim paneli erisimi). Varsayilan "open":
                          panel ana adreste (/auth/admin/). "port": panel yalnizca :8090'da,
                          8090/tcp dis guvenlik duvarinda yonetim IP'lerine kisitlanir.
  --demo                  Demo verisi kur: ornek "demo" sirketi ve demo.admin kullanicisi
                          (deneme/sunum icin; uretimde kullanmayin). Varsayilan: kurulmaz.
                          Yalnizca cok sirket kipinde (--tenancy multi).
  --no-signup             Herkese acik sirket kaydini kapat (giris ekranindaki "Sirketinizi
                          kaydedin"); sirketleri yalnizca platform.admin olusturur.
  --edition TUR           full (Tam: tum moduller) | lite (Hafif: cekirdek + puantaj + bordro) |
                          custom (--modules ile). Varsayilan: sorulur; --yes ile bellek 8 GB'tan
                          azsa lite, degilse full.
  --modules LISTE         Acik isteğe bagli moduller (virgullu): recruitment, onboarding, timeshift,
                          performance, learning, compensation, ml, expense. Sonradan: Platform > Bakim >
                          Moduller ya da scripts/modules.sh.
  --tenancy KIP           single (tek sirket; onerilen) | multi (cok sirket: barindirma, holding, buro).
                          Varsayilan: Hafif'te single, Tam'da sorulur (--yes ile multi).
  --company AD            Tek sirket kipinde sirketin adi.
  -y, --yes               Hicbir soru sorma; verilmeyenler icin varsayilanlar kullanilir,
                          sirlar rastgele uretilir. Mevcut kurulumda guncelleme yapilir.
  -h, --help              Bu yardim.
EOF
}

ASSUME_YES=0
OPT_URL=""; OPT_DOMAIN=""; OPT_HTTP_PORT=""; OPT_EMAIL=""; OPT_TLS=""; OPT_CERT=""; OPT_KEY=""
OPT_SMTP_FROM=""; OPT_SMTP_HOST=""; OPT_SMTP_PORT=""; OPT_SMTP_USER=""; OPT_SMTP_NAME=""
OPT_SMTP_PASSWORD="${HR360_SMTP_PASSWORD:-}"; OPT_SMTP_TEST=1; OPT_KC_ADMIN=""
OPT_DEMO=""; OPT_SIGNUP=""; OPT_MAIL_MODE=""; OPT_EDITION=""; OPT_MODULES=""; OPT_TENANCY=""; OPT_COMPANY=""
while [ $# -gt 0 ]; do
  case "$1" in
    --domain) OPT_DOMAIN="${2:-}"; shift 2 ;;
    --url) OPT_URL="${2:-}"; shift 2 ;;
    --http-port) OPT_HTTP_PORT="${2:-}"; shift 2 ;;
    --email) OPT_EMAIL="${2:-}"; shift 2 ;;
    --tls) OPT_TLS="${2:-}"; shift 2 ;;
    --cert) OPT_CERT="${2:-}"; shift 2 ;;
    --key) OPT_KEY="${2:-}"; shift 2 ;;
    --smtp-from) OPT_SMTP_FROM="${2:-}"; shift 2 ;;
    --smtp-host) OPT_SMTP_HOST="${2:-}"; shift 2 ;;
    --smtp-port) OPT_SMTP_PORT="${2:-}"; shift 2 ;;
    --smtp-user) OPT_SMTP_USER="${2:-}"; shift 2 ;;
    --smtp-password) OPT_SMTP_PASSWORD="${2:-}"; shift 2 ;;
    --smtp-name) OPT_SMTP_NAME="${2:-}"; shift 2 ;;
    --no-smtp-test) OPT_SMTP_TEST=0; shift ;;
    --mail-mode) OPT_MAIL_MODE="${2:-}"; shift 2 ;;
    --mail-mode=*) OPT_MAIL_MODE="${1#*=}"; shift ;;
    --keycloak-admin) OPT_KC_ADMIN="${2:-}"; shift 2 ;;
    --demo) OPT_DEMO=true; shift ;;
    --no-signup) OPT_SIGNUP=false; shift ;;
    --edition) OPT_EDITION="${2:-}"; shift 2 ;;
    --modules) OPT_MODULES="${2:-}"; shift 2 ;;
    --tenancy) OPT_TENANCY="${2:-}"; shift 2 ;;
    --company) OPT_COMPANY="${2:-}"; shift 2 ;;
    -y|--yes) ASSUME_YES=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Bilinmeyen secenek: $1 (yardim: ./install.sh --help)"; exit 2 ;;
  esac
done
if [ -n "$OPT_DOMAIN" ]; then
  OPT_DOMAIN="${OPT_DOMAIN#http://}"; OPT_DOMAIN="${OPT_DOMAIN#https://}"; OPT_DOMAIN="${OPT_DOMAIN%/}"
  [[ "$OPT_DOMAIN" =~ ^[A-Za-z0-9.-]+\.[A-Za-z]{2,}$ ]] || { echo "Gecersiz alan adi: $OPT_DOMAIN"; exit 2; }
  [ -z "$OPT_URL" ] || { echo "--domain ve --url birlikte verilemez."; exit 2; }
  OPT_URL="https://${OPT_DOMAIN}"
  if [ -n "$OPT_HTTP_PORT" ] && [ "$OPT_HTTP_PORT" != 80 ]; then
    echo "--domain ile HTTP portu 80 olmali (Let's Encrypt dogrulamasi 80'den yapilir)."; exit 2
  fi
  OPT_HTTP_PORT=80
fi
case "$OPT_TLS" in ""|letsencrypt|self-signed|none|cert) ;; *) echo "Gecersiz --tls: $OPT_TLS"; exit 2 ;; esac
case "$OPT_EDITION" in ""|full|lite|custom) ;; *) echo "Gecersiz --edition: $OPT_EDITION (full|lite|custom)"; exit 2 ;; esac
case "$OPT_TENANCY" in ""|single|multi) ;; *) echo "Gecersiz --tenancy: $OPT_TENANCY (single|multi)"; exit 2 ;; esac
demo_single_err() {
  echo "--demo tek sirket kipinde kullanilamaz: demo verisi ayri bir \"demo\" sirketi olusturur."
  echo "Demo icin --tenancy multi ile ayri bir kurulum yapin."
  exit 2
}
[ "$OPT_DEMO" = true ] && [ "$OPT_TENANCY" = single ] && demo_single_err
if [ -n "$OPT_MODULES" ]; then
  [ -z "$OPT_EDITION" ] && OPT_EDITION=custom
  [ "$OPT_EDITION" = custom ] || { echo "--modules yalnizca --edition custom ile (ya da tek basina) verilir."; exit 2; }
  for _m in ${OPT_MODULES//,/ }; do
    case "$_m" in recruitment|onboarding|timeshift|performance|learning|compensation|ml|expense) ;;
      *) echo "Bilinmeyen modul: $_m"; exit 2 ;; esac
  done
fi
if [ -n "$OPT_COMPANY" ] && ! [[ "$OPT_COMPANY" =~ ^[^\'\"\`\$]{2,80}$ ]]; then
  echo "Gecersiz sirket adi (2-80 karakter; tirnak ve \$ olmadan)."; exit 2
fi
if [ -n "$OPT_EMAIL" ] && ! [[ "$OPT_EMAIL" =~ ^[^@[:space:]]+@[^@[:space:]]+[.][^@[:space:]]+$ ]]; then
  echo "Gecersiz e-posta: $OPT_EMAIL"; exit 2
fi
# --yes ile e-posta yapilandirmasi istendiyse parola eksikligi en basta yakalanir.
case "$OPT_MAIL_MODE" in
  ""|smtp) OPT_MAIL_MODE="" ;;
  self)
    if [ -z "$OPT_DOMAIN" ] && { [ -z "$OPT_URL" ] || ! [[ "$OPT_URL" =~ ^(https?://)?[^/:]*[A-Za-z][^/:]*\.[A-Za-z]{2,}(:[0-9]+)?/?$ ]]; } \
       && [ "$ASSUME_YES" = 1 ]; then
      echo "--mail-mode self yalnizca alan adli kurulumda kullanilabilir (--domain hr.sirket.com)."; exit 2
    fi
    if [ -n "$OPT_SMTP_HOST$OPT_SMTP_PORT$OPT_SMTP_USER$OPT_SMTP_PASSWORD" ]; then
      echo "--mail-mode self ile --smtp-host/--smtp-port/--smtp-user/--smtp-password verilemez (sunucu paketteki posta servisidir)."; exit 2
    fi ;;
  *) echo "Gecersiz --mail-mode: $OPT_MAIL_MODE (self)"; exit 2 ;;
esac
if [ "$ASSUME_YES" = 1 ] && [ -z "$OPT_MAIL_MODE" ] && [ -n "$OPT_SMTP_FROM" ] && [ "$OPT_SMTP_HOST" != mailpit ] \
   && [ "$OPT_SMTP_USER" != "-" ] && [ -z "$OPT_SMTP_PASSWORD" ]; then
  echo "--smtp-from verildi ama parola yok: --smtp-password ya da HR360_SMTP_PASSWORD (kimlik dogrulamasiz icin --smtp-user -)."
  exit 2
fi

# ask DEGISKEN "soru": girdiyi degiskene yazar. --yes ile soru sorulmaz, bos (varsayilan) kabul edilir.
ask() {
  local __in=""
  if [ "$ASSUME_YES" = 1 ]; then echo "$2(otomatik: varsayilan)"
  else read -r -p "$2" __in || true; fi
  printf -v "$1" '%s' "$__in"
}

random_secret() {
  # URL-safe, 24 karakter
  openssl rand -base64 24 2>/dev/null | tr -dc 'A-Za-z0-9' | head -c 24 \
    || head -c 32 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c 24
}

random_aes_key() {
  # TAM 32 ham byte'lik, gecerli base64 kodlu bir anahtar uretir (AES-256 icin).
  # NOT: random_secret() ozel karakterleri (+ / =) siler ve 24 karaktere keser
  # - bu, gecerli bir base64(32 byte) DEGILDIR ve Convert.FromBase64String()
  # ile acilinca ~18 byte'a dusup SmtpCredentialProtector'in "32 byte
  # (base64) olmali" hatasini atmasina sebep olur (hardcore test sirasinda
  # bulundu: TENANT_SECRET_KEY icin bu fonksiyon kullanilmali, random_secret()
  # DEGIL). Ozel karakterler KORUNUR, kesinlikle strip/kirpma yapilmaz.
  openssl rand -base64 32 2>/dev/null \
    || head -c 32 /dev/urandom | base64
}

# Gecerli bir AES-256 anahtari mi: base64, cozulunce tam 32 byte.
is_aes_key() {
  # wc -c macOS'ta basa bosluk koyar; aritmetik ile sayiya cevrilir.
  [ "$(( $(printf '%s' "$1" | base64 -d 2>/dev/null | wc -c) ))" = "32" ]
}

ask_secret_aes_key() {
  # ask_secret ile ayni akis, ama otomatik uretimde random_aes_key() kullanir
  # (tam 32 ham byte garantisi icin). Elle girilen deger de dogrulanir: servisler
  # gecersiz anahtarla (SmtpCredentialProtector) acilista hata veriyordu.
  local var_name="$1"; local prompt="$2"
  local input=""
  while :; do
    input=""
    ask input "$prompt [bos birakin, otomatik guclu bir deger uretilsin]: "
    if [ -z "$input" ]; then
      input="$(random_aes_key)"
      echo "   -> otomatik uretildi (.env dosyasina yazilacak)"
      break
    fi
    is_aes_key "$input" && break
    warn "Gecersiz anahtar: base64 kodlu tam 32 byte olmali (ornek: openssl rand -base64 32)."
  done
  printf -v "$var_name" '%s' "$input"
}

ask_secret() {
  # ask_secret <degisken_adi> <soru_metni> [en_az_uzunluk]
  # Elle girilen sirlar .env'e tirnaksiz yazilir ve baglanti dizelerinde/URI'lerde
  # kullanilir ($, ;, @, /, :, bosluk, # gibi karakterler bunlari bozar). Bu yuzden
  # yalnizca guvenli karakterlere izin verilir.
  # Keycloak kullanici parolalari (demo.admin, platform.admin) realm parola politikasina
  # (en az 12 karakter) uymali: uymazsa Keycloak realm'i iceri alirken acilmaz.
  local var_name="$1"; local prompt="$2"; local min="${3:-8}"
  local input=""
  while :; do
    input=""
    ask input "$prompt [bos birakin, otomatik guclu bir deger uretilsin]: "
    if [ -z "$input" ]; then
      input="$(random_secret)"
      echo "   -> otomatik uretildi (.env dosyasina yazilacak)"
      break
    fi
    [[ "$input" =~ ^[A-Za-z0-9._~+=-]+$ ]] && [ "${#input}" -ge "$min" ] && break
    warn "En az ${min} karakter; yalnizca harf, rakam ve . _ ~ + = - kullanin."
  done
  printf -v "$var_name" '%s' "$input"
}

echo ""
echo "${BOLD}HR360 Enterprise ${HR360_VERSION:+${HR360_VERSION} }— tek sunucu kurulumu${RESET}"
echo "----------------------------------------------"

# --- 1) On kosullar -----------------------------------------------------
USE_SUDO_DOCKER=0
# root olarak calisirken (ozellikle sudo'nun kurulu olmadigi minimal Debian/Alpine
# sunucularda) "sudo" komutlari dogrudan calistirilir.
if [ "$(id -u)" = 0 ] && ! command -v sudo >/dev/null 2>&1; then
  sudo() { "$@"; }
fi
OS_KERNEL="$(uname -s)"
IS_WSL=0
if [ "$OS_KERNEL" = "Linux" ] && grep -qi microsoft /proc/version 2>/dev/null; then IS_WSL=1; fi

# Isletim sistemi kimligi (/etc/os-release). OS_RELEASE_FILE test icin degistirilebilir.
OS_ID=""; OS_LIKE=""; OS_VERSION=""; OS_CODENAME=""
if [ -r "${OS_RELEASE_FILE:-/etc/os-release}" ]; then
  # shellcheck disable=SC1090
  OS_ID="$(. "${OS_RELEASE_FILE:-/etc/os-release}"; echo "${ID:-}")"
  OS_LIKE="$(. "${OS_RELEASE_FILE:-/etc/os-release}"; echo "${ID_LIKE:-}")"
  OS_VERSION="$(. "${OS_RELEASE_FILE:-/etc/os-release}"; echo "${VERSION_ID:-}")"
  OS_CODENAME="$(. "${OS_RELEASE_FILE:-/etc/os-release}"; echo "${UBUNTU_CODENAME:-${VERSION_CODENAME:-}}")"
fi

# Docker'in hangi yolla kurulacagi: apt-ubuntu | apt-debian | apt-raspbian |
# dnf-fedora | dnf-rhel | dnf-centos | amzn | zypper | pacman | apk | (bos = desteklenmiyor)
docker_install_method() {
  local like=" ${OS_LIKE} "
  case "$OS_ID" in
    ubuntu) echo apt-ubuntu; return ;;
    debian) echo apt-debian; return ;;
    raspbian) echo apt-raspbian; return ;;
    fedora) echo dnf-fedora; return ;;
    rhel) echo dnf-rhel; return ;;
    centos|rocky|almalinux|ol|eurolinux|navy|circle|virtuozzo|cloudlinux) echo dnf-centos; return ;;
    amzn) echo amzn; return ;;
    opensuse*|sles|sled|suse) echo zypper; return ;;
    arch|manjaro|endeavouros|garuda) echo pacman; return ;;
    alpine) echo apk; return ;;
  esac
  # Turev dagitimlar (Linux Mint, Pop!_OS, Zorin, Kali...) ID_LIKE ile eslesir.
  case "$like" in
    *" ubuntu "*) echo apt-ubuntu ;;
    *" debian "*) echo apt-debian ;;
    *" rhel "*|*" centos "*) echo dnf-centos ;;
    *" fedora "*) echo dnf-fedora ;;
    *" suse "*|*" opensuse "*) echo zypper ;;
    *" arch "*) echo pacman ;;
    *) echo "" ;;
  esac
}

pkg_manager() {
  case "$(docker_install_method)" in
    apt-*) echo apt ;;
    dnf-*) command -v dnf >/dev/null 2>&1 && echo dnf || echo yum ;;
    amzn) command -v dnf >/dev/null 2>&1 && echo dnf || echo yum ;;
    zypper) echo zypper ;;
    pacman) echo pacman ;;
    apk) echo apk ;;
  esac
}

# Kurulumun kullandigi temel araclar (curl, openssl) yoksa paket yoneticisiyle kurulur.
ensure_tools() {
  local missing=()
  for t in curl openssl; do command -v "$t" >/dev/null 2>&1 || missing+=("$t"); done
  [ ${#missing[@]} -eq 0 ] && return 0
  if [ "$OS_KERNEL" != "Linux" ]; then
    echo "Gerekli araclar eksik: ${missing[*]}. Kurup scripti tekrar calistirin."; exit 1
  fi
  info "Eksik araclar kuruluyor: ${missing[*]}"
  case "$(pkg_manager)" in
    apt) sudo apt-get update -qq && sudo apt-get install -y -qq "${missing[@]}" ca-certificates ;;
    dnf) sudo dnf install -y -q "${missing[@]}" ;;
    yum) sudo yum install -y -q "${missing[@]}" ;;
    zypper) sudo zypper -n -q install "${missing[@]}" ;;
    pacman) sudo pacman -Sy --noconfirm --needed "${missing[@]}" ;;
    apk) sudo apk add --no-cache "${missing[@]}" ;;
    *) echo "Gerekli araclar eksik: ${missing[*]}. Kurup scripti tekrar calistirin."; exit 1 ;;
  esac
}

# Docker Compose v2 eklentisi paketle gelmeyen dagitimlarda (Amazon Linux, bazi
# SUSE surumleri) resmi ikili indirilir.
install_compose_plugin() {
  if sudo docker compose version >/dev/null 2>&1; then return 0; fi
  local arch; arch="$(uname -m)"
  case "$arch" in x86_64|aarch64|armv7l|ppc64le|s390x) ;; arm64) arch=aarch64 ;; *) echo "Compose icin desteklenmeyen mimari: $arch"; exit 1 ;; esac
  info "Docker Compose eklentisi indiriliyor..."
  sudo mkdir -p /usr/local/lib/docker/cli-plugins
  sudo curl -fsSL "https://github.com/docker/compose/releases/latest/download/docker-compose-linux-${arch}" \
    -o /usr/local/lib/docker/cli-plugins/docker-compose
  sudo chmod +x /usr/local/lib/docker/cli-plugins/docker-compose
}

install_docker() {
  local method; method="$(docker_install_method)"

  if [ "$OS_KERNEL" = "Darwin" ]; then
    echo "macOS'ta Docker Desktop gerekir: https://www.docker.com/products/docker-desktop/"
    echo "Kurduktan sonra Settings > Resources'ta bellegi en az 8 GB (onerilen 12 GB) yapin, Docker Desktop'i"
    echo "baslatin ve bu scripti tekrar calistirin. Ayrintilar: docs/kurulum/isletim-sistemleri.md"
    exit 1
  fi
  if [ "$IS_WSL" = 1 ] && [ "$(ps -p 1 -o comm= 2>/dev/null)" != "systemd" ]; then
    echo "WSL'de Docker iki yoldan kullanilabilir:"
    echo "  1) Windows'a Docker Desktop kurup Settings > Resources > WSL Integration'da bu dagitimi acin (onerilen)."
    echo "  2) Docker Engine'i WSL icine kurun: once systemd'yi acin (/etc/wsl.conf -> [boot] systemd=true,"
    echo "     PowerShell'de 'wsl --shutdown'), sonra bu scripti tekrar calistirin."
    echo "Ayrintilar: docs/kurulum/isletim-sistemleri.md"
    exit 1
  fi
  if [ -z "$method" ]; then
    echo "Bu dagitim (${OS_ID:-bilinmiyor}) icin Docker otomatik kurulamiyor."
    echo "Once Docker Engine + Compose eklentisini kurun: https://docs.docker.com/engine/install/"
    echo "Ayrintilar: docs/kurulum/isletim-sistemleri.md"
    exit 1
  fi

  warn "Docker Engine bulunamadi."
  echo "Docker Engine ve Compose eklentisini ${OS_ID} ${OS_VERSION} icin resmi depodan, 'sudo'"
  echo "yetkisiyle kurmami ister misiniz? Mevcut kullanici (${USER:-$(whoami)}) 'docker' grubuna eklenir."
  if [ "$ASSUME_YES" = 1 ]; then reply=e; else read -r -p "Devam edilsin mi? [e/H]: " reply || true; fi
  if [[ ! "$reply" =~ ^[eEyY]$ ]]; then
    echo "Kurulum iptal edildi. Docker'i elle kurup scripti tekrar calistirabilirsiniz: https://docs.docker.com/engine/install/"
    exit 1
  fi

  info "Docker Engine kuruluyor (${method}; sudo sifresi istenebilir)..."
  local pkgs="docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin"
  case "$method" in
    apt-*)
      local repo="${method#apt-}" codename="$OS_CODENAME"
      sudo apt-get update -qq
      sudo apt-get install -y -qq ca-certificates curl gnupg
      sudo install -m 0755 -d /etc/apt/keyrings
      sudo curl -fsSL "https://download.docker.com/linux/${repo}/gpg" -o /etc/apt/keyrings/docker.asc
      sudo chmod a+r /etc/apt/keyrings/docker.asc
      echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/${repo} ${codename} stable" \
        | sudo tee /etc/apt/sources.list.d/docker.list >/dev/null
      sudo apt-get update -qq
      # shellcheck disable=SC2086
      sudo apt-get install -y -qq $pkgs
      ;;
    dnf-*)
      local repo="${method#dnf-}" pm; pm="$(pkg_manager)"
      sudo curl -fsSL "https://download.docker.com/linux/${repo}/docker-ce.repo" -o /etc/yum.repos.d/docker-ce.repo
      # RHEL/Rocky/Alma'da onceden gelen podman-docker/runc paketleriyle cakisma
      # olursa --allowerasing onlari kaldirir.
      if [ "$pm" = dnf ]; then
        # shellcheck disable=SC2086
        sudo dnf install -y --allowerasing $pkgs
      else
        # shellcheck disable=SC2086
        sudo yum install -y $pkgs
      fi
      ;;
    amzn)
      if command -v dnf >/dev/null 2>&1; then sudo dnf install -y docker; else sudo yum install -y docker; fi
      ;;
    zypper)
      sudo zypper -n install docker || {
        echo "SLES'te once Containers modulunu acin: sudo SUSEConnect -p sle-module-containers/${OS_VERSION}/$(uname -m)"
        exit 1
      }
      sudo zypper -n install docker-compose >/dev/null 2>&1 || true
      ;;
    pacman)
      sudo pacman -Sy --noconfirm --needed docker docker-compose docker-buildx
      ;;
    apk)
      sudo apk add --no-cache docker docker-cli-compose
      sudo rc-update add docker default >/dev/null 2>&1 || true
      ;;
  esac

  if [ "$method" = apk ]; then
    sudo rc-service docker start >/dev/null 2>&1 || sudo service docker start >/dev/null 2>&1 || true
  else
    sudo systemctl enable --now docker >/dev/null 2>&1 \
      || warn "docker servisi systemctl ile baslatilamadi, devam ediliyor (farkli bir init sistemi olabilir)."
  fi
  for _ in $(seq 1 15); do sudo docker info >/dev/null 2>&1 && break; sleep 2; done
  install_compose_plugin

  if [ "$(id -u)" != 0 ] && ! id -nG "${USER:-$(whoami)}" 2>/dev/null | grep -qw docker; then
    sudo usermod -aG docker "${USER:-$(whoami)}" 2>/dev/null || sudo addgroup "${USER:-$(whoami)}" docker 2>/dev/null || true
    warn "Kullaniciniz 'docker' grubuna eklendi; bu ancak yeni bir oturumda (yeniden giris/SSH) etkin olur."
    warn "Bu kurulumun geri kalaninda gecici olarak 'sudo docker' kullanilacak."
    USE_SUDO_DOCKER=1
  fi

  info "Docker Engine kuruldu: $(sudo docker --version 2>/dev/null || docker --version)"
}

# Windows dosya sisteminde (/mnt/c/...) calismak hem cok yavas hem de satir sonu
# (CRLF) sorunlarina acik; repo WSL'in kendi dosya sistemine klonlanmali.
if [ "$IS_WSL" = 1 ]; then
  case "$PWD" in
    /mnt/[a-z]/*) warn "Repo Windows diski uzerinde ($PWD). Build cok yavas olur; ~/ altina klonlamaniz onerilir." ;;
  esac
fi

[ "$OS_KERNEL" = "Linux" ] && ensure_tools

if ! command -v docker >/dev/null 2>&1; then
  install_docker
elif ! docker info >/dev/null 2>&1; then
  # Docker kurulu ama mevcut kullanicinin 'docker' grup yetkisi henuz aktif
  # olmayabilir (yeni eklenmis olabilir) - sudo ile devam edelim.
  if [ "$OS_KERNEL" = "Darwin" ] || { [ "$IS_WSL" = 1 ] && [ "$(ps -p 1 -o comm= 2>/dev/null)" != "systemd" ]; }; then
    echo "Docker'a ulasilamiyor. Docker Desktop'in calistigindan (WSL'de: Settings > Resources >"
    echo "WSL Integration'da bu dagitimin acik oldugundan) emin olup scripti tekrar calistirin."
    exit 1
  elif [ "$(id -u)" != 0 ] && sudo docker info >/dev/null 2>&1; then
    warn "Docker kurulu ama mevcut oturumda 'docker' grubu yetkiniz henuz aktif degil, 'sudo docker' kullanilacak."
    USE_SUDO_DOCKER=1
  else
    echo "Docker kurulu gorunuyor ama calismiyor. 'sudo systemctl start docker' ile baslatip"
    echo "'sudo systemctl status docker' ile kontrol edin."
    exit 1
  fi
fi

# Bu noktadan itibaren tum docker cagrilari (docker compose dahil) bu
# sarmalayicidan gecer - USE_SUDO_DOCKER=1 ise otomatik 'sudo docker' olur.
docker() {
  if [ "$USE_SUDO_DOCKER" = "1" ]; then
    command sudo docker "$@"
  else
    command docker "$@"
  fi
}
# Yardimci betikler (scripts/tls.sh, scripts/keycloak-admin-access.sh) ayri
# surec olarak calisir; sarmalayici onlara da gecsin diye disari aktarilir.
export USE_SUDO_DOCKER
export -f docker

if ! docker compose version >/dev/null 2>&1; then
  if [ "$OS_KERNEL" = "Linux" ] && [ "$IS_WSL" = 0 ]; then
    # Dagitimin kendi docker paketiyle kurulmus sistemlerde eklenti eksik olabilir.
    install_compose_plugin
  fi
  if ! docker compose version >/dev/null 2>&1; then
    echo "Docker Compose (v2 eklentisi) bulunamadi. 'docker compose version' calisir hale getirin:"
    echo "https://docs.docker.com/compose/install/linux/"
    exit 1
  fi
fi

# Kaynak kontrolu: Docker'in kullanabildigi bellek ve bulundugumuz diskteki bos alan.
mem_bytes="$(docker info --format '{{.MemTotal}}' 2>/dev/null || echo 0)"
if [ "${mem_bytes:-0}" -gt 0 ] && [ "$mem_bytes" -lt $((7 * 1024 * 1024 * 1024)) ]; then
  warn "Docker'in kullanabildigi bellek $((mem_bytes / 1024 / 1024 / 1024)) GB; Tam kurulum icin en az 8 GB (onerilen 16 GB),"
  warn "Hafif kurulum (cekirdek + puantaj + bordro) icin en az 6 GB gerekir."
  if [ "$OS_KERNEL" = "Darwin" ] || [ "$IS_WSL" = 1 ]; then
    warn "Docker Desktop: Settings > Resources > Memory (WSL'de ayrica %UserProfile%\\.wslconfig)."
  fi
fi
free_kb="$(df -Pk . 2>/dev/null | awk 'NR==2 {print $4}')"
if [ -n "$free_kb" ] && [ "$free_kb" -lt $((40 * 1024 * 1024)) ]; then
  warn "Bu diskte $((free_kb / 1024 / 1024)) GB bos alan var; imajlar ve derleme icin en az 40 GB onerilir."
fi

if [ -f .env ]; then
  warn ".env dosyasi zaten var."
  overwrite=""; ask overwrite "Uzerine yazip sirlari yeniden mi uretelim? [e/H]: "
  if [[ "$overwrite" =~ ^[eEyY]$ ]]; then
    # Mevcut veriler (volume'ler) eski sirlarla olusturuldu: Postgres eski parolayi,
    # Keycloak eski realm ve yonetici parolasini, kiracilarin sifreli SMTP parolalari
    # eski anahtari kullanir. Yeni sirlarla bu verilere erisilemez; yeniden uretmek
    # ancak TUM verileri silerek mumkundur.
    project="$(docker compose config 2>/dev/null | sed -n 's/^name: //p' | head -1)"
    if [ -n "$project" ] && docker volume ls -q --filter "label=com.docker.compose.project=${project}" | grep -q .; then
      warn "Bu kurulumun verileri var (veritabani, Keycloak kullanicilari, logolar)."
      warn "Sirlari yeniden uretmek icin BUTUN veriler silinecek; bu geri alinamaz."
      read -r -p "Tum verileri silip sifirdan kurmak icin SIL yazin (vazgecmek icin Enter): " wipe || true
      if [ "$wipe" != "SIL" ]; then
        echo "Vazgecildi. Guncelleme icin scripti tekrar calistirip bu soruya 'H' deyin."
        exit 1
      fi
      docker compose --profile letsencrypt down -v --remove-orphans
      rm -f deploy/nginx/tls/*.conf deploy/nginx/tls/*.pem
    fi
  else
    info "Mevcut .env korunuyor (guncelleme): once veritabani goclerini uyguluyorum."
    set -a; . ./.env; set +a
    # scripts/secrets-migrate.sh ile secrets/*.txt'ye tasinmis sirlar (.env'de bos): denetimler ve
    # "eksikse uret" adimlari dosyadaki degeri gorsun, yenisini uretip .env'e yazmasin.
    for _k in TENANT_SECRET_KEY INTERNAL_SERVICE_TOKEN KEYCLOAK_TENANT_ADMIN_CLIENT_SECRET; do
      if [ -z "${!_k:-}" ] && secret_in_file "$_k"; then printf -v "$_k" '%s' "$(secret_get "$_k")"; fi
    done
    if ! is_aes_key "${TENANT_SECRET_KEY:-}"; then
      warn "TENANT_SECRET_KEY gecerli bir AES-256 anahtari degil (base64, 32 byte olmali)."
      warn "Kiracilarin SMTP parolalari bu anahtarla sifrelendigi icin otomatik degistirilmiyor;"
      warn "ozel SMTP kullanan kiraci yoksa .env'de 'openssl rand -base64 32' ile yenileyin."
    fi

    # Keycloak artik sabit genel adresle (KC_HOSTNAME) calisiyor; eski .env'lerde
    # PUBLIC_ORIGIN yok - PUBLIC_URL ve GATEWAY_PORT'tan uretilir.
    if [ -z "${PUBLIC_ORIGIN:-}" ]; then
      if [[ "${PUBLIC_URL%/}" =~ ://[^/]+:[0-9]+$ ]] || [ -z "${GATEWAY_PORT:-}" ] \
         || [ "${GATEWAY_PORT}" = "80" ] || [ "${GATEWAY_PORT}" = "443" ]; then
        PUBLIC_ORIGIN="${PUBLIC_URL%/}"
      else
        PUBLIC_ORIGIN="${PUBLIC_URL%/}:${GATEWAY_PORT}"
      fi
      echo "PUBLIC_ORIGIN=${PUBLIC_ORIGIN}" >> .env
      info "PUBLIC_ORIGIN=${PUBLIC_ORIGIN} .env'e eklendi (Keycloak genel adresi)."
    fi

    # Servisler arasi ic cagri anahtari (Slack/Teams'ten onay kararlari icin
    # governance -> workflow). Eski .env'lerde yok; uretilip eklenir.
    if [ -z "${INTERNAL_SERVICE_TOKEN:-}" ]; then
      INTERNAL_SERVICE_TOKEN="$(random_secret)$(random_secret)"
      echo "INTERNAL_SERVICE_TOKEN=${INTERNAL_SERVICE_TOKEN}" >> .env
      info "INTERNAL_SERVICE_TOKEN .env'e eklendi."
    fi

    # Yonetici e-postasi verildiyse sistem uyarilarinin alicisi yapilir (bossa).
    if [ -n "$OPT_EMAIL" ] && [ -z "${ALERT_EMAIL_TO:-}" ]; then
      printf 'HR360_ADMIN_EMAIL=%s\nALERT_EMAIL_TO=%s\n' "$OPT_EMAIL" "$OPT_EMAIL" >> .env
      info "ALERT_EMAIL_TO=${OPT_EMAIL} .env'e eklendi."
    fi
    if [ -n "$OPT_SMTP_FROM" ] || [ -n "$OPT_MAIL_MODE" ]; then
      warn "Guncellemede e-posta ayari degistirilmez: scripts/smtp.sh set --from ... (ya da scripts/smtp.sh self) ile ayarlayin."
    fi
    # HR360_DEMO .env'de yoksa (bu ayardan onceki kurulum) demo davranisi aynen korunur
    # (tenant-service degiskeni yoksa demo sirketini olusturur); guncelleme onu degistirmez.
    if [ -n "$OPT_DEMO" ]; then
      warn "Guncellemede --demo uygulanmaz: demo kullanicisi realm'e yalnizca ilk kurulumda eklenir."
    fi
    if [ -n "$OPT_SIGNUP" ]; then
      if grep -q '^PUBLIC_SIGNUP_ENABLED=' .env; then sed_i "s/^PUBLIC_SIGNUP_ENABLED=.*/PUBLIC_SIGNUP_ENABLED=${OPT_SIGNUP}/" .env
      else echo "PUBLIC_SIGNUP_ENABLED=${OPT_SIGNUP}" >> .env; fi
      # .env yukarida kabuga aktarildi (set -a); compose kabuktaki eski degeri tercih etmesin.
      export PUBLIC_SIGNUP_ENABLED="$OPT_SIGNUP"
      info "Herkese acik sirket kaydi kapatildi (PUBLIC_SIGNUP_ENABLED=false)."
    fi

    # Redis (Valkey) onbellek parolasi; eski .env'lerde yok.
    if [ -z "${REDIS_PASSWORD:-}" ]; then
      REDIS_PASSWORD="$(random_secret)$(random_secret)"
      echo "REDIS_PASSWORD=${REDIS_PASSWORD}" >> .env
      info "REDIS_PASSWORD .env'e eklendi."
    fi

    # Yedek sifreleme anahtari (KVKK m.12); eski .env'lerde yok.
    if [ -z "${BACKUP_ENCRYPTION_KEY:-}" ]; then
      BACKUP_ENCRYPTION_KEY="$(random_secret)$(random_secret)$(random_secret)"
      echo "BACKUP_ENCRYPTION_KEY=${BACKUP_ENCRYPTION_KEY}" >> .env
      info "BACKUP_ENCRYPTION_KEY .env'e eklendi; yedekler artik sifreli. Anahtari ayri bir yerde saklayin."
    fi

    # NOT: Onceden bu yol yalnizca "docker compose up --build" calistiriyordu -
    # scripts/sql altindaki goc betikleri hic uygulanmiyordu (yeni kolon eksik
    # kalinca ilgili servis tum sorgularda 500 veriyordu).
    docker compose up -d postgres
    pg_ok=0
    for _ in $(seq 1 60); do
      docker compose exec -T postgres pg_isready -U hr360admin >/dev/null 2>&1 && { pg_ok=1; break; }
      sleep 2
    done
    [ "$pg_ok" = 1 ] || { echo "PostgreSQL 2 dakikada hazir olmadi; 'docker compose logs postgres' ile kontrol edin."; exit 1; }
    info "Goc betikleri uygulaniyor (scripts/sql)..."
    scripts/migrate.sh || exit 1

    # Keycloak artik Postgres'teki "keycloak" veritabanini kullaniyor. Veritabani yoksa
    # iki durum var: (a) Keycloak hic kurulmamis -> olustur; (b) eski kurulum, veriler
    # hala Keycloak konteynerinin ICINDE -> dogrudan baslatmak tum kullanicilari
    # "kaybettirir" (bos veritabaniyla acilir). (b)'de durup runbook'u gosteriyoruz.
    if ! docker compose exec -T postgres psql -U hr360admin -d postgres -tAc \
         "SELECT 1 FROM pg_database WHERE datname='keycloak'" | grep -q 1; then
      if [ -n "$(docker compose ps -a -q keycloak 2>/dev/null)" ]; then
        warn "Keycloak kullanicilari hala eski konteynerin icinde (dosya veritabani)."
        warn "Postgres'e gecis icin once su adimlari uygulayin: docs/runbooks/keycloak-postgres-gecisi.md"
        warn "Diger servisleri baslatmadan cikiliyor; Keycloak'a dokunulmadi."
        exit 1
      fi
      docker compose exec -T postgres psql -U hr360admin -d postgres -c "CREATE DATABASE keycloak OWNER hr360admin;"
    fi

    # Hazir imaj (scripts/lib/images.sh): CI'nin bu commit icin yayinladigi imzali imajlar indirilir;
    # yoksa ya da imza dogrulanamazsa sunucuda derlenir.
    build_targets="$( ENV_FILE=.env; . scripts/lib/modules.sh; mod_build_targets )"
    # shellcheck source=scripts/lib/images.sh
    . scripts/lib/images.sh; images_use_tag
    # shellcheck disable=SC2086
    images_pull $build_targets || docker compose build $build_targets
    docker compose up -d
    scripts/modules.sh sync >/dev/null 2>&1 || true

    # Guvenlik dalgasi 2A: tenant-service eski kurulumlarda Keycloak yonetim API'sine master
    # yonetici parolasiyla giriyordu. Servis hesabi bir kez olusturulur (anahtar .env'e eklenir,
    # parola politikasi ve giris olaylari uygulanir, tenant-service yeniden olusturulur).
    if [ -z "${KEYCLOAK_TENANT_ADMIN_CLIENT_SECRET:-}" ]; then
      info "Keycloak yonetim API servis hesabi olusturuluyor..."
      scripts/keycloak-service-account.sh apply \
        || warn "Servis hesabi olusturulamadi; sonra scripts/keycloak-service-account.sh ile deneyin."
    fi

    # Guncellemede verilen --domain/--tls ve --keycloak-admin de uygulanir (onceden yalnizca
    # uyari veriliyordu; alan adina gecis elle scripts/tls.sh ile yapilmak zorundaydi).
    if [ -n "$OPT_DOMAIN" ]; then
      email_args=(); [ -n "$OPT_EMAIL" ] && email_args=(--email "$OPT_EMAIL")
      info "Alan adi uygulaniyor: ${OPT_DOMAIN} (TLS: ${OPT_TLS:-auto})"
      case "$OPT_TLS" in
        letsencrypt) scripts/tls.sh enable --letsencrypt --host "$OPT_DOMAIN" "${email_args[@]}" ;;
        self-signed) scripts/tls.sh enable --self-signed --host "$OPT_DOMAIN" ;;
        cert) scripts/tls.sh enable --cert "$OPT_CERT" --key "$OPT_KEY" --host "$OPT_DOMAIN" ;;
        none) warn "--tls none ile alan adina gecis yapilmaz (HTTPS'siz alan adi onerilmez). TLS'i onundeki"
              warn "dengeleyici sonlandiriyorsa: scripts/tls.sh external --host ${OPT_DOMAIN}" ;;
        *) scripts/tls.sh auto --host "$OPT_DOMAIN" "${email_args[@]}" ;;
      esac || warn "Alan adi/HTTPS ayarlanamadi; scripts/tls.sh check --host ${OPT_DOMAIN} ile on kosullari denetleyin."
    fi
    if [ -n "$OPT_KC_ADMIN" ]; then
      case "$OPT_KC_ADMIN" in
        open|port) scripts/keycloak-admin-access.sh "$OPT_KC_ADMIN" ;;
        ip:*) scripts/keycloak-admin-access.sh ip "${OPT_KC_ADMIN#ip:}" ;;
        port:*) scripts/keycloak-admin-access.sh port "${OPT_KC_ADMIN#port:}" ;;
        *) warn "Gecersiz --keycloak-admin: $OPT_KC_ADMIN (open | ip:CIDR,... | port)" ;;
      esac || warn "Keycloak paneli erisimi ayarlanamadi; scripts/keycloak-admin-access.sh ile deneyin."
    fi
    # Veritabani yalitimi (varsayilan): servis basina en az yetkili rol + kiraci basina satir duzeyi guvenlik.
    # Kapatmak: .env'e HR360_DB_ISOLATION=off (ya da scripts/db-isolation.sh off).
    if scripts/db-isolation.sh ensure; then docker compose up -d >/dev/null 2>&1 || true
    else warn "Veritabani yalitimi acilamadi; sonra: scripts/db-isolation.sh ensure && docker compose up -d"; fi
    info "Guncelleme tamamlandi: $(grep '^PUBLIC_ORIGIN=' .env | tail -1 | cut -d= -f2-)"
    info "HTTPS: $(scripts/tls.sh status | head -1 | sed 's/^HTTPS: //')"
    exit 0
  fi
fi

# --- 2) Adres, HTTPS ve e-posta --------------------------------------------
echo ""
echo "Asagidaki sirlar icin Enter'a basarsaniz guclu, rastgele degerler"
echo "otomatik uretilir — cogu kullanim icin bu yeterli ve onerilir."
echo ""

if [ -n "$OPT_URL" ]; then
  PUBLIC_URL="${OPT_URL%/}"
  [[ "$PUBLIC_URL" =~ ^https?:// ]] || PUBLIC_URL="http://${PUBLIC_URL}"
  [[ "$PUBLIC_URL" =~ ^https?://[A-Za-z0-9.-]+(:[0-9]+)?$ ]] || { echo "Gecersiz adres: ${PUBLIC_URL}"; exit 2; }
  echo "Adres: ${PUBLIC_URL}"
else
  echo "Uygulamanin adresi. Alan adi girerseniz (orn. hr.sirket.com) HTTPS sertifikasi otomatik alinir."
  while :; do
    PUBLIC_URL=""
    ask PUBLIC_URL "Alan adi ya da adres [http://localhost]: "
    PUBLIC_URL=${PUBLIC_URL:-http://localhost}
    PUBLIC_URL="${PUBLIC_URL%/}"
    # Sema yazilmadiysa http varsayilir (Keycloak adresi ve jeton ureticisi sema ister).
    [[ "$PUBLIC_URL" =~ ^https?:// ]] || PUBLIC_URL="http://${PUBLIC_URL}"
    [[ "$PUBLIC_URL" =~ ^https?://[A-Za-z0-9.-]+(:[0-9]+)?$ ]] && break
    warn "Gecersiz adres: ${PUBLIC_URL} (ornek: hr.sirket.com ya da http://10.0.0.5 - yol icermemeli)"
  done
fi
PUBLIC_HOST="$(printf '%s' "${PUBLIC_URL}" | sed -E 's#^[a-zA-Z]+://([^/:]+).*#\1#')"
IS_DOMAIN=0
if [[ "$PUBLIC_HOST" =~ [A-Za-z] ]] && [[ "$PUBLIC_HOST" == *.* ]] && [ "$PUBLIC_HOST" != localhost ]; then
  IS_DOMAIN=1
fi

valid_gateway_port() {
  if ! [[ "$1" =~ ^[0-9]+$ ]] || [ "$1" -lt 1 ] || [ "$1" -gt 65535 ]; then
    warn "Gecersiz port: $1"
  elif [ "$1" = 443 ]; then
    # 443 HTTPS icin ayrilmis: tls.sh gateway'in 443'unu ayrica yayinlar; buraya
    # 443 verilirse iki eslesme cakisir ("port is already allocated").
    warn "443 HTTPS icindir ve otomatik acilir. HTTP portunu (genelde 80) girin."
  elif [ "$1" = 8090 ]; then
    warn "8090 Keycloak yonetim paneline ayrilmis; baska bir port girin."
  else
    return 0
  fi
  return 1
}
if [ -n "$OPT_HTTP_PORT" ]; then
  GATEWAY_PORT="$OPT_HTTP_PORT"
  valid_gateway_port "$GATEWAY_PORT" || exit 2
elif [ "$IS_DOMAIN" = 1 ]; then
  # Alan adiyla kurulumda Let's Encrypt dogrulamasi 80'den yapilir; soru sorulmaz.
  GATEWAY_PORT=80
else
  echo ""
  echo "HTTP portu: uygulama bu porttan yayinlanir. HTTPS (443) ayri ayarlanir."
  while :; do
    GATEWAY_PORT=""
    ask GATEWAY_PORT "Gateway'in disariya acacagi HTTP portu [80]: "
    GATEWAY_PORT=${GATEWAY_PORT:-80}
    valid_gateway_port "$GATEWAY_PORT" && break
  done
fi

KEYCLOAK_ADMIN_ALLOWED_IPS=""
# Varsayilan: panel ana adreste acik (ek firewall kurali gerekmez). Sonradan dis guvenlik
# duvarindan kisitlamak icin: scripts/keycloak-admin-access.sh port (panel 8090'a tasinir).
KC_ADMIN_DEFAULT=1
KC_ADMIN_DEFAULTED=0
if [ -n "$OPT_KC_ADMIN" ]; then
  KC_ADMIN_CHOICE=1
  case "$OPT_KC_ADMIN" in
    open) KC_ADMIN_CHOICE=1 ;;
    ip:*) KC_ADMIN_CHOICE=2; KEYCLOAK_ADMIN_ALLOWED_IPS="${OPT_KC_ADMIN#ip:}" ;;
    port) KC_ADMIN_CHOICE=3 ;;
    port:*) KC_ADMIN_CHOICE=3; KEYCLOAK_ADMIN_ALLOWED_IPS="${OPT_KC_ADMIN#port:}" ;;
    *) echo "Gecersiz --keycloak-admin: $OPT_KC_ADMIN (open | ip:CIDR,... | port)"; exit 2 ;;
  esac
else
  echo ""
  echo "Keycloak yonetim paneline erisim:"
  echo "  1) Ana adreste acik (varsayilan; sonradan 3'e gecip firewall'dan kisitlayabilirsiniz)"
  echo "  2) Yalnizca belirli IP/CIDR'ler (nginx ile)"
  echo "  3) Ayri port (8090) - erisimi dis guvenlik duvarinda siz kisitlarsiniz"
  ask KC_ADMIN_CHOICE "Seciminiz [${KC_ADMIN_DEFAULT}]: "
  [ -n "$KC_ADMIN_CHOICE" ] || { KC_ADMIN_CHOICE=$KC_ADMIN_DEFAULT; KC_ADMIN_DEFAULTED=1; }
  # Varsayilan (Enter) secildiyse ek IP sorulmaz; nginx IP listesi sonra da eklenebilir.
  if [ "$KC_ADMIN_DEFAULTED" = 0 ]; then
    case "$KC_ADMIN_CHOICE" in
      2) ask KEYCLOAK_ADMIN_ALLOWED_IPS "Izinli IP/CIDR'ler (virgulle, orn. 203.0.113.10,10.0.0.0/8): " ;;
      3) ask KEYCLOAK_ADMIN_ALLOWED_IPS "Ek olarak nginx'te izinli IP/CIDR'ler (bos = yalnizca firewall): " ;;
    esac
  fi
fi
case "${KC_ADMIN_CHOICE:-1}" in
  2) KEYCLOAK_ADMIN_MODE=ip
     [ -n "$KEYCLOAK_ADMIN_ALLOWED_IPS" ] || { warn "IP verilmedi; panel herkese acik birakiliyor."; KEYCLOAK_ADMIN_MODE=open; } ;;
  3) KEYCLOAK_ADMIN_MODE=port ;;
  *) KEYCLOAK_ADMIN_MODE=open ;;
esac

# Yonetici e-postasi: Let's Encrypt bildirimleri, sistem uyarilari (Alertmanager) ve
# SMTP deneme e-postasi. Ayni adres birkac kez sorulmasin diye bir kez alinir.
ADMIN_EMAIL="$OPT_EMAIL"
if [ -z "$ADMIN_EMAIL" ]; then
  echo ""
  while :; do
    ask ADMIN_EMAIL "Yonetici e-postasi (sertifika bildirimleri ve sistem uyarilari; bos gecilebilir): "
    { [ -z "$ADMIN_EMAIL" ] || is_email "$ADMIN_EMAIL"; } && break
    warn "Gecerli bir e-posta adresi girin (ya da bos birakin)."
  done
fi

# Kurulum turu (moduller) ve sirket kipi. Sonradan Platform > Bakim'dan degistirilebilir.
LITE_MODULES="timeshift,compensation,expense"
EDITION="$OPT_EDITION"
if [ -z "$EDITION" ]; then
  if [ "$ASSUME_YES" = 1 ]; then
    if [ "${mem_bytes:-0}" -gt 0 ] && [ "$mem_bytes" -lt $((7 * 1024 * 1024 * 1024)) ]; then EDITION=lite; else EDITION=full; fi
  else
    echo ""
    echo "Kurulum turu:"
    echo "  [1] Tam    - tum moduller (onerilen bellek 8 GB)"
    echo "  [2] Hafif  - cekirdek + puantaj + bordro (onerilen bellek 6 GB)"
    echo "  [3] Ozel   - modulleri tek tek secin"
    def_ed=1; if [ "${mem_bytes:-0}" -gt 0 ] && [ "$mem_bytes" -lt $((7 * 1024 * 1024 * 1024)) ]; then def_ed=2; fi
    ask ed_ans "Seciminiz [$def_ed]: "
    case "${ed_ans:-$def_ed}" in 2) EDITION=lite ;; 3) EDITION=custom ;; *) EDITION=full ;; esac
  fi
fi
case "$EDITION" in
  full) MODULES_SEL=all ;;
  lite) MODULES_SEL="$LITE_MODULES" ;;
  custom)
    MODULES_SEL="$OPT_MODULES"
    if [ -z "$OPT_MODULES" ] && [ "$ASSUME_YES" != 1 ]; then
      MODULES_SEL=""
      echo "Cekirdek her zaman kurulur: calisanlar, organizasyon, izin, onaylar, bildirimler, belgeler, KVKK."
      for _m in "timeshift|Puantaj ve vardiya|E" "compensation|Ucret ve bordro|E" "expense|Masraf ve seyahat ekranlari|E" \
                "recruitment|Ise alim|H" "onboarding|Ise alistirma ve demirbas|H" "performance|Performans|H" \
                "learning|Egitim|H" "ml|Yapay zeka (ek ~1,8 GB bellek)|H"; do
        IFS='|' read -r _id _ad _def <<< "$_m"
        ask _a "  ${_ad} kurulsun mu? [$( [ "$_def" = E ] && echo E/h || echo e/H )]: "
        _a="${_a:-$_def}"
        [[ "$_a" =~ ^[eEyY] ]] && MODULES_SEL="${MODULES_SEL:+$MODULES_SEL,}$_id"
      done
    fi ;;
esac
TENANCY="$OPT_TENANCY"
if [ -z "$TENANCY" ]; then
  if [ "$ASSUME_YES" = 1 ]; then
    # --demo ayri bir "demo" sirketi demektir: kip verilmediyse cok sirket.
    if [ "$EDITION" = full ] || [ "$OPT_DEMO" = true ]; then TENANCY=multi; else TENANCY=single; fi
  else
    echo ""
    echo "Sirket kipi:"
    echo "  [1] Tek sirket  - bu kurulumu yalnizca kendi sirketiniz kullanir (onerilen)"
    echo "  [2] Cok sirket  - birden cok sirket (barindirma, holding, muhasebe/bordro burosu)"
    ask ten_ans "Seciminiz [1]: "
    case "${ten_ans:-1}" in 2) TENANCY=multi ;; *) TENANCY=single ;; esac
  fi
fi
COMPANY_NAME="$OPT_COMPANY"
if [ "$TENANCY" = single ]; then
  # Tek sirkette herkese acik sirket kaydi anlamsiz: kapali.
  OPT_SIGNUP=false
  _tries=0
  # Girdi biterse (EOF) ya da 3 gecersiz denemeden sonra varsayilan ad kullanilir; sonsuz dongu olmaz.
  while [ -z "$COMPANY_NAME" ] && [ "$ASSUME_YES" != 1 ] && [ "$_tries" -lt 3 ]; do
    _tries=$((_tries + 1))
    ask COMPANY_NAME "Sirketinizin adi: "
    if [ -n "$COMPANY_NAME" ] && ! [[ "$COMPANY_NAME" =~ ^[^\'\"\`\$]{2,80}$ ]]; then
      warn "2-80 karakter girin (tirnak ve \$ olmadan)."; COMPANY_NAME=""
    fi
  done
  COMPANY_NAME="${COMPANY_NAME:-Sirketim}"
  # Tek sirkette demo sirketi olmaz (kipi --yes ya da Hafif secimi belirlediyse de).
  [ "$OPT_DEMO" = true ] && demo_single_err
  OPT_DEMO=false
fi
info "Kurulum: $( case "$EDITION" in full) echo Tam ;; lite) echo Hafif ;; *) echo Ozel ;; esac ) (moduller: ${MODULES_SEL:-yalniz cekirdek}), $( [ "$TENANCY" = single ] && echo "tek sirket: $COMPANY_NAME" || echo "cok sirket" )"

# Demo verisi ve herkese acik sirket kaydi. Uretimde demo onerilmez (varsayilan: Hayir).
HR360_DEMO="${OPT_DEMO}"
if [ -z "$HR360_DEMO" ]; then
  echo ""
  echo "Demo verisi: ornek \"demo\" sirketi ve demo.admin kullanicisi (deneme/sunum icin)."
  ask demo_ans "Demo verisi kurulsun mu? Uretim kurulumunda Hayir deyin. [e/H]: "
  if [[ "${demo_ans:-}" =~ ^[eEyY] ]]; then HR360_DEMO=true; else HR360_DEMO=false; fi
fi
PUBLIC_SIGNUP_ENABLED="${OPT_SIGNUP}"
if [ -z "$PUBLIC_SIGNUP_ENABLED" ]; then
  ask signup_ans "Herkese acik sirket kaydi (giris ekraninda \"Sirketinizi kaydedin\") acik olsun mu? [E/h]: "
  if [[ "${signup_ans:-}" =~ ^[hHnN] ]]; then PUBLIC_SIGNUP_ENABLED=false; else PUBLIC_SIGNUP_ENABLED=true; fi
fi

# HTTPS kurulumun sonunda scripts/tls.sh ile otomatik acilir.
if [ "$IS_DOMAIN" = 1 ]; then
  TLS_DEFAULT=1   # gercek alan adi -> Let's Encrypt
else
  TLS_DEFAULT=4   # localhost / IP -> HTTPS yok
fi
case "$OPT_TLS" in
  letsencrypt) TLS_CHOICE=1 ;;
  cert) TLS_CHOICE=2 ;;
  self-signed) TLS_CHOICE=3 ;;
  none) TLS_CHOICE=4 ;;
  *)
    if [ -n "$OPT_CERT" ]; then TLS_CHOICE=2
    elif [ -n "$OPT_URL" ] || [ "$ASSUME_YES" = 1 ]; then TLS_CHOICE=$TLS_DEFAULT
    else
      echo ""
      echo "HTTPS (adres: ${PUBLIC_HOST}):"
      echo "  1) Let's Encrypt ile otomatik, ucretsiz sertifika + otomatik yenileme"
      echo "     (DNS kaydi otomatik denetlenir; hazir degilse gecici sertifikayla"
      echo "      acilir ve hazir oldugunda kendiliginden gercek sertifikaya gecilir)"
      echo "  2) Kendi sertifikam var (fullchain + private key dosya yollari)"
      echo "  3) Kendinden imzali sertifika (yalnizca test - tarayici uyari verir)"
      echo "  4) HTTPS yok (yalnizca HTTP; yerel/deneme kurulumu)"
      ask TLS_CHOICE "Seciminiz [${TLS_DEFAULT}]: "
      TLS_CHOICE=${TLS_CHOICE:-$TLS_DEFAULT}
    fi ;;
esac
TLS_MODE=none; TLS_CERT=""; TLS_KEY=""
case "$TLS_CHOICE" in
  1)
    if [ "$IS_DOMAIN" != 1 ]; then
      warn "Let's Encrypt gercek bir alan adi ister (${PUBLIC_HOST} olmaz); HTTPS kapali birakiliyor."
    elif [ "${GATEWAY_PORT}" != 80 ]; then
      warn "Let's Encrypt dogrulamasi 80. porttan yapilir (gateway portu: ${GATEWAY_PORT}); HTTPS kapali birakiliyor."
    else
      TLS_MODE=letsencrypt
    fi ;;
  2)
    TLS_CERT="$OPT_CERT"; TLS_KEY="$OPT_KEY"
    [ -n "$TLS_CERT" ] || ask TLS_CERT "Sertifika zinciri dosyasi (fullchain.pem) yolu: "
    [ -n "$TLS_KEY" ] || ask TLS_KEY "Ozel anahtar dosyasi (privkey.pem) yolu: "
    if [ -f "$TLS_CERT" ] && [ -f "$TLS_KEY" ]; then
      TLS_MODE=certificate; TLS_CERT="$(cd "$(dirname "$TLS_CERT")" && pwd)/$(basename "$TLS_CERT")"
      TLS_KEY="$(cd "$(dirname "$TLS_KEY")" && pwd)/$(basename "$TLS_KEY")"
    else
      warn "Sertifika/anahtar dosyasi bulunamadi; HTTPS kapali birakiliyor (sonra: scripts/tls.sh enable --cert ... --key ...)."
    fi ;;
  3) TLS_MODE=self-signed ;;
  *) TLS_MODE=none ;;
esac
if [ "$TLS_MODE" = none ] && [[ "${PUBLIC_URL}" == https://* ]]; then
  # HTTPS acilmayacaksa adres http olmali; aksi halde giris yonlendirmeleri kirilir.
  PUBLIC_URL="http://${PUBLIC_URL#https://}"
  warn "HTTPS acilmayacagi icin adres ${PUBLIC_URL} olarak kullanilacak."
fi

# Disariya acik kurulumda portlari baska bir web sunucusunun tutmadigi denetlenir. Sunucunun
# kendi guvenlik duvarina (ufw/firewalld) HICBIR ZAMAN dokunulmaz; erisim izni dis guvenlik
# duvarinda (bulut guvenlik grubu, vCloud Edge Gateway) verilir. Yerel duvar portu
# engelliyorsa yalnizca uyarilir.
if [ "$OS_KERNEL" = Linux ] && [ "$IS_WSL" = 0 ] && [ "$PUBLIC_HOST" != localhost ] && [[ ! "$PUBLIC_HOST" =~ ^127\. ]]; then
  fw_ports=("$GATEWAY_PORT"); [ "$TLS_MODE" != none ] && fw_ports+=(443)
  warn_host_firewall "${fw_ports[@]}"
  for p in "${fw_ports[@]}"; do
    if owner="$(port_owner "$p")"; then
      warn "Port $p su anda '$owner' tarafindan kullaniliyor; HR360 bu portu acamaz."
      warn "Durdurun (orn. sudo systemctl disable --now $owner) ve kurulumu surdurun."
    fi
  done
fi

# Let's Encrypt on kontrolu: DNS kaydi bu sunucuyu gosteriyor mu? Sertifika kurulumun
# sonunda istenir (build birkac dakika surer); kayit o zamana kadar duzelmezse gecici
# sertifikayla acilir ve saatte bir yeniden denenir.
if [ "$TLS_MODE" = letsencrypt ]; then
  dns_rc=0; dns_points_here "$PUBLIC_HOST" || dns_rc=$?
  case "$dns_rc" in
    0) info "DNS: ${DNS_CHECK_MSG}" ;;
    1) warn "DNS: ${DNS_CHECK_MSG}"
       warn "Kurulum surerken kaydi ekleyebilirsiniz; sonunda yeniden denetlenir. Hazir degilse gecici"
       warn "sertifikayla HTTPS acilir ve Let's Encrypt saatte bir kendiliginden yeniden denenir." ;;
    *) warn "DNS denetlenemedi (${DNS_CHECK_MSG}); sertifika kurulum sonunda yine de denenecek." ;;
  esac
  warn "Dis guvenlik duvarinda (bulut guvenlik grubu, vCloud Edge Gateway firewall/NAT) 80 ve 443/tcp"
  warn "disaridan gelen trafige acik olmali; betik bunu ayarlamaz."
fi

# E-posta (SMTP) -------------------------------------------------------------
use_mailpit() {
  SMTP_HOST=mailpit; SMTP_PORT=1025; SMTP_USER=hr360; SMTP_PASSWORD=hr360
  SMTP_FROM_ADDRESS=noreply@hr360.local; SMTP_FROM_NAME=HR360
  SMTP_AUTH=false; SMTP_SSL=false; SMTP_STARTTLS=false; SMTP_STATUS="Mailpit (test kutusu; gercek gonderim yok)"
  warn "Mailpit secildi: kullanicilara e-posta ulasmayacak (test modu)."
  warn "Gercek SMTP sunucusunu sonradan eklemek icin: scripts/smtp.sh set"
}

# Gonderen adresten sunucu/port bulunur, eksikler sorulur, deneme e-postasiyla dogrulanir.
configure_smtp() {
  local g_host g_port g_kind hint to choice
  read -r g_host g_port g_kind <<< "$(smtp_guess "$SMTP_FROM_ADDRESS")"
  while :; do
    SMTP_HOST="$OPT_SMTP_HOST"
    if [ -z "$SMTP_HOST" ]; then
      if [ "$g_kind" != bilinmiyor ]; then info "E-posta sunucusu bulundu: ${g_host}:${g_port} (${g_kind})"
      else info "E-posta saglayicisi taninamadi; tahmin: ${g_host}"; fi
      ask SMTP_HOST "SMTP sunucusu [${g_host}]: "
      SMTP_HOST="${SMTP_HOST:-$g_host}"
    fi
    SMTP_PORT="$OPT_SMTP_PORT"
    if [ -z "$SMTP_PORT" ]; then
      local def_port=587; [ "$SMTP_HOST" = "$g_host" ] && def_port="$g_port"
      ask SMTP_PORT "SMTP portu (587 = STARTTLS, 465 = SSL) [${def_port}]: "
      SMTP_PORT="${SMTP_PORT:-$def_port}"
    fi
    [[ "$SMTP_PORT" =~ ^[0-9]+$ ]] || { warn "Gecersiz port: $SMTP_PORT"; SMTP_PORT=587; }
    SMTP_USER="$OPT_SMTP_USER"
    [ -n "$SMTP_USER" ] || ask SMTP_USER "Kullanici adi [${SMTP_FROM_ADDRESS}] ('-' = kimlik dogrulamasi yok): "
    SMTP_USER="${SMTP_USER:-$SMTP_FROM_ADDRESS}"; [ "$SMTP_USER" = "-" ] && SMTP_USER=""
    SMTP_PASSWORD="$OPT_SMTP_PASSWORD"
    if [ -n "$SMTP_USER" ] && [ -z "$SMTP_PASSWORD" ]; then
      hint="$(smtp_hint "$g_kind")"; [ "$SMTP_HOST" = "$g_host" ] && [ -n "$hint" ] && echo "   Not: $hint"
      read -r -s -p "SMTP parolasi: " SMTP_PASSWORD || true; echo ""
    fi
    SMTP_FROM_NAME="$OPT_SMTP_NAME"
    [ -n "$SMTP_FROM_NAME" ] || ask SMTP_FROM_NAME "Gonderen adi [HR360]: "
    SMTP_FROM_NAME=${SMTP_FROM_NAME:-HR360}
    SMTP_AUTH=$([ -n "${SMTP_USER}" ] && echo true || echo false)
    if [ "${SMTP_PORT}" = "465" ]; then SMTP_SSL=true; SMTP_STARTTLS=false; else SMTP_SSL=false; SMTP_STARTTLS=true; fi
    local v bad=0
    for v in "${SMTP_HOST}" "${SMTP_USER}" "${SMTP_PASSWORD}" "${SMTP_FROM_ADDRESS}" "${SMTP_FROM_NAME}"; do
      case "$v" in *"'"*|*'$'*|*$'\n'*) bad=1 ;; esac
    done
    if [ "$bad" = 1 ]; then
      warn "SMTP bilgilerinde tek tirnak ('), \$ ve satir sonu kullanilamaz."
      [ "$ASSUME_YES" = 1 ] && exit 2
      OPT_SMTP_PASSWORD=""; continue
    fi
    SMTP_STATUS="${SMTP_HOST}:${SMTP_PORT} (dogrulanmadi)"
    [ "$OPT_SMTP_TEST" = 1 ] || return 0

    to="${ADMIN_EMAIL:-$SMTP_FROM_ADDRESS}"
    info "Deneme e-postasi gonderiliyor: ${to}"
    if smtp_send_test "$SMTP_HOST" "$SMTP_PORT" "$SMTP_USER" "$SMTP_PASSWORD" "$SMTP_FROM_ADDRESS" "$SMTP_FROM_NAME" \
         "$to" "HR360 kurulumu: e-posta ayari calisiyor" \
         "Bu e-posta HR360 kurulumu sirasinda SMTP ayarini dogrulamak icin gonderildi. This is an HR360 setup test email."; then
      info "SMTP calisiyor: deneme e-postasi ${to} adresine gonderildi."
      SMTP_STATUS="${SMTP_HOST}:${SMTP_PORT} (deneme e-postasi gonderildi: ${to})"
      return 0
    fi
    warn "SMTP denemesi basarisiz: ${SMTP_TEST_ERR}"
    if [ "$ASSUME_YES" = 1 ]; then
      warn "Ayar yine de kaydediliyor; duzeltmek icin: scripts/smtp.sh set, denemek icin: scripts/smtp.sh test ${to}"
      SMTP_STATUS="${SMTP_HOST}:${SMTP_PORT} (DENEME BASARISIZ: ${SMTP_TEST_ERR})"
      return 0
    fi
    echo "  1) Bilgileri yeniden gir (varsayilan)"
    echo "  2) Bu ayarla devam et (sonra: scripts/smtp.sh test ${to})"
    echo "  3) Simdilik Mailpit (test kutusu) kullan"
    read -r -p "Seciminiz [1]: " choice || true
    case "${choice:-1}" in
      2) SMTP_STATUS="${SMTP_HOST}:${SMTP_PORT} (DENEME BASARISIZ: ${SMTP_TEST_ERR})"; return 0 ;;
      3) use_mailpit; return 0 ;;
      *) OPT_SMTP_HOST=""; OPT_SMTP_PORT=""; OPT_SMTP_USER=""; OPT_SMTP_PASSWORD="" ;;
    esac
  done
}

# "Sunucu kendisi gondersin" (gelismis): paketteki Postfix (compose "mailer" profili)
# alicilarin MX'lerine dogrudan teslim eder, OpenDKIM imzalar. Yalnizca alan adli kurulum.
MAIL_MODE=""; MAIL_HOSTNAME=""; MAIL_DOMAIN=""; DKIM_SELECTOR=hr360
use_self_mailer() {
  local from="$1"
  SMTP_HOST="$MAILER_HOST"; SMTP_PORT="$MAILER_PORT"; SMTP_USER=""; SMTP_PASSWORD=""
  SMTP_FROM_ADDRESS="$from"; SMTP_FROM_NAME="${OPT_SMTP_NAME:-HR360}"
  SMTP_AUTH=false; SMTP_SSL=false; SMTP_STARTTLS=false
  MAIL_MODE=self; MAIL_HOSTNAME="$PUBLIC_HOST"; MAIL_DOMAIN="${from##*@}"
  SMTP_STATUS="Sunucunun kendi posta servisi (${MAIL_HOSTNAME}; gonderen ${from}) - DNS kayitlari asagida"
}
configure_self_mailer() {
  local from="$OPT_SMTP_FROM" go
  if [ "$IS_DOMAIN" != 1 ]; then
    echo "Sunucunun kendi posta servisi yalnizca alan adli kurulumda kullanilabilir (PTR/SPF/DKIM bir alan adi ister)."
    exit 2
  fi
  if [ -z "$from" ]; then
    ask from "Gonderen adres [noreply@${PUBLIC_HOST}]: "
    from="${from:-noreply@${PUBLIC_HOST}}"
  fi
  is_email "$from" || { echo "Gecersiz gonderen adres: $from"; exit 2; }
  info "Posta servisi on denetimleri (${PUBLIC_HOST}):"
  if ! mail_self_precheck "$PUBLIC_HOST"; then
    warn "Bu denetimler gecmeden e-postalar alicilara ulasmayabilir ya da istenmeyen klasorune duser."
    if [ "${HR360_MAIL_IGNORE_CHECKS:-0}" = 1 ]; then
      warn "HR360_MAIL_IGNORE_CHECKS=1: denetimlere ragmen devam ediliyor."
    elif [ "$ASSUME_YES" = 1 ]; then
      echo "Kurulum iptal edildi (--yes). Sorunlari giderip tekrar deneyin; yine de devam etmek icin"
      echo "HR360_MAIL_IGNORE_CHECKS=1 verin ya da --mail-mode olmadan kurup e-postayi sonra ayarlayin."
      exit 2
    else
      echo "  1) Iptal et, paketteki test kutusunu (Mailpit) kullan (varsayilan)"
      echo "  2) Yine de sunucunun kendi posta servisiyle devam et"
      read -r -p "Seciminiz [1]: " go || true
      [ "${go:-1}" = 2 ] || { use_mailpit; return 0; }
    fi
  fi
  use_self_mailer "$from"
}

echo ""
echo "E-posta: davet, parola belirleme ve bildirim e-postalari bu adresten gonderilir."
MAIL_CHOICE=1
if [ "$OPT_MAIL_MODE" = self ]; then
  MAIL_CHOICE=2
elif [ "$IS_DOMAIN" = 1 ] && [ -z "$OPT_SMTP_FROM" ] && [ -z "$OPT_SMTP_HOST" ] && [ "$ASSUME_YES" != 1 ]; then
  echo "  1) E-posta hesabiniz / kurumsal SMTP sunucunuz ya da test kutusu (varsayilan)"
  echo "  2) Sunucu kendisi gondersin (gelismis: 25/tcp cikisi, PTR ve SPF/DKIM/DMARC kayitlari gerekir)"
  ask MAIL_CHOICE "Seciminiz [1]: "
  [ "${MAIL_CHOICE:-1}" = 2 ] || MAIL_CHOICE=1
fi
if [ "$MAIL_CHOICE" = 2 ]; then
  configure_self_mailer
else
  echo "Sunucu ve port adresten otomatik bulunur. Bos birakirsaniz paketteki Mailpit kullanilir:"
  echo "e-postalar GERCEKTEN GONDERILMEZ, yalnizca sunucudaki test kutusunda (http://localhost:8025) gorunur."
  SMTP_FROM_ADDRESS="$OPT_SMTP_FROM"
  if [ -z "$SMTP_FROM_ADDRESS" ] && [ "$OPT_SMTP_HOST" != mailpit ]; then
    while :; do
      ask SMTP_FROM_ADDRESS "Gonderen e-posta adresi (orn. ik@sirket.com) [mailpit]: "
      { [ -z "$SMTP_FROM_ADDRESS" ] || [ "$SMTP_FROM_ADDRESS" = mailpit ] || is_email "$SMTP_FROM_ADDRESS"; } && break
      warn "Gecerli bir e-posta adresi girin (ornek: ik@sirket.com) ya da bos birakin."
    done
  fi
  if [ -z "$SMTP_FROM_ADDRESS" ] || [ "$SMTP_FROM_ADDRESS" = mailpit ] || [ "$OPT_SMTP_HOST" = mailpit ]; then
    use_mailpit
  else
    is_email "$SMTP_FROM_ADDRESS" || { echo "Gecersiz gonderen adres: $SMTP_FROM_ADDRESS"; exit 2; }
    configure_smtp
  fi
fi

# --- Sirlar -------------------------------------------------------------
ask_secret HR360_DB_PASSWORD          "PostgreSQL (hr360admin) parolasi"
ask_secret KEYCLOAK_ADMIN_PASSWORD    "Keycloak master admin parolasi"
ask_secret_aes_key TENANT_SECRET_KEY  "tenant-service imza anahtari"
ask_secret MINIO_ROOT_PASSWORD        "MinIO root parolasi"
ask_secret ML_KEYCLOAK_CLIENT_SECRET  "ml-inference Keycloak client secret'i"
if [ "$HR360_DEMO" = true ]; then
  ask_secret DEMO_ADMIN_PASSWORD      "Demo giris kullanicisi (demo.admin) parolasi" 12
else
  DEMO_ADMIN_PASSWORD=""
fi
ask_secret PLATFORM_ADMIN_PASSWORD    "Platform yoneticisi (platform.admin) parolasi" 12
# Tek sirket kipi: gunluk yonetim hesabi "yonetici" (sirketin tenant-admin'i). Yonetici e-postasi ona verilir;
# platform.admin (acil durum/bakim) ayri adres alir: Keycloak'ta ayni e-posta iki kullanicida olamaz.
if [ "$TENANCY" = single ]; then
  ask_secret COMPANY_ADMIN_PASSWORD   "Sirket yoneticisi (yonetici) parolasi" 12
  COMPANY_ADMIN_EMAIL="${ADMIN_EMAIL:-yonetici@hr360.local}"
  PLATFORM_ADMIN_EMAIL=platform.admin@hr360.local
else
  COMPANY_ADMIN_PASSWORD=""; COMPANY_ADMIN_EMAIL=""
  PLATFORM_ADMIN_EMAIL="${ADMIN_EMAIL:-platform.admin@hr360.local}"
fi

KEYCLOAK_ADMIN_USER=admin
MINIO_ROOT_USER=hr360minio
INTERNAL_SERVICE_TOKEN="$(random_secret)$(random_secret)"
REDIS_PASSWORD="$(random_secret)$(random_secret)"
BACKUP_ENCRYPTION_KEY="$(random_secret)$(random_secret)$(random_secret)"
# tenant-service'in Keycloak yonetim API servis hesabi (hr360-tenant-admin, client_credentials).
KEYCLOAK_TENANT_ADMIN_CLIENT_SECRET="$(random_secret)$(random_secret)"

# --- 3) .env yaz ----------------------------------------------------------
# Tum sirlar bu dosyada: yalnizca sahibi okuyabilsin (varsayilan umask 664 birakir).
( umask 077; : > .env ); chmod 600 .env
cat > .env <<EOF
GATEWAY_PORT=${GATEWAY_PORT}
PUBLIC_URL=${PUBLIC_URL}

HR360_DB_PASSWORD=${HR360_DB_PASSWORD}

KEYCLOAK_ADMIN_USER=${KEYCLOAK_ADMIN_USER}
KEYCLOAK_ADMIN_PASSWORD=${KEYCLOAK_ADMIN_PASSWORD}

TENANT_SECRET_KEY=${TENANT_SECRET_KEY}

MINIO_ROOT_USER=${MINIO_ROOT_USER}
MINIO_ROOT_PASSWORD=${MINIO_ROOT_PASSWORD}

ML_KEYCLOAK_CLIENT_SECRET=${ML_KEYCLOAK_CLIENT_SECRET}

# Keycloak yonetim API servis hesabi (hr360-tenant-admin, client_credentials). tenant-service
# master yonetici parolasini kullanmaz (LEGACY degerleri bos); bkz. scripts/keycloak-service-account.sh
KEYCLOAK_TENANT_ADMIN_CLIENT_SECRET=${KEYCLOAK_TENANT_ADMIN_CLIENT_SECRET}
KEYCLOAK_TENANT_ADMIN_LEGACY_USER=
KEYCLOAK_TENANT_ADMIN_LEGACY_PASSWORD=

# Servisler arasi ic cagri anahtari (yalnizca konteynerler arasi; gateway disariya acmaz).
INTERNAL_SERVICE_TOKEN=${INTERNAL_SERVICE_TOKEN}

# Redis (Valkey) onbellegi; yalnizca ic agda.
REDIS_PASSWORD=${REDIS_PASSWORD}
# Yedek sifreleme anahtari: scripts/backup.sh arsivi AES-256 ile sifreler. Kaybolursa sifreli
# yedekler acilamaz; bu dosyanin disinda da guvenli bir yerde saklayin.
BACKUP_ENCRYPTION_KEY=${BACKUP_ENCRYPTION_KEY}

# Demo verisi (install.sh --demo): "demo" sirketi ve demo.admin. false ise yalnizca platform.admin
# olusur; ilk sirketi o olusturur. Degisken yoksa (eski kurulum) demo acik kabul edilir.
HR360_DEMO=${HR360_DEMO}
# Herkese acik sirket kaydi (giris ekranindaki "Sirketinizi kaydedin"); install.sh --no-signup = false.
PUBLIC_SIGNUP_ENABLED=${PUBLIC_SIGNUP_ENABLED}

# Sirket kipi (install.sh --tenancy): single = tek sirket (sirket kurulumda olusur, sirket listesi ve kayit
# gizli), multi = cok sirket. Tek sirketten cok sirkete Platform > Bakim'dan gecilir.
HR360_TENANCY=${TENANCY}
HR360_COMPANY_NAME='${COMPANY_NAME}'

# demo.admin parolasi (yalnizca demo verisi kuruluysa; demo sirketinin yoneticisi).
DEMO_ADMIN_PASSWORD=${DEMO_ADMIN_PASSWORD}
# Platform yoneticisi (platform.admin): tum kiracilar, sirket olusturma. Kiraciya bagli degildir.
PLATFORM_ADMIN_PASSWORD=${PLATFORM_ADMIN_PASSWORD}

# E-posta sunucusu (uygulama bildirimleri + Keycloak davet/parola e-postalari).
# SMTP_HOST=mailpit = paketteki test kutusu, gercek gonderim yapmaz.
SMTP_HOST='${SMTP_HOST}'
SMTP_PORT=${SMTP_PORT}
SMTP_USER='${SMTP_USER}'
SMTP_PASSWORD='${SMTP_PASSWORD}'
SMTP_FROM_ADDRESS='${SMTP_FROM_ADDRESS}'
SMTP_FROM_NAME='${SMTP_FROM_NAME}'

# Yonetici e-postasi; sistem uyarilari (scripts/monitoring.sh enable ile acilan
# Alertmanager) bu adrese gider. Degistirmek: scripts/monitoring.sh alerts email ...
HR360_ADMIN_EMAIL=${ADMIN_EMAIL}
ALERT_EMAIL_TO=${ADMIN_EMAIL}
EOF
if [ "$TENANCY" = single ]; then
  cat >> .env <<EOF

# Sirket yoneticisi (tek sirket kipi): gunluk yonetim hesabi "yonetici". tenant-service sirketi ilk
# acilista olusturur ve bu e-postadaki kullaniciyi sirketin yoneticisi yapar.
COMPANY_ADMIN_EMAIL=${COMPANY_ADMIN_EMAIL}
COMPANY_ADMIN_PASSWORD=${COMPANY_ADMIN_PASSWORD}
EOF
fi
if [ "$MAIL_MODE" = self ]; then
  cat >> .env <<EOF

# Sunucunun kendi posta servisi (install.sh --mail-mode=self): paketteki Postfix + DKIM.
# MAIL_HOSTNAME'in PTR kaydi bu sunucuyu gostermeli. DNS kayitlari: scripts/smtp.sh dns
MAIL_MODE=self
MAIL_HOSTNAME=${MAIL_HOSTNAME}
MAIL_DOMAIN=${MAIL_DOMAIN}
DKIM_SELECTOR=${DKIM_SELECTOR}
COMPOSE_PROFILES=mailer
EOF
fi
# Isteğe bagli moduller (HR360_MOD_* + HR360_MODULES): kapalilar derlenmez ve baslatilmaz.
( ENV_FILE=.env; . scripts/lib/modules.sh; mod_apply_selection "${MODULES_SEL:-none}" )
# Kapali modullerin servisleri compose'dan cikarilir (compose.modules.yml + COMPOSE_FILE): imajlari hic derlenmez.
scripts/modules.sh sync >/dev/null
info ".env yazildi."

# --- 4) Keycloak realm sablonunu doldur ------------------------------------
# GUVENLIK: hr360-web istemcisinin yonlendirme adresleri onceden "*" idi - Keycloak
# giris kodunu HERHANGI bir siteye (orn. https://evil.example/cb) gonderiyordu
# (canli dogrulandi). Artik yalnizca uygulamanin kendi kokeni kabul edilir.
if [[ "${PUBLIC_URL%/}" =~ ://[^/]+:[0-9]+$ ]] || [ -z "${GATEWAY_PORT}" ] \
   || [ "${GATEWAY_PORT}" = "80" ] || [ "${GATEWAY_PORT}" = "443" ]; then
  # PUBLIC_URL zaten port iceriyorsa (orn. http://sunucu:8080) tekrar eklenmez.
  PUBLIC_ORIGIN="${PUBLIC_URL%/}"
else
  PUBLIC_ORIGIN="${PUBLIC_URL%/}:${GATEWAY_PORT}"
fi
warn "Giris yalnizca su adresten calisir: ${PUBLIC_ORIGIN}  (tarayicida TAM olarak bu adresi kullanin;"
warn "baska bir ad/IP ile erisilecekse PUBLIC_URL'i ona gore verin - Keycloak diger adreslere yonlendirmez)."
# NOT: Realm'de e-posta sunucusu tanimli degildi - yeni sirket kaydinda ve davette
# "parola belirleme baglantisi gonderildi" deniyor ama e-posta HIC gitmiyordu
# (Keycloak: "Failed to send execute actions email"). Uygulamanin kendi SMTP
# ayarlariyla (varsayilan: paketteki Mailpit) ayni sunucu kullanilir.
# Yer tutucular sed yerine bash ile, degerler JSON'a kacirilarak doldurulur: kullanicinin
# girdigi SMTP parolasindaki / & " \ gibi karakterler sablonu bozmasin.
json_escape() { local v="$1"; v="${v//\\/\\\\}"; v="${v//\"/\\\"}"; printf '%s' "$v"; }
shopt -u patsub_replacement 2>/dev/null || true
realm="$(<deploy/keycloak/realm-export.template.json)"
for pair in \
  "__ML_KEYCLOAK_CLIENT_SECRET__=${ML_KEYCLOAK_CLIENT_SECRET}" \
  "__TENANT_ADMIN_CLIENT_SECRET__=${KEYCLOAK_TENANT_ADMIN_CLIENT_SECRET}" \
  "__DEMO_ADMIN_PASSWORD__=${DEMO_ADMIN_PASSWORD}" \
  "__PLATFORM_ADMIN_PASSWORD__=${PLATFORM_ADMIN_PASSWORD}" \
  "__PLATFORM_ADMIN_EMAIL__=${PLATFORM_ADMIN_EMAIL}" \
  "__COMPANY_ADMIN_PASSWORD__=${COMPANY_ADMIN_PASSWORD}" \
  "__COMPANY_ADMIN_EMAIL__=${COMPANY_ADMIN_EMAIL}" \
  "__PUBLIC_ORIGIN__=${PUBLIC_ORIGIN}" \
  "__SMTP_HOST__=${SMTP_HOST}" \
  "__SMTP_PORT__=${SMTP_PORT}" \
  "__SMTP_FROM_ADDRESS__=${SMTP_FROM_ADDRESS}" \
  "__SMTP_FROM_NAME__=${SMTP_FROM_NAME}" \
  "__SMTP_AUTH__=${SMTP_AUTH}" \
  "__SMTP_USER__=${SMTP_USER}" \
  "__SMTP_PASSWORD__=${SMTP_PASSWORD}" \
  "__SMTP_SSL__=${SMTP_SSL}" \
  "__SMTP_STARTTLS__=${SMTP_STARTTLS}"; do
  key="${pair%%=*}"; val="$(json_escape "${pair#*=}")"
  realm="${realm//"$key"/$val}"
done
# realm_drop_user KULLANICI: kullanicinin blogunu realm'den cikarir (platform.admin son blok, hep kalir).
realm_drop_user() {
  realm="$(printf '%s\n' "$realm" | awk -v u="\"username\": \"$1\"" '
    /^    \{$/ { buf = $0; inb = 1; next }
    inb { buf = buf "\n" $0
          if ($0 ~ /^    \},?$/) { inb = 0; if (index(buf, u) == 0) print buf }
          next }
    { print }')"
  if printf '%s' "$realm" | grep -qF "\"username\": \"$1\""; then echo "$1 realm'den cikarilamadi."; exit 1; fi
}
# Demo verisi istenmediyse demo.admin, cok sirket kipinde sirket yoneticisi (yonetici) cikarilir.
if [ "$HR360_DEMO" != true ]; then realm_drop_user demo.admin; fi
if [ "$TENANCY" != single ]; then realm_drop_user yonetici; fi
printf '%s\n' "$realm" > deploy/keycloak/realm-export.json
if grep -q "__[A-Z_]*__" deploy/keycloak/realm-export.json; then
  echo "Realm sablonunda doldurulmamis alan kaldi."; exit 1
fi
info "Keycloak realm sablonu dolduruldu."

# Keycloak'in genel adresi (KC_HOSTNAME) bu kokenden uretilir.
if grep -q "^PUBLIC_ORIGIN=" .env; then
  sed_i "s#^PUBLIC_ORIGIN=.*#PUBLIC_ORIGIN=${PUBLIC_ORIGIN}#" .env
else
  echo "PUBLIC_ORIGIN=${PUBLIC_ORIGIN}" >> .env
fi

# --- 5) Ayaga kaldir --------------------------------------------------------
# Temel sema (data/migrations/sql-all-schemas.sql) bos veritabaninda init betigi olarak yuklenir; ustune
# scripts/sql gocleri (idempotent) servisler baslamadan uygulanir. Boylece temel semaya eklenmesi unutulan
# bir goc yuzunden yeni kurulumda tablo/kolon eksik kalmaz (1.1.0-1.1.1'de oldu).
info "Veritabani hazirlaniyor..."
docker compose up -d postgres
pg_tries=0
# TCP ile sor: ilk acilista init betikleri yalnizca Unix soketini dinleyen gecici sunucuda calisir; soketten
# "hazir" cevabi init bitmeden gelebilir.
until docker compose exec -T postgres pg_isready -h 127.0.0.1 -U hr360admin -d hr360_operational >/dev/null 2>&1; do
  pg_tries=$((pg_tries+1))
  if [ "$pg_tries" -gt 90 ]; then
    echo "PostgreSQL 3 dakikada hazir olmadi; 'docker compose logs postgres' ile kontrol edin."
    exit 1
  fi
  sleep 2
done
scripts/migrate.sh >/dev/null || { echo "Goc betikleri uygulanamadi (scripts/migrate.sh)"; exit 1; }

info "Imajlar build ediliyor ve servisler baslatiliyor (ilk calistirmada birkac dakika surebilir)..."
# Yalnizca acik modullerin imajlari derlenir (scripts/lib/modules.sh).
# Hazir imaj (scripts/lib/images.sh): CI'nin bu commit icin yayinladigi imzali imajlar indirilir;
# yoksa ya da imza dogrulanamazsa sunucuda derlenir.
build_targets="$( ENV_FILE=.env; . scripts/lib/modules.sh; mod_build_targets )"
# shellcheck source=scripts/lib/images.sh
. scripts/lib/images.sh; images_use_tag
# shellcheck disable=SC2086
images_pull $build_targets || docker compose build $build_targets
docker compose up -d
scripts/modules.sh sync >/dev/null 2>&1 || true

info "PostgreSQL'in hazir olmasi bekleniyor..."
pg_tries=0
until docker compose exec -T postgres pg_isready -U hr360admin -d hr360_operational >/dev/null 2>&1; do
  pg_tries=$((pg_tries+1))
  if [ "$pg_tries" -gt 90 ]; then
    echo "PostgreSQL 3 dakikada hazir olmadi; 'docker compose logs postgres' ile kontrol edin."
    exit 1
  fi
  sleep 2
done

info "Keycloak'in hazir olmasi bekleniyor..."
tries=0
until curl -sf "http://localhost:${GATEWAY_PORT}/auth/realms/hr360" >/dev/null 2>&1; do
  tries=$((tries+1))
  if [ "$tries" -gt 60 ]; then
    warn "Keycloak beklenen surede ayaga kalkmadi. 'docker compose logs keycloak' ile kontrol edin."
    break
  fi
  sleep 3
done

# Servis hesabi, parola politikasi ve giris olaylari realm sablonunda var; Keycloak veritabani
# onceki bir kurulumdan kaldiysa (realm iceri alinmadan atlanir) burada uygulanir. Panel erisimi
# ayari (asagida) servis hesabini gorunce master realm'de kaba kuvvet korumasini acar.
scripts/keycloak-service-account.sh apply \
  || warn "Keycloak servis hesabi ayarlanamadi; sonra scripts/keycloak-service-account.sh ile deneyin."

info "Keycloak yonetim paneli erisimi ayarlaniyor (${KEYCLOAK_ADMIN_MODE})..."
if [ "$KEYCLOAK_ADMIN_MODE" = "open" ]; then
  scripts/keycloak-admin-access.sh open || warn "Panel erisimi ayarlanamadi; sonra scripts/keycloak-admin-access.sh ile deneyin."
else
  scripts/keycloak-admin-access.sh "$KEYCLOAK_ADMIN_MODE" "$KEYCLOAK_ADMIN_ALLOWED_IPS" \
    || warn "Panel erisimi ayarlanamadi; sonra scripts/keycloak-admin-access.sh ile deneyin."
fi

# Realm ilk kurulumda temayla birlikte olusur; Keycloak veritabani onceki bir
# kurulumdan kaldiysa (realm iceri alinmadan atlanir) tema burada uygulanir.
scripts/keycloak-theme.sh >/dev/null 2>&1 \
  || warn "Giris ekrani temasi uygulanamadi; sonra scripts/keycloak-theme.sh ile deneyin."

if [ "$TLS_MODE" != none ]; then
  info "HTTPS aciliyor (${TLS_MODE})..."
  tls_cmd=enable
  case "$TLS_MODE" in
    letsencrypt)
      # "auto": Let's Encrypt olmazsa gecici sertifika + saatlik otomatik yeniden deneme.
      tls_cmd=auto; tls_args=(--host "$PUBLIC_HOST")
      [ -n "$ADMIN_EMAIL" ] && tls_args+=(--email "$ADMIN_EMAIL") ;;
    certificate) tls_args=(--cert "$TLS_CERT" --key "$TLS_KEY" --host "$PUBLIC_HOST") ;;
    self-signed) tls_args=(--self-signed --host "$PUBLIC_HOST") ;;
  esac
  tls_rc=0; scripts/tls.sh "$tls_cmd" "${tls_args[@]}" || tls_rc=$?
  if [ "$tls_rc" = 3 ]; then
    warn "HTTPS gecici (kendinden imzali) sertifikayla acildi; Let's Encrypt saatte bir yeniden denenecek."
    warn "Sorunu gormek icin: scripts/tls.sh check   -   hemen denemek icin: scripts/tls.sh retry"
  elif [ "$tls_rc" != 0 ]; then
    warn "HTTPS acilamadi; uygulama HTTP ile calisacak sekilde ayarlaniyor."
    # tls.sh kendi degisikliklerini geri alir; yine de durum tutarli olsun diye
    # adres ve Keycloak ayarlari acikca HTTP'ye cekilir.
    scripts/tls.sh disable >/dev/null 2>&1 || warn "HTTP'ye donus de basarisiz; 'scripts/tls.sh status' ile kontrol edin."
    warn "Sorunu giderdikten sonra tekrar deneyin: scripts/tls.sh ${tls_cmd} ${tls_args[*]}"
  fi
else
  # HTTPS yok: onceki bir kurulumdan kalmis TLS ayarlari temizlenir ve Keycloak'in
  # yonetim realm'i de duz HTTP'ye izin verecek sekilde ayarlanir (aksi halde
  # genel IP'den yonetim paneli "HTTPS required" der).
  info "HTTPS kapali; adres ve Keycloak ayarlari HTTP icin duzenleniyor..."
  scripts/tls.sh disable >/dev/null 2>&1 || warn "Ayar yapilamadi; 'scripts/tls.sh disable' ile tekrar deneyin."
fi
PUBLIC_ORIGIN="$(grep '^PUBLIC_ORIGIN=' .env | tail -1 | cut -d= -f2-)"

# Veritabani yalitimi (varsayilan): servis basina en az yetkili rol + kiraci basina satir duzeyi guvenlik.
# Kapatmak: .env'e HR360_DB_ISOLATION=off (ya da scripts/db-isolation.sh off).
if scripts/db-isolation.sh ensure; then docker compose up -d >/dev/null 2>&1 || true
else warn "Veritabani yalitimi acilamadi; sonra: scripts/db-isolation.sh ensure && docker compose up -d"; fi

# Bakim calistiricisi: yedek, dogrulama, guncelleme ve geri yukleme bundan sonra Platform > Bakim
# ekranindan yapilir (kullanicinin crontab'ina dakikada bir calisan tek satir; root gerekmez).
if command -v crontab >/dev/null 2>&1; then
  scripts/ops-runner.sh install >/dev/null 2>&1 || warn "Bakim calistiricisi kurulamadi; sonra: scripts/ops-runner.sh install"
else
  warn "crontab yok; Platform > Bakim ekrani icin: scripts/ops-runner.sh install (cron kurduktan sonra)"
fi

info "Kurulum dogrulaniyor..."
verify_out="$(scripts/tls.sh verify 2>&1)" && verify_ok=1 || verify_ok=0

if [ "$MAIL_MODE" = self ]; then
  info "Posta servisinin DKIM anahtari okunuyor..."
  dkim_txt="$(mailer_dkim_txt "$DKIM_SELECTOR")"
  [ -n "$dkim_txt" ] || warn "DKIM anahtari okunamadi (docker compose logs mailer); kayitlari sonra: scripts/smtp.sh dns"
  mkdir -p "$(dirname "$MAILER_DNS_FILE")"
  mail_dns_records "$MAIL_DOMAIN" "$MAIL_HOSTNAME" "$(public_ipv4 || echo SUNUCU_IP)" "$DKIM_SELECTOR" "$dkim_txt" "$ADMIN_EMAIL" \
    > "$MAILER_DNS_FILE"
  chmod 600 "$MAILER_DNS_FILE" 2>/dev/null || true
fi

echo ""
# Eski kurulumlarda .env genis izinle yazilmis olabilir; guncellemede de daralt.
chmod 600 .env 2>/dev/null || true
echo "${GREEN}${BOLD}Kurulum tamamlandi.${RESET}"
echo "----------------------------------------------"
echo "Uygulama:         ${PUBLIC_ORIGIN}"
echo "Keycloak admin:   $(scripts/keycloak-admin-access.sh status | grep 'Konsol adresi' | awk '{print $3}' | grep . || echo "${PUBLIC_ORIGIN}/auth")/admin/  (kullanici: ${KEYCLOAK_ADMIN_USER})"
echo "                  Erisimi degistirmek icin: scripts/keycloak-admin-access.sh open|ip|port"
echo "HTTPS:            $(scripts/tls.sh status | head -1 | sed "s/^HTTPS: //")"
echo "MinIO konsolu:    http://localhost:9001   (yalnizca sunucunun kendisinden / SSH tuneliyle)"
echo "E-posta (SMTP):   ${SMTP_STATUS}"
[ -n "$ADMIN_EMAIL" ] && echo "Sistem uyarilari: ${ADMIN_EMAIL}  (izleme acildiginda: scripts/monitoring.sh enable)"
[ "$SMTP_HOST" = mailpit ] && echo "Mailpit (e-posta):http://localhost:8025   (yalnizca sunucunun kendisinden / SSH tuneliyle)"
if [ "$MAIL_MODE" = self ]; then
  echo ""
  echo "${BOLD}E-posta DNS kayitlari (teslim icin gerekli; ${MAILER_DNS_FILE} dosyasina da yazildi):${RESET}"
  sed 's/^/  /' "$MAILER_DNS_FILE"
  echo "  Uyari: yeni bir IP'den ilk gunlerde az sayida e-posta gonderin (isinma); Gmail/Outlook"
  echo "  itibar olusana kadar e-postalari istenmeyen klasorune atabilir."
fi
( ENV_FILE=.env; . scripts/lib/modules.sh; mod_enabled ml ) && \
  echo "MLflow:           http://localhost:5000   (yalnizca sunucunun kendisinden / SSH tuneliyle)"
echo "Kurulum turu:     $( case "$EDITION" in full) echo Tam ;; lite) echo Hafif ;; *) echo Ozel ;; esac ); acik moduller: $(grep '^HR360_MODULES=' .env | cut -d= -f2- | sed 's/^$/yalniz cekirdek/')"
echo "                  Degistirmek: Platform > Bakim > Moduller (ya da scripts/modules.sh)"
echo "Sirket kipi:      $( [ "$TENANCY" = single ] && echo "tek sirket ($COMPANY_NAME)" || echo "cok sirket" )"
echo ""
echo "Dogrulama:"
printf '%s\n' "$verify_out" | sed 's/^/  /'
[ "$verify_ok" = 1 ] || warn "Dogrulamada sorun var; ayrinti: scripts/tls.sh verify, docker compose ps"
echo ""
echo "Ilk giris:"
if [ "$TENANCY" = single ]; then
  echo "  Sirket yonetimi:   yonetici / (.env icindeki COMPANY_ADMIN_PASSWORD)  - gunluk kullanim"
  echo "  Platform yonetimi: platform.admin / (.env icindeki PLATFORM_ADMIN_PASSWORD)  - yalnizca acil durum ve bakim; paylasmayin"
else
  echo "  Platform yonetimi: platform.admin / (.env icindeki PLATFORM_ADMIN_PASSWORD)  - tum kiracilar; paylasmayin"
  if [ "$HR360_DEMO" = true ]; then
    echo "  Demo giris:        demo.admin / (.env icindeki DEMO_ADMIN_PASSWORD)  - yalnizca demo sirketinin yoneticisi"
  else
    echo "  Ilk sirket:        platform.admin ile giris yapip Platform yonetiminden sirketi olusturun"
    echo "                     (demo verisi kurulmadi; denemek icin: ./install.sh --demo ile ayri bir kurulum)."
  fi
  if [ "$PUBLIC_SIGNUP_ENABLED" = true ]; then
    echo "  Sirket kaydi:      herkese acik (${PUBLIC_ORIGIN}/kayit). Kapatmak: .env PUBLIC_SIGNUP_ENABLED=false"
  else
    echo "  Sirket kaydi:      kapali (PUBLIC_SIGNUP_ENABLED=false); sirketleri platform.admin olusturur."
  fi
fi
if [ "$KEYCLOAK_ADMIN_MODE" = port ]; then
  echo ""
  warn "Keycloak yonetim paneli ayri portta: $(scripts/keycloak-admin-access.sh status | sed -n 's/^Konsol adresi: //p')/admin/"
  warn "8090/tcp'yi dis guvenlik duvarinda (bulut guvenlik grubu, vCloud Edge) yalnizca yonetim IP'lerine acin."
  warn "Sunucunun kendi guvenlik duvarina (ufw/firewalld) bu betik dokunmaz."
elif [ "$KEYCLOAK_ADMIN_MODE" = open ]; then
  echo "  Keycloak paneli:   ana adreste acik. Dis guvenlik duvarindan kisitlamak icin:"
  echo "                     scripts/keycloak-admin-access.sh port  (panel :8090'a tasinir; 8090/tcp'yi"
  echo "                     dis guvenlik duvarinda yonetim IP'lerine acin)"
fi
echo ""
echo "Tum sirlar .env dosyasinda — bu dosyayi asla commit etmeyin (.gitignore'da zaten haric)."
echo "Loglari izlemek icin: docker compose logs -f"
echo "Durdurmak icin:       docker compose down"

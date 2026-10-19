#!/usr/bin/env bash
# install.sh ve scripts/tls.sh otomasyonunun davranis testi (Docker gerektirmez).
#
# Depo kopyasinda, docker / curl / crontab / ufw / ss / sudo yerine kayit tutan sahte
# komutlarla calisir. Denetlenenler: komut satiri secenekleri, soru sormadan (--yes) kurulum,
# alan adindan Let's Encrypt, DNS hazir degilse gecici sertifika + saatlik yeniden deneme ve
# DNS duzelince gercek sertifikaya gecis, sunucu guvenlik duvarina dokunulmamasi, gonderen adresten SMTP sunucusu
# bulma, deneme e-postasi, hatali parolada yeniden sorma, yonetici e-postasinin uyari alicisi
# olmasi, demo verisinin istege bagli olmasi (--demo), herkese acik kaydin kapatilmasi (--no-signup),
# Keycloak panelinin varsayilan olarak ana adreste (open) acilmasi, istege bagli "sunucu kendisi
# gondersin" posta kipi (--mail-mode self: yalnizca alan adinda, 25/tcp + PTR on denetimi, DNS kayitlari).
#
# Kullanim: tests/install/test_install.sh
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
FAILS=0
pass() { echo "  ok   $*"; }
fail() { echo "  FAIL $*"; FAILS=$((FAILS + 1)); }
check() { local desc="$1"; shift; if "$@"; then pass "$desc"; else fail "$desc"; fi; }

# --- Sahte komutlar ----------------------------------------------------------
BIN="$WORK/bin"; mkdir -p "$BIN"
cat > "$BIN/docker" <<'EOF'
#!/usr/bin/env bash
echo "docker $*" >> "$HR360_TEST_LOG"
case "$*" in
  "info --format {{.MemTotal}}") echo 17179869184 ;;
  *"compose config"*) echo "name: hr360test" ;;
  *"exec -T mailer cat"*)
    [ -n "${FAKE_DKIM:-}" ] && printf 'hr360._domainkey\tIN\tTXT\t( "v=DKIM1; h=sha256; k=rsa; "\n\t  "p=%s" )  ; ----- DKIM key\n' "$FAKE_DKIM" ;;
  *certonly*)
    [ "${FAKE_LE:-fail}" = ok ] || { echo "certbot: dogrulama basarisiz (sahte)" >&2; exit 1; }
    host=""; prev=""
    for a in "$@"; do [ "$prev" = -d ] && host="$a"; prev="$a"; done
    mkdir -p "deploy/letsencrypt/live/$host"
    openssl req -x509 -newkey rsa:2048 -nodes -days 90 -subj "/CN=$host" \
      -keyout "deploy/letsencrypt/live/$host/privkey.pem" -out "deploy/letsencrypt/live/$host/fullchain.pem" >/dev/null 2>&1 ;;
esac
exit 0
EOF
cat > "$BIN/curl" <<'EOF'
#!/usr/bin/env bash
args="$*"
echo "curl $args" >> "$HR360_TEST_LOG"
url=""; for a in "$@"; do case "$a" in http://*|https://*|smtp://*|smtps://*) url="$a" ;; esac; done
case "$args" in
  *--mail-rcpt*)
    case "$args" in *":${FAKE_SMTP_BAD_PASSWORD:-__yok__}"*) echo "curl: (67) Login denied" >&2; exit 67 ;; esac
    [ "${FAKE_SMTP:-ok}" = ok ] || { echo "curl: (67) Login denied" >&2; exit 67; }
    exit 0 ;;
esac
case "$url" in
  *ipify*|*icanhazip*|*ifconfig.me*) echo "$FAKE_PUBLIC_IP" ;;
  *dns-query*|*dns.google/resolve*)
    name="$(printf '%s' "$url" | sed -E 's/.*name=([^&]*).*/\1/')"; type="${url##*type=}"
    if [ "$type" = PTR ]; then
      [ -n "${FAKE_PTR:-}" ] && printf '{"Status":0,"Answer":[{"name":"%s","type":12,"TTL":60,"data":"%s."}]}' "$name" "$FAKE_PTR" || echo '{"Status":3}'
    elif [ "$type" = A ]; then
      [ -n "${FAKE_DNS_A:-}" ] && printf '{"Status":0,"Answer":[{"name":"%s","type":1,"TTL":60,"data":"%s"}]}' "$name" "$FAKE_DNS_A" || echo '{"Status":3}'
    else
      [ -n "${FAKE_MX:-}" ] && printf '{"Status":0,"Answer":[{"name":"%s","type":15,"TTL":60,"data":"10 %s."}]}' "$name" "$FAKE_MX" || echo '{"Status":0}'
    fi ;;
  *acme-challenge*) echo ok ;;
  *openid-configuration)
    origin="$(grep '^PUBLIC_ORIGIN=' .env | tail -1 | cut -d= -f2-)"
    printf '{"issuer":"%s/auth/realms/hr360"}' "$origin" ;;
  *)
    case "$args" in
      *"%{http_code} %{redirect_url}"*)
        host="$(printf '%s' "$args" | sed -nE 's/.*Host: ([^ ]+).*/\1/p')"; printf '301 https://%s/' "$host" ;;
      *"%{http_code}"*) printf 200 ;;
    esac ;;
esac
exit 0
EOF
cat > "$BIN/crontab" <<'EOF'
#!/usr/bin/env bash
f="$HR360_TEST_CRON"
# Gercek crontab gibi gecici dosyaya yazip degistirir: "crontab -l | ... | crontab -" borusunda okuma bitmeden
# dosya kesilmesin (aksi halde onceki satirlar kaybolur).
case "${1:-}" in -l) [ -s "$f" ] && cat "$f" || exit 1 ;; -) t="$(mktemp)"; cat > "$t"; mv "$t" "$f" ;; esac
EOF
cat > "$BIN/ufw" <<'EOF'
#!/usr/bin/env bash
echo "ufw $*" >> "$HR360_TEST_LOG"
if [ "${1:-}" = status ]; then
  echo "Status: active"; echo; echo "To                         Action      From"
  echo "22/tcp                     ALLOW       Anywhere"
  [ -n "${FAKE_UFW_WEB:-}" ] && { echo "80/tcp                     ALLOW       Anywhere"; echo "443                        ALLOW       Anywhere"; }
fi
exit 0
EOF
cat > "$BIN/timeout" <<'EOF'
#!/usr/bin/env bash
# tcp_probe: timeout SN bash -c '...' _ HOST PORT
if [ "${2:-}" = bash ] && [ "${7:-}" = 25 ]; then
  echo "port25 ${6}" >> "$HR360_TEST_LOG"
  [ "${FAKE_PORT25:-blocked}" = ok ]; exit
fi
shift; exec "$@"
EOF
printf '#!/usr/bin/env bash\nexit 0\n' > "$BIN/ss"
# Parolasiz sudo: komutu dogrudan calistirir (CI'da sudo kendi PATH'ini kullanmasin).
printf '#!/usr/bin/env bash\n[ "${1:-}" = -n ] && shift\nexec "$@"\n' > "$BIN/sudo"
# Zamanlayici cron'a dussun diye systemctl hep basarisiz.
printf '#!/usr/bin/env bash\nexit 1\n' > "$BIN/systemctl"
chmod +x "$BIN"/*

# Her senaryo temiz bir depo kopyasinda calisir.
new_copy() {
  local d="$WORK/repo-$1"; rm -rf "$d"; mkdir -p "$d"
  cp "$ROOT/install.sh" "$d/"
  cp -r "$ROOT/scripts" "$d/"
  mkdir -p "$d/deploy/keycloak" "$d/deploy/nginx/tls"
  cp "$ROOT/deploy/keycloak/realm-export.template.json" "$d/deploy/keycloak/"
  cp "$ROOT/VERSION" "$d/"
  : > "$WORK/log-$1"; : > "$WORK/cron-$1"
  echo "$d"
}
run_in() { # run_in AD DIZIN [env...] -- komut...
  local name="$1" dir="$2"; shift 2
  (cd "$dir" && env PATH="$BIN:$PATH" HR360_TEST_LOG="$WORK/log-$name" HR360_TEST_CRON="$WORK/cron-$name" \
     HR360_SYSTEMD_DIR="$WORK/systemd" FAKE_PUBLIC_IP=203.0.113.10 HR360_DKIM_WAIT=1 "$@")
}
envv() { grep "^$2=" "$1/.env" | tail -1 | cut -d= -f2-; }
realm_ok() { python3 -m json.tool "$1/deploy/keycloak/realm-export.json" >/dev/null 2>&1; }
has_user() { python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); sys.exit(0 if any(u.get("username")==sys.argv[2] for u in d["users"]) else 1)' "$1/deploy/keycloak/realm-export.json" "$2"; }
user_email() { python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); print(next(u.get("email","") for u in d["users"] if u.get("username")==sys.argv[2]))' "$1/deploy/keycloak/realm-export.json" "$2"; }
mkdir -p "$WORK/systemd"

echo "1) Komut satiri secenekleri"
d="$(new_copy opts)"
out="$(run_in opts "$d" ./install.sh --help 2>&1)"; rc=$?
check "--help calisir ve secenekleri listeler" eval '[ "$rc" = 0 ] && grep -q -- "--domain" <<< "$out" && grep -q -- "--smtp-from" <<< "$out"'
check "--help --demo ve --no-signup secenekleri" eval 'grep -q -- "--demo" <<< "$out" && grep -q -- "--no-signup" <<< "$out"'
check "--help --mail-mode self secenegi" grep -q -- "--mail-mode self" <<< "$out"
run_in opts "$d" ./install.sh --domain "kotu alan" --yes >/dev/null 2>&1; check "gecersiz alan adi reddedilir (2)" [ $? = 2 ]
run_in opts "$d" ./install.sh --domain hr.example.com --http-port 8080 --yes >/dev/null 2>&1; check "--domain ile 80 disi port reddedilir" [ $? = 2 ]
run_in opts "$d" ./install.sh --smtp-from ik@example.com --yes >/dev/null 2>&1; check "--yes ile parolasiz SMTP reddedilir" [ $? = 2 ]
run_in opts "$d" ./install.sh --bilinmeyen >/dev/null 2>&1; check "bilinmeyen secenek reddedilir" [ $? = 2 ]
run_in opts "$d" ./install.sh --mail-mode relay --yes >/dev/null 2>&1; check "gecersiz --mail-mode reddedilir" [ $? = 2 ]
out="$(run_in opts "$d" ./install.sh --url http://10.0.0.5 --mail-mode=self --yes 2>&1)"; rc=$?
check "IP adresli kurulumda --mail-mode=self reddedilir" eval '[ "$rc" = 2 ] && grep -q "yalnizca alan adli kurulumda" <<< "$out"'
run_in opts "$d" ./install.sh --domain hr.example.com --mail-mode self --smtp-host smtp.x.com --yes >/dev/null 2>&1
check "--mail-mode self ile --smtp-host reddedilir" [ $? = 2 ]
check "secenek hatasinda .env yazilmaz" [ ! -f "$d/.env" ]

echo "2) Alan adi + Let's Encrypt + Google Workspace SMTP (soru sormadan)"
d="$(new_copy le)"
out="$(run_in le "$d" FAKE_DNS_A=203.0.113.10 FAKE_MX=aspmx.l.google.com FAKE_LE=ok \
        ./install.sh --domain hr.example.com --email admin@example.com --smtp-from ik@example.com \
        --smtp-password 'uygulama-sifresi' --yes 2>&1)"; rc=$?
check "kurulum basarili" [ "$rc" = 0 ]
check "adres https://hr.example.com" [ "$(envv "$d" PUBLIC_ORIGIN)" = https://hr.example.com ]
check "HTTP portu 80" [ "$(envv "$d" GATEWAY_PORT)" = 80 ]
check "TLS_MODE=letsencrypt" [ "$(envv "$d" TLS_MODE)" = letsencrypt ]
check "certbot yonetici e-postasiyla cagrildi" grep -q "certonly.*-d hr.example.com.*--email admin@example.com" "$WORK/log-le"
check "ufw'ye hicbir kural eklenmedi / devreye alinmadi" eval '! grep -Eq "ufw (allow|enable|deny|reject|delete|reset|disable)" "$WORK/log-le"'
check "ufw 80/443'u engelliyor: yalnizca uyari" grep -q "ufw calisiyor ve su portlara izin yok: 80 443/tcp" <<< "$out"
check "HSTS acik (guvenilir sertifika)" grep -q Strict-Transport-Security "$d/deploy/nginx/tls/listen.conf"
check "MX'ten Google bulundu: smtp.gmail.com:587" eval '[ "$(envv "$d" SMTP_HOST)" = "'\''smtp.gmail.com'\''" ] && [ "$(envv "$d" SMTP_PORT)" = 587 ]'
check "kullanici adi = gonderen adres" [ "$(envv "$d" SMTP_USER)" = "'ik@example.com'" ]
check "deneme e-postasi STARTTLS ile yoneticiye gitti" grep -q "smtp://smtp.gmail.com:587 --ssl-reqd --user ik@example.com:uygulama-sifresi --mail-from ik@example.com --mail-rcpt admin@example.com" "$WORK/log-le"
check "uyari alicisi = yonetici e-postasi" [ "$(envv "$d" ALERT_EMAIL_TO)" = admin@example.com ]
check "realm SMTP ayari dolduruldu" grep -q '"host" *: *"smtp.gmail.com"' "$d/deploy/keycloak/realm-export.json"
sa_secret="$(envv "$d" KEYCLOAK_TENANT_ADMIN_CLIENT_SECRET)"
check "Keycloak servis hesabi anahtari uretildi" [ "${#sa_secret}" -ge 32 ]
check "realm sablonunda servis hesabi anahtari dolduruldu" grep -q "\"secret\": \"${sa_secret}\"" "$d/deploy/keycloak/realm-export.json"
check "tenant-service'e master parola verilmez" eval 'grep -q "^KEYCLOAK_TENANT_ADMIN_LEGACY_PASSWORD=$" "$d/.env"'
check "servis hesabi ve parola politikasi Keycloak'a uygulandi" eval 'grep -q "hr360-tenant-admin" "$WORK/log-le" && grep -q "passwordPolicy" "$WORK/log-le"'
check "bekleyen yeniden deneme yok" eval '[ -z "$(envv "$d" TLS_LE_PENDING)" ] && ! grep -q hr360-tls-retry "$WORK/cron-le" 2>/dev/null'
check "bakim calistiricisi cron'a eklendi (Platform > Bakim)" grep -q "# hr360-ops-runner$" "$WORK/cron-le"
check "ozet: dogrulama tamam" eval 'grep -q "HTTPS:    calisiyor, sertifika gecerli" <<< "$out" && grep -q "Giris:    tamam" <<< "$out"'
check "ozet: deneme e-postasi gonderildi" grep -q "deneme e-postasi gonderildi: admin@example.com" <<< "$out"
check "--demo yok: HR360_DEMO=false, demo parolasi bos" eval '[ "$(envv "$d" HR360_DEMO)" = false ] && [ -z "$(envv "$d" DEMO_ADMIN_PASSWORD)" ]'
check "realm gecerli JSON, demo.admin yok, platform.admin var" eval 'realm_ok "$d" && ! has_user "$d" demo.admin && has_user "$d" platform.admin'
check "platform.admin e-postasi = --email" [ "$(user_email "$d" platform.admin)" = admin@example.com ]
check "herkese acik kayit varsayilan acik" [ "$(envv "$d" PUBLIC_SIGNUP_ENABLED)" = true ]
check "alan adi + HTTPS: Keycloak paneli varsayilan ana adreste (open)" eval '[ "$(envv "$d" KEYCLOAK_ADMIN_MODE)" = open ] && [ -z "$(envv "$d" KEYCLOAK_ADMIN_URL)" ]'
check "ozet: panel adresi ve sonradan kisitlama ipucu" eval 'grep -q "https://hr.example.com/auth/admin/" <<< "$out" && grep -q "keycloak-admin-access.sh port" <<< "$out"'
check "ozet: ilk sirketi platform.admin olusturur" grep -q "Ilk sirket:" <<< "$out"
check "surum ozette" grep -q "HR360 Enterprise $(cat "$ROOT/VERSION")" <<< "$out"

echo "3) DNS hazir degil: gecici sertifika + saatlik yeniden deneme, DNS duzelince gecis"
d="$(new_copy dns)"
out="$(run_in dns "$d" FAKE_DNS_A=198.51.100.7 FAKE_LE=ok FAKE_UFW_WEB=1 \
        ./install.sh --domain hr.example.com --email admin@example.com --yes 2>&1)"; rc=$?
check "kurulum yine de tamamlandi" [ "$rc" = 0 ]
check "ufw 80/443'e izin veriyorsa uyari yok" eval '! grep -q "portlara izin yok" <<< "$out"'
check "DNS uyarisi dogru IP'yi soyluyor" grep -q "198.51.100.7; bu sunucu: 203.0.113.10" <<< "$out"
check "certbot hic cagrilmadi (limit korunur)" bash -c "! grep -q certonly '$WORK/log-dns'"
check "gecici kendinden imzali sertifikayla HTTPS acik" eval '[ "$(envv "$d" TLS_MODE)" = self-signed ] && [ -f "$d/deploy/nginx/tls/fullchain.pem" ]'
check "gecici sertifikada HSTS yok" bash -c "! grep -q Strict-Transport-Security '$d/deploy/nginx/tls/listen.conf'"
check "adres yine https" [ "$(envv "$d" PUBLIC_ORIGIN)" = https://hr.example.com ]
check "bekleyen Let's Encrypt kaydi" eval '[ "$(envv "$d" TLS_LE_PENDING)" = hr.example.com ] && [ "$(envv "$d" TLS_LE_EMAIL)" = admin@example.com ]'
check "saatlik cron gorevi kuruldu" grep -q "scripts/tls.sh retry .*# hr360-tls-retry$" "$WORK/cron-dns"
check "SMTP verilmedi -> Mailpit" [ "$(envv "$d" SMTP_HOST)" = "'mailpit'" ]
out="$(run_in dns "$d" FAKE_DNS_A=198.51.100.7 FAKE_LE=ok scripts/tls.sh retry 2>&1)"
check "DNS hala yanlis: retry certbot'u cagirmaz" grep -q "Henuz degil" <<< "$out"
out="$(run_in dns "$d" FAKE_DNS_A=203.0.113.10 FAKE_LE=fail scripts/tls.sh retry 2>&1)"
check "certbot basarisiz: gecici sertifika korunur" eval '[ "$(envv "$d" TLS_MODE)" = self-signed ] && [ -f "$d/deploy/nginx/tls/fullchain.pem" ] && [ -n "$(envv "$d" TLS_LE_PENDING)" ]'
out="$(run_in dns "$d" FAKE_DNS_A=203.0.113.10 FAKE_LE=ok scripts/tls.sh retry 2>&1)"
check "DNS duzeldi: Let's Encrypt'e gecildi" eval '[ "$(envv "$d" TLS_MODE)" = letsencrypt ] && grep -q "Let'\''s Encrypt sertifikasi alindi" <<< "$out"'
check "gecici sertifika dosyalari kaldirildi" [ ! -f "$d/deploy/nginx/tls/fullchain.pem" ]
check "bekleyen kayit ve cron gorevi temizlendi" eval '[ -z "$(envv "$d" TLS_LE_PENDING)" ] && ! grep -q hr360-tls-retry "$WORK/cron-dns"'
out="$(run_in dns "$d" scripts/tls.sh retry 2>&1)"
check "bekleyen yokken retry bir sey yapmaz" grep -q "Bekleyen Let's Encrypt denemesi yok" <<< "$out"

echo "4) SMTP denemesi basarisiz (--yes): ayar kaydedilir, ozette uyari"
d="$(new_copy smtpfail)"
out="$(run_in smtpfail "$d" FAKE_SMTP=fail ./install.sh --url http://10.0.0.5 --smtp-from ik@outlook.com \
        --smtp-password x --yes 2>&1)"; rc=$?
check "kurulum tamamlandi" [ "$rc" = 0 ]
check "Outlook sunucusu bulundu" [ "$(envv "$d" SMTP_HOST)" = "'smtp-mail.outlook.com'" ]
check "hata nedeni gosterildi" grep -q "kullanici adi / parola" <<< "$out"
check "ozette DENEME BASARISIZ" grep -q "E-posta (SMTP):   smtp-mail.outlook.com:587 (DENEME BASARISIZ" <<< "$out"
check "IP adresiyle HTTPS kapali, adres http" eval '[ "$(envv "$d" PUBLIC_ORIGIN)" = http://10.0.0.5 ] && [ -z "$(envv "$d" TLS_MODE)" ]'
check "yonetici e-postasi verilmedi -> uyari alicisi bos" [ -z "$(envv "$d" ALERT_EMAIL_TO)" ]
check "IP adresi: Keycloak paneli ana adreste (open)" [ "$(envv "$d" KEYCLOAK_ADMIN_MODE)" = open ]
check "e-posta yok: platform.admin varsayilan adresi" [ "$(user_email "$d" platform.admin)" = platform.admin@hr360.local ]

echo "5) Sorulu kurulum: yanlis parola -> yeniden sorulur -> dogru parola"
d="$(new_copy ask)"
# Sira: adres, Keycloak paneli (Enter = alan adinda ayri port), yonetici e-postasi, kurulum turu (1 = Tam),
# sirket kipi (2 = cok sirket), demo (e), herkese acik kayit (h), HTTPS, e-posta kipi (Enter = 1), gonderen adres...
answers=$'hr.example.com\n\nadmin@example.com\n1\n2\ne\nh\n\n\nik@sirket.com.tr\n\n\n\nyanlis\n\n1\n\n\n\ndogru\nIK Ekibi\n\n\n\n\n\n\n\n'
out="$(printf '%s' "$answers" | run_in ask "$d" FAKE_DNS_A=203.0.113.10 FAKE_LE=ok FAKE_MX=sirket-com-tr.mail.protection.outlook.com \
        FAKE_SMTP_BAD_PASSWORD=yanlis ./install.sh 2>&1)"; rc=$?
check "kurulum basarili" [ "$rc" = 0 ]
check "alan adi girilince port sorulmadi, Let's Encrypt secildi" eval '[ "$(envv "$d" GATEWAY_PORT)" = 80 ] && [ "$(envv "$d" TLS_MODE)" = letsencrypt ]'
check "MX'ten Microsoft 365 bulundu" grep -q "smtp.office365.com:587 (microsoft365)" <<< "$out"
check "Microsoft 365 ipucu gosterildi" grep -q "SMTP AUTH" <<< "$out"
check "ilk deneme hatali parolayla basarisiz" grep -q "SMTP denemesi basarisiz" <<< "$out"
check "ikinci denemede dogru parola kaydedildi" eval '[ "$(envv "$d" SMTP_PASSWORD)" = "'\''dogru'\''" ] && [ "$(envv "$d" SMTP_FROM_NAME)" = "'\''IK Ekibi'\''" ]'
check "iki deneme e-postasi denendi" [ "$(grep -c -- '--mail-rcpt admin@example.com' "$WORK/log-ask")" = 2 ]
check "soruda demo = evet: HR360_DEMO=true, demo.admin realm'de" eval '[ "$(envv "$d" HR360_DEMO)" = true ] && [ -n "$(envv "$d" DEMO_ADMIN_PASSWORD)" ] && realm_ok "$d" && has_user "$d" demo.admin'
check "soruda kayit = hayir: PUBLIC_SIGNUP_ENABLED=false" [ "$(envv "$d" PUBLIC_SIGNUP_ENABLED)" = false ]
check "Enter: panel ana adreste, ek IP sorulmadi" eval '[ "$(envv "$d" KEYCLOAK_ADMIN_MODE)" = open ] && [ -z "$(envv "$d" KEYCLOAK_ADMIN_ALLOWED_IPS)" ]'

echo "6) Gonderen adresten sunucu tahmini"
guess() { (cd "$ROOT" && PATH="$BIN:$PATH" HR360_TEST_LOG=/dev/null FAKE_MX="${2:-}" bash -c '. scripts/lib/net.sh; smtp_guess "$1"' _ "$1"); }
check "gmail.com" [ "$(guess a@gmail.com)" = "smtp.gmail.com 587 google" ]
check "hotmail.com" [ "$(guess a@hotmail.com)" = "smtp-mail.outlook.com 587 outlook" ]
check "yandex.com.tr" [ "$(guess a@yandex.com.tr)" = "smtp.yandex.com 465 yandex" ]
check "ozel alan + Yandex MX" [ "$(guess a@firma.com mx.yandex.net)" = "smtp.yandex.com 465 yandex" ]
check "ozel alan + Zoho MX" [ "$(guess a@firma.com mx.zoho.eu)" = "smtp.zoho.com 465 zoho" ]
check "ozel alan, bilinmeyen MX" [ "$(guess a@firma.com mail.firma.com)" = "smtp.firma.com 587 bilinmiyor" ]

echo "7) Kurulumdan sonra: scripts/smtp.sh set --from (sunucu adresten bulunur)"
d="$(new_copy smtpset)"
printf "SMTP_HOST='mailpit'\nSMTP_PORT=1025\n" > "$d/.env"
out="$(run_in smtpset "$d" FAKE_MX=aspmx.l.google.com scripts/smtp.sh set --from ik@firma.com --password 'abc' 2>&1)"; rc=$?
check "ayar uygulandi" [ "$rc" = 0 ]
check "Google Workspace sunucusu ve kullanici adi" eval '[ "$(envv "$d" SMTP_HOST)" = "'\''smtp.gmail.com'\''" ] && [ "$(envv "$d" SMTP_USER)" = "'\''ik@firma.com'\''" ]'

echo "8) Guncelleme yolu: mevcut .env korunur, --domain ve --keycloak-admin uygulanir"
d="$(new_copy upd)"
cp "$WORK/repo-smtpfail/.env" "$d/.env"
# Eski kurulum: servis hesabi anahtari, demo ve kayit ayarlari yok.
sed -i '/^KEYCLOAK_TENANT_ADMIN_/d; /^HR360_DEMO=/d; /^PUBLIC_SIGNUP_ENABLED=/d' "$d/.env"
secret_before="$(envv "$d" TENANT_SECRET_KEY)"
out="$(run_in upd "$d" FAKE_DNS_A=203.0.113.10 FAKE_LE=ok ./install.sh --domain hr.example.com --email admin@example.com \
        --tls letsencrypt --keycloak-admin ip:198.51.100.0/24 --no-signup --yes 2>&1)"; rc=$?
check "guncelleme basarili" [ "$rc" = 0 ]
check "guncelleme yolu secildi, sirlar korundu" eval 'grep -q "Mevcut .env korunuyor" <<< "$out" && [ "$(envv "$d" TENANT_SECRET_KEY)" = "$secret_before" ]'
check "adres https://hr.example.com oldu" [ "$(envv "$d" PUBLIC_ORIGIN)" = https://hr.example.com ]
check "Let's Encrypt yonetici e-postasiyla istendi" grep -q "certonly.*-d hr.example.com.*--email admin@example.com" "$WORK/log-upd"
check "Keycloak paneli IP listesine kisitlandi" eval '[ "$(envv "$d" KEYCLOAK_ADMIN_MODE)" = ip ] && [ "$(envv "$d" KEYCLOAK_ADMIN_ALLOWED_IPS)" = 198.51.100.0/24 ]'
check "ufw'ye dokunulmadi" eval '! grep -Eq "ufw (allow|enable|deny|reject|delete|reset|disable)" "$WORK/log-upd"'
check "eski kurulumda servis hesabi olusturuldu, master parola geri donusu kapandi" eval '[ "$(envv "$d" KEYCLOAK_TENANT_ADMIN_CLIENT_SECRET | wc -c)" -ge 33 ] && grep -q "^KEYCLOAK_TENANT_ADMIN_LEGACY_USER=$" "$d/.env" && grep -q "hr360-tenant-admin" "$WORK/log-upd"'
check "servis hesabiyla master kaba kuvvet korumasi acilir" grep -q 'BF=true' "$WORK/log-upd"
check "eski kurulum: HR360_DEMO eklenmez (demo davranisi korunur)" eval '! grep -q "^HR360_DEMO=" "$d/.env"'
check "guncellemede --no-signup uygulanir" [ "$(envv "$d" PUBLIC_SIGNUP_ENABLED)" = false ]

echo "9) --demo --no-signup (IP adresiyle, soru sormadan)"
d="$(new_copy demo)"
out="$(run_in demo "$d" ./install.sh --url http://10.0.0.5 --demo --no-signup --yes 2>&1)"; rc=$?
check "kurulum basarili" [ "$rc" = 0 ]
check "HR360_DEMO=true, demo parolasi uretildi (12+)" eval '[ "$(envv "$d" HR360_DEMO)" = true ] && [ "$(envv "$d" DEMO_ADMIN_PASSWORD | wc -c)" -ge 13 ]'
check "realm: demo.admin ve platform.admin, gecerli JSON" eval 'realm_ok "$d" && has_user "$d" demo.admin && has_user "$d" platform.admin'
check "demo.admin parolasi realm'e yazildi" grep -q "\"value\": \"$(envv "$d" DEMO_ADMIN_PASSWORD)\"" "$d/deploy/keycloak/realm-export.json"
check "PUBLIC_SIGNUP_ENABLED=false" [ "$(envv "$d" PUBLIC_SIGNUP_ENABLED)" = false ]
check "ozet: demo girisi ve kayit kapali" eval 'grep -q "Demo giris:" <<< "$out" && grep -q "Sirket kaydi:      kapali" <<< "$out"'
check "HTTPS yok: panel ana adreste (open)" [ "$(envv "$d" KEYCLOAK_ADMIN_MODE)" = open ]

echo "9b) Kurulum turu ve sirket kipi (--edition / --modules / --tenancy / --company)"
d="$(new_copy lite)"
out="$(run_in lite "$d" ./install.sh --url http://10.0.0.5 --edition lite --company "Acme Lojistik A.Ş." --yes 2>&1)"; rc=$?
check "Hafif kurulum basarili" [ "$rc" = 0 ]
check "Hafif: puantaj, bordro, masraf acik; digerleri 0" eval '[ "$(envv "$d" HR360_MODULES)" = "timeshift,compensation,expense" ] && [ "$(envv "$d" HR360_MOD_RECRUITMENT)" = 0 ] && [ "$(envv "$d" HR360_MOD_ML)" = 0 ] && [ -z "$(envv "$d" HR360_MOD_TIMESHIFT)" ]'
check "Hafif: tek sirket, ad yazildi, kayit kapali" eval '[ "$(envv "$d" HR360_TENANCY)" = single ] && [ "$(envv "$d" HR360_COMPANY_NAME)" = "'\''Acme Lojistik A.Ş.'\''" ] && [ "$(envv "$d" PUBLIC_SIGNUP_ENABLED)" = false ]'
check "ozet: kurulum turu ve sirket kipi" eval 'grep -q "Kurulum turu:     Hafif" <<< "$out" && grep -q "Sirket kipi:      tek sirket (Acme Lojistik A.Ş.)" <<< "$out"'
d="$(new_copy custom)"
out="$(run_in custom "$d" ./install.sh --url http://10.0.0.5 --modules recruitment,onboarding --tenancy multi --yes 2>&1)"; rc=$?
check "Ozel: yalniz secilen moduller, cok sirket" eval '[ "$rc" = 0 ] && [ "$(envv "$d" HR360_MODULES)" = "recruitment,onboarding" ] && [ "$(envv "$d" HR360_TENANCY)" = multi ]'
d="$(new_copy fullyes)"
out="$(run_in fullyes "$d" ./install.sh --url http://10.0.0.5 --edition full --yes 2>&1)"; rc=$?
check "Tam + --yes: tum moduller, cok sirket, HR360_MOD_ yok" eval '[ "$rc" = 0 ] && [ "$(envv "$d" HR360_MODULES)" = all ] && [ "$(envv "$d" HR360_TENANCY)" = multi ] && ! grep -q "^HR360_MOD_" "$d/.env"'
run_in opts "$d" ./install.sh --modules bordro --yes >/dev/null 2>&1; check "bilinmeyen modul reddedilir (2)" [ $? = 2 ]
run_in opts "$d" ./install.sh --edition mini --yes >/dev/null 2>&1; check "gecersiz --edition reddedilir (2)" [ $? = 2 ]
run_in opts "$d" ./install.sh --tenancy tek --yes >/dev/null 2>&1; check "gecersiz --tenancy reddedilir (2)" [ $? = 2 ]
run_in opts "$d" ./install.sh --company 'A"B' --yes >/dev/null 2>&1; check "tirnakli sirket adi reddedilir (2)" [ $? = 2 ]

echo "10) Sunucu kendisi gondersin (--mail-mode=self, denetimler tamam)"
d="$(new_copy self)"
out="$(run_in self "$d" FAKE_DNS_A=203.0.113.10 FAKE_LE=ok FAKE_PORT25=ok FAKE_PTR=hr.example.com FAKE_DKIM=MIIBdkimtest \
        ./install.sh --domain hr.example.com --email admin@example.com --mail-mode=self --yes 2>&1)"; rc=$?
check "kurulum basarili" [ "$rc" = 0 ]
check "on denetimler gecti (25/tcp + PTR)" eval 'grep -q "\[tamam\] 25/tcp cikisi acik" <<< "$out" && grep -q "\[tamam\] PTR: 203.0.113.10 -> hr.example.com" <<< "$out"'
check "SMTP = paketteki posta servisi (mailer:25, kimlik dogrulamasiz)" eval '[ "$(envv "$d" SMTP_HOST)" = "'\''mailer'\''" ] && [ "$(envv "$d" SMTP_PORT)" = 25 ] && [ "$(envv "$d" SMTP_USER)" = "'\'''\''" ]'
check "gonderen varsayilan noreply@alan-adi" [ "$(envv "$d" SMTP_FROM_ADDRESS)" = "'noreply@hr.example.com'" ]
check "MAIL_MODE/HOSTNAME/DOMAIN .env'de" eval '[ "$(envv "$d" MAIL_MODE)" = self ] && [ "$(envv "$d" MAIL_HOSTNAME)" = hr.example.com ] && [ "$(envv "$d" MAIL_DOMAIN)" = hr.example.com ]'
check "mailer profili acik, letsencrypt korunur" eval 'case ",$(envv "$d" COMPOSE_PROFILES)," in *,mailer,*) case ",$(envv "$d" COMPOSE_PROFILES)," in *,letsencrypt,*) true ;; *) false ;; esac ;; *) false ;; esac'
check "realm SMTP: mailer, auth/ssl/starttls kapali" eval 'grep -q "\"host\" *: *\"mailer\"" "$d/deploy/keycloak/realm-export.json" && grep -q "\"auth\" *: *\"false\"" "$d/deploy/keycloak/realm-export.json"'
check "harici SMTP denemesi yapilmadi" bash -c "! grep -q -- '--mail-rcpt' '$WORK/log-self'"
check "DNS dosyasi: SPF, DKIM, DMARC, PTR" eval 'f="$d/deploy/mailer/dns-kayitlari.txt"; grep -q "v=spf1 ip4:203.0.113.10 ~all" "$f" && grep -q "hr360._domainkey.hr.example.com" "$f" && grep -q "v=DKIM1; h=sha256; k=rsa; p=MIIBdkimtest" "$f" && grep -q "rua=mailto:admin@example.com" "$f" && grep -q "203.0.113.10 -> hr.example.com" "$f"'
check "ozet: DNS kayitlari ve isinma uyarisi" eval 'grep -q "E-posta DNS kayitlari" <<< "$out" && grep -q "_dmarc.hr.example.com" <<< "$out" && grep -q "isinma" <<< "$out"'

echo "11) --mail-mode=self, denetimler basarisiz"
d="$(new_copy selffail)"
out="$(run_in selffail "$d" FAKE_DNS_A=203.0.113.10 FAKE_LE=ok ./install.sh --domain hr.example.com --mail-mode self --yes 2>&1)"; rc=$?
check "--yes: kurulum iptal (2), .env yazilmadi" eval '[ "$rc" = 2 ] && [ ! -f "$d/.env" ]'
check "25/tcp ve PTR sorunlari acikca yazildi" eval 'grep -q "\[SORUN\] 25/tcp cikisi kapali" <<< "$out" && grep -q "203.0.113.10 icin PTR kaydi yok" <<< "$out"'
check "25/tcp bilinen MX'lerle denendi" grep -q "port25 gmail-smtp-in.l.google.com" "$WORK/log-selffail"
out="$(run_in selffail "$d" FAKE_DNS_A=203.0.113.10 FAKE_LE=ok FAKE_PTR=baska.example.net HR360_MAIL_IGNORE_CHECKS=1 \
        ./install.sh --domain hr.example.com --mail-mode self --smtp-from ik@example.com --yes 2>&1)"; rc=$?
check "HR360_MAIL_IGNORE_CHECKS=1 ile devam edilir" eval '[ "$rc" = 0 ] && [ "$(envv "$d" MAIL_MODE)" = self ] && [ "$(envv "$d" MAIL_DOMAIN)" = example.com ]'
check "yanlis PTR adi soylendi" grep -q "baska.example.net' gosteriyor; 'hr.example.com' olmali" <<< "$out"
check "DKIM okunamazsa uyari, DNS dosyasi yine yazildi" eval 'grep -q "DKIM anahtari okunamadi" <<< "$out" && grep -q "scripts/smtp.sh dns" "$d/deploy/mailer/dns-kayitlari.txt"'

echo "12) Sorulu kurulum: e-posta menusunden 'sunucu kendisi gondersin'"
d="$(new_copy selfask)"
# adres, panel, yonetici e-postasi, kurulum turu (1), sirket kipi (2), demo, kayit, HTTPS, e-posta kipi (2), gonderen (Enter), denetim sonucu (1 = iptal)
answers=$'hr.example.com\n\nadmin@example.com\n1\n2\n\n\n\n2\n\n1\n\n\n\n\n\n\n\n\n\n'
out="$(printf '%s' "$answers" | run_in selfask "$d" FAKE_DNS_A=203.0.113.10 FAKE_LE=ok ./install.sh 2>&1)"; rc=$?
check "menu gosterildi" grep -q "2) Sunucu kendisi gondersin (gelismis" <<< "$out"
check "denetim basarisiz + iptal -> Mailpit" eval '[ "$rc" = 0 ] && [ "$(envv "$d" SMTP_HOST)" = "'\''mailpit'\''" ] && ! grep -q "^MAIL_MODE=" "$d/.env"'
d="$(new_copy selfask2)"
answers=$'hr.example.com\n\nadmin@example.com\n1\n2\n\n\n\n2\nik@example.com\n\n\n\n\n\n\n\n\n'
out="$(printf '%s' "$answers" | run_in selfask2 "$d" FAKE_DNS_A=203.0.113.10 FAKE_LE=ok FAKE_PORT25=ok FAKE_PTR=hr.example.com ./install.sh 2>&1)"; rc=$?
check "denetimler tamam -> sunucunun kendi posta servisi, girilen gonderen" eval '[ "$rc" = 0 ] && [ "$(envv "$d" MAIL_MODE)" = self ] && [ "$(envv "$d" SMTP_FROM_ADDRESS)" = "'\''ik@example.com'\''" ]'
d="$(new_copy ipask)"
out="$(printf '%s' $'http://10.0.0.5\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n' | run_in ipask "$d" ./install.sh 2>&1)"; rc=$?
check "IP adresli kurulumda menu gosterilmez" eval '[ "$rc" = 0 ] && ! grep -q "Sunucu kendisi gondersin" <<< "$out"'

echo "13) Kurulumdan sonra: scripts/smtp.sh self / dns / mailpit"
d="$(new_copy smtpself)"
printf "PUBLIC_ORIGIN=http://10.0.0.5\nSMTP_HOST='mailpit'\nSMTP_PORT=1025\nCOMPOSE_PROFILES=letsencrypt\n" > "$d/.env"
out="$(run_in smtpself "$d" scripts/smtp.sh self --yes 2>&1)"; rc=$?
check "IP adresinde reddedilir" eval '[ "$rc" != 0 ] && grep -q "yalnizca alan adli kurulumda" <<< "$out"'
sed -i 's#^PUBLIC_ORIGIN=.*#PUBLIC_ORIGIN=https://hr.example.com#' "$d/.env"
out="$(run_in smtpself "$d" FAKE_PORT25=ok FAKE_PTR=hr.example.com FAKE_DKIM=MIIBsonra scripts/smtp.sh self --yes 2>&1)"; rc=$?
check "self uygulandi" eval '[ "$rc" = 0 ] && [ "$(envv "$d" MAIL_MODE)" = self ] && [ "$(envv "$d" SMTP_HOST)" = "'\''mailer'\''" ] && [ "$(envv "$d" COMPOSE_PROFILES)" = letsencrypt,mailer ]'
check "posta servisi baslatildi, Keycloak guncellendi" eval 'grep -q "compose up -d --build mailer" "$WORK/log-smtpself" && grep -q "smtpServer.host=" "$WORK/log-smtpself"'
check "DNS kayitlari yazildi" eval 'grep -q "p=MIIBsonra" <<< "$out" && [ -f "$d/deploy/mailer/dns-kayitlari.txt" ]'
out="$(run_in smtpself "$d" FAKE_DKIM=MIIBsonra scripts/smtp.sh dns 2>&1)"
check "smtp.sh dns kayitlari yeniden yazar" grep -q "_dmarc.hr.example.com" <<< "$out"
out="$(run_in smtpself "$d" scripts/smtp.sh mailpit 2>&1)"; rc=$?
check "mailpit: posta servisi kapatildi, diger profiller korundu" eval '[ "$rc" = 0 ] && [ -z "$(envv "$d" MAIL_MODE)" ] && [ "$(envv "$d" COMPOSE_PROFILES)" = letsencrypt ] && grep -q "compose rm -sf mailer" "$WORK/log-smtpself"'
out="$(run_in smtpself "$d" scripts/smtp.sh dns 2>&1)"; rc=$?
check "kapaliyken smtp.sh dns hata verir" [ "$rc" != 0 ]

echo "14) On denetim fonksiyonlari (kuru calisma)"
lib() { (cd "$ROOT" && PATH="$BIN:$PATH" HR360_TEST_LOG=/dev/null FAKE_PUBLIC_IP=203.0.113.10 "$@"); }
out="$(lib env FAKE_PORT25=ok bash -c '. scripts/lib/net.sh; . scripts/lib/mail.sh; port25_outbound && echo "$PORT25_MSG"')"
check "port25_outbound: acik" grep -q "25/tcp cikisi acik (gmail-smtp-in.l.google.com)" <<< "$out"
out="$(lib env HR360_PORT25_HOSTS="mx1.ornek.com mx2.ornek.com" bash -c '. scripts/lib/net.sh; . scripts/lib/mail.sh; port25_outbound; echo "rc=$? $PORT25_MSG"')"
check "port25_outbound: kapali, denenenler listelenir" grep -q "rc=1 25/tcp cikisi kapali gorunuyor (denenen: mx1.ornek.com, mx2.ornek.com)" <<< "$out"
out="$(lib env FAKE_PTR=HR.Example.COM bash -c '. scripts/lib/net.sh; . scripts/lib/mail.sh; ptr_matches hr.example.com; echo "rc=$?"')"
check "ptr_matches: buyuk/kucuk harf duyarsiz eslesir" grep -q "rc=0" <<< "$out"
out="$(lib bash -c '. scripts/lib/net.sh; . scripts/lib/mail.sh; HR360_PUBLIC_IP=""; public_ipv4() { return 1; }; ptr_matches hr.example.com; echo "rc=$? $PTR_CHECK_MSG"')"
check "ptr_matches: genel IP yoksa belirlenemedi (2)" grep -q "rc=2 sunucunun genel IP adresi belirlenemedi" <<< "$out"
out="$(lib bash -c '. scripts/lib/mail.sh; mail_dns_records firma.com hr.firma.com 198.51.100.4 hr360 "" ""')"
check "mail_dns_records: varsayilan DMARC raporu postmaster@, DKIM bekleniyor notu" eval 'grep -q "rua=mailto:postmaster@firma.com" <<< "$out" && grep -q "anahtar henuz okunamadi" <<< "$out" && grep -q "v=spf1 ip4:198.51.100.4 ~all" <<< "$out"'

echo ""
if [ "$FAILS" = 0 ]; then echo "Tum kurulum testleri gecti."; else echo "$FAILS test basarisiz."; fi
[ "$FAILS" = 0 ]

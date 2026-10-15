#!/usr/bin/env bash
# scripts/secrets-migrate.sh (sirlarin .env'den secrets/*.txt'ye tasinmasi, compose eki) ve
# scripts/crypto-keys.sh (anahtar halkasi: ekle / etkinlestir / cikar) davranis testi.
# Docker gerektirmez: docker yerine basarisiz donen sahte komut (grup degistirme uyarisi beklenir).
# Sir degerlerinin hicbir ciktida gorunmedigi de denetlenir.
#
# Kullanim: tests/install/test_secrets.sh
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
FAILS=0
pass() { echo "  ok   $*"; }
fail() { echo "  FAIL $*"; FAILS=$((FAILS + 1)); }
check() { local desc="$1"; shift; if "$@"; then pass "$desc"; else fail "$desc"; fi; }

BIN="$WORK/bin"; mkdir -p "$BIN"
printf '#!/usr/bin/env bash\nexit 1\n' > "$BIN/docker"; chmod +x "$BIN/docker"
export PATH="$BIN:$PATH"

D="$WORK/repo"; mkdir -p "$D/scripts/lib"
cp "$ROOT/docker-compose.yml" "$D/"
cp "$ROOT/scripts/secrets-migrate.sh" "$ROOT/scripts/crypto-keys.sh" "$D/scripts/"
cp "$ROOT/scripts/lib/secrets.sh" "$D/scripts/lib/"
KEY="$(head -c 32 /dev/urandom | base64 | tr -d '\n')"
cat > "$D/.env" <<EOF
HR360_DB_PASSWORD=ortak-parola-xyz
TENANT_SECRET_KEY=$KEY
KEYCLOAK_TENANT_ADMIN_CLIENT_SECRET=kc-gizli-xyz
INTERNAL_SERVICE_TOKEN=ic-jeton-xyz
HR360_DB_USER_LEAVE=hr360_leave
HR360_DB_PASSWORD_LEAVE=izin-parola-xyz
PUBLIC_URL=http://localhost
EOF
cp "$D/.env" "$WORK/env.orig"
envv() { grep "^$1=" "$D/.env" | tail -1 | cut -d= -f2-; }
no_secret_in() { ! grep -qE "xyz|${KEY:0:12}" "$1"; }
run() { (cd "$D" && "$@") > "$WORK/out" 2>&1; }

echo "1) secrets-migrate.sh move (varsayilan secim)"
run scripts/secrets-migrate.sh move --dry-run
check "deneme modu dosyalara dokunmaz" eval 'cmp -s "$D/.env" "$WORK/env.orig" && [ ! -e "$D/secrets" ]'
check "deneme ciktisinda sir yok" no_secret_in "$WORK/out"
run scripts/secrets-migrate.sh move
check "tasima basarili" grep -q "compose.secrets.yml uretildi" "$WORK/out"
check "ciktida sir yok" no_secret_in "$WORK/out"
check ".env'den silindi" eval '[ -z "$(envv TENANT_SECRET_KEY)" ] && [ -z "$(envv KEYCLOAK_TENANT_ADMIN_CLIENT_SECRET)" ] && [ -z "$(envv HR360_DB_PASSWORD_LEAVE)" ]'
check "secilmeyenler .env'de kaldi" eval '[ "$(envv INTERNAL_SERVICE_TOKEN)" = ic-jeton-xyz ] && [ "$(envv HR360_DB_PASSWORD)" = ortak-parola-xyz ]'
check "dosyada deger dogru" eval '[ "$(cat "$D/secrets/tenant_secret_key.txt")" = "$KEY" ]'
check "dosya izinleri 0640, dizin 0700" eval '[ "$(stat -c %a "$D/secrets/tenant_secret_key.txt")" = 640 ] && [ "$(stat -c %a "$D/secrets")" = 700 ]'
check "COMPOSE_FILE yazildi" eval '[ "$(envv COMPOSE_FILE)" = "docker-compose.yml:secrets/compose.secrets.yml" ]'
O="$D/secrets/compose.secrets.yml"
check "compose eki: tenant-service anahtar dosyasi" grep -q "TENANT_SECRET_KEY_FILE: /run/secrets/tenant_secret_key" "$O"
check "compose eki: servis rolu parolasi leave-service'e" eval 'grep -A6 "^  leave-service:" "$O" | grep -q "HR360_SERVICE_DB_PASSWORD_FILE: /run/secrets/hr360_db_password_leave"'
check "compose eki: baglanti dizesinde ortak parola yok" eval 'grep -A8 "^  leave-service:" "$O" | grep "LEAVE_DB_CONNECTION" | grep -q "Password=;"'
check "compose ekinde sir degeri yok" no_secret_in "$O"
check "status sir yazmaz" eval '(cd "$D" && scripts/secrets-migrate.sh status) > "$WORK/st" 2>&1 && grep -q "dosya (secrets/tenant_secret_key.txt)" "$WORK/st" && no_secret_in "$WORK/st"'

echo "2) desteklenmeyen anahtar"
run scripts/secrets-migrate.sh move HR360_DB_PASSWORD
check "HR360_DB_PASSWORD reddedilir (Keycloak vb. kullaniyor)" eval '! (cd "$D" && scripts/secrets-migrate.sh move HR360_DB_PASSWORD) >/dev/null 2>&1 && grep -q "tasinamaz" "$WORK/out"'

echo "3) crypto-keys.sh: dosya modunda anahtar halkasi"
run scripts/crypto-keys.sh list
check "tek anahtar k0" grep -q "^k0 (etkin)" "$WORK/out"
run scripts/crypto-keys.sh add --id k1
check "yeni anahtar eklendi (sona)" eval '(cd "$D" && scripts/crypto-keys.sh list) | paste -sd" " - | grep -q "^k0 (etkin) k1$"'
check "halka dosyaya yazildi, .env'e degil" eval '[ -f "$D/secrets/tenant_secret_keys.txt" ] && [ -z "$(envv TENANT_SECRET_KEYS)" ]'
check "halka eski anahtari k0 olarak icerir" eval 'grep -q "^k0:${KEY}," "$D/secrets/tenant_secret_keys.txt"'
check "compose ekine TENANT_SECRET_KEYS_FILE eklendi" grep -q "TENANT_SECRET_KEYS_FILE: /run/secrets/tenant_secret_keys" "$O"
check "ciktida anahtar yok" no_secret_in "$WORK/out"
run scripts/crypto-keys.sh activate k1
check "k1 etkin" eval '(cd "$D" && scripts/crypto-keys.sh list) | paste -sd" " - | grep -q "^k1 (etkin) k0$"'
check "etkin anahtar cikarilamaz" eval '! (cd "$D" && scripts/crypto-keys.sh remove k1) >/dev/null 2>&1'
check "gecersiz kimlik reddedilir" eval '! (cd "$D" && scripts/crypto-keys.sh add --id "a:b") >/dev/null 2>&1'
check "k0 kimligi yeni anahtara verilemez" eval '! (cd "$D" && scripts/crypto-keys.sh add --id k0) >/dev/null 2>&1'
run scripts/crypto-keys.sh remove k0 --force
check "k0 --force ile cikarildi" eval '(cd "$D" && scripts/crypto-keys.sh list) | paste -sd" " - | grep -q "^k1 (etkin)$"'
check "TENANT_SECRET_KEY yerinde (turetilmis anahtarlarin koku)" eval '[ "$(cat "$D/secrets/tenant_secret_key.txt")" = "$KEY" ]'

echo "4) secrets-migrate.sh restore"
run scripts/secrets-migrate.sh restore
check "sirlar .env'e dondu" eval '[ "$(envv TENANT_SECRET_KEY)" = "$KEY" ] && [ "$(envv HR360_DB_PASSWORD_LEAVE)" = izin-parola-xyz ] && [ -n "$(envv TENANT_SECRET_KEYS)" ]'
check "compose eki ve COMPOSE_FILE kaldirildi" eval '[ ! -f "$O" ] && [ -z "$(envv COMPOSE_FILE)" ]'
check "ciktida sir yok" no_secret_in "$WORK/out"

echo "5) .env modunda halka"
cp "$WORK/env.orig" "$D/.env"; rm -rf "$D/secrets"
run scripts/crypto-keys.sh add --id k2 --activate
check "halka .env'de, yeni anahtar etkin" eval '[[ "$(envv TENANT_SECRET_KEYS)" == k2:*,k0:"$KEY" ]] && [ ! -e "$D/secrets" ]'

echo
if [ "$FAILS" -eq 0 ]; then echo "Tum sir testleri gecti."; else echo "$FAILS test basarisiz."; exit 1; fi

#!/usr/bin/env bash
# scripts/tenancy.sh: durum çıktısı, .env yazımı ve "docker compose up -d" çağrısı, tek şirket kipine geçiş
# koşulu (sahte docker; şirket sayısı HR360_TEST_TENANTS ile verilir).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
T="$(mktemp -d)"; trap 'rm -rf "$T"' EXIT
fail=0
ok() { echo "  ok   $*"; }
bad() { echo "  XX   $*"; fail=1; }
check() { if eval "$2"; then ok "$1"; else bad "$1"; fi; }

mkdir -p "$T/repo/scripts/lib" "$T/bin"
cp "$ROOT/scripts/tenancy.sh" "$T/repo/scripts/"
cp "$ROOT/scripts/lib/modules.sh" "$T/repo/scripts/lib/"
printf 'HR360_DB_PASSWORD=x\nHR360_COMPANY_NAME=Örnek A.Ş.\n' > "$T/repo/.env"
chmod 600 "$T/repo/.env"
cat > "$T/bin/docker" <<'EOS'
#!/usr/bin/env bash
echo "docker $*" >> "$HR360_TEST_LOG"
if [[ "$*" == *"psql"* ]]; then
  [ "${HR360_TEST_TENANTS:-}" = down ] && { echo "service postgres is not running" >&2; exit 1; }
  echo "${HR360_TEST_TENANTS:-1}"
fi
EOS
chmod +x "$T/bin/docker"
export HR360_TEST_LOG="$T/docker.log" PATH="$T/bin:$PATH"
S="$T/repo/scripts/tenancy.sh"
envv() { grep "^$1=" "$T/repo/.env" | tail -1 | cut -d= -f2- || true; }
log() { cat "$HR360_TEST_LOG" 2>/dev/null || true; }
reset_log() { : > "$HR360_TEST_LOG"; }

echo "tenancy.sh"
out="$(HR360_TEST_TENANTS=3 $S status)"
check "değişken yoksa çok şirketli; şirket sayısı" '[ "$out" = "$(printf "tenancy=multi\ntenants=3")" ]'
check "durum yalnızca okur (up yok)" '! log | grep -q "compose up"'
out="$(HR360_TEST_TENANTS=down $S status)"
check "veritabanı yoksa tenants=?" '[[ "$out" == *"tenants=?"* ]]'

reset_log
HR360_TEST_TENANTS=2 $S multi >/dev/null
check "multi .env'e yazar" '[ "$(envv HR360_TENANCY)" = multi ]'
check "multi servisleri yeniler" 'log | grep -q "compose up -d"'
HR360_TEST_TENANTS=2 $S multi >/dev/null
check "multi tekrarında tek satır kalır" '[ "$(grep -c "^HR360_TENANCY=" "$T/repo/.env")" = 1 ]'
check ".env izinleri korunur" '[ "$(stat -c %a "$T/repo/.env")" = 600 ]'

reset_log
if HR360_TEST_TENANTS=2 $S single >/dev/null 2>"$T/err"; then bad "2 şirketle tek şirket kipi reddedilmeli"; else ok "2 şirketle tek şirket kipi reddedilir"; fi
check "ret iletisi Türkçe ve sayıyı içerir" 'grep -q "tam olarak bir şirket" "$T/err" && grep -q "2 şirket" "$T/err"'
check "ret .env'e dokunmaz, up çağırmaz" '[ "$(envv HR360_TENANCY)" = multi ] && ! log | grep -q "compose up"'
if HR360_TEST_TENANTS=down $S single >/dev/null 2>&1; then bad "sayı okunamazken tek şirket kipi reddedilmeli"; else ok "sayı okunamazken reddedilir"; fi

reset_log
HR360_TEST_TENANTS=1 $S single >/dev/null
check "1 şirketle tek şirket kipine geçilir" '[ "$(envv HR360_TENANCY)" = single ] && log | grep -q "compose up -d"'
check "şirket adı korunur" '[ "$(envv HR360_COMPANY_NAME)" = "Örnek A.Ş." ]'
out="$(HR360_TEST_TENANTS=1 $S status)"
check "durum tek şirket gösterir" '[[ "$out" == "tenancy=single"* ]]'
HR360_TEST_TENANTS=1 $S multi >/dev/null
check "tek şirketten çok şirkete dönülür" '[ "$(envv HR360_TENANCY)" = multi ]'

check "bilinmeyen komut reddedilir" '! $S bilinmeyen >/dev/null 2>&1'

[ "$fail" = 0 ] && echo "Tum denetimler gecti." || { echo "Basarisiz denetim var."; exit 1; }

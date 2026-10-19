#!/usr/bin/env bash
# scripts/modules.sh ve scripts/lib/modules.sh: .env değişkenleri, ağ geçidi işaretleri, derleme/başlatma
# çağrıları (sahte docker) ve docker-compose.yml'deki scale değerleri (gerçek "docker compose config").
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
T="$(mktemp -d)"; trap 'rm -rf "$T"' EXIT
fail=0
ok() { echo "  ok   $*"; }
bad() { echo "  XX   $*"; fail=1; }
check() { if eval "$2"; then ok "$1"; else bad "$1"; fi; }

mkdir -p "$T/repo/scripts/lib" "$T/bin"
cp "$ROOT/scripts/modules.sh" "$T/repo/scripts/"
cp "$ROOT/scripts/lib/modules.sh" "$ROOT/scripts/lib/backup-common.sh" "$T/repo/scripts/lib/"
cp "$ROOT/VERSION" "$T/repo/"
printf 'HR360_DB_PASSWORD=x\nCOMPOSE_PROFILES=letsencrypt\n' > "$T/repo/.env"
cat > "$T/bin/docker" <<'EOF'
#!/usr/bin/env bash
echo "docker $*" >> "$HR360_TEST_LOG"
EOF
chmod +x "$T/bin/docker"
export HR360_TEST_LOG="$T/docker.log" PATH="$T/bin:$PATH" HR360_METRICS_DIR="$T/metrics"
M="$T/repo/scripts/modules.sh"
envv() { grep "^$1=" "$T/repo/.env" | tail -1 | cut -d= -f2- || true; }
log() { cat "$HR360_TEST_LOG" 2>/dev/null || true; }
reset_log() { : > "$HR360_TEST_LOG"; }

echo "modules.sh"
out="$($M status)"
check "başlangıçta hepsi açık (değişken yok)" '[[ "$out" == *"HR360_MODULES=all"* && "$out" != *"=off"* ]]'

reset_log
$M disable recruitment ml >/dev/null
check "kapatma .env'e 0 yazar" '[ "$(envv HR360_MOD_RECRUITMENT)" = 0 ] && [ "$(envv HR360_MOD_ML)" = 0 ]'
check "HR360_MODULES kapalıları içermez" '[ "$(envv HR360_MODULES)" = "onboarding,timeshift,performance,learning,compensation,expense" ]'
check "ağ geçidi işaretleri yazıldı" '[ -f "$T/repo/deploy/nginx/modules/recruitment.off" ] && [ -f "$T/repo/deploy/nginx/modules/ml.off" ]'
check "kapatma derleme yapmaz, up -d çağırır" '! log | grep -q " build" && log | grep -q "compose up -d"'
check "COMPOSE_PROFILES korunur" '[ "$(envv COMPOSE_PROFILES)" = letsencrypt ]'
check "izleme metriği: kapalı servis 1, açık 0" 'grep -q "hr360_module_disabled{service=\"ml-inference\"} 1" "$T/metrics/hr360_modules.prom" && grep -q "hr360_module_disabled{service=\"timeshift-service\"} 0" "$T/metrics/hr360_modules.prom"'

rm -f "$T/repo/deploy/nginx/modules/"*.off; sed -i '/^HR360_MODULES=/d' "$T/repo/.env"
$M sync
check "sync işaretleri ve HR360_MODULES'u .env'den yeniden üretir" '[ -f "$T/repo/deploy/nginx/modules/ml.off" ] && [ "$(envv HR360_MODULES)" = "onboarding,timeshift,performance,learning,compensation,expense" ]'

reset_log
$M enable recruitment >/dev/null
check "açma değişkeni kaldırır" '[ -z "$(envv HR360_MOD_RECRUITMENT)" ]'
check "açma işareti kaldırır" '[ ! -f "$T/repo/deploy/nginx/modules/recruitment.off" ]'
check "açma yalnızca modülün imajını derler" 'log | grep -q "compose build recruitment-service$"'
check "kapalıdan açılınca Kafka konumu sona alınır" 'log | grep -q -- "--group recruitment-service --topic hr360.workflow.events --reset-offsets --to-latest"'

reset_log
$M enable timeshift >/dev/null
check "zaten açık modülde Kafka konumuna dokunulmaz" '! log | grep -q reset-offsets'

reset_log
$M set timeshift,compensation,expense --no-start >/dev/null
check "Hafif seçimi: yalnız puantaj, bordro, masraf açık" '[ "$(envv HR360_MODULES)" = "timeshift,compensation,expense" ]'
check "Hafif seçimi: beş modül kapalı" '[ "$(grep -c "^HR360_MOD_.*=0$" "$T/repo/.env")" = 5 ]'
check "--no-start docker çağırmaz" '[ ! -s "$HR360_TEST_LOG" ]'

$M set all --no-start >/dev/null
check "all: kapalı değişken kalmaz" '! grep -q "^HR360_MOD_" "$T/repo/.env" && [ "$(envv HR360_MODULES)" = all ]'
check "all: işaret kalmaz" '[ -z "$(ls "$T/repo/deploy/nginx/modules/"*.off 2>/dev/null)" ]'

check "bilinmeyen modül reddedilir" '! $M disable bordro >/dev/null 2>&1'
check "çekirdek servis modül değildir" '! $M disable employee >/dev/null 2>&1'

echo "lib/modules.sh"
(
  cd "$T/repo"; ENV_FILE=.env; . scripts/lib/modules.sh
  mod_set ml off; mod_set onboarding off
  r="$(mod_filter_services engagement-service onboarding-service ml-inference tenant-service)"
  [ "$r" = "engagement-service tenant-service" ] && echo "  ok   mod_filter_services kapalı modül servislerini çıkarır" || { echo "  XX   mod_filter_services: $r"; exit 1; }
) || fail=1

echo "docker-compose.yml (gerçek compose)"
if command -v /usr/bin/docker >/dev/null 2>&1 && /usr/bin/docker compose version >/dev/null 2>&1; then
  printf 'HR360_MOD_ML=0\nHR360_MOD_TIMESHIFT=0\nCOMPOSE_PROFILES=letsencrypt\n' > "$T/compose.env"
  scales="$(cd "$ROOT" && /usr/bin/docker compose --env-file "$T/compose.env" config --format json 2>/dev/null | python3 -c '
import json, sys
s = json.load(sys.stdin)["services"]
want = ("ml-inference", "mlflow", "timeshift-service", "recruitment-service", "employee-service")
print(" ".join("%s=%s" % (k, s[k].get("scale", 1)) for k in sorted(s) if k in want))')"
  check "kapalı modül scale=0, diğerleri 1 (COMPOSE_PROFILES tanımlıyken de)" \
    '[ "$scales" = "employee-service=1 ml-inference=0 mlflow=0 recruitment-service=1 timeshift-service=0" ]'
  n="$(cd "$ROOT" && /usr/bin/docker compose --env-file "$T/compose.env" config --services 2>/dev/null | grep -c -E "^(recruitment|onboarding|performance|learning|compensation)-service$")"
  check "modül servisleri COMPOSE_PROFILES tanımlıyken listede" '[ "$n" = 5 ]'
else
  echo "  --   docker compose yok; compose denetimi atlandı"
fi

[ "$fail" = 0 ] && echo "Tum denetimler gecti." || { echo "Basarisiz denetim var."; exit 1; }

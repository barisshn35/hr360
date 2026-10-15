#!/usr/bin/env bash
# HR360 guvenlik taramasi (Trivy + npm audit + NuGet).
#
#   scripts/security-scan.sh repo     Depo: bagimlilik aciklari (npm, pip, NuGet kilitleri),
#                                     sizmis gizli anahtarlar, Dockerfile/compose yapilandirma hatalari
#   scripts/security-scan.sh images   Calisan kurulumun imajlari (isletim sistemi paketleri dahil)
#   scripts/security-scan.sh deps     npm audit + .NET "dotnet list package --vulnerable"
#   scripts/security-scan.sh all      Hepsi (varsayilan; zap dahil DEGIL)
#   scripts/security-scan.sh zap [ADRES] [--ajax] [--minutes N]
#                                     OWASP ZAP temel taramasi (pasif + orumcek) calisan kuruluma.
#                                     ADRES yoksa ZAP_TARGET, o da yoksa .env'deki PUBLIC_ORIGIN,
#                                     o da yoksa Docker agindan http://gateway/ taranir.
#                                     Kurallar: .zap/rules.tsv. Rapor: security-reports/zap-<zaman>.html/.json
#                                     Cikis: 0 FAIL yok, 1 FAIL kurali eslesti, 2 tarama calismadi.
#
# Yalnizca HIGH/CRITICAL bulgular raporlanir; bulgu varsa cikis kodu 1'dir.
# Trivy, ZAP ve .NET SDK Docker imaji olarak calisir; yerelde kurulum gerekmez.
set -euo pipefail

cd "$(dirname "$0")/.."
ROOT="$PWD"
what="${1:-all}"
TRIVY_IMAGE="aquasec/trivy:0.57.1@sha256:5c59e08f980b5d4d503329773480fcea2c9bdad7e381d846fbf9f2ecb8050f6b"
ZAP_IMAGE="${ZAP_IMAGE:-ghcr.io/zaproxy/zaproxy:stable@sha256:781a2bdaea47324e7bab583e2263f21d257b0aee61ed51521a5be45f5f5081ef}"
SEVERITY="${SEVERITY:-HIGH,CRITICAL}"
fail=0
step() { printf '\n\033[1m== %s\033[0m\n' "$*"; }

if ! docker info >/dev/null 2>&1 && sudo docker info >/dev/null 2>&1; then
  docker() { command sudo docker "$@"; }
fi

trivy() {
  docker run --rm -v "$ROOT:/src:ro" -v hr360-trivy-cache:/root/.cache \
    -v /var/run/docker.sock:/var/run/docker.sock "$TRIVY_IMAGE" "$@"
}

repo() {
  step "Trivy: depo (bagimliliklar, gizli anahtarlar, yapilandirma)"
  trivy fs --scanners vuln,secret,misconfig --file-patterns 'pip:requirements-.*\.txt' --severity "$SEVERITY" --exit-code 1 \
    --skip-dirs /src/apps/web/node_modules --skip-dirs /src/tests/load/results \
    --skip-files /src/tests/credentials.json --skip-files /src/.env /src || fail=1
}

images() {
  step "Trivy: imajlar (yalnizca duzeltmesi yayimlanmis aciklar)"
  local imgs
  imgs=$(docker compose config --images 2>/dev/null | sort -u)
  for img in $imgs; do
    docker image inspect "$img" >/dev/null 2>&1 || continue
    echo "-- $img"
    trivy image --scanners vuln --severity "$SEVERITY" --ignore-unfixed --exit-code 1 --quiet "$img" || fail=1
  done
}

deps() {
  step "npm audit (uretim bagimliliklari)"
  (cd apps/web && npm audit --omit=dev --audit-level=high) || fail=1
  step "NuGet: bilinen acigi olan paketler"
  docker run --rm -v "$ROOT:/src:ro" -v hr360-nuget:/root/.nuget/packages mcr.microsoft.com/dotnet/sdk:9.0 sh -c '
    rc=0
    for p in /src/apps/services/*/; do
      rm -rf /tmp/s && mkdir /tmp/s && cp "$p"/*.csproj /tmp/s/ && cd /tmp/s
      out=$(dotnet list package --vulnerable --include-transitive 2>&1)
      if echo "$out" | grep -q "has the following vulnerable packages"; then echo "$out"; rc=1
      elif echo "$out" | grep -qi "error"; then echo "$out"; rc=1
      else echo "temiz: $(basename "$p")"; fi
      cd /
    done
    exit $rc' || fail=1
}

# OWASP ZAP temel taramasi: yalnizca pasif kurallar + kisa orumcek (saldiri istegi gondermez).
# Sunucuda NAT hairpin olmadigindan genel ad konteyner icinden cozulmeyebilir: ad sunucunun
# kendi cozumlemesiyle (/etc/hosts dahil) bulunur ve --add-host ile konteynere verilir;
# adres 127.x ise konteyner sunucunun ag yiginini kullanir (--network host).
zap() {
  local target="" ajax=() minutes="${ZAP_MINUTES:-2}"
  while [ $# -gt 0 ]; do
    case "$1" in
      --ajax) ajax=(-j); shift ;;
      --minutes) minutes="${2:-}"; shift 2 ;;
      -*) echo "bilinmeyen secenek: $1" >&2; return 2 ;;
      *) target="$1"; shift ;;
    esac
  done
  [[ "$minutes" =~ ^[0-9]+$ ]] || { echo "--minutes sayi olmali" >&2; return 2; }
  if [ -z "$target" ]; then target="${ZAP_TARGET:-}"; fi
  if [ -z "$target" ] && [ -f .env ]; then
    target="$(sed -n 's/^PUBLIC_ORIGIN=//p' .env | tail -1 | tr -d '"'"'"'\r')"
  fi
  [ -n "$target" ] || target="http://gateway/"
  case "$target" in http://*|https://*) ;; *) echo "adres http:// ya da https:// ile baslamali: $target" >&2; return 2 ;; esac

  local host net=() addhost=() ip
  host="${target#*://}"; host="${host%%/*}"; host="${host%%:*}"
  if [ "$host" = "gateway" ] || [ -n "${ZAP_NETWORK:-}" ]; then
    net=(--network "${ZAP_NETWORK:-hr360-net}")
    docker network inspect "${ZAP_NETWORK:-hr360-net}" >/dev/null 2>&1 \
      || { echo "Docker agi yok: ${ZAP_NETWORK:-hr360-net} (kurulum calisiyor mu?)" >&2; return 2; }
  elif [[ ! "$host" =~ ^[0-9.]+$ ]]; then
    ip="$(getent hosts "$host" 2>/dev/null | awk '{print $1; exit}')"
    if [[ "$ip" == 127.* ]]; then net=(--network host)
    elif [ -n "$ip" ]; then addhost=(--add-host "$host:$ip"); fi
  fi

  step "OWASP ZAP temel taramasi: $target (orumcek $minutes dk)"
  local wrk stamp rc=0
  wrk="$(mktemp -d)"
  chmod 777 "$wrk"   # ZAP konteyneri 1000 kullanicisiyla yazar
  cp .zap/rules.tsv "$wrk/rules.tsv"
  stamp="$(date +%Y%m%d-%H%M%S)"
  docker run --rm "${net[@]}" "${addhost[@]}" -v "$wrk:/zap/wrk:rw" "$ZAP_IMAGE" \
    zap-baseline.py -t "$target" -m "$minutes" "${ajax[@]}" -c rules.tsv -I \
      -r "zap-$stamp.html" -J "zap-$stamp.json" || rc=$?
  mkdir -p security-reports
  cp "$wrk"/zap-"$stamp".* security-reports/ 2>/dev/null || true
  rm -rf "$wrk"
  if [ -f "security-reports/zap-$stamp.html" ]; then echo "Rapor: security-reports/zap-$stamp.html"; fi
  case "$rc" in
    0) echo "ZAP: FAIL kurali eslesmedi (uyarilar raporda)."; return 0 ;;
    1) echo "ZAP: FAIL kurali eslesti - rapora bakin."; return 1 ;;
    *) echo "ZAP taramasi calismadi (cikis $rc)." >&2; return 2 ;;
  esac
}

case "$what" in
  repo) repo ;;
  images) images ;;
  deps) deps ;;
  all) repo; deps; images ;;
  zap) shift; rc=0; zap "$@" || rc=$?; exit "$rc" ;;
  *) echo "kullanim: scripts/security-scan.sh repo|images|deps|all|zap [ADRES]" >&2; exit 2 ;;
esac

echo
if [ "$fail" = 0 ]; then echo "Bulgu yok ($SEVERITY)."; else echo "Bulgu var - yukaridaki ciktiya bakin."; fi
exit "$fail"

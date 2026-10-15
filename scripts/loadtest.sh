#!/usr/bin/env bash
# HR360 yuk testi (k6, Docker icinde; kurulum gerekmez).
#
#   scripts/loadtest.sh [smoke|load|stress]
#     smoke   5 kullanici, 30 sn — dogrulama
#     load    100 eszamanli kullaniciya kadar, 3 dk (varsayilan)
#     stress  400 eszamanli kullaniciya kadar, 3 dk — kirilma noktasi
#
# Senaryo: %70 calisan, %25 yonetici, %5 IK (tests/load/hr360.js). Istekler gateway'e
# Docker agi icinden gider (internet baglantisi olcume girmez). Sonuc:
# tests/load/results/<tarih>-<profil>.json ve .md. Test sirasinda servislerin
# davranisini Grafana "HR360 — Servis sagligi" panosunda izleyin (scripts/monitoring.sh).
#
# Gereken: test kullanicilari (tests/credentials.json; ayse, mehmet, admin) ve
# python3 + playwright (erisim jetonlari tarayicidan giris yapilarak alinir).
set -euo pipefail

cd "$(dirname "$0")/.."
PROFILE="${1:-load}"
case "$PROFILE" in smoke|load|stress) ;; *) echo "kullanim: scripts/loadtest.sh [smoke|load|stress]" >&2; exit 2 ;; esac

if ! declare -F docker >/dev/null && ! docker info >/dev/null 2>&1 && sudo docker info >/dev/null 2>&1; then
  docker() { command sudo docker "$@"; }
fi

echo "Erisim jetonlari aliniyor..."
TOKENS="$(python3 tests/support/hr360_login.py ayse mehmet admin)"
TS="$(date +%Y%m%d-%H%M%S)"
OUT="tests/load/results"
mkdir -p "$OUT"
WORK="$(mktemp -d)"
chmod 777 "$WORK"
trap 'rm -rf "$WORK"' EXIT

echo "k6 ($PROFILE) calisiyor..."
set +e
docker run --rm --network hr360-net -v "$PWD/tests/load:/scripts:ro" -v "$WORK:/results" \
  -e BASE_URL=http://gateway -e PROFILE="$PROFILE" -e TOKENS="$TOKENS" \
  grafana/k6:0.54.0 run --quiet /scripts/hr360.js
rc=$?
set -e

[ -f "$WORK/summary.json" ] || { echo "k6 ozet uretmedi (cikis kodu $rc)"; exit 1; }
cp "$WORK/summary.json" "$OUT/$TS-$PROFILE.json"
python3 - "$OUT/$TS-$PROFILE.json" > "$OUT/$TS-$PROFILE.md" <<'PY'
import json, sys
d = json.load(open(sys.argv[1]))
s = d["summary"]
print(f"# HR360 yük testi — {s['profile']} ({s['at'][:19].replace('T', ' ')} UTC)\n")
print("| Ölçü | Değer |\n|---|---|")
print(f"| Toplam istek | {s['requests']:,} |".replace(",", "."))
print(f"| Ortalama hız | {s['rps']:.1f} istek/sn |")
print(f"| En fazla eşzamanlı kullanıcı | {s['maxVUs']} |")
print(f"| Hatalı istek | %{s['failedRate'] * 100:.2f} |")
print(f"| Yanıt süresi ort / p95 / p99 | {s['avg']:.0f} / {s['p95']:.0f} / {s['p99']:.0f} ms |")
print(f"| Ekran yükleme p95 (paralel istekler) | {s['screenP95']:.0f} ms |")
print(f"| Eşikler (hata <%1, okuma p95 <800 ms, p99 <2 sn) | {'geçti' if s['thresholdsPassed'] else 'KALDI'} |\n")
print("## Uç bazında p95\n\n| Uç | ort (ms) | p95 (ms) |\n|---|---|---|")
for r in sorted((r for r in d["endpoints"] if r["avg"] > 0), key=lambda r: -r["p95"]):
    print(f"| `{r['endpoint']}` | {r['avg']:.0f} | {r['p95']:.0f} |")
PY
echo
cat "$OUT/$TS-$PROFILE.md"
echo
echo "Kaydedildi: $OUT/$TS-$PROFILE.md"
exit "$rc"

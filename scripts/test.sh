#!/usr/bin/env bash
# HR360 testlerini calistirir.
#
#   scripts/test.sh unit          Birim testleri: .NET (xUnit), arayuz (Vitest), ML (pytest)
#   scripts/test.sh integration   API entegrasyon testleri (36 betik: KVKK, bordro, izin/vardiya, ise alim,
#                                 performans, kimlik guvenligi, webhook, ML, sohbet/takvim/LLM, onbellek)
#                                 (sahte saglayici sunucusu "chatmock" otomatik baslar; bitince
#                                 servisler normal ayarlarla yeniden olusturulur)
#   scripts/test.sh e2e           Tarayici testleri (Playwright, paralel): tum ekranlar x tum roller, izin akisi
#   scripts/test.sh all           Hepsi
#   scripts/test.sh logs [ZAMAN]  Yalnizca servis gunluklerinde beklenmeyen hata taramasi (ZAMAN: UTC, ornek 2026-10-25T03:00:00Z)
#
# integration, e2e ve all YALNIZCA test ortaminda calisir: HR360_TEST_ENV=1 verilmelidir. Bu testler
# demo sirketine test verisi yazar ve servisleri sahte saglayici katmaniyla (deploy/testing/chat-mock.yml)
# yeniden olusturur; gercek musteri verisi olan bir kurulumda calistirilmamalidir. unit her yerde calisir.
# integration ve e2e sonunda test kalintilari temizlenir (tests/support/cleanup_test_data.py).
# integration ve e2e calisan bir kurulum ister. Test kullanicilari: tests/credentials.json
# (ornek: tests/credentials.example.json) ya da HR360_TEST_USERS. Adres: HR360_BASE_URL.
# Gereken: docker; e2e/integration icin python3 + `pip install pytest playwright pytest-xdist` + `playwright install chromium`
# (pytest-xdist istege bagli: yoksa e2e sirali calisir; HR360_E2E_WORKERS ile islemci sayisi).
set -euo pipefail

cd "$(dirname "$0")/.."
ROOT="$PWD"
what="${1:-unit}"
fail=0
step() { printf '\n\033[1m== %s\033[0m\n' "$*"; }
run() { if "$@"; then echo "OK"; else echo "BASARISIZ: $*"; fail=1; fi; }
# Testlerin canli demo veritabaninda biraktigi kalintilari temizler (tests/support/cleanup_test_data.py).
# Testler basarisiz olsa da calisir; temizlik hatasi ekrana yazilir ama test sonucunu (fail) degistirmez.
cleanup_data() {
  step "Test kalintilari temizligi"
  if ! python3 tests/support/cleanup_test_data.py; then
    echo "UYARI: test kalintilari temizlenemedi (test sonucu etkilenmez)" >&2
  fi
}

# Test katmani; sirlar dosyaya tasindiysa (scripts/secrets-migrate.sh) uretilen compose eki de verilir,
# yoksa -f kullanildigi icin .env'deki COMPOSE_FILE yok sayilir ve servisler sirsiz acilir.
TEST_COMPOSE=(-f docker-compose.yml -f deploy/testing/chat-mock.yml)
if [ -f secrets/compose.secrets.yml ]; then TEST_COMPOSE+=(-f secrets/compose.secrets.yml); fi

if ! declare -F docker >/dev/null && ! docker info >/dev/null 2>&1 && sudo docker info >/dev/null 2>&1; then
  docker() { command sudo docker "$@"; }
fi

unit() {
  step ".NET birim testleri"
  if command -v dotnet >/dev/null 2>&1; then
    for p in tests/dotnet/*/; do run dotnet test "$p" --nologo -v q; done
  else
    run docker run --rm -v "$ROOT:/src" -v hr360-nuget:/root/.nuget/packages -w /src/tests/dotnet \
      mcr.microsoft.com/dotnet/sdk:9.0 sh -c 'for p in */; do dotnet test "$p" --nologo -v q || exit 1; done'
  fi
  step "Arayuz birim testleri (Vitest)"
  (cd apps/web && { [ -d node_modules ] || npm ci --no-audit --no-fund; } && run npx vitest run)
  step "ML servisi (pytest)"
  run docker run --rm -v "$ROOT/apps/ml-inference:/src:ro" python:3.14-slim sh -c \
    "cp -r /src /w && cd /w && grep -E '^(fastapi|starlette|pydantic|httpx|numpy|scikit-learn|prometheus-fastapi-instrumentator|ortools)==' requirements.txt > r.txt && cat requirements-ai.txt requirements-dev.txt >> r.txt && pip install -q -r r.txt >/dev/null 2>&1 && python -m pytest -q -p no:warnings tests"
}

# Servis gunluklerinde hata taramasi. Testler yalnizca API yanitlarina bakar; arka plan islerindeki
# (zamanlayici, kuyruk, gozlemci) hatalar yanita yansimaz ve gozden kacar (ornek: toplu goruntuleme
# denetleyicisi her turda dusuyordu). Beklenen hatalar tests/support/log-allowlist.txt'te (regex, satir basina bir).
log_scan() {
  local since="$1" found=0 c lines patterns
  # Bos desen listesiyle "grep -v -f" her satiri suzer; desen yoksa suzgec uygulanmaz.
  patterns="$(grep -vE '^[[:space:]]*(#|$)' "$ROOT/tests/support/log-allowlist.txt" 2>/dev/null || true)"
  step "Servis gunluklerinde hata taramasi"
  for c in $(docker ps --format '{{.Names}}' | grep -E '^hr360-.*(service|ml-inference)' | sort); do
    # Her hata bir blok (baslik + 3 satir); izin listesindeki bir desen bloğun herhangi bir satırına uyarsa blok atlanır.
    lines="$(docker logs --since "$since" "$c" 2>&1 | grep -A3 -E '^fail: |Unhandled exception|^Traceback' \
      | PATTERNS="$patterns" awk '
          function flush() { if (blk != "" && !allowed) printf "%s", keep; blk = ""; keep = ""; allowed = 0; nl = 0 }
          BEGIN { n = split(ENVIRON["PATTERNS"], pat, "\n") }
          /^--$/ { flush(); next }
          { blk = blk $0 "\n"; nl++; for (i = 1; i <= n; i++) if (pat[i] != "" && $0 ~ pat[i]) allowed = 1
            # Başlığın hemen altındaki ileti satırı da gösterilir (ör. "An error occurred using the connection...").
            if ($0 ~ /^fail: |Exception|Error|^Traceback/ || nl == 2) keep = keep $0 "\n" }
          END { flush() }' || true)"
    if [ -n "$lines" ]; then
      found=1; echo "-- $c"; printf '%s\n' "$lines" | sed 's/^ *//' | cut -c1-200 | sort | uniq -c | sort -rn | head -8
    fi
  done
  if [ "$found" = 0 ]; then echo "OK (beklenmeyen hata yok)"; else echo "BASARISIZ: servis gunluklerinde beklenmeyen hata"; fail=1; fi
}

integration() {
  local t0; t0="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  step "Sahte saglayici sunucusu (Slack, Teams, Google, Microsoft Graph, Zoom, LLM)"
  docker rm -f chatmock >/dev/null 2>&1 || true
  docker run -d --name chatmock --network hr360-net -v "$ROOT/tests/integration:/t:ro" python:3.11-slim \
    sh -c "pip install -q cryptography pyjwt 2>/dev/null && python -u /t/chatmock.py" >/dev/null
  # Test katmani (sahte saglayicilar, kisa is araliklari) tum ilgili servislere ve OpenLDAP test sunucusuna uygulanir.
  docker compose "${TEST_COMPOSE[@]}" --profile ldaptest up -d >/dev/null
  for _ in $(seq 1 60); do docker logs chatmock 2>&1 | grep -q "chatmock :8000" && break; sleep 2; done
  # Yeniden olusturulan servisler ve gateway'in yeni adresleri cozmesi (resolver valid=10s) beklenir:
  # sabit bekleme yetmeyince ilk testler eski IP'ye giden istekte 404 aliyordu. Governance'a ozgu
  # anonim uc art arda 3 kez 200 donene kadar (en fazla ~2 dk) beklenir.
  base="${HR360_BASE_URL:-http://localhost}"; ok=0
  for _ in $(seq 1 60); do
    if [ "$(curl -sk -o /dev/null -w '%{http_code}' "$base/api/governance/ethics/public/demo")" = 200 ]; then
      ok=$((ok + 1)); [ "$ok" -ge 3 ] && break
    else ok=0; fi
    sleep 2
  done
  sleep 10
  # HR360_INTEGRATION_TESTS="test_kvkk test_chat" ile yalnizca secilen betikler calisir.
  local all="test_chat test_calendar test_ai_llm test_cache test_report_lang test_email_lang test_kvkk
           test_payroll_time test_push test_workflow_docs test_kvkk_ops test_payroll_eco test_hr_compliance test_recruitment_plus test_learning_perf test_ops_plus
           test_platform_reports test_notify_prefs test_identity_sign test_paging test_model_card test_chat_plus test_telemetry test_identity_security
           test_time_leave test_kvkk_ml10 test_recruitment_w11 test_offer_esign test_performance_w11 test_learning_w11 test_webhooks_apikeys test_account_provisioning test_ui_prefs
           test_org_scenarios test_password_links test_public_site test_employee_photo test_employee_of_month test_team_calendar test_ideas test_document_compliance test_product_insights"
  for t in ${HR360_INTEGRATION_TESTS:-$all}; do
    step "Entegrasyon: $t"
    run python3 "tests/integration/$t.py"
  done
  # Gunlukler, servisler yeniden olusturulmadan once taranir (sonra kaybolur).
  log_scan "$t0"
  step "Temizlik: servisler normal ayarlarla"
  docker compose up -d >/dev/null
  docker compose "${TEST_COMPOSE[@]}" --profile ldaptest rm -sf openldap >/dev/null 2>&1 || true
  docker rm -f chatmock >/dev/null 2>&1 || true
  cleanup_data
}

# Servisler yeniden olusturulduktan sonra (ornegin integration sonunda) hazir olana kadar beklenir:
# paralel e2e cok hizli basladigi icin ilk istekler 404/502 aliyordu.
wait_ready() {
  local base="${HR360_BASE_URL:-http://localhost}" ok=0 p code all
  for _ in $(seq 1 90); do
    all=1
    for p in /api/governance/ethics/public/demo /api/tenant/public/branding /gateway/health; do
      code="$(curl -sk -o /dev/null -w '%{http_code}' "$base$p")"
      case "$code" in 2*|401|403) ;; *) all=0 ;; esac
    done
    # Kimlik isteyen servisler 401 doner (yonlendirme hazir demektir); 404/502 hazir degil.
    for p in /api/employee/employees /api/leave/leave-requests /api/notification/notifications/ui-prefs /api/workflow/workflows; do
      code="$(curl -sk -o /dev/null -w '%{http_code}' "$base$p")"
      [ "$code" = 401 ] || all=0
    done
    if [ "$all" = 1 ]; then ok=$((ok + 1)); [ "$ok" -ge 3 ] && return 0; else ok=0; fi
    sleep 2
  done
  echo "UYARI: servisler 3 dakikada hazir olmadi" >&2
}

e2e() {
  local t0
  wait_ready
  t0="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  step "Tarayici testleri (Playwright)"
  # pytest-xdist varsa iki asama: (1) paralel testler HR360_E2E_WORKERS (varsayilan 4) islemciyle,
  # (2) kullanicinin sunucudaki ortak durumunu degistiren "serial" isaretliler tek basina.
  # HR360_E2E_WORKERS=0 ya da xdist yoksa eski sirali calistirma. Ayrinti: tests/e2e/conftest.py
  local workers="${HR360_E2E_WORKERS:-4}"
  if [ "$workers" != 0 ] && python3 -c 'import xdist' >/dev/null 2>&1; then
    run python3 -m pytest -q -p no:cacheprovider -n "$workers" --dist load -m "not serial" tests/e2e
    run python3 -m pytest -q -p no:cacheprovider -m serial tests/e2e
  else
    run python3 -m pytest -q -p no:cacheprovider tests/e2e
  fi
  log_scan "$t0"
  cleanup_data
}

# Koruma: canli/uretim kurulumunda yanlislikla entegrasyon/tarayici testi calistirilmasin.
case "$what" in
  integration|e2e|all)
    if [ "${HR360_TEST_ENV:-}" != 1 ]; then
      echo "HATA: '$what' testleri yalnizca test ortaminda calistirilir." >&2
      echo "Bu testler demo sirketine test verisi yazar ve servisleri sahte saglayici ayarlariyla yeniden" >&2
      echo "olusturur (deploy/testing/chat-mock.yml). Gercek verisi olan bir kurulumda CALISTIRMAYIN." >&2
      echo "Test ortamindaysaniz: HR360_TEST_ENV=1 scripts/test.sh $what   (birim testleri: scripts/test.sh unit)" >&2
      exit 2
    fi ;;
esac

case "$what" in
  unit) unit ;;
  integration) integration ;;
  e2e) e2e ;;
  all) unit; integration; e2e ;;
  # Yalnizca servis gunluklerinde hata taramasi (CI temiz kurulum isi; varsayilan: son 1 saat).
  logs) log_scan "${2:-$(date -u -d '-1 hour' +%Y-%m-%dT%H:%M:%SZ)}" ;;
  *) echo "kullanim: scripts/test.sh unit|integration|e2e|all|logs [UTC-ZAMAN]" >&2; exit 2 ;;
esac

echo
if [ "$fail" = 0 ]; then echo "Tum testler gecti."; else echo "Basarisiz test var."; fi
exit "$fail"

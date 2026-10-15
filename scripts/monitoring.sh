#!/usr/bin/env bash
# Izleme yiginini (Prometheus + Grafana + Loki/Promtail + exporter'lar) yonetir.
#
# Kullanim:
#   scripts/monitoring.sh enable     Yigini kurar ve baslatir; Grafana sifresini uretir.
#   scripts/monitoring.sh disable    Konteynerleri durdurur (veriler volume'larda kalir).
#   scripts/monitoring.sh status     Hedeflerin ve alarmlarin ozetini gosterir.
#   scripts/monitoring.sh password   Grafana yonetici sifresini gosterir.
#   scripts/monitoring.sh purge      Durdurur ve metrik/log/iz verilerini siler.
#
# Dagitik izleme (G25, OpenTelemetry -> otel-collector -> Tempo; varsayilan KAPALI):
#   scripts/monitoring.sh tracing on [oran]   .NET servislerini collector'a baglar (oran 0..1, varsayilan 1)
#   scripts/monitoring.sh tracing off         Izlemeyi kapatir (servisler yeniden baslar)
#   scripts/monitoring.sh tracing status
# Izler kisisel veriden arindirilir (servis ici + collector) ve TEMPO_RETENTION
# (varsayilan 72h) sonunda silinir. Grafana > Explore > Tempo.
#
# Alarm kanallari (Alertmanager):
#   scripts/monitoring.sh alerts status
#   scripts/monitoring.sh alerts email ops@sirket.com,it@sirket.com   (SMTP_* ile; Microsoft 365 dahil)
#   scripts/monitoring.sh alerts slack https://hooks.slack.com/services/...
#   scripts/monitoring.sh alerts teams https://....logic.azure.com/workflows/...
#   scripts/monitoring.sh alerts <email|slack|teams> off
#   scripts/monitoring.sh alerts test    Tum kanallara deneme alarmi gonderir
#
# Teams: kanalda "Workflows" > "Post to a channel when a webhook request is received"
# sablonuyla bir akis olusturun ve verdigi adresi kullanin (eski "Incoming Webhook"
# baglayicilari Microsoft tarafindan kapatildi).
# Erisim:
#   Grafana     PUBLIC_ORIGIN/grafana/  (gateway uzerinden, Grafana girisi ister)
#               ya da sunucunun kendisinden http://127.0.0.1:3000/grafana/
#   Prometheus  yalnizca sunucunun kendisinden: http://127.0.0.1:9090
#               (disariya acmak icin SSH tuneli: ssh -L 9090:127.0.0.1:9090 sunucu)
#
# "monitoring" profili .env'deki COMPOSE_PROFILES listesine eklenir; boylece
# sonraki "docker compose up -d" cagrilari da yigini ayakta tutar.
set -euo pipefail

cd "$(dirname "$0")/.."
unset COMPOSE_PROFILES GRAFANA_ADMIN_PASSWORD ALERT_EMAIL_TO ALERT_SLACK_WEBHOOK_URL ALERT_TEAMS_WEBHOOK_URL
ENV_FILE=.env
SERVICES=(prometheus alertmanager postgres-exporter node-exporter blackbox-exporter loki promtail tempo otel-collector grafana)
DOTNET_SERVICES=(organization-service employee-service workflow-service leave-service recruitment-service onboarding-service
  timeshift-service performance-service learning-service engagement-service governance-service compensation-service
  expense-service notification-service tenant-service)
TRACING_ENV=deploy/monitoring/tracing.env

sed_i() { if sed --version >/dev/null 2>&1; then sed -i "$@"; else sed -i '' "$@"; fi; }
die() { echo "HATA: $*" >&2; exit 1; }
[ -f "$ENV_FILE" ] || die ".env bulunamadi; once install.sh calistirin."

if ! declare -F docker >/dev/null && ! docker info >/dev/null 2>&1 && sudo docker info >/dev/null 2>&1; then
  docker() { command sudo docker "$@"; }
fi

set_env() {
  if grep -q "^$1=" "$ENV_FILE"; then sed_i "s#^$1=.*#$1=$2#" "$ENV_FILE"
  else printf '%s=%s\n' "$1" "$2" >> "$ENV_FILE"; fi
}
get_env() { grep "^$1=" "$ENV_FILE" | tail -1 | cut -d= -f2- || true; }
profile_set() { # profile_set NAME on|off  (diger profiller korunur)
  local cur out=""
  cur="$(get_env COMPOSE_PROFILES)"
  for p in ${cur//,/ }; do [ "$p" = "$1" ] || out="${out:+$out,}$p"; done
  [ "$2" = on ] && out="${out:+$out,}$1"
  set_env COMPOSE_PROFILES "$out"
}
gen_password() { LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom | head -c 24 || true; }
origin() { local o; o="$(get_env PUBLIC_ORIGIN)"; [ -n "$o" ] || o="$(get_env PUBLIC_URL)"; echo "${o:-http://localhost}"; }

wait_http() { # wait_http SERVICE PATH PORT
  local i
  for i in $(seq 1 60); do
    if docker compose exec -T "$1" wget -qO- "http://127.0.0.1:$3$2" >/dev/null 2>&1; then return 0; fi
    sleep 2
  done
  return 1
}

cmd="${1:-status}"
case "$cmd" in
  enable)
    if [ -z "$(get_env GRAFANA_ADMIN_PASSWORD)" ]; then
      set_env GRAFANA_ADMIN_PASSWORD "$(gen_password)"
    fi
    profile_set monitoring on
    docker compose --profile monitoring up -d "${SERVICES[@]}"
    # prometheus.yml / alerts.yml degismis olabilir; konteyner yeniden olusmadiysa da okusun.
    docker compose exec -T prometheus wget -qO- --post-data '' http://127.0.0.1:9090/-/reload >/dev/null 2>&1 || true
    # Gateway'in /grafana/ yolu yeni konteyneri hemen bulsun.
    docker compose exec -T gateway nginx -s reload >/dev/null 2>&1 || true
    printf 'Grafana bekleniyor'
    if wait_http grafana /api/health 3000; then echo " hazir."; else echo " (zaman asimi; 'docker compose logs grafana' ile bakin)"; fi
    # Grafana sifresi yalnizca ilk acilista ortam degiskeninden alinir; .env'deki
    # sifre sonradan degistiyse veritabanindakiyle esitlenir.
    docker compose exec -T grafana grafana cli --homepath /usr/share/grafana admin reset-admin-password "$(get_env GRAFANA_ADMIN_PASSWORD)" >/dev/null 2>&1 || true
    echo
    echo "Grafana:    $(origin)/grafana/   (kullanici: admin, sifre: scripts/monitoring.sh password)"
    echo "Prometheus: http://127.0.0.1:9090 (yalnizca sunucudan)"
    echo "Pano:       HR360 > HR360 — Servis sagligi"
    echo "Alarmlar:   scripts/monitoring.sh alerts status"
    echo "Izleme:     scripts/monitoring.sh tracing on   (dagitik izler, varsayilan kapali)"
    ;;
  tracing)
    sub="${2:-status}"
    case "$sub" in
      on)
        ratio="${3:-1}"
        [[ "$ratio" =~ ^(0(\.[0-9]+)?|1(\.0+)?)$ ]] || die "oran 0 ile 1 arasinda olmali (orn. 0.2)"
        case ",$(get_env COMPOSE_PROFILES)," in
          *,monitoring,*) ;;
          *) die "once izleme yiginini acin: scripts/monitoring.sh enable" ;;
        esac
        docker compose --profile monitoring up -d tempo otel-collector
        printf '# scripts/monitoring.sh tracing ile uretildi (G25).\nOTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317\nOTEL_TRACES_SAMPLER_ARG=%s\n' "$ratio" > "$TRACING_ENV"
        docker compose up -d "${DOTNET_SERVICES[@]}"
        echo "Dagitik izleme acik (ornekleme orani: $ratio). Grafana > Explore > Tempo."
        ;;
      off)
        rm -f "$TRACING_ENV"
        docker compose up -d "${DOTNET_SERVICES[@]}"
        echo "Dagitik izleme kapatildi (mevcut izler TEMPO_RETENTION sonunda silinir; hemen silmek icin: purge)."
        ;;
      status)
        if [ -f "$TRACING_ENV" ]; then echo "Izleme: acik ($(grep '^OTEL_TRACES_SAMPLER_ARG=' "$TRACING_ENV" | cut -d= -f2) oraninda)"
        else echo "Izleme: kapali (scripts/monitoring.sh tracing on)"; fi
        echo "Saklama: $(get_env TEMPO_RETENTION | sed 's/^$/72h (varsayilan)/')"
        ;;
      *) die "bilinmeyen: tracing $sub (on [oran] | off | status)" ;;
    esac
    ;;
  alerts)
    sub="${2:-status}"
    mask() { local v="$1"; [ -n "$v" ] && echo "${v:0:32}…" || echo "(kapali)"; }
    apply_alerts() {
      if docker compose --profile monitoring ps --status running --services 2>/dev/null | grep -qx alertmanager; then
        docker compose --profile monitoring up -d alertmanager >/dev/null 2>&1
        echo "Alertmanager yeni ayarla yeniden baslatildi."
      else
        echo "Ayar kaydedildi; izleme acildiginda gecerli olur (scripts/monitoring.sh enable)."
      fi
    }
    case "$sub" in
      status)
        echo "E-posta: $(get_env ALERT_EMAIL_TO | sed 's/^$/(kapali)/')   [SMTP: $(get_env SMTP_HOST | tr -d "'"):$(get_env SMTP_PORT)]"
        echo "Slack:   $(mask "$(get_env ALERT_SLACK_WEBHOOK_URL)")"
        echo "Teams:   $(mask "$(get_env ALERT_TEAMS_WEBHOOK_URL)")"
        ;;
      email)
        v="${3:-}"; [ -n "$v" ] || die "kullanim: alerts email adres1,adres2 | off"
        if [ "$v" = off ]; then v=""; else
          for a in ${v//,/ }; do [[ "$a" =~ ^[^@[:space:]]+@[^@[:space:]]+\.[^@[:space:]]+$ ]] || die "gecersiz e-posta: $a"; done
        fi
        set_env ALERT_EMAIL_TO "$v"; apply_alerts ;;
      slack|teams)
        key=ALERT_SLACK_WEBHOOK_URL; [ "$sub" = teams ] && key=ALERT_TEAMS_WEBHOOK_URL
        v="${3:-}"; [ -n "$v" ] || die "kullanim: alerts $sub <webhook-adresi> | off"
        if [ "$v" = off ]; then v=""; else
          [[ "$v" =~ ^https?://[^[:space:]\'\"#]+$ ]] || die "gecersiz adres: $v"
        fi
        set_env "$key" "$v"; apply_alerts ;;
      test)
        docker compose --profile monitoring ps --status running --services 2>/dev/null | grep -qx alertmanager \
          || die "alertmanager calismiyor (scripts/monitoring.sh enable)"
        now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
        # 2 dk sonra kendiliginden "duzeldi" olur (GNU date; yoksa resolve_timeout = 5 dk).
        ends="$(date -u -d '+2 minutes' +%Y-%m-%dT%H:%M:%SZ 2>/dev/null || true)"
        body='[{"labels":{"alertname":"HR360DenemeAlarmi","service":"monitoring","severity":"warning"},"annotations":{"summary":"Bu bir deneme alarmidir; kanallar calisiyor."},"startsAt":"'"$now"'"'"${ends:+,\"endsAt\":\"$ends\"}"'}]'
        docker compose exec -T alertmanager wget -qO- --header 'Content-Type: application/json' \
          --post-data "$body" http://127.0.0.1:9093/api/v2/alerts >/dev/null
        echo "Deneme alarmi gonderildi; ~30 sn icinde ayarli kanallara ulasir, birkac dakika sonra 'Duzeldi' mesaji gelir."
        ;;
      *) die "bilinmeyen: alerts $sub (status | email | slack | teams | test)" ;;
    esac
    ;;
  disable)
    profile_set monitoring off
    # Collector durunca servislerin iz gondermesi anlamsiz: izlemeyi de kapat.
    if [ -f "$TRACING_ENV" ]; then rm -f "$TRACING_ENV"; docker compose up -d "${DOTNET_SERVICES[@]}" >/dev/null; fi
    docker compose --profile monitoring stop "${SERVICES[@]}" >/dev/null
    docker compose --profile monitoring rm -f "${SERVICES[@]}" >/dev/null
    echo "Izleme durduruldu. Veriler korunuyor (silmek icin: scripts/monitoring.sh purge)."
    ;;
  purge)
    "$0" disable
    for v in prometheus-data grafana-data loki-data alertmanager-data tempo-data; do
      docker volume rm "hr360_$v" >/dev/null 2>&1 && echo "silindi: hr360_$v" || true
    done
    ;;
  password)
    p="$(get_env GRAFANA_ADMIN_PASSWORD)"
    [ -n "$p" ] || die "Izleme henuz etkinlestirilmedi (scripts/monitoring.sh enable)."
    echo "$p"
    ;;
  status)
    case ",$(get_env COMPOSE_PROFILES)," in
      *,monitoring,*) echo "Profil: etkin" ;;
      *) echo "Profil: kapali (scripts/monitoring.sh enable)"; exit 0 ;;
    esac
    docker compose --profile monitoring ps --format '{{.Service}}\t{{.State}}\t{{.Status}}' "${SERVICES[@]}" 2>/dev/null || true
    echo
    if out="$(docker compose exec -T prometheus wget -qO- 'http://127.0.0.1:9090/api/v1/targets?state=active' 2>/dev/null)"; then
      echo "$out" | python3 -c '
import json,sys
t=json.load(sys.stdin)["data"]["activeTargets"]
up=[x for x in t if x["health"]=="up"]
print(f"Hedefler: {len(up)}/{len(t)} ayakta")
for x in t:
    if x["health"]!="up": print("  KAPALI:", x["labels"].get("service", x["scrapeUrl"]), "-", x.get("lastError",""))
' 2>/dev/null || echo "(hedef ozeti icin python3 gerekli)"
      docker compose exec -T prometheus wget -qO- 'http://127.0.0.1:9090/api/v1/alerts' 2>/dev/null | python3 -c '
import json,sys
a=[x for x in json.load(sys.stdin)["data"]["alerts"] if x["state"]=="firing"]
print(f"Aktif alarm: {len(a)}")
for x in a: print("  -", x["labels"]["alertname"], x["labels"].get("service",""), "-", x["annotations"].get("summary",""))
' 2>/dev/null || true
    fi
    ;;
  *)
    die "bilinmeyen komut: $cmd (enable | disable | status | password | purge | alerts | tracing)"
    ;;
esac

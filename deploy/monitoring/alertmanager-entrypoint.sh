#!/bin/sh
# Alertmanager yapilandirmasini konteyner acilirken ortam degiskenlerinden uretir.
# Boylece servis anahtari ve webhook adresleri diske yazilmaz; yalnizca .env'de durur.
# Kanallar: scripts/monitoring.sh alerts ... ile ayarlanir.
#
#   ALERT_EMAIL_TO           Virgullu alici listesi. Alertmanager SMTP'ye baglanmaz: alarm
#                            notification-service'e (webhook) gider ve platformun e-posta
#                            ayariyla (Platform > Sistem ayarlari ya da kurulumdaki SMTP) gonderilir.
#   INTERNAL_SERVICE_TOKEN   notification-service'e kimlik (Authorization: Bearer)
#   ALERT_SLACK_WEBHOOK_URL  Slack "Incoming Webhook" adresi
#   ALERT_TEAMS_WEBHOOK_URL  Microsoft Teams "Workflows" (Power Automate) webhook adresi
#
# Hicbiri tanimli degilse alarmlar yalnizca Alertmanager/Grafana arayuzunde gorunur.
set -eu

OUT=/tmp/alertmanager.yml
q() { printf "'%s'" "$(printf '%s' "$1" | sed "s/'/''/g")"; }

# Alici listesi sorgu parametresine yazilir; adreslerdeki ozel karakterler kodlanir.
urlenc() { printf '%s' "$1" | tr -d ' ' | sed 's/%/%25/g; s/+/%2B/g; s/&/%26/g; s/#/%23/g; s/?/%3F/g'; }

{
  echo "global:"
  echo "  resolve_timeout: 5m"
  cat <<'EOF'

templates:
  - /etc/alertmanager/templates/*.tmpl

route:
  receiver: hr360
  group_by: [alertname, service]
  group_wait: 30s
  group_interval: 5m
  repeat_interval: 4h
  routes:
    - matchers: [severity="critical"]
      receiver: hr360
      repeat_interval: 1h
    # Bilgi duzeyi (ornek: yedek sunucu disinda degil): haftada bir hatirlatma yeter.
    - matchers: [severity="info"]
      receiver: hr360
      repeat_interval: 168h

inhibit_rules:
  # Servis tamamen kapaliyken ayni servisin yavaslik/hata alarmlari susturulur.
  - source_matchers: [alertname="ServisErisilemiyor"]
    target_matchers: [severity="warning"]
    equal: [service]
  # Kritik esik asildiginda ayni olcumun uyari duzeyindeki alarmi tekrar iletilmez.
  - source_matchers: [alertname="Yuksek5xxOraniKritik"]
    target_matchers: [alertname="Yuksek5xxOrani"]
    equal: [service]
  - source_matchers: [alertname="YavasYanitKritik"]
    target_matchers: [alertname="YavasYanit"]
    equal: [service]
  - source_matchers: [alertname="SertifikaBitiyorKritik"]
    target_matchers: [alertname="SertifikaBitiyor"]
  - source_matchers: [alertname="YedekCokEski"]
    target_matchers: [alertname="YedekEski"]

receivers:
  - name: hr360
EOF
  if [ -n "${ALERT_EMAIL_TO:-}" ]; then
    echo "    webhook_configs:"
    echo "      - url: $(q "http://notification-service:8080/api/internal/alerts?to=$(urlenc "$ALERT_EMAIL_TO")")"
    echo "        send_resolved: true"
    echo "        max_alerts: 20"
    echo "        http_config:"
    echo "          authorization:"
    echo "            type: Bearer"
    echo "            credentials: $(q "${INTERNAL_SERVICE_TOKEN:-}")"
  fi
  if [ -n "${ALERT_SLACK_WEBHOOK_URL:-}" ]; then
    echo "    slack_configs:"
    echo "      - api_url: $(q "$ALERT_SLACK_WEBHOOK_URL")"
    echo "        title_link: $(q "${PUBLIC_ORIGIN:-http://localhost}/grafana/alerting/groups?dataSource=alertmanager")"
    echo "        send_resolved: true"
    echo "        title: '{{ template \"hr360.title\" . }}'"
    echo "        text: '{{ template \"hr360.text\" . }}'"
    echo "        color: '{{ if eq .Status \"firing\" }}{{ if eq .CommonLabels.severity \"critical\" }}danger{{ else }}warning{{ end }}{{ else }}good{{ end }}'"
  fi
  if [ -n "${ALERT_TEAMS_WEBHOOK_URL:-}" ]; then
    echo "    msteamsv2_configs:"
    echo "      - webhook_url: $(q "$ALERT_TEAMS_WEBHOOK_URL")"
    echo "        send_resolved: true"
    echo "        title: '{{ template \"hr360.title\" . }}'"
    echo "        text: '{{ template \"hr360.text\" . }}'"
  fi
} > "$OUT"

# CI/elle dogrulama: "entrypoint.sh check" yalnizca uretilen yapilandirmayi denetler.
if [ "${1:-}" = "check" ]; then exec /bin/amtool check-config "$OUT"; fi

exec /bin/alertmanager --config.file="$OUT" --storage.path=/alertmanager "$@"

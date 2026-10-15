#!/bin/sh
# Alertmanager yapilandirmasini konteyner acilirken ortam degiskenlerinden uretir.
# Boylece SMTP parolasi ve webhook adresleri diske yazilmaz; yalnizca .env'de durur.
# Kanallar: scripts/monitoring.sh alerts ... ile ayarlanir.
#
#   ALERT_EMAIL_TO           Virgullu alici listesi (SMTP_* ayarlariyla gonderilir;
#                            Microsoft 365 icin SMTP_HOST=smtp.office365.com, port 587)
#   ALERT_SLACK_WEBHOOK_URL  Slack "Incoming Webhook" adresi
#   ALERT_TEAMS_WEBHOOK_URL  Microsoft Teams "Workflows" (Power Automate) webhook adresi
#
# Hicbiri tanimli degilse alarmlar yalnizca Alertmanager/Grafana arayuzunde gorunur.
set -eu

OUT=/tmp/alertmanager.yml
q() { printf "'%s'" "$(printf '%s' "$1" | sed "s/'/''/g")"; }

SMTP_PORT="${SMTP_PORT:-25}"
# 587 (STARTTLS) ve 465 (dogrudan TLS) disindaki portlar sifresiz kabul edilir
# (Mailpit 1025, ic aga acik role 25). Go'nun SMTP istemcisi sifresiz baglantida
# parola gondermeyi reddettigi icin o durumda kimlik bilgisi de verilmez.
case "$SMTP_PORT" in 465|587) TLS=true ;; *) TLS=false ;; esac

{
  echo "global:"
  echo "  resolve_timeout: 5m"
  if [ -n "${ALERT_EMAIL_TO:-}" ]; then
    echo "  smtp_smarthost: $(q "${SMTP_HOST:-localhost}:$SMTP_PORT")"
    echo "  smtp_from: $(q "${SMTP_FROM_NAME:-HR360} <${SMTP_FROM_ADDRESS:-noreply@localhost}>")"
    echo "  smtp_require_tls: $TLS"
    if [ "$TLS" = true ] && [ -n "${SMTP_USER:-}" ]; then
      echo "  smtp_auth_username: $(q "$SMTP_USER")"
      echo "  smtp_auth_password: $(q "${SMTP_PASSWORD:-}")"
    fi
  fi
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
    echo "    email_configs:"
    echo "$ALERT_EMAIL_TO" | tr ',' '\n' | while read -r to; do
      to="$(echo "$to" | tr -d ' ')"
      [ -n "$to" ] || continue
      echo "      - to: $(q "$to")"
      echo "        send_resolved: true"
      echo "        headers: { Subject: '{{ template \"hr360.title\" . }}' }"
      echo "        html: '{{ template \"hr360.email\" . }}'"
    done
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

#!/usr/bin/env bash
# Duzenli isler icin systemd zamanlayicisi (ya da cron) kurar. Varsayilan komut "print":
# yalnizca uretilecek birimleri gosterir, hicbir sey yazmaz.
#
# Kullanim:
#   scripts/schedule.sh [print] [IS...] [secenekler]     Birimleri goster (varsayilan: tum isler)
#   scripts/schedule.sh install IS... [secenekler]       Kur ve etkinlestir (root ya da sudo gerekir)
#   scripts/schedule.sh remove IS... [--cron]            Kaldir
#   scripts/schedule.sh status                           Kurulu zamanlayicilar
#
# Isler:
#   backup         scripts/backup.sh (varsayilan her gece 03:15). .env'de BACKUP_MINIO_URL ya da
#                  BACKUP_MINIO_BUCKET tanimliysa (ya da --to-minio verilirse) --to-minio ile.
#   restore-drill  scripts/restore-drill.sh (varsayilan Pazar 05:00): son yedegi gecici
#                  konteynerde dener; basarisizsa birim "failed" olur (systemctl --failed).
#   zap            scripts/security-scan.sh zap (varsayilan Cumartesi 02:00): OWASP ZAP temel taramasi.
#
# Secenekler:
#   --at SS:DD       Saat (ise ozgu varsayilanin yerine)
#   --day GUN        Haftanin gunu (Mon..Sun); backup icin verilirse haftalik olur
#   --keep N         backup: tutulacak yedek sayisi (varsayilan 14)
#   --to-minio       backup: degismez kopyayi her zaman gonder
#   --cron           systemd yerine kullanicinin crontab'ina yaz
#   --user AD        Isi calistiracak kullanici (varsayilan: repo dizininin sahibi; docker erisimi olmali)
#   --unit-dir DIZIN systemd birim dizini (varsayilan /etc/systemd/system)
#
# Ciktilar <repo>/backups/<is>.log dosyasina eklenir (systemd: ayrica journalctl -u hr360-<is>).
# Not: scripts/backup.sh schedule (eski cron yolu) ile ayni anda kurmayin; cift yedek alinir.
set -euo pipefail

cd "$(dirname "$0")/.."
REPO="$(pwd)"
die() { echo "HATA: $*" >&2; exit 1; }

CMD=print
case "${1:-}" in print|install|remove|status) CMD="$1"; shift ;; esac
JOBS=(); AT=""; DAY=""; KEEP=14; TO_MINIO=0; CRON=0; RUN_USER=""; UNIT_DIR=/etc/systemd/system
while [ $# -gt 0 ]; do
  case "$1" in
    --at) AT="$2"; shift 2 ;;
    --day) DAY="$2"; shift 2 ;;
    --keep) KEEP="$2"; shift 2 ;;
    --to-minio) TO_MINIO=1; shift ;;
    --cron) CRON=1; shift ;;
    --user) RUN_USER="$2"; shift 2 ;;
    --unit-dir) UNIT_DIR="$2"; shift 2 ;;
    -h|--help) sed -n '2,29p' "$0"; exit 0 ;;
    -*) die "bilinmeyen secenek: $1" ;;
    backup|restore-drill|zap) JOBS+=("$1"); shift ;;
    *) die "bilinmeyen is: $1 (backup, restore-drill, zap)" ;;
  esac
done
[ "${#JOBS[@]}" -gt 0 ] || { [ "$CMD" = print ] || [ "$CMD" = status ] || die "en az bir is verin (backup, restore-drill, zap)"; JOBS=(backup restore-drill zap); }
[ -z "$AT" ] || [[ "$AT" =~ ^([01]?[0-9]|2[0-3]):[0-5][0-9]$ ]] || die "--at SS:DD biciminde olmali (ornek 03:15)"
case "$DAY" in ""|Mon|Tue|Wed|Thu|Fri|Sat|Sun) ;; *) die "--day Mon..Sun olmali" ;; esac
[[ "$KEEP" =~ ^[0-9]+$ ]] || die "--keep sayi olmali"
[ -n "$RUN_USER" ] || RUN_USER="$(stat -c %U "$REPO")"

envval() { [ -f .env ] && grep -E "^$1=" .env | tail -1 | cut -d= -f2- || true; }
as_root() {
  if [ "$(id -u)" = 0 ] || [ "$UNIT_DIR" != /etc/systemd/system ]; then "$@"
  elif command -v sudo >/dev/null 2>&1; then sudo "$@"
  else die "root yetkisi gerekli (sudo yok)"; fi
}

# is -> "takvim|komut"
job_spec() {
  local j="$1" at day cmd
  case "$j" in
    backup)
      at="${AT:-03:15}"; day="$DAY"
      cmd="scripts/backup.sh --keep $KEEP"
      if [ "$TO_MINIO" = 1 ] || [ -n "$(envval BACKUP_MINIO_URL)" ] || [ -n "$(envval BACKUP_MINIO_BUCKET)" ]; then cmd="$cmd --to-minio"; fi ;;
    restore-drill) at="${AT:-05:00}"; day="${DAY:-Sun}"; cmd="scripts/restore-drill.sh" ;;
    zap) at="${AT:-02:00}"; day="${DAY:-Sat}"; cmd="scripts/security-scan.sh zap" ;;
  esac
  printf '%s|%s|%s\n' "$day" "$at" "$cmd"
}

service_unit() {
  local j="$1" cmd="$2"
  cat <<EOF
[Unit]
Description=HR360 $j (scripts/schedule.sh)
Wants=docker.service
After=docker.service

[Service]
Type=oneshot
User=$RUN_USER
WorkingDirectory=$REPO
ExecStart=/bin/bash -c 'mkdir -p backups && exec $cmd >> backups/$j.log 2>&1'
Nice=10
EOF
}
timer_unit() {
  local j="$1" day="$2" at="$3"
  cat <<EOF
[Unit]
Description=HR360 $j zamanlayicisi (scripts/schedule.sh)

[Timer]
OnCalendar=${day:+$day }*-*-* $at:00
Persistent=true
RandomizedDelaySec=300

[Install]
WantedBy=timers.target
EOF
}
cron_line() {
  local j="$1" day="$2" at="$3" cmd="$4" h m dow="*"
  h="${at%%:*}"; m="${at##*:}"; h="${h#0}"; m="${m#0}"
  case "$day" in Sun) dow=0;; Mon) dow=1;; Tue) dow=2;; Wed) dow=3;; Thu) dow=4;; Fri) dow=5;; Sat) dow=6;; esac
  echo "${m:-0} ${h:-0} * * $dow cd $REPO && mkdir -p backups && $cmd >> backups/$j.log 2>&1 # hr360-schedule:$j"
}

if [ "$CMD" = status ]; then
  if command -v systemctl >/dev/null 2>&1; then systemctl list-timers --all 'hr360-*' 2>/dev/null || true; fi
  if command -v crontab >/dev/null 2>&1; then crontab -l 2>/dev/null | grep -E '# hr360-(schedule|backup)' || echo "cron: kayit yok"; fi
  exit 0
fi

for j in "${JOBS[@]}"; do
  IFS='|' read -r day at cmd <<< "$(job_spec "$j")"
  case "$CMD" in
    print)
      if [ "$CRON" = 1 ]; then cron_line "$j" "$day" "$at" "$cmd"
      else
        echo "# ---- $UNIT_DIR/hr360-$j.service"; service_unit "$j" "$cmd"
        echo "# ---- $UNIT_DIR/hr360-$j.timer"; timer_unit "$j" "$day" "$at"; echo
      fi ;;
    install)
      if [ "$j" = backup ] && command -v crontab >/dev/null 2>&1 && crontab -l 2>/dev/null | grep -q '# hr360-backup'; then
        echo "UYARI: 'scripts/backup.sh schedule' ile kurulmus cron yedegi var; cift yedek olmamasi icin 'scripts/backup.sh unschedule' calistirin." >&2
      fi
      if [ "$CRON" = 1 ]; then
        command -v crontab >/dev/null 2>&1 || die "crontab bulunamadi"
        current="$(crontab -l 2>/dev/null | grep -v "# hr360-schedule:$j\$" || true)"
        printf '%s\n%s\n' "$current" "$(cron_line "$j" "$day" "$at" "$cmd")" | sed '/^$/d' | crontab -
        echo "cron: $j kuruldu ($([ -n "$day" ] && echo "$day ")$at)"
      else
        service_unit "$j" "$cmd" | as_root tee "$UNIT_DIR/hr360-$j.service" >/dev/null
        timer_unit "$j" "$day" "$at" | as_root tee "$UNIT_DIR/hr360-$j.timer" >/dev/null
        as_root systemctl daemon-reload
        as_root systemctl enable --now "hr360-$j.timer" >/dev/null
        echo "systemd: hr360-$j.timer etkin ($([ -n "$day" ] && echo "$day ")$at; kullanici $RUN_USER)"
      fi ;;
    remove)
      if [ "$CRON" = 1 ]; then
        current="$(crontab -l 2>/dev/null | grep -v "# hr360-schedule:$j\$" || true)"
        printf '%s\n' "$current" | sed '/^$/d' | crontab -
        echo "cron: $j kaldirildi"
      else
        as_root systemctl disable --now "hr360-$j.timer" >/dev/null 2>&1 || true
        as_root rm -f "$UNIT_DIR/hr360-$j.service" "$UNIT_DIR/hr360-$j.timer"
        as_root systemctl daemon-reload
        echo "systemd: hr360-$j kaldirildi"
      fi ;;
  esac
done

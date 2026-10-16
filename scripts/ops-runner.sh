#!/usr/bin/env bash
# Arayüzden yedek/geri yükleme/güncelleme için bakım çalıştırıcısını (scripts/ops-runner.py) kurar.
# Kullanıcının crontab'ına dakikada bir çalışan tek satır ekler; root gerekmez (kullanıcının docker
# erişimi olmalı). Kurulumdan sonra bu işler Platform > Bakım ekranından yapılır.
#
# Kullanim:
#   scripts/ops-runner.sh install     Kur (zaten kuruluysa dokunmaz)
#   scripts/ops-runner.sh uninstall   Kaldır
#   scripts/ops-runner.sh status      Kurulu mu, son tur ne zaman
#   scripts/ops-runner.sh run         Bir turu hemen çalıştır
set -euo pipefail

cd "$(dirname "$0")/.."
REPO="$(pwd)"
MARK="# hr360-ops-runner"
LINE="* * * * * cd '$REPO' && /usr/bin/env python3 scripts/ops-runner.py >> '$REPO/ops-logs/runner.log' 2>&1 $MARK"

case "${1:-status}" in
  install)
    mkdir -p ops-logs
    if crontab -l 2>/dev/null | grep -qF "$MARK"; then echo "Bakım çalıştırıcısı zaten kurulu."; exit 0; fi
    { crontab -l 2>/dev/null || true; echo "$LINE"; } | crontab -
    echo "Kuruldu: dakikada bir çalışır. Günlük: ops-logs/runner.log" ;;
  uninstall)
    { crontab -l 2>/dev/null || true; } | grep -vF "$MARK" | crontab -
    echo "Kaldırıldı." ;;
  status)
    if crontab -l 2>/dev/null | grep -qF "$MARK"; then echo "Kurulu (cron, dakikada bir)"; else echo "Kurulu değil (scripts/ops-runner.sh install)"; fi
    [ -f ops-logs/.runner.lock ] && echo "Son tur: $(date -r ops-logs/.runner.lock '+%F %T')" || true ;;
  run)
    mkdir -p ops-logs
    exec python3 scripts/ops-runner.py ;;
  *) echo "Kullanım: $0 install|uninstall|status|run" >&2; exit 2 ;;
esac

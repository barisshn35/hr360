#!/usr/bin/env bash
# Veritabani yalitimi (varsayilan ACIK): servis basina en az yetkili rol (scripts/db-roles.sh) ve kiraci
# basina satir duzeyi guvenlik (RLS, scripts/sql/2026-10-20_rls.sql). install.sh ve update.sh cagirir.
#
#   scripts/db-isolation.sh ensure [--recreate]   roller + yetkiler + parolalar, servis secimi (.env), RLS (yeni tablolar dahil)
#   scripts/db-isolation.sh off      servisler hr360admin'e doner, RLS kapanir, .env'e HR360_DB_ISOLATION=off
#   scripts/db-isolation.sh status
#
# .env'de HR360_DB_ISOLATION=off ise ensure hicbir sey yapmaz (bilerek kapatilmis kurulum).
# ensure'den sonra servisler yeniden olusturulmalidir (docker compose up -d); cagiran betik yapar.
set -euo pipefail
cd "$(dirname "$0")/.."
ENV_FILE="${ENV_FILE:-.env}"

psql_admin() { docker compose exec -T postgres psql -X -At -v ON_ERROR_STOP=1 -U hr360admin -d hr360_operational -c "$1"; }
disabled() { grep -qE '^HR360_DB_ISOLATION=off\s*$' "$ENV_FILE" 2>/dev/null; }

case "${1:-}" in
  ensure)
    if disabled; then echo "Veritabani yalitimi kapali (HR360_DB_ISOLATION=off); atlandi."; exit 0; fi
    [ -f deploy/postgres/roles.sql ] || { echo "deploy/postgres/roles.sql yok; yalitim atlandi." >&2; exit 0; }
    scripts/db-roles.sh apply >/dev/null
    scripts/db-roles.sh enable all >/dev/null
    n="$(psql_admin "SELECT hr360_rls_enable()")"
    echo "Veritabani yalitimi acik: servis rolleri + RLS ($n tablo)."
    # --recreate: servisler yeni baglanti ayarlariyla yeniden olusturulur (Platform > Bakim'dan acilinca).
    if [ "${2:-}" = --recreate ]; then docker compose up -d >/dev/null && echo "Servisler yeniden olusturuldu."; fi
    ;;
  off)
    scripts/db-roles.sh disable all >/dev/null
    psql_admin "SELECT hr360_rls_disable()" >/dev/null
    grep -q '^HR360_DB_ISOLATION=' "$ENV_FILE" 2>/dev/null \
      && sed -i 's/^HR360_DB_ISOLATION=.*/HR360_DB_ISOLATION=off/' "$ENV_FILE" \
      || echo "HR360_DB_ISOLATION=off" >> "$ENV_FILE"
    echo "Veritabani yalitimi kapatildi; servisleri yeniden olusturun: docker compose up -d"
    ;;
  status)
    disabled && echo "Ayar: kapali (HR360_DB_ISOLATION=off)" || echo "Ayar: acik (varsayilan)"
    scripts/db-roles.sh status | tail -n +1
    ;;
  *) echo "kullanim: $0 ensure|off|status" >&2; exit 2 ;;
esac

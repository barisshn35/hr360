#!/usr/bin/env bash
# Yavas sorgu incelemesi (pg_stat_statements). Varsayilan KAPALIDIR; once eklentinin
# yuklenmesi gerekir (postgres yeniden baslar):
#   docker compose -f docker-compose.yml -f deploy/postgres/pg-stat-statements.yml up -d postgres
#
# Kullanim:
#   scripts/slow-queries.sh status        Eklenti yuklu/etkin mi
#   scripts/slow-queries.sh enable        CREATE EXTENSION pg_stat_statements (hr360_operational)
#   scripts/slow-queries.sh [top] [N]     Toplam sureye gore ilk N sorgu (varsayilan 20)
#   scripts/slow-queries.sh mean [N]      Ortalama sureye gore ilk N sorgu (en az 20 cagri)
#   scripts/slow-queries.sh seqscan [N]   En cok ardisik tarama yapan tablolar (indeks adayi)
#   scripts/slow-queries.sh reset         Istatistikleri sifirlar
#
# Yalnizca okur (enable/reset disinda). Sorgu metinleri normalize edilmistir ($1, $2 ...);
# cikti yine de sunucuda kalmali, repoya konmamalidir. Ayrinti: docs/runbooks/yavas-sorgu.md
set -euo pipefail
cd "$(dirname "$0")/.."

CID="$(docker compose ps -q postgres 2>/dev/null || true)"
[ -n "$CID" ] || { echo "postgres konteyneri calismiyor" >&2; exit 1; }
psql_q() { docker exec -i "$CID" psql -U hr360admin -d hr360_operational -v ON_ERROR_STOP=1 -P pager=off "$@"; }

loaded() { [ "$(psql_q -Atc "select count(*) from pg_settings where name='shared_preload_libraries' and setting like '%pg_stat_statements%'" | tr -d '\r')" = "1" ]; }
installed() { [ "$(psql_q -Atc "select count(*) from pg_extension where extname='pg_stat_statements'" | tr -d '\r')" = "1" ]; }
need() {
  loaded || { echo "pg_stat_statements yuklu degil. Once: docker compose -f docker-compose.yml -f deploy/postgres/pg-stat-statements.yml up -d postgres" >&2; exit 1; }
  installed || { echo "Eklenti olusturulmamis. Once: scripts/slow-queries.sh enable" >&2; exit 1; }
}
num() { case "${1:-}" in ''|*[!0-9]*) echo "${2}";; *) echo "$1";; esac; }

cmd="${1:-top}"
case "$cmd" in
  status)
    if loaded; then echo "shared_preload_libraries: pg_stat_statements YUKLU"; else echo "shared_preload_libraries: yuklu DEGIL"; fi
    if installed; then echo "eklenti: OLUSTURULMUS"; else echo "eklenti: olusturulmamis"; fi
    psql_q -Atc "select 'log_min_duration_statement: '||setting||' ms' from pg_settings where name='log_min_duration_statement'"
    ;;
  enable)
    loaded || { echo "Once eklentiyi yukleyin: docker compose -f docker-compose.yml -f deploy/postgres/pg-stat-statements.yml up -d postgres" >&2; exit 1; }
    psql_q -qc "CREATE EXTENSION IF NOT EXISTS pg_stat_statements"
    echo "pg_stat_statements etkin."
    ;;
  reset)
    need; psql_q -qAtc "select pg_stat_statements_reset()" >/dev/null; echo "Istatistikler sifirlandi."
    ;;
  top|mean)
    need; n="$(num "${2:-}" 20)"
    if [ "$cmd" = top ]; then order="total_exec_time"; filt="true"; else order="mean_exec_time"; filt="calls >= 20"; fi
    psql_q -c "
      select round(total_exec_time::numeric/1000, 1) as toplam_sn, calls as cagri,
             round(mean_exec_time::numeric, 2) as ort_ms, round(max_exec_time::numeric, 1) as en_cok_ms,
             rows as satir, shared_blks_read as disk_blok,
             left(regexp_replace(query, '\s+', ' ', 'g'), 160) as sorgu
      from pg_stat_statements s join pg_database d on d.oid = s.dbid
      where d.datname = 'hr360_operational' and $filt
      order by $order desc limit $n"
    ;;
  seqscan)
    n="$(num "${2:-}" 20)"
    psql_q -c "
      select relname as tablo, n_live_tup as satir, seq_scan as ardisik_tarama, seq_tup_read as okunan_satir,
             coalesce(idx_scan, 0) as indeks_tarama
      from pg_stat_user_tables where seq_scan > 0
      order by seq_tup_read desc limit $n"
    ;;
  *)
    sed -n '2,15p' "$0"; exit 1;;
esac

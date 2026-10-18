#!/usr/bin/env bash
# scripts/sql goclerini kayit defteriyle (schema_migrations: ad + sha256) uygular. install.sh ve update.sh cagirir.
#
#   scripts/migrate.sh          yeni ya da degisen ilk dosyadan itibaren SONRAKI TUM dosyalar uygulanir
#   scripts/migrate.sh --all    hepsi yeniden uygulanir (dosyalar idempotent)
#   scripts/migrate.sh status   uygulanmamis / degismis dosyalari listeler
#
# Neden "ilk degisenden itibaren hepsi": bazi dosyalar oncekilerin fonksiyonlarini yeniden tanimlar
# (or. 2026-10-31_rls_shared_rows, 2026-10-20_rls'in hr360_rls_apply_policies'ini). Yalnizca eski dosya
# yeniden uygulanirsa sonraki tanimi ezer; sirayi korumak icin arkasindakiler de uygulanir.
set -euo pipefail
cd "$(dirname "$0")/.."

psql_q() { docker compose exec -T -e PGOPTIONS="-c client_min_messages=warning" postgres psql -X -U hr360admin -d hr360_operational -v ON_ERROR_STOP=1 -q "$@"; }
sum() { if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1"; else shasum -a 256 "$1"; fi | cut -d' ' -f1; }

psql_q -c 'CREATE TABLE IF NOT EXISTS schema_migrations ("Name" text PRIMARY KEY, "Checksum" text NOT NULL, "AppliedAt" timestamptz NOT NULL DEFAULT now())' >/dev/null
have="$(psql_q -At -c 'SELECT "Name" || chr(124) || "Checksum" FROM schema_migrations')"

mode="${1:-}"
started=0; applied=0
for f in scripts/sql/*.sql; do
  [ -f "$f" ] || continue
  n="$(basename "$f")"; c="$(sum "$f")"
  if [ "$started" = 0 ] && [ "$mode" != --all ] && printf '%s\n' "$have" | grep -qxF "$n|$c"; then continue; fi
  started=1
  if [ "$mode" = status ]; then echo "bekliyor: $n"; continue; fi
  psql_q -f - < "$f" >/dev/null || { echo "Goc betigi basarisiz: $f" >&2; exit 1; }
  printf '%s\n' 'INSERT INTO schema_migrations ("Name","Checksum") VALUES (:'"'"'n'"'"', :'"'"'c'"'"')
    ON CONFLICT ("Name") DO UPDATE SET "Checksum" = EXCLUDED."Checksum", "AppliedAt" = now();' \
    | psql_q -v n="$n" -v c="$c" -f - >/dev/null
  applied=$((applied + 1))
done
[ "$mode" = status ] && { [ "$started" = 0 ] && echo "Tum gocler uygulanmis."; exit 0; }
echo "Goc: $applied dosya uygulandi."

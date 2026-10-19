#!/usr/bin/env bash
# Anlik geri donus (PITR): PostgreSQL WAL arsivi + temel yedek ile veritabanini gecmisteki herhangi bir
# ana (en fazla ~5 dk kayipla) geri dondurur. Gecelik pg_dump yedegini (scripts/backup.sh) TAMAMLAR,
# yerine gecmez: pg_dump disk kaybina karsi (dis depo), PITR yanlis silme/toplu hatali isleme karsidir.
#
# Kullanim:
#   scripts/pitr.sh status                  Acik mi, arsiv boyutu, son arsivlenen, geri donulebilir aralik
#   scripts/pitr.sh enable [--keep N]       Arsivlemeyi ac ve ilk temel yedegi al
#   scripts/pitr.sh disable [--purge]       Arsivlemeyi kapat (--purge: arsiv ve temel yedekleri de sil)
#   scripts/pitr.sh base [--keep N]         Temel yedek al; en yeni N tanesi kalir (varsayilan 2), daha
#                                           eskilerin ihtiyac duydugu WAL parcalari silinir
#   scripts/pitr.sh restore --to "YYYY-AA-GG SS:DD[:ss]" --yes
#                                           Tum veritabanlarini (uygulama, Keycloak, MLflow) o ana dondurur.
#                                           Saat Europe/Istanbul. Mevcut veri dizini pitr/pre-restore-* olarak
#                                           saklanir (geri alinabilir; disk acmak icin "cleanup").
#   scripts/pitr.sh cleanup                 Eski veri dizini kopyalarini (pre-restore-*) siler
#
# Nasil calisir: postgres archive_mode=on ve archive_command=deploy/postgres/archive-wal.sh ile baslar;
# betik yalnizca pitr/wal/ENABLED varken WAL kopyalar (acip kapamak yeniden baslatma istemez).
# archive_timeout=300: en kotu durumda son 5 dakika kaybolur. Arsiv ve temel yedekler postgres
# hacmindedir (/var/lib/postgresql/pitr); disk kaybina karsi gecelik sifreli yedek ve dis depo surer.
# Gecelik yedek (scripts/backup.sh) PITR aciksa temel yedegi de yeniler.
#
# KVKK: geri donulen andan sonra imha edilmis kayitlar geri gelebilir; restore sonunda saklama
# politikalari yeniden calistirilir (scripts/restore.sh ile ayni).
set -euo pipefail

cd "$(dirname "$0")/.."
. scripts/lib/backup-common.sh

ROOT_IN=/var/lib/postgresql/pitr
WAL_IN=$ROOT_IN/wal
BASE_IN=$ROOT_IN/base

pg() { docker compose exec -T postgres "$@"; }
psql_q() { pg psql -U hr360admin -d postgres -Atq -v ON_ERROR_STOP=1 -c "$1"; }
in_pg() { pg sh -c "$1"; }

die() { echo "HATA: $*" >&2; exit 1; }

need_running() {
  docker compose exec -T postgres pg_isready -U hr360admin -d postgres >/dev/null 2>&1 \
    || die "postgres calismiyor (docker compose up -d postgres)."
}

# archive_mode kapaliysa (eski compose ile baslamis) arsivleme hic calismaz.
need_archive_mode() {
  [ "$(psql_q "SHOW archive_mode")" = "on" ] \
    || die "postgres archive_mode=on ile baslamamis. Guncel docker-compose.yml ile 'docker compose up -d postgres' (kisa kesinti) gerekir."
}

is_enabled() { in_pg "test -f $WAL_IN/ENABLED" 2>/dev/null; }

keep_arg() { # --keep N (varsayilan 2)
  local k=2
  while [ $# -gt 0 ]; do
    case "$1" in --keep) k="${2:?--keep sayi ister}"; shift 2 ;; *) die "bilinmeyen secenek: $1" ;; esac
  done
  [[ "$k" =~ ^[1-9][0-9]?$ ]] || die "--keep 1-99 olmali"
  echo "$k"
}

cmd_base() {
  local keep; keep="$(keep_arg "$@")"
  need_running; need_archive_mode
  is_enabled || die "PITR kapali (scripts/pitr.sh enable)."
  local ts; ts="$(date -u +%Y%m%dT%H%M%SZ)"
  echo "==> Temel yedek: $ts"
  # -X fetch: tutarlilik icin gereken WAL de arsive konur; -Ft -z: sikistirilmis tar.
  in_pg "mkdir -p $BASE_IN/$ts.tmp && pg_basebackup -U hr360admin -D $BASE_IN/$ts.tmp -Ft -z -X fetch -c fast --no-manifest -l hr360-$ts"
  # Yedegin bitis ani ve ilk WAL parcasi (backup_label): geri donus hedefi bitisten sonra olmali; o parcadan
  # onceki WAL bu yedek icin gereksizdir.
  in_pg "seg=\$(tar xzf $BASE_IN/$ts.tmp/base.tar.gz -O backup_label | sed -n 's/^START WAL LOCATION: .*(file \\([0-9A-F]*\\))\$/\\1/p');
         [ -n \"\$seg\" ] || exit 1;
         echo \"\$seg\" > $BASE_IN/$ts.tmp/LABEL && date -u +%Y-%m-%dT%H:%M:%SZ > $BASE_IN/$ts.tmp/FINISHED &&
         mv $BASE_IN/$ts.tmp $BASE_IN/$ts" || die "temel yedekte backup_label okunamadi"
  # Eskileri sil; en eski kalan temel yedekten oncesine ait WAL gereksizdir.
  in_pg "cd $BASE_IN && ls -d 2*Z 2>/dev/null | sort -r | tail -n +$((keep + 1)) | xargs -r rm -rf;
         rm -rf $BASE_IN/*.tmp;
         oldest=\$(ls -d 2*Z | sort | head -1); [ -n \"\$oldest\" ] && pg_archivecleanup -x .gz $WAL_IN \"\$(cat \$oldest/LABEL)\""
  echo "Tamam: $(in_pg "du -sh $BASE_IN/$ts | cut -f1") (en yeni $keep temel yedek tutulur)"
}

cmd_enable() {
  local keep; keep="$(keep_arg "$@")"
  need_running; need_archive_mode
  in_pg "mkdir -p $WAL_IN $BASE_IN && chown -R postgres:postgres $ROOT_IN && chmod 700 $ROOT_IN && touch $WAL_IN/ENABLED"
  echo "WAL arsivlemesi acik."
  cmd_base --keep "$keep"
}

cmd_disable() {
  need_running
  in_pg "rm -f $WAL_IN/ENABLED"
  if [ "${1:-}" = "--purge" ]; then
    in_pg "rm -rf $WAL_IN $BASE_IN"
    echo "PITR kapatildi; arsiv ve temel yedekler silindi."
  else
    echo "PITR kapatildi (arsiv duruyor; silmek icin: scripts/pitr.sh disable --purge)."
  fi
}

cmd_status() {
  need_running
  local mode; mode="$(psql_q "SHOW archive_mode")"
  if [ "$mode" != "on" ]; then echo "durum=hazir-degil (archive_mode kapali; postgres yeniden baslatilmali)"; return 0; fi
  if is_enabled; then echo "durum=acik"; else echo "durum=kapali"; fi
  in_pg "[ -d $WAL_IN ] || exit 0
         echo \"wal_dosya=\$(ls $WAL_IN | grep -c '\\.gz\$' || true)\"
         echo \"wal_boyut=\$(du -sh $WAL_IN | cut -f1)\"
         echo \"temel_boyut=\$(du -sh $BASE_IN 2>/dev/null | cut -f1)\"
         for b in \$(ls -d $BASE_IN/2*Z 2>/dev/null | sort); do echo \"temel=\$(basename \$b) bitis=\$(cat \$b/FINISHED)\"; done
         for d in \$(ls -d $ROOT_IN/pre-restore-* 2>/dev/null); do echo \"eski_veri=\$(basename \$d) \$(du -sh \$d | cut -f1)\"; done"
  psql_q "SELECT 'son_arsiv=' || coalesce(last_archived_wal,'-') || ' ' || coalesce(to_char(last_archived_time AT TIME ZONE 'UTC','YYYY-MM-DD\"T\"HH24:MI:SS\"Z\"'),'-')
          || E'\nhata_sayisi=' || failed_count || coalesce(' son_hata=' || last_failed_wal, '') FROM pg_stat_archiver"
}

# Istanbul saatini UTC ISO'ya cevirir (GNU date). Gecersizse bos.
to_utc() { date -u -d "TZ=\"Europe/Istanbul\" $1" +%Y-%m-%dT%H:%M:%SZ 2>/dev/null || true; }

cmd_restore() {
  local target="" yes=0
  while [ $# -gt 0 ]; do
    case "$1" in
      --to) target="${2:?--to tarih ister}"; shift 2 ;;
      --yes) yes=1; shift ;;
      *) die "bilinmeyen secenek: $1" ;;
    esac
  done
  [ -n "$target" ] || die "--to \"YYYY-AA-GG SS:DD\" gerekli (Istanbul saati)."
  [[ "$target" =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}[\ T][0-9]{2}:[0-9]{2}(:[0-9]{2})?$ ]] || die "tarih bicimi: YYYY-AA-GG SS:DD[:ss]"
  local utc; utc="$(to_utc "$target")"; [ -n "$utc" ] || die "gecersiz tarih: $target"
  [ "$(date -u +%s)" -gt "$(date -u -d "$utc" +%s)" ] || die "hedef an gelecekte."
  need_running; need_archive_mode

  # Hedeften once bitmis en yeni temel yedek.
  local base
  base="$(in_pg "for b in \$(ls -d $BASE_IN/2*Z 2>/dev/null | sort -r); do
                   [ \"\$(cat \$b/FINISHED)\" \\< '$utc' ] && { basename \$b; break; }; done")"
  [ -n "$base" ] || die "hedef andan ($target) once bitmis temel yedek yok; geri donulebilir aralik icin: scripts/pitr.sh status"
  echo "Hedef: $target (Istanbul) = $utc"
  echo "Temel yedek: $base"
  if [ "$yes" != 1 ]; then
    echo "Bu islem TUM veritabanlarini o ana dondurur; sonrasindaki tum degisiklikler kaybolur."
    echo "Mevcut veri $ROOT_IN/pre-restore-<zaman> olarak saklanir. Onaylamak icin --yes ekleyin."
    exit 2
  fi

  # Hedefe kadar olan son WAL parcasi da arsivde olsun: devam eden parca kapatilir ve arsivlenmesi beklenir.
  local last
  last="$(psql_q "SELECT pg_walfile_name(pg_switch_wal())")"
  for _ in $(seq 1 60); do
    [[ "$(psql_q "SELECT coalesce(last_archived_wal,'') FROM pg_stat_archiver")" < "$last" ]] || break
    sleep 1
  done
  [[ "$(psql_q "SELECT coalesce(last_archived_wal,'') FROM pg_stat_archiver")" < "$last" ]] \
    && die "son WAL parcasi ($last) arsivlenemedi; scripts/pitr.sh status ile arsiv hatasina bakin."

  local project vol pgdata stamp
  project="$(docker compose config --format json 2>/dev/null | python3 -c 'import json,sys;print(json.load(sys.stdin)["name"])' 2>/dev/null || echo hr360)"
  vol="${project}_postgres-data"
  pgdata="$(pg sh -c 'echo $PGDATA')"
  stamp="$(date -u +%Y%m%dT%H%M%SZ)"
  local image; image="$(docker compose config --format json | python3 -c 'import json,sys;print(json.load(sys.stdin)["services"]["postgres"]["image"])')"

  echo "Servisler durduruluyor..."
  mapfile -t RUNNING < <(docker compose ps --status running --services 2>/dev/null)
  STOP=()
  for s in "${RUNNING[@]}"; do case "$s" in postgres) ;; *) STOP+=("$s") ;; esac; done
  [ "${#STOP[@]}" -gt 0 ] && docker compose stop "${STOP[@]}" >/dev/null
  docker compose stop postgres >/dev/null

  echo "Veri dizini hazirlaniyor (eski veri: pitr/pre-restore-$stamp)..."
  docker run --rm -v "$vol:/var/lib/postgresql" -e PGDATA="$pgdata" -e BASE="$BASE_IN/$base" \
    -e OLD="$ROOT_IN/pre-restore-$stamp" -e WAL="$WAL_IN" -e TARGET="$(date -u -d "$utc" '+%Y-%m-%d %H:%M:%S+00')" "$image" sh -euc '
      mv "$PGDATA" "$OLD"
      mkdir -p "$PGDATA"
      tar xzf "$BASE/base.tar.gz" -C "$PGDATA"
      # Geri donus sirasinda olusan yeni WAL arsive yazilmaz (eski zaman cizgisine karismasin);
      # promote sonrasi yeni temel yedekle birlikte yeniden acilir.
      rm -f "$WAL/ENABLED"
      cat >> "$PGDATA/postgresql.auto.conf" <<EOF
restore_command = '"'"'gzip -dc $WAL/%f.gz > %p'"'"'
recovery_target_time = '"'"'$TARGET'"'"'
recovery_target_action = '"'"'promote'"'"'
EOF
      touch "$PGDATA/recovery.signal"
      chown -R postgres:postgres "$PGDATA" && chmod 700 "$PGDATA"'

  echo "Postgres hedef ana kadar WAL oynatiyor..."
  docker compose up -d postgres >/dev/null
  local ok=0 cid rc0
  cid="$(docker compose ps -aq postgres)"; rc0="$(docker inspect -f '{{.RestartCount}}' "$cid")"
  for _ in $(seq 1 360); do
    if [ "$(psql_q "SELECT pg_is_in_recovery()" 2>/dev/null)" = "f" ]; then ok=1; break; fi
    # Postgres kurtarma sirasinda durduysa (ayar hatasi, eksik WAL) beklemenin anlami yok.
    # (restart: unless-stopped ile yeniden basliyorsa RestartCount artar.)
    [ "$(docker inspect -f '{{.State.Running}}/{{.RestartCount}}' "$cid" 2>/dev/null)" = "true/$rc0" ] || break
    sleep 5
  done
  if [ "$ok" != 1 ]; then
    echo "HATA: geri donus tamamlanamadi. Gunluk: docker compose logs postgres" >&2
    echo "Eski veriye donmek icin: docker compose stop postgres; veri dizinini $ROOT_IN/pre-restore-$stamp ile degistirin." >&2
    exit 1
  fi
  psql_q "ALTER SYSTEM RESET restore_command" >/dev/null
  psql_q "ALTER SYSTEM RESET recovery_target_time" >/dev/null
  psql_q "ALTER SYSTEM RESET recovery_target_action" >/dev/null
  psql_q "SELECT pg_reload_conf()" >/dev/null

  # Yeni zaman cizgisi icin arsivlemeyi yeniden ac ve hemen temel yedek al.
  in_pg "touch $WAL_IN/ENABLED"
  cmd_base --keep 2 || echo "UYARI: yeni temel yedek alinamadi; 'scripts/pitr.sh base' ile tekrarlayin." >&2

  echo "Servisler baslatiliyor..."
  docker compose up -d >/dev/null 2>&1 || docker compose up -d

  # KVKK: hedef andan sonra imha edilen kayitlar geri gelmis olabilir.
  local token; token="$(secret_env INTERNAL_SERVICE_TOKEN)"
  if [ -n "$token" ]; then
    printf 'KVKK: saklama politikalari yeniden calistiriliyor ... '
    local done_ok=0
    for _ in $(seq 1 40); do
      if out="$(docker compose exec -T gateway wget -qO- --header "X-Internal-Token: $token" --post-data '' \
           http://governance-service:8080/api/internal/retention/run 2>/dev/null)"; then
        echo "tamam ($out)"; done_ok=1; break
      fi
      sleep 5
    done
    [ "$done_ok" = 1 ] || echo "yapilamadi. Panelden KVKK > Saklama politikalari > Simdi calistir ile elle calistirin." >&2
  fi
  echo "Geri donus tamamlandi: $target. Eski veri: $ROOT_IN/pre-restore-$stamp (dogrulayinca: scripts/pitr.sh cleanup)."
}

cmd_cleanup() {
  need_running
  in_pg "rm -rf $ROOT_IN/pre-restore-*"
  echo "Eski veri dizini kopyalari silindi."
}

case "${1:-status}" in
  status) cmd_status ;;
  enable) shift; cmd_enable "$@" ;;
  disable) shift; cmd_disable "$@" ;;
  base) shift; cmd_base "$@" ;;
  restore) shift; cmd_restore "$@" ;;
  cleanup) cmd_cleanup ;;
  enabled) need_running; is_enabled ;;  # betikler icin: acik mi (cikis kodu)
  *) sed -n '2,24p' "$0"; exit 2 ;;
esac

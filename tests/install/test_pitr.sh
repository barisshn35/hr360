#!/usr/bin/env bash
# scripts/pitr.sh uctan uca: gercek PostgreSQL (docker-compose.yml'deki imaj ve ayarlar) ile ayri, gecici bir
# compose projesinde calisir; kurulu HR360'a dokunmaz.
#   1. enable → temel yedek; 2. satir ekle, an T; 3. satirlari sil + yeni satir; 4. restore --to T;
#   5. T'deki veri geri gelmis, sonraki degisiklikler yok; arsivleme yeni zaman cizgisinde surer;
#   6. base --keep 1 eski WAL'i temizler; disable --purge.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
T="$(mktemp -d)"
PROJECT="hr360pitrtest$$"
fail=0
ok() { echo "OK  $*"; }
bad() { echo "XX  $*"; fail=1; }

cleanup() {
  (cd "$T" && docker compose down -v >/dev/null 2>&1) || true
  rm -rf "$T"
}
trap cleanup EXIT

mkdir -p "$T/scripts/lib" "$T/deploy/postgres"
cp "$ROOT/scripts/pitr.sh" "$T/scripts/"
cp "$ROOT/scripts/lib/backup-common.sh" "$T/scripts/lib/"
cp "$ROOT/deploy/postgres/archive-wal.sh" "$T/deploy/postgres/"
# Kurulumdaki postgres servisinin aynisi (imaj, komut, arsiv betigi); yayinlanan kapi ve init betikleri yok.
python3 - "$ROOT/docker-compose.yml" "$T/docker-compose.yml" "$PROJECT" <<'EOF'
import sys, yaml
src = yaml.safe_load(open(sys.argv[1]))["services"]["postgres"]
svc = {k: src[k] for k in ("image", "command", "healthcheck")}
svc["environment"] = {"POSTGRES_USER": "hr360admin", "POSTGRES_PASSWORD": "pitrtest", "POSTGRES_DB": "hr360_operational"}
svc["volumes"] = ["postgres-data:/var/lib/postgresql", "./deploy/postgres/archive-wal.sh:/usr/local/bin/hr360-archive-wal:ro"]
yaml.safe_dump({"name": sys.argv[3], "services": {"postgres": svc}, "volumes": {"postgres-data": {}}}, open(sys.argv[2], "w"))
EOF
: > "$T/.env"
cd "$T"
P=scripts/pitr.sh
q() { docker compose exec -T postgres psql -U hr360admin -d hr360_operational -Atq -c "$1"; }

docker compose up -d postgres >/dev/null
# Ilk acilista imaj once gecici bir sunucuyla kurar, sonra yeniden baslar: kurulumun bitmesi beklenir.
for _ in $(seq 1 120); do
  docker compose logs postgres 2>/dev/null | grep -q 'init process complete' \
    && docker compose exec -T postgres pg_isready -U hr360admin -d postgres >/dev/null 2>&1 && break
  sleep 1
done

out="$($P status)"; [[ "$out" == *"durum=kapali"* ]] && ok "baslangicta kapali" || bad "baslangic durumu: $out"
$P base >/dev/null 2>&1 && bad "kapaliyken temel yedek alinmamali" || ok "kapaliyken temel yedek reddedilir"

$P enable >/dev/null || bad "enable"
out="$($P status)"
[[ "$out" == *"durum=acik"* && "$out" == *"temel=2"* ]] && ok "enable: acik ve temel yedek var" || bad "enable sonrasi: $out"

q "CREATE TABLE pitr_t(id int primary key, v text); INSERT INTO pitr_t SELECT g, 'once' FROM generate_series(1,100) g;"
docker compose exec -T postgres psql -U hr360admin -d postgres -Atq -c "CREATE DATABASE kc2" >/dev/null
docker compose exec -T postgres psql -U hr360admin -d kc2 -Atq -c "CREATE TABLE k(x int); INSERT INTO k VALUES (7)" >/dev/null
sleep 2
TARGET="$(TZ=Europe/Istanbul date '+%Y-%m-%d %H:%M:%S')"
sleep 2
q "DELETE FROM pitr_t WHERE id > 10; INSERT INTO pitr_t VALUES (1000, 'sonra');"
docker compose exec -T postgres psql -U hr360admin -d kc2 -Atq -c "DROP TABLE k" >/dev/null

rc=0; $P restore --to "$TARGET" >/dev/null 2>&1 || rc=$?
[ "$rc" = 2 ] && ok "--yes olmadan geri donus yapilmaz" || bad "--yes olmadan rc=$rc"
$P restore --to "2099-01-01 00:00" --yes >/dev/null 2>&1 && bad "gelecek hedef kabul edildi" || ok "gelecek hedef reddedilir"
$P restore --to "2000-01-01 00:00" --yes >/dev/null 2>&1 && bad "temel yedekten onceki hedef kabul edildi" || ok "temel yedekten onceki hedef reddedilir"

if $P restore --to "$TARGET" --yes > "$T/restore.log" 2>&1; then ok "restore tamamlandi"; else bad "restore: $(tail -5 "$T/restore.log")"; fi
[ "$(q "SELECT count(*) || '/' || count(*) FILTER (WHERE v='sonra') FROM pitr_t")" = "100/0" ] \
  && ok "hedef andaki veri geri geldi, sonraki degisiklik yok" || bad "pitr_t: $(q "SELECT count(*) FROM pitr_t")"
[ "$(docker compose exec -T postgres psql -U hr360admin -d kc2 -Atq -c "SELECT x FROM k" 2>&1)" = "7" ] \
  && ok "diger veritabani da ayni ana dondu" || bad "kc2 geri donmedi"
[ "$(q "SELECT pg_is_in_recovery()")" = "f" ] && ok "yazilabilir (promote)" || bad "hala kurtarma kipinde"
# recovery_* yeniden baslatmada okunur; calisan deger kalir ama yalnizca recovery.signal varken kullanilir.
! docker compose exec -T postgres sh -c 'grep -q recovery_target "$PGDATA/postgresql.auto.conf" || test -f "$PGDATA/recovery.signal"' \
  && ok "kurtarma ayarlari ve recovery.signal temizlendi" || bad "postgresql.auto.conf'ta kurtarma ayari ya da recovery.signal duruyor"
out="$($P status)"
[[ "$out" == *"durum=acik"* && "$out" == *"eski_veri=pre-restore-"* ]] && ok "arsivleme yeni zaman cizgisinde acik, eski veri saklandi" || bad "restore sonrasi durum: $out"

q "INSERT INTO pitr_t VALUES (2000, 'yeni')"
seg="$(q "SELECT pg_walfile_name(pg_switch_wal())")"
for _ in $(seq 1 20); do docker compose exec -T postgres test -f "/var/lib/postgresql/pitr/wal/$seg.gz" && break; sleep 1; done
docker compose exec -T postgres test -f "/var/lib/postgresql/pitr/wal/$seg.gz" && ok "yeni zaman cizgisinde WAL arsivleniyor ($seg)" || bad "WAL arsivlenmedi: $seg"
[[ "$seg" == 00000002* ]] && ok "zaman cizgisi 2" || bad "zaman cizgisi: $seg"

before="$(docker compose exec -T postgres sh -c 'ls /var/lib/postgresql/pitr/wal | wc -l')"
sleep 1; $P base --keep 1 >/dev/null || bad "base --keep 1"
after="$(docker compose exec -T postgres sh -c 'ls /var/lib/postgresql/pitr/wal | wc -l')"
nb="$(docker compose exec -T postgres sh -c 'ls -d /var/lib/postgresql/pitr/base/2*Z | wc -l')"
[ "$nb" = 1 ] && [ "$after" -lt "$before" ] && ok "--keep 1: tek temel yedek, eski WAL silindi ($before → $after)" || bad "keep: base=$nb wal $before → $after"

$P cleanup >/dev/null
[[ "$($P status)" != *"eski_veri="* ]] && ok "cleanup eski veri dizinini siler" || bad "cleanup"
$P disable --purge >/dev/null
[[ "$($P status)" == *"durum=kapali"* ]] && ! docker compose exec -T postgres test -d /var/lib/postgresql/pitr/wal \
  && ok "disable --purge" || bad "disable --purge"
q "SELECT pg_switch_wal()" >/dev/null
sleep 2
[ "$(docker compose exec -T postgres psql -U hr360admin -d postgres -Atq -c "SELECT failed_count FROM pg_stat_archiver")" = "0" ] \
  && ok "kapaliyken arsiv komutu hata vermez (WAL birikmez)" || bad "kapaliyken arsiv hatasi"

[ "$fail" = 0 ] && echo "PITR testleri gecti." || { echo "PITR testlerinde hata var."; exit 1; }

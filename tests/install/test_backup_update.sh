#!/usr/bin/env bash
# scripts/backup.sh (sifreleme, dis depo, zamanlama, geri yukleme testi), scripts/restore.sh
# (sifreli arsiv, KVKK yeniden imha) ve scripts/update.sh (guncelleme, otomatik geri donus)
# davranis testi. Docker gerektirmez: docker ve crontab yerine kayit tutan sahte komutlar.
#
# Kullanim: tests/install/test_backup_update.sh
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
FAILS=0
pass() { echo "  ok   $*"; }
fail() { echo "  FAIL $*"; FAILS=$((FAILS + 1)); }
check() { local desc="$1"; shift; if "$@"; then pass "$desc"; else fail "$desc"; fi; }

BIN="$WORK/bin"; mkdir -p "$BIN"
export HR360_TEST_LOG="$WORK/calls.log" FAKE_CRON="$WORK/crontab"
: > "$HR360_TEST_LOG"

cat > "$BIN/docker" <<'EOF'
#!/usr/bin/env bash
echo "docker $*" >> "$HR360_TEST_LOG"
# --env-file ile aktarilan kimlik bilgisi (komut satirinda degil) ayri kaydedilir.
prev=""; for x in "$@"; do [ "$prev" = --env-file ] && cat "$x" >> "$HR360_TEST_LOG.env"; prev="$x"; done
a="$*"
case "$a" in
  "info"*) exit 0 ;;
  *"retention info"*) [ "${FAKE_NOLOCK:-0}" = 1 ] && exit 1; exit 0 ;;
  "image inspect"*) exit 0 ;;
  *"python /dec.py"*) cat >/dev/null; if [ "${FAKE_DEC_FAIL:-0}" = 1 ]; then echo "engagement_profiles.Iban 1 1 0 0"; else echo "engagement_profiles.Iban 3 0 0 1"; fi; exit 0 ;;
  "exec hr360-restore-drill-"*"to_regclass"*) echo t ;;
  "exec hr360-restore-drill-"*"information_schema.tables"*) echo 42 ;;
  "exec hr360-restore-drill-"*"information_schema.columns"*) echo 1 ;;
  "exec hr360-restore-drill-"*"pg_proc"*) echo 1 ;;
  "exec hr360-restore-drill-"*"WITH c AS"*) if [ "${FAKE_CHAIN_BAD:-0}" = 1 ]; then echo "10 1 0 0 0 2"; else echo "10 0 0 0 0 2"; fi ;;
  "exec hr360-restore-drill-"*"governance_audit_anchors"*) echo "2 0" ;;
  "exec hr360-restore-drill-"*"select count(*) from"*) echo 3 ;;
  "exec hr360-restore-drill-"*"random()"*) : ;;
  # Kapali modul (scale 0) saglik denetimine ve derlemeye girmez (scripts/modules.sh).
  *"compose config --format json"*) echo '{"name":"hr360test","services":{"postgres":{"image":"postgres:16-alpine"},"employee-service":{"build":{}},"leave-service":{"build":{}},"recruitment-service":{"build":{},"scale":0},"gateway":{}}}' ;;
  *"compose config --services"*) printf 'employee-service\nleave-service\ngateway\npostgres\n' ;;
  *"compose ps --status running --services"*) echo postgres ;;
  *"select 1 from pg_database where datname='hr360_operational'"*) echo 1 ;;
  *"select 1 from pg_database"*) : ;;
  *"pg_dump"*) echo "DUMP-DATA" ;;
  *"postgres --version"*) echo "postgres (PostgreSQL) 16.4" ;;
  *"run -d --network none"*) echo cid123 ;;
  *"exec -i cid123 pg_restore"*) cat >/dev/null; [ "${FAKE_RESTORE_FAIL:-0}" = 1 ] && exit 1; exit 0 ;;
  *"exec cid123"*"information_schema.tables"*) echo 42 ;;
  *"exec cid123"*"employee_employees"*) echo 7 ;;
  *"exec cid123"*) exit 0 ;;
  *"gateway wget"*"retention/run"*) echo '{"policies":2,"affected":1}' ;;
  *"gateway wget"*)
    # Saglik: calisma dizininde BROKEN dosyasi varsa (bozuk surum) servisler ayaga kalkmaz.
    [ -f BROKEN ] && exit 1; exit 0 ;;
  *"psql"*"-f -"*|*"pg_restore"*) cat >/dev/null ;;
esac
exit 0
EOF
cat > "$BIN/crontab" <<'EOF'
#!/usr/bin/env bash
case "${1:-}" in
  -l) [ -f "$FAKE_CRON" ] && cat "$FAKE_CRON" || exit 1 ;;
  -) cat > "$FAKE_CRON" ;;
esac
EOF
chmod +x "$BIN"/*
export PATH="$BIN:$PATH"

# ============================================================== yedek
echo "== yedek"
B="$WORK/repo"; mkdir -p "$B/scripts/lib"
cp "$ROOT/scripts/backup.sh" "$ROOT/scripts/restore.sh" "$ROOT/scripts/restore-drill.sh" "$ROOT/scripts/schedule.sh" "$B/scripts/"
cp "$ROOT/scripts/lib/backup-common.sh" "$B/scripts/lib/"
KEY="test-anahtar-$(openssl rand -hex 8)"
printf 'HR360_DB_PASSWORD=x\nINTERNAL_SERVICE_TOKEN=tok\nBACKUP_ENCRYPTION_KEY=%s\n' "$KEY" > "$B/.env"
cd "$B" || exit 1

out="$(scripts/backup.sh --no-minio 2>&1)"; rc=$?
check "yedek alindi" test "$rc" = 0
f="$(ls backups/hr360-*.tar.gz.enc 2>/dev/null | head -1)"
check "arsiv sifreli (.enc), duz arsiv birakilmadi" eval '[ -n "$f" ] && [ -z "$(ls backups/*.tar.gz 2>/dev/null)" ]'
check "arsiv izni 600" test "$(stat -c %a "$f" 2>/dev/null)" = 600
check "sifreli arsiv anahtarsiz acilamaz" eval '! tar tzf "$f" >/dev/null 2>&1'
HR360_BK="$KEY" openssl enc -d -aes-256-cbc -pbkdf2 -iter 200000 -in "$f" -out "$WORK/plain.tgz" -pass env:HR360_BK 2>/dev/null
check "anahtarla cozulur, MANIFEST encrypted=1" eval 'tar xzf "$WORK/plain.tgz" -O --wildcards "*/MANIFEST" | grep -q "^encrypted=1"'
M=deploy/monitoring/textfile
check "izleme metrigi: son yedek zamani ve sifreli bayragi (644)" eval 'grep -q "^hr360_backup_last_success_timestamp_seconds [0-9]" $M/hr360_backup.prom && grep -q "^hr360_backup_last_encrypted 1" $M/hr360_backup.prom && grep -q "^hr360_backup_last_offsite 0" $M/hr360_backup.prom && [ "$(stat -c %a $M/hr360_backup.prom)" = 644 ]'
check "  metrikte dosya adi/anahtar yok" eval '! grep -Eq "hr360-[0-9]{8}|$KEY" $M/hr360_backup.prom'

out="$(scripts/backup.sh verify 2>&1)"; rc=$?
check "geri yukleme testi: gecici veritabaninda dogrulandi" eval '[ $rc = 0 ] && grep -q "^DOGRULANDI" <<< "$out"'
check "  uretim veritabanina dokunmadi (ag kapali gecici konteyner)" grep -q "run -d --network none" "$HR360_TEST_LOG"
check "  gecici konteyner silindi" grep -q "rm -f cid123" "$HR360_TEST_LOG"
out="$(FAKE_RESTORE_FAIL=1 scripts/backup.sh verify 2>&1)"; rc=$?
check "bozuk dokumde dogrulama basarisiz" eval '[ $rc != 0 ] && grep -q "^DOGRULANAMADI" <<< "$out"'
check "  izleme metrigi: verify basarisiz, onceki basarili zaman korunur" eval 'grep -q "^hr360_restore_test_success{tool=\"verify\"} 0" $M/hr360_restore_test_verify.prom && grep -q "^hr360_restore_test_last_success_timestamp_seconds{tool=\"verify\"} [0-9]" $M/hr360_restore_test_verify.prom'
cp .env .env.ok; sed -i "s/^BACKUP_ENCRYPTION_KEY=.*/BACKUP_ENCRYPTION_KEY=yanlis/" .env
out="$(scripts/backup.sh verify 2>&1)"; rc=$?
check "yanlis anahtarla dogrulama basarisiz" eval '[ $rc != 0 ] && grep -Eq "sifre cozulemedi|anahtar .*farkli" <<< "$out"'
mv .env.ok .env

# dis depo
printf 'BACKUP_S3_ENDPOINT=https://s3.eu-central-1.amazonaws.com\nBACKUP_S3_BUCKET=b\nBACKUP_S3_ACCESS_KEY=ak\nBACKUP_S3_SECRET_KEY=sk\n' >> .env
out="$(scripts/backup.sh --no-minio 2>&1)"; rc=$?
check "KVKK m.9: yurt disi depoya dayanaksiz gonderilmez" eval '[ $rc != 0 ] && grep -q "KVKK m.9" <<< "$out"'
sed -i 's#^BACKUP_S3_ENDPOINT=.*#BACKUP_S3_ENDPOINT=https://depo.ornek.com.tr:9000#' .env
: > "$HR360_TEST_LOG"
out="$(scripts/backup.sh --no-minio 2>&1)"; rc=$?
check "yurt ici S3 uyumlu depoya gonderildi" eval '[ $rc = 0 ] && grep -q "MC_HOST_dst=https://ak:sk@depo.ornek.com.tr:9000" "$HR360_TEST_LOG.env"'
check "  kimlik bilgisi komut satirinda yok (ortamdan)" eval '! grep -q "ak:sk" "$HR360_TEST_LOG" && grep -q -- "--env-file " "$HR360_TEST_LOG"'
check "  gonderilen sifreli arsiv ve imzali ozeti" eval 'grep -Eq "cp --quiet /b/hr360-.*\.tar\.gz\.enc dst/b/" "$HR360_TEST_LOG" && grep -Eq "cp --quiet /b/hr360-.*\.tar\.gz\.enc\.manifest dst/b/" "$HR360_TEST_LOG"'
out="$(scripts/backup.sh --no-minio --no-encrypt 2>&1)"; rc=$?
check "sifresiz yedek dis depoya gonderilmez" eval '[ $rc != 0 ] && grep -q "yalnizca sifreli" <<< "$out"'
sed -i '/^BACKUP_S3_/d' .env

# zamanlama
scripts/backup.sh schedule --at 02:30 --keep 30 --verify-weekly >/dev/null 2>&1
check "zamanlama: her gece 02:30 yedek" grep -q "^30 2 \* \* \* cd .* && scripts/backup.sh --keep 30 .*# hr360-backup" "$FAKE_CRON"
check "zamanlama: Pazar geri yukleme testi" grep -q "^30 3 \* \* 0 .*backup.sh verify.*# hr360-backup" "$FAKE_CRON"
echo "0 1 * * * baska-is" >> "$FAKE_CRON"
scripts/backup.sh schedule --at 04:00 >/dev/null 2>&1
check "yeniden zamanlama tek kayit birakir, diger isler korunur" eval '[ "$(grep -c "backup.sh --keep" "$FAKE_CRON")" = 1 ] && grep -q baska-is "$FAKE_CRON"'
scripts/backup.sh unschedule >/dev/null 2>&1
check "zamanlama kaldirildi" eval '! grep -q hr360-backup "$FAKE_CRON" && grep -q baska-is "$FAKE_CRON"'
out="$(scripts/backup.sh schedule --at 25:00 2>&1)"; rc=$?
check "gecersiz saat reddedilir" test "$rc" != 0

# geri yukleme: sifreli arsiv + KVKK yeniden imha
: > "$HR360_TEST_LOG"
out="$(scripts/restore.sh "$f" --yes --only-db 2>&1)"; rc=$?
check "sifreli arsiv geri yuklendi" test "$rc" = 0
check "KVKK: saklama politikalari geri yukleme sonrasi yeniden calisti" eval 'grep -q "X-Internal-Token: tok.*retention/run" "$HR360_TEST_LOG" && grep -q "policies" <<< "$out"'

# imzali ozet
echo "== imzali ozet ve degismez kopya"
check "arsivin yaninda imzali ozet (BACKUP_ENCRYPTION_KEY turevi)" eval '[ -f "$f.manifest" ] && grep -q "^keyid=derived" "$f.manifest" && grep -Eq "^hmac-sha256=[0-9a-f]{64}$" "$f.manifest"'
check "  ozet arsivin SHA-256'sini tasir" grep -q "^sha256=$(sha256sum "$f" | cut -d" " -f1)$" "$f.manifest"
out="$(scripts/backup.sh verify "$f" 2>&1)"
check "  verify ozeti dogrular" grep -q "ozet: SHA-256 ve imza dogrulandi" <<< "$out"
T="$WORK/tampered"; mkdir -p "$T"; cp "$f" "$f.manifest" "$T/"; tf="$T/$(basename "$f")"
printf 'X' >> "$tf"
out="$(scripts/restore.sh "$tf" --yes --only-db 2>&1)"; rc=$?
check "degismis arsiv geri yuklenmez" eval '[ $rc != 0 ] && grep -q "SHA-256 tutmuyor" <<< "$out"'
out="$(scripts/restore.sh "$tf" --yes --only-db --ignore-manifest 2>&1)"
check "  --ignore-manifest ile ozet denetimi gecilir" grep -q "ignore-manifest ile devam" <<< "$out"
out="$(scripts/backup.sh verify "$tf" 2>&1)"; rc=$?
check "  verify de degismis arsivi reddeder" eval '[ $rc != 0 ] && grep -q "^DOGRULANAMADI.*SHA-256 tutmuyor" <<< "$out"'
cp "$f" "$tf"; sed -i "s/^size=.*/size=1/" "$tf.manifest"
out="$(scripts/restore.sh "$tf" --yes --only-db 2>&1)"; rc=$?
check "ozetin kendisi degistirilirse HMAC tutmaz" eval '[ $rc != 0 ] && grep -q "imza (HMAC) tutmuyor" <<< "$out"'
cp "$f.manifest" "$tf.manifest"; printf 'BACKUP_MANIFEST_KEY=baska\n' >> .env
out="$(scripts/restore.sh "$tf" --yes --only-db 2>&1)"; rc=$?
check "  ozet hangi anahtarla imzalandiysa onunla denetlenir (keyid)" test "$rc" = 0
sed -i '/^BACKUP_MANIFEST_KEY=/d' .env
rm "$tf.manifest"
out="$(scripts/restore.sh "$tf" --yes --only-db 2>&1)"; rc=$?
check "ozeti olmayan eski yedek uyariyla geri yuklenir" eval '[ $rc = 0 ] && grep -q "imzali ozet yok" <<< "$out"'

# degismez kopya (--to-minio)
printf 'MINIO_ROOT_USER=minioadmin\nMINIO_ROOT_PASSWORD=gizli-minio-parola\n' >> .env
: > "$HR360_TEST_LOG"; : > "$HR360_TEST_LOG.env"
out="$(scripts/backup.sh --no-minio --to-minio 2>&1)"; rc=$?
nf="$(ls -1t backups/hr360-*.tar.gz.enc | head -1)"; nb="$(basename "$nf")"
check "--to-minio: kurulumun MinIO'suna kopyalandi" eval '[ $rc = 0 ] && grep -q "degismez kopya" <<< "$out"'
check "  kova nesne kilidiyle olusturulur" grep -q -- "--network hr360-net --env-file .*--entrypoint mc .* mb --ignore-existing --with-lock dst/hr360-backups-locked" "$HR360_TEST_LOG"
check "  varsayilan saklama GOVERNANCE 30 gun" grep -q "retention set --default GOVERNANCE 30d dst/hr360-backups-locked" "$HR360_TEST_LOG"
check "  arsiv ve ozet kopyalandi" eval 'grep -q "cp /b/$nb dst/hr360-backups-locked/$nb" "$HR360_TEST_LOG" && grep -q "cp /b/$nb.manifest dst/hr360-backups-locked/$nb.manifest" "$HR360_TEST_LOG"'
check "  parola komut satirinda yok, ortamdan aktarildi" eval '! grep -q "gizli-minio-parola" "$HR360_TEST_LOG" && grep -q "MC_HOST_dst=http://minioadmin:gizli-minio-parola@minio:9000" "$HR360_TEST_LOG.env"'
: > "$HR360_TEST_LOG"
scripts/backup.sh --to-minio >/dev/null 2>&1
check "  nesne yedegi degismez kovayi icermez (katlanarak buyumez)" eval 'grep -q -- "-e LB=hr360-backups-locked" "$HR360_TEST_LOG" && grep -qF -- "--exclude=\"./\$LB\"" "$HR360_TEST_LOG"'
out="$(FAKE_NOLOCK=1 scripts/backup.sh --no-minio --to-minio 2>&1)"; rc=$?
check "kilitsiz olusturulmus kova reddedilir" eval '[ $rc != 0 ] && grep -q "nesne kilidi olmadan" <<< "$out"'
printf 'BACKUP_MINIO_RETENTION_MODE=COMPLIANCE\nBACKUP_MINIO_RETENTION_DAYS=90\nBACKUP_MINIO_BUCKET=worm-yedek\n' >> .env
: > "$HR360_TEST_LOG"
scripts/backup.sh --no-minio --to-minio >/dev/null 2>&1
check "COMPLIANCE kipi, sure ve kova adi .env'den" grep -q "retention set --default COMPLIANCE 90d dst/worm-yedek" "$HR360_TEST_LOG"
printf 'BACKUP_MINIO_URL=https://s3.eu-west-1.amazonaws.com\n' >> .env
out="$(scripts/backup.sh --no-minio --to-minio 2>&1)"; rc=$?
check "dis MinIO yurt disindaysa KVKK m.9 denetimi" eval '[ $rc != 0 ] && grep -q "KVKK m.9" <<< "$out"'
mkdir -p secrets; printf 'dis-gizli\n' > secrets/bk_secret.txt; chmod 600 secrets/bk_secret.txt
sed -i 's#^BACKUP_MINIO_URL=.*#BACKUP_MINIO_URL=https://yedek.ornek.com.tr:9000#' .env
printf 'BACKUP_MINIO_ACCESS_KEY=disak\nBACKUP_MINIO_SECRET_KEY_FILE=/run/secrets/bk_secret\n' >> .env
: > "$HR360_TEST_LOG"; : > "$HR360_TEST_LOG.env"
out="$(scripts/backup.sh --no-minio --to-minio 2>&1)"; rc=$?
check "dis MinIO: _FILE ile dosyadan parola (./secrets), host agi" eval '[ $rc = 0 ] && grep -q "MC_HOST_dst=https://disak:dis-gizli@yedek.ornek.com.tr:9000" "$HR360_TEST_LOG.env" && grep -q -- "--network host" "$HR360_TEST_LOG"'
out="$(scripts/backup.sh --no-minio --no-encrypt --to-minio 2>&1)"; rc=$?
check "  dis MinIO'ya sifresiz yedek gonderilmez" eval '[ $rc != 0 ] && grep -q "yalnizca sifreli" <<< "$out"'
sed -i '/^BACKUP_MINIO_/d' .env

# geri yukleme tatbikati
echo "== geri yukleme tatbikati"
printf 'TENANT_SECRET_KEY=%s\n' "$(openssl rand -base64 32)" >> .env
: > "$HR360_TEST_LOG"
out="$(scripts/restore-drill.sh 2>&1)"; rc=$?
lb="$(basename "$(ls -1t backups/hr360-*.tar.gz.enc | head -1)")"
check "tatbikat: son yedek basarili" eval '[ $rc = 0 ] && grep -q "^TATBIKAT BASARILI .*$lb" <<< "$out"'
check "  ozet, zincir, capa, sifre cozme denetlendi" eval 'grep -q "imzali ozet: SHA-256 ve imza dogrulandi" <<< "$out" && grep -q "zincir saglam: 10 satir" <<< "$out" && grep -q "capalar tutuyor" <<< "$out" && grep -q "engagement_profiles.Iban: 3 acildi, 1 eski duz metin" <<< "$out"'
check "  gecici konteyner agsiz, etiketli ve birimiyle silindi" eval 'grep -q "run -d --name hr360-restore-drill-.* --label hr360.restore-drill=1 --network none" "$HR360_TEST_LOG" && grep -q "rm -f -v hr360-restore-drill-" "$HR360_TEST_LOG"'
check "  canli postgres'e dokunulmadi" eval '! grep -Eq "compose (exec|up|stop|down)" "$HR360_TEST_LOG"'
check "  izleme metrigi: tatbikat basarili" eval 'grep -q "^hr360_restore_test_success{tool=\"drill\"} 1" $M/hr360_restore_test_drill.prom'
tkey="$(sed -n "s/^TENANT_SECRET_KEY=//p" .env)"
check "  anahtarlar komut satirinda yok" eval '! grep -qF "$tkey" "$HR360_TEST_LOG" && grep -q "TENANT_SECRET_KEY=$tkey" "$HR360_TEST_LOG.env" && grep -q -- "--env-file .*keys.env" "$HR360_TEST_LOG"'
: > "$HR360_TEST_LOG"
out="$(FAKE_CHAIN_BAD=1 scripts/restore-drill.sh 2>&1)"; rc=$?
check "bozuk zincirde tatbikat basarisiz (cikis 1), konteyner yine silinir" eval '[ $rc = 1 ] && grep -q "^TATBIKAT BASARISIZ.*zincir bozuk" <<< "$out" && grep -q "rm -f -v hr360-restore-drill-" "$HR360_TEST_LOG"'
out="$(FAKE_DEC_FAIL=1 scripts/restore-drill.sh 2>&1)"; rc=$?
check "acilamayan sifreli deger tatbikati basarisiz kilar" eval '[ $rc = 1 ] && grep -q "engagement_profiles.Iban: 1 acildi, 1 acilamadi" <<< "$out"'
cp "$f.manifest" "$tf.manifest"; printf 'X' >> "$tf"
: > "$HR360_TEST_LOG"
out="$(scripts/restore-drill.sh "$tf" 2>&1)"; rc=$?
check "degismis arsiv: konteyner acilmadan durur" eval '[ $rc = 1 ] && grep -q "TATBIKAT BASARISIZ" <<< "$out" && ! grep -q "run -d" "$HR360_TEST_LOG"'
rm -f "$tf" "$tf.manifest"
out="$(scripts/restore-drill.sh --require-manifest "$f" 2>&1)"; rc=$?
check "--require-manifest: ozet varsa gecer" test "$rc" = 0
mv "$f.manifest" "$f.manifest.bak"
out="$(scripts/restore-drill.sh --require-manifest "$f" 2>&1)"; rc=$?
check "  ozet yoksa basarisiz" eval '[ $rc = 1 ] && grep -q "imzali ozet yok" <<< "$out"'
mv "$f.manifest.bak" "$f.manifest"
out="$(scripts/restore-drill.sh --no-decrypt "$f" 2>&1)"; rc=$?
check "--no-decrypt" eval '[ $rc = 0 ] && grep -q "sifre cozme denetimi atlandi" <<< "$out"'
cp .env .env.ok; sed -i '/^TENANT_SECRET_KEY=/d' .env
out="$(scripts/restore-drill.sh "$f" 2>&1)"; rc=$?
check "anahtar yoksa tatbikat basarisiz" eval '[ $rc = 1 ] && grep -q "anahtar yok" <<< "$out"'
mv .env.ok .env
out="$(scripts/restore-drill.sh --out "$WORK/bos" 2>&1)"; rc=$?
check "yedek yoksa hata" eval '[ $rc != 0 ] && grep -q "yedek bulunamadi" <<< "$out"'
out="$(scripts/restore-drill.sh --samples x 2>&1)"; rc=$?
check "gecersiz --samples reddedilir" test "$rc" != 0

# secrets/ (scripts/secrets-migrate.sh ile .env'den tasinmis sirlar)
echo "== secrets/ dizini"
mkdir -p secrets; chmod 700 secrets
sed -n 's/^TENANT_SECRET_KEY=//p' .env > secrets/tenant_secret_key.txt; chmod 640 secrets/tenant_secret_key.txt
sed -i '/^TENANT_SECRET_KEY=/d' .env
out="$(scripts/backup.sh --no-minio --with-env 2>&1)"; rc=$?
sf="$(ls -1t backups/hr360-*.tar.gz.enc | head -1)"
HR360_BK="$KEY" openssl enc -d -aes-256-cbc -pbkdf2 -iter 200000 -in "$sf" -out "$WORK/s.tgz" -pass env:HR360_BK 2>/dev/null
check "--with-env: secrets/ de arsivde, saglama listesinde" eval '[ $rc = 0 ] && tar tzf "$WORK/s.tgz" | grep -q "/secrets/tenant_secret_key.txt$" && tar xzf "$WORK/s.tgz" -O --wildcards "*/SHA256SUMS" | grep -q "secrets/tenant_secret_key.txt"'
out="$(scripts/restore-drill.sh "$sf" 2>&1)"; rc=$?
check "  tatbikat anahtari arsivdeki secrets/'ten okur" eval '[ $rc = 0 ] && grep -q "anahtarlar: arsivdeki .env" <<< "$out" && grep -q "Iban: 3 acildi" <<< "$out"'
echo degisik > secrets/tenant_secret_key.txt
out="$(scripts/restore.sh "$sf" --yes --only-db --with-env 2>&1)"; rc=$?
check "  restore --with-env secrets/'i geri yukler, eskisini saklar" eval '[ $rc = 0 ] && [ "$(cat secrets/tenant_secret_key.txt)" != degisik ] && [ "$(stat -c %a secrets)" = 700 ] && [ "$(stat -c %a secrets/tenant_secret_key.txt)" = 640 ] && ls -d secrets.before-restore-* >/dev/null 2>&1'

# zamanlayici
echo "== zamanlayici"
printf '#!/usr/bin/env bash\necho "systemctl $*" >> "$HR360_TEST_LOG"\n' > "$BIN/systemctl"
chmod +x "$BIN/systemctl"
out="$(scripts/schedule.sh 2>&1)"; rc=$?
check "print (varsayilan) uc isi gosterir" eval '[ $rc = 0 ] && [ "$(grep -c "^OnCalendar=" <<< "$out")" = 3 ] && grep -q "OnCalendar=Sun \*-\*-\* 05:00:00" <<< "$out" && grep -q "security-scan.sh zap" <<< "$out"'
U="$WORK/units"; mkdir -p "$U"; : > "$HR360_TEST_LOG"
scripts/schedule.sh install backup restore-drill --unit-dir "$U" --at 01:30 --user hr360 >/dev/null 2>&1
check "install: birimler yazildi, zamanlayici etkin" eval '[ -f "$U/hr360-backup.timer" ] && [ -f "$U/hr360-restore-drill.service" ] && grep -q "enable --now hr360-backup.timer" "$HR360_TEST_LOG"'
check "  gunluk yedek 01:30, tatbikat Pazar, kullanici" eval 'grep -q "^OnCalendar=\*-\*-\* 01:30:00" "$U/hr360-backup.timer" && grep -q "^OnCalendar=Sun " "$U/hr360-restore-drill.timer" && grep -q "^User=hr360$" "$U/hr360-backup.service"'
scripts/schedule.sh remove backup --unit-dir "$U" >/dev/null 2>&1
check "remove" eval '[ ! -f "$U/hr360-backup.timer" ] && [ -f "$U/hr360-restore-drill.timer" ]'
echo "0 1 * * * baska-is" > "$FAKE_CRON"
scripts/schedule.sh install zap --cron >/dev/null 2>&1
scripts/schedule.sh install zap --cron --at 03:00 >/dev/null 2>&1
check "cron: tek kayit, diger isler korunur" eval '[ "$(grep -c "hr360-schedule:zap" "$FAKE_CRON")" = 1 ] && grep -q "^0 3 \* \* 6 .*security-scan.sh zap" "$FAKE_CRON" && grep -q baska-is "$FAKE_CRON"'
out="$(scripts/schedule.sh install 2>&1)"; rc=$?
check "install is adi ister" test "$rc" != 0

# ============================================================== guncelleme
echo "== guncelleme"
O="$WORK/origin.git"; U="$WORK/kurulum"
git init -q --bare -b main "$O"
git clone -q "$O" "$U" 2>/dev/null
cd "$U" || exit 1
git config user.email t@t; git config user.name t
mkdir -p scripts/sql
cp "$ROOT/scripts/update.sh" scripts/
cat > scripts/backup.sh <<'EOF'
#!/usr/bin/env bash
mkdir -p backups && touch backups/hr360-test.tar.gz.enc
echo "Yedek: x"; echo "Tamam: backups/hr360-test.tar.gz.enc (1K) — sifreli"
EOF
cat > scripts/restore.sh <<'EOF'
#!/usr/bin/env bash
echo "restore $*" >> "$HR360_TEST_LOG"
EOF
chmod +x scripts/*.sh
echo "SELECT 1;" > scripts/sql/a.sql
printf '.env\n.update-state\nbackups/\n' > .gitignore
echo 1.0.0 > VERSION
git add -A; git commit -qm v1; git push -q origin HEAD:main 2>/dev/null
git branch -q --set-upstream-to=origin/main 2>/dev/null || git branch -q -u origin/main
V1="$(git rev-parse HEAD)"
printf 'X=1\n' > .env
# uzak depoda yeni surumler: v2 (saglam), v3 (bozuk)
T="$WORK/t"; git clone -q "$O" "$T" 2>/dev/null
( cd "$T" && git config user.email t@t && git config user.name t && echo v2 > SURUM && echo 1.0.1 > VERSION && git add -A && git commit -qm v2 && git push -q origin HEAD:main 2>/dev/null )
V2="$(cd "$T" && git rev-parse HEAD)"
( cd "$T" && touch BROKEN && git add -A && git commit -qm v3 && git tag v3 && git push -q origin HEAD:refs/heads/bozuk 2>/dev/null && git push -q origin v3 2>/dev/null )

: > "$HR360_TEST_LOG"
out="$(scripts/update.sh --yes --timeout 1 2>&1)"; rc=$?
check "guncelleme basarili (v1 → v2)" eval '[ $rc = 0 ] && [ "$(git rev-parse HEAD)" = "$V2" ]'
check "  once yedek alindi, durum kaydedildi" eval 'grep -q "^backup=backups/hr360-test.tar.gz.enc" .update-state && grep -q "^result=basarili" .update-state'
check "  goc betikleri uygulandi, imajlar yeniden kuruldu" eval 'grep -q "psql -U hr360admin -d hr360_operational -v ON_ERROR_STOP=1 -q -f -" "$HR360_TEST_LOG" && grep -q "compose build" "$HR360_TEST_LOG"'
check "  kurulu ve yeni urun surumu (VERSION) yazildi" eval 'grep -q "Kurulu surum: 1.0.0" <<< "$out" && grep -q "Yeni surum: 1.0.1" <<< "$out" && grep -q "Guncelleme tamamlandi: 1.0.1" <<< "$out"'
check "  tum servislerin saglik ucu denetlendi" eval 'grep -q "wget -qO- -T 5 http://employee-service:8080/health" "$HR360_TEST_LOG" && grep -q "leave-service:8080/health" "$HR360_TEST_LOG"'
check "  kapali modul derlenmedi ve denetlenmedi" eval '! grep -q "recruitment-service" "$HR360_TEST_LOG" && grep -q "compose build employee-service leave-service" "$HR360_TEST_LOG"'
out="$(scripts/update.sh --yes --timeout 1 2>&1)"; rc=$?
check "zaten guncel" eval '[ $rc = 0 ] && grep -q "Zaten guncel" <<< "$out"'

: > "$HR360_TEST_LOG"
out="$(scripts/update.sh --yes --ref v3 --timeout 1 2>&1)"; rc=$?
check "bozuk surumde otomatik geri donus (cikis 2)" eval '[ $rc = 2 ] && [ "$(git rev-parse HEAD)" = "$V2" ] && [ ! -f BROKEN ]'
check "  durum: basarisiz, geri donuldu" grep -q "^result=basarisiz, geri donuldu" .update-state
check "  veritabani varsayilan olarak geri yuklenmedi" eval '! grep -q "^restore" "$HR360_TEST_LOG"'

: > "$HR360_TEST_LOG"
out="$(scripts/update.sh --yes --ref v3 --timeout 1 --restore-db 2>&1)"; rc=$?
check "--restore-db: geri donuste guncelleme oncesi yedek geri yuklenir" eval '[ $rc = 2 ] && grep -q "restore backups/hr360-test.tar.gz.enc --yes --only-db" "$HR360_TEST_LOG"'

echo degisti >> scripts/sql/a.sql
out="$(scripts/update.sh --yes --timeout 1 2>&1)"; rc=$?
check "kaydedilmemis degisiklik varken guncelleme yapilmaz" eval '[ $rc != 0 ] && grep -q "kaydedilmemis" <<< "$out"'
git checkout -q -- scripts/sql/a.sql

git reset -q --hard "$V1"; rm -f .update-state
scripts/update.sh --yes --timeout 1 >/dev/null 2>&1
out="$(scripts/update.sh rollback --yes --timeout 1 2>&1)"; rc=$?
check "elle geri donus (rollback) onceki surume" eval '[ $rc = 0 ] && [ "$(git rev-parse HEAD)" = "$V1" ]'
out="$(scripts/update.sh status 2>&1)"
check "durum komutu" eval 'grep -q "Son guncelleme" <<< "$out" && grep -q "Kurulu surum: 1.0.0" <<< "$out"'

echo
if [ "$FAILS" -gt 0 ]; then echo "BASARISIZ: $FAILS"; exit 1; fi
echo "Tum denetimler gecti."

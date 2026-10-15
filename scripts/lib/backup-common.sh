# shellcheck shell=bash
# scripts/backup.sh, scripts/restore.sh ve scripts/restore-drill.sh'in ortak yardimcilari
# (kaynak olarak yuklenir: ". scripts/lib/backup-common.sh"; calisma dizini repo koku olmali).
#
# Imzali ozet (manifest): her arsivin yaninda <arsiv>.manifest dosyasi durur:
#   format=hr360-backup-manifest/1
#   file=hr360-YYYYmmdd-HHMMSS.tar.gz.enc
#   size=<bayt>
#   sha256=<arsivin SHA-256 ozeti>
#   created=<UTC zaman>
#   keyid=manifest|derived|none
#   hmac-sha256=<ustteki satirlarin HMAC-SHA256'si ya da ->
# Anahtar: BACKUP_MANIFEST_KEY (ya da BACKUP_MANIFEST_KEY_FILE). Yoksa BACKUP_ENCRYPTION_KEY'den
# turetilir (keyid=derived); o da yoksa ozet imzasiz yazilir (keyid=none, uyari).
# Arsiv sifrelemesi (openssl aes-256-cbc) butunluk saglamaz; degisiklik bu HMAC ile yakalanir.

# .env (ya da verilen dosya) icindeki degisken. Bos ise sirasiyla: NAME_FILE'in gosterdigi dosya
# (Docker secrets: /run/secrets/x yolu sunucuda yoksa ./secrets/x ve ./secrets/x.txt denenir) ve
# env dosyasinin yanindaki secrets/<ad kucuk harf>.txt (scripts/secrets-migrate.sh ile tasinmis sir;
# scripts/lib/secrets.sh secret_get ile ayni kural). Arsivdeki env + secrets/ icin de calisir.
envfile_val() {
  local file="$1" name="$2" v p c lc
  v="$( { [ -f "$file" ] && grep -E "^$name=" "$file" | tail -1 | cut -d= -f2-; } || true)"
  if [ -z "$v" ]; then
    p="$( { [ -f "$file" ] && grep -E "^${name}_FILE=" "$file" | tail -1 | cut -d= -f2-; } || true)"
    if [ -n "$p" ]; then
      for c in "$p" "secrets/$(basename "$p")" "secrets/$(basename "$p").txt"; do
        if [ -r "$c" ] && [ -f "$c" ]; then v="$(tr -d '\r\n' < "$c")"; break; fi
      done
    fi
  fi
  if [ -z "$v" ]; then
    lc="$(dirname "$file")/secrets/$(printf '%s' "$name" | tr '[:upper:]' '[:lower:]').txt"
    if [ -r "$lc" ] && [ -f "$lc" ]; then v="$(tr -d '\r\n' < "$lc")"; fi
  fi
  printf '%s' "$v"
}
secret_env() { envfile_val .env "$1"; }

# HMAC-SHA256 (hex). Anahtar ortam degiskeninden (HR360_MK), veri stdin'den; anahtar komut
# satirinda (ps) gorunmez.
_hmac_hex() { python3 -c 'import hmac,hashlib,os,sys;print(hmac.new(os.environ["HR360_MK"].encode(),sys.stdin.buffer.read(),hashlib.sha256).hexdigest())'; }

# manifest_key <keyid> [env-dosyasi]: imza anahtari (bos: yok).
manifest_key() {
  local id="$1" f="${2:-.env}" k
  case "$id" in
    manifest) envfile_val "$f" BACKUP_MANIFEST_KEY ;;
    derived)
      k="$(envfile_val "$f" BACKUP_ENCRYPTION_KEY)"
      [ -n "$k" ] && printf 'hr360-backup-manifest/1' | HR360_MK="$k" _hmac_hex
      ;;
  esac
}

# Yeni ozet icin anahtar kimligi.
manifest_keyid() {
  if [ -n "$(envfile_val "${1:-.env}" BACKUP_MANIFEST_KEY)" ]; then echo manifest
  elif [ -n "$(envfile_val "${1:-.env}" BACKUP_ENCRYPTION_KEY)" ]; then echo derived
  else echo none; fi
}

# manifest_write <arsiv>: <arsiv>.manifest yazar.
manifest_write() {
  local a="$1" id key body mac
  id="$(manifest_keyid)"
  command -v python3 >/dev/null 2>&1 || id=none
  body="format=hr360-backup-manifest/1
file=$(basename "$a")
size=$(stat -c %s "$a")
sha256=$(sha256sum "$a" | cut -d' ' -f1)
created=$(date -u +%Y-%m-%dT%H:%M:%SZ)
keyid=$id
"
  mac=-
  if [ "$id" != none ]; then
    key="$(manifest_key "$id")"
    mac="$(printf '%s' "$body" | HR360_MK="$key" _hmac_hex)"
  else
    echo "UYARI: BACKUP_MANIFEST_KEY / BACKUP_ENCRYPTION_KEY yok (ya da python3 yok); ozet imzasiz yazildi." >&2
  fi
  printf '%shmac-sha256=%s\n' "$body" "$mac" > "$a.manifest"
  chmod 600 "$a.manifest" 2>/dev/null || true
}

# manifest_verify <arsiv> [env-dosyasi]
#   0: ozet ve imza tutuyor   1: tutmuyor (arsiv ya da ozet degismis)
#   2: ozet dosyasi yok       3: SHA-256 tutuyor ama imza dogrulanamadi (imzasiz ya da anahtar yok)
# Sonuc mesaji MANIFEST_MSG degiskenine yazilir.
manifest_verify() {
  local a="$1" f="${2:-.env}" m="$1.manifest" body want got id key mac
  MANIFEST_MSG=""
  [ -f "$m" ] || { MANIFEST_MSG="imzali ozet yok ($(basename "$m"))"; return 2; }
  body="$(grep -v '^hmac-sha256=' "$m")
"
  want="$(sed -n 's/^sha256=//p' "$m" | head -1)"
  [ "$(sed -n 's/^file=//p' "$m" | head -1)" = "$(basename "$a")" ] || { MANIFEST_MSG="ozet baska bir arsive ait"; return 1; }
  got="$(sha256sum "$a" | cut -d' ' -f1)"
  [ -n "$want" ] && [ "$want" = "$got" ] || { MANIFEST_MSG="SHA-256 tutmuyor: arsiv degismis ya da bozuk"; return 1; }
  id="$(sed -n 's/^keyid=//p' "$m" | head -1)"
  mac="$(sed -n 's/^hmac-sha256=//p' "$m" | head -1)"
  if [ "$id" = none ] || [ "$mac" = - ]; then MANIFEST_MSG="SHA-256 tutuyor; ozet imzasiz"; return 3; fi
  command -v python3 >/dev/null 2>&1 || { MANIFEST_MSG="SHA-256 tutuyor; python3 yok, imza denetlenemedi"; return 3; }
  key="$(manifest_key "$id" "$f")"
  [ -n "$key" ] || { MANIFEST_MSG="SHA-256 tutuyor; imza anahtari ($id) yok, imza denetlenemedi"; return 3; }
  [ "$(printf '%s' "$body" | HR360_MK="$key" _hmac_hex)" = "$mac" ] || { MANIFEST_MSG="imza (HMAC) tutmuyor: ozet degistirilmis ya da anahtar farkli$([ "$id" = derived ] && echo " (BACKUP_ENCRYPTION_KEY)")"; return 1; }
  MANIFEST_MSG="SHA-256 ve imza dogrulandi"
  return 0
}

# Degismez kopya hedefi (backup.sh --to-minio; restore.sh bu kovayi MinIO geri yuklemesinde korur).
lock_bucket() { local b; b="$(secret_env BACKUP_MINIO_BUCKET)"; printf '%s' "${b:-hr360-backups-locked}"; }
# Ici bos ise hedef kurulumun kendi MinIO'sudur (hr360-net agindaki http://minio:9000).
lock_external_url() { secret_env BACKUP_MINIO_URL; }

# Izleme (Prometheus, node-exporter "textfile" toplayicisi): yedek ve geri yukleme testi
# sonuclari deploy/monitoring/textfile/<ad>.prom dosyasina yazilir; alarmlar (YedekEski,
# GeriYuklemeTestiBasarisiz...) bunlara bakar. Dizin HR360_METRICS_DIR ile degistirilebilir.
# Yalnizca zaman/boyut/0-1 bayraklari yazilir; dosya adi, anahtar ya da kisisel veri yok.
# Yazilamazsa (izin vb.) yedek basarisiz sayilmaz, yalnizca uyari verilir.
# metrics_write <ad> : icerigi stdin'den okur, gecici dosya + mv ile atomik yazar (644).
metrics_write() {
  local dir="${HR360_METRICS_DIR:-deploy/monitoring/textfile}" tmp
  { mkdir -p "$dir" && chmod 755 "$dir"; } 2>/dev/null || true
  tmp="$(mktemp "$dir/.$1.XXXXXX" 2>/dev/null)" || { echo "UYARI: izleme metrigi yazilamadi ($dir)" >&2; cat >/dev/null; return 0; }
  cat > "$tmp" && chmod 644 "$tmp" && mv -f "$tmp" "$dir/$1.prom" \
    || { rm -f "$tmp"; echo "UYARI: izleme metrigi yazilamadi ($dir/$1.prom)" >&2; }
  return 0
}

# metrics_restore_test <arac: drill|verify> <basarili: 0|1>
# Basarisiz calismada onceki basarili zaman korunur (alarm "son basarili test ne zaman" diye bakar).
metrics_restore_test() {
  local tool="$1" success="$2" now prev dir="${HR360_METRICS_DIR:-deploy/monitoring/textfile}"
  now="$(date +%s)"
  prev="$(sed -n "s/^hr360_restore_test_last_success_timestamp_seconds{tool=\"$tool\"} //p" "$dir/hr360_restore_test_$tool.prom" 2>/dev/null | head -1 || true)"
  [ "$success" = 1 ] && prev="$now"
  {
    echo "# HELP hr360_restore_test_last_run_timestamp_seconds Son geri yukleme testinin zamani."
    echo "# TYPE hr360_restore_test_last_run_timestamp_seconds gauge"
    echo "hr360_restore_test_last_run_timestamp_seconds{tool=\"$tool\"} $now"
    echo "# HELP hr360_restore_test_success Son geri yukleme testi basarili mi (1/0)."
    echo "# TYPE hr360_restore_test_success gauge"
    echo "hr360_restore_test_success{tool=\"$tool\"} $success"
    if [ -n "$prev" ]; then
      echo "# HELP hr360_restore_test_last_success_timestamp_seconds Son basarili geri yukleme testinin zamani."
      echo "# TYPE hr360_restore_test_last_success_timestamp_seconds gauge"
      echo "hr360_restore_test_last_success_timestamp_seconds{tool=\"$tool\"} $prev"
    fi
  } | metrics_write "hr360_restore_test_$tool"
}

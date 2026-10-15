#!/usr/bin/env bash
# Geri yukleme tatbikati: en son (ya da verilen) yedegi GECICI bir PostgreSQL konteynerine
# yukler, saglamlik denetimlerini yapar, rapor yazar ve her seyi temizler. Canli sisteme
# dokunmaz: canli veritabani, MinIO, birimler ve .env yalnizca OKUNUR (.env'den anahtarlar).
#
# Kullanim:
#   scripts/restore-drill.sh [ARSIV] [--out DIZIN] [--samples N] [--env DOSYA] [--no-decrypt]
#                            [--require-manifest] [--keep]
#
#   ARSIV              Denenecek arsiv (varsayilan: --out dizinindeki en yeni yedek)
#   --out DIZIN        Yedeklerin dizini (varsayilan: ./backups, scripts/backup.sh ile ayni)
#   --samples N        Sifreli sutun basina denenecek deger sayisi (varsayilan: 5)
#   --env DOSYA        Anahtarlarin okunacagi dosya (varsayilan: arsivde env varsa o, yoksa .env)
#   --no-decrypt       Sifre cozme denetimini atla
#   --require-manifest Imzali ozet (<arsiv>.manifest) yoksa ya da imza dogrulanamiyorsa basarisiz say
#   --keep             Gecici konteyneri incelemek icin birak (sonra: docker rm -f -v <ad>)
#
# Denetimler:
#   1. Imzali ozet (SHA-256 + HMAC), arsivin sifresinin cozulmesi, SHA256SUMS.
#   2. Her veritabani dokumunun gecici konteynerde pg_restore ile acilmasi.
#   3. Ana tablolarin satir sayilari (kiraci, calisan, izin, denetim kaydi, profil).
#   4. Denetim kaydi zinciri (audit_row_hash; governance AuditChainGuard ile ayni kurallar) ve
#      her kiracinin son capasi (governance_audit_anchors).
#   5. Sifreli alanlardan ornekler anahtar halkasiyla (TENANT_SECRET_KEYS ya da TENANT_SECRET_KEY)
#      acilir. Degerler ASLA yazdirilmaz; yalnizca sutun basina basarili/basarisiz sayisi.
#
# Cikis kodu: 0 tum denetimler gecti, 1 en az biri basarisiz.
# Son satir: "TATBIKAT BASARILI <zaman> <arsiv>" ya da "TATBIKAT BASARISIZ <zaman> <arsiv>: ...".
set -euo pipefail

cd "$(dirname "$0")/.."
# shellcheck source=lib/backup-common.sh
. scripts/lib/backup-common.sh
OUT=backups; SAMPLES=5; ENVSRC=""; DECRYPT=1; REQUIRE_MANIFEST=0; KEEP_CT=0; ARCHIVE=""
CRYPTO_IMAGE="hr360-restore-drill-crypto:1"

die() { echo "HATA: $*" >&2; exit 1; }
while [ $# -gt 0 ]; do
  case "$1" in
    --out) OUT="$2"; shift 2 ;;
    --samples) SAMPLES="$2"; shift 2 ;;
    --env) ENVSRC="$2"; shift 2 ;;
    --no-decrypt) DECRYPT=0; shift ;;
    --require-manifest) REQUIRE_MANIFEST=1; shift ;;
    --keep) KEEP_CT=1; shift ;;
    -h|--help) sed -n '2,29p' "$0"; exit 0 ;;
    -*) die "bilinmeyen secenek: $1" ;;
    *) [ -z "$ARCHIVE" ] || die "tek bir arsiv verin"; ARCHIVE="$1"; shift ;;
  esac
done
[[ "$SAMPLES" =~ ^[0-9]+$ ]] || die "--samples sayi olmali"
[ -z "$ENVSRC" ] || [ -f "$ENVSRC" ] || die "--env dosyasi bulunamadi: $ENVSRC"

if ! declare -F docker >/dev/null && ! docker info >/dev/null 2>&1 && sudo docker info >/dev/null 2>&1; then
  docker() { command sudo docker "$@"; }
fi

if [ -z "$ARCHIVE" ]; then
  # shellcheck disable=SC2012
  ARCHIVE="$({ ls -1t "$OUT"/hr360-*.tar.gz "$OUT"/hr360-*.tar.gz.enc 2>/dev/null || true; } | head -1)"
fi
[ -n "$ARCHIVE" ] && [ -f "$ARCHIVE" ] || die "denenecek yedek bulunamadi (${OUT}/hr360-*.tar.gz[.enc])"

umask 077
WORK="$(mktemp -d)"; CID=""
cleanup() {
  if [ -n "$CID" ]; then
    if [ "$KEEP_CT" = 1 ]; then echo "Gecici konteyner birakildi: $CID (silmek icin: docker rm -f -v $CID)"
    else docker rm -f -v "$CID" >/dev/null 2>&1 || true; fi
  fi
  rm -rf "$WORK"
}
trap cleanup EXIT

PROBLEMS=()
NAME="$(basename "$ARCHIVE")"
ok()  { echo "  ok    $*"; }
bad() { echo "  HATA  $*"; PROBLEMS+=("$*"); }
finish() {
  if [ "${#PROBLEMS[@]}" -eq 0 ]; then
    metrics_restore_test drill 1; echo "TATBIKAT BASARILI $(date -u +%FT%TZ) $NAME"; exit 0
  fi
  metrics_restore_test drill 0
  echo "TATBIKAT BASARISIZ $(date -u +%FT%TZ) $NAME: ${#PROBLEMS[@]} sorun (${PROBLEMS[0]}$([ "${#PROBLEMS[@]}" -gt 1 ] && echo ', ...'))"
  exit 1
}
fatal() { bad "$*"; finish; }

echo "Geri yukleme tatbikati: $NAME"

# ---------------------------------------------------------------- 1. ozet, sifre, saglama
echo "1) Arsiv"
mrc=0; manifest_verify "$ARCHIVE" || mrc=$?
case "$mrc" in
  0) ok "imzali ozet: $MANIFEST_MSG" ;;
  1) fatal "imzali ozet: $MANIFEST_MSG" ;;
  *) if [ "$REQUIRE_MANIFEST" = 1 ]; then bad "imzali ozet: $MANIFEST_MSG"; else echo "  uyari $MANIFEST_MSG"; fi ;;
esac
src="$ARCHIVE"
if [[ "$ARCHIVE" == *.enc ]]; then
  KEY="$(secret_env BACKUP_ENCRYPTION_KEY)"
  [ -n "$KEY" ] || fatal "arsiv sifreli ama .env'de BACKUP_ENCRYPTION_KEY yok"
  HR360_BK="$KEY" openssl enc -d -aes-256-cbc -pbkdf2 -iter 200000 -in "$ARCHIVE" -out "$WORK/a.tar.gz" -pass env:HR360_BK 2>/dev/null \
    || fatal "sifre cozulemedi (anahtar yanlis ya da arsiv bozuk)"
  src="$WORK/a.tar.gz"
  ok "arsivin sifresi cozuldu"
fi
tar xzf "$src" -C "$WORK" 2>/dev/null || fatal "arsiv acilamadi"
rm -f "$WORK/a.tar.gz"
D="$(find "$WORK" -mindepth 1 -maxdepth 1 -type d -name 'hr360-*' | head -1)"
[ -n "$D" ] && [ -f "$D/MANIFEST" ] || fatal "gecerli bir HR360 yedegi degil (MANIFEST yok)"
(cd "$D" && sha256sum -c --quiet SHA256SUMS >/dev/null 2>&1) || fatal "SHA256SUMS tutmuyor"
ok "SHA256SUMS tutuyor ($(sed -n 's/^created=//p' "$D/MANIFEST"))"
[ -f "$D/hr360_operational.dump" ] || fatal "arsivde hr360_operational dokumu yok"

# ---------------------------------------------------------------- 2. gecici veritabani
echo "2) Gecici veritabani"
IMAGE="$(docker compose config --format json 2>/dev/null | python3 -c 'import json,sys;print(json.load(sys.stdin)["services"]["postgres"]["image"])' 2>/dev/null || echo postgres:18-alpine)"
CT_NAME="hr360-restore-drill-$(date +%s)-$RANDOM"
# Aga bagli olmayan, adlandirilmis gecici konteyner; canli postgres'e ve birimlerine dokunulmaz.
CID="$CT_NAME"
docker run -d --name "$CT_NAME" --label hr360.restore-drill=1 --network none \
  -e POSTGRES_USER=hr360admin -e POSTGRES_HOST_AUTH_METHOD=trust "$IMAGE" >/dev/null || fatal "gecici veritabani baslatilamadi ($IMAGE)"
ready=0
for _ in $(seq 1 90); do docker exec "$CID" pg_isready -U hr360admin -d postgres >/dev/null 2>&1 && { ready=1; break; }; sleep 1; done
[ "$ready" = 1 ] || fatal "gecici veritabani hazir olmadi"
# pg_isready ilk acilista (initdb sonrasi yeniden baslatma) erken donebilir; kisa bir sorguyla dogrulanir.
for _ in $(seq 1 30); do docker exec "$CID" psql -U hr360admin -d postgres -Atc "select 1" >/dev/null 2>&1 && break; sleep 1; done
for f in "$D"/*.dump; do
  [ -e "$f" ] || continue
  db="$(basename "$f" .dump)"
  if ! docker exec "$CID" psql -U hr360admin -d postgres -q -c "CREATE DATABASE \"$db\"" >/dev/null 2>&1; then bad "$db olusturulamadi"; continue; fi
  if ! docker exec -i "$CID" pg_restore -U hr360admin -d "$db" --no-owner --no-privileges --exit-on-error < "$f" >/dev/null 2>"$WORK/restore.err"; then
    bad "$db geri yuklenemedi: $(head -c 300 "$WORK/restore.err" | tr '\n' ' ')"; continue
  fi
  n="$(docker exec "$CID" psql -U hr360admin -d "$db" -Atc "select count(*) from information_schema.tables where table_schema='public'" | tr -d '\r')"
  if [ "${n:-0}" -gt 0 ] 2>/dev/null; then ok "$db: $n tablo"; else bad "$db bos (tablo yok)"; fi
done

q() { docker exec "$CID" psql -U hr360admin -d hr360_operational -v ON_ERROR_STOP=1 -Atc "$1" 2>/dev/null | tr -d '\r'; }
has_table() { [ "$(q "select to_regclass('public.$1') is not null")" = t ]; }
has_col() { [ "$(q "select count(*) from information_schema.columns where table_schema='public' and table_name='$1' and column_name='$2'")" = 1 ]; }

# ---------------------------------------------------------------- 3. satir sayilari
echo "3) Ana tablolar"
for t in platform_tenants employee_employees leave_requests leave_balances engagement_profiles audit_log; do
  if has_table "$t"; then ok "$t: $(q "select count(*) from $t") satir"; else bad "$t tablosu yok"; fi
done
if has_table platform_tenants && [ "$(q "select count(*) from platform_tenants")" = 0 ]; then
  echo "  uyari platform_tenants bos (yeni kurulum degilse beklenmez)"
fi

# ---------------------------------------------------------------- 4. denetim zinciri
echo "4) Denetim kaydi zinciri"
if [ "$(q "select count(*) from pg_proc where proname='audit_row_hash'")" != 0 ] && has_col audit_log ChainSeq; then
  res="$(q "WITH c AS (
      SELECT \"ChainSeq\", \"Hash\", \"PrevHash\",
             audit_row_hash(\"PrevHash\", \"ChainSeq\", \"TenantSlug\", \"Service\", \"EntityType\", \"EntityId\", \"Action\", \"Changes\",
                            \"UserId\", \"UserName\", \"CorrelationId\", \"IpAddress\", \"OccurredAt\") AS calc,
             lag(\"Hash\") OVER w AS prev_hash, lag(\"ChainSeq\") OVER w AS prev_seq
        FROM audit_log WHERE \"ChainSeq\" IS NOT NULL
      WINDOW w AS (PARTITION BY coalesce(\"TenantSlug\", '') ORDER BY \"ChainSeq\"))
    SELECT count(*) || ' ' ||
           count(*) FILTER (WHERE calc IS DISTINCT FROM \"Hash\") || ' ' ||
           count(*) FILTER (WHERE prev_hash IS NOT NULL AND \"PrevHash\" IS DISTINCT FROM prev_hash) || ' ' ||
           count(*) FILTER (WHERE prev_seq IS NOT NULL AND \"ChainSeq\" <> prev_seq + 1) || ' ' ||
           (SELECT count(*) FROM audit_log WHERE \"ChainSeq\" IS NULL) || ' ' ||
           (SELECT count(DISTINCT coalesce(\"TenantSlug\", '')) FROM audit_log)
      FROM c")"
  read -r rows tampered broken gaps unchained tenants <<< "${res:-x x x x x x}"
  if [ "$rows" = x ]; then bad "zincir sorgusu calismadi"
  elif [ "$tampered" = 0 ] && [ "$broken" = 0 ] && [ "$gaps" = 0 ] && [ "$unchained" = 0 ]; then
    ok "zincir saglam: $rows satir, $tenants zincir"
  else
    bad "zincir bozuk: degismis $tampered, kopuk $broken, bosluk $gaps, zincirsiz $unchained"
  fi
  if has_table governance_audit_anchors; then
    # Her kiracinin son capasi: zincir geriye gitmemis ve capa satiri ayni ozetle yerinde olmali
    # (capa satiri saklama suresiyle silinmisse, yani zincirin basi capadan ilerideyse sorun degil).
    anc="$(q "SELECT count(*) || ' ' || count(*) FILTER (WHERE mx IS NULL OR mx < \"ToSeq\" OR (mn <= \"ToSeq\" AND h IS DISTINCT FROM \"HeadHash\"))
      FROM (SELECT DISTINCT ON (a.\"TenantSlug\") a.\"TenantSlug\", a.\"ToSeq\", a.\"HeadHash\",
              (SELECT max(\"ChainSeq\") FROM audit_log l WHERE coalesce(l.\"TenantSlug\", '') = a.\"TenantSlug\") AS mx,
              (SELECT min(\"ChainSeq\") FROM audit_log l WHERE coalesce(l.\"TenantSlug\", '') = a.\"TenantSlug\") AS mn,
              (SELECT \"Hash\" FROM audit_log l WHERE coalesce(l.\"TenantSlug\", '') = a.\"TenantSlug\" AND l.\"ChainSeq\" = a.\"ToSeq\") AS h
            FROM governance_audit_anchors a WHERE a.\"ToSeq\" IS NOT NULL
           ORDER BY a.\"TenantSlug\", a.\"CheckedAt\" DESC) x")"
    read -r anchors anchor_bad <<< "${anc:-x x}"
    if [ "$anchors" = x ]; then bad "capa sorgusu calismadi"
    elif [ "$anchor_bad" = 0 ]; then ok "capalar tutuyor ($anchors kiraci)"
    else bad "$anchor_bad kiracinin capasi tutmuyor (zincir geriye gitmis ya da capa satiri degismis)"; fi
  else
    echo "  uyari governance_audit_anchors yok; capa denetimi atlandi"
  fi
else
  bad "audit_row_hash islevi ya da ChainSeq sutunu yok (zincir dogrulanamadi)"
fi

# ---------------------------------------------------------------- 5. sifre cozme
echo "5) Sifreli alanlar"
# tablo sutun tur  (pii: oneksiz deger eski duz metindir; raw: oneksiz deger eski enc1 govdesidir)
ENC_COLUMNS="engagement_profiles Iban pii
engagement_profiles NationalId pii
expense_travel_requests PassportCipher pii
governance_custom_field_values Value pii
governance_chat_apps SlackBotTokenEnc raw
governance_chat_apps SlackSigningSecretEnc raw
governance_chat_apps TeamsAppPasswordEnc raw
governance_chat_apps BotTokenEnc raw
governance_chat_apps IncomingTokenEnc raw
governance_calendar_connections AccessTokenEnc raw
governance_calendar_connections RefreshTokenEnc raw
governance_provider_configs ClientSecretEnc raw
governance_provisioning_configs CredentialsEnc raw
governance_ethics_reports ContactEnc raw
governance_osh_exams NotesEnc raw
governance_document_requests DocumentEnc raw
platform_tenants SmtpPasswordEncrypted raw
tenant_directory_settings LdapBindPasswordEncrypted raw
notification_vapid_keys PrivateKeyEnc raw"

if [ "$DECRYPT" = 0 ]; then
  echo "  uyari --no-decrypt: sifre cozme denetimi atlandi"
else
  if [ -n "$ENVSRC" ]; then KEYFILE="$ENVSRC"
  elif [ -f "$D/env" ]; then KEYFILE="$D/env"
  else KEYFILE=.env; fi
  TENANT_SECRET_KEYS="$(envfile_val "$KEYFILE" TENANT_SECRET_KEYS)"
  TENANT_SECRET_KEY="$(envfile_val "$KEYFILE" TENANT_SECRET_KEY)"
  if [ -z "$TENANT_SECRET_KEYS$TENANT_SECRET_KEY" ]; then
    bad "anahtar yok: $KEYFILE icinde TENANT_SECRET_KEYS / TENANT_SECRET_KEY (ya da _FILE) tanimli degil"
  else
    echo "  anahtarlar: $([ "$KEYFILE" = "$D/env" ] && echo 'arsivdeki .env' || echo "$KEYFILE")"
    if ! docker image inspect "$CRYPTO_IMAGE" >/dev/null 2>&1; then
      # Tek seferlik: AES-GCM icin python + cryptography imaji (internet yalnizca bu adimda gerekir).
      printf 'FROM python:3.11-slim\nRUN pip install --no-cache-dir cryptography==44.0.3\nUSER 65534\n' \
        | docker build -q -t "$CRYPTO_IMAGE" - >/dev/null 2>&1 || true
    fi
    if ! docker image inspect "$CRYPTO_IMAGE" >/dev/null 2>&1; then
      bad "sifre cozme imaji ($CRYPTO_IMAGE) olusturulamadi (internet gerekli); --no-decrypt ile atlanabilir"
    else
      cat > "$WORK/dec.py" <<'PY'
import base64, os, sys
from cryptography.hazmat.primitives.ciphers.aead import AESGCM

def ring():
    keys = {}
    spec = os.environ.get("TENANT_SECRET_KEYS", "").strip()
    if spec:
        for part in spec.split(","):
            kid, _, b64 = part.strip().partition(":")
            if kid and b64:
                keys[kid.strip()] = base64.b64decode(b64.strip())
    elif os.environ.get("TENANT_SECRET_KEY", "").strip():
        keys["k0"] = base64.b64decode(os.environ["TENANT_SECRET_KEY"].strip())
    return keys

keys = ring()
stats = {}
for raw in sys.stdin.buffer:
    parts = raw.rstrip(b"\n").decode("utf-8", "replace").split("\t", 2)
    if len(parts) != 3:
        continue
    label, kind, v = parts
    st = stats.setdefault(label, {"ok": 0, "fail": 0, "plain": 0, "nokey": 0})
    try:
        if v.startswith("enc2:"):
            _, kid, b64 = v.split(":", 2)
        elif v.startswith("enc1:"):
            kid, b64 = "k0", v[5:]
        elif kind == "raw":
            kid, b64 = "k0", v
        else:
            st["plain"] += 1
            continue
        if kid not in keys:
            st["nokey"] += 1
            continue
        d = base64.b64decode(b64, validate=True)
        # Bicim: nonce(12) | etiket(16) | sifreli metin; cryptography sifreli metin + etiket bekler.
        AESGCM(keys[kid]).decrypt(d[:12], d[28:] + d[12:28], None)
        st["ok"] += 1
    except Exception:
        st["fail"] += 1
for label, st in sorted(stats.items()):
    print(f"{label} {st['ok']} {st['fail']} {st['nokey']} {st['plain']}")
PY
      chmod 644 "$WORK/dec.py"
      sample() {
        local t c k
        while read -r t c k; do
          [ -n "$t" ] || continue
          has_col "$t" "$c" || continue
          docker exec "$CID" psql -U hr360admin -d hr360_operational -At -F "$(printf '\t')" -c \
            "SELECT '$t.$c', '$k', \"$c\" FROM $t WHERE \"$c\" IS NOT NULL AND \"$c\" <> '' ORDER BY random() LIMIT $SAMPLES" 2>/dev/null | tr -d '\r' || true
        done <<< "$ENC_COLUMNS"
      }
      # Degerler yalnizca bu iki konteyner arasinda borudan gecer; diske ve ekrana yazilmaz.
      # Anahtarlar komut satirinda gorunmesin diye 600 izinli gecici dosyadan (--env-file) verilir.
      printf 'TENANT_SECRET_KEYS=%s\nTENANT_SECRET_KEY=%s\n' "$TENANT_SECRET_KEYS" "$TENANT_SECRET_KEY" > "$WORK/keys.env"
      if ! report="$(sample | docker run --rm -i --network none --read-only --env-file "$WORK/keys.env" \
             -v "$WORK/dec.py:/dec.py:ro" "$CRYPTO_IMAGE" python /dec.py 2>/dev/null)"; then
        bad "sifre cozme denetimi calismadi (anahtar bicimi gecersiz olabilir)"
      elif [ -z "$report" ]; then
        echo "  uyari sifreli alanlarda ornek deger yok"
      else
        while read -r label nok nfail nnokey nplain; do
          extra=""; [ "$nplain" != 0 ] && extra=", $nplain eski duz metin"
          if [ "$nfail" = 0 ] && [ "$nnokey" = 0 ]; then ok "$label: $nok acildi$extra"
          else bad "$label: $nok acildi, $nfail acilamadi, $nnokey anahtari halkada yok$extra"; fi
        done <<< "$report"
      fi
    fi
  fi
fi

finish

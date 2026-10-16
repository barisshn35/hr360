#!/usr/bin/env bash
# HR360 Android uygulamasını (apps/mobile) Docker içinde derler ve imzalı APK üretir.
#
# Kullanım:
#   scripts/android-build.sh            İmzalı sürüm APK'sı → dist/HR360-<sürüm>.apk
#   scripts/android-build.sh --debug    Hata ayıklama APK'sı (imza anahtarı gerekmez)
#
# İmza anahtarı repoda DEĞİL: varsayılan ~/.hr360-android/ (HR360_ANDROID_DIR ile değişir). Yoksa ilk
# derlemede oluşturulur (release.jks + signing.properties, yalnızca sahibi okuyabilir; parola ekrana
# basılmaz). DİKKAT: Bu anahtar kaybolursa kurulu uygulamalar güncellenemez; yedeğini ayrı ve güvenli
# bir yerde saklayın. Uygulama tek APK'dır: ilk açılışta kurulumun adresini sorar.
set -euo pipefail
cd "$(dirname "$0")/.."
ROOT="$(pwd)"
die() { echo "HATA: $*" >&2; exit 1; }
info() { echo "==> $*"; }

MODE=release
if [ "${1:-}" = "--debug" ]; then MODE=debug; fi
VERSION="$(cat VERSION)"
KEYDIR="${HR360_ANDROID_DIR:-$HOME/.hr360-android}"
IMAGE=hr360-android-build
command -v docker >/dev/null || die "docker gerekli"

info "Derleme ortamı (Android SDK) hazırlanıyor"
docker build -q -t "$IMAGE" -f apps/mobile/Dockerfile.build apps/mobile >/dev/null

SIGN_ARGS=()
if [ "$MODE" = release ]; then
  mkdir -p "$KEYDIR"; chmod 700 "$KEYDIR"
  if [ ! -f "$KEYDIR/signing.properties" ]; then
    info "İmza anahtarı oluşturuluyor: $KEYDIR (yedekleyin!)"
    PASS="$(openssl rand -base64 30 | tr -d '/+=' | cut -c1-32)"
    docker run --rm -u "$(id -u):$(id -g)" -v "$KEYDIR:/k" "$IMAGE" keytool -genkeypair -v -keystore /k/release.jks -alias hr360 \
      -keyalg RSA -keysize 4096 -validity 10000 -storepass "$PASS" -keypass "$PASS" -dname "CN=HR360, O=HR360, C=TR" >/dev/null 2>&1 \
      || die "anahtar oluşturulamadı"
    umask 077
    printf 'storeFile=/keys/release.jks\nstorePassword=%s\nkeyAlias=hr360\nkeyPassword=%s\n' "$PASS" "$PASS" > "$KEYDIR/signing.properties"
    chmod 600 "$KEYDIR/signing.properties" "$KEYDIR/release.jks"
    unset PASS
  fi
  SIGN_ARGS=(-v "$KEYDIR:/keys:ro" -e HR360_ANDROID_SIGNING=/keys/signing.properties)
fi

info "APK derleniyor ($MODE, sürüm $VERSION)"
TASK=assembleRelease
if [ "$MODE" = debug ]; then TASK=assembleDebug; fi
# Konteyner kök olarak çalışır (npm/gradle kullanıcı kaydı ister); üretilen dosyalar sonda size devredilir.
docker run --rm -v "$ROOT:/repo" -v hr360-gradle:/root/.gradle "${SIGN_ARGS[@]}" -w /repo/apps/mobile "$IMAGE" sh -c "
  set -e
  trap 'chown -R $(id -u):$(id -g) /repo/apps/mobile' EXIT
  npm ci --no-audit --no-fund >/dev/null 2>&1 || npm install --no-audit --no-fund >/dev/null
  npx cap sync android >/dev/null
  cd android && ./gradlew --no-daemon -q $TASK
"

mkdir -p dist
SRC="apps/mobile/android/app/build/outputs/apk/$MODE/app-$MODE.apk"
[ -f "$SRC" ] || die "APK üretilemedi"
SUFFIX=""
if [ "$MODE" = debug ]; then SUFFIX="-debug"; fi
OUT="dist/HR360-$VERSION$SUFFIX.apk"
cp "$SRC" "$OUT"
if [ "$MODE" = release ]; then
  docker run --rm -v "$ROOT:/repo" "$IMAGE" apksigner verify --print-certs "/repo/$OUT" | grep -E "Signer #1 certificate (DN|SHA-256)" || die "imza doğrulanamadı"
fi
if [ "$MODE" = release ]; then
  # Sunucudan indirilebilsin: /indir/HR360.apk (gateway deploy/downloads'u salt okunur bağlar).
  cp "$OUT" deploy/downloads/HR360.apk.tmp && mv deploy/downloads/HR360.apk.tmp deploy/downloads/HR360.apk
  info "Sunucuda yayında: /indir/HR360.apk"
fi
info "Hazır: $OUT ($(du -h "$OUT" | cut -f1))"

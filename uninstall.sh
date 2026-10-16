#!/usr/bin/env bash
# HR360'i bu sunucudan kaldirir.
#
# Kullanim:
#   ./uninstall.sh [secenekler]
#
#   --backup-dir DIZIN  Son yedegin yazilacagi dizin (repo DISINDA olmali).
#                       Varsayilan: ~/hr360-yedek-YYYYAAGG-SSDDss
#   --no-backup         Yedek alma (veriler geri getirilemez).
#   --keep-data         Konteynerleri kaldir, veri birimlerine (veritabani, dosyalar,
#                       Kafka) dokunma. Ayni klasorle ./install.sh tekrar calisirsa veriler yerindedir.
#   --remove-images     HR360 imajlarini ve derleme onbellegini de sil (disk acar).
#   --purge             Kurulum klasorunu de sil (.env, sertifikalar, kod). Yedek klasoru kalir.
#   --dry-run           Hicbir sey degistirme; yapilacaklari listele.
#   -y, --yes           Onay sorma (otomasyon icin).
#   -h, --help          Bu yardim.
#
# Ne yapar (sirayla):
#   1. Veri silinecekse son yedegi alir: scripts/backup.sh arsivi (sifreli), .env, secrets/,
#      Keycloak realm sablonu, sertifikalar ve (varsa) posta servisinin DKIM anahtari. Yedek alinamazsa durur.
#   2. Bu kurulumun zamanlanmis islerini (yedek, bakim, sertifika yenileme, bakim calistiricisi) crontab'dan siler.
#   3. Butun konteynerleri (izleme, posta, test katmanlari dahil) durdurur ve kaldirir.
#   4. Veri birimlerini siler (--keep-data yoksa).
#   5. Istenirse imajlari ve klasoru siler.
#
# Sunucunun guvenlik duvarina, /etc/hosts'a ve Docker'in kendisine DOKUNMAZ.
set -euo pipefail

REPO="$(cd "$(dirname "$0")" && pwd)"
cd "$REPO"

BOLD=$(tput bold 2>/dev/null || echo "")
RESET=$(tput sgr0 2>/dev/null || echo "")
GREEN=$(tput setaf 2 2>/dev/null || echo "")
RED=$(tput setaf 1 2>/dev/null || echo "")

die() { echo "${RED}HATA:${RESET} $*" >&2; exit 1; }
say() { echo "==> $*"; }
envval() { [ -f .env ] && grep -E "^$1=" .env | tail -1 | cut -d= -f2- | tr -d "'\"" || true; }

BACKUP_DIR=""; BACKUP=1; KEEP_DATA=0; REMOVE_IMAGES=0; PURGE=0; DRY=0; ASSUME_YES=0
usage() { sed -n '2,27p' "$0" | sed 's/^# \{0,1\}//'; }
while [ $# -gt 0 ]; do
  case "$1" in
    --backup-dir) BACKUP_DIR="${2:-}"; shift 2 ;;
    --no-backup) BACKUP=0; shift ;;
    --keep-data) KEEP_DATA=1; shift ;;
    --remove-images) REMOVE_IMAGES=1; shift ;;
    --purge) PURGE=1; shift ;;
    --dry-run) DRY=1; shift ;;
    -y|--yes) ASSUME_YES=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) die "bilinmeyen secenek: $1 (yardim: ./uninstall.sh --help)" ;;
  esac
done

# Yalnizca veri silinirken yedek anlamli; --keep-data'da veri yerinde kalir.
[ "$KEEP_DATA" = 1 ] && BACKUP=0

command -v docker >/dev/null 2>&1 || die "docker bulunamadi"
docker compose version >/dev/null 2>&1 || die "docker compose bulunamadi"
[ -f docker-compose.yml ] || die "docker-compose.yml yok; bu betik kurulum klasorunde calismali ($REPO)"

PROJECT="$(docker compose config 2>/dev/null | awk '/^name:/{print $2; exit}')"
PROJECT="${PROJECT:-$(basename "$REPO")}"
# Butun profiller: izleme, posta, sertifika ve (kurulmussa) test katmanlari ayni projede.
PROFILE_ARGS=()
while read -r p; do [ -n "$p" ] && PROFILE_ARGS+=(--profile "$p"); done < <(docker compose config --profiles 2>/dev/null || true)
COMPOSE=(docker compose -p "$PROJECT" "${PROFILE_ARGS[@]}")
# Kok yetkisiyle yazilmis dosyalara (certbot) erismek icin kurulumun kendi imajlarindan biri.
HELPER_IMAGE="$(docker compose config --images 2>/dev/null | grep -m1 '^postgres' || echo alpine:3)"

PUBLIC_HOST="$(envval PUBLIC_URL | sed -E 's#^https?://##; s#[:/].*$##')"
CONFIRM_WORD="${PUBLIC_HOST:-$PROJECT}"

if [ -z "$BACKUP_DIR" ]; then BACKUP_DIR="$HOME/hr360-yedek-$(date +%Y%m%d-%H%M%S)"; fi
case "$(realpath -m "$BACKUP_DIR")/" in
  "$REPO"/*) [ "$PURGE" = 1 ] && die "--backup-dir kurulum klasorunun icinde; --purge onu da siler. Disarida bir dizin verin." ;;
esac

run() {
  # Deneme satirlari stderr'e: cagiranin >/dev/null yonlendirmesi onlari yutmasin.
  if [ "$DRY" = 1 ]; then printf '   [deneme] %s\n' "$*" >&2; else "$@"; fi
}

# --- Ozet ve onay -----------------------------------------------------------
volumes="$(docker volume ls -q --filter "label=com.docker.compose.project=$PROJECT" | sort || true)"
containers="$(docker ps -aq --filter "label=com.docker.compose.project=$PROJECT" | wc -l)"
echo
echo "${BOLD}HR360 kaldirma${RESET} — $REPO (proje: $PROJECT)"
echo "----------------------------------------------"
echo "  Konteyner:        $containers adet durdurulup kaldirilacak"
if [ "$KEEP_DATA" = 1 ]; then
  echo "  Veri birimleri:   KORUNACAK"
else
  echo "  Veri birimleri:   ${RED}SILINECEK${RESET} — $(echo "$volumes" | grep -c . || true) adet: $(echo "$volumes" | tr '\n' ' ')"
fi
if [ "$BACKUP" = 1 ]; then echo "  Son yedek:        $BACKUP_DIR"
elif [ "$KEEP_DATA" = 0 ]; then echo "  Son yedek:        ${RED}ALINMAYACAK (--no-backup)${RESET}"; fi
echo "  Zamanlanmis isler: crontab'daki bu kuruluma ait satirlar silinecek"
[ "$REMOVE_IMAGES" = 1 ] && echo "  Imajlar:          silinecek"
[ "$PURGE" = 1 ] && echo "  Klasor:           ${RED}SILINECEK${RESET} ($REPO)"
echo "  Dokunulmayacak:   guvenlik duvari, /etc/hosts, Docker'in kendisi"
echo

if [ "$DRY" = 1 ]; then
  echo "Deneme kipi: hicbir sey degistirilmeyecek."
elif [ "$ASSUME_YES" != 1 ]; then
  [ -t 0 ] || die "onay icin etkilesimli terminal gerekli (otomasyonda --yes)"
  read -r -p "Devam etmek icin '${CONFIRM_WORD}' yazin: " answer
  [ "$answer" = "$CONFIRM_WORD" ] || die "onaylanmadi; hicbir sey degistirilmedi"
fi

# --- 1) Son yedek -----------------------------------------------------------
tar_from_container() {
  # tar_from_container <baglama-kaynagi> <cikti.tar>: kok yetkili dosyalari konteyner icinden arsivler.
  local src="$1" out="$2"
  if [ "$DRY" = 1 ]; then printf '   [deneme] %s -> %s\n' "$src" "$out"; return 0; fi
  docker run --rm -v "$src":/kaynak:ro --entrypoint tar "$HELPER_IMAGE" -C /kaynak -cf - . > "$out"
  chmod 600 "$out"
}

if [ "$BACKUP" = 1 ]; then
  say "Son yedek aliniyor: $BACKUP_DIR"
  run mkdir -p "$BACKUP_DIR"
  run chmod 700 "$BACKUP_DIR"
  # Yedek icin veritabani ve nesne deposu calismali.
  run "${COMPOSE[@]}" up -d --wait postgres minio >/dev/null || true
  run scripts/backup.sh --out "$BACKUP_DIR" || die "yedek alinamadi; hicbir sey silinmedi. (Yedeksiz: --no-backup)"
  if [ -f .env ]; then run install -m 600 .env "$BACKUP_DIR/env.yedek"; fi
  if [ -d secrets ]; then run tar -cf "$BACKUP_DIR/secrets.tar" secrets; run chmod 600 "$BACKUP_DIR/secrets.tar"; fi
  # Arsive girmeyen yapilandirma: Keycloak realm sablonu (istemci sirlari) ve kendi sertifikaniz.
  if [ -f deploy/keycloak/realm-export.json ]; then run install -m 600 deploy/keycloak/realm-export.json "$BACKUP_DIR/realm-export.json"; fi
  if [ -n "$(ls -A deploy/nginx/tls 2>/dev/null | grep -vE '^((listen|redirect)\.conf|README\.md)$' || true)" ]; then
    run tar -cf "$BACKUP_DIR/nginx-tls.tar" -C deploy/nginx tls; run chmod 600 "$BACKUP_DIR/nginx-tls.tar"
  fi
  if [ -d deploy/letsencrypt/live ] || [ -d deploy/letsencrypt/accounts ]; then
    tar_from_container "$REPO/deploy/letsencrypt" "$BACKUP_DIR/letsencrypt.tar"
  fi
  if docker volume inspect "${PROJECT}_mailer-dkim" >/dev/null 2>&1; then
    tar_from_container "${PROJECT}_mailer-dkim" "$BACKUP_DIR/mailer-dkim.tar"
  fi
  if [ "$DRY" != 1 ]; then
    cat > "$BACKUP_DIR/GERI-YUKLEME.txt" <<EOF
HR360 yedegi — $(date '+%Y-%m-%d %H:%M') — $REPO

Icerik:
  hr360-*.tar.gz.enc   Veritabanlari ve dosyalar (env.yedek'teki BACKUP_ENCRYPTION_KEY ile sifreli)
  env.yedek            Kurulumun butun sirlari. Bu dosya olmadan arsiv ACILAMAZ; ayri ve guvenli saklayin.
  letsencrypt.tar      TLS sertifikasi (Let's Encrypt, varsa)
  nginx-tls.tar        Kendi TLS sertifikaniz (varsa)
  realm-export.json    Keycloak realm sablonu
  secrets.tar          Dosyadan sirlar (varsa)
  mailer-dkim.tar      Posta servisi DKIM anahtari (varsa)

Geri yukleme (yeni ya da ayni sunucuda):
  git clone https://github.com/barisshn35/hr360.git hr360 && cd hr360
  install -m 600 $BACKUP_DIR/env.yedek .env
  sudo tar -xf $BACKUP_DIR/letsencrypt.tar -C deploy/letsencrypt     # varsa
  tar -xf $BACKUP_DIR/secrets.tar                                    # varsa
  tar -xf $BACKUP_DIR/nginx-tls.tar -C deploy/nginx                  # varsa
  install -m 600 $BACKUP_DIR/realm-export.json deploy/keycloak/
  ./install.sh --yes
  scripts/restore.sh $BACKUP_DIR/hr360-<tarih>.tar.gz.enc --yes
EOF
    chmod 600 "$BACKUP_DIR/GERI-YUKLEME.txt"
  fi
fi

# --- 2) Zamanlanmis isler ---------------------------------------------------
# Isaretler: "# hr360-backup" (scripts/backup.sh), "# hr360-schedule:<is>" (scripts/schedule.sh),
# "# hr360-tls-retry" (scripts/tls.sh), "# hr360-ops-runner" (scripts/ops-runner.sh). Yalnizca bu
# klasore ait satirlar silinir.
if command -v crontab >/dev/null 2>&1 && crontab -l >/dev/null 2>&1; then
  current="$(crontab -l 2>/dev/null)"
  ours="$(printf '%s\n' "$current" | grep -E '# hr360-(backup|schedule:|tls-retry|ops-runner)' | grep -F "$REPO" || true)"
  if [ -n "$ours" ]; then
    say "Zamanlanmis isler kaldiriliyor ($(printf '%s\n' "$ours" | wc -l) satir)"
    if [ "$DRY" = 1 ]; then printf '%s\n' "$ours" | sed 's/^/   [deneme] silinecek: /'
    else printf '%s\n' "$current" | grep -vxF -f <(printf '%s\n' "$ours") | sed '/^$/d' | crontab -; fi
  fi
fi

# --- 3-4) Konteynerler ve veri birimleri --------------------------------------
say "Konteynerler durduruluyor ve kaldiriliyor"
down_args=(down --remove-orphans)
[ "$KEEP_DATA" = 0 ] && down_args+=(-v)
run "${COMPOSE[@]}" "${down_args[@]}"
if [ "$KEEP_DATA" = 0 ]; then
  # Test katmanlari gibi ana dosyada tanimli olmayan birimler compose down -v ile gitmez.
  left="$(docker volume ls -q --filter "label=com.docker.compose.project=$PROJECT" || true)"
  for v in $left; do run docker volume rm "$v" >/dev/null; done
fi

# --- 5) Imajlar ve klasor ---------------------------------------------------
if [ "$REMOVE_IMAGES" = 1 ]; then
  say "Imajlar siliniyor"
  imgs="$(docker image ls -q --filter "label=com.docker.compose.project=$PROJECT" | sort -u || true)"
  imgs="$imgs $(docker image ls --format '{{.Repository}}:{{.Tag}}' | grep -E "^${PROJECT}-" || true)"
  for i in $imgs; do run docker image rm -f "$i" >/dev/null || true; done
  if docker volume inspect "${PROJECT}-nuget" >/dev/null 2>&1; then run docker volume rm "${PROJECT}-nuget" >/dev/null; fi
fi

if [ "$PURGE" = 1 ]; then
  say "Kurulum klasoru siliniyor: $REPO"
  # certbot dosyalari kok yetkisiyle yazilir; once konteyner icinden bu kullaniciya devredilir.
  if [ -n "$(find "$REPO" ! -user "$(id -u)" -print -quit 2>/dev/null)" ]; then
    run docker run --rm -v "$REPO":/kurulum --entrypoint chown "$HELPER_IMAGE" -R "$(id -u):$(id -g)" /kurulum
  fi
  if [ "$DRY" != 1 ]; then
    cd /
    rm -rf "$REPO" || die "klasor tamamen silinemedi: $REPO"
  else
    printf '   [deneme] rm -rf %s\n' "$REPO"
  fi
fi

echo
if [ "$DRY" = 1 ]; then
  echo "${BOLD}Deneme bitti.${RESET} Gercekten kaldirmak icin --dry-run olmadan calistirin."
  exit 0
fi
echo "${GREEN}${BOLD}HR360 kaldirildi.${RESET}"
[ "$BACKUP" = 1 ] && echo "Yedek: $BACKUP_DIR (geri yukleme adimlari: $BACKUP_DIR/GERI-YUKLEME.txt)"
[ "$KEEP_DATA" = 1 ] && echo "Veri birimleri korundu; ./install.sh ile ayni verilerle yeniden baslar."
[ "$PURGE" = 1 ] || echo "Kurulum klasoru yerinde: $REPO (silmek icin --purge)"
exit 0

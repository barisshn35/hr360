#!/usr/bin/env bash
# Servisler arasi anahtari (INTERNAL_SERVICE_TOKEN, X-Internal-Token) kesintisiz degistirir.
#
# Kullanim:
#   scripts/rotate-internal-token.sh start
#       Yeni anahtar uretir ve gecis penceresini acar. Iki asamada servisleri yeniler:
#         1) INTERNAL_SERVICE_TOKEN=eski, INTERNAL_SERVICE_TOKEN_PREVIOUS=yeni
#            -> tum servisler iki anahtari da kabul eder, hala eskiyi gonderir.
#         2) INTERNAL_SERVICE_TOKEN=yeni, INTERNAL_SERVICE_TOKEN_PREVIOUS=eski
#            -> servisler yeniyi gonderir; henuz yenilenmemis olanlar da yeniyi kabul eder.
#       Her asamada servisler yeniden olusturulur ve calisir duruma gelmeleri beklenir.
#       Pencere acik kalir: eski anahtar hala gecerlidir.
#
#   scripts/rotate-internal-token.sh finish
#       Pencereyi kapatir: INTERNAL_SERVICE_TOKEN_PREVIOUS bosaltilir, servisler yenilenir;
#       bundan sonra yalnizca yeni anahtar gecerlidir. Yarida kalmis bir "start" sonrasinda
#       da guvenle calisir (once mevcut .env degerleriyle tum servisleri esitler).
#
#   scripts/rotate-internal-token.sh auto [SANIYE]
#       start + SANIYE (varsayilan 120) bekleme + finish.
#
#   scripts/rotate-internal-token.sh status
#       Pencerenin acik olup olmadigini gosterir (anahtar degerleri asla yazdirilmaz).
#
# Ortam degiskenleri:
#   COMPOSE_ARGS   docker compose'a eklenecek dosya/profil argumanlari. Varsayilan bos.
#                  Test ortami ornegi:
#                  COMPOSE_ARGS="-f docker-compose.yml -f deploy/testing/chat-mock.yml --profile ldaptest"
#   SERVICES       Yenilenecek servisler (bosluklu liste). Varsayilan: anahtari alan tum servisler.
#   WAIT_SECONDS   Her asamada servislerin hazir olmasi icin azami bekleme (varsayilan 180).
#
# NOT: SIEM_PSEUDONYM_KEY tanimli degilse SIEM disa aktarimindaki takma adlar bu anahtardan
# turetilir; anahtar degisince takma adlar da degisir (betik uyarir).
set -euo pipefail

cd "$(dirname "$0")/.."
# Docker Compose kabuktaki degiskenleri .env'e tercih eder; kabukta eski degerler kalmissa
# yeni anahtar uygulanmaz. Yonetilen degiskenler temizlenir.
unset INTERNAL_SERVICE_TOKEN INTERNAL_SERVICE_TOKEN_PREVIOUS
ENV_FILE=.env

die() { echo "HATA: $*" >&2; exit 1; }
[ -f "$ENV_FILE" ] || die ".env bulunamadi; once install.sh calistirin."
command -v openssl >/dev/null 2>&1 || die "openssl bulunamadi."

# Docker grup yetkisi bu oturumda henuz aktif degilse sudo ile devam edilir.
if ! declare -F docker >/dev/null && ! docker info >/dev/null 2>&1 && sudo docker info >/dev/null 2>&1; then
  docker() { command sudo docker "$@"; }
fi

read -r -a COMPOSE <<< "${COMPOSE_ARGS:-}"
dc() { docker compose ${COMPOSE[@]+"${COMPOSE[@]}"} "$@"; }

DEFAULT_SERVICES="employee-service workflow-service leave-service onboarding-service timeshift-service
engagement-service governance-service expense-service notification-service tenant-service ml-inference"
read -r -d '' -a SVCS <<< "${SERVICES:-$DEFAULT_SERVICES}" || true
[ "${#SVCS[@]}" -gt 0 ] || die "servis listesi bos"
WAIT_SECONDS="${WAIT_SECONDS:-180}"

get_env() { grep "^$1=" "$ENV_FILE" | tail -1 | cut -d= -f2- || true; }

# shellcheck source=lib/secrets.sh
. scripts/lib/secrets.sh
# Anahtar secrets/*.txt'ye tasindiysa (scripts/secrets-migrate.sh) bu betik konteyner ortamindaki
# degeri denetleyemez ve dosya degisince compose konteyneri yeniden olusturmaz: once geri alinmali.
if secret_in_file INTERNAL_SERVICE_TOKEN || secret_in_file INTERNAL_SERVICE_TOKEN_PREVIOUS; then
  case "${1:-}" in -h|--help|""|status) ;; *)
    die "INTERNAL_SERVICE_TOKEN secrets/ altinda (dosya modu). Once: scripts/secrets-migrate.sh restore INTERNAL_SERVICE_TOKEN INTERNAL_SERVICE_TOKEN_PREVIOUS && docker compose up -d --force-recreate; degisimden sonra yeniden tasiyabilirsiniz." ;;
  esac
fi

# set_env KEY VALUE: deger komut satirina (ps) ya da ekrana dusmesin diye awk'a ortamdan verilir;
# dosya yerinde yeniden yazilir (sahiplik ve izinler korunur).
set_env() {
  local tmp
  tmp="$(mktemp "${TMPDIR:-/tmp}/hr360-env.XXXXXX")"
  chmod 600 "$tmp"
  K="$1" V="$2" awk '
    BEGIN { k = ENVIRON["K"]; v = ENVIRON["V"]; done = 0 }
    index($0, k "=") == 1 { if (!done) { print k "=" v; done = 1 } ; next }
    { print }
    END { if (!done) print k "=" v }
  ' "$ENV_FILE" > "$tmp"
  cat "$tmp" > "$ENV_FILE"
  rm -f "$tmp"
}

# Konteynerin ortaminda KEY=VALUE var mi (deger yazdirilmaz).
container_has() { # container_has CID KEY VALUE (deger grep vb. komut satirina verilmez)
  local envs
  envs="$(docker inspect -f '{{range .Config.Env}}{{println .}}{{end}}' "$1" 2>/dev/null)" || return 1
  [[ $'\n'"$envs"$'\n' == *$'\n'"$2=$3"$'\n'* ]]
}

# Servisleri yeniden olusturur ve hepsinin beklenen anahtarlarla calisir (saglik denetimi
# varsa "healthy") olmasini bekler.
apply() { # apply ASAMA_ADI
  local cur prev err
  cur="$(get_env INTERNAL_SERVICE_TOKEN)"; prev="$(get_env INTERNAL_SERVICE_TOKEN_PREVIOUS)"
  echo "[$1] servisler yenileniyor: ${SVCS[*]}"
  err="$(mktemp "${TMPDIR:-/tmp}/hr360-compose.XXXXXX")"
  if ! dc up -d --no-deps "${SVCS[@]}" >/dev/null 2>"$err"; then
    tail -5 "$err" >&2; rm -f "$err"; die "docker compose up basarisiz"
  fi
  rm -f "$err"
  local deadline=$((SECONDS + WAIT_SECONDS)) pending svc cid st
  while :; do
    pending=()
    for svc in "${SVCS[@]}"; do
      cid="$(dc ps -q "$svc" 2>/dev/null | head -1)"
      if [ -z "$cid" ]; then pending+=("$svc"); continue; fi
      st="$(docker inspect -f '{{.State.Status}} {{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' "$cid" 2>/dev/null || true)"
      if [ "$st" != "running healthy" ] && [ "$st" != "running none" ]; then pending+=("$svc"); continue; fi
      container_has "$cid" INTERNAL_SERVICE_TOKEN "$cur" || { pending+=("$svc"); continue; }
      container_has "$cid" INTERNAL_SERVICE_TOKEN_PREVIOUS "$prev" || { pending+=("$svc"); continue; }
    done
    [ "${#pending[@]}" -eq 0 ] && break
    [ "$SECONDS" -lt "$deadline" ] || die "[$1] su servisler hazir olmadi: ${pending[*]} (pencere acik birakildi; sorunu giderip 'finish' calistirin)"
    sleep 3
  done
  # Uygulamanin dinlemeye baslamasi icin kisa pay (servislerde saglik denetimi tanimli degil).
  sleep "${SETTLE_SECONDS:-10}"
  echo "[$1] tamam."
}

siem_warning() {
  if [ -z "$(get_env SIEM_PSEUDONYM_KEY)" ] && [ -n "$(get_env SIEM_SYSLOG_ENDPOINT)" ]; then
    echo "UYARI: SIEM_PSEUDONYM_KEY tanimli degil; SIEM takma adlari anahtardan turetildigi icin degisecek." >&2
  fi
}

start() {
  local old new
  old="$(get_env INTERNAL_SERVICE_TOKEN)"
  [ -n "$old" ] || die "INTERNAL_SERVICE_TOKEN .env'de tanimli degil; degistirilecek anahtar yok."
  [ -z "$(get_env INTERNAL_SERVICE_TOKEN_PREVIOUS)" ] \
    || die "gecis penceresi zaten acik (INTERNAL_SERVICE_TOKEN_PREVIOUS dolu); once '$0 finish' calistirin."
  new="$(openssl rand -hex 32)"
  [ -n "$new" ] && [ "$new" != "$old" ] || die "yeni anahtar uretilemedi"
  siem_warning
  # 1) Herkes yeniyi de kabul etsin; gonderilen anahtar hala eski.
  set_env INTERNAL_SERVICE_TOKEN_PREVIOUS "$new"
  apply "1/2 yeni anahtar kabule aciliyor"
  # 2) Gonderilen anahtar yeni; eski hala kabul ediliyor.
  set_env INTERNAL_SERVICE_TOKEN "$new"
  set_env INTERNAL_SERVICE_TOKEN_PREVIOUS "$old"
  apply "2/2 yeni anahtara geciliyor"
  echo "Gecis penceresi acik: yeni anahtar kullaniliyor, eski anahtar hala kabul ediliyor."
  echo "Servisleri dogruladiktan sonra pencereyi kapatin: $0 finish"
}

finish() {
  [ -n "$(get_env INTERNAL_SERVICE_TOKEN)" ] || die "INTERNAL_SERVICE_TOKEN .env'de tanimli degil."
  if [ -z "$(get_env INTERNAL_SERVICE_TOKEN_PREVIOUS)" ]; then
    echo "Gecis penceresi zaten kapali."
    return 0
  fi
  # Yarida kalmis bir gecisten sonra once tum servisler .env'deki mevcut ciftle esitlenir.
  apply "esitleme"
  set_env INTERNAL_SERVICE_TOKEN_PREVIOUS ""
  apply "pencere kapatiliyor"
  echo "Tamam. Yalnizca yeni anahtar gecerli."
}

mode="${1:-}"
case "$mode" in
  start) start ;;
  finish) finish ;;
  auto)
    secs="${2:-120}"
    [[ "$secs" =~ ^[0-9]+$ ]] || die "gecersiz bekleme suresi: '$secs'"
    start
    echo "${secs} sn sonra pencere kapatilacak..."
    sleep "$secs"
    finish
    ;;
  status)
    if [ -z "$(get_env INTERNAL_SERVICE_TOKEN)" ]; then
      echo "INTERNAL_SERVICE_TOKEN: tanimli degil (ic uclar kapali)"
    else
      echo "INTERNAL_SERVICE_TOKEN: tanimli"
    fi
    if [ -n "$(get_env INTERNAL_SERVICE_TOKEN_PREVIOUS)" ]; then
      echo "Gecis penceresi: ACIK (ikinci anahtar kabul ediliyor; kapatmak icin: $0 finish)"
    else
      echo "Gecis penceresi: kapali"
    fi
    ;;
  -h|--help|"") sed -n '2,33p' "$0"; [ -n "$mode" ] || exit 1 ;;
  *) die "bilinmeyen mod '$mode' (start | finish | auto [SANIYE] | status)" ;;
esac

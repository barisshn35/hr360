#!/usr/bin/env bash
# Keycloak hr360 realm'inin kimlik guvenligi ayarlari (Guvenlik dalgasi 2A). Idempotent:
# her calistirmada ayni sonuca getirir; canli sisteme guvenle tekrar uygulanabilir.
#
# Kullanim:
#   scripts/keycloak-service-account.sh [apply]
#       client + policy (asagida), varsayilan.
#
#   scripts/keycloak-service-account.sh client
#       tenant-service'in Keycloak yonetim API'si icin servis hesabi:
#         * .env'de KEYCLOAK_TENANT_ADMIN_CLIENT_SECRET yoksa rastgele uretilip eklenir.
#         * hr360 realm'inde "hr360-tenant-admin" gizli istemcisi (yalnizca client_credentials,
#           tarayici/parola girisi kapali) olusturulur ya da anahtari esitlenir.
#         * Servis hesabina realm-management'in yalnizca gereken rolleri verilir:
#             manage-users              kullanici, rol atama, oturum, davet e-postasi
#             manage-realm              organizasyonlar (KC 25 bunu ister), realm rolleri,
#                                       giris akisi (passkey), gerekli eylemler, realm ayari
#             manage-identity-providers kurumsal SSO (Google / Entra ID)
#             manage-clients            dogrulanan ozel alan adinin hr360-web'e eklenmesi
#             view-events               supheli giris tespiti (LOGIN / LOGIN_ERROR olaylari)
#           Master realm'e hicbir yetkisi yoktur.
#         * Istemciyle jeton alinip yonetim API'si denenir; basariliysa master yonetici
#           parolasi tenant-service'e artik verilmez (KEYCLOAK_TENANT_ADMIN_LEGACY_* bos) ve
#           tenant-service yeniden olusturulur.
#
#   scripts/keycloak-service-account.sh policy
#       hr360 realm'i: parola politikasi (en az 12 karakter, kullanici adi/e-posta olamaz,
#       son 3 parola tekrar kullanilamaz) ve giris olaylari (yalnizca LOGIN, LOGIN_ERROR;
#       30 gun saklama - KVKK: yalnizca gereken olay turleri). Mevcut parolalar gecerli kalir;
#       politika parola degisiminde uygulanir.
#
#   scripts/keycloak-service-account.sh status
#       Servis hesabinin ve politikanin durumunu yazar (anahtar degeri asla yazdirilmaz).
#
# Ortam degiskenleri:
#   COMPOSE_ARGS        docker compose'a eklenecek dosya/profil argumanlari (test ortami:
#                       "-f docker-compose.yml -f deploy/testing/chat-mock.yml --profile ldaptest").
#   HR360_KC_CONTAINER  Keycloak'a "docker compose exec" yerine dogrudan bu konteynerle
#                       baglanilir (betigin gecici Keycloak'ta denenmesi icin).
#   HR360_NO_RESTART=1  tenant-service yeniden olusturulmaz.
#   HR360_PASSWORD_POLICY  Varsayilan politika yerine kullanilacak Keycloak politika ifadesi.
#
# Ardindan master realm'de kaba kuvvet korumasi acilabilir: scripts/keycloak-admin-access.sh
# (mevcut modla, orn. "open") servis hesabi tanimliysa korumayi kendisi acar.
set -euo pipefail

cd "$(dirname "$0")/.."
unset KEYCLOAK_TENANT_ADMIN_CLIENT_SECRET KEYCLOAK_TENANT_ADMIN_LEGACY_USER KEYCLOAK_TENANT_ADMIN_LEGACY_PASSWORD
ENV_FILE=.env
REALM=hr360
CLIENT_ID=hr360-tenant-admin
SA_USER="service-account-${CLIENT_ID}"
SA_ROLES="manage-users manage-realm manage-identity-providers manage-clients view-events"
POLICY="${HR360_PASSWORD_POLICY:-length(12) and notUsername and notEmail and passwordHistory(3)}"

sed_i() { if sed --version >/dev/null 2>&1; then sed -i "$@"; else sed -i '' "$@"; fi; }
die() { echo "HATA: $*" >&2; exit 1; }
info() { echo "==> $*"; }
[ -f "$ENV_FILE" ] || die ".env bulunamadi; once install.sh calistirin."

# Docker grup yetkisi bu oturumda henuz aktif degilse sudo ile devam edilir.
if ! declare -F docker >/dev/null && ! docker info >/dev/null 2>&1 && sudo docker info >/dev/null 2>&1; then
  docker() { command sudo docker "$@"; }
fi

read -r -a COMPOSE <<< "${COMPOSE_ARGS:-}"
dc() { docker compose ${COMPOSE[@]+"${COMPOSE[@]}"} "$@"; }

# shellcheck source=lib/secrets.sh
. scripts/lib/secrets.sh
# .env'de bossa secrets/<ad>.txt (scripts/secrets-migrate.sh) okunur.
get_env() { secret_get "$1"; }
has_env() { grep -q "^$1=" "$ENV_FILE"; }
set_env() { # set_env KEY VALUE (deger .env'e tirnaksiz yazilir; yalnizca guvenli karakterler)
  local k="$1" v="$2"
  if has_env "$k"; then sed_i "s#^${k}=.*#${k}=${v}#" "$ENV_FILE"; else printf '%s=%s\n' "$k" "$v" >> "$ENV_FILE"; fi
}
random_secret() {
  { openssl rand -base64 48 2>/dev/null || head -c 48 /dev/urandom | base64; } | tr -dc 'A-Za-z0-9' | head -c 40
}

# Keycloak konteynerinde kabuk komutu calistirir; stdin konteynere aktarilir (gizli
# degerler komut satirina degil stdin'e yazilir).
kc_sh() {
  if [ -n "${HR360_KC_CONTAINER:-}" ]; then docker exec -i "$HR360_KC_CONTAINER" sh -c "$1"
  else dc exec -T keycloak sh -c "$1"; fi
}

wait_keycloak() {
  for _ in $(seq 1 60); do
    kc_sh 'exec 3<>/dev/tcp/127.0.0.1/8080' </dev/null >/dev/null 2>&1 && return 0
    sleep 3
  done
  die "Keycloak 3 dakikada hazir olmadi ('docker compose logs keycloak')."
}

# Konteyner icinde master yoneticisiyle kcadm oturumu acan ortak baslangic.
# shellcheck disable=SC2016  # $ ifadeleri konteynerdeki kabukta acilir.
KC_LOGIN='K=/opt/keycloak/bin/kcadm.sh; C=/tmp/kcadm-sa.$$.config; trap "rm -f $C $C.sa" EXIT
for i in $(seq 1 20); do
  $K config credentials --config $C --server http://127.0.0.1:8080/auth --realm master \
    --user "$KEYCLOAK_ADMIN" --password "$KEYCLOAK_ADMIN_PASSWORD" >/dev/null 2>&1 && break
  [ "$i" = 20 ] && { echo "Keycloak yoneticisiyle oturum acilamadi" >&2; exit 1; }
  sleep 3
done'

ensure_client() {
  local secret created=0
  secret="$(get_env KEYCLOAK_TENANT_ADMIN_CLIENT_SECRET)"
  if [ -z "$secret" ]; then
    secret="$(random_secret)"
    [ "${#secret}" -ge 32 ] || die "rastgele anahtar uretilemedi"
    printf '\n# Keycloak yonetim API servis hesabi (hr360-tenant-admin, client_credentials).\nKEYCLOAK_TENANT_ADMIN_CLIENT_SECRET=%s\n' "$secret" >> "$ENV_FILE"
    created=1
    info "KEYCLOAK_TENANT_ADMIN_CLIENT_SECRET .env'e eklendi."
  fi
  wait_keycloak
  info "hr360-tenant-admin istemcisi ve servis hesabi rolleri ayarlaniyor..."
  # shellcheck disable=SC2016
  printf '%s\n' "$secret" | kc_sh "$KC_LOGIN"'
read -r SECRET
R='"$REALM"'; CID='"$CLIENT_ID"'
id=$($K get clients -r $R -q clientId=$CID --fields id --format csv --noquotes --config $C | head -1)
set -- -s enabled=true -s publicClient=false -s serviceAccountsEnabled=true -s standardFlowEnabled=false \
  -s implicitFlowEnabled=false -s directAccessGrantsEnabled=false -s frontchannelLogout=false \
  -s "secret=$SECRET" -s "name=tenant-service yonetim API servis hesabi"
if [ -z "$id" ]; then
  $K create clients -r $R -s clientId=$CID "$@" --config $C >/dev/null || exit 1
  echo "   istemci olusturuldu"
else
  $K update clients/$id -r $R "$@" --config $C || exit 1
  echo "   istemci guncellendi"
fi
roles=""; for role in '"$SA_ROLES"'; do roles="$roles --rolename $role"; done
$K add-roles -r $R --uusername '"$SA_USER"' --cclientid realm-management $roles --config $C || exit 1
# Dogrulama: istemciyle (client_credentials) jeton alip yonetim API okunur.
$K config credentials --config $C.sa --server http://127.0.0.1:8080/auth --realm $R --client $CID --secret "$SECRET" >/dev/null 2>&1 \
  || { echo "servis hesabiyla jeton alinamadi" >&2; exit 2; }
$K get organizations -r $R --fields id -q max=1 --config $C.sa >/dev/null 2>&1 \
  || { echo "servis hesabi organizasyonlari okuyamadi (yetki eksik)" >&2; exit 2; }
echo "   servis hesabi dogrulandi (jeton + organizasyon okuma)"
' || die "servis hesabi ayarlanamadi"

  # Servis hesabi calisiyor: master yonetici parolasi artik tenant-service'e verilmez.
  local legacy_changed=0
  if [ "$(get_env KEYCLOAK_TENANT_ADMIN_LEGACY_USER)" != "" ] || ! has_env KEYCLOAK_TENANT_ADMIN_LEGACY_USER \
     || ! has_env KEYCLOAK_TENANT_ADMIN_LEGACY_PASSWORD; then
    set_env KEYCLOAK_TENANT_ADMIN_LEGACY_USER ""
    set_env KEYCLOAK_TENANT_ADMIN_LEGACY_PASSWORD ""
    legacy_changed=1
    info "tenant-service'e master yonetici parolasi artik verilmeyecek (KEYCLOAK_TENANT_ADMIN_LEGACY_* bos)."
  fi
  if [ "${HR360_NO_RESTART:-0}" != 1 ] && [ -z "${HR360_KC_CONTAINER:-}" ] && { [ "$created" = 1 ] || [ "$legacy_changed" = 1 ]; }; then
    info "tenant-service yeni ayarlarla yeniden olusturuluyor..."
    dc up -d tenant-service >/dev/null || die "tenant-service yeniden baslatilamadi ('docker compose logs tenant-service')"
  fi
}

apply_policy() {
  wait_keycloak
  info "hr360 realm'i: parola politikasi ve giris olaylari ayarlaniyor..."
  # shellcheck disable=SC2016
  printf '%s\n' "$POLICY" | kc_sh "$KC_LOGIN"'
IFS= read -r POL
$K update realms/'"$REALM"' --config $C -s "passwordPolicy=$POL" -s eventsEnabled=true -s eventsExpiration=2592000 \
  -s '"'"'enabledEventTypes=["LOGIN","LOGIN_ERROR"]'"'"' || exit 1
echo "   parola politikasi: $POL"
echo "   giris olaylari: LOGIN, LOGIN_ERROR (30 gun)"
' || die "realm ayarlari uygulanamadi"
}

status() {
  if [ -n "$(get_env KEYCLOAK_TENANT_ADMIN_CLIENT_SECRET)" ]; then echo "Servis hesabi anahtari: .env'de tanimli"
  else echo "Servis hesabi anahtari: TANIMSIZ (tenant-service master yonetici parolasini kullaniyor)"; fi
  if has_env KEYCLOAK_TENANT_ADMIN_LEGACY_PASSWORD && [ -z "$(get_env KEYCLOAK_TENANT_ADMIN_LEGACY_PASSWORD)" ]; then
    echo "Master parola geri donusu: kapali"
  else
    echo "Master parola geri donusu: acik (gecis)"
  fi
  wait_keycloak
  # shellcheck disable=SC2016
  kc_sh "$KC_LOGIN"'
echo "Istemci: $($K get clients -r '"$REALM"' -q clientId='"$CLIENT_ID"' --fields clientId,serviceAccountsEnabled --format csv --noquotes --config $C | head -1)"
echo "Realm: $($K get realms/'"$REALM"' --fields passwordPolicy,eventsEnabled,eventsExpiration --format csv --noquotes --config $C)"
' </dev/null || die "Keycloak okunamadi"
}

case "${1:-apply}" in
  apply) ensure_client; apply_policy; echo "Tamam." ;;
  client) ensure_client; echo "Tamam." ;;
  policy) apply_policy; echo "Tamam." ;;
  status) status ;;
  *) die "bilinmeyen komut '${1}' (apply | client | policy | status)" ;;
esac

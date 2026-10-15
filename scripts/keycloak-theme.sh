#!/usr/bin/env bash
# Keycloak giris ekranini HR360 temasina (Obsidyen Zumrut) ve Turkceye alir.
#
# install.sh (kurulum ve guncelleme) bunu kendisi calistirir. Realm onceden
# olusturulmus sunucularda Keycloak ayari kendiliginden degistirmedigi icin
# gerekirse elle de calistirilabilir:
#
#   scripts/keycloak-theme.sh
#
# Kullanim:
#   scripts/keycloak-theme.sh            HR360 temasi + Turkce (varsayilan)
#   scripts/keycloak-theme.sh default    Keycloak'in kendi temasina doner
#
# Betik tekrar calistirilabilir; ayni ayari yeniden yazar.
set -euo pipefail
cd "$(dirname "$0")/.."

die() { echo "HATA: $*" >&2; exit 1; }
[ -f .env ] || die ".env bulunamadi; once install.sh calistirin."

# Docker grup yetkisi bu oturumda henuz aktif degilse sudo ile devam edilir.
if ! docker info >/dev/null 2>&1 && sudo docker info >/dev/null 2>&1; then
  docker() { command sudo docker "$@"; }
fi

mode="${1:-hr360}"
case "$mode" in
  hr360)   theme=hr360 ;;
  default) theme=keycloak ;;
  *) die "kullanim: $0 [hr360|default]" ;;
esac

[ -d deploy/keycloak/themes/hr360/login ] || die "deploy/keycloak/themes/hr360 bulunamadi; repoyu guncelleyin (git pull)."

echo "Keycloak bekleniyor..."
for _ in $(seq 1 60); do
  docker compose exec -T keycloak sh -c 'exec 3<>/dev/tcp/127.0.0.1/8080' >/dev/null 2>&1 && break
  sleep 3
done

if [ "$theme" = hr360 ] && ! docker compose exec -T keycloak test -f /opt/keycloak/themes/hr360/login/theme.properties; then
  echo "Tema klasoru konteynere bagli degil; Keycloak yeniden olusturuluyor..."
  docker compose up -d keycloak >/dev/null
  for _ in $(seq 1 60); do
    docker compose exec -T keycloak sh -c 'exec 3<>/dev/tcp/127.0.0.1/8080' >/dev/null 2>&1 && break
    sleep 3
  done
  docker compose exec -T keycloak test -f /opt/keycloak/themes/hr360/login/theme.properties \
    || die "Tema klasoru hala gorunmuyor; docker-compose.yml'de keycloak volumes altinda themes/hr360 satiri var mi?"
fi

# Giris ekraninin icerik guvenligi politikasi (Keycloak varsayilani yalnizca cerceveleri kisitlar).
# Keycloak sablonlari satir ici betik kullandigindan script-src 'unsafe-inline' gerekir.
KC_CSP="default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:; connect-src 'self'; frame-src 'self'; frame-ancestors 'self'; object-src 'none'; base-uri 'self'"

docker compose exec -T -e THEME="$theme" -e KC_CSP="$KC_CSP" keycloak sh -c '
  K=/opt/keycloak/bin/kcadm.sh; C=/tmp/kcadm-theme.config
  for i in $(seq 1 30); do
    $K config credentials --config $C --server http://127.0.0.1:8080/auth --realm master \
      --user "$KEYCLOAK_ADMIN" --password "$KEYCLOAK_ADMIN_PASSWORD" >/dev/null 2>&1 && break
    sleep 3
  done
  $K update realms/hr360 --config $C \
    -s "loginTheme=$THEME" -s "displayName=HR360" \
    -s internationalizationEnabled=true -s "supportedLocales=[\"tr\",\"en\"]" -s defaultLocale=tr \
    -s "browserSecurityHeaders.contentSecurityPolicy=$KC_CSP"
  rc=$?; rm -f $C; exit $rc
' || die "Keycloak ayari guncellenemedi"

# Keycloak tema dosyalarinin sikistirilmis kopyasini konteyner icinde saklar ve
# guncellemeden sonra da eskisini sunar. Onbellek doluysa temizlenip Keycloak
# yeniden baslatilir (giris ekrani ~20 sn kapali kalir).
if docker compose exec -T keycloak sh -c 'd=/opt/keycloak/data/tmp/kc-gzip-cache; [ -d "$d" ] && [ -n "$(ls -A "$d")" ] && rm -rf "$d"' >/dev/null 2>&1; then
  echo "Tema onbellegi temizlendi; Keycloak yeniden baslatiliyor..."
  docker compose restart keycloak >/dev/null
fi

if [ "$theme" = hr360 ]; then
  echo "Giris ekrani HR360 temasina ve Turkceye alindi."
else
  echo "Giris ekrani Keycloak'in varsayilan temasina dondu."
fi

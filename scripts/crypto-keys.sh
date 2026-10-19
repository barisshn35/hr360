#!/usr/bin/env bash
# Sifreli alan anahtari (TENANT_SECRET_KEY / TENANT_SECRET_KEYS) yenileme yardimcisi.
#
#   scripts/crypto-keys.sh list                    Anahtar kimlikleri ve etkin anahtar (degerler yazilmaz)
#   scripts/crypto-keys.sh status [--key KIMLIK]   Sutun basina hangi anahtarla kac deger var (salt okunur).
#                                                  --key: o anahtarla deger kaldiysa cikis kodu 1
#   scripts/crypto-keys.sh add [--id KIMLIK] [--activate]
#                                                  Yeni rastgele anahtar ekler. Varsayilan: listenin SONUNA
#                                                  (yalnizca acmak icin); --activate: basina (etkin).
#   scripts/crypto-keys.sh activate KIMLIK         Anahtari listenin basina alir (yeni yazimlar onunla)
#   scripts/crypto-keys.sh remove KIMLIK [--force] Anahtari cikarir; etkinse ya da o anahtarla deger
#                                                  kaldiysa reddeder
#   scripts/crypto-keys.sh rollout                 Sifreli alan kullanan 6 servisi yeni anahtarlarla yeniden
#                                                  olusturur (docker compose up -d --force-recreate)
#
# Yenileme sirasi (docs/guvenlik/README.md "Sifreleme anahtari yenileme"):
#   1) add            -> rollout   (tum servisler yeni anahtari tanir, henuz eskiyle yazar)
#   2) activate KIM   -> rollout   (yeni yazimlar enc2:KIM:; servisler acilistan ~1 dk sonra eski
#                                   degerleri partiler halinde yeniden sifreler, gunlukte yalnizca sayilar)
#   3) status --key k0             (0 kalana kadar; gerekirse servis gunlugune bakin)
#   4) remove k0      -> rollout
# Anahtar halkasi .env'de (ya da scripts/secrets-migrate.sh ile secrets/tenant_secret_keys.txt'de)
# TENANT_SECRET_KEYS="kimlik:base64,..." olarak tutulur; ilk eleman etkindir. Tanimli degilse tek anahtar
# "k0" = TENANT_SECRET_KEY'dir. TENANT_SECRET_KEY ayrica turetilmis anahtarlarin (SCORM cerezi, form
# jetonu, giris agi ozeti, SIEM takma adi) kokudur ve bu betik onu DEGISTIRMEZ.
set -euo pipefail

cd "$(dirname "$0")/.."
ENV_FILE="${ENV_FILE:-.env}"
# shellcheck source=lib/secrets.sh
. scripts/lib/secrets.sh

die() { echo "HATA: $*" >&2; exit 1; }
info() { echo "==> $*"; }
[ -f "$ENV_FILE" ] || die "$ENV_FILE bulunamadi; once install.sh calistirin."

if ! declare -F docker >/dev/null && ! docker info >/dev/null 2>&1 && sudo docker info >/dev/null 2>&1; then
  docker() { command sudo docker "$@"; }
fi
read -r -a COMPOSE <<< "${COMPOSE_ARGS:-}"
dc() { docker compose ${COMPOSE[@]+"${COMPOSE[@]}"} "$@"; }

CRYPTO_SERVICES="engagement-service governance-service tenant-service notification-service expense-service compensation-service"

# Sifreli sutunlar: tablo|sutun|tur. Tur: text (onceksiz deger de sifreli, k0), prefixed (onceksiz deger
# duz metin), bytes (bytea). Servislerin Program.cs'indeki encryptedColumns listesiyle ayni tutun.
COLUMNS="engagement_profiles|Iban|prefixed
engagement_profiles|NationalId|prefixed
expense_travel_requests|PassportCipher|prefixed
compensation_payroll_exports|Cipher|bytes
compensation_payslip_deliveries|SealedCopy|bytes
notification_vapid_keys|PrivateKeyEnc|text
platform_tenants|SmtpPasswordEncrypted|text
platform_settings|SmtpPasswordEncrypted|text
tenant_directory_settings|LdapBindPasswordEncrypted|text
governance_chat_apps|SlackBotTokenEnc|text
governance_chat_apps|SlackSigningSecretEnc|text
governance_chat_apps|TeamsAppPasswordEnc|text
governance_chat_apps|BotTokenEnc|text
governance_chat_apps|IncomingTokenEnc|text
governance_calendar_connections|AccessTokenEnc|text
governance_calendar_connections|RefreshTokenEnc|text
governance_provider_configs|ClientSecretEnc|text
governance_provisioning_configs|CredentialsEnc|text
governance_chat_context|TextEnc|text
governance_chat_exit_progress|AnswersEnc|text
governance_chat_pending|PayloadEnc|text
governance_document_requests|DocumentEnc|text
governance_ethics_reports|ContactEnc|text
governance_osh_exams|NotesEnc|text
governance_custom_field_values|Value|prefixed"

# --- anahtar halkasi ---------------------------------------------------------------------------
ring_get() { # halka "kimlik:base64,..." (TENANT_SECRET_KEYS yoksa k0:TENANT_SECRET_KEY)
  local r k
  r="$(secret_get TENANT_SECRET_KEYS)"
  if [ -z "$r" ]; then
    k="$(secret_get TENANT_SECRET_KEY)"
    [ -n "$k" ] || die "TENANT_SECRET_KEY de TENANT_SECRET_KEYS de tanimli degil."
    r="k0:$k"
  fi
  printf '%s' "$r" | tr -d ' '
}
ring_ids() { local r; r="$(ring_get)"; tr ',' '\n' <<< "$r" | cut -d: -f1; }
has_id() { local ids; ids="$(ring_ids)"; grep -qx -- "$1" <<< "$ids"; }
active_id() { local ids; ids="$(ring_ids)"; printf '%s\n' "${ids%%$'\n'*}"; }

ring_set() { # ring_set HALKA (ekrana basilmaz)
  local r="$1"
  if secret_in_file TENANT_SECRET_KEYS || { secret_in_file TENANT_SECRET_KEY && [ -z "$(env_file_get TENANT_SECRET_KEYS)" ]; }; then
    # Anahtarlar dosya modunda: halka da dosyaya (eski anahtar .env'e dusmesin) ve compose eki guncellenir.
    secret_write_file TENANT_SECRET_KEYS "$r"
    UNSET=1 env_file_set TENANT_SECRET_KEYS ""
    scripts/secrets-migrate.sh compose >/dev/null
    info "TENANT_SECRET_KEYS $(secret_file TENANT_SECRET_KEYS) dosyasina yazildi."
  else
    env_file_set TENANT_SECRET_KEYS "$r"
    info "TENANT_SECRET_KEYS $ENV_FILE dosyasina yazildi."
  fi
}

valid_id() { [[ "$1" =~ ^[A-Za-z0-9_-]{1,32}$ ]]; }

cmd_list() {
  local first=1 id
  if [ -z "$(secret_get TENANT_SECRET_KEYS)" ]; then echo "TENANT_SECRET_KEYS tanimli degil: tek anahtar k0 (TENANT_SECRET_KEY), eski bicim yazilir."; fi
  for id in $(ring_ids); do
    if [ "$first" = 1 ]; then echo "$id (etkin)"; first=0; else echo "$id"; fi
  done
}

cmd_add() {
  local id="" activate=0 r new
  while [ $# -gt 0 ]; do
    case "$1" in
      --id) id="${2:-}"; shift 2 ;;
      --activate) activate=1; shift ;;
      *) die "bilinmeyen secenek: $1" ;;
    esac
  done
  r="$(ring_get)"
  if [ -z "$id" ]; then
    id="k$(date +%Y%m%d)"
    while has_id "$id"; do id="${id}x"; done
  fi
  valid_id "$id" || die "gecersiz kimlik '$id' (harf, rakam, _ ve -; en fazla 32)."
  [ "$id" != "k0" ] || die "k0 eski anahtara ayrilmistir."
  if has_id "$id"; then die "'$id' zaten halkada."; fi
  command -v openssl >/dev/null 2>&1 || die "openssl bulunamadi."
  new="$(openssl rand -base64 32 | tr -d '\n')"
  [ "${#new}" -eq 44 ] || die "anahtar uretilemedi"
  if [ "$activate" = 1 ]; then ring_set "$id:$new,$r"; else ring_set "$r,$id:$new"; fi
  info "Anahtar '$id' eklendi$([ "$activate" = 1 ] && echo " ve etkin yapildi" || echo " (yalnizca acmak icin; etkinlestirmek: $0 activate $id)")."
  echo "Uygulamak icin: $0 rollout"
}

cmd_activate() {
  local id="${1:-}" r rest
  valid_id "$id" || die "kullanim: $0 activate KIMLIK"
  has_id "$id" || die "'$id' halkada yok ($0 list)."
  r="$(ring_get)"
  [ "$(active_id)" != "$id" ] || { info "'$id' zaten etkin."; return 0; }
  rest="$(tr ',' '\n' <<< "$r" | grep -v "^$id:" | paste -sd, -)"
  ring_set "$(tr ',' '\n' <<< "$r" | grep "^$id:"),$rest"
  info "'$id' etkin. Uygulamak icin: $0 rollout (servisler eski degerleri kendiliginden yeniden sifreler)"
}

cmd_remove() {
  local id="${1:-}" force="${2:-}" r rest
  valid_id "$id" || die "kullanim: $0 remove KIMLIK [--force]"
  has_id "$id" || die "'$id' halkada yok."
  [ "$(active_id)" != "$id" ] || die "'$id' etkin anahtar; once baska bir anahtari etkinlestirin."
  if [ "$force" != "--force" ]; then
    cmd_status --key "$id" >/dev/null || die "'$id' ile sifreli degerler duruyor ('$0 status'); yeniden sifreleme bitmeden cikarilamaz (--force veri kaybettirir)."
  fi
  r="$(ring_get)"
  rest="$(tr ',' '\n' <<< "$r" | grep -v "^$id:" | paste -sd, -)"
  ring_set "$rest"
  info "'$id' cikarildi. Uygulamak icin: $0 rollout"
  if [ "$id" = "k0" ]; then
    echo "NOT: TENANT_SECRET_KEY turetilmis anahtarlarin kokudur ve yerinde kaldi; eski anahtar sizdiysa onu da degistirin (docs/guvenlik/README.md)."
  fi
}

psql_ro() { dc exec -T postgres psql -U hr360admin -d hr360_operational -v ON_ERROR_STOP=1 -AtF '|' "$@"; }

status_sql() { # mevcut tablo/sutunlar icin UNION sorgusu (degerler degil, yalnizca kimlik ve sayi)
  local existing t c kind expr parts=()
  existing="$(psql_ro -c "SELECT table_name || '|' || column_name FROM information_schema.columns WHERE table_schema = 'public'")"
  while IFS='|' read -r t c kind; do
    grep -qx "$t|$c" <<< "$existing" || continue
    case "$kind" in
      bytes) expr="CASE WHEN substring(\"$c\" from 1 for 4) = '\\x48524532'::bytea AND length(\"$c\") > 5 THEN convert_from(substring(\"$c\" from 6 for get_byte(\"$c\", 4)), 'SQL_ASCII') ELSE 'k0' END" ;;
      text) expr="CASE WHEN \"$c\" LIKE 'enc2:%' THEN split_part(\"$c\", ':', 2) ELSE 'k0' END" ;;
      *) expr="CASE WHEN \"$c\" LIKE 'enc2:%' THEN split_part(\"$c\", ':', 2) WHEN \"$c\" LIKE 'enc1:%' THEN 'k0' ELSE '(duz metin)' END" ;;
    esac
    if [ "$kind" = bytes ]; then
      parts+=("SELECT '$t.$c', $expr, count(*) FROM $t WHERE \"$c\" IS NOT NULL GROUP BY 2")
    else
      parts+=("SELECT '$t.$c', $expr, count(*) FROM $t WHERE \"$c\" IS NOT NULL AND \"$c\" <> '' GROUP BY 2")
    fi
  done <<< "$COLUMNS"
  [ "${#parts[@]}" -gt 0 ] || return 0
  local IFS=$'\n'
  printf '%s\n' "${parts[*]/%/ UNION ALL}" | sed '$ s/ UNION ALL$//'
  echo "ORDER BY 1, 2;"
}

cmd_status() {
  local only="" sql rows left=0 col id n
  if [ "${1:-}" = "--key" ]; then only="${2:-}"; valid_id "$only" || die "kullanim: $0 status --key KIMLIK"; fi
  sql="$(status_sql)"
  [ -n "$sql" ] || { echo "Sifreli sutun bulunamadi."; return 0; }
  rows="$(psql_ro <<< "$sql")"
  printf '%-58s %-14s %s\n' "SUTUN" "ANAHTAR" "DEGER"
  while IFS='|' read -r col id n; do
    [ -n "$col" ] || continue
    printf '%-58s %-14s %s\n' "$col" "$id" "$n"
    if [ -n "$only" ] && [ "$id" = "$only" ]; then left=$((left + n)); fi
  done <<< "$rows"
  echo "Etkin anahtar: $(active_id); halka: $(ring_ids | paste -sd' ' -)"
  if [ -n "$only" ]; then
    echo "'$only' ile sifreli deger: $left"
    [ "$left" -eq 0 ] || return 1
  fi
}

cmd_rollout() {
  read -r -a svcs <<< "$CRYPTO_SERVICES"
  # Kapali modulun servisi baslatilmaz; acilinca yeni anahtarla yeniden sifreler.
  if [ -f scripts/lib/modules.sh ]; then
    . scripts/lib/modules.sh
    read -r -a svcs <<< "$(mod_filter_services "${svcs[@]}")"
  fi
  info "Yeniden olusturuluyor: ${svcs[*]}"
  dc up -d --no-deps --force-recreate "${svcs[@]}"
  echo "Yeniden sifreleme servis acilisindan ~1 dk sonra baslar. Ilerleme: $0 status"
}

mode="${1:-}"
[ $# -gt 0 ] && shift
case "$mode" in
  list) cmd_list ;;
  status) cmd_status "$@" ;;
  add) cmd_add "$@" ;;
  activate) cmd_activate "$@" ;;
  remove) cmd_remove "$@" ;;
  rollout) cmd_rollout ;;
  -h|--help|"") sed -n '2,28p' "$0"; [ -n "$mode" ] || exit 1 ;;
  *) die "bilinmeyen mod '$mode' (list | status | add | activate | remove | rollout)" ;;
esac

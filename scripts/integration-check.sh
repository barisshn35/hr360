#!/usr/bin/env bash
# Dalga 12 (madde 91): yapilandirilmis dis entegrasyonlarin baglanti kontrolu. Uygulamanin KENDI
# "baglantiyi test et" uclarini cagirir; saglayicilara dogrudan gitmez. Hicbir sir (jeton, parola,
# gizli anahtar) ekrana basilmaz: yalnizca saglayici adi, durum ve uygulamanin dondurdugu ileti.
#
# Kullanim:
#   HR360_TOKEN=<erisim jetonu> scripts/integration-check.sh [--url https://hr.sirket.com] [--send]
#   scripts/integration-check.sh --login admin          # gelistirme/test: tests/credentials.json ile giris
#
#   --url URL     Adres (varsayilan: HR360_BASE_URL, yoksa .env PUBLIC_ORIGIN, yoksa http://localhost)
#   --login WHO   tests/support/hr360_login.py ile jeton al (playwright gerekir; yalnizca test ortami)
#   --send        Gercek ileti gonderen testleri de calistir: sohbet uygulamalarinda size dogrudan
#                 mesaj, kanal bildirimlerinde kanala deneme mesaji, webhook'lara "ping" teslimati,
#                 bu kullanicinin cihazlarina deneme anlik bildirimi. Varsayilan: yalnizca okuma +
#                 ileti gondermeyen baglanti testleri (jeton alma, salt okunur API cagrisi, SMTP oturumu).
#   --json        Sonuclari JSON satirlari olarak yaz (izleme/CI icin)
#
# Jeton: tarayicida HR360'a sirket yoneticisi (IK + sirket yoneticisi) olarak girip gelistirici
# araclarinda "openid-connect/token" yanitindaki access_token (5 dk gecerli). Jeton degiskende
# tutulur, komut satirina ya da ciktiya yazilmaz.
#
# Cikis kodu: 0 = yapilandirilmis her sey basarili (ya da hic yapilandirma yok), 1 = en az bir hata,
# 2 = kullanim/oturum hatasi. Ayrinti ve elle dogrulama adimlari: docs/entegrasyonlar/dogrulama.md
set -euo pipefail
cd "$(dirname "$0")/.."

URL="${HR360_BASE_URL:-}"
LOGIN=""
SEND=0
JSON=0
while [ $# -gt 0 ]; do
  case "$1" in
    --url) URL="${2:-}"; shift 2 ;;
    --login) LOGIN="${2:-}"; shift 2 ;;
    --send) SEND=1; shift ;;
    --json) JSON=1; shift ;;
    -h|--help) sed -n '2,26p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "bilinmeyen secenek: $1 (--help)" >&2; exit 2 ;;
  esac
done

if [ -z "$URL" ] && [ -f .env ]; then
  URL="$(grep -E '^PUBLIC_ORIGIN=' .env | tail -1 | cut -d= -f2- | tr -d '"'"'" || true)"
fi
URL="${URL:-http://localhost}"
URL="${URL%/}"

command -v curl >/dev/null || { echo "curl gerekli" >&2; exit 2; }
command -v python3 >/dev/null || { echo "python3 gerekli" >&2; exit 2; }

TOKEN="${HR360_TOKEN:-}"
if [ -n "$LOGIN" ]; then
  TOKEN="$(HR360_BASE_URL="$URL" python3 tests/support/hr360_login.py "$LOGIN" 2>/dev/null \
    | python3 -c 'import json,sys; print(next(iter(json.load(sys.stdin).values())))' 2>/dev/null || true)"
fi
[ -n "$TOKEN" ] || { echo "Jeton yok: HR360_TOKEN ortam degiskeni ya da --login (--help)" >&2; exit 2; }
unset HR360_TOKEN

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
# Jeton curl'e dosyadan verilir (ps ciktisinda gorunmez).
printf 'header = "Authorization: Bearer %s"\n' "$TOKEN" > "$TMP/auth"
chmod 600 "$TMP/auth"
unset TOKEN

FAILED=0
OKS=0
SKIPS=0

# call METHOD PATH -> govde $TMP/body, HTTP kodu stdout
call() {
  curl -sS -o "$TMP/body" -w '%{http_code}' -K "$TMP/auth" -X "$1" -H 'Content-Type: application/json' \
    -H 'X-HR360-Lang: tr' --max-time 40 "$URL$2" 2>/dev/null || true
}

# js EXPR: $TMP/body JSON'u uzerinde python ifadesi (d = govde); hata -> bos
js() { python3 -c "import json,sys
try:
    d = json.load(open(sys.argv[1]))
except Exception:
    d = None
try:
    r = $1
except Exception:
    r = ''
if isinstance(r, (list, dict)): print(json.dumps(r, ensure_ascii=False))
elif r is None: print('')
else: print(r)" "$TMP/body"; }

report() { # durum alan ad ileti
  local status="$1" area="$2" name="$3" msg="${4:-}"
  msg="$(printf '%s' "$msg" | tr '\n' ' ' | cut -c1-220)"
  case "$status" in
    OK) OKS=$((OKS + 1)) ;;
    HATA) FAILED=1 ;;
    *) SKIPS=$((SKIPS + 1)) ;;
  esac
  if [ "$JSON" = 1 ]; then
    python3 -c 'import json,sys; print(json.dumps(dict(zip(["status","area","name","message"], sys.argv[1:])), ensure_ascii=False))' \
      "$status" "$area" "$name" "$msg"
  else
    printf '%-5s %-20s %-34s %s\n' "$status" "$area" "$name" "$msg"
  fi
}

# Oturum kontrolu
code="$(call GET /api/tenant/my-tenant)"
case "$code" in
  200) ;;
  401) echo "Jeton gecersiz ya da suresi dolmus (HTTP 401)" >&2; exit 2 ;;
  000) echo "$URL adresine ulasilamadi" >&2; exit 2 ;;
  *) echo "Oturum kontrolu basarisiz (HTTP $code)" >&2; exit 2 ;;
esac
[ "$JSON" = 1 ] || { echo "HR360 entegrasyon kontrolu: $URL ($(js "d.get('name','')"))"; [ "$SEND" = 1 ] || echo "(ileti gonderen testler atlandi; --send ile calisir)"; echo; }

# ---------------------------------------------------------------- SMTP (kiracinin ozel sunucusu)
code="$(call POST /api/tenant/my-tenant/smtp-test)"
if [ "$code" = 200 ]; then
  if [ "$(js "d['configured']")" = True ]; then
    if [ "$(js "d['ok']")" = True ]; then report OK SMTP "ozel sunucu" "$(js "d['message']")"
    else report HATA SMTP "ozel sunucu" "$(js "d['message']")"; fi
  else report ATLA SMTP "ozel sunucu" "$(js "d['message']")"; fi
else report ATLA SMTP "ozel sunucu" "HTTP $code (sirket yoneticisi yetkisi gerekir ya da surum eski)"; fi

# ---------------------------------------------------------------- Sohbet uygulamalari (Slack/Teams/Mattermost/Rocket.Chat)
code="$(call GET /api/governance/chat-apps)"
if [ "$code" = 200 ]; then
  js "'\n'.join('%s\t%s\t%s\t%s' % (a['id'], a['platform'], a['name'], 'on' if a['isEnabled'] else 'off') for a in d)" > "$TMP/apps"
  [ -s "$TMP/apps" ] || report ATLA "Sohbet" "-" "yapilandirilmis uygulama yok"
  while IFS=$'\t' read -r id platform name enabled; do
    [ -n "$id" ] || continue
    if [ "$enabled" != on ]; then report ATLA "Sohbet/$platform" "$name" "kapali"; continue; fi
    if [ "$SEND" = 1 ]; then
      c="$(call POST "/api/governance/chat-apps/$id/test")"
      if [ "$c" = 200 ]; then report OK "Sohbet/$platform" "$name" "deneme mesaji gonderildi"
      else report HATA "Sohbet/$platform" "$name" "HTTP $c: $(js "d.get('message','')")"; fi
    else
      call GET /api/governance/chat-apps >/dev/null
      err="$(js "next((a.get('lastError') or '') for a in d if a['id'] == '$id')")"
      if [ -z "$err" ]; then report OK "Sohbet/$platform" "$name" "son hata yok (gonderim testi icin --send)"
      else report HATA "Sohbet/$platform" "$name" "son hata: $err"; fi
    fi
  done < "$TMP/apps"
else report ATLA "Sohbet" "-" "HTTP $code"; fi

# ---------------------------------------------------------------- Takvim ve toplanti (Google, Microsoft 365, Zoom)
code="$(call GET /api/governance/calendar/providers)"
if [ "$code" = 200 ]; then
  for p in $(js "' '.join(x['provider'] for x in d if x['configured'])"); do
    c="$(call POST "/api/governance/calendar/providers/$p/test")"
    if [ "$c" = 200 ] && [ "$(js "d['ok']")" = True ]; then
      report OK "Takvim/$p" "istemci bilgileri" "$( [ "$(js "d['enabled']")" = True ] && echo "baglanti basarili" || echo "baglanti basarili (saglayici kapali)")"
    else report HATA "Takvim/$p" "istemci bilgileri" "HTTP $c: $(js "d.get('message','')")"; fi
  done
  call GET /api/governance/calendar/providers >/dev/null
  [ -n "$(js "' '.join(x['provider'] for x in d if x['configured'])")" ] || report ATLA "Takvim" "-" "yapilandirilmis saglayici yok"
else report ATLA "Takvim" "-" "HTTP $code"; fi

# ---------------------------------------------------------------- Hesap acma/kapatma (Google Workspace / Microsoft 365)
code="$(call GET /api/governance/account-provisioning)"
if [ "$code" = 200 ] && [ "$(js "d['enabled']")" = True ]; then
  provs="$(js "' '.join(x['provider'] for x in d['providers'] if x['configured'])")"
  [ -n "$provs" ] || report ATLA "Hesap saglama" "-" "yapilandirilmis saglayici yok"
  for p in $provs; do
    c="$(call POST "/api/governance/account-provisioning/configs/$p/test")"
    if [ "$c" = 200 ] && [ "$(js "d['ok']")" = True ]; then report OK "Hesap/$p" "dizin API" "$(js "d['message']")"
    else report HATA "Hesap/$p" "dizin API" "HTTP $c: $(js "d.get('message','')")"; fi
  done
elif [ "$code" = 200 ]; then report ATLA "Hesap saglama" "-" "kurulumda kapali (ACCOUNT_PROVISIONING_ENABLED)"
else report ATLA "Hesap saglama" "-" "HTTP $code"; fi

# ---------------------------------------------------------------- LDAP / Active Directory
code="$(call GET /api/tenant/my-tenant/directory/settings)"
if [ "$code" = 200 ]; then
  if [ "$(js "d['ldap']['enabled']")" = True ]; then
    c="$(call POST /api/tenant/my-tenant/directory/ldap/test)"
    if [ "$c" = 200 ]; then report OK "LDAP/AD" "$(js "d.get('entries','?')") kayit" "baglanti ve arama basarili $(js "d.get('warning') or ''")"
    else report HATA "LDAP/AD" "-" "HTTP $c: $(js "d.get('message','')")"; fi
  else report ATLA "LDAP/AD" "-" "kapali"; fi
else report ATLA "LDAP/AD" "-" "HTTP $code (sirket yoneticisi yetkisi gerekir)"; fi

# ---------------------------------------------------------------- SCIM
code="$(call GET /api/tenant/my-tenant/directory/scim-tokens)"
if [ "$code" = 200 ]; then
  n="$(js "len([t for t in d if not t.get('revokedAt')])")"
  if [ "${n:-0}" -gt 0 ]; then report OK "SCIM" "$n etkin jeton" "son kullanim: $(js "max([t.get('lastUsedAt') or '' for t in d if not t.get('revokedAt')] or [''] ) or 'hic'")"
  else report ATLA "SCIM" "-" "etkin jeton yok"; fi
else report ATLA "SCIM" "-" "HTTP $code"; fi
if [ -n "${SCIM_TOKEN:-}" ]; then
  printf 'header = "Authorization: Bearer %s"\n' "$SCIM_TOKEN" > "$TMP/scim"
  c="$(curl -sS -o "$TMP/body" -w '%{http_code}' -K "$TMP/scim" --max-time 20 "$URL/api/tenant/scim/v2/ServiceProviderConfig" 2>/dev/null || true)"
  rm -f "$TMP/scim"
  if [ "$c" = 200 ]; then report OK "SCIM" "ServiceProviderConfig" "SCIM_TOKEN ile erisim basarili"
  else report HATA "SCIM" "ServiceProviderConfig" "HTTP $c"; fi
fi

# ---------------------------------------------------------------- Web Push
code="$(call GET /api/notification/notifications/push/public-key)"
if [ "$code" = 200 ] && [ -n "$(js "d.get('publicKey')")" ]; then report OK "Web Push" "VAPID" "genel anahtar hazir"
else report HATA "Web Push" "VAPID" "HTTP $code"; fi
if [ "$SEND" = 1 ]; then
  c="$(call POST /api/notification/notifications/push/test)"
  if [ "$c" = 200 ]; then report OK "Web Push" "deneme bildirimi" "kuyruga alindi (cihazinizda gorunmeli)"
  else report HATA "Web Push" "deneme bildirimi" "HTTP $c: $(js "d.get('message','')")"; fi
fi

# ---------------------------------------------------------------- Kanal bildirimleri ve webhook'lar (yalnizca --send)
if [ "$SEND" = 1 ]; then
  code="$(call GET /api/governance/integrations)"
  if [ "$code" = 200 ]; then
    js "'\n'.join('%s\t%s\t%s' % (i['id'], i['kind'], i['name']) for i in d)" > "$TMP/ints"
    while IFS=$'\t' read -r id kind name; do
      [ -n "$id" ] || continue
      c="$(call POST "/api/governance/integrations/$id/test")"
      if [ "$c" = 200 ]; then report OK "Kanal/$kind" "$name" "deneme mesaji gonderildi"
      else report HATA "Kanal/$kind" "$name" "HTTP $c: $(js "d.get('message','')")"; fi
    done < "$TMP/ints"
  fi
  code="$(call GET /api/governance/webhooks)"
  if [ "$code" = 200 ]; then
    js "'\n'.join('%s\t%s' % (w['id'], w['name']) for w in d if w.get('isEnabled'))" > "$TMP/hooks"
    while IFS=$'\t' read -r id name; do
      [ -n "$id" ] || continue
      c="$(call POST "/api/governance/webhooks/$id/ping")"
      if [ "$c" = 200 ] && [ "$(js "d.get('ok')")" = True ]; then report OK "Webhook" "$name" "HTTP $(js "d.get('lastStatus')")"
      else report HATA "Webhook" "$name" "HTTP $c / alici $(js "d.get('lastStatus')")"; fi
    done < "$TMP/hooks"
  fi
fi

# ---------------------------------------------------------------- LLM (yalnizca yapilandirma durumu)
code="$(call GET /api/governance/ai/settings)"
if [ "$code" = 200 ]; then
  if [ "$(js "d['configured']")" = True ]; then
    report OK "LLM" "$(js "d.get('provider')")" "model $(js "d.get('model')"), kiracida $( [ "$(js "d['tenantEnabled']")" = True ] && echo acik || echo kapali)"
  elif [ -n "$(js "d.get('configError') or ''")" ]; then report HATA "LLM" "-" "$(js "d.get('configError')")"
  else report ATLA "LLM" "-" "yapilandirilmamis (kural tabanli calisir)"; fi
fi

[ "$JSON" = 1 ] || { echo; echo "Ozet: $OKS basarili, $SKIPS atlandi, $([ "$FAILED" = 1 ] && echo "HATA VAR" || echo "hata yok")"; }
exit "$FAILED"

#!/usr/bin/env bash
# HR360 şirket kipi: tek şirket (single) ya da çok şirketli (multi). Arayüzde Platform › Bakım › Şirket kipi
# bu betiği (bakım çalıştırıcısı üzerinden) çağırır.
#
# Kullanım:
#   scripts/tenancy.sh status   Makine için: "tenancy=single|multi" ve "tenants=N" (veritabanına ulaşılamazsa "?")
#   scripts/tenancy.sh multi    Çok şirketli kipe geç (şirket listesi, yeni şirket ve herkese açık kayıt açılır)
#   scripts/tenancy.sh single   Tek şirket kipine geç (yalnızca tam olarak bir şirket varken)
#
# Kip .env'deki HR360_TENANCY değişkenidir; yoksa "multi" sayılır (eski kurulumlar değişmeden çalışır).
# Geçişte servisler "docker compose up -d" ile yeni ortamı alır. Veri silinmez; şirket adı
# (HR360_COMPANY_NAME) olduğu gibi kalır. Ayrıntı: docs/isletim/README.md "Modüller ve şirket kipi".
set -euo pipefail
cd "$(dirname "$0")/.."
ENV_FILE=.env
# .env yardımcıları (_mod_env_get, _mod_env_write: geçici dosyaya yazıp yerine taşır).
. scripts/lib/modules.sh

die() { echo "HATA: $*" >&2; exit 1; }

current_mode() {
  local m; m="$(_mod_env_get HR360_TENANCY)"
  if [ "$m" = single ]; then echo single; else echo multi; fi
}

# platform_tenants satır sayısı; postgres çalışmıyorsa ya da tablo yoksa "?".
tenant_count() {
  local n
  n="$(docker compose exec -T postgres psql -U hr360admin -d hr360_operational -Atc 'SELECT count(*) FROM platform_tenants' 2>/dev/null | tr -d '[:space:]')" || n=""
  if [[ "$n" =~ ^[0-9]+$ ]]; then echo "$n"; else echo "?"; fi
}

# Servisler yeni HR360_TENANCY değerini alsın: compose değişen ortamı görüp yalnızca ilgili konteynerleri yeniden oluşturur.
apply() {
  _mod_env_write HR360_TENANCY "$1"
  docker compose up -d >/dev/null
}

cmd_status() {
  echo "tenancy=$(current_mode)"
  echo "tenants=$(tenant_count)"
}

cmd_multi() {
  apply multi
  echo "Çok şirketli kip açık: şirket listesi, yeni şirket ve herkese açık kayıt kullanılabilir."
}

cmd_single() {
  local n; n="$(tenant_count)"
  [ "$n" != "?" ] || die "şirket sayısı okunamadı (veritabanı çalışıyor mu?); tek şirket kipine geçilmedi"
  [ "$n" = 1 ] || die "tek şirket kipine yalnızca tam olarak bir şirket varken geçilebilir (şu an: $n şirket)"
  apply single
  echo "Tek şirket kipi açık."
}

case "${1:-status}" in
  status) cmd_status ;;
  multi) cmd_multi ;;
  single) cmd_single ;;
  *) sed -n '2,12p' "$0"; exit 2 ;;
esac

# shellcheck shell=bash
# HR360 isteğe bağlı modülleri: tanımlar ve .env yardımcıları (scripts/modules.sh, install.sh, update.sh ve
# servisleri adıyla yeniden başlatan betikler kullanır). Kaynak: docs/isletim/README.md "Modüller".
#
# Bir modül kapatılınca .env'e HR360_MOD_<MODÜL>=0 yazılır; docker-compose.yml'de modülün servisleri
# scale: ${HR360_MOD_<MODÜL>:-1} taşır. Değişken yoksa 1 kopya çalışır: eski kurulumlar hiçbir taşıma
# yapmadan tüm modüllerle devam eder. 0 olunca "docker compose up -d" konteyneri kendiliğinden kaldırır.
# (Profil kullanılmaz: COMPOSE_PROFILES tanımlıyken profilli servisler o profil açılmadan başlamaz.)
# HR360_MODULES servislere ve arayüze etkin modül listesini bildirir ("all" ya da virgüllü liste).
#
# Çekirdek (her zaman): tenant, employee, organization, workflow, leave, notification, governance,
# engagement (kişisel profil), expense (belgeler, İK vakaları) ve altyapı.

# Kimlik | .env değişkeni | servisler | yaklaşık bellek sınırı (MB) | ad
HR360_MODULE_TABLE="recruitment|HR360_MOD_RECRUITMENT|recruitment-service|512|İşe alım
onboarding|HR360_MOD_ONBOARDING|onboarding-service|512|İşe alıştırma ve demirbaş
timeshift|HR360_MOD_TIMESHIFT|timeshift-service|512|Puantaj ve vardiya
performance|HR360_MOD_PERFORMANCE|performance-service|512|Performans
learning|HR360_MOD_LEARNING|learning-service|512|Eğitim
compensation|HR360_MOD_COMPENSATION|compensation-service|512|Ücret ve bordro
ml|HR360_MOD_ML|ml-inference mlflow|1792|Yapay zekâ
expense|HR360_MOD_EXPENSE||0|Masraf ve seyahat (yalnız ekranlar)"

# Hazır seçimler.
HR360_EDITION_LITE="timeshift compensation expense"

: "${ENV_FILE:=.env}"

mod_ids() { printf '%s\n' "$HR360_MODULE_TABLE" | cut -d'|' -f1; }
mod_field() { printf '%s\n' "$HR360_MODULE_TABLE" | awk -F'|' -v id="$1" -v f="$2" '$1 == id { print $f }'; }
mod_var() { mod_field "$1" 2; }
mod_services() { mod_field "$1" 3; }
mod_mem() { mod_field "$1" 4; }
mod_name() { mod_field "$1" 5; }
mod_known() { [ -n "$(mod_var "$1")" ]; }

_mod_env_get() { [ -f "$ENV_FILE" ] && grep "^$1=" "$ENV_FILE" | tail -1 | cut -d= -f2- || true; }

_mod_env_write() { # _mod_env_write ANAHTAR [DEGER]  (değer yoksa satır silinir)
  local key="$1" tmp
  touch "$ENV_FILE"
  tmp="$(mktemp "${ENV_FILE}.XXXXXX")"
  grep -v "^${key}=" "$ENV_FILE" > "$tmp" || true
  [ $# -ge 2 ] && printf '%s=%s\n' "$key" "$2" >> "$tmp"
  chmod --reference="$ENV_FILE" "$tmp" 2>/dev/null || chmod 600 "$tmp"
  mv "$tmp" "$ENV_FILE"
}

mod_enabled() { [ "$(_mod_env_get "$(mod_var "$1")")" != "0" ]; }

# Etkin modüller (boşlukla); hepsi açıksa "all".
mod_active_list() {
  local id out="" all=1
  for id in $(mod_ids); do
    if mod_enabled "$id"; then out="$out $id"; else all=0; fi
  done
  if [ "$all" = 1 ]; then echo all; else echo "${out# }"; fi
}

# HR360_MODULES'u .env'deki HR360_MOD_* değerlerinden yeniden yazar (servisler ve arayüz bunu okur).
mod_sync_env() {
  local l; l="$(mod_active_list)"
  _mod_env_write HR360_MODULES "$([ "$l" = all ] && echo all || echo "${l// /,}")"
}

mod_set() { # mod_set ID on|off
  local v; v="$(mod_var "$1")"
  [ -n "$v" ] || { echo "Bilinmeyen modül: $1" >&2; return 1; }
  if [ "$2" = off ]; then _mod_env_write "$v" 0; else _mod_env_write "$v"; fi
  mod_sync_env
}

# Seçimi bir kerede uygular: verilen liste açık, geri kalanı kapalı. "all" = hepsi.
mod_apply_selection() { # mod_apply_selection "timeshift compensation" | all
  local want=" ${1//,/ } " id
  for id in $(mod_ids); do
    if [ "$1" = all ] || [[ "$want" == *" $id "* ]]; then mod_set "$id" on; else mod_set "$id" off; fi
  done
}

# Kapalı modüllerin servisleri (boşlukla).
mod_disabled_services() {
  local id out=""
  for id in $(mod_ids); do mod_enabled "$id" || out="$out $(mod_services "$id")"; done
  echo "${out# }"
}

# Verilen servis listesinden kapalı modüllere ait olanları çıkarır (servisleri adıyla yeniden başlatan
# betikler için: adı verilen profilli servis compose'da profili kendiliğinden açar).
mod_filter_services() {
  local off s out=""
  off=" $(mod_disabled_services) "
  for s in "$@"; do [[ "$off" == *" $s "* ]] || out="$out $s"; done
  echo "${out# }"
}

# Derlenecek servisler: build bölümü olan ve kapalı olmayan (scale 0 değil) compose servisleri.
mod_build_targets() {
  docker compose config --format json 2>/dev/null | python3 -c '
import json, sys
try:
    services = json.load(sys.stdin)["services"]
except (ValueError, KeyError):
    sys.exit(0)
print(" ".join(n for n, s in services.items() if "build" in s and s.get("scale", 1) != 0))'
}

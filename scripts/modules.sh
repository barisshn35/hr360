#!/usr/bin/env bash
# HR360 isteğe bağlı modülleri: listele, aç, kapat. Arayüzde Platform › Bakım › Modüller bu betiği
# (bakım çalıştırıcısı üzerinden) çağırır.
#
# Kullanım:
#   scripts/modules.sh list                         Modüller, durumları ve bellek sınırları
#   scripts/modules.sh status                       Makine için: "id=on|off" satırları + HR360_MODULES
#   scripts/modules.sh json                         Makine için JSON: etkin modüller, tablo sırası, ad/bellek/servis
#   scripts/modules.sh enable MODÜL... [--no-start] Aç: imajı derle, Kafka konumunu sona al, başlat
#   scripts/modules.sh disable MODÜL... [--no-stop] Kapat: konteyneri kaldır (VERİ SİLİNMEZ)
#   scripts/modules.sh set "timeshift,compensation"|all [--no-start]
#                                                   Seçimi bir kerede uygula (kurulum kullanır)
#   scripts/modules.sh sync                         .env'e göre ağ geçidi işaretlerini, HR360_MODULES'u ve
#                                                   izleme metriğini yeniden yaz (güncelleme çağırır)
#
# Çekirdek servisler (şirket, çalışan, organizasyon, onay, izin, bildirim, yönetişim, profil, belgeler)
# her zaman açıktır. Kapatılan modülün tabloları ve verisi yerinde kalır; açınca kaldığı yerden devam eder.
# Ayrıntı: scripts/lib/modules.sh, docs/isletim/README.md "Modüller".
set -euo pipefail
cd "$(dirname "$0")/.."
ENV_FILE=.env
. scripts/lib/modules.sh
. scripts/lib/backup-common.sh

die() { echo "HATA: $*" >&2; exit 1; }

# Modülün Kafka tüketici grupları (servis kodundaki GroupId). Açılırken konumları sona alınır: kapalıyken
# biriken eski olaylar (ör. aylar önceki onaylar) yeniden işlenmez.
kafka_groups() {
  case "$1" in
    recruitment) echo "recruitment-service:hr360.workflow.events" ;;
    timeshift) echo "timeshift-service:hr360.leave.events timeshift-service-workflow:hr360.workflow.events" ;;
  esac
}

marker() { # marker ID on|off — ağ geçidi işareti (deploy/nginx/modules/<id>.off)
  local f="deploy/nginx/modules/$1.off"
  [ "$1" = expense ] && return 0   # yalnız ekran modülü; API çekirdek servisinde
  mkdir -p deploy/nginx/modules
  if [ "$2" = off ]; then : > "$f"; else rm -f "$f"; fi
}

# İzleme: kapalı modülün servisleri "servis erişilemiyor" alarmı vermesin (deploy/monitoring/alerts.yml
# ServisErisilemiyor: up == 0 unless hr360_module_disabled == 1). Ağ geçidi işaretleri ve HR360_MODULES de
# .env'den yeniden üretilir; her komuttan sonra ve güncellemede (sync) çalışır.
# Kapalı modüllerin servisleri compose'dan tamamen çıkarılır: hiç açılmayan bir profile alınırlar
# (compose.modules.yml, .env COMPOSE_FILE ile eklenir). Yalnızca "scale: 0" yetmez: compose eksik imajı
# yine de derler. Hepsi açıksa dosya ve COMPOSE_FILE girdisi kaldırılır (eski kurulumlarla aynı).
OVERRIDE=compose.modules.yml
write_override() {
  local off cur new part
  off="$(mod_disabled_services)"
  if [ -z "${off// /}" ]; then
    rm -f "$OVERRIDE"
  else
    {
      echo "# OTOMATIK: scripts/modules.sh yazar, elle düzenlemeyin. Kapalı modüllerin servisleri."
      echo "services:"
      for s in $off; do printf '  %s:\n    profiles: ["hr360-module-disabled"]\n' "$s"; done
    } > "$OVERRIDE.tmp" && mv "$OVERRIDE.tmp" "$OVERRIDE"
  fi
  cur="$(_mod_env_get COMPOSE_FILE)"; new=""
  IFS=':' read -r -a parts <<< "${cur:-}"
  for part in "${parts[@]}"; do [ -n "$part" ] && [ "$part" != "$OVERRIDE" ] && new="${new:+$new:}$part"; done
  if [ -f "$OVERRIDE" ]; then new="${new:-docker-compose.yml}:$OVERRIDE"
  elif [ "$new" = docker-compose.yml ]; then new=""; fi
  [ "$new" = "$cur" ] && return 0
  if [ -n "$new" ]; then _mod_env_write COMPOSE_FILE "$new"; else _mod_env_write COMPOSE_FILE; fi
}

write_state() {
  local id s v
  mod_sync_env
  write_override
  {
    echo "# HELP hr360_module_disabled İsteğe bağlı modülün servisi bu kurulumda kapalı (1) ya da açık (0)."
    echo "# TYPE hr360_module_disabled gauge"
    for id in $(mod_ids); do
      if mod_enabled "$id"; then v=0; marker "$id" on; else v=1; marker "$id" off; fi
      for s in $(mod_services "$id"); do echo "hr360_module_disabled{service=\"$s\"} $v"; done
    done
  } | metrics_write hr360_modules
}

check_ids() {
  local id
  [ $# -gt 0 ] || die "modül adı verin (scripts/modules.sh list)"
  for id in "$@"; do mod_known "$id" || die "bilinmeyen modül: $id (geçerli: $(mod_ids | tr '\n' ' '))"; done
}

reset_kafka() { # modül başlamadan önce tüketici konumlarını sona al
  local g grp topic
  for g in $(kafka_groups "$1"); do
    grp="${g%%:*}"; topic="${g#*:}"
    docker compose exec -T kafka /opt/kafka/bin/kafka-consumer-groups.sh --bootstrap-server kafka:9092 \
      --group "$grp" --topic "$topic" --reset-offsets --to-latest --execute >/dev/null 2>&1 \
      || echo "UYARI: $grp için Kafka konumu ayarlanamadı; modül eski olayları işleyebilir." >&2
  done
}

# Modül listesini okuyan servisler (HR360_MODULES) ve ağ geçidi yeni değeri alsın: compose değişen ortamı
# görüp yalnızca bunları yeniden oluşturur.
refresh_core() {
  docker compose up -d >/dev/null
}

cmd_list() {
  local id st svc
  printf '%-14s %-6s %-8s %-34s %s\n' MODÜL DURUM BELLEK SERVİSLER AD
  for id in $(mod_ids); do
    if mod_enabled "$id"; then st=açık; else st=kapalı; fi
    svc="$(mod_services "$id")"
    printf '%-14s %-6s %-8s %-34s %s\n' "$id" "$st" "$(mod_mem "$id") MB" "${svc:-(yalnız ekranlar)}" "$(mod_name "$id")"
  done
}

cmd_status() {
  local id
  for id in $(mod_ids); do
    if mod_enabled "$id"; then echo "$id=on"; else echo "$id=off"; fi
  done
  echo "HR360_MODULES=$(mod_active_list | tr ' ' ',')"
}

_json_str() { # JSON metni: ters bölü ve tırnak kaçırılır (tablodaki adlar sabit metindir)
  local v="${1//\\/\\\\}"
  printf '"%s"' "${v//\"/\\\"}"
}

# Bakım çalıştırıcısı (scripts/ops-runner.py) bunu okuyup Platform › Bakım › Modüller'e taşır.
cmd_json() {
  local id s sep="" enabled="" all="" details="" svcs
  for id in $(mod_ids); do
    all="$all$sep$(_json_str "$id")"
    if mod_enabled "$id"; then enabled="$enabled${enabled:+,}$(_json_str "$id")"; fi
    svcs=""
    for s in $(mod_services "$id"); do svcs="$svcs${svcs:+,}$(_json_str "$s")"; done
    details="$details$sep{\"id\":$(_json_str "$id"),\"name\":$(_json_str "$(mod_name "$id")"),\"memoryMb\":$(( $(mod_mem "$id") + 0 )),\"services\":[$svcs]}"
    sep=","
  done
  printf '{"enabled":[%s],"all":[%s],"details":[%s]}\n' "$enabled" "$all" "$details"
}

cmd_enable() {
  local start=1 ids=() id svcs="" was_off=()
  for a in "$@"; do [ "$a" = --no-start ] && start=0 || ids+=("$a"); done
  check_ids "${ids[@]}"
  for id in "${ids[@]}"; do
    mod_enabled "$id" || was_off+=("$id")
    mod_set "$id" on; marker "$id" on; svcs="$svcs $(mod_services "$id")"
  done
  write_state   # derlemeden önce: servis compose.modules.yml'den çıksın
  svcs="${svcs# }"
  if [ "$start" = 1 ]; then
    if [ -n "${svcs// /}" ]; then
      HR360_VERSION="$(tr -d '[:space:]' < VERSION 2>/dev/null || true)"; export HR360_VERSION
      echo "==> İmajlar: $svcs"
      # shellcheck disable=SC2086
      docker compose build $svcs >/dev/null
      for id in "${was_off[@]}"; do reset_kafka "$id"; done
    fi
    refresh_core
  fi
  write_state
  echo "Açıldı: ${ids[*]}"
}

cmd_disable() {
  local stop=1 ids=() id svcs=""
  for a in "$@"; do [ "$a" = --no-stop ] && stop=0 || ids+=("$a"); done
  check_ids "${ids[@]}"
  for id in "${ids[@]}"; do mod_set "$id" off; marker "$id" off; svcs="$svcs $(mod_services "$id")"; done
  write_state
  if [ "$stop" = 1 ]; then
    # scale 0: "up -d" kapalı modülün konteynerlerini kaldırır; hacim ve veritabanı tabloları kalır.
    refresh_core
  fi
  echo "Kapatıldı: ${ids[*]} (veri korunur; yeniden açmak için: scripts/modules.sh enable ${ids[*]})"
}

cmd_set() {
  local sel="${1:?seçim verin: all ya da virgüllü liste}" start=1 id on=() off=()
  [ "${2:-}" = --no-start ] && start=0
  if [ "$sel" != all ]; then check_ids ${sel//,/ }; fi
  for id in $(mod_ids); do
    # Yalnızca durumu değişenler: zaten açık modülün imajı yeniden derlenmez.
    if [ "$sel" = all ] || [[ ",$sel," == *",$id,"* ]]; then mod_enabled "$id" || on+=("$id")
    else mod_enabled "$id" && off+=("$id"); fi
  done
  [ "${#off[@]}" -gt 0 ] && cmd_disable "${off[@]}" --no-stop >/dev/null
  if [ "${#on[@]}" -gt 0 ]; then
    if [ "$start" = 1 ]; then cmd_enable "${on[@]}" >/dev/null; else cmd_enable "${on[@]}" --no-start >/dev/null; fi
  fi
  [ "$start" = 1 ] && refresh_core
  cmd_list
}

case "${1:-list}" in
  list) cmd_list ;;
  status) cmd_status ;;
  json) cmd_json ;;
  enable) shift; cmd_enable "$@" ;;
  disable) shift; cmd_disable "$@" ;;
  set) shift; cmd_set "$@" ;;
  sync) write_state ;;
  *) sed -n '2,16p' "$0"; exit 2 ;;
esac

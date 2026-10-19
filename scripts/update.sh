#!/usr/bin/env bash
# HR360'i tek komutla gunceller; bir sey ters giderse otomatik olarak onceki surume doner.
#
# Kullanim:
#   scripts/update.sh [--ref REF] [--no-backup] [--restore-db] [--timeout SN] [--yes]
#   scripts/update.sh rollback [--restore-db] [--yes]   Son guncellemeden onceki surume don
#   scripts/update.sh status                            Kurulu surum ve son guncelleme
#
#   --ref REF      Gidilecek surum: etiket (onerilen, orn. v1.0.0), dal (varsayilan: izlenen uzak
#                  dal) ya da commit. Surum numarasi depo kokundeki VERSION dosyasindadir.
#   --no-backup    Guncellemeden once yedek alma (onerilmez)
#   --restore-db   Geri donuste veritabanini guncelleme oncesi yedekten geri yukle. Gerekmedikce
#                  kullanmayin: goc betikleri yalnizca ekleme yaptigi icin eski surum yeni semayla
#                  calisir; geri yukleme guncellemeden sonra girilen verileri siler.
#   --timeout SN   Saglik denetimi icin bekleme (varsayilan 300 sn)
#
# Adimlar: on denetim (depo temiz mi) → yedek (scripts/backup.sh) → surum degisikligi (git) →
# goc betikleri (scripts/sql) → imajlar ve servisler → saglik denetimi (tum servislerin /health
# ucu ve gateway). Saglik denetimi gecmezse onceki surume donulur ve servisler yeniden kurulur.
# Durum .update-state dosyasinda tutulur.
set -euo pipefail

cd "$(dirname "$0")/.."
die() { echo "HATA: $*" >&2; exit 1; }
info() { echo "==> $*"; }
STATE=.update-state

CMD=update
case "${1:-}" in rollback|status) CMD="$1"; shift ;; esac
REF=""; BACKUP=1; RESTORE_DB=0; TIMEOUT=300; YES=0
while [ $# -gt 0 ]; do
  case "$1" in
    --ref) REF="$2"; shift 2 ;;
    --no-backup) BACKUP=0; shift ;;
    --restore-db) RESTORE_DB=1; shift ;;
    --timeout) TIMEOUT="$2"; shift 2 ;;
    --yes|-y) YES=1; shift ;;
    -h|--help) sed -n '2,23p' "$0"; exit 0 ;;
    *) die "bilinmeyen secenek: $1" ;;
  esac
done
[[ "$TIMEOUT" =~ ^[0-9]+$ ]] || die "--timeout sayi olmali"

if ! declare -F docker >/dev/null && ! docker info >/dev/null 2>&1 && sudo docker info >/dev/null 2>&1; then
  docker() { command sudo docker "$@"; }
fi

# Bir commit'teki urun surumu (VERSION dosyasi); dosya yoksa "-".
version_at() { git show "$1:VERSION" 2>/dev/null | head -1 | tr -d '[:space:]' | grep . || echo "-"; }

state() { [ -f "$STATE" ] && grep -E "^$1=" "$STATE" | tail -1 | cut -d= -f2- || true; }

if [ "$CMD" = status ]; then
  echo "Kurulu surum: $(version_at HEAD) — $(git rev-parse --short HEAD 2>/dev/null || echo ?) ($(git log -1 --format=%cs 2>/dev/null || echo -))"
  if [ -f "$STATE" ]; then
    echo "Son guncelleme: $(state at) — $(state from) → $(state to) [$(state result)]"
    [ -n "$(state backup)" ] && echo "Guncelleme oncesi yedek: $(state backup)"
  else
    echo "Son guncelleme: kayit yok"
  fi
  exit 0
fi

[ -f .env ] || die ".env bulunamadi; once install.sh calistirin."
git rev-parse --git-dir >/dev/null 2>&1 || die "bu dizin bir git deposu degil"

# Compose servisleri: kapali moduller (scale 0, scripts/modules.sh) haric. ALAN: "build" yalnizca derlenenler.
active_services() { # active_services [build]
  docker compose config --format json 2>/dev/null | python3 -c '
import json, sys
want_build = len(sys.argv) > 1 and sys.argv[1] == "build"
try:
    services = json.load(sys.stdin)["services"]
except (ValueError, KeyError):
    sys.exit(0)  # okunamazsa bos: derleme tum servislere, saglik denetimi yalnizca gatewaye duser
for name, svc in services.items():
    if svc.get("scale", 1) == 0 or (want_build and "build" not in svc):
        continue
    print(name)' "${1:-}"
}

# Calisan servislerin /health uclari (gateway konteyneri uzerinden, ic agda).
SERVICES=()
while IFS= read -r s; do SERVICES+=("$s"); done < <(active_services | grep -E -- '-service$' || true)

healthy() {
  local deadline=$(( $(date +%s) + TIMEOUT )) bad
  while :; do
    bad=""
    docker compose exec -T gateway wget -qO- http://127.0.0.1:8080/gateway/health >/dev/null 2>&1 \
      || docker compose exec -T gateway wget -qO- http://127.0.0.1/gateway/health >/dev/null 2>&1 || bad="gateway"
    for s in "${SERVICES[@]}"; do
      # /health/ready veritabanina baglantiyi da dogrular; eski surumde (geri donus) yoksa /health'e duser.
      docker compose exec -T gateway wget -qO- -T 5 "http://$s:8080/health/ready" >/dev/null 2>&1 \
        || { ! docker compose exec -T gateway wget -qO- -T 5 -S "http://$s:8080/health/ready" 2>&1 | grep -q " 503 " \
             && docker compose exec -T gateway wget -qO- -T 5 "http://$s:8080/health" >/dev/null 2>&1; } || bad="$bad $s"
    done
    [ -z "$bad" ] && return 0
    [ "$(date +%s)" -ge "$deadline" ] && { echo "Saglik denetimi gecmedi:$bad" >&2; return 1; }
    sleep 5
  done
}

apply_migrations() {
  if [ -x scripts/migrate.sh ]; then
    # Kayit defterli (schema_migrations): yalnizca yeni/degisen dosyalar ve sonrakiler.
    scripts/migrate.sh >/dev/null || return 1
  else
    for f in scripts/sql/*.sql; do
      [ -f "$f" ] || continue
      docker compose exec -T postgres psql -U hr360admin -d hr360_operational -v ON_ERROR_STOP=1 -q -f - < "$f" >/dev/null \
        || { echo "Goc betigi basarisiz: $f" >&2; return 1; }
    done
  fi
  # Servis basina veritabani rolleri kullaniliyorsa (scripts/db-roles.sh enable) yeni tablolarin yetkileri
  # deploy/postgres/roles.sql'den esitlenir; aksi halde kendi rolundeki servis yeni tabloya erisemez.
  if [ -f deploy/postgres/roles.sql ] && grep -qE '^HR360_DB_USER_[A-Z]+=.' .env 2>/dev/null; then
    docker compose exec -T postgres psql -X -U hr360admin -d hr360_operational -v ON_ERROR_STOP=1 -q -f - \
      < deploy/postgres/roles.sql >/dev/null || { echo "Rol yetkileri esitlenemedi (deploy/postgres/roles.sql)" >&2; return 1; }
  fi
}

deploy() {
  # Web arayuzu surumu gosterir: derleme argumani depo kokundeki VERSION'dan (docker-compose.yml).
  HR360_VERSION="$(tr -d '[:space:]' < VERSION 2>/dev/null || true)"; export HR360_VERSION
  docker compose up -d postgres >/dev/null
  for _ in $(seq 1 60); do docker compose exec -T postgres pg_isready -U hr360admin >/dev/null 2>&1 && break; sleep 2; done
  apply_migrations || return 1
  # Veritabani yalitimi varsayilan acik (servis rolleri + RLS; yeni tablolar dahil). Eski surume donuste betik
  # yoksa atlanir: eski kod app.tenant ayarlamaz, RLS politikasi o zaman her satiri gosterir.
  if [ -x scripts/db-isolation.sh ]; then
    scripts/db-isolation.sh ensure || echo "UYARI: veritabani yalitimi acilamadi (scripts/db-isolation.sh ensure)." >&2
  fi
  # Paralel derleme, calisan servislerin yaninda kucuk sunucularda bellegi tuketebilir: ya npm "cannot
  # allocate memory" ile duser ya da hic dusmeden saatlerce takas yapar. Bos bellek azsa bastan sirayla
  # derle; paralel derleme basarisiz olursa da (gecici ag hatasi vb.) servis servis bir kez daha dene.
  # Yalnizca acik modullerin imajlari derlenir (kapali modul: scale 0).
  local targets; targets="$(active_services build | tr '\n' ' ')"
  local avail_kb cg_max cg_cur; avail_kb="$(awk '/^MemAvailable:/ {print $2}' /proc/meminfo 2>/dev/null || echo 0)"
  # Konteyner/LXC icinde /proc/meminfo makinenin tamamini gosterir; cgroup siniri daha darsa onu kullan.
  cg_max="$(cat /sys/fs/cgroup/memory.max 2>/dev/null || echo max)"; cg_cur="$(cat /sys/fs/cgroup/memory.current 2>/dev/null || echo 0)"
  if [[ "$cg_max" =~ ^[0-9]+$ ]] && [[ "$cg_cur" =~ ^[0-9]+$ ]] && [ $(( (cg_max - cg_cur) / 1024 )) -lt "${avail_kb:-0}" ]; then
    avail_kb=$(( (cg_max - cg_cur) / 1024 ))
  fi
  if [ "${avail_kb:-0}" -lt "${HR360_PARALLEL_BUILD_MIN_KB:-6291456}" ]; then
    echo "==> Bos bellek az ($((${avail_kb:-0} / 1024)) MB): imajlar sirayla derleniyor" >&2
    local s; for s in $targets; do docker compose build "$s" >/dev/null || return 1; done
  else docker compose build $targets >/dev/null || {
    echo "UYARI: imaj derlemesi basarisiz; servisler sirayla yeniden derleniyor." >&2
    local s; for s in $targets; do docker compose build "$s" >/dev/null || return 1; done
  } || return 1; fi
  docker compose up -d --remove-orphans >/dev/null || return 1
}

record() { # record key=value ...
  { for kv in "$@"; do echo "$kv"; done; } > "$STATE.tmp"
  [ -f "$STATE" ] && grep -v -E "^($(printf '%s|' "${@%%=*}" | sed 's/|$//'))=" "$STATE" >> "$STATE.tmp" || true
  mv "$STATE.tmp" "$STATE"
}

rollback_to() { # rollback_to COMMIT BACKUP
  local prev="$1" bk="$2"
  info "Onceki surume donuluyor: ${prev:0:12}"
  git reset -q --hard "$prev"
  if [ "$RESTORE_DB" = 1 ] && [ -n "$bk" ] && [ -f "$bk" ]; then
    info "Veritabani guncelleme oncesi yedekten geri yukleniyor: $bk"
    scripts/restore.sh "$bk" --yes --only-db
  fi
  deploy || die "onceki surum de ayaga kalkmadi; 'docker compose ps' ve 'docker compose logs' ile inceleyin"
  if healthy; then
    info "Onceki surum calisiyor (${prev:0:12})."
    return 0
  fi
  die "onceki surumde de saglik denetimi gecmedi; 'docker compose logs' ile inceleyin"
}

if [ "$CMD" = rollback ]; then
  prev="$(state from)"; bk="$(state backup)"
  [ -n "$prev" ] || die "geri donulecek surum kaydi yok (.update-state)"
  if [ "$YES" != 1 ]; then
    read -r -p "${prev:0:12} surumune donulsun mu? (evet/hayir) " ans
    [ "$ans" = evet ] || die "iptal edildi"
  fi
  cur="$(git rev-parse HEAD)"
  rollback_to "$prev" "$bk"
  record "at=$(date -u +%FT%TZ)" "from=$cur" "to=$prev" "result=elle geri donuldu"
  exit 0
fi

# ---------------------------------------------------------------- guncelleme
[ -z "$(git status --porcelain --untracked-files=no)" ] || die "depoda kaydedilmemis degisiklik var; once commit edin ya da geri alin (git status)"
PREV="$(git rev-parse HEAD)"
info "Kurulu surum: $(version_at "$PREV") (${PREV:0:12})"

git fetch -q --tags origin 2>/dev/null || die "uzak depoya ulasilamadi (git fetch)"
if [ -z "$REF" ]; then
  REF="$(git rev-parse --abbrev-ref --symbolic-full-name '@{u}' 2>/dev/null || echo origin/main)"
fi
TARGET="$(git rev-parse --verify -q "$REF^{commit}" || git rev-parse --verify -q "origin/$REF^{commit}" || true)"
[ -n "$TARGET" ] || die "surum bulunamadi: $REF"
if [ "$TARGET" = "$PREV" ]; then
  info "Zaten guncel (${PREV:0:12})."
  exit 0
fi
echo "Yeni surum: $(version_at "$TARGET") (${TARGET:0:12}, $REF)"
# "| head" KULLANMAYIN: pipefail altında 20'den fazla commit varken git SIGPIPE alır ve betik sessizce çıkar.
git --no-pager log --oneline -n 20 "$PREV..$TARGET" 2>/dev/null | sed 's/^/   /' || true
if [ "$YES" != 1 ]; then
  read -r -p "Guncellensin mi? (evet/hayir) " ans
  [ "$ans" = evet ] || die "iptal edildi"
fi

BK=""
if [ "$BACKUP" = 1 ]; then
  info "Guncelleme oncesi yedek aliniyor"
  out="$(scripts/backup.sh --keep 14 2>&1)" || { echo "$out" >&2; die "yedek alinamadi; guncelleme yapilmadi (--no-backup ile atlanabilir)"; }
  echo "$out" | tail -1
  BK="$(echo "$out" | sed -n 's/^Tamam: \([^ ]*\).*/\1/p' | tail -1)"
fi
record "at=$(date -u +%FT%TZ)" "from=$PREV" "to=$TARGET" "backup=$BK" "result=suruyor"

info "Surum degistiriliyor"
git reset -q --hard "$TARGET"

info "Goc betikleri, imajlar ve servisler"
if deploy && healthy; then
  record "result=basarili"
  # Guvenlik dalgasi 2A (bir kez): tenant-service'in Keycloak yonetim API servis hesabi yoksa
  # olusturulur (anahtar .env'e eklenir, parola politikasi ve giris olaylari uygulanir).
  if [ -x scripts/keycloak-service-account.sh ] && ! grep -q '^KEYCLOAK_TENANT_ADMIN_CLIENT_SECRET=.' .env; then
    info "Keycloak yonetim API servis hesabi olusturuluyor"
    scripts/keycloak-service-account.sh apply \
      || echo "UYARI: servis hesabi olusturulamadi; scripts/keycloak-service-account.sh ile tekrar deneyin." >&2
  fi
  info "Guncelleme tamamlandi: $(version_at "$TARGET") (${TARGET:0:12})"
  exit 0
fi

echo "Guncelleme basarisiz; otomatik geri donus yapiliyor." >&2
rollback_to "$PREV" "$BK"
record "result=basarisiz, geri donuldu"
exit 2

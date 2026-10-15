#!/usr/bin/env bash
# Secili sirlari .env'den secrets/*.txt dosyalarina tasir ve Docker secrets ile servislere verir.
#
#   scripts/secrets-migrate.sh status                 Hangi sir nerede (degerler yazilmaz)
#   scripts/secrets-migrate.sh move [ANAHTAR...]      .env -> secrets/<anahtar>.txt (varsayilan secim asagida)
#   scripts/secrets-migrate.sh restore [ANAHTAR...]   secrets/<anahtar>.txt -> .env (hepsi, ya da verilenler)
#   scripts/secrets-migrate.sh compose                Yalnizca secrets/compose.secrets.yml'i yeniden uretir
#   --dry-run                                         Yapilacaklari yazar, hicbir dosyaya dokunmaz
#
# move/restore sonrasi servisler yeniden olusturulmalidir:  docker compose up -d --force-recreate
#
# Nasil calisir: deger .env'den silinip secrets/<kucuk harf ad>.txt'ye yazilir (dizin 0700, dosya 0640,
# grup 1654 = .NET konteyner kullanicisi; 0600 olsaydi root olmayan konteyner okuyamazdi). Uretilen
# secrets/compose.secrets.yml bu dosyalari yalnizca o degiskeni kullanan servislere /run/secrets/<ad>
# olarak baglar ve X_FILE verir; servisler (Security/SecretEnv.cs, ml-inference secret_env.py) X bos ise
# X_FILE'i okur. .env'e COMPOSE_FILE=docker-compose.yml:secrets/compose.secrets.yml yazilir; bu yuzden
# "docker compose" komutlari ve scripts/*.sh dosyayi kendiliginden kullanir. -f ile dosya veren komutlar
# (gelistirme, scripts/test.sh) ek olarak "-f secrets/compose.secrets.yml" vermelidir; scripts/test.sh
# bunu kendisi yapar. Unutulursa servisler sirsiz acilir (or. TCKN/IBAN sifrelenmez, ic uclar kapanir).
#
# Tasinabilen sirlar (yalnizca .NET servisleri ve ml-inference kullaniyor):
#   TENANT_SECRET_KEY TENANT_SECRET_KEYS KEYCLOAK_TENANT_ADMIN_CLIENT_SECRET SIEM_PSEUDONYM_KEY LLM_API_KEY
#   HR360_DB_PASSWORD_<SERVIS> HR360_DB_PASSWORD_RETENTION (scripts/db-roles.sh servis rolleri)
#   INTERNAL_SERVICE_TOKEN INTERNAL_SERVICE_TOKEN_PREVIOUS (yalnizca acikca istenirse; scripts/rotate-internal-token.sh
#   dosya modunda calismaz, once 'restore INTERNAL_SERVICE_TOKEN INTERNAL_SERVICE_TOKEN_PREVIOUS')
# Varsayilan secim: TENANT_SECRET_KEY, TENANT_SECRET_KEYS, KEYCLOAK_TENANT_ADMIN_CLIENT_SECRET ve .env'de
# dolu olan HR360_DB_PASSWORD_* anahtarlari.
# .env'de KALANLAR (bilerek): HR360_DB_PASSWORD (postgres, Keycloak, MLflow, postgres-exporter da kullanir),
# MINIO_ROOT_* (minio-init, MLflow), KEYCLOAK_ADMIN_PASSWORD, REDIS_PASSWORD (baglanti dizesi compose'da),
# SMTP_PASSWORD, BACKUP_ENCRYPTION_KEY. Ayrinti: docs/guvenlik/README.md "Sirlar dosyada".
set -euo pipefail

cd "$(dirname "$0")/.."
ENV_FILE="${ENV_FILE:-.env}"
SECRETS_DIR="${SECRETS_DIR:-secrets}"
COMPOSE_BASE="${COMPOSE_BASE:-docker-compose.yml}"
OVERRIDE="$SECRETS_DIR/compose.secrets.yml"
# shellcheck source=lib/secrets.sh
. scripts/lib/secrets.sh

die() { echo "HATA: $*" >&2; exit 1; }
info() { echo "==> $*"; }

DRY=0
args=()
for a in "$@"; do
  case "$a" in
    --dry-run) DRY=1 ;;
    -h|--help) sed -n '2,33p' "$0"; exit 0 ;;
    *) args+=("$a") ;;
  esac
done
mode="${args[0]:-status}"
keys=("${args[@]:1}")

[ -f "$ENV_FILE" ] || die "$ENV_FILE bulunamadi; once install.sh calistirin."
[ -f "$COMPOSE_BASE" ] || die "$COMPOSE_BASE bulunamadi."

SERVICE_KEYS="ORGANIZATION EMPLOYEE WORKFLOW LEAVE RECRUITMENT ONBOARDING TIMESHIFT PERFORMANCE LEARNING ENGAGEMENT GOVERNANCE COMPENSATION EXPENSE NOTIFICATION TENANT RETENTION"
PLAIN_KEYS="TENANT_SECRET_KEY TENANT_SECRET_KEYS KEYCLOAK_TENANT_ADMIN_CLIENT_SECRET SIEM_PSEUDONYM_KEY LLM_API_KEY INTERNAL_SERVICE_TOKEN INTERNAL_SERVICE_TOKEN_PREVIOUS"

supported() { # supported KEY
  local k s
  for k in $PLAIN_KEYS; do [ "$1" = "$k" ] && return 0; done
  for s in $SERVICE_KEYS; do [ "$1" = "HR360_DB_PASSWORD_$s" ] && return 0; done
  return 1
}

why_not() {
  case "$1" in
    HR360_DB_PASSWORD) echo "postgres, Keycloak, MLflow ve postgres-exporter de kullaniyor (compose icinde); servis rolleri icin scripts/db-roles.sh" ;;
    MINIO_ROOT_USER|MINIO_ROOT_PASSWORD) echo "minio-init ve MLflow komut satirinda/ortaminda kullaniyor" ;;
    REDIS_PASSWORD) echo "governance/engagement REDIS_URL baglanti dizesi compose'da kuruluyor" ;;
    *) echo "desteklenmiyor" ;;
  esac
}

all_supported_present() { # .env'de dolu ya da dosyada olan desteklenen anahtarlar
  local k s
  for k in $PLAIN_KEYS; do printf '%s\n' "$k"; done
  for s in $SERVICE_KEYS; do printf '%s\n' "HR360_DB_PASSWORD_$s"; done
}

default_selection() {
  local k
  for k in TENANT_SECRET_KEY TENANT_SECRET_KEYS KEYCLOAK_TENANT_ADMIN_CLIENT_SECRET; do printf '%s\n' "$k"; done
  grep -oE '^HR360_DB_PASSWORD_[A-Z]+=.+' "$ENV_FILE" | cut -d= -f1 | while read -r k; do supported "$k" && printf '%s\n' "$k"; done
}

# compose.secrets.yml: dosyadaki her sir icin onu kullanan servislere secret + X_FILE.
PENDING=" "   # deneme modunda dosyaya yazilmis sayilan anahtarlar
generate_override() {
  local present=() k
  while read -r k; do
    if secret_in_file "$k" || [[ "$PENDING" == *" $k "* ]]; then present+=("$k"); fi
  done < <(all_supported_present)
  if [ "${#present[@]}" -eq 0 ]; then
    if [ "$DRY" = 1 ]; then info "(deneme) $OVERRIDE silinirdi (dosyada sir yok)"; else rm -f "$OVERRIDE"; fi
    return 0
  fi
  local out
  out="$(python3 - "$COMPOSE_BASE" "$SECRETS_DIR" "${present[@]}" <<'PY'
import re, sys
base, sdir, keys = sys.argv[1], sys.argv[2], sys.argv[3:]
# docker-compose.yml'den servis -> ortam degiskeni adlari (6 bosluk girintili BUYUK_HARF anahtarlar).
env, conns, svc, in_services = {}, {}, None, False
for line in open(base, encoding="utf-8"):
    if re.match(r"^services:\s*$", line): in_services = True; continue
    if re.match(r"^[^\s#]", line): in_services = line.startswith("services:")
    if not in_services: continue
    m = re.match(r"^  ([a-z0-9][a-z0-9-]*):\s*$", line)
    if m: svc = m.group(1); env.setdefault(svc, set()); continue
    m = re.match(r"^      ([A-Z][A-Z0-9_]*):\s*(.*?)\s*$", line)
    if m and svc:
        env[svc].add(m.group(1))
        if m.group(1).endswith("_DB_CONNECTION"):
            conns.setdefault(svc, {})[m.group(1)] = m.group(2)
# Parola dosyadaysa baglanti dizesindeki ${HR360_DB_PASSWORD...} bosaltilir: aksi halde compose ortak
# parolayi (HR360_DB_PASSWORD) dizeye yazar ve servis konteyneri onu gorur. Parolayi SecretEnv doldurur.
PW = re.compile(r"Password=\$\{HR360_DB_PASSWORD_[A-Z]+:-\$\{HR360_DB_PASSWORD:-\}\}")
blank_conn = {}  # servis -> {ad: deger}
def blank(s, only=None, skip=()):
    for k, v in conns.get(s, {}).items():
        if (only and k != only) or k in skip or not PW.search(v): continue
        blank_conn.setdefault(s, {})[k] = PW.sub("Password=", v)
uses = {}  # servis -> [(secret adi, ortam adi, bosaltilacak ad)]
for k in keys:
    name = k.lower()
    m = re.match(r"^HR360_DB_PASSWORD_([A-Z]+)$", k)
    if m:
        s = m.group(1)
        if s == "RETENTION":
            uses.setdefault("governance-service", []).append((name, "RETENTION_DB_PASSWORD_FILE", None))
            blank("governance-service", only="RETENTION_DB_CONNECTION")
        else:
            target = s.lower() + "-service"
            if target in env:
                uses.setdefault(target, []).append((name, "HR360_SERVICE_DB_PASSWORD_FILE", None))
                blank(target, skip=("RETENTION_DB_CONNECTION",))
        continue
    for s, names in sorted(env.items()):
        if k in names:
            uses.setdefault(s, []).append((name, k + "_FILE", k))
print("# scripts/secrets-migrate.sh tarafindan uretildi; elle degistirmeyin.")
print("# Yeniden uretmek: scripts/secrets-migrate.sh compose. Ayrinti: docs/guvenlik/README.md")
print("secrets:")
for k in keys:
    print(f"  {k.lower()}:")
    print(f"    file: ./{sdir}/{k.lower()}.txt")
print("services:")
for s in sorted(uses):
    print(f"  {s}:")
    if s == "ml-inference":
        print('    group_add: ["1654"]   # sir dosyalari 0640, grup 1654')
    print("    secrets:")
    for name, _, _ in uses[s]:
        print(f"      - {name}")
    print("    environment:")
    for name, fvar, blank in uses[s]:
        if blank:
            print(f'      {blank}: ""')
        print(f"      {fvar}: /run/secrets/{name}")
    for k, v in sorted(blank_conn.get(s, {}).items()):
        print(f"      {k}: {v}")
PY
)"
  if [ "$DRY" = 1 ]; then info "(deneme) $OVERRIDE:"; printf '%s\n' "$out"; return 0; fi
  mkdir -p "$SECRETS_DIR"; chmod 700 "$SECRETS_DIR"
  printf '%s\n' "$out" > "$OVERRIDE"
  chmod 640 "$OVERRIDE"
  info "$OVERRIDE uretildi (${#present[@]} sir)."
}

# COMPOSE_FILE: override varsa ekler, yoksa kaldirir (baska girdileri korur).
sync_compose_file() {
  local cur new want=0 part
  if [ -f "$OVERRIDE" ] || { [ "$DRY" = 1 ] && [ "$PENDING" != " " ]; }; then want=1; fi
  cur="$(env_file_get COMPOSE_FILE)"
  new=""
  IFS=':' read -r -a parts <<< "${cur:-}"
  for part in "${parts[@]}"; do
    [ -n "$part" ] && [ "$part" != "$OVERRIDE" ] && new="${new:+$new:}$part"
  done
  if [ "$want" = 1 ]; then
    [ -n "$new" ] || new="$COMPOSE_BASE"
    new="$new:$OVERRIDE"
  elif [ "$new" = "$COMPOSE_BASE" ]; then
    new=""
  fi
  [ "$new" = "$cur" ] && return 0
  if [ "$DRY" = 1 ]; then info "(deneme) .env COMPOSE_FILE=${new:-<silinir>}"; return 0; fi
  if [ -n "$new" ]; then env_file_set COMPOSE_FILE "$new"; else UNSET=1 env_file_set COMPOSE_FILE ""; fi
  info ".env COMPOSE_FILE=${new:-<silindi>}"
}

move() {
  local sel=() k v moved=0
  if [ "${#keys[@]}" -gt 0 ]; then sel=("${keys[@]}"); else mapfile -t sel < <(default_selection); fi
  for k in "${sel[@]}"; do
    supported "$k" || die "$k tasinamaz: $(why_not "$k")."
  done
  for k in "${sel[@]}"; do
    v="$(env_file_get "$k")"
    if [ -z "$v" ]; then
      if secret_in_file "$k"; then info "$k zaten dosyada."; else info "$k .env'de bos/yok; atlandi."; fi
      continue
    fi
    if [ "$DRY" = 1 ]; then info "(deneme) $k -> $(secret_file "$k")"; PENDING="$PENDING$k "; continue; fi
    secret_write_file "$k" "$v"
    UNSET=1 env_file_set "$k" ""
    info "$k -> $(secret_file "$k")"
    moved=$((moved + 1))
    case "$k" in INTERNAL_SERVICE_TOKEN*) echo "UYARI: scripts/rotate-internal-token.sh dosya modunda calismaz (once restore)." >&2 ;; esac
  done
  generate_override
  sync_compose_file
  if [ "$moved" -gt 0 ]; then echo "Servisleri yeniden olusturun: docker compose up -d --force-recreate"; fi
}

restore() {
  local sel=() k v
  if [ "${#keys[@]}" -gt 0 ]; then sel=("${keys[@]}"); else mapfile -t sel < <(all_supported_present); fi
  for k in "${sel[@]}"; do
    secret_in_file "$k" || continue
    v="$(tr -d '\r\n' < "$(secret_file "$k")")"
    if [ "$DRY" = 1 ]; then info "(deneme) $(secret_file "$k") -> .env $k"; continue; fi
    env_file_set "$k" "$v"
    rm -f "$(secret_file "$k")"
    info "$(secret_file "$k") -> .env $k"
  done
  generate_override
  sync_compose_file
  echo "Servisleri yeniden olusturun: docker compose up -d --force-recreate"
}

status() {
  local k where
  printf '%-40s %s\n' "SIR" "YER"
  while read -r k; do
    if secret_in_file "$k"; then where="dosya ($(secret_file "$k"))"
    elif [ -n "$(env_file_get "$k")" ]; then where=".env"
    else continue; fi
    printf '%-40s %s\n' "$k" "$where"
  done < <(all_supported_present)
  if [ -f "$OVERRIDE" ]; then echo "Compose eki: $OVERRIDE (COMPOSE_FILE=$(env_file_get COMPOSE_FILE))"
  else echo "Compose eki yok (tum sirlar .env'de)."; fi
}

case "$mode" in
  status) status ;;
  move) move ;;
  restore) restore ;;
  compose) generate_override; sync_compose_file ;;
  *) die "bilinmeyen mod '$mode' (status | move | restore | compose)" ;;
esac

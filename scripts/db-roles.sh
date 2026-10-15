#!/usr/bin/env bash
# Servis basina PostgreSQL rolleri (en az yetki). Varsayilan davranis DEGISMEZ: servisler, .env'de
# HR360_DB_USER_<SERVIS> tanimlanana kadar hr360admin ile baglanir.
#
# Kullanim:
#   scripts/db-roles.sh generate
#       Koddan rol/yetki eslemesini cikarir: deploy/postgres/roles.sql (GRANT'lar, parolasiz) ve
#       deploy/postgres/roles-review.md (elle gozden gecirme listesi). Veritabanina dokunmaz.
#   scripts/db-roles.sh grants
#       roles.sql'i uygular: roller (yoksa NOLOGIN) olusturulur, yetkiler esitlenir. Idempotent.
#       Her migration'dan sonra (yeni tablo) yeniden calistirin.
#   scripts/db-roles.sh apply [--secrets-dir secrets]
#       grants + rollere LOGIN ve parola verir. Parola sirasi: ortam/.env'deki HR360_DB_PASSWORD_<SERVIS>,
#       HR360_DB_PASSWORD_<SERVIS>_FILE, ./secrets/hr360_db_password_<servis>.txt; hicbiri yoksa uretilir
#       ve .env'e (ya da --secrets-dir verilirse o dizine, chmod 600) yazilir. Parolalar ekrana basilmaz;
#       veritabanina SCRAM dogrulayicisi olarak gider.
#   scripts/db-roles.sh check [SERVIS...]
#       Kuru calisma: her rolun, servisin kodunda gecen her tabloya gereken yetkisi var mi
#       (has_table_privilege + rol olarak SELECT ... LIMIT 0, islem geri alinir), audit_log'da
#       yasak UPDATE/DELETE var mi. Eksik varsa cikis kodu 1. Yalnizca okur.
#   scripts/db-roles.sh enable SERVIS...|all [--force]
#       Once check; sorun yoksa .env'e HR360_DB_USER_<SERVIS>=hr360_<servis> yazar (secim bayragi).
#       Sonra servisleri yeniden olusturun: docker compose up -d <servis>
#   scripts/db-roles.sh disable SERVIS...|all
#       HR360_DB_USER_<SERVIS> satirlarini siler (servis yeniden hr360admin ile baglanir).
#   scripts/db-roles.sh status
#       Hangi servisin kendi rolune gectigini ve rollerin durumunu gosterir.
#
# SERVIS: leave, governance, ... (ya da leave-service). PG_CONTAINER ile postgres konteyneri secilebilir.
set -euo pipefail

cd "$(dirname "$0")/.."
ENV_FILE="${ENV_FILE:-.env}"
py() { python3 scripts/db-roles.py "$@"; }

die() { echo "HATA: $*" >&2; exit 1; }
command -v python3 >/dev/null 2>&1 || die "python3 bulunamadi."

container_args=()
[ -n "${PG_CONTAINER:-}" ] && container_args=(--container "$PG_CONTAINER")

pg_container() {
  if [ -n "${PG_CONTAINER:-}" ]; then echo "$PG_CONTAINER"; return; fi
  local c
  c="$(docker compose ps -q postgres 2>/dev/null || true)"
  [ -n "$c" ] || c=hr360-postgres-1
  echo "$c"
}

# "leave" | "leave-service" | "LEAVE" -> LEAVE ; bilinmiyorsa bos
svc_key() {
  local s="${1%-service}"
  s="$(printf '%s' "$s" | tr '[:lower:]' '[:upper:]')"
  py services | awk -v k="$s" '$1 == k { print $1 }'
}

all_keys() { py services | awk '{ print $1 }'; }

set_env() {  # set_env ANAHTAR DEGER  (deger gizli degil: yalnizca rol adi)
  local key="$1" val="$2" tmp
  touch "$ENV_FILE"
  tmp="$(mktemp "${ENV_FILE}.XXXXXX")"
  grep -v "^${key}=" "$ENV_FILE" > "$tmp" || true
  printf '%s=%s\n' "$key" "$val" >> "$tmp"
  chmod --reference="$ENV_FILE" "$tmp" 2>/dev/null || chmod 600 "$tmp"
  mv "$tmp" "$ENV_FILE"
}

unset_env() {
  local key="$1" tmp
  [ -f "$ENV_FILE" ] || return 0
  tmp="$(mktemp "${ENV_FILE}.XXXXXX")"
  grep -v "^${key}=" "$ENV_FILE" > "$tmp" || true
  chmod --reference="$ENV_FILE" "$tmp" 2>/dev/null || chmod 600 "$tmp"
  mv "$tmp" "$ENV_FILE"
}

resolve_keys() {  # argumanlar -> KEYS dizisi
  KEYS=()
  local a k
  for a in "$@"; do
    [ "$a" = "--force" ] && continue
    if [ "$a" = all ]; then mapfile -t KEYS < <(all_keys); return; fi
    k="$(svc_key "$a")"
    [ -n "$k" ] || die "bilinmeyen servis: $a"
    KEYS+=("$k")
  done
  [ "${#KEYS[@]}" -gt 0 ] || die "servis belirtin (ya da all)"
}

cmd="${1:-}"
shift || true
case "$cmd" in
  generate)
    py generate "$@"
    ;;
  grants)
    [ -f deploy/postgres/roles.sql ] || die "deploy/postgres/roles.sql yok; once: scripts/db-roles.sh generate"
    docker exec -i "$(pg_container)" psql -X -q -v ON_ERROR_STOP=1 -U hr360admin -d hr360_operational \
      < deploy/postgres/roles.sql >/dev/null
    echo "Roller ve yetkiler esitlendi (deploy/postgres/roles.sql)."
    ;;
  apply)
    "$0" grants
    py passwords ${container_args[@]+"${container_args[@]}"} --env-file "$ENV_FILE" "$@"
    ;;
  check)
    args=()
    for a in "$@"; do args+=(--service "$(svc_key "$a")"); done
    py check ${container_args[@]+"${container_args[@]}"} ${args[@]+"${args[@]}"}
    ;;
  enable)
    force=0
    for a in "$@"; do [ "$a" = "--force" ] && force=1; done
    resolve_keys "$@"
    args=()
    for k in "${KEYS[@]}"; do
      args+=(--service "$k")
      if [ "$k" = GOVERNANCE ] && [ "$force" = 0 ] \
        && ! grep -rqs "RETENTION_DB_CONNECTION" apps/services/governance-service --include=*.cs; then
        die "governance: denetim kaydi saklama silmesi (Retention, AuditLog) audit_log'da DELETE ister ve bu yetki
yalnizca hr360_retention rolundedir; governance kodu henuz ayri baglanti (RETENTION_DB_CONNECTION) kullanmiyor.
Saklama politikasinda AuditLog kapaliysa --force ile gecebilirsiniz."
      fi
    done
    py check ${container_args[@]+"${container_args[@]}"} "${args[@]}" \
      || die "eksik yetki var; once: scripts/db-roles.sh apply"
    c="$(pg_container)"
    for k in "${KEYS[@]}"; do
      role="hr360_$(printf '%s' "$k" | tr '[:upper:]' '[:lower:]')"
      can="$(docker exec -i "$c" psql -X -At -U hr360admin -d hr360_operational \
        -c "SELECT rolcanlogin AND rolpassword IS NOT NULL FROM pg_authid WHERE rolname = '$role'" 2>/dev/null || true)"
      [ "$can" = t ] || die "$role giris yapamiyor (LOGIN/parola yok); once: scripts/db-roles.sh apply"
      if ! grep -q "^HR360_DB_PASSWORD_${k}=" "$ENV_FILE" 2>/dev/null; then
        echo "UYARI: .env'de HR360_DB_PASSWORD_${k} yok; parola dosyayla (secrets) verilmiyorsa servis baglanamaz." >&2
      fi
      set_env "HR360_DB_USER_${k}" "$role"
      echo "$k -> $role"
      if [ "$k" = GOVERNANCE ]; then
        # audit_log saklama silmesi ayri baglantiyla (RETENTION_DB_CONNECTION) hr360_retention roluyle yapilir.
        can="$(docker exec -i "$c" psql -X -At -U hr360admin -d hr360_operational \
          -c "SELECT rolcanlogin AND rolpassword IS NOT NULL FROM pg_authid WHERE rolname = 'hr360_retention'" 2>/dev/null || true)"
        if [ "$can" = t ]; then
          set_env HR360_DB_USER_RETENTION hr360_retention
          echo "RETENTION -> hr360_retention"
        else
          echo "UYARI: hr360_retention giris yapamiyor; saklama silmesi hr360admin ile kalir (scripts/db-roles.sh apply)." >&2
        fi
      fi
    done
    echo "Servisleri yeniden olusturun: docker compose up -d <servis>  (geri donus: scripts/db-roles.sh disable ...)"
    ;;
  disable)
    resolve_keys "$@"
    for k in "${KEYS[@]}"; do
      unset_env "HR360_DB_USER_${k}"; echo "$k -> hr360admin"
      if [ "$k" = GOVERNANCE ]; then unset_env HR360_DB_USER_RETENTION; echo "RETENTION -> hr360admin"; fi
    done
    echo "Servisleri yeniden olusturun: docker compose up -d <servis>"
    ;;
  status)
    c="$(pg_container)"
    roles="$(docker exec -i "$c" psql -X -At -U hr360admin -d hr360_operational \
      -c "SELECT rolname || ' ' || CASE WHEN rolcanlogin THEN 'LOGIN' ELSE 'NOLOGIN' END FROM pg_roles WHERE rolname LIKE 'hr360\_%' ORDER BY 1" 2>/dev/null || true)"
    while read -r k role _; do
      user="$(grep "^HR360_DB_USER_${k}=" "$ENV_FILE" 2>/dev/null | tail -1 | cut -d= -f2- || true)"
      state="$(printf '%s\n' "$roles" | awk -v r="$role" '$1 == r { print $2 }')"
      printf '%-14s %-22s rol:%-8s servis kullanicisi: %s\n' "$k" "$role" "${state:-yok}" "${user:-hr360admin (varsayilan)}"
    done < <(py services)
    rls="$(docker exec -i "$c" psql -X -At -U hr360admin -d hr360_operational \
      -c "SELECT count(*) FILTER (WHERE rls_enabled) || '/' || count(*) FROM hr360_rls_status()" 2>/dev/null || echo "?")"
    echo "Satir duzeyi guvenlik (RLS) acik tablo: $rls"
    ;;
  *)
    sed -n '2,31p' "$0" | sed 's/^# \{0,1\}//'
    [ -z "$cmd" ] || exit 2
    ;;
esac

#!/usr/bin/env bash
# Ortak .NET kaynaklarını (apps/shared-dotnet/*.cs) her servisin Shared/ klasörüne kopyalar.
# Servisler kendi klasörlerinden derlenir (Docker bağlamı), bu yüzden ortak kod proje başvurusu yerine
# kopya olarak taşınır; kopyalar elle DÜZENLENMEZ.
#   scripts/sync-shared.sh          kopyala
#   scripts/sync-shared.sh --check  kopyalar kaynakla aynı mı (CI); farklıysa çıkış kodu 1
set -euo pipefail
cd "$(dirname "$0")/.."
SRC=apps/shared-dotnet
HEADER="// OTOMATIK KOPYA: kaynak $SRC (scripts/sync-shared.sh). Bu dosyayi duzenlemeyin."
rc=0
for svc in apps/services/*/; do
  [ -n "$(ls "$svc"*.csproj 2>/dev/null)" ] || continue
  dst="${svc}Shared"
  for f in "$SRC"/*.cs; do
    want="$(printf '%s\n' "$HEADER"; cat "$f")"
    out="$dst/$(basename "$f")"
    if [ "${1:-}" = "--check" ]; then
      if [ ! -f "$out" ] || [ "$(cat "$out")" != "$want" ]; then echo "FARKLI: $out"; rc=1; fi
    else
      mkdir -p "$dst"; printf '%s\n' "$want" > "$out"
    fi
  done
done
[ "${1:-}" = "--check" ] && [ $rc = 0 ] && echo "Ortak kaynaklar eşit."
exit $rc

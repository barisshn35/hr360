#!/bin/sh
# PostgreSQL archive_command: tamamlanan WAL parcasini anlik geri donus (PITR) arsivine kopyalar.
# archive_mode her zaman acik; arsivleme yalnizca $ARCHIVE/ENABLED varken yapilir (scripts/pitr.sh
# enable/disable). Kapaliyken 0 doner ve WAL normal sekilde geri donusturulur — boylece ozellik
# veritabanini yeniden baslatmadan acilip kapanir.
#   $1 = %p (veri dizinine gore WAL yolu), $2 = %f (dosya adi)
# Basarisizlikta sifirdan farkli doner: PostgreSQL parcayi silmez, daha sonra yeniden dener.
set -eu
ARCHIVE=/var/lib/postgresql/pitr/wal
[ -f "$ARCHIVE/ENABLED" ] || exit 0
dst="$ARCHIVE/$2.gz"
# Ayni ad zaten varsa (yeniden deneme) icerik ayniysa basarili say, farkliysa ustune yazma.
if [ -f "$dst" ]; then
  gzip -dc "$dst" | cmp -s - "$1" && exit 0
  echo "archive-wal: $2 arsivde farkli icerikle var" >&2
  exit 1
fi
gzip -c "$1" > "$dst.tmp"
sync "$dst.tmp" 2>/dev/null || true
mv "$dst.tmp" "$dst"

# İzleme metrik dosyaları (node-exporter textfile toplayıcısı)

`scripts/backup.sh`, `scripts/backup.sh verify` ve `scripts/restore-drill.sh` bitince buraya
`*.prom` dosyaları yazar (son başarılı yedek zamanı, boyutu, şifreli/sunucu dışı bayrakları;
geri yükleme testinin sonucu). node-exporter bu dizini okur (`docker-compose.yml`,
`--collector.textfile.directory`), Prometheus alarmları (`deploy/monitoring/alerts.yml`:
YedekEski, YedekKaydiYok, GeriYuklemeTestiBasarisiz, GeriYuklemeTestiEski) bunlara bakar.

Dosyalar ortama özeldir, git'e girmez. Kişisel veri, dosya adı ya da anahtar içermez.

# İzleme alarmları

İzleme yığını isteğe bağlıdır (`scripts/monitoring.sh enable`, "monitoring" profili). Kurallar
`deploy/monitoring/alerts.yml`'dedir; Alertmanager kanalları `scripts/monitoring.sh alerts …` ile
ayarlanır. Saklama süreleri ve disk tavanları: `izleme-saklama.md`; yavaş sorgular: `yavas-sorgu.md`.

## Ölçüm kaynakları

| Kaynak | Ne ölçer | Nereden |
|---|---|---|
| Servisler (`/metrics`) | istek sayısı/süresi, bellek, önbellek, denetim zinciri, bot kuyruğu | prometheus-net, ml-inference için FastAPI instrumentator |
| postgres-exporter | bağlantılar, boyut, `pg_up`; **Kafka outbox** (`hr360_outbox_*`) | `deploy/monitoring/postgres-exporter-queries.yml` (yalnızca sayı ve süre, 60 sn önbellek) |
| node-exporter | disk, inode, CPU/bellek; **yedek ve geri yükleme testi** (`hr360_backup_*`, `hr360_restore_test_*`) | textfile toplayıcısı: `deploy/monitoring/textfile/*.prom` |
| blackbox-exporter | **TLS sertifikası bitişi** (`gateway:443`), gateway HTTP yanıtı | `deploy/monitoring/blackbox.yml` |

Yedek metriklerini `scripts/backup.sh` (başarıyla bitince), `scripts/backup.sh verify` ve
`scripts/restore-drill.sh` yazar. Zamanlama: `sudo scripts/schedule.sh install backup restore-drill`.
Metrik dosyaları kişisel veri, dosya adı ya da anahtar içermez; git'e girmez.

## Alarmlar ve yapılacaklar

| Alarm | Önem | Koşul | İlk bakılacak yer |
|---|---|---|---|
| ServisErisilemiyor | kritik | hedef 2 dk yanıt vermiyor | `docker compose ps`, `docker compose logs <servis>` |
| Yuksek5xxOrani / …Kritik | uyarı / kritik | 5xx oranı > %5 / > %20 (servis başına) | Grafana › Loglar; son dağıtım, veritabanı, Kafka |
| MLServisi5xx | uyarı | ml-inference 5xx > %5 | `docker compose logs ml-inference` |
| YavasYanit / …Kritik | uyarı / kritik | p95 > 2 sn / > 5 sn (SSE hariç) | "En yavaş uçlar" tablosu, `yavas-sorgu.md` |
| GatewayYanitVermiyor | kritik | gateway iç ağdan yanıt vermiyor | `docker compose logs gateway`, `nginx -t` |
| PostgresErisilemiyor | kritik | `pg_up == 0` | `docker compose logs postgres`, disk |
| PostgresBaglantiYuksek | uyarı | > 150 bağlantı | bağlantı sızıntısı yapan servis |
| OutboxBirikiyor | uyarı | yayınlanmayı bekleyen en eski olay > 5 dk | `docker compose logs kafka`; servis günlüklerinde "Event yayinlanamadi" |
| OutboxTeslimEdilemedi | uyarı | 10 denemede yayınlanamayan olay var | aşağıdaki SQL |
| BotKuyruguBirikiyor | uyarı | bot yeniden deneme kuyruğu > 200 (30 dk) | Slack/Teams erişimi, hız sınırı |
| DiskDoluyor / DiskDoluyorDiger | kritik / uyarı | boş alan < %10 | `docker system df`, `backups/`, izleme verisi |
| DiskDolacak | uyarı | bu hızla 24 saatte dolacak (boş < %25) | aynı |
| DiskInodeAzaldi | uyarı | inode < %10 | çok sayıda küçük dosya (günlükler, geçici dosyalar) |
| SertifikaBitiyor / …Kritik | uyarı / kritik | sertifikanın bitmesine < 21 / < 7 gün | `scripts/tls.sh check`, `scripts/tls.sh retry` |
| YedekEski / YedekCokEski | uyarı / kritik | son başarılı yedek > 26 / > 50 saat | `scripts/schedule.sh status`, `backups/backup.log` |
| YedekKaydiYok | uyarı | 24 saattir hiç yedek metriği yok | yedek zamanlanmamış |
| YedekSunucuDisindaDegil | bilgi (haftalık) | yedek yalnızca bu sunucuda | `BACKUP_S3_*` ya da `--to-minio` (README "Yedekleme") |
| GeriYuklemeTestiBasarisiz | kritik | son geri yükleme testi başarısız | `backups/restore-drill.log`, `scripts/restore-drill.sh` |
| GeriYuklemeTestiEski | uyarı | son başarılı test > 8 gün | `restore-drill` zamanlaması |
| DenetimZinciriBozuk / …KontrolEdilmiyor | kritik / uyarı | KVKK denetim kaydı zinciri | governance günlüğü "Denetim zinciri SORUNU" |
| MLOzellikKaymasi / MLTahminKaymasi | uyarı | PSI > 0,2 | model kartı "Veri kayması" |
| PrometheusKuralHatasi, PrometheusYapilandirmaYuklenemedi, AlarmIletilemiyor | uyarı | izleme yığınının kendisi | `docker compose logs prometheus alertmanager`, `scripts/monitoring.sh alerts test` |

Kritik eşik aşıldığında aynı ölçümün uyarı alarmı ayrıca iletilmez (Alertmanager inhibit kuralları).

### Teslim edilemeyen outbox olayları

```bash
docker exec -it hr360-postgres-1 psql -U hr360admin -d hr360_operational -c \
  'SELECT "Topic","EventType","AttemptCount",left("LastError",120),"CreatedAt" FROM messaging_outbox
    WHERE "PublishedAt" IS NULL AND "AttemptCount" >= 10 ORDER BY "CreatedAt" LIMIT 20'
# Neden giderildikten sonra yeniden denetmek için (yayıncılar 5 sn içinde alır):
#   UPDATE messaging_outbox SET "AttemptCount" = 0 WHERE "PublishedAt" IS NULL AND "AttemptCount" >= 10;
```

Olay yükleri kişisel veri içerebilir; yükü (`"Payload"`) ekrana dökmeyin.

## Eşikleri değiştirme ve doğrulama

Eşikler tek sunuculu kurulum içindir. Değiştirdikten sonra:

```bash
docker run --rm --entrypoint promtool -v "$PWD/deploy/monitoring:/m:ro" -w /m prom/prometheus:v2.55.1 check rules /m/alerts.yml
docker run --rm --entrypoint promtool -v "$PWD/deploy/monitoring:/m:ro" -w /m prom/prometheus:v2.55.1 test rules /m/alerts.test.yml
curl -X POST http://127.0.0.1:9090/-/reload      # çalışan Prometheus'a yükle
```

`alerts.test.yml` eşik testlerini içerir (CI "Dağıtım dosyaları" işinde çalışır); eşiği
değiştirirseniz testi de güncelleyin.

## Etkinleştirme (mevcut kurulum)

İzleme açık bir kurulumda yeni bileşenler (blackbox-exporter, textfile dizini, outbox sorgusu)
için:

```bash
docker compose --profile monitoring up -d prometheus node-exporter postgres-exporter blackbox-exporter
docker compose --profile monitoring restart alertmanager     # yeni yönlendirme/susturma kuralları
```

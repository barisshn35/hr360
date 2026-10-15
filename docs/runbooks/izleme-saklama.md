# İzleme verisi: saklama süreleri ve disk sınırları

İzleme yığını (`monitoring` profili, `scripts/monitoring.sh enable`) üç yerel birimde veri tutar.
Hepsinin bir süre sınırı, Prometheus'un ayrıca bir boyut tavanı vardır. Ayarlar `.env`'den okunur;
değiştirdikten sonra ilgili konteyner yeniden oluşturulmalıdır.

| Bileşen | Birim | Ayar (`.env`) | Varsayılan | Nasıl siler |
|---|---|---|---|---|
| Prometheus (metrikler) | `prometheus-data` | `PROMETHEUS_RETENTION`, `PROMETHEUS_RETENTION_SIZE` | `15d`, `5GB` | Hangisi önce dolarsa en eski 2 saatlik bloklar silinir |
| Loki (konteyner logları) | `loki-data` | `LOKI_RETENTION`, `LOKI_INGESTION_RATE_MB` | `168h` (7 gün), `4` MB/sn | Compactor 10 dakikada bir süresi dolan parçaları işaretler, 2 saat sonra siler |
| Tempo (izler, isteğe bağlı) | `tempo-data` | `TEMPO_RETENTION` | `72h` | Compactor |
| Alertmanager | `alertmanager-data` | — | — | Yalnızca susturmalar/bildirim durumu; birkaç KB |

```bash
# .env'de değiştir, sonra:
docker compose up -d --force-recreate prometheus loki
# Kontrol
docker compose exec prometheus wget -qO- localhost:9090/api/v1/status/flags | grep -o '"storage.tsdb.retention[^,]*'
docker compose logs loki | grep -i retention
```

## Boyut tahmini

- **Prometheus:** 11 kazıma hedefi, 15 sn aralık. .NET servisi başına yaklaşık 1–2 bin seri
  (HTTP histogramları uç sayısıyla büyür), toplam ~25–35 bin aktif seri. Sıkıştırılmış örnek başına
  ~1,5 bayt ile günde yaklaşık 250–350 MB, 15 günde 4–5 GB. `5GB` tavan bu yüzden süre sınırıyla
  aynı yere denk gelir; daha uzun saklama isteniyorsa ikisini birlikte artırın (ör. `30d` + `10GB`).
  WAL (son ~2 saat) tavana dahildir ama sınırı birkaç yüz MB aşabilir; diskte %20 pay bırakın.
- **Loki:** Loki boyut tabanlı silme yapmaz. Disk = günlük log hacmi × saklama günü. Normal yükte
  servisler günde 50–200 MB log üretir (sıkıştırılınca 5–10 kat küçülür), 7 günde birkaç yüz MB.
  Hatalı bir döngüde log patlaması diski doldurmasın diye alım hızı kiracı başına 4 MB/sn
  (`LOKI_INGESTION_RATE_MB`, ani yük 8 MB) ve akış başına 2 MB/sn ile sınırlıdır; aşan satırlar
  Loki tarafından reddedilir (Promtail `429` günlüğe yazar), konteynerin kendi `json-file` logu
  etkilenmez.
- **Docker'ın kendi logları:** Loki'den bağımsızdır. Sunucuda `/etc/docker/daemon.json`
  `{"log-driver":"json-file","log-opts":{"max-size":"20m","max-file":"3"}}` olmalı (konteyner
  başına en çok 60 MB). Yeni kurulumda bu dosya yoksa ekleyin ve Docker'ı yeniden başlatın.

## KVKK notu

Loglarda kişisel veri olmaması hedeflenir (servisler kimlik yerine kimlik numarası/izleme kodu
yazar, izler collector'da arındırılır). Yine de loglar "teknik kayıt" saklama kategorisindedir:
`LOKI_RETENTION` kurumun imha politikasındaki süreden uzun olmamalıdır. Süre kısaltıldığında
eski veri bir sonraki compactor turunda (en geç ~2 saat 10 dakika) silinir. Prometheus metrikleri
kişisel veri içermez (etiketlerde kiracı kısa adı ve uç şablonu var, kişi kimliği yok).

## Disk doluluğu alarmı

`deploy/monitoring/alerts.yml` içinde node-exporter tabanlı disk alarmı vardır; izleme
birimleri Docker'ın veri dizininde (`/var/lib/docker/volumes`) durduğu için kök dosya sisteminin
doluluğu izlenir. Hacimleri görmek için:

```bash
docker system df -v | grep -E 'prometheus-data|loki-data|tempo-data|postgres-data'
```

Acil durumda (disk %95+): `PROMETHEUS_RETENTION_SIZE` ve `LOKI_RETENTION` değerini düşürüp
konteynerleri yeniden oluşturun; tamamen boşaltmak için `scripts/monitoring.sh purge`
(metrik/log/iz verisini siler, uygulama verisine dokunmaz).

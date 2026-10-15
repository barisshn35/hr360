# Yavaş sorgu incelemesi

İki araç var: istek üzerine açılan **pg_stat_statements** (hangi sorgu ne kadar süre harcıyor) ve
her zaman açık olan **tablo istatistikleri** (hangi tablo çok ardışık tarama yapıyor).

## 1. Tablo istatistikleri (eklenti gerektirmez)

```bash
scripts/slow-queries.sh seqscan 20
```

Çok satırlı bir tabloda `okunan_satir` yüksek ve `indeks_tarama` düşükse indeks adayıdır. Birkaç
satırlık tablolarda ardışık tarama normaldir (planlayıcı indeksi kullanmaz).

## 2. pg_stat_statements (isteğe bağlı, varsayılan KAPALI)

`shared_preload_libraries` yalnızca PostgreSQL başlarken okunduğu için açmak bir yeniden
başlatma gerektirir (birkaç saniye; servisler bağlantılarını yeniden kurar). Varsayılan
`docker-compose.yml` değiştirilmez; ek dosya kullanılır:

```bash
docker compose -f docker-compose.yml -f deploy/postgres/pg-stat-statements.yml up -d postgres
scripts/slow-queries.sh enable          # CREATE EXTENSION (bir kez)
# ... bir süre normal kullanım / yük testi (scripts/loadtest.sh) ...
scripts/slow-queries.sh top 20          # toplam süreye göre
scripts/slow-queries.sh mean 20         # ortalama süreye göre (en az 20 çağrı)
scripts/slow-queries.sh reset           # yeni ölçüm öncesi sıfırla
```

Kalıcı olsun isteniyorsa `.env`'e `COMPOSE_FILE=docker-compose.yml:deploy/postgres/pg-stat-statements.yml`
yazılır (test ortamında chat-mock dosyası da kullanılıyorsa listeye eklenir). Kapatmak için ek dosya
olmadan `docker compose up -d postgres`.

Ek dosya ayrıca `log_min_duration_statement` (varsayılan 500 ms, `.env`: `PG_SLOW_QUERY_MS`)
açar: eşiği aşan sorgular PostgreSQL loguna (`docker compose logs postgres`, izleme açıksa
Loki'de `{container="hr360-postgres-1"} |= "duration:"`) yazılır.

Maliyet: pg_stat_statements için ~1–2 MB paylaşımlı bellek ve ihmal edilebilir CPU;
`track_io_timing` bazı sanal makinelerde saat çağrısı maliyeti ekler
(`docker compose exec postgres pg_test_timing` ile kontrol edilebilir).

### KVKK

- pg_stat_statements sorgudaki sabitleri `$1, $2 …` ile değiştirir; uygulamanın sorguları
  parametreli olduğundan kişisel veri saklanmaz.
- `log_min_duration_statement` parametreli sorgularda değerleri yazmaz
  (`log_parameter_max_length=0`). Ancak **psql ile elle çalıştırılan** (basit protokol) bir
  sorgu loga sabitleriyle düşer; canlıda elle sorgu çalıştırırken bunu unutmayın.
- Çıktılar sunucuda okunur; repoya, bilete ya da sohbete ham olarak yapıştırılmaz.

## 3. İndeks ekleme kuralları

1. Önce sorguyu bul (servis kodu ya da `top` çıktısı), sonra canlıda **salt okunur**
   plan al: `BEGIN READ ONLY; EXPLAIN <sorgu>; ROLLBACK;` (ANALYZE canlıda yalnızca
   SELECT için ve dikkatle; yazan sorgularda asla).
2. Demo verisi küçük olduğu için planlayıcı canlıda çoğunlukla ardışık tarama seçer.
   Etkiyi görmek için geçici `postgres:18-alpine` konteynerinde şemayı kurup sentetik veriyle
   (yüz binlerce satır) indeksli/indekssiz `EXPLAIN ANALYZE` karşılaştırın.
3. Migrasyon `scripts/sql/YYYY-MM-DD_<konu>.sql`, `CREATE INDEX IF NOT EXISTS`. Her indeks
   yazmayı yavaşlatır; özellikle `audit_log` (her değişiklikte satır) için gereksiz indeks eklemeyin.
4. Büyük canlı tabloda (milyonlarca satır) düz `CREATE INDEX` tabloya yazmayı indeks bitene kadar
   bekletir. O durumda migrasyondaki ifadeyi elle, **transaction dışında** ve tek tek
   `CREATE INDEX CONCURRENTLY IF NOT EXISTS ...` olarak çalıştırın; başarısız olursa kalan
   INVALID indeksi `DROP INDEX CONCURRENTLY` ile silip tekrarlayın. Ardından migrasyon dosyasını
   yine uygulayın (artık hepsini atlar). `sql-all-schemas.sql` boş kurulumda çalıştığı için
   dosyada CONCURRENTLY kullanılmaz.

## 2026-10-25 indeks seti (`scripts/sql/2026-10-25_indeksler.sql`)

Kod okunarak bulunan, sık çalışan ama indekssiz filtreler: iş akışı onay kutusu ve görünürlük
(onaycı/vekil), SLA tırmandırma işi, vekaletlerim, iş akışı olay tüketicilerinin
`WorkflowRequestId` aramaları (izin, masraf, seyahat), denetim kaydı kullanıcı/varlık aramaları,
yönetici ekip sorgularındaki `HeadEmployeeId` birleşimi ve kişi bazlı birkaç liste.

Ölçüm (600 bin satırlık sentetik `audit_log`, 200 bin talep / 400 bin adım):

| Sorgu | Önce | Sonra |
|---|---|---|
| İş akışı listesi (çalışan görünürlüğü) | 614 ms (adımlarda ardışık tarama) | 75 ms |
| Vekalet aktarımı (onaycı + Pending) | 71 ms | 0,07 ms |
| SLA tırmandırma (tüm kiracılar) | 73 ms (paralel ardışık tarama) | 5,7 ms |
| Denetim araması, kullanıcı filtresi | 70 ms | 1,5 ms |
| KVKK erişim dökümü (kişi) | 0,34 ms (PG18 skip scan) | 0,14 ms |

Canlıda (38 bin satırlık `audit_log`) kullanıcı filtreli denetim araması bugün ardışık tarama
yapıyor (plan maliyeti ~3300). Bildirim kutusu için ayrı indeks eklenmedi: mevcut
`(RecipientEmployeeId, Status)` yeterli.

# HR360 işletim kılavuzu

Kurulumdan sonra sunucuyu yöneten kişi için: yönetim komutları, izleme ve alarmlar, dağıtık izleme
ve testler. Kurulum, güncelleme ve yedekleme özeti ana [README](../../README.md)'dedir.

## Yönetim komutları

| İş | Komut |
|---|---|
| HTTPS'i aç/kapat, sertifika değiştir | `scripts/tls.sh enable ... / disable / status` |
| Let's Encrypt (olmazsa geçici sertifika + saatlik yeniden deneme) | `scripts/tls.sh auto --host hr.sirket.com --email it@sirket.com`; ön koşullar `scripts/tls.sh check`, hemen dene `scripts/tls.sh retry`, uçtan uca denetim `scripts/tls.sh verify` |
| TLS'i öndeki bir yük dengeleyici sonlandırıyorsa | `scripts/tls.sh external --host hr.sirket.com` |
| Keycloak paneli erişimi | `scripts/keycloak-admin-access.sh open / ip <IP,...> / port [IP,...] / status` |
| Keycloak servis hesabı, parola politikası, giriş olayları (idempotent) | `scripts/keycloak-service-account.sh [apply / client / policy / status]` — kurulum ve güncelleme bir kez kendisi çalıştırır |
| E-posta (SMTP) sunucusu | Arayüzden: **Platform › Sistem ayarları** (kaydet, deneme e-postası, test kutusuna dön). Arayüzde sunucu girildiyse o geçerlidir; sunucudan (kurulum ayarı, `.env`): `scripts/smtp.sh set --from ik@sirket.com --password '…'` (sunucu adresten bulunur), `scripts/smtp.sh set` (soru sorar), `scripts/smtp.sh test adres@sirket.com`, `scripts/smtp.sh status`, `scripts/smtp.sh mailpit` |
| Sunucunun kendi posta servisi (gelişmiş, isteğe bağlı) | `scripts/smtp.sh self [--from noreply@hr.sirket.com]` (aç: 25/tcp + PTR denetimi, DNS kayıtları), `scripts/smtp.sh dns` (kayıtları yeniden yaz), kapatmak için `scripts/smtp.sh set …` ya da `scripts/smtp.sh mailpit`. Ayrıntı aşağıda |
| Keycloak giriş ekranı teması (HR360 görünümü + Türkçe) | Kurulum ve güncelleme (`./install.sh`) sırasında otomatik uygulanır. Elle: `scripts/keycloak-theme.sh`; Keycloak'ın kendi temasına dönmek için `scripts/keycloak-theme.sh default` |
| Yeni sürüme güncelleme (tek komut, otomatik geri dönüş) | `scripts/update.sh` (önce yedek, sonra sürüm, göçler, imajlar ve tüm servislerin sağlık denetimi; denetim geçmezse önceki sürüme döner). `scripts/update.sh --ref v1.0.0`, `scripts/update.sh rollback`, `scripts/update.sh status` |
| İzleme (Prometheus + Grafana + Loki + Alertmanager) | `scripts/monitoring.sh enable / status / password / disable / purge` |
| Alarm kanalları (e-posta, Slack, Teams) | `scripts/monitoring.sh alerts status / email … / slack … / teams … / test` |
| Dağıtık izleme (OpenTelemetry → Tempo, KVKK maskeli) | `scripts/monitoring.sh tracing on [oran] / off / status` |
| Testler | `scripts/test.sh unit / integration / e2e / all` (ayrıntı: [tests/README.md](../../tests/README.md)) |
| Yük testi | `scripts/loadtest.sh smoke / load / stress` |
| Güvenlik taraması | `scripts/security-scan.sh repo / deps / images` (Trivy, npm audit, NuGet) |
| Yedek al | `scripts/backup.sh [--keep 14] [--with-env] [--no-minio] [--out DİZİN]` |
| Gece yedeği + haftalık geri yükleme testi | `scripts/backup.sh schedule --at 03:15 --keep 30 --verify-weekly`, kaldırmak için `unschedule`, durum `scripts/backup.sh status` |
| Yedeği doğrula (geçici veritabanına geri yükler) | `scripts/backup.sh verify [arşiv]` |
| Yedekten dön | `scripts/restore.sh backups/hr360-….tar.gz[.enc] [--with-env] [--only-db] [--yes]` |
| Anlık geri dönüş (PITR: veritabanını geçmişteki bir dakikaya döndür) | Arayüzden: **Platform › Bakım › Anlık geri dönüş** (aç/kapat, bir ana dön, eski veriyi sil). Sunucudan: `scripts/pitr.sh enable / status / disable [--purge]`; dönüş `scripts/pitr.sh restore --to "2026-10-19 14:05" --yes` (İstanbul saati), sonra `scripts/pitr.sh cleanup`. Ayrıntı aşağıda |
| İsteğe bağlı modüller ve şirket kipi | Arayüzden: **Platform › Bakım › Modüller** (seçimi işaretle, Uygula) ve **Platform › Bakım › Şirket kipi**. Sunucudan: `scripts/modules.sh list / enable MODÜL… / disable MODÜL… / set timeshift,compensation\|all`, `scripts/tenancy.sh status / single / multi`. Ayrıntı aşağıda |

Tüm ortam değişkenlerinin açıklamalı listesi: [.env.example](../../.env.example).

### Bakım ve hata sayfaları

Uygulama ya da Keycloak yanıt vermediğinde (güncelleme, yeniden başlatma) gateway ham nginx
hatası yerine `deploy/nginx/errors/bakim.html` sayfasını 502/503/504 koduyla gösterir; sayfa
JavaScript kullanmaz ve 20 saniyede bir kendiliğinden yeniden dener. `/api` yanıtları bundan
etkilenmez. Çevrimdışı açılışta servis çalışanı önbellekteki uygulamayı, o da yoksa
`offline.html`'i gösterir. Keycloak giriş temasının hata sayfası `deploy/keycloak/themes/hr360/login/error.ftl`'dir;
tema dosyası değiştiğinde Keycloak'ın sıkıştırılmış kaynak önbelleği `scripts/keycloak-theme.sh` ile temizlenir.

### Herkese açık site, çerezler ve arama motorları

Giriş sayfasının alt bilgisi **SSS** (`/sss`), **Gizlilik** (`/gizlilik`), **Kullanım koşulları**
(`/kosullar`) ve **İletişim** (`/iletisim`) sayfalarına bağlanır. İçerikleri sunucudan değil
**Platform › Sistem ayarları › Herkese açık site** ekranından düzenlenir: işletmeci (veri sorumlusu)
adı, adres, e-posta, telefon, KEP ve VERBİS numarası; gizlilik politikası ve kullanım koşulları
(Markdown); SSS soruları. Boş bırakılan metin yerine KVKK'ya göre hazırlanmış varsayılan şablon
gösterilir; şablon genel bir örnektir, işletmeci kendi durumuna göre gözden geçirmelidir.

- **Çerezler:** Uygulama yalnızca zorunlu çerez ve tarayıcı depolaması kullanır (Keycloak oturum
  çerezleri, tema/dil, taslaklar, uygulama önbelleği); reklam, analiz ya da izleme çerezi yoktur. Bu
  yüzden rıza değil bilgilendirme bandı gösterilir (giriş, herkese açık sayfalar ve Keycloak giriş
  ekranı); "Anladım" seçimi tarayıcıda (`hr360.cookieNotice`) tutulur. Ayrıntılı çerez tablosu
  gizlilik sayfasında her zaman ayrı bölümdür (`/gizlilik#cerezler`). İzleme/analiz aracı eklenirse
  kategorili rıza gerekir.
- **Arama motorları:** Varsayılan **kapalı** (İK uygulaması dizinlenmesin): `/robots.txt` tüm
  siteyi engeller, `/sitemap.xml` boştur, sayfalara `noindex` eklenir. "Arama motorlarında görünür"
  açılınca robots.txt yalnızca panel, API, Keycloak ve jetonlu bağlantıları engeller; sitemap giriş,
  kayıt (açıksa), SSS, gizlilik, koşullar ve iletişim sayfalarını `PUBLIC_ORIGIN` (ya da doğrulanmış
  özel alan adı) ile mutlak adresli listeler. İkisi de tenant-service'te üretilir, gateway yönlendirir.
- **Paylaşım önizlemesi:** `index.html` Open Graph / X etiketleri taşır; görsel `og-image.png`
  (1200×630). Mutlak adres web kapsayıcısının nginx'inde isteğin kökeniyle doldurulur.
- **Teşekkür sayfası:** Herkese açık kayıt e-postayla devam ediyorsa ve parola linkle belirlenince
  `/tesekkurler` açılır. Kayıtta parola linki ekranda gösteriliyorsa kayıt ekranında kalınır.

### Sunucunun kendi posta servisi

İsteğe bağlı, varsayılan değil ve yalnızca alan adlı kurulumda (`install.sh --domain … --mail-mode self`
ya da kurulumdan sonra `scripts/smtp.sh self`). Paketteki `mailer` servisi (Postfix + OpenDKIM,
compose `mailer` profili) uygulamanın e-postalarını alıcıların posta sunucularına doğrudan teslim eder;
ayrı bir e-posta hesabı ya da SMTP parolası gerekmez. Servis dışarıya port açmaz; yalnızca iç ağdaki
HR360 servislerinden (`mailer:25`) e-posta kabul eder. Arayüzde **Platform › Sistem ayarları** bu kipi
"Sunucunun kendi posta servisi" olarak gösterir; arayüzden başka bir SMTP sunucusu girilirse o geçerli
olur, "Sunucunun kendi posta servisine dön" ile geri dönülür.

Teslim edilebilirlik için gerekenler (kurulum ilk ikisini denetler; geçmezse uyarır, `--yes` ile
kurulumu durdurur, yine de devam etmek için `HR360_MAIL_IGNORE_CHECKS=1`):

1. **25/tcp çıkışı.** AWS, Azure, GCP, Hetzner, DigitalOcean gibi sağlayıcılar yeni hesaplarda 25'i
   engeller; destek talebiyle açtırın. Açılamıyorsa bu kipi kullanmayın, gerçek bir SMTP hesabı girin.
2. **PTR (ters DNS).** Sunucunun genel IP'si alan adını göstermeli (`203.0.113.10 → hr.sirket.com`).
   PTR DNS sağlayıcınızda değil, sunucu/IP sağlayıcınızın panelinde tanımlanır.
3. **SPF, DKIM, DMARC.** Kurulum özetinde ve `deploy/mailer/dns-kayitlari.txt` dosyasında yazılır
   (`scripts/smtp.sh dns` ile yeniden üretilir):
   - SPF: `v=spf1 ip4:<IP> ~all` (alan adında zaten SPF varsa yeni kayıt eklemeyin, mevcut kayda `ip4:<IP>` ekleyin).
   - DKIM: `hr360._domainkey.<alan>` TXT; anahtar ilk açılışta `mailer-dkim` biriminde üretilir ve korunur.
     Birim silinirse yeni anahtar üretilir, DNS kaydını güncellemeniz gerekir.
   - DMARC: `_dmarc.<alan>` TXT, `p=none` ile başlayın; raporlar temizse `p=quarantine`'e geçin.

**Isınma ve itibar.** Yeni bir IP'nin itibarı yoktur: ilk günlerde az sayıda e-posta gönderin, toplu
davetleri birkaç güne yayın. Gmail ve Outlook itibar oluşana kadar e-postaları istenmeyen klasörüne
atabilir. Teslim durumunu `docker compose logs mailer` ile izleyin; ertelenen e-postalar 2 gün
yeniden denenir. Paylaşımlı ya da kara listedeki bir IP'de bu kip uygun değildir.

## Modüller ve şirket kipi

**Arayüzden (önerilen):** **Platform › Bakım › Modüller** kartında sekiz isteğe bağlı modül (işe alım, işe
alıştırma ve demirbaş, puantaj ve vardiya, performans, eğitim, ücret ve bordro, yapay zekâ, masraf ve seyahat)
durumları ve yaklaşık bellek sınırlarıyla listelenir. İstenen seçimi işaretleyip **Uygula**'ya basın; özet
penceresi açılacak ve kapatılacak modülleri gösterir. İş, bakım çalıştırıcısı üzerinden `scripts/modules.sh`
ile tek seferde uygulanır:

- **Açılan modül:** imajı derlenir (modül başına birkaç dakika), Kafka tüketici konumu sona alınır (kapalıyken
  biriken eski olaylar işlenmez), servisler yeniden başlatılır.
- **Kapatılan modül:** ekranları menülerden kalkar, konteyneri durdurulur. **Veri silinmez**; modül istendiği zaman
  yeniden açılabilir ve kaldığı yerden devam eder. Kapatma yazılı onay ister (`MODÜLÜ KAPAT`).
- **Çekirdek** (çalışanlar, organizasyon, izin, onaylar, bildirimler, belgeler, KVKK) her zaman açıktır.

İş bitince menüler yeni seçime göre güncellenir. Sürmekte olan başka bir bakım işi varken uygulanamaz.

**Şirket kipi** (`HR360_TENANCY`, yoksa `multi`): **Platform › Bakım › Şirket kipi** kartı geçerli kipi ve
şirket sayısını gösterir.

- *Çok şirketli* kipte ve kurulumda tam olarak bir şirket varsa **Tek şirket kipine geç** düğmesi görünür
  (birden çok şirket varken tek şirket kipine geçilemez).
- *Tek şirket* kipinde **Çok şirketli kipe geç** yazılı onay ister (`ÇOK ŞİRKET`): şirket listesi, yeni şirket ve
  herkese açık kayıt kullanılabilir olur; platform ve şirket yönetimi yeniden ayrılır. İkinci şirket
  eklendikten sonra tek şirket kipine dönülemez, bu yüzden pratikte tek yönlüdür.

Geçişte yalnızca `.env` değişir ve servisler `docker compose up -d` ile yeni ortamı alır; veri ve şirket adı
(`HR360_COMPANY_NAME`) değişmez.

**Sunucudan:**

```bash
scripts/modules.sh list                       # modüller, durum, bellek, servisler
scripts/modules.sh enable timeshift ml        # aç (imaj derlenir, servis başlar)
scripts/modules.sh disable recruitment        # kapat (veri korunur)
scripts/modules.sh set timeshift,compensation,expense   # seçimi bir kerede uygula; "all" = hepsi
scripts/modules.sh status | json              # makine için durum (bakım çalıştırıcısı json'u okur)
scripts/tenancy.sh status                     # tenancy=single|multi, tenants=N
scripts/tenancy.sh single                     # yalnızca tam olarak bir şirket varken
scripts/tenancy.sh multi
```

## Anlık geri dönüş (PITR)

Arayüz: **Platform › Bakım › Anlık geri dönüş** (aşağıdaki işlemler bakım çalıştırıcısı üzerinden). Gece yedeği (`scripts/backup.sh`) bir günlük veri kaybına kadar korur. Yanlışlıkla toplu silme ya
da hatalı bir içe aktarma gibi durumlarda veritabanını o işlemden **hemen önceki dakikaya** döndürmek
için PostgreSQL WAL arşivi kullanılır:

- `scripts/pitr.sh enable` arşivlemeyi açar ve ilk temel yedeği alır. Açıkken gece yedeği temel yedeği
  de yeniler (en yeni 2 tanesi kalır; eskilerin WAL parçaları silinir). En kötü durumda son 5 dakika
  kaybolur (`archive_timeout=300`).
- `scripts/pitr.sh status`: durum, arşiv boyutu, temel yedekler (geri dönülebilecek en eski an, en eski
  temel yedeğin bitişidir), son arşivlenen WAL ve arşiv hataları.
- `scripts/pitr.sh restore --to "YYYY-AA-GG SS:DD" --yes`: tüm veritabanlarını (uygulama, Keycloak,
  MLflow) o ana döndürür; servisler kısa süre durur. Mevcut veri `pitr/pre-restore-…` olarak saklanır,
  doğrulayınca `scripts/pitr.sh cleanup` ile silin. Dönüşten sonra KVKK saklama politikaları yeniden
  çalışır (o andan sonra imha edilmiş kayıtlar geri gelmesin).
- Nesne deposu (MinIO: belgeler, fotoğraflar) geri dönmez; o andan sonra yüklenen dosyalar kayıtsız
  kalır, sonra silinen dosyalar eksik görünebilir.
- Arşiv ve temel yedekler PostgreSQL hacmindedir, yani diskin kendisini korumaz: disk kaybına karşı
  şifreli gece yedeği ve dış depo kopyası (S3/MinIO) sürmelidir. Arşivin boyutu yazma yoğunluğuna
  bağlıdır; `status` ile izleyin.
- `archive_mode` her zaman açıktır; arşivleme `pitr/wal/ENABLED` dosyasıyla açılıp kapandığından
  özelliği açmak veritabanını yeniden başlatmaz. (Bu ayardan önceki bir kurulumda postgres bir kez
  yeniden başlatılmalıdır; güncelleme bunu kendisi yapar.)

## İzleme

`scripts/monitoring.sh enable` isteğe bağlı `monitoring` profilini açar:
Prometheus (15 .NET servisi, ML servisi, PostgreSQL ve sunucu metrikleri),
Loki + Promtail (tüm konteyner logları, 7 gün) ve Grafana. Saklama süreleri ve disk
tavanları (`PROMETHEUS_RETENTION[_SIZE]`, `LOKI_RETENTION`): `docs/runbooks/izleme-saklama.md`;
yavaş sorgu incelemesi (isteğe bağlı pg_stat_statements): `docs/runbooks/yavas-sorgu.md`. Grafana
`<adres>/grafana/` altında açılır (kullanıcı `admin`, parola
`scripts/monitoring.sh password`). "HR360 — Servis sağlığı" panosu hazır gelir:
ayakta olan servisler, istek hızı, 5xx oranı, p95 süre, en yavaş uçlar,
bellek/CPU, PostgreSQL bağlantı ve boyutu, son yedek, geri yükleme testi, TLS sertifikası,
Kafka outbox birikimi, etkin alarmlar, hata logları ve log araması.

.NET servisleri tek satırlık JSON log yazar (`HR360_LOG_FORMAT=json`, varsayılan; düz metin için
`.env`'de `HR360_LOG_FORMAT=text`). İstek içindeki her satırda `CorrelationId` (yanıt başlığı
`X-Correlation-Id` ve denetim kaydıyla aynı) ve varsa şirket (`Tenant`) bulunur. Kullanıcının
bildirdiği bir hatayı bulmak için: Grafana › Explore › Loki, `{service=~".+"} |= "<kimlik>"`;
izleme kapalıysa `docker compose logs <servis> | grep <kimlik>`.

Alarm kuralları (`deploy/monitoring/alerts.yml`: servis erişilemiyor, 5xx oranı,
yavaş yanıt, bellek, disk (doluluk, 24 saatlik tahmin, inode), PostgreSQL, Kafka outbox
birikimi, TLS sertifikası bitişi, yedek yaşı ve geri yükleme testi, denetim zinciri; tam liste
ve ne yapılacağı: `docs/runbooks/izleme-alarmlari.md`) Alertmanager üzerinden
e-posta, Slack ve Microsoft Teams'e gider; düzelince "düzeldi" mesajı gelir:

```bash
scripts/monitoring.sh alerts email ops@sirket.com,it@sirket.com   # SMTP_* ile (Microsoft 365: smtp.office365.com:587)
scripts/monitoring.sh alerts slack https://hooks.slack.com/services/…
scripts/monitoring.sh alerts teams https://….logic.azure.com/workflows/…
scripts/monitoring.sh alerts test     # tüm kanallara deneme alarmı
```

Teams adresi kanalda **Workflows › "Post to a channel when a webhook request is
received"** şablonuyla alınır (eski "Incoming Webhook" bağlayıcıları Microsoft
tarafından kapatıldı). Alarmlar Grafana'da da (Alerting, "Alertmanager" veri
kaynağı) görülür ve susturulabilir. Yığın yaklaşık 2,3 GB bellek sınırı ekler (Tempo ve collector dahil).
Prometheus (`127.0.0.1:9090`) ve Alertmanager (`127.0.0.1:9093`) yalnızca
sunucunun kendisinden erişilebilir.

#### Dağıtık izleme (OpenTelemetry) ve iş metrikleri

Tüm .NET servisleri OpenTelemetry ile iz üretebilir: gelen HTTP istekleri, servisler
arası HttpClient çağrıları, PostgreSQL sorguları (Npgsql) ve Kafka yayın/tüketim
adımları (outbox yayıncısı W3C `traceparent` başlığını mesaja yazar, tüketici izi
sürdürür). **Varsayılan kapalıdır**: servis yalnızca `OTEL_EXPORTER_OTLP_ENDPOINT`
tanımlıysa izleme kurar; tanımsızken ek yük yoktur.

```bash
scripts/monitoring.sh enable          # izleme yığını (tempo + otel-collector dahil)
scripts/monitoring.sh tracing on      # tüm istekler; örnekleme için: tracing on 0.2
scripts/monitoring.sh tracing status
scripts/monitoring.sh tracing off
```

`tracing on`, `deploy/monitoring/tracing.env` dosyasına
`OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317` ve
`OTEL_TRACES_SAMPLER_ARG` yazar (tüm .NET servisleri bu dosyayı `env_file` olarak
okur) ve servisleri yeniden başlatır. Akış: servis → `otel-collector` → `tempo`;
ikisi de yalnızca iç ağdadır. İzler Grafana › Explore › **Tempo** veri kaynağından
(TraceQL, ör. `{ resource.service.name = "leave-service" }`) sorgulanır.

**KVKK — maskelenenler.** Kişisel veri iki katmanda temizlenir: servis içinde
(`apps/services/*/Observability/Telemetry.cs`, `PiiMaskingProcessor`, birim testleri
`tests/dotnet/Leave.Tests/TelemetryMaskingTests.cs`) ve collector'da
(`deploy/monitoring/otel-collector.yml`, ikinci savunma hattı):

- Span adı ve `http.route` rota şablonudur (`GET api/leave-requests/{id}`); ham yol
  (`url.path`) içindeki GUID'ler `{guid}` olur.
- Silinenler: sorgu dizesi (`url.query`), tüm istek/yanıt başlıkları (Authorization,
  Cookie, X-Device-Key, X-Follow-Up-Code, X-Internal-Token dahil hiçbiri kaydedilmez),
  istek/yanıt gövdeleri, `enduser.*` (kullanıcı kimliği hiç yazılmaz), istemci IP'si,
  user-agent, SQL parametre değerleri, veritabanı bağlantı dizesi, istisna yığın izleri.
- SQL metni yalnızca yer tutucularla (`@p0`, `$1`) tutulur; metin literalleri `'?'` olur.
- Kalan tüm değerlerde GUID, e-posta, TCKN benzeri 11 haneli sayı, IBAN, uzun sayı
  dizileri ve JWT/Bearer jetonları yer tutucuyla (`{email}`, `{tckn}`, …) değiştirilir.
- Arka plan döngülerinin (outbox taraması vb.) bağlamsız veritabanı sorguları aktarılmaz.

**Saklama.** Tempo izleri yerel `tempo-data` biriminde tutar ve `TEMPO_RETENTION`
(varsayılan `72h`) sonunda kalıcı olarak siler (KVKK: amaçla sınırlı, gerektiği kadar
saklama). Hemen silmek için `scripts/monitoring.sh purge`.

**İş metrikleri.** Servisler Prometheus'a düşük kardinaliteli iş sayaçları yayınlar
(kişi düzeyinde etiket yoktur) ve "HR360 iş metrikleri" panosu hazır gelir:

| Metrik | Etiketler |
|---|---|
| `hr360_leave_requests_total` | `action` (created/approved/rejected/cancelled), `type` |
| `hr360_expense_claims_total` | `action` (submitted/approved/rejected/paid), `category` |
| `hr360_payroll_periods_total` | `action` (calculated/closed/reopened) |
| `hr360_workflow_requests_total`, `hr360_workflow_step_decisions_total` | `action`/`decision`, `type` |
| `hr360_workflow_approval_duration_seconds` (histogram) | `outcome`, `type` |
| `hr360_overtime_requests_total`, `hr360_overtime_hours_approved_total` | `action` |
| `hr360_recruitment_stage_transitions_total` | `from`, `to` |
| `hr360_onboarding_plans_total` | `action` (created/completed/cancelled) |

Kiracı etiketi varsayılan olarak **yoktur** (paylaşılan Grafana'da şirket bazlı
bilgi sızmasın); tek kiracılı kurulumda servis ortamına `METRICS_TENANT_LABEL=true`
eklenerek `tenant` etiketi açılabilir.

## Testler

- **Birim testleri:** 13 .NET test projesinde ~730 test (xUnit), arayüzde ~37 Vitest dosyası ve ML
  servisinde ~15 pytest dosyası. Her push'ta CI'da çalışır; CI ayrıca tema kontrastını (WCAG AA),
  çeviri sözlüğünü, SQL şemasını ve veritabanı rol dosyasını denetler.
- **API entegrasyon testleri:** 36 betik (`tests/integration/`): KVKK, bordro ve Türkiye dosyaları,
  izin/vardiya, işe alım ve e-imza, performans, kimlik güvenliği, webhook ve API anahtarları, ML,
  Slack/Teams/takvim/LLM ve önbellek. Dış servisler sahte sağlayıcı sunucusuyla (`chatmock.py`)
  taklit edilir; gerçek hesap gerektirmez. Bitince servis günlüklerinde beklenmeyen hata da aranır.
- **Tarayıcı testleri (Playwright):** Beş rolle tüm ekranları, İngilizce arayüzü, erişilebilirliği ve izin
  onay akışını baştan sona dener (paralel, ~6 dk).
- Hepsi `scripts/test.sh` ile çalışır; ayrıntılar [tests/README.md](../../tests/README.md)'de.
- **Yük testi (k6):** `scripts/loadtest.sh [smoke|load|stress]`. 2 vCPU / 8 GB'lık test
  sunucusunda 100 eşzamanlı kullanıcıda p95 ≈ 46 ms ve sıfır hata.

Entegrasyon ve tarayıcı testleri (`scripts/test.sh integration | e2e`) demo şirketine test verisi
yazar ve servisleri sahte sağlayıcı katmanıyla yeniden yapılandırır; bu yüzden yalnızca test
ortamında, `HR360_TEST_ENV=1` ortam değişkeniyle çalışır. Birim testleri (`scripts/test.sh unit`)
her zaman çalıştırılabilir. **Üretim sunucusunda entegrasyon/e2e testi çalıştırmayın.**

# HR360 mimarisi

Bu belge sistemin bütününü dört düzeyde anlatır:

1. Bağlam: HR360 kimlerle ve hangi dış sistemlerle konuşur.
2. Konteynerler: hangi servisler var ve aralarında hangi yollar açık.
3. Olay akışı: Kafka'daki olayları kim yayımlar, kim tüketir.
4. Önemli akışlar: izin onayı, Slack'ten onay, işe alım.

Servis bazında uç listesi [servisler.md](servisler.md) dosyasında,
canlı API belgesi uygulamada **Yönetim → API belgeleri** ekranındadır (`/panel/api-belgeleri`).

Diyagramlar Mermaid ile yazıldı; GitHub ve çoğu Markdown görüntüleyici doğrudan çizer.

---

## 1. Sistem bağlamı (C4 düzey 1)

```mermaid
flowchart LR
    emp([Çalışan])
    mgr([Yönetici])
    hr([İK / şirket yöneticisi])
    padm([Platform yöneticisi])

    subgraph HR360["HR360 (tek sunucu, Docker Compose)"]
        sys[HR360 platformu]
    end

    emp & mgr & hr -->|tarayıcı, HTTPS| sys
    padm -->|kiracı yönetimi| sys

    sys -->|e-posta| smtp[(SMTP sunucusu<br/>şirketin ya da platformun)]
    sys <-->|onay düğmeleri, komutlar| slack[Slack]
    sys <-->|Bot Framework| teams[Microsoft Teams]
    sys <-->|OAuth2 + PKCE| gcal[Google Takvim / Meet]
    sys <-->|OAuth2 + PKCE, Graph| m365[Microsoft 365 takvim / Teams toplantısı]
    sys -->|Server-to-Server OAuth| zoom[Zoom]
    sys -->|isteğe bağlı| llm[LLM sağlayıcısı<br/>Anthropic / OpenAI uyumlu / Ollama]
    sys -->|HMAC imzalı webhook| ext[Şirketin kendi sistemleri]
    ext -->|X-Api-Key, açık API| sys
    sys -->|SSO| idp[Google / Microsoft kimlik]
```

Dış sistemlerin hepsi isteğe bağlıdır: hiçbiri tanımlanmadan HR360 tam çalışır.
Tanımlandıklarında kimlik bilgileri kiracı bazında şifreli saklanır (AES-256-GCM).

## 2. Konteynerler (C4 düzey 2)

```mermaid
flowchart TB
    browser([Tarayıcı])
    browser -->|":80 / :443"| gw

    subgraph edge[Giriş]
        gw[gateway<br/>nginx: yönlendirme, TLS,<br/>hız sınırı, güvenlik başlıkları]
    end

    gw -->|/| web[web<br/>React SPA, nginx]
    gw -->|/auth/| kc[keycloak<br/>OIDC, organizasyonlar = kiracılar]
    gw -->|"/api/&lt;servis&gt;/"| svc
    gw -->|/ml/| mli[ml-inference<br/>FastAPI]
    gw -->|/grafana/| graf[grafana]
    gw -->|/logos/| minio[(minio<br/>S3)]

    subgraph svc[".NET 9 servisleri (15)"]
        direction LR
        core[tenant · organization · employee<br/>workflow · leave · expense]
        talent[recruitment · onboarding · learning<br/>performance · compensation · timeshift]
        cross[notification · engagement · governance]
    end

    svc -->|EF Core / Npgsql| pg[(postgres<br/>hr360_operational)]
    svc -->|outbox → olay| kafka[[kafka<br/>KRaft, tek düğüm]]
    kafka -->|tüketiciler| svc
    svc -.->|önbellek| redis[(redis<br/>Valkey)]
    kc --> pg
    mli --> mlflow[mlflow] --> pg
    mlflow --> minio
    svc -->|SMTP| mail[mailpit ya da gerçek SMTP]

    subgraph obs["İzleme (profil: monitoring)"]
        prom[prometheus] --> am[alertmanager]
        loki[loki] --- promtail[promtail]
        graf
    end
    prom -.->|/metrics| svc
```

| Konteyner | Görev | Dışarı açık mı |
|---|---|---|
| gateway | Tek giriş noktası. `/api/<servis>/X` → servisin `/api/X` ucu | 80, 443 (HTTPS açıksa), 8090 (Keycloak paneli "ayrı port" modunda) |
| web | Derlenmiş React uygulaması, katı CSP | gateway üzerinden |
| keycloak | Giriş, roller, kiracı = Keycloak organizasyonu | gateway üzerinden (`/auth/`); 8080 yalnızca 127.0.0.1 |
| 15 .NET servisi | İş mantığı (aşağıda) | hayır |
| postgres | Tek veritabanı sunucusu: `hr360_operational`, `keycloak`, `hr360_mlflow` | yalnızca 127.0.0.1:5432 |
| kafka | Servisler arası olaylar | hayır |
| redis (Valkey) | Kısa ömürlü önbellek, hız sınırı sayacı | hayır |
| minio | Logolar, ML artefaktları | konsol yalnızca 127.0.0.1:9001 |
| ml-inference, mlflow | Ayrılma riski modeli, AI araçları (CV ayrıştırma, ilan dili denetimi), model kaydı | ml-inference gateway üzerinden; mlflow yalnızca 127.0.0.1 |
| prometheus, alertmanager, grafana, loki, promtail, exporter'lar | İzleme, alarm, log | grafana gateway üzerinden |
| certbot | Let's Encrypt yenileme (profil: letsencrypt) | hayır |

### Servisler

| Servis | Sahip olduğu veri (tablo öneki) | Notlar |
|---|---|---|
| tenant | `platform_tenants` | Kiracı kaydı, Keycloak organizasyon/kullanıcı yönetimi, marka, kiracının SMTP'si |
| organization | `organization_*` | Şirket, departman, ekip |
| employee | `employee_*` | Çalışan ana verisi, atamalar |
| workflow | `workflow_*` | Çok adımlı onay, vekâlet, SLA |
| leave | `leave_*` | Bakiye, talep, resmi tatil |
| expense | `expense_*` | Masraf, özlük belgesi, İK vakası |
| recruitment | `recruitment_*` | İlan, aday, başvuru, mülakat |
| onboarding | `onboarding_*` | İşe başlangıç planı, zimmet |
| learning | `learning_*` | Eğitim, kayıt, sertifika |
| performance | `performance_*` | Dönem, hedef, değerlendirme, analiz |
| compensation | `compensation_*` | Ücret geçmişi, bant |
| timeshift | `timeshift_*` | Vardiya, puantaj |
| notification | `notification_*` | Bildirim kuyruğu, e-posta gönderimi |
| engagement | `engagement_*` | Takdir, anket, 1:1, mentorluk, masa/ofis, ayrılış, yedekleme |
| governance | `governance_*` | Denetim, olay radarı, kural motoru, webhook, açık API, Slack/Teams, takvim/toplantı, AI, KVKK, analitik |

Tüm servisler aynı PostgreSQL veritabanını kullanır; her servis yalnızca kendi
öneki olan tablolara yazar. İki istisna bilinçlidir:

- **engagement** ve **governance** raporlama ve çapraz modül ekranları için diğer
  modüllerin tablolarını salt okunur sorgular (ör. ekip sağlığı izin, mesai, performans
  ve 1:1 verisini tek sorguda birleştirir). Yazma her zaman sahibi servisin API'siyle
  yapılır. İstisnalar: uygulama içi bildirim satırı (`notification_messages`) ve
  KVKK saklama politikalarının anonimleştirme/silme işleri.
- Ortak tablolar: `messaging_outbox`, `messaging_processed_events`, `audit_log` ve
  analitik görünümler (`analytics_*`).

### Çok kiracılılık

```mermaid
flowchart LR
    tok["JWT (Keycloak)<br/>organization: { acme: {} }<br/>realm_access.roles: [...]"] --> mw[TenantMiddleware]
    mw --> tc[TenantContext.TenantSlug = acme]
    tc --> ef[EF Core genel sorgu filtresi<br/>WHERE TenantSlug = 'acme']
    tc --> sql[Ham SQL: her sorguda $1 = kiracı]
    mw --> gate[TenantStatusGate<br/>askıdaki kiracı → 403]
```

- Kiracı yalnızca jetondaki `organization` talebinden okunur; istek gövdesi ya da
  başlıkla değiştirilemez.
- Platform yöneticisi filtreyi atlar; kiracı değiştirirken arayüz Keycloak'tan
  `organization:<kiracı>` kapsamıyla yeni jeton alır.
- Önbellek anahtarları da kiracıyı içerir (`hr360:<önbellek>:<kiracı>:...`).

### Yetki

Keycloak realm rolleri: `employee`, `manager`, `accounting`, `hr-admin`,
`tenant-admin`, `platform-admin`. Ek izinler `ext-<izin>` rolleriyle verilir
(ör. `ext-leave-manageBalance`, `ext-recruitment-publish`). Kiracı yöneticisi bunları
Ekip ekranından atar. Servisler politikaları (`RequireHrAdmin`,
`RequireManagerOrAbove` vb.) kendileri uygular. Arayüzdeki izin denetimi yalnızca
neyin gösterileceğini belirler.

## 3. Olay akışı (Kafka)

Yayımlayan servis olayı iş kaydıyla **aynı veritabanı işleminde** `messaging_outbox`
tablosuna yazar (Outbox). Arka plan yayımcısı her 5 sn'de yayımlanmamış satırları
`FOR UPDATE SKIP LOCKED` ile alıp Kafka'ya gönderir:

- `acks=all`, idempotent üretici.
- Başlıklar: `event-type`, `event-id`.

Tablo ortaktır: hangi servisin yayımcısı alırsa alsın satır kendi konusuna gider.
Kilitleme sayesinde bir satır iki kez gönderilmez.

Tüketen servis olay kimliğini iş değişikliğiyle aynı işlemde
`messaging_processed_events` tablosuna yazar (Inbox). Aynı olay ikinci kez gelirse
atlanır. Hata durumunda en çok 5 kez artan beklemeyle yeniden denenir.

```mermaid
flowchart LR
    subgraph prod[Yayımlayanlar]
        E[employee-service]
        W[workflow-service]
        L[leave-service]
    end
    E -->|employee.hired<br/>employee.assigned<br/>employee.status-changed| TE[[hr360.employee.events]]
    W -->|workflow.submitted<br/>workflow.approved<br/>workflow.rejected| TW[[hr360.workflow.events]]
    L -->|leave.approved<br/>leave.rejected| TL[[hr360.leave.events]]

    TW --> Lc[leave-service<br/>izin talebini kapatır, bakiyeyi düşer,<br/>leave.* yayımlar]
    TW --> Xc[expense-service<br/>masrafı onaylar/reddeder]
    TE --> Nc[notification-service<br/>hoş geldin, yeni pozisyon,<br/>onaycıya e-posta, sonuç bildirimi]
    TW --> Nc
    TL --> Tc[timeshift-service<br/>izin günlerini vardiyaya işler]
    TE & TW & TL --> Gc[governance-service<br/>olay radarı, kural motoru, webhook,<br/>Slack/Teams, takvime işleme]
```

### Olay kataloğu

| Konu | Olay | Ne zaman | Yük (alanlar) |
|---|---|---|---|
| `hr360.employee.events` | `employee.hired` | Çalışan oluşturulunca | TenantSlug, EmployeeId, FirstName, LastName, Email, HireDate |
| | `employee.assigned` | Yeni atama (departman/pozisyon) | TenantSlug, EmployeeId, AssignmentId, DepartmentId, PositionTitle, EffectiveFrom, Email, FirstName, LastName |
| | `employee.status-changed` | Durum değişince (ör. ayrılış) | TenantSlug, EmployeeId, FirstName, LastName, OldStatus, NewStatus, EffectiveDate |
| `hr360.workflow.events` | `workflow.submitted` | Talep oluşunca ve her yeni onay adımında | TenantSlug, WorkflowRequestId, WorkflowType, RequesterEmployeeId, RequesterName, Subject, ApproverEmployeeId, ApproverEmail, ApproverFirstName, SlaDueAt |
| | `workflow.approved` / `workflow.rejected` | Akış sonuçlanınca | TenantSlug, WorkflowRequestId, WorkflowType, RequesterEmployeeId, Subject, Approved, DecidedByEmployeeId, Comment |
| `hr360.leave.events` | `leave.approved` / `leave.rejected` | İzin onay akışıyla ya da elle sonuçlanınca | TenantSlug, LeaveRequestId, EmployeeId, StartDate, EndDate, Approved, Type, Days |

Tüm yüklerde `OccurredAt` de vardır. `WorkflowType`: `LeaveRequest`, `ExpenseClaim`,
`PositionChange`, `AssetRequest`, `Other`. Alan adları PascalCase'tir.

| Tüketici (grup) | Konular | Yaptığı |
|---|---|---|
| leave-service | workflow | `LeaveRequest` türündeki kararlar: talebi kapatır, bakiyeyi (bekleyen → kullanılan) günceller, `leave.*` yayımlar |
| expense-service | workflow | `ExpenseClaim` türündeki kararlar: masrafı onaylar ya da reddeder |
| timeshift-service | leave | `leave.approved`: her gün için sistem yönetimli izin kaydı |
| notification-service | employee, workflow | Hoş geldin ve pozisyon e-postaları, onaycıya e-posta, talep sahibine uygulama içi sonuç |
| governance-service | `^hr360\..*` (hepsi) | `governance_events`'e yazar (30 gün), canlı olay radarına (SSE) iter, kural motoru, webhook'lar, Slack/Teams kanal bildirimleri, onaycıya Slack/Teams'te düğmeli mesaj, `leave.approved` için bağlı takvime "izinli" etkinliği |

Konular kurulumda `kafka-init` konteyneriyle oluşturulur. Yeni bir konu eklenirse
`docker-compose.yml`'deki bu listeye de eklenmelidir; böylece tüketiciler ilk olaydan
önce de konuya abone olabilir.

## 4. Önemli akışlar

### İzin talebi ve onayı

```mermaid
sequenceDiagram
    autonumber
    actor A as Çalışan
    actor M as Yönetici
    participant LV as leave-service
    participant EM as employee / organization
    participant WF as workflow-service
    participant K as Kafka
    participant NT as notification-service
    participant TS as timeshift-service
    participant GV as governance-service

    A->>LV: POST /api/leave/leave-requests
    LV->>EM: çalışan ve departman başı (onaycı)
    LV->>WF: POST /api/workflows (LeaveRequest)
    WF-->>K: workflow.submitted (outbox)
    K-->>NT: onaycıya e-posta
    K-->>GV: radar + Slack/Teams'te onay düğmesi
    M->>WF: Onayla (web ya da Slack/Teams)
    WF-->>K: workflow.approved
    K-->>LV: talebi kapat, bakiyeyi düş
    LV-->>K: leave.approved (Type, Days)
    K-->>NT: talep sahibine bildirim
    K-->>TS: izin günlerini vardiyaya işle
    K-->>GV: kurallar, webhook, takvime "izinli" etkinliği
```

### Slack ya da Teams'ten onay

```mermaid
sequenceDiagram
    actor M as Yönetici (Slack/Teams)
    participant P as Slack / Bot Framework
    participant GV as governance-service
    participant WF as workflow-service

    M->>P: "Onayla" düğmesi
    P->>GV: POST /api/chat/slack/{app}/interactions<br/>(Slack imzası) ya da /teams/{app}/messages (JWT)
    GV->>GV: imza/JWT doğrula, kullanıcıyı çalışana eşle (e-posta)
    GV->>WF: POST /api/internal/workflows/{id}/steps/{step}/decide<br/>X-Internal-Token
    WF->>WF: web ile aynı kurallar: onaycı ya da vekil mi, kendi talebi mi
    WF-->>GV: sonuç
    GV->>P: mesajı güncelle (karar, kim, ne zaman)
```

İç uç gateway'den erişilemez (`/api/*/internal/` → 404). Anahtar yalnızca workflow ve
governance servislerindedir.

### İşe alım: tekliften çalışana

governance-service, kabul edilen teklif için önce employee-service'te çalışanı
(`POST /api/employees`), sonra onboarding-service'te işe başlangıç planını
(`POST /api/onboarding-plans`) oluşturur. Çalışanın oluşması `employee.hired` olayını
tetikler; notification-service hoş geldin e-postasını gönderir.

## 5. Eşzamanlı servis çağrıları

| Çağıran → çağrılan | Amaç |
|---|---|
| leave, expense → employee, organization, workflow | Onaycıyı bul, onay akışını başlat/iptal et |
| workflow, timeshift, performance, learning, onboarding, notification, tenant → employee | Çalışan bilgisi (`/employees/me`, `/{id}`) |
| employee → organization | Departmandan ayrılan departman başını temizle |
| tenant → Keycloak yönetim API'si | Organizasyon, kullanıcı, rol, kimlik sağlayıcı |
| notification → tenant | Kiracının markası ve SMTP ayarı (iç uç) |
| engagement → employee | Ayrılış tamamlanınca çalışanı "Terminated" yap |
| governance → employee, onboarding | İşe alım sagası |
| governance → workflow (iç uç) | Slack/Teams kararları |
| governance, performance → ml-inference | İlan dili denetimi; performans anomali/eğilim analizi |

Çağrıların çoğu kullanıcının jetonunu aynen iletir; yetki her serviste yeniden
denetlenir. İstisnalar:

- workflow iç ucu `X-Internal-Token` kullanır.
- ml-inference performans uçları jeton istemez; yalnızca iç ağdan erişilebilir.
- tenant'ın SMTP iç ucu gateway'den erişilemez.

## 6. Kesişen konular

| Konu | Nasıl | Ayrıntı |
|---|---|---|
| Gözlemlenebilirlik | Her servis `/metrics` (prometheus-net), `X-Correlation-Id` gateway'den servislere, loglar Loki'de | [İşletim → İzleme](../isletim/README.md#izleme) |
| Alarm | Prometheus kuralları → Alertmanager → e-posta, Slack, Teams | `deploy/monitoring/` |
| Önbellek | Valkey; dizin, ofis doluluğu, ekip sağlığı, analitik; Redis yoksa doğrudan veritabanı | [Bileşenler](#8-bileşenler) |
| Güvenlik | Hız sınırı, CSP, root olmayan konteynerler, şifreli kiracı sırları, taramalar | [güvenlik](../guvenlik/README.md) |
| Yedek | `pg_dump` (3 veritabanı) + MinIO, tek arşiv | `scripts/backup.sh`, `scripts/restore.sh` |
| Test | Birim (.NET, Vitest, pytest), API entegrasyon (sahte sağlayıcılar), Playwright (4 rol × tüm ekranlar), k6 | [tests/README.md](../../tests/README.md) |

## 7. Bilinen sınırlar

- **Tek düğüm:** Kafka (1 bölüm, çoğaltma 1), PostgreSQL ve Keycloak tek kopyadır.
  2 vCPU'da yaklaşık 400 eşzamanlı kullanıcı test edildi. Daha fazlası için servisleri
  çoğaltmak ve gateway'de yük dengelemek gerekir. Önbellek ve açık API hız sayacı
  zaten paylaşımlıdır; gateway'in hız sınırı ise örnek başınadır.
- **Ortak veritabanı:** Servisler fiziksel olarak ayrı veritabanında değil. Raporlama
  için okuma kolaylığı sağlar, ama şema değişikliklerinde engagement ve governance'ın
  okuduğu tablolar da hesaba katılmalıdır.
- **Olay sözleşmesi:** Yükler C# kayıtlarından üretilir, şema kaydı yoktur. Alan
  ekleme geriye uyumludur; alan silme ya da yeniden adlandırma tüm tüketicileri etkiler.
- **Tek sunucu:** Sistem başlangıçta çok sunuculu (uygulama sunucuları arasında yedeklilik,
  PostgreSQL yedek sunucusu, yük dengeleyici) bir dağıtım için tasarlandı; bu sürüm aynı
  servislerin tamamını Docker Compose ile tek makinede çalıştırır ve bu altyapı katmanlarını içermez.

## 8. Bileşenler

- **Kimlik doğrulama:** Keycloak (Organizations özelliği ile çok
  kiracılılık — her kiracı kendi Keycloak organizasyonuna karşılık gelir)
- **Mesajlaşma:** Apache Kafka (KRaft, tek node), Outbox (yayınlayan
  tarafta) + Inbox (`messaging_processed_events`, tüketen tarafta
  idempotency) desenleriyle
- **Veritabanı:** PostgreSQL — tüm servisler `hr360_operational`
  veritabanını, kendi tablo kümeleriyle ve ortak tenant filtreleme
  altyapısıyla izole şekilde paylaşır; Keycloak (`keycloak`) ve MLflow
  (`hr360_mlflow`) için aynı PostgreSQL'de ayrı veritabanları
- **Depolama:** MinIO (S3 uyumlu nesne depolama — logo ve ML artefact'ları)
- **Önbellek:** Valkey (Redis uyumlu). Çalışan dizini, ofis doluluğu, ekip sağlığı
  ve analitik özet 30 sn–2 dk tutulur; açık API'nin dakikalık sınır sayacı da buradadır.
  Kalıcı veri tutmaz; erişilemezse servisler doğrudan veritabanından okur
- **E-posta:** `install.sh` kurulumda gönderen adresi sorar, SMTP sunucusunu
  adresten bulur ve deneme e-postasıyla doğrular; boş
  bırakılırsa Mailpit (yerel SMTP yakalayıcı, demo/dev için — e-postalar
  gerçekten gönderilmez) kullanılır. Girilen ayar hem bildirim servisine
  (`.env` → `SMTP_*`) hem Keycloak'a (davet/şifre sıfırlama e-postaları,
  realm'in "Email" ayarı) yazılır. Kurulumda boş bırakıldıysa ya da sonradan
  değişecekse `scripts/smtp.sh set` ikisini birlikte günceller.
- **ML:** MLflow tracking + Registry, FastAPI tabanlı inference servisi
  (işten ayrılma riski tahmini), Postgres backend store + MinIO artifact
  store. Aynı serviste `/ml/ai/*` altında AI araçları (bkz.
  [AI araçları hakkında](../kullanim/README.md#ai-araçları-hakkında))
- **Denetim kaydı:** Bütün servislerde EF Core `SaveChanges` kesicisi her
  ekleme/güncelleme/silmeyi `audit_log` tablosuna yazar (kim, ne zaman, hangi
  alan eski → yeni). IBAN, TCKN, maaş gibi hassas alanlar `***` olarak
  maskelenir; anket yanıtları anonimlik için hiç kaydedilmez. Gateway her isteğe
  `X-Correlation-Id` verir, aynı istekte yapılan değişiklikler bu kimlikle
  gruplanır
- **Olaylar:** governance-service tüm `hr360.*` Kafka konularını dinler; olay
  radarı (SSE ile canlı akış), kural motoru, webhook'lar ve Slack/Teams
  bildirimleri bu akıştan beslenir
- **Paket yok:** Her kiracı bütün modülleri ve özellikleri (beyaz etiket,
  özel alan adı, açık API dahil) sınırsız kullanır; plan, çalışan kotası ve
  faturalandırma yoktur
- **Gateway:** Nginx — path tabanlı routing (`/api/<servis>/`), `/auth/`
  üzerinden Keycloak'a, `/ml/` üzerinden inference servisine, `/logos/`
  üzerinden MinIO'ya passthrough, `/grafana/` üzerinden Grafana'ya (izleme açıksa). HTTPS (Let's Encrypt dahil) ve Keycloak
  yönetim paneli erişim kısıtı da burada yapılır. Her servisin tek kopyası
  çalışır; yük dengeleme yoktur.

Servisler birbirini Docker'ın dahili servis-adı DNS'i üzerinden bulur
(örn. `http://employee-service:8080`). Adresler `docker-compose.yml` içindeki ortam
değişkenlerinden gelir; koddaki varsayılanlar da aynı servis adlarıdır.

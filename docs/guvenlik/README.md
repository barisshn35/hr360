# HR360 güvenlik notları

Bu belge sistemde hangi korumaların nerede olduğunu, düzenli taramaların nasıl
çalıştırıldığını ve bilinen, kabul edilmiş riskleri anlatır. Kurulum sırasında
yapılması gerekenler en sondaki kontrol listesindedir.

## Katmanlar

| Katman | Koruma | Nerede |
|---|---|---|
| Kimlik | Keycloak (OIDC, PKCE), isteğe bağlı TOTP zorunluluğu, kaba kuvvet koruması, Google/Microsoft SSO | `deploy/keycloak/realm-export.template.json` |
| Yetki | Rol ve izin denetimi her serviste (`[Authorize]` + izin politikaları); arayüz yalnızca görünürlük için aynı kuralları kullanır | `apps/services/*/Program.cs` |
| Kiracı yalıtımı | EF Core genel sorgu filtresi (`ITenantOwned`), ham SQL'de `tenant_id` koşulu | servislerin `DbContext`'leri |
| Veritabanı | İsteğe bağlı servis başına roller (`hr360_<servis>`), `audit_log`'da UPDATE/DELETE yok (yalnızca `hr360_retention` siler); isteğe bağlı RLS politikaları | `deploy/postgres/roles.sql`, `scripts/db-roles.sh`, `scripts/sql/2026-10-20_rls.sql` |
| Ağ | Dışarıya yalnızca gateway açık; veritabanı, MinIO, Keycloak (8080), MLflow yalnızca `127.0.0.1`; Redis (Valkey) hiç yayımlanmaz ve parola ister (`REDIS_PASSWORD`) | `docker-compose.yml` |
| Gateway | Hız sınırı, güvenlik başlıkları, iç uçların kapatılması | `deploy/nginx/nginx.conf` |
| Arayüz | Katı içerik güvenliği politikası (CSP), satır içi betik yok | `apps/web/docker/security-headers.conf` |
| Giriş ekranı | Keycloak CSP'si sıkılaştırıldı | `scripts/keycloak-theme.sh` |
| Sırlar | `.env` dışında repo'da sır yok; isteğe bağlı `secrets/*.txt` + Docker secrets (`X_FILE`); kiracı sırları ve TCKN/IBAN AES-256-GCM ile şifreli, anahtar halkasıyla yenilenebilir (`enc2:`) | `Security/KeyRing.cs`, `Security/SecretEnv.cs`, `scripts/crypto-keys.sh`, `scripts/secrets-migrate.sh` |
| Servisler arası | İç uçlar `X-Internal-Token` ister ve gateway'den erişilemez (404) | workflow-service `api/internal/*` |
| Dış entegrasyonlar | Webhook'lar HMAC imzalı; Slack istekleri imzalı (5 dk pencere), Teams istekleri Bot Framework JWT'si ile doğrulanır; takvim bağlantısı OAuth2 + PKCE | governance-service |
| Yapay zekâ | Kiracı açmadıkça modele istek gitmez; kişisel veri ayrı izne bağlı, gönderilmeden önce takma ad kullanılır; kullanım kaydında içerik tutulmaz | `Infrastructure/Ai/LlmClient.cs` |
| Konteynerler | Hiçbir uygulama konteyneri root çalışmaz | Dockerfile'lar |
| Denetim | Önemli işlemler denetim kaydına yazılır | governance `audit` |

### Gateway hız sınırı

| Bölge | Sınır | Uygulandığı yer |
|---|---|---|
| `hr360_api` | IP başına 50 istek/sn, 200 isteklik tampon | tüm sunucu |
| `hr360_login` | IP başına 10 istek/sn, 30 isteklik tampon | `/auth/` (Keycloak) |

Sınır aşılınca `429` döner. Özel ağ adresleri (`10/8`, `172.16/12`, `192.168/16`,
`127/8`) muaftır: servisler arası trafik ve ters vekil arkasındaki kurulumlar
etkilenmez. Gateway başka bir vekilin arkasındaysa gerçek istemci adresinin
görülmesi için `real_ip` ayarı eklenmelidir; aksi halde tüm istekler vekilin özel
adresinden geliyor görünür ve sınır uygulanmaz.

Sayaçlar her gateway örneğinin belleğindedir. Birden çok gateway çalıştırılırsa
sınır örnek başına uygulanır.

### Güvenlik başlıkları

Gateway her yanıta `X-Content-Type-Options: nosniff`, `Referrer-Policy:
strict-origin-when-cross-origin`, `X-Frame-Options: SAMEORIGIN`,
`Permissions-Policy` (kamera, mikrofon, konum, ödeme, USB kapalı) ve
`Cross-Origin-Resource-Policy: same-origin` ekler. Sürüm bilgisi (`server_tokens`)
gizlidir. HTTPS, güvenilir bir sertifikayla açıldığında (`scripts/tls.sh enable
--cert/--letsencrypt`) `Strict-Transport-Security: max-age=15552000` eklenir.
Kendinden imzalı sertifikada HSTS bilerek eklenmez: tarayıcı o durumda uyarıyı
geçmeye izin vermez ve site aylarca açılamaz.

Arayüzün CSP'si:

```
default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline';
img-src 'self' data: blob:; font-src 'self' data:; connect-src 'self';
frame-src 'self'; worker-src 'self'; manifest-src 'self'; object-src 'none';
base-uri 'self'; form-action 'self'; frame-ancestors 'self'
```

Satır içi betik yoktur: tema ön yüklemesi (`public/theme-init.js`) ve Keycloak
sessiz oturum denetimi (`public/silent-check-sso.js`) ayrı dosyadadır. Yeni bir
`<script>` bloğu eklemek CSP'yi bozar; e2e testleri konsoldaki CSP ihlalini hata
sayar.

## Taramalar

```bash
scripts/security-scan.sh repo     # Trivy: bağımlılıklar (npm, pip, NuGet), sızmış sırlar, Dockerfile/compose hataları
scripts/security-scan.sh deps     # npm audit + dotnet list package --vulnerable
scripts/security-scan.sh images   # çalışan kurulumun imajları (işletim sistemi paketleri dahil)
scripts/security-scan.sh          # hepsi
```

Yalnızca HIGH ve CRITICAL bulgular raporlanır. Bulgu varsa çıkış kodu 1'dir.
Trivy sürümü özet değeriyle (`sha256`) sabitlenmiştir.

CI'da (`.github/workflows/ci.yml`) her push'ta `security` işi depo taramasını,
`npm audit`'i ve NuGet denetimini çalıştırır. `main`'e push'ta derlenen imajlar
düzeltmesi yayımlanmış CRITICAL açıklar için ayrıca taranır.

Dinamik tarama OWASP ZAP'in temel taramasıyla yapılır (pasif kurallar + örümcek; kurallar
`.zap/rules.tsv`). CI her hafta arayüz + gateway'i tarar (`.github/workflows/zap.yml`); çalışan
kurulum için:

```bash
scripts/security-scan.sh zap                 # PUBLIC_ORIGIN ya da http://gateway/
scripts/security-scan.sh zap https://<adres> # belirli adres
```

Ayrıntı: “Altyapı ve veri koruma › Haftalık ZAP taraması”.

## Ekim 2026 taraması ve yapılan düzeltmeler

| Bulgu | Önem | Düzeltme |
|---|---|---|
| `xlsx` 0.18.5 (SheetJS): dosya okurken prototip kirletme ve ReDoS (CVE-2023-30533, CVE-2024-22363). Çalışan ve masraf içe aktarımı kullanıcı dosyası okuyordu. | HIGH | npm'deki son sürüm düzeltmeyi içermiyor. `read-excel-file` ve `write-excel-file`'a geçildi (`src/lib/spreadsheet.ts`). Eski ikili `.xls` biçimi artık okunmaz; kullanıcıya `.xlsx` olarak kaydetmesi söylenir. CSV okuma eklendi. |
| MLflow 2.17.2: 8 CRITICAL, 15 HIGH açık (yol geçişi, uzaktan kod çalıştırma vb.) | CRITICAL | İstemci ve sunucu 3.16.1'e yükseltildi. Sunucu açılışta şemayı günceller (`mlflow db upgrade`), Host başlığı denetimi açık, arka plan iş yürütücüsü kapalı. |
| `pypdf` 5.1.0 (CV ayrıştırma): 10 açık | HIGH | 6.19.0 |
| `python-multipart` 0.0.17, `starlette` 0.41.3 | HIGH | 0.0.32; FastAPI 0.142.2 + Starlette 1.7.0 (başlangıç kancası `lifespan`'e taşındı) |
| Taban imajlarda OpenSSL, pcre2, util-linux, gzip paketleri | HIGH | Dockerfile'larda `apt-get upgrade` / `apk upgrade` ve `pip`/`setuptools`/`wheel` güncellemesi |
| 18 Dockerfile root kullanıcıyla çalışıyordu (DS002) | HIGH | .NET servisleri `$APP_UID` (1654), arayüz `nginx-unprivileged` (101), ML imajları UID 10001 |
| MLflow veritabanı parolası süreç listesinde (`ps`) görünüyordu | Orta | Bağlantı dizesi ortam değişkenine taşındı |
| Arayüzde satır içi betikler; CSP yoktu | Orta | Betikler dosyaya taşındı, katı CSP |
| Keycloak giriş ekranında yalnızca çerçeve kısıtı vardı | Orta | `default-src 'self'` tabanlı CSP |
| Gateway'de hız sınırı yoktu | Orta | `hr360_api` ve `hr360_login` bölgeleri |
| Çalışan içe aktarımında "İşe giriş tarihi" başlığı tanınmıyordu (JavaScript'te `"İ".toLowerCase()` birleşik nokta üretir) | İşlev hatası | Başlık eşleştirme Türkçe harflere göre düzeltildi |

Taramanın son durumu: Trivy (depo ve imajlar) HIGH/CRITICAL bulgu yok, `npm audit`
0 açık, 15 .NET servisinde bilinen açıklı NuGet paketi yok. ZAP temel taraması:
0 FAIL. Kalan uyarılar aşağıda.

## Dosya yükleme: gerçek tür denetimi

Sunucuya dosya içeriği ulaşan her uçta tür, istemcinin bildirdiği Content-Type'a değil
dosyanın ilk baytlarına (magic bytes) göre belirlenir; bildirilen tür ya da uzantı içerikle
uyuşmazsa 400 döner ("Dosyanın içeriği, bildirilen dosya türü ya da uzantısıyla uyuşmuyor").
.NET tarafında her servisin kendi `FileSniffer` kopyası vardır (learning, tenant, expense,
governance); ML servisinde `ocr.py` / `ai_tools.py` aynı denetimi yapar.

| Uç | Gelen içerik | İzinli | Boyut |
|---|---|---|---|
| `POST /api/learning/scorm/packages` | multipart, `.zip` | zip (PK imzası) | 50 MB (açılmış 250 MB, 5000 dosya) |
| `POST /api/tenant/my-tenant/logo` | multipart | PNG, JPEG, WebP (SVG yok) | 2 MB |
| `POST /api/expense/expense-claims/ocr` | multipart | PNG, JPEG, WebP | 5 MB, saklanmaz |
| Sohbet botu fiş fotoğrafı (Slack/Teams dosya adresi) | sağlayıcıdan indirme | PNG, JPEG, WebP | 5 MB, saklanmaz |
| `POST /ml/ocr/receipt` | multipart | PNG, JPEG, WebP | 5 MB, saklanmaz |
| `POST /ml/ai/cv/parse` | multipart | PDF, DOCX (zip), UTF-8 metin | 5 MB, saklanmaz |

Belge/fiş/aday özgeçmişi kayıtlarındaki `StorageKey` alanları yalnızca metadata'dır; sunucuya
dosya yükleyen (ön imzalı MinIO dahil) bir uç yoktur. Böyle bir uç eklenirse doğrulama
sonlandırma (confirm) adımında depodan ilk baytlar okunarak yapılmalıdır.

Virüs taraması (ör. ClamAV `clamd` INSTREAM) eklenmedi: geliştirme VM'inin belleği sınırlı.
Gerekirse kancanın yeri, çağrı yerinde `FileSniffer.Check` sonrasıdır ve dosya saklanmadan önce.

## İşletim güvenliği: denetim zinciri, Keycloak yönetimi, konteynerler, parolalar

- **Denetim kaydı gecelik çapası:** governance `AuditChainGuard` her 24 saatte bir (servis açılışından
  2 dk sonra da) tüm kiracıların zincirini doğrular ve zincir başını (son sıra no + özet)
  `governance_audit_anchors` tablosuna ve servis günlüğüne ("Denetim zinciri çapası", Loki/SIEM'de
  veritabanı dışı kopya) yazar. Sonraki turda önceki çapanın satırı aynı özetle yerinde mi ve zincir
  geriye gitmiş mi diye bakılır; böylece zincirin baştan yeniden hesaplanması ya da sondan satır
  silinmesi de görünür. Sorun olursa `hr360_audit_chain_problems > 0` → `DenetimZinciriBozuk` alarmı.
  Son sonuç Gizlilik › denetim zinciri panelinde.
- **Keycloak yönetim girişi (master realm):** giriş ve yönetim olayları 90 gün saklanır
  (`scripts/keycloak-admin-access.sh` her çalıştığında uygular). Kaba kuvvet koruması ilk başta
  açılamadı (tenant-service aynı yönetici hesabıyla sık parola girişi yapıyor, koruma hesabı
  kilitliyordu); tenant-service servis hesabına geçince koruma açıldı (“Kimlik güvenliği”, aşağıda).
- **Konteyner sertleştirme:** .NET servisleri ve arayüz salt okunur kök dosya sistemiyle (yalnızca
  bellek içi `/tmp` yazılabilir), tüm Linux yetkileri düşürülmüş ve `no-new-privileges` ile çalışır;
  ML servisi ve gateway'de `no-new-privileges` (ML kök dosya sistemi model indirmesi için yazılabilir).
- **İç servis anahtarı değişimi:** servisler `INTERNAL_SERVICE_TOKEN` yanında geçiş süresince
  `INTERNAL_SERVICE_TOKEN_PREVIOUS`'u da kabul eder. `scripts/rotate-internal-token.sh auto`
  (ya da `start` … `finish`) çağrı reddi olmadan anahtarı değiştirir. `SIEM_PSEUDONYM_KEY`
  tanımlı değilse SIEM takma adları da değişir; tanımlamanız önerilir.
- **Parola yenileme:** `python3 scripts/rotate-passwords.py [--keycloak-admin] [kullanıcı...]`
  `platform.admin`'in (varsa `demo.admin`'in de) ve isteğe bağlı Keycloak master yöneticisinin
  parolasını yeniler. Parolalar açığa çıktığında (sohbet, ekran paylaşımı, e-posta) kullanılır.
  Yeni parolalar ekrana basılmaz; yalnızca `.env`'e (`PLATFORM_ADMIN_PASSWORD`,
  `DEMO_ADMIN_PASSWORD`, `KEYCLOAK_ADMIN_PASSWORD`) yazılır. `tests/credentials.json` yalnızca
  test ortamında, dosya varsa güncellenir.
- **Tedarik zinciri:** CI 17 imajın tamamını Trivy ile tarar ve her imaj için CycloneDX SBOM'u
  (90 gün) saklar; `.github/dependabot.yml` haftalık toplu bağımlılık PR'ları açar. İmaj imzalama
  imajlar bir kayıt deposuna gönderilmediği için yapılmadı.
- **Kurulum:** `install.sh` güncelleme yolunda da `--domain/--tls` ve `--keycloak-admin` uygulanır.

## Kimlik güvenliği: servis hesabı, parola politikası, iki adımlı doğrulama

- **tenant-service Keycloak servis hesabı:** yönetim API'sine artık hr360 realm'indeki gizli
  `hr360-tenant-admin` istemcisiyle (client credentials) girilir; master yönetici parolası
  tenant-service'e verilmez. Servis hesabının realm-management rolleri yalnızca gerekenler
  (canlı Keycloak 25'te tek tek denendi): `manage-users` (kullanıcı, rol atama, oturum, davet),
  `manage-realm` (organizasyonlar — KC 25 organizasyon API'si bunu ister —, realm rolleri, giriş
  akışı, gerekli eylemler), `manage-identity-providers` (kurumsal SSO), `manage-clients` (özel alan
  adının hr360-web'e eklenmesi), `view-events` (şüpheli giriş). Master realm'e yetkisi yoktur.
  Jeton süreç genelinde tek (eşzamanlı istekler ayrı giriş yapmaz). Kurulum/güncelleme:
  `scripts/keycloak-service-account.sh` (idempotent; anahtarı `.env`'e
  `KEYCLOAK_TENANT_ADMIN_CLIENT_SECRET` olarak üretir, istemciyi oluşturur/eşitler, jetonla dener,
  başarılıysa `KEYCLOAK_TENANT_ADMIN_LEGACY_*` değişkenlerini boşaltıp tenant-service'i yeniler).
  Anahtar yoksa ya da Keycloak reddederse tenant-service eski yola (master parola) düşer ve uyarı yazar.
- **Master realm kaba kuvvet koruması:** `scripts/keycloak-admin-access.sh` servis hesabı anahtarı
  `.env`'de varsa ve istemci Keycloak'ta tanımlıysa korumayı açar (10 hatalı denemede artan
  bekleme, en fazla 15 dk, kalıcı kilit yok); yoksa kapalı bırakır ve uyarır.
- **Parola politikası (hr360 realm):** en az 12 karakter, kullanıcı adı ve e-posta olamaz, son 3
  parola tekrar kullanılamaz. Mevcut parolalar geçerli kalır; politika parola değişiminde uygulanır.
  Test/demo parolaları (seed_demo.py ve rotate-passwords.py `Hr360-…` 22+ karakter, install.sh
  üretimi 24 karakter) uyumludur. Keycloak realm'i içeri alırken (ilk kurulum) şablondaki
  parolaları da politikaya göre denetler ve uymazsa HİÇ AÇILMAZ; bu yüzden `install.sh` elle girilen
  demo.admin/platform.admin parolalarında en az 12 karakter ister. SCIM/LDAP/kayıt kullanıcıları
  parolasız oluşturulur (parolayı kendileri belirler ya da LDAP doğrular).
- **İki adımlı doğrulama politikası:** Güvenlik ekranında şirket başına “Zorunlu değil / İK,
  yönetici ve platform rollerinde zorunlu / Herkes için zorunlu”. Zorunlu olup ne TOTP ne passkey'i
  olan kullanıcıya Keycloak'ta `CONFIGURE_TOTP` gerekli eylemi eklenir; tenant-service kuralı
  10 dakikada bir yeniden uygular (sonradan role atananlar, doğrulayıcısını silenler). Yetkili
  roller: `tenant-admin`, `hr-admin`, `manager`. Platform yöneticisi hesapları kiracıya bağlı
  olmadığından ayrı anahtar: `MFA_REQUIRE_PLATFORM_ADMIN=true` (varsayılan kapalı: entegrasyon
  testleri platform.admin ile tarayıcıdan parola girişi yapar). Demo şirketinde politikayı açmak
  test kullanıcılarının girişini (doğrulayıcı kurulumu) gerektirir; testler bu yüzden açmaz.
- **Şüpheli giriş tespiti:** hr360 realm'inde yalnızca `LOGIN` ve `LOGIN_ERROR` olayları 30 gün
  saklanır. tenant-service bunları dakikada bir okur: kullanıcının kayıtlı ağları dışından (IPv4 /24,
  IPv6 /48) başarılı girişte kullanıcıya “Yeni bir ağdan giriş” bildirimi; 10 dakikada 5+ hatalı
  girişte (aynı kullanıcı ya da aynı ağ) İK ve şirket yöneticilerine bildirim + `audit_log`
  (`SuspiciousLogin`). Aynı konu için saatte bir uyarı; kullanıcının ilk gözlemi yalnızca kaydedilir.
  KVKK: ham IP saklanmaz (ağ öneki `TENANT_SECRET_KEY`'den türetilen anahtarla HMAC), GeoIP/dış servis
  yok; Keycloak olayları cihaz/tarayıcı bilgisi taşımadığından “yeni cihaz” ayrımı yapılmaz.
  Durum `tenant_login_networks` ve `tenant_security_alerts` tablolarında (180 gün).
- **Platform yöneticisi “cam kırma” erişimi:** platform yöneticisi kiracı verisine yalnızca
  Kiracılar ekranından açtığı gerekçeli, en fazla 4 saatlik izinle (`platform_access_grants`)
  ulaşır. 14 servisin kiracı kapısı (`Tenancy/PlatformAccessGate.cs`) izni denetler, isteği o
  kiracıyla sınırlar (kiracı filtresi uygulanır) ve her erişimi (yöntem + kimliksiz yol, 10 dk'da
  bir) şirketin `audit_log`'una yazar. Şirket yöneticileri bildirim alır, Güvenlik ekranında etkin
  ve geçmiş izinleri görür ve kapatabilir. Kiracı listesi, faturalar, plan ve kayıt izinsiz çalışır.
  Acil geri dönüş: `PLATFORM_ACCESS_GRANTS=off` (eski davranış). tenant-service'in kendi kiracı
  uçları (my-tenant, güvenlik) platform yöneticisinde organizasyon üyeliğine bağlıdır; bu kapıya
  dahil değildir.

## Altyapı ve veri koruma

### Şifreleme anahtarı yenileme (`enc1:` → `enc2:<kimlik>:`)

Şifreli alanlar (AES-256-GCM, yerleşim `nonce(12) | etiket(16) | şifreli metin`) artık bir anahtar
halkasıyla açılır. Kod her serviste aynı kopyadır: `Security/KeyRing.cs` (engagement, governance,
tenant, notification, expense, compensation).

- **Anahtarlar:** `TENANT_SECRET_KEYS="k20261020:base64,k0:base64"`; ilk eleman etkin anahtardır,
  diğerleri yalnızca açmak içindir. Tanımlıysa tek kaynak budur. Tanımlı değilse tek anahtar vardır:
  `k0` = `TENANT_SECRET_KEY`. Mevcut kurulumlar hiçbir şey yapmadan aynen çalışır.
- **Biçimler:** `enc2:<kimlik>:<base64>` yeni biçimdir. `enc1:<base64>` (TCKN/IBAN, pasaport, özel
  nitelikli özel alanlar) ve öneksiz base64 (entegrasyon sırları, SMTP/LDAP parolası, VAPID anahtarı)
  eski biçimlerdir ve `k0` ile açılır. Bordro dışa aktarım dosyaları (bytea) için `HRE2` başlığı +
  kimlik kullanılır; başlıksız dosya `k0`'dır.
- **Yazma:** etkin anahtar `k0` ise eski biçim yazılır. Böylece varsayılan kurulumda veritabanına
  yazılan bayt biçimi değişmez ve eski imaja geri dönülebilir. Etkin anahtar başka bir kimlikse
  `enc2:<kimlik>:` yazılır.
- **Yeniden şifreleme:** sahibi olan her servis açılıştan yaklaşık 1 dk sonra ve 6 saatte bir
  `KeyRotationJob`'u çalıştırır. İş, etkin olmayan anahtarla şifrelenmiş değerleri 200'lük partiler
  hâlinde etkin anahtarla yeniden yazar. UPDATE yalnızca eski değer hâlâ yerindeyse yapılır; bu
  yüzden eşzamanlı yazımlarda veri ezilmez ve iş yeniden çalıştırılabilir. Günlüğe yalnızca
  tablo/sütun ve sayı yazılır, değer asla yazılmaz. Halkada yalnızca `k0` varsa iş hiçbir şey yapmaz.
  Kapatmak için `CRYPTO_REENCRYPT=off`. Açılamayan değerler sayılır ve atlanır.
- **Şifreli sütunlar** (servislerin `Program.cs`'indeki `encryptedColumns` ile `scripts/crypto-keys.sh`
  listesi aynı tutulmalıdır; yeni şifreli sütun eklerken ikisine de eklenmelidir):

  | Servis | Tablo.sütun |
  |---|---|
  | engagement | `engagement_profiles.Iban`, `.NationalId` (öneksiz değer eski düz metindir, `PiiBackfill` şifreler) |
  | expense | `expense_travel_requests.PassportCipher` |
  | compensation | `compensation_payroll_exports.Cipher` (bytea) |
  | notification | `notification_vapid_keys.PrivateKeyEnc` |
  | tenant | `platform_tenants.SmtpPasswordEncrypted`, `tenant_directory_settings.LdapBindPasswordEncrypted` |
  | governance | `governance_chat_apps` (5 sütun), `governance_calendar_connections` (2), `governance_provider_configs.ClientSecretEnc`, `governance_provisioning_configs.CredentialsEnc`, `governance_chat_context.TextEnc`, `governance_chat_exit_progress.AnswersEnc`, `governance_chat_pending.PayloadEnc`, `governance_document_requests.DocumentEnc`, `governance_ethics_reports.ContactEnc`, `governance_osh_exams.NotesEnc`, `governance_custom_field_values.Value` |

  Okuyan diğer servisler de halkayı kullanır: compensation ve governance TCKN/IBAN'ı, notification
  SMTP parolasını okur.
- **Kapsam dışı:** `governance_webhooks.Secret`, `governance_integrations.SigningSecret` ve
  `timeshift_clock_sites.QrSecret` şifreli değildir. Bunlar karşı tarafla paylaşılan HMAC sırlarıdır
  ve ayrıca yenilenir. `TENANT_SECRET_KEY` aynı zamanda türetilmiş anahtarların kökü olmaya devam eder:
  SCORM çerezi, form jetonu, giriş ağı özeti, SIEM takma adı (tanımlı değilse) ve e-imza. Halka bu
  değeri değiştirmez.

**Yenileme kılavuzu.** Tüm adımlar kesintisizdir; her adımdan sonra sistem çalışır durumdadır.

```bash
scripts/crypto-keys.sh add                 # 1) yeni anahtar listenin SONUNA (yalnızca açmak için); .env'e ya da secrets/'e yazılır
scripts/crypto-keys.sh rollout             #    6 servis yeniden oluşturulur: hepsi yeni anahtarı tanır, hâlâ k0 ile yazar
scripts/crypto-keys.sh activate k20261020  # 2) yeni anahtar etkin
scripts/crypto-keys.sh rollout             #    yeni yazımlar enc2:k20261020:; ~1 dk sonra eski değerler yeniden şifrelenir
scripts/crypto-keys.sh status --key k0     # 3) 0 olana kadar bekleyin (çıkış kodu 0); kalan varsa servis günlüğüne bakın
scripts/backup.sh                          #    yeni anahtarla yedek (eski yedekler eski anahtarı ister: anahtarı yedekler kadar saklayın)
scripts/crypto-keys.sh remove k0           # 4) eski anahtar çıkarılır (o anahtarla değer kaldıysa reddeder)
scripts/crypto-keys.sh rollout
```

1. ve 2. adımlar ayrıdır, çünkü bir servis yeni biçimde yazarken başka bir servisin o anahtarı
henüz tanımıyor olması gerekir (ör. notification, tenant'ın şifrelediği SMTP parolasını okur).
Hızlı yol `add --activate` + tek `rollout` olur; bu durumda servisler aynı anda yeniden
oluşturulurken birkaç saniyelik bir pencere kalır. Geri dönüş: `k0`'ı yeniden etkin yapıp (`activate k0`)
`rollout` çalıştırmak. İş, `enc2:` değerleri yeniden eski biçime çevirir.

`status` yalnızca `docker compose exec postgres psql` ile sayım yapar (salt okunur): sütun başına
anahtar kimliği ve değer sayısı. `remove k0` sonrasında da `TENANT_SECRET_KEY` `.env`'de kalır
(türetilmiş anahtarlar). Eski anahtar sızdıysa onu da değiştirin. Bunun etkileri: kayıtlı giriş
ağları bir kez "yeni ağ" sayılır, açık SCORM oturumları ve form jetonları düşer, SIEM takma adları
değişir (`SIEM_PSEUDONYM_KEY` tanımlı değilse).

Testler: 6 servisin test projesinde `KeyRingTests` var. Bu testler eski kodun ürettiği değerlerin
açıldığını, halka biçimini, kurcalamayı, geçersiz listeyi, bayt biçimini, yeniden şifreleme
kararlarını ve geri dönüşü denetler. İş ayrıca geçici bir PostgreSQL'de 450 satırlık tablolarla
(parti sınırı, int kimlik, bytea, düz metin, bozuk değer, olmayan tablo) elle denendi; bu test
repoda yok, çünkü veritabanı ister. Restore tatbikatı (`scripts/restore-drill.sh`) yedekteki
örnek değerleri aynı halkayla açar.

### Sırlar dosyada (`X_FILE`, Docker secrets)

Her .NET servisinin `Program.cs`'i ilk satırda `Security/SecretEnv.Load()` çağırır; ml-inference'ta
`secret_env.py` bu işi yapar. Bilinen bir `X` değişkeni boşsa ve `X_FILE` tanımlıysa dosyanın içeriği
süreç ortamına `X` olarak yazılır. `X` doluysa hiçbir şey değişmez. Dosya okunamaz ya da boşsa
servis açılmaz.

- **Desteklenenler (.NET):** `HR360_DB_PASSWORD`, `HR360_SERVICE_DB_PASSWORD`, `*_DB_PASSWORD`,
  `*_DB_CONNECTION`, `TENANT_SECRET_KEY`, `TENANT_SECRET_KEYS`, `INTERNAL_SERVICE_TOKEN(_PREVIOUS)`,
  `KEYCLOAK_TENANT_ADMIN_CLIENT_SECRET`, `KEYCLOAK_ADMIN_PASSWORD`, `MINIO_ACCESS_KEY`,
  `MINIO_SECRET_KEY`, `SMTP_PASSWORD`, `FORM_TOKEN_KEY`, `LOGIN_WATCH_KEY`, `SIEM_PSEUDONYM_KEY`,
  `LLM_API_KEY`, `OCR_CLIENT_SECRET`.
- **Desteklenenler (ml-inference):** `INTERNAL_SERVICE_TOKEN(_PREVIOUS)`, `KEYCLOAK_CLIENT_SECRET`, `AWS_*`.
- **Bağlantı dizesi parolası:** `X_DB_CONNECTION` için sıra şöyledir:
  1. `X_DB_PASSWORD` (ör. `RETENTION_DB_PASSWORD`)
  2. `HR360_SERVICE_DB_PASSWORD` (servis rolünün parolası; `RETENTION_DB_CONNECTION` hariç)
  3. dizedeki parola
  4. dizedeki parola boşsa `HR360_DB_PASSWORD`

  Böylece parola compose'daki dizeye hiç girmeden dosyadan verilebilir.

**Taşıma:** `scripts/secrets-migrate.sh` (önce `status`, sonra `move --dry-run`):

```bash
scripts/secrets-migrate.sh status          # hangi sır nerede (değer yazmaz)
scripts/secrets-migrate.sh move --dry-run  # varsayılan seçim: TENANT_SECRET_KEY(S), KEYCLOAK_TENANT_ADMIN_CLIENT_SECRET, dolu HR360_DB_PASSWORD_*
scripts/secrets-migrate.sh move            # .env -> secrets/<ad>.txt, secrets/compose.secrets.yml, .env'e COMPOSE_FILE
docker compose up -d --force-recreate
scripts/secrets-migrate.sh restore         # geri: dosyalar -> .env, ek ve COMPOSE_FILE kaldırılır
```

- **Dosya izinleri:** dizin 0700, dosyalar 0640 ve grup 1654 (.NET konteyner kullanıcısı). 0600
  olsaydı root olmayan konteyner dosyayı okuyamazdı (Docker Compose, swarm dışında secret dosyasını
  olduğu gibi bağlar). `secrets/` gitignore'dadır.
- **Compose eki:** üretilen `secrets/compose.secrets.yml` her sırrı yalnızca o değişkeni kullanan
  servislere `/run/secrets/<ad>` olarak bağlar ve `X_FILE`'ı verir. Servis rolü parolası taşındıysa
  o servisin bağlantı dizesinden ortak parola da çıkarılır. Eki elle düzenlemeyin; yeniden üretmek
  için `scripts/secrets-migrate.sh compose`.
- **COMPOSE_FILE:** `.env`'deki `COMPOSE_FILE=docker-compose.yml:secrets/compose.secrets.yml`
  sayesinde `docker compose` ve `scripts/*.sh` eki kendiliğinden kullanır. **`-f` veren komutlar
  `.env`'deki `COMPOSE_FILE`'ı yok sayar.** Kendi `docker compose -f ...` komutlarınıza (geliştirme
  ve test komutları dahil) `-f secrets/compose.secrets.yml` ekleyin; `scripts/test.sh` bunu kendisi yapar. Ek verilmezse
  servisler sırsız açılır: TCKN/IBAN şifrelenmeden yazılır (uyarı günlüğe düşer), iç uçlar kapanır.
- **Betiklerin davranışı:**
  - `scripts/crypto-keys.sh`, `keycloak-service-account.sh` ve `install.sh` (güncelleme yolu) dosyaya
    taşınmış değeri görür. Yedek betikleri de görür; `--with-env` yedeklemede `secrets/` arşive girer.
  - `scripts/rotate-internal-token.sh` dosya modunda çalışmaz. Önce
    `restore INTERNAL_SERVICE_TOKEN INTERNAL_SERVICE_TOKEN_PREVIOUS` gerekir; bu yüzden
    `INTERNAL_SERVICE_TOKEN` varsayılan seçimde değildir.

**`.env`'de kalanlar** (bilerek; taşıma reddedilir ya da desteklenmez):

| Sır | Neden kalıyor |
|---|---|
| `HR360_DB_PASSWORD` | postgres ilk kurulumu, Keycloak (`KC_DB_PASSWORD`), MLflow URI'si ve postgres-exporter da kullanıyor |
| `MINIO_ROOT_*` | minio-init ve MLflow komut satırında/ortamında kullanıyor; tenant-service `MINIO_*_FILE`'ı okuyabilir |
| `REDIS_PASSWORD` | `REDIS_URL` compose'da kuruluyor |
| `KEYCLOAK_ADMIN_PASSWORD`, `SMTP_PASSWORD`, `ML_KEYCLOAK_CLIENT_SECRET`, `BACKUP_ENCRYPTION_KEY` | Keycloak, Alertmanager ve betikler doğrudan okuyor |

Testler:

- 13 servis test projesinde `SecretEnvTests`.
- `apps/ml-inference/tests/test_secret_env.py`.
- `tests/install/test_secrets.sh` (CI'da): taşıma/geri alma, izinler, compose eki, çıktıda sır
  olmaması, dosya modunda anahtar halkası.
- Varsayılan `.env` ile 31 bağlantı dizesinin compose çıktısının önceki sürümle aynı olduğu denetlendi.

### Veritabanı en az yetki ve satır düzeyi güvenlik (isteğe bağlı)

Varsayılan davranış değişmez: tüm servisler `hr360admin` ile bağlanır. Servis başına rollere geçiş
servis servis yapılır ve tek komutla geri alınır.

**Roller.** Her .NET servisi için bir rol vardır (`hr360_leave`, `hr360_governance` … 15 rol). Bunlara
ek olarak yalnızca `audit_log`'da `SELECT, DELETE` yetkisi olan `hr360_retention` rolü bulunur. Yetkiler
koddan üretilir (`scripts/db-roles.sh generate` → `deploy/postgres/roles.sql`) ve şu kurallara göre verilir:

- DbContext'te eşlenen tablolar (`ToTable`) tam yetki alır (SELECT/INSERT/UPDATE/DELETE).
- SQL metinlerindeki `INSERT INTO`, `UPDATE … SET`, `DELETE FROM`, `ON CONFLICT DO UPDATE` ve
  `FOR UPDATE` kalıpları ilgili yazma yetkisini getirir. Metinde adı geçen her tablo SELECT alır.
- Tetikleyici fonksiyonları çağıranın yetkisiyle çalışır. Bu yüzden bir tetikleyicinin dokunduğu
  tablolar da yetkiye eklenir. Örnek: `expense_documents` silindiğinde `governance_signatures` ve
  `governance_signature_otps` satırları da silinir.
- `audit_log` için hiçbir servis rolüne UPDATE, DELETE ya da TRUNCATE verilmez. Servis rolleri
  yalnızca SELECT ve INSERT alır, çünkü zincir tetikleyicisi önceki satırı okur. UPDATE ayrıca
  `audit_log_immutable` tetikleyicisiyle de engellidir.
- Tablolara bağlı serial/identity dizileri INSERT yetkisiyle birlikte verilir. Rollerin şemada
  CREATE yetkisi yoktur. Roller NOSUPERUSER ve NOBYPASSRLS'dir; bağlantı sınırı 40'tır.
- Kesin karar verilemeyen noktalar `deploy/postgres/roles-review.md` dosyasında listelenir: başka
  servisin tablosuna yazma, dinamik tablo adı, satır kilidi.

`roles.sql` parola içermez ve idempotenttir. Tek işlemde önce yetkileri geri alır, sonra yeniden
verir. Bu sırada çalışan servisler ara durumu görmez.

**Geçiş sırası (servis başına):**

1. `scripts/db-roles.sh apply`: rolleri oluşturur, yetkileri verir ve rollere giriş izniyle parola
   atar. Parola şu sırayla aranır: `.env`'deki `HR360_DB_PASSWORD_<SERVİS>`, ardından
   `HR360_DB_PASSWORD_<SERVİS>_FILE`, ardından `./secrets/hr360_db_password_<servis>.txt`. Hiçbiri
   yoksa parola üretilir ve `.env`'e yazılır (`--secrets-dir secrets` verilirse o dizine, chmod 600).
   Parolalar ekrana basılmaz. Veritabanına düz parola değil, istemcide hesaplanan SCRAM doğrulayıcısı
   gider.
2. `scripts/db-roles.sh check`: kuru çalıştırmadır, yalnızca okur. Her rolün, servisin kodunda geçen
   her tabloya gereken yetkisi olup olmadığını denetler (`has_table_privilege`). Ayrıca rol olarak
   `SELECT … LIMIT 0` dener; işlem geri alınır. `audit_log`'da yasak yetki varsa bildirir. Sorun
   varsa çıkış kodu 1'dir.
3. `scripts/db-roles.sh enable leave` (ya da `all`): önce `check` çalışır. Sorun yoksa `.env`'e
   `HR360_DB_USER_LEAVE=hr360_leave` yazılır. Ardından `docker compose up -d leave-service`.
   Compose bağlantı dizesi
   `Username=${HR360_DB_USER_<SERVİS>:-hr360admin};Password=${HR360_DB_PASSWORD_<SERVİS>:-${HR360_DB_PASSWORD:-}}`
   biçimindedir.
4. Geri dönüş: `scripts/db-roles.sh disable leave` ve ardından servisi yeniden oluşturmak.
   Durumu görmek için: `scripts/db-roles.sh status`.
5. Her migration'dan (yeni tablo) sonra `scripts/db-roles.sh generate && scripts/db-roles.sh grants`
   çalıştırılmalıdır. `hr360admin` için varsayılan yetkiler (DEFAULT PRIVILEGES) bilerek açılmadı.

**governance-service:** saklama süresi işi (`Retention`, kategori AuditLog) `audit_log`'dan satır
siler. Bu yetki yalnızca `hr360_retention` rolündedir. Governance bu silmeyi ayrı bir bağlantıyla
(`RETENTION_DB_CONNECTION`, compose'da tanımlı, varsayılan kullanıcı yine `hr360admin`) yapar.
`enable governance`, `hr360_retention` girebiliyorsa `HR360_DB_USER_RETENTION`'ı da yazar;
`disable governance` geri alır. Governance retention işi başka servislerin tablolarına da yazar
(aday anonimleştirme, çalışan/profil anonimleştirme, bordro ve bildirim silme). Bu yetkiler
inceleme listesinde görünür.

`scripts/update.sh`, `.env`'de herhangi bir `HR360_DB_USER_*` tanımlıysa göçlerden sonra
`deploy/postgres/roles.sql`'i uygular; böylece yeni tablolar için yetki eksik kalmaz. Anahtar
yenileme işinin yeniden yazdığı tablolar da (`encryptedColumns`) üreticide UPDATE yetkisi alır.

Keycloak, MLflow, ML servisi ve postgres-exporter `hr360admin` ile kalır.

**Satır düzeyi güvenlik (RLS).** `scripts/sql/2026-10-20_rls.sql`, `"TenantSlug"` sütunu olan her
tabloya (şu an 186 tablo) `hr360_tenant_isolation` politikasını ekler. **RLS'yi açmaz.** Politikanın
davranışı şöyledir:

- Oturum değişkeni `app.tenant` tanımsız ya da boşsa her satır görünür (izin verici).
- Tanımlıysa yalnızca o kiracının satırları okunabilir ve yazılabilir. Başka kiracı adına satır
  eklemek de reddedilir (`WITH CHECK`).

Yönetim komutları (yalnızca `hr360admin`):

```sql
SELECT hr360_rls_enable();          -- eksik politikaları tamamlar ve RLS'yi açar
SELECT hr360_rls_disable();         -- RLS'yi kapatır
SELECT * FROM hr360_rls_status();   -- tablo başına durum
```

Tablo sahibi `hr360admin` RLS'den muaftır (FORCE kullanılmaz). Bu yüzden RLS, ancak servisler kendi
rolleriyle bağlandığında etki eder. Servisler henüz `app.tenant`'ı ayarlamıyor; RLS açılsa bile
izin verici kural sayesinde hiçbir şey değişmez. Gerçek yalıtım için ileride yapılacaklar:

- Her serviste, istek kiracısını bağlantı açılışında ya da işlem başında
  `set_config('app.tenant', <slug>, true)` ile yazan bir EF kesicisi eklenmeli.
- Kiracılar arası çalışan arka plan işleri ve platform yöneticisi değişkeni boş bırakmalı.
- PlatformAccessGate'in başka kiracının `audit_log`'una yazması değişken o kiracıya ayarlıyken
  yapılmalı. `"TenantSlug"` NULL olan denetim satırları, değişken ayarlıyken eklenemez ve görünmez.
- Görünümler (`analytics_*`) sahibinin yetkisiyle çalışır ve RLS'yi atlar. Kendi kiracı koşullarına
  güvenilir; gerekirse `security_invoker` ile değiştirilmeli.

Doğrulama boş bir postgres:18-alpine'da yapıldı. Tüm şema `ON_ERROR_STOP` ile uygulandı, RLS
dosyası iki kez çalıştırıldı. `hr360_rls_enable()` sonrası rol olarak denendi: değişken yokken 3/3
satır, `a` kiracısıyla yalnızca `a` satırları görünüyor, `b` adına ekleme reddediliyor.

### Yedekler: değişmez kopya, imzalı özet, geri yükleme tatbikatı

Yedekler zaten şifreliydi: `.env`'de `BACKUP_ENCRYPTION_KEY` varsa (`install.sh` üretir)
`scripts/backup.sh` arşivi `openssl aes-256-cbc` (PBKDF2, 200 000 tur) ile şifreler. Ek korumalar:

- **İmzalı özet:** her arşivin yanına `<arşiv>.manifest` yazılır: dosya adı, boyut, SHA-256,
  oluşturma zamanı, anahtar kimliği ve bunların HMAC-SHA256 imzası. Anahtar `BACKUP_MANIFEST_KEY`
  (ya da `BACKUP_MANIFEST_KEY_FILE` / `secrets/backup_manifest_key.txt`); tanımlı değilse
  `BACKUP_ENCRYPTION_KEY`'den türetilir (`keyid=derived`), o da yoksa özet imzasız yazılır ve
  uyarılır. CBC şifrelemesi bütünlük sağlamadığından arşivdeki değişiklik bu imzayla yakalanır.
  Ayrı bir `BACKUP_MANIFEST_KEY` önerilir: arşivi açamayan biri de (yalnızca bu anahtarla)
  doğrulayabilir. Anahtar `openssl rand -hex 32` ile üretilip `.env`'e eklenir.
  - `scripts/restore.sh` özeti denetler: arşiv ya da özet değişmişse **durur**; bilerek devam etmek
    için `--ignore-manifest`. Özeti olmayan eski yedekler uyarıyla geri yüklenir.
  - `scripts/backup.sh verify` de özeti denetler. Dış depoya (S3) arşivle birlikte özet de gider.
- **Değişmez kopya (`scripts/backup.sh --to-minio`):** arşiv ve özeti nesne kilitli (WORM) bir
  MinIO kovasına kopyalanır. Kova yoksa `mc mb --with-lock` ile oluşturulur (sürümleme otomatik
  açılır) ve varsayılan saklama ayarlanır; her nesne bu süre boyunca silinemez, üzerine
  yazılamaz (silme yalnızca "silme işareti" bırakır, eski sürüm durur). Ayarlar (`.env`):

  | Değişken | Varsayılan | Anlamı |
  |---|---|---|
  | `BACKUP_MINIO_URL` | boş | Boşsa kurulumun kendi MinIO'su (`http://minio:9000`, `hr360-net` ağı); doluysa dış MinIO/S3 adresi (yalnızca şifreli arşiv, KVKK m.9 denetimi `BACKUP_S3_*` ile aynı) |
  | `BACKUP_MINIO_BUCKET` | `hr360-backups-locked` | Kova adı |
  | `BACKUP_MINIO_RETENTION_MODE` | `GOVERNANCE` | `GOVERNANCE` (özel yetkiyle kısaltılabilir) ya da `COMPLIANCE` (kök hesap dahil kimse kısaltamaz/silemez) |
  | `BACKUP_MINIO_RETENTION_DAYS` | `30` | Saklama süresi (gün) |
  | `BACKUP_MINIO_ACCESS_KEY` / `_SECRET_KEY` | `MINIO_ROOT_USER` / `_PASSWORD` | Kimlik (`_FILE` ya da `secrets/` ile de) |
  | `BACKUP_MINIO_PREFIX`, `BACKUP_MINIO_NETWORK`, `BACKUP_MC_IMAGE` | — | Nesne öneki, docker ağı (dış hedefte `host`), `mc` imajı (özetle sabitlenmiş `bitnamilegacy/minio-client`) |

  Nesne kilidi yalnızca kova **oluşturulurken** açılabilir: aynı adda kilitsiz bir kova varsa betik
  durur ve yeni bir kova adı ister. Kimlik bilgileri `docker` komut satırında görünmez (600 izinli
  geçici `--env-file`). Kurulumun kendi MinIO'sundaki kova sonraki yedeklerin `minio.tar.gz`'sine
  girmez (yedekler katlanarak büyümesin) ve `restore.sh` MinIO'yu geri yüklerken bu kovaya dokunmaz.

  **Sınırlar:** kurulumun kendi MinIO'su aynı disktedir: sunucunun ya da diskin kaybına karşı
  korumaz; sunucuda root olan biri birim dosyalarını doğrudan silebilir. Kilit, uygulama/MinIO
  düzeyindeki silmeye (ör. ele geçirilmiş bir MinIO hesabı, fidye yazılımının S3 API'si üzerinden
  silmesi) karşı korur. Gerçek felaket kurtarma için `BACKUP_MINIO_URL` ile başka bir sunucudaki
  MinIO'yu `COMPLIANCE` kipinde kullanın; kopyalayan hesap yalnızca `s3:PutObject` (ve
  `s3:GetBucketObjectLockConfiguration`) yetkisine sahip ayrı bir kullanıcı olsun (kova ve varsayılan
  saklama bir kez yönetici hesabıyla oluşturulur). GOVERNANCE kipinde kök MinIO hesabı kilidi
  aşabilir.
- **Geri yükleme tatbikatı (`scripts/restore-drill.sh [ARŞİV]`):** en son (ya da verilen) yedeği
  ağa bağlı olmayan, adlandırılmış geçici bir PostgreSQL konteynerinde (`hr360-restore-drill-*`,
  compose'daki postgres imajı) açar; canlı veritabanı, MinIO, birimler ve `.env` yalnızca okunur.
  Denetimler: imzalı özet, arşivin şifresinin çözülmesi, `SHA256SUMS`, her dökümün `pg_restore`'u,
  ana tabloların satır sayıları (`platform_tenants`, `employee_employees`, `leave_requests`,
  `leave_balances`, `engagement_profiles`, `audit_log`), denetim zinciri (`audit_row_hash` ile
  governance `AuditChainGuard`'ın kuralları: değişmiş/kopuk/boşluk/zincirsiz satır yok) ve her
  kiracının son çapası (`governance_audit_anchors`), şifreli alanlardan örnekler (sütun başına
  `--samples`, varsayılan 5) anahtar halkasıyla açılır (`TENANT_SECRET_KEYS`, yoksa
  `TENANT_SECRET_KEY` = `k0`; `enc2:<kimlik>:`, `enc1:` ve öneksiz eski biçim). Anahtarlar
  arşivde `env` (ve `secrets/`) varsa oradan, yoksa canlı `.env`/`secrets/`'tan okunur. Değerler
  hiçbir zaman yazdırılmaz ve diske yazılmaz: yalnızca sütun başına açılan/açılamayan/anahtarı
  halkada olmayan sayısı. Şifre çözme `--network none` çalışan küçük bir Python + `cryptography`
  imajında (`hr360-restore-drill-crypto:1`, ilk çalıştırmada bir kez oluşturulur; internet yalnızca
  bu adımda gerekir) yapılır. Sonunda konteyner birimiyle silinir (`--keep` ile incelemeye bırakılır).
  Çıkış kodu 0/1; son satır `TATBIKAT BASARILI …` ya da `TATBIKAT BASARISIZ …`.
  Seçenekler: `--require-manifest` (özetsiz/imzasız yedeği başarısız say), `--no-decrypt`,
  `--env DOSYA`, `--out DİZİN`. Mevcut `scripts/backup.sh verify` (daha hafif deneme) korunur.
  Ekim 2026'da canlı sistemin taze yedeğiyle denendi: 25 799 denetim satırı, 3 zincir ve çapalar
  sağlam, şifreli alan örnekleri açıldı.
- **`--with-env` ve `secrets/`:** sırlar `scripts/secrets-migrate.sh` ile `secrets/` dizinine
  taşındıysa `--with-env` bu dizini de arşive koyar; `restore.sh --with-env` geri yükler (dizin 0700,
  dosyalar 0640; eskisi `secrets.before-restore-*` olarak saklanır). Yedek betikleri tüm sırları
  (`BACKUP_ENCRYPTION_KEY`, `INTERNAL_SERVICE_TOKEN`, `MINIO_ROOT_*`, S3 anahtarları,
  `TENANT_SECRET_KEY(S)`) `.env`'den, `X_FILE`'dan ya da `secrets/<ad>.txt`'ten okur.

Uygulama sırası (öneri):

```bash
# 1) (önerilir) ayrı özet anahtarı
echo "BACKUP_MANIFEST_KEY=$(openssl rand -hex 32)" >> .env
# 2) değişmez kopya: önce elle bir kez
scripts/backup.sh --to-minio
# 3) tatbikat
scripts/restore-drill.sh
# 4) zamanlayıcıları gözden geçirip kurun (aşağıda)
scripts/schedule.sh print
sudo scripts/schedule.sh install backup restore-drill
```

### Zamanlayıcı (`scripts/schedule.sh`)

Düzenli işler için systemd zamanlayıcısı (ya da `--cron` ile kullanıcı crontab'ı) üretir.
Varsayılan komut `print`: birimleri gösterir, hiçbir şey yazmaz. İşler: `backup` (her gece 03:15;
`.env`'de `BACKUP_MINIO_URL`/`BACKUP_MINIO_BUCKET` varsa ya da `--to-minio` verilirse değişmez kopya
ile), `restore-drill` (Pazar 05:00), `zap` (Cumartesi 02:00, `scripts/security-scan.sh zap`).
`install İŞ...` birimleri `/etc/systemd/system/hr360-<iş>.{service,timer}` olarak yazar ve
etkinleştirir (root/sudo), `remove İŞ...` kaldırır, `status` listeler. Seçenekler: `--at SS:DD`,
`--day Mon..Sun`, `--keep N`, `--user AD` (varsayılan repo sahibi; docker erişimi olmalı).
Çıktılar `backups/<iş>.log`'a eklenir; başarısız tatbikat birimi `failed` yapar
(`systemctl --failed`, `journalctl -u hr360-restore-drill`). Eski `scripts/backup.sh schedule`
(cron) yoluyla aynı anda kurmayın; `install backup` bunu fark edip uyarır.

### Haftalık ZAP taraması

OWASP ZAP temel taraması (`zap-baseline.py`: yalnızca pasif kurallar + kısa örümcek; saldırı
isteği göndermez) iki yerde çalışır. Kurallar tek dosyadadır: `.zap/rules.tsv`.

- **CI (`.github/workflows/zap.yml`)**: her pazartesi 03:17 UTC ve elle (Actions › ZAP › Run
  workflow). Tüm yığın (Keycloak, Kafka, 15 servis) CI için ağır olduğundan yalnızca canlıdaki
  iki ön parça kurulur: arayüz imajı (`apps/web`, kendi nginx'i, CSP) ve önündeki gateway
  (`deploy/nginx/nginx.conf`, aynı `nginx:1.27-alpine`). Gateway `resolve` ile çalıştığından arka
  uçlar olmadan açılır; `/api` ve `/auth` 502 döner. Böylece güvenlik başlığı, CSP ve gateway
  yapılandırması gerilemeleri yakalanır. FAIL kuralı eşleşirse iş başarısız olur; HTML/JSON rapor
  iş çıktısıdır (30 gün). API ve Keycloak ekranları bu taramada YOKTUR.
- **Canlı/hazırlık sunucusu (`scripts/security-scan.sh zap`)**: sunucudan çalıştırılır.

  ```bash
  scripts/security-scan.sh zap                       # .env'deki PUBLIC_ORIGIN, yoksa http://gateway/ (hr360-net)
  scripts/security-scan.sh zap https://hr.ornek.com  # belirli adres
  scripts/security-scan.sh zap http://gateway/       # Docker ağı içinden (TLS ve dış katman hariç)
  scripts/security-scan.sh zap --ajax --minutes 5    # tarayıcılı (AJAX) örümcek, daha uzun
  ```

  Sunucuda NAT hairpin olmayabilir: genel ad sunucunun kendi çözümlemesiyle (`/etc/hosts` dahil)
  bulunur ve konteynere `--add-host` ile verilir; adres `127.x` ise konteyner sunucunun ağını
  kullanır. Ortam değişkenleri: `ZAP_TARGET` (adres), `ZAP_NETWORK` (Docker ağı), `ZAP_MINUTES`,
  `ZAP_IMAGE`. Rapor `security-reports/zap-<zaman>.html` ve `.json` (gitignore'da; rapor sunucu
  adı ve yolları içerir, paylaşırken dikkat). Çıkış kodu: 0 FAIL yok, 1 FAIL kuralı eşleşti,
  2 tarama çalışmadı. `scripts/security-scan.sh` (= `all`) ZAP'i **çalıştırmaz**: çalışan bir
  hedef ister. Haftalık zamanlama: `scripts/schedule.sh` (`zap` işi; kurmadan önce gözden geçirin).
- **Kurallar (`.zap/rules.tsv`)**: güvenlik başlıkları (10020, 10021, 10063), CSP yokluğu
  (10038), başka alandan betik (10017), bilinen açıklı JS kütüphanesi (10003), karışık içerik ve
  HTTP/HTTPS form geçişleri (10040–10042), dizin listeleme, X-Powered-By, kaynak sızıntısı ve
  polyfill alanı **FAIL**'dir. Kabul edilen riskler **IGNORE**: 10055 (`style-src 'unsafe-inline'`),
  90004 (Cross-Origin-Embedder-Policy yok: SCORM iframe'i ve dış görseller COEP ile çalışmaz; COOP
  ve CORP zaten var) ve bilgi amaçlı kurallar (10049, 10096, 10109, 10111–10113, 10116). Keycloak
  çerezleri (10010 HttpOnly, 10054 SameSite), HSTS (10035) ve HTTP üzerinden taranınca Secure
  bayrağı (10011) **WARN** kalır. Yeni bir IGNORE eklerken gerekçesini “Bilinen ve kabul edilen
  riskler”e de yazın. ZAP imajı özet değeriyle (`sha256`) sabittir; güncellemek için
  `security-scan.sh` içindeki `ZAP_IMAGE`'ı değiştirin.
- **İlk sonuç (6 Ekim 2026, 1 dk örümcek)**: iç gateway (`http://gateway/`) ve genel adres: 0 FAIL;
  genel adreste 1 WARN (10015 Cache-control, yeniden inceleme önerisi), 4 IGNORE. CI benzetimi
  (arayüz + gateway, arka uçsuz): 0 FAIL, 0 WARN. Başlıksız düz bir nginx'e karşı denetim: 4 FAIL
  (10020, 10021, 10038, 10063) ve çıkış kodu 1 — kural dosyasının gerilemeyi yakaladığı doğrulandı.

## Bilinen ve kabul edilen riskler

- **SCORM içeriği aynı kökenden sunulur**: SCORM 1.2 paketinin HTML/JS'i (ve içindeki SVG'ler)
  `window.parent.API`'ye erişebilmek için uygulamayla aynı kökende iframe içinde çalışır.
  Paket yüklemek yalnızca İK yetkisindedir; paket içi dosyalar uzantıya göre türlenir ve
  `nosniff` ile sunulur. Güvenilmeyen kaynaktan paket yüklemeyin.

- **Cross-Origin-Embedder-Policy yok** (ZAP 90004): SCORM iframe'i ve dış kaynaklı görseller COEP
  ile çalışmaz; COOP ve CORP başlıkları zaten var. `.zap/rules.tsv`'de IGNORE.
- **`style-src 'unsafe-inline'`** (ZAP uyarısı 10055): React bileşenleri ve
  animasyon kütüphanesi `style` özniteliği kullanır. Betik çalıştırmaya izin
  vermediği için risk düşüktür.
- **Keycloak giriş ekranında `script-src 'unsafe-inline'`**: Keycloak'ın kendi
  şablonları satır içi betik kullanır. Kullanıcı girdisi bu sayfalarda Keycloak
  tarafından kaçışlanır.
- **`KEYCLOAK_LOCALE` çerezi `SameSite=None`**: Keycloak'ın varsayılanı; oturum
  bilgisi taşımaz.
- **MLflow model yükleme**: Kayıtlı modeller pickle biçimindedir; model kaydına
  yazabilen biri sunucuda kod çalıştırabilir (CVE-2024-37059 türü). MLflow
  dışarıya kapalıdır (`127.0.0.1:5000`) ve kimlik doğrulaması yoktur; erişimi
  yalnızca sunucu yöneticisine verin.
- **Hız sınırı gateway belleğinde**: çok örnekli kurulumda örnek başınadır.
- **E-posta ile davet ve parola sıfırlama** Keycloak'ın SMTP ayarına bağlıdır;
  SMTP TLS'siz (25) yapılandırılırsa bu iletiler şifresiz gider.

## Kurulum kontrol listesi

1. HTTPS'i güvenilir bir sertifikayla açın: `scripts/tls.sh enable --letsencrypt ...`
   ya da `--cert/--key`. Slack, Teams, takvim ve Zoom entegrasyonları HTTPS ister.
2. `.env` dosyasını yalnızca root okuyabilsin: `chmod 600 .env`. Yedeklerde
   `--with-env` kullanıyorsanız arşivi de aynı şekilde saklayın.
3. Keycloak yönetim panelini ana adreste kapatın: `scripts/keycloak-admin-access.sh port`; panel
   ayrı portta (8090, HTTPS) yayınlanır. Alan adı + HTTPS ile kurulumda `install.sh` bunu
   varsayılan olarak yapar (`--keycloak-admin` verilmediyse). Bu portu **dış güvenlik duvarında**
   yalnızca yönetim IP'lerine açın (bkz. [Keycloak yönetim paneli erişimi](../runbooks/keycloak-yonetim-paneli-erisimi.md));
   sunucunun kendi güvenlik duvarına (ufw/firewalld) dokunulmaz.
4. Yönetici hesaplarında iki adımlı doğrulamayı zorunlu yapın (Güvenlik ekranı).
5. Sunucuda yalnızca 80/443 (ve gerekiyorsa 8090) açık olsun.
6. Ayda bir `scripts/security-scan.sh` çalıştırın; imajları güncellemek için
   `docker compose build --pull && docker compose up -d`. Canlı ZAP taraması:
   `scripts/security-scan.sh zap` (haftalık: `scripts/schedule.sh`).
7. Yedekleri değişmez kopyayla ve haftalık tatbikatla doğrulayın: `scripts/backup.sh --to-minio`,
   `scripts/restore-drill.sh`, `scripts/schedule.sh print` → `install backup restore-drill`.
8. Üretimde demo verisi kurmayın (`install.sh` varsayılanı; `--demo` vermeyin). Şirketleri
   yalnızca platform yöneticisi açacaksa (Platform › Kiracılar › Yeni kiracı) herkese açık şirket
   kaydını kapatın: `.env`'de `PUBLIC_SIGNUP_ENABLED=false` (ya da kurulumda `--no-signup`).
9. Kurulumda üretilen yönetici parolaları bir yerde göründüyse yenileyin:
   `python3 scripts/rotate-passwords.py [--keycloak-admin]`.
10. Swagger arayüzü üretimde (`ASPNETCORE_ENVIRONMENT=Production`) kapalıdır; API belgeleri
    yalnızca oturum açmış kullanıcıya uygulamadaki **API belgeleri** ekranından sunulur.
11. İsteğe bağlı: sırları dosyaya taşıyın (`scripts/secrets-migrate.sh`), servis başına veritabanı
   rollerine geçin (`scripts/db-roles.sh`), şifreleme anahtarını yılda bir yenileyin
   (`scripts/crypto-keys.sh`).

# Dış entegrasyonların sandbox doğrulama listesi

HR360'ın dış entegrasyonları (Slack, Microsoft Teams, Google, Microsoft 365, Zoom, SMTP, Web Push, SCIM,
LDAP/Active Directory, LLM, webhook) **gerçek hesaplarla denenmedi**. Otomatik testler yalnızca sahte
sunucuya (`tests/integration/chatmock.py`) karşı çalışır. Bu belge, bir entegrasyonu canlıya almadan önce
**deneme (sandbox) hesabıyla** elle doğrulamak için yazıldı: her sağlayıcı için ne ayarlanır, hangi adımlar
izlenir ve ne görülmelidir.

Genel kurallar:

- Deneme için **ayrı bir sandbox kiracısı / çalışma alanı** kullanın (Slack ücretsiz çalışma alanı, Microsoft 365
  Developer Program kiracısı, Google Workspace deneme sürümü, Zoom geliştirici hesabı). Gerçek çalışan verisini
  deneme hesabına aktarmayın; HR360'ta demo şirketi ya da test kullanıcılarıyla çalışın.
- **KVKK m.9:** Slack, Microsoft, Google, Zoom ve bulut LLM'ler kişisel veriyi yurt dışına aktarır. HR360 bunları
  **KVKK › Yurt dışı aktarım** ekranında hukuki dayanak kaydı olmadan etkinleştirmez (`kvkk_transfer` hatası).
  Sandbox denemesi için de kayıt girin; "referans" alanına "sandbox" yazın.
- HTTPS gerekir: Google, Microsoft ve Slack yönlendirme/olay adresi olarak yalnızca `https://` kabul eder
  (`scripts/tls.sh enable`, bkz. `docs/runbooks/https.md`). Adresler `.env` → `PUBLIC_ORIGIN` ile üretilir.
- Sırlar (bot jetonu, client secret, servis hesabı anahtarı, SMTP/LDAP parolası) veritabanında şifreli
  (`TENANT_SECRET_KEY`) saklanır ve arayüzde bir daha gösterilmez. Ekran görüntüsü/kayıt paylaşırken dikkat.
- Her doğrulamadan sonra `scripts/integration-check.sh` çalıştırın (aşağıda) ve sonucu bu listedeki
  "Beklenen" sütunuyla karşılaştırın.

## Otomatik bağlantı kontrolü: `scripts/integration-check.sh`

Uygulamanın kendi "bağlantıyı test et" uçlarını çağırır; sağlayıcıya doğrudan gitmez, hiçbir sırrı yazmaz.

```bash
# Şirket yöneticisi (İK + şirket yöneticisi) jetonuyla; jeton 5 dk geçerlidir.
HR360_TOKEN=<erişim jetonu> scripts/integration-check.sh                 # yalnızca okuma + ileti göndermeyen testler
HR360_TOKEN=<erişim jetonu> scripts/integration-check.sh --send          # + size DM, kanala deneme mesajı, webhook ping, push
HR360_TOKEN=<...> SCIM_TOKEN=<hr360scim_...> scripts/integration-check.sh   # + SCIM jetonuyla ServiceProviderConfig
scripts/integration-check.sh --login admin --url http://localhost       # test ortamı (tests/credentials.json)
scripts/integration-check.sh --json                                     # izleme/CI için JSON satırları
```

Jeton: tarayıcıda HR360'a girip geliştirici araçları › Ağ sekmesinde `openid-connect/token` yanıtındaki
`access_token`. Çıkış kodu 0 = yapılandırılmış her şey başarılı, 1 = en az bir hata, 2 = oturum/kullanım hatası.

| Alan | Çağrılan uç | İleti gönderir mi |
|---|---|---|
| SMTP (şirketin özel sunucusu) | `POST /api/tenant/my-tenant/smtp-test` (bağlan + TLS + kimlik doğrulama) | Hayır |
| Sohbet uygulamaları | `GET /api/governance/chat-apps` (son hata) · `--send`: `POST …/chat-apps/{id}/test` | `--send` ile size DM |
| Takvim/toplantı (Google, Microsoft, Zoom) | `POST /api/governance/calendar/providers/{p}/test` | Hayır |
| Hesap açma/kapatma | `POST /api/governance/account-provisioning/configs/{p}/test` | Hayır (salt okunur liste) |
| LDAP / AD | `POST /api/tenant/my-tenant/directory/ldap/test` | Hayır (bağlan + arama) |
| SCIM | `GET …/directory/scim-tokens` · `SCIM_TOKEN` ile `GET /api/tenant/scim/v2/ServiceProviderConfig` | Hayır |
| Web Push | `GET /api/notification/notifications/push/public-key` · `--send`: `POST …/push/test` | `--send` ile kendi cihazlarınıza |
| Kanal bildirimleri, webhook | yalnızca `--send`: `POST …/integrations/{id}/test`, `POST …/webhooks/{id}/ping` | Evet |
| LLM | `GET /api/governance/ai/settings` (yalnızca yapılandırma durumu) | Hayır |

Takvim testinin Google/Microsoft için anlamı: kullanıcı adına OAuth'ta gerçek bir yetki kodu olmadan jeton
alınamaz; HR360 jeton ucuna geçersiz bir kod gönderir. `invalid_grant` = istemci kimliği ve gizli anahtar
**doğru**; `invalid_client`/401 = yanlış. Uçtan uca doğrulama için aşağıdaki elle adımlar gerekir.

---

## Slack

**Ayarla (sandbox):** ücretsiz bir Slack çalışma alanı. HR360 › Entegrasyonlar › Sohbet uygulamaları › Slack ›
"Uygulama bildirimi (manifest)" ile api.slack.com/apps › *Create New App › From a manifest*. Bot jetonu
(`xoxb-…`) ve Signing Secret'ı HR360'a girin. Olay, komut ve etkileşim adresleri
`https://<alan>/api/governance/chat/slack/<uygulama-kimliği>/{events|commands|interactions}`.

| # | Adım | Beklenen |
|---|---|---|
| 1 | Kaydet | Slack'te `url_verification` başarılı (Event Subscriptions "Verified") |
| 2 | Entegrasyonlar › Test | Size DM: "HR360 bu hesaba bildirim gönderebiliyor" |
| 3 | Slack'te bota "ben" yazın (bağlı değilken) → gelen tek kullanımlık bağlantıyla HR360'a girip onaylayın | Kimlik bağlanır; "Bağlı kullanıcılar" artar |
| 4 | Ayşe (sandbox) izin talebi açar | Yöneticinin Slack DM'inde Onayla/Reddet düğmeli kart |
| 5 | Karttan "Onayla" | HR360'ta talep onaylı; kart "Onaylandı" olarak güncellenir; talep sahibine DM |
| 6 | Signing Secret'ı bozun, tekrar deneyin | Slack isteği 401 ile reddedilir (imza doğrulaması) |
| 7 | KVKK › Yurt dışı aktarım kaydını kaldırıp kaydedin | `kvkk_transfer` hatası |

## Microsoft Teams

**Ayarla:** Microsoft 365 Developer Program kiracısı. Entra ID › App registrations (tek kiracı) + Azure Bot
kaynağı; mesajlaşma adresi `https://<alan>/api/governance/chat/teams/<uygulama-kimliği>/messages`. HR360'a App ID,
parola ve dizin kimliğini girin; "Teams paketi"ni indirip Teams Admin Center'dan yükleyin.

| # | Adım | Beklenen |
|---|---|---|
| 1 | Bot'a Teams'de "merhaba" | Yardım kartı |
| 2 | Onay akışı (Slack 4–5 gibi) | Adaptive Card ile onay; kart güncellenir |
| 3 | Bot Framework jetonu olmadan POST | 401 (JWT doğrulaması) |

## Google Workspace — Takvim ve Meet

**Ayarla:** Google Workspace deneme sürümü. Cloud Console › Google Calendar API etkin; OAuth consent screen
"Internal"; OAuth client (Web) yönlendirme adresi `https://<alan>/api/governance/calendar/oauth/callback/google`.

| # | Adım | Beklenen |
|---|---|---|
| 1 | Entegrasyonlar › Takvim › Google › Kur | Kaydedilir; `integration-check.sh` → OK |
| 2 | Profil › Takvim bağla (çalışan) | Google onay ekranı → HR360'a dönüş, bağlantı "Etkin" |
| 3 | İzin onayla | Google Takvim'de tüm gün "dışarıda" etkinliği; iptalde silinir |
| 4 | 1:1 / mülakat › Google Meet | Davet + Meet bağlantısı; katılımcılarda görünür |
| 5 | Boş saat bul | Yalnızca dolu aralıklar (başlık yok) |

## Google Workspace — hesap açma/kapatma (Admin SDK)

**Ayarla:** `.env` `ACCOUNT_PROVISIONING_ENABLED=true` → governance-service yeniden başlat. Cloud Console ›
"Admin SDK API" etkin; servis hesabı + JSON anahtar; Google Admin › Security › API controls ›
Domain-wide delegation: servis hesabının istemci kimliğine `https://www.googleapis.com/auth/admin.directory.user`.
HR360 › Entegrasyonlar › Hesap açma/kapatma › Google: alan adı, süper yönetici e-postası, JSON anahtar,
isteğe bağlı kuruluş birimi (sandbox için ayrı bir OU, ör. `/HR360-Test`).

| # | Adım | Beklenen |
|---|---|---|
| 1 | Bağlantıyı test et | "Bağlantı başarılı" (JWT → jeton → kullanıcı listesi) |
| 2 | İşe girişi 2 hafta sonra olan test çalışanı oluştur › Şimdi tara | "1 açma isteği" (Onay bekliyor), adres `ad.soyad@alan` |
| 3 | Aynı İK kullanıcısıyla elle istek açıp onaylamayı dene | 403 (dört göz) |
| 4 | Başka İK yöneticisi › Onayla ve aç | Admin Console'da kullanıcı, OU doğru; geçici parola bir kez gösterilir; ilk girişte değişim istenir |
| 5 | Çalışanı "Ayrıldı" yap › Şimdi tara › Onayla | Admin Console'da kullanıcı **askıda**, oturumları kapalı; veri silinmez |
| 6 | Denetim kaydı (`audit_log`, EntityType `AccountProvisioning`) | Requested/Approved/AccountCreated/AccountSuspended; parola yok |
| 7 | Süper yönetici yerine sıradan kullanıcı girin › test | 401/403 `unauthorized_client` ya da "Not Authorized" iletisi |

Not: lisans ataması, grup üyeliği ve e-posta yönlendirme yapılmaz; hesap silinmez (yalnızca askıya alınır).

## Microsoft 365 — Outlook takvimi ve Teams toplantısı

**Ayarla:** Developer Program kiracısı. App registration; yönlendirme `https://<alan>/api/governance/calendar/oauth/callback/microsoft`;
Delegated: `User.Read`, `Calendars.ReadWrite`, `offline_access` (admin consent).

| # | Adım | Beklenen |
|---|---|---|
| 1 | Kur › `integration-check.sh` | OK; yanlış secret'ta "reddedildi" |
| 2 | Takvim bağla, izin onayla | Outlook'ta "Dışarıda" etkinliği |
| 3 | Toplantı › Teams | Teams bağlantılı davet |

## Microsoft 365 — hesap açma/kapatma (Graph)

**Ayarla:** App registration › Application permission `User.ReadWrite.All` (admin consent), client secret.
HR360: uygulama kimliği, dizin kimliği (GUID), secret, kullanım konumu (TR).

| # | Adım | Beklenen |
|---|---|---|
| 1 | Bağlantıyı test et | "Bağlantı başarılı" (istemci kimlik bilgileri → `GET /users?$top=1`) |
| 2 | Açma isteğini onayla | Entra ID'de kullanıcı (UPN = iş adresi, usageLocation=TR), ilk girişte parola değişimi |
| 3 | Var olan UPN ile tekrar | İstek "Başarısız": "… adresiyle bir hesap zaten var" |
| 4 | Askıya alma | `accountEnabled=false`, oturumlar iptal (`revokeSignInSessions`) |

## Zoom

**Ayarla:** marketplace.zoom.us › Server-to-Server OAuth; kapsamlar `meeting:write:meeting:admin`,
`meeting:delete:meeting:admin`, `user:read:user:admin`; Account ID, Client ID/Secret.

| # | Adım | Beklenen |
|---|---|---|
| 1 | Kaydet | Kaydederken jeton alınır; yanlış bilgide hata iletisi |
| 2 | 1:1 › Zoom | Düzenleyicinin Zoom hesabında toplantı; HR360'ta katılım bağlantısı |
| 3 | Toplantıyı iptal | Zoom'da silinir |

## SMTP (e-posta)

**Ayarla:** platform: `scripts/smtp.sh set` (Keycloak + bildirim servisi). Şirkete özel (Enterprise):
Ayarlar › Marka › SMTP. Sandbox için Mailpit (`scripts/smtp.sh mailpit`) ya da sağlayıcının test hesabı.

| # | Adım | Beklenen |
|---|---|---|
| 1 | `scripts/smtp.sh test alici@…` | Deneme e-postası gelir |
| 2 | `integration-check.sh` | Özel SMTP varsa "bağlantı ve kimlik doğrulama başarılı" |
| 3 | Yanlış parola | "SMTP kimlik doğrulaması başarısız" |
| 4 | Davet / parola sıfırlama | Keycloak e-postası, şirket dili (TR/EN) |
| 5 | SPF/DKIM | Alıcıda `spf=pass dkim=pass` |

## Web Push

**Ayarla:** HTTPS zorunlu (localhost hariç). VAPID anahtarı ilk açılışta üretilir (şifreli).

| # | Adım | Beklenen |
|---|---|---|
| 1 | Profil › Bu cihaz › Anlık bildirim aç | Tarayıcı izni; abonelik listede |
| 2 | Deneme bildirimi (`--send`) | Cihazda "Deneme bildirimi" |
| 3 | iOS (16.4+) | Yalnızca ana ekrana eklenen PWA'da çalışır |
| 4 | Aboneliği tarayıcıdan sil | Sonraki gönderimde 410 → abonelik otomatik kaldırılır |

## SCIM 2.0 (Entra ID, Okta)

**Ayarla:** Entegrasyonlar › Dizin › SCIM jetonu üret (bir kez gösterilir). Entra ID › Enterprise application ›
Provisioning: Tenant URL `https://<alan>/api/tenant/scim/v2`, Secret Token = jeton. Okta: SCIM 2.0 uygulaması.

| # | Adım | Beklenen |
|---|---|---|
| 1 | "Test Connection" | Başarılı; `SCIM_TOKEN=… integration-check.sh` OK |
| 2 | Kullanıcı ata › provision on demand | HR360'ta hesap + çalışan kaydı (yalnızca izinli nitelikler) |
| 3 | Kullanıcıyı devre dışı bırak | HR360 hesabı kapanır |
| 4 | Jetonu iptal et | 401 |

## LDAP / Active Directory

**Ayarla:** Ayarlar › Dizin › LDAP: `ldaps://` adresi, salt okunur bağlama hesabı, base DN, filtre.

| # | Adım | Beklenen |
|---|---|---|
| 1 | Bağlantıyı test et / `integration-check.sh` | Kayıt sayısı ve örnek; `ldap://` için uyarı |
| 2 | Eşitleme › Kuru çalıştırma | Plan raporu; hiçbir şey değişmez |
| 3 | Gerçek eşitleme | Yeni kullanıcılar oluşur; dizinde kapatılanlar kapanır (toplu kapatma koruması) |

## LLM (isteğe bağlı)

**Ayarla:** `.env` `LLM_PROVIDER/LLM_MODEL/LLM_API_KEY`; kiracı yöneticisi AI araçları ekranından açar. Yerel
model (ollama) veri yurt dışına çıkarmaz.

| # | Adım | Beklenen |
|---|---|---|
| 1 | `integration-check.sh` | Sağlayıcı/model; kiracıda açık/kapalı |
| 2 | İlan taslağı oluştur | Yanıt; saatlik kota sayacı artar |
| 3 | Kişisel veri izni kapalıyken kişisel veri içeren araç (ör. performans özeti) | 403 `llm_personal_data`; modele istek gitmez |

## Webhook / REST hook (n8n, Zapier)

| # | Adım | Beklenen |
|---|---|---|
| 1 | Test alıcısı oluştur, olay seç | Teslimat listesinde HTTP 2xx, `X-HR360-Signature` başlığı |
| 2 | Alıcıyı 500 döndürecek şekilde boz | Teslimat hataları sekmesinde yeniden deneme zamanları; elle yeniden gönder |
| 3 | `integration-check.sh --send` | Her etkin webhook için ping sonucu |

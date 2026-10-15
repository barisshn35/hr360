# HR360 kullanım kılavuzu: modüller ve özellikler

Bu belge HR360'ın modüllerini ve yapılandırma gerektiren özelliklerini ayrıntılı anlatır. Kurulum,
güncelleme ve yedekleme için ana [README](../../README.md)'ye, sunucu işletimi (izleme, alarmlar,
yönetim komutları) için [İşletim](../isletim/README.md) belgesine bakın.

Panel Türkçe ve İngilizce kullanılabilir (üst çubukta **EN / TR**); ayrıntı: [docs/dil](../dil/README.md).

## Modüller

### Takvimler ve toplantılar (Google, Microsoft 365, Zoom)

- **Takvim bağlama:** Çalışan Profil › Takvim'den Google Takvim ya da Outlook
  (Microsoft 365) hesabını bağlar (OAuth 2.0 + PKCE). Onaylanan izinleri anında
  takvime "dışarıda" etkinliği olarak yazılır; bağlantı kurulunca gelecekteki izinler
  de eklenir. Takvim bağlamayanlar için .ics aboneliği her takvimle çalışır.
- **Toplantı:** 1:1 ve mülakat ekranlarında tek tıkla **Zoom**, **Microsoft Teams**
  ya da **Google Meet** bağlantısı; katılımcılara (istenirse adaya) takvim daveti
  gider, iptal edilince toplantı ve davetler silinir.
- **Uygun saat:** Yönetici 1:1 planlarken herkesin boş olduğu iş saatleri önerilir;
  bağlı takvimlerden yalnızca dolu aralıklar okunur (başlık görülmez), HR360'taki
  izin ve 1:1'ler de hesaba katılır.
- **Kurulum:** Entegrasyonlar › Takvim ve toplantı'da Google (OAuth istemcisi),
  Microsoft (Entra ID uygulama kaydı) ve Zoom (Server-to-Server OAuth) bilgileri
  girilir; adım adım yönerge ekrandadır. Google ve Microsoft yönlendirme adresi
  için HTTPS gerekir.
- Uçtan uca test (sahte Google/Graph/Zoom ile): `tests/integration/test_calendar.py`.

### Bordro, fazla mesai ve giriş-çıkış

- **Bordro dönemi** (Ücret › Bordro, `/panel/bordro`): ay açılır, hesaplanır, kontrol edilip
  kapatılır. Brütten nete: SGK işçi %14 ve işsizlik %1 (tavan asgari ücretin 9 katı), kümülatif
  matrahla gelir vergisi (2026 ücret tarifesi 190 bin / 400 bin / 1,5 milyon / 5,3 milyon),
  asgari ücret GV ve damga istisnası, işveren SGK %21,75 − teşvik puanı. Girdiler: onaylı
  ücretsiz izin günleri (eksik gün), **yalnızca onaylı** fazla mesai (saatlik ücretin %150'si)
  ve döneme özel ek ödeme/kesintiler. Parametreler yıl başına değiştirilebilir. Kapanan dönem
  değiştirilemez; yeniden açmayı yalnızca kiracı yöneticisi gerekçeyle yapar (denetim kaydına
  yazılır). Doğrulama: 2026 asgari ücret neti 28.075,50 TL, işveren maliyeti 40.214,03 TL.
- **Bordrolarım** (`/panel/bordrolarim`): çalışan, dönem kapanınca kendi pusulasını görür ve
  yazdırır/PDF alır. KVKK: pusulayı yalnızca çalışan ve bordro yetkilisi görür, İK'nın listeyi
  açması erişim kaydına yazılır, pusulalar 10 yıl saklanıp imha edilir.
- **Fazla mesai** (Puantaj sayfası): talep onay akışına gider; günde en fazla 4 saat, yılda
  270 saat (İş Kanunu m.41) aşılamaz. Gerekçede sağlık bilgisi istenmez.
- **Giriş-çıkış** (`/panel/giris-cikis`, yönetim `/panel/giris-cikis/yonetim`): ofisteki
  kiosk ekranında dakikada bir değişen QR kod, kart okuyucu ya da sicil kodu + PIN terminali
  (`POST /api/timeshift/time-clock/terminal/punch`, `X-Device-Key`). **Biyometri yoktur**
  (KVKK Kurulu 2026/921). Konum denetimi nokta başına isteğe bağlıdır; koordinat yalnızca o
  istekte "noktada mı" hesabında kullanılır, saklanmaz. Kart numarası ve PIN özet olarak
  tutulur; 5 hatalı PIN'de 15 dakika kilit.
- **Bordro dosyaları** (kapanmış dönem sayfası): SGK aylık prim ve hizmet belgesi (XML taslağı,
  eksik gün nedeni, işe giriş/çıkış günü), SGK işe giriş/ayrılış listesi, banka toplu maaş ödeme
  dosyası (CSV) ve muhasebe fişi (genel/Logo/Mikro/Netsis sütunları, masraf merkezi özeti; kişi
  verisi içermez). Dosyalar şifreli saklanır, her indirme kaydedilir; banka dosyası bir kez
  indirilir, tümü 24 saatte silinir. SGK XML'i ve muhasebe şablonları SGK'nın/yazılımların güncel
  şablonuyla birebir doğrulanmadı; yüklemeden önce karşılaştırın.
- **Türkiye bordrosu** — ayrıntı: [docs/bordro](../bordro/README.md). Yıl/yarıyıl yürürlüklü
  bordro parametreleri (asgari ücret, SGK/işsizlik oranları, tavan, damga, dilimler, istisna, kıdem
  tavanı; 2026 tohumu "doğrulanmadı"), APHB XML/TXT + indirmeden önce doğrulama raporu (meslek kodu,
  eksik gün nedeni eşlemesi, belge türü/kanun, SGDP), banka örnek şablonları ve özel düzen (IBAN
  mod-97, toplam satırı, dosya özeti denetim kaydında), şirket hesap planı ve masraf merkezi eşlemesi
  (dengesiz fiş üretilmez), fark bordrosu (kapanmış dönem açılmadan "Fark: YYYY/AA"), kıdem/ihbar
  (giydirilmiş ücret, ihbar GV'si, dört göz onayı, ibraname), e-bordro (yayım, okundu, "Okudum, teslim
  aldım"), bant uyumu ve compa-ratio. SGK, banka ve e-posta sağlayıcısına bağlanılmaz: dosya üretilir,
  e-posta bildirimi notification kuyruğuna yazılır (test ortamında mailpit).
- **Avans ve borç** (Bordrolarım › Avans ve borçlarım; İK: Bordro › Avanslar): çalışan talep eder,
  İK onaylar; taksitler dönem hesabında kesinti olarak otomatik eklenir, dönem kapanınca ödenmiş
  sayılır. Taksit brüt ücretin %25'ini aşarsa İK uyarı görür.
- **Esnek yan haklar** (`/panel/yan-haklar`): yıllık bütçe ve seçim penceresi; kategori başına bir
  seçenek. Özel sağlık sigortası için sağlık beyanı istenmez.
- **Zam dönemi** (`/panel/zam-donemi`): İK bütçe yüzdesiyle dönemi açar, bölüm yöneticileri kendi
  ekipleri için öneri yapar (bant konumu ve bant dışı uyarısıyla), İK onaylar ve yürürlük
  tarihiyle yeni ücret kaydına dönüştürür. Bölüm özetinde 5 kişiden az gruplar gizlenir; çalışma
  sayfasının her açılışı erişim kaydına yazılır.
- **Masraf**: yabancı para kalemleri harcama günündeki TCMB döviz alış kuruyla TL'ye çevrilir
  (hafta sonu önceki iş günü; İK elle kur girebilir), kilometre masrafı km × şirket km ücreti,
  kategori başına kalem/aylık limit ve fiş zorunluluğu onaya gönderirken denetlenir. **Fiş okuma**
  yereldir (ml-inference içindeki Tesseract): görüntü saklanmaz, yalnızca tutar/tarih/VKN önerisi
  döner.
- **Seyahat** (`/panel/seyahat`): talep onay akışına gider, harcırah gün × şirket oranıyla hesaplanır
  ve onaydan sonra tek tıkla masraf beyanına dönüşür. Pasaport no yalnızca yurt dışı seyahatte,
  şifreli tutulur; seyahat bitiminden 7 gün sonra (iptal/retle hemen) silinir.

### Belge talebi, onay akışları ve vekâlet

- **Belge talebi** (`/panel/belge-talebi`): çalışma belgesi, maaş yazısı gibi şablonları
  çalışan kendisi ister; İK şablonda "çalışan talep edebilir" ve "İK onayı gerekir" seçer.
  Düzenlenen belge şifreli saklanır, altında doğrulama kodu bulunur. `/belge-dogrula/<kod>`
  oturumsuz doğrular; kişisel veri göstermez (belge türü, tarih, baş harfler).
- **Onay akışları** (`/panel/onay-akislari`): talep türüne göre çok adımlı zincir — bölüm başı,
  üst bölüm başı, belirli kişi; gün/tutar/saat koşulları; adım başına karar süresi. KVKK:
  belirli kişiye giden adım için uyarı, onaycılardan gizlenecek alanlar (ör. izin gerekçesi).
- **Vekâlet** (Onay kutusu › Vekâlet): tarih aralığında onaylar vekile geçer; vekil yalnızca
  kendisine düşen kaydı görür, süre bitince ya da geri alınınca adımlar asıl onaycıya döner;
  vekilin kararı geçmişte "(vekâleten)" diye görünür.
- **Toplu onay** (Onay kutusunda seçip), **süre aşımında üst yöneticiye iletme** ve
  **e-postadan tek tıkla karar**: onaycı e-postasında tek kullanımlık (72 saat) bağlantı;
  sayfa ayrıca onay ister, böylece e-posta tarayıcıları karar veremez. E-postada talep konusu
  yazmaz (yalnızca tür ve talep eden).
- **İzin**: saatlik izin (tek gün, 0,5 saat adımlarla; gün = saat / 7,5, `LEAVE_DAY_HOURS`),
  kıdeme ve yaşa göre yasal yıllık izin hakkı (İş Kanunu m.53) ön izleme ve bakiyelere yazma,
  kullanılmayan iznin sonraki yıla devri (isteğe bağlı üst sınır; tekrar çalıştırılabilir).

### İK süreçleri (duyuru, doküman, etik, İSG, disiplin, işe alım, eğitim, operasyon)

- **Duyurular** (`/panel/duyurular`) ve **doküman kütüphanesi** (`/panel/belgeler-kutuphanesi`): hedef kitleye göre
  yayın, "Okudum" ve politika kabulü (açık rızadan ayrı kayıt; yeni sürüm yeniden kabul ister), sürümlü
  belgeler ve yetki süzgeçli tam metin arama.
- **Etik hattı**: `/etik/<şirket>` oturumsuz ve anonimdir (IP, kullanıcı, tarayıcı bilgisi tutulmaz; gateway bu yolu
  kaydetmez), takip kodu yalnızca özet olarak saklanır; bildirimleri yalnızca şirketin atadığı etik kurulu görür.
- **İSG** (`/panel/isg`): iş kazası/ramak kala (SGK'ya 3 iş günü bildirim süresi), periyodik muayene (sağlık notları
  şifreli, yalnızca işyeri hekimi rolü — Ayarlar › Roller'den `osh:physician` ek izni), İSG eğitimleri.
  İSG rolleri otomatik atanmaz; şirket yöneticisi **Ayarlar › Roller** sayfasında kişiye ek izin olarak verir:
  `osh:physician` (işyeri hekimi: muayene ve sağlık notlarını görür/girer) ve `osh:specialist` (iş güvenliği uzmanı:
  kaza/ramak kala ve İSG eğitimlerini yönetir, sağlık notlarını göremez). İK yöneticisi rolü sağlık notlarını göremez.
- **Disiplin** (`/panel/disiplin`): savunma istemi (en az 2 iş günü), tutanak, karar; çalışan kendi savunmasını
  uygulamadan verir; adli sicil bilgisi tutulmaz (uyarı), kapanıştan 24 ay sonra imha.
- **İşe alım**: herkese açık kariyer sayfası `/kariyer/<şirket>` (aydınlatma bilgi olarak, CV havuzu için ayrı açık
  rıza, adayın kendi kaydını görüp silebildiği tek kullanımlık bağlantı, süre dolunca anonimleştirme), mükerrer aday
  tespiti, sürükle-bırak aday hattı, mülakat değerlendirme kartı (özel nitelikli veri uyarısı) ve randevu çakışma
  denetimi, teklif mektubu + onay akışı (maaş bildirimlerde yer almaz).
- **Eğitim ve gelişim**: yetkinlik matrisi ve açık analizi (yalnızca öneri), video/metin/sınav/SCORM 1.2 içerik,
  sertifika ve doğrulama kodu, sertifika süresi hatırlatmaları; performansta 9-kutu (tartışma aracı; potansiyel
  puanı varsayılan olarak çalışana gizli, kalibrasyon gerekçeli ve kayıtlı) ve dönem şablonları.
- **Operasyon**: rol bazlı onboarding şablonları, buddy ve ilk gün karşılama mesajı; işten ayrılışta Keycloak hesabının
  kapatılması, zimmet iade kontrolü ve planlı anonimleştirme tarihi; zimmet QR etiketi, iade hatırlatma, bakım
  kaydı; anketlerde en az 5 yanıt eşiği, eNPS eğilimi ve yerel (sunucu dışına çıkmayan) duygu analizi; vardiya
  tercihleri ve takas (11 saat dinlenme, haftalık 45 saat); giriş-çıkıştan geç kalma/fazla mesai raporu (bilgi amaçlı).

### Mobil uygulama (PWA)

Uygulama telefona kurulur (Hesap menüsü › Uygulama olarak yükle). **Profilim › Güvenlik ›
Bu cihaz** ekranından anlık bildirim açılır: Web Push (VAPID, RFC 8291 şifreleme; harici
servis yok). Bildirimde kişisel veri yer almaz ("Yeni bir bildiriminiz var"). İnternet yokken
izin ve fazla mesai talepleri cihazda sıraya alınır, bağlantı gelince gönderilir. Oturum
kapatılınca bu cihazın aboneliği ve sıradaki talepler silinir.

### Slack ve Microsoft Teams'ten onay

Onaycıya izin, masraf ve diğer talepler **Onayla / Reddet** düğmeli kişisel mesaj
olarak gider; karar verilince mesaj güncellenir, talep sahibine sonuç bildirilir.
Komutlar (Slack'te `/hr360 …` ya da bota DM, Teams'te bota mesaj): `onaylarım`,
`bakiye`, `izindekiler`, `kimnerede`, `bekleyen`, `ben`, `izin al`, `özet`, `yardım`.
Komut dışındaki her soru ("kaç gün iznim kaldı?", "masraf politikası ne?") web'deki
İK asistanına gider ve aynı yetki kurallarıyla yanıtlanır. Dil, kişinin HR360 bildirim
tercihine göre seçilir; İngilizce komutlar da çalışır (`approvals`, `balance`, `leave`,
`home`, `help`).

- **İzin formu:** `izin al` Slack'te bir form (modal), Teams'te Adaptive Card açar; talep
  sohbetten oluşturulur, onay akışı web'deki gibi başlar.
- **Gerekçeli ret:** Reddet'e basınca gerekçe sorulur; gerekçe onay geçmişine yazılır,
  talep sahibine giden sohbet mesajında yer almaz (yalnızca "karar notu var" denir).
- **Ana sayfa:** Slack App Home sekmesi ve `özet` komutu bekleyen onayları, izin
  bakiyesini ve bugün izinde olan ekip arkadaşlarını gösterir.
- **Sabah özeti:** İş günleri `CHAT_DIGEST_HOUR` saatinde
  (varsayılan 09:00) bekleyen onaylar ve bugünün özeti kişiye gönderilir; uygulama ayarından kapatılabilir.

- Kişiler **e-posta adresiyle** eşleşir, ama e-posta eşleşmesi tek başına yetmez:
  bot ilk kez yazan ya da onay alacak kişiye tek kullanımlık bir bağlantı gönderir; kişi
  HR360'a giriş yapıp "Bu hesap benim" diyene kadar ona talep içeriği gitmez. E-postalar
  uyuşmazsa bağlama reddedilir. Bağlı hesaplar **Profilim › Güvenlik**'te görünür ve
  kaldırılabilir; yönetici de bağı kaldırabilir. Uygulama ayarından kapatılabilir.
- **KVKK veri en aza indirme:** Varsayılan "Az" ayrıntıda onay mesajında talep edenin
  soyadı kısaltılır, talep konusu yazılmaz. `izindekiler` yöneticiye yalnızca kendi
  ekibini, diğerlerine yalnızca sayıyı gösterir. Teams'te grup sohbetine yazılan komutun
  yanıtı kişiye özel sohbete gider. Ayrılan çalışanın sohbet erişimi ilk istekte kapanır.
- Gönderilemeyen mesajlar kuyruğa alınır ve 1, 5, 15, 60 ve 180 dakika sonra yeniden
  denenir; talep bu arada karara bağlandıysa gönderilmez.
- Karar workflow-service'te web arayüzüyle **aynı kurallarla** yetkilendirilir:
  yalnızca adımın onaycısı ya da vekili karar verebilir, kimse kendi talebini
  onaylayamaz. Servisler arası çağrı `INTERNAL_SERVICE_TOKEN` ile korunur, gateway
  `/api/*/internal/` yollarını dışarıya kapatır.
- Slack istekleri imzayla (HMAC, 5 dk penceresi), Teams istekleri Bot Framework
  JWT'siyle (imza, yayıncı, hedef kitle, `serviceUrl` ve Microsoft 365 kiracısı)
  doğrulanır. Bot jetonları ve gizli anahtarlar veritabanında AES-256-GCM ile
  şifreli tutulur.
- Gerçek Slack/Teams hesabı olmadan uçtan uca test: `tests/integration/chatmock.py`
  ve `tests/integration/test_chat.py`.

**Ek bot özellikleri.** Mattermost ve Rocket.Chat (kendi sunucunuzda; bulut adresinde
KVKK aktarım kaydı gerekir), fiş fotoğrafından taslak masraf (yerel OCR, görüntü saklanmaz),
`izin iptal`, `teşekkür @kişi mesaj`, `masa [tarih]`, `vardiyam` / `takas`, `geldim` / `çıktım`,
`duyurular` ("Okudum" düğmesi), `belge` (yalnızca bağlantı), `bordrom` ve toplu/ücretle ilgili
onaylar için HR360'ta ek doğrulama (`/panel/sohbet-onay`, ya da panelde gösterilen kod: `kod 123456`),
`onaylarım` › "Tümünü onayla", işe başlama mesajları (0/1/3/7. gün, onboarding arkadaşı),
izin verenlerin doğum günü / iş yıldönümü (yaş yazılmaz), anonim nabız anketi (≥ 5 yanıtla sonuç),
birebir hatırlatmaları (gündem içeriği değil sayısı), ayrılanlara çıkış anketi (yalnızca İK),
yöneticinin ekip düzeyi rapor soruları, yazım hatasına dayanıklı komutlar, düğmeli yardım,
30 günlük şifreli konuşma bağlamı (`geçmişimi sil`), iki kez anlaşılamayınca İK vakası önerisi.
Sessiz saatlere uyulur (kritik olmayan mesaj ertelenir), kişi/kiracı başına hız sınırı
(`CHAT_RATE_USER`, `CHAT_RATE_TENANT`, `CHAT_RATE_WINDOW_SECONDS`), eski düğmeler süresi dolunca
nazik mesaj verir. Entegrasyonlar › Sohbet uygulamaları › **Bot yönetimi**: komut aç/kapat,
kullanım sayıları, son hatalar, nabız planı. Zamanlanmış işler `CHAT_JOBS_SECONDS` (varsayılan
300); fiş okuma `OCR_CLIENT_SECRET` (ml-inference hizmet hesabı) ister. Prometheus ölçümleri
`hr360_chat_*`, Grafana panosu `deploy/monitoring/grafana/dashboards/hr360-chatbot.json`.
Test: `tests/integration/test_chat_plus.py` (test_chat.py ile aynı anda çalıştırmayın).

### Zapier ve n8n (REST hook)

Webhook'lar ve açık API (`/api/governance/public/v1`, OpenAPI: `.../openapi.json`)
Zapier ve n8n ile kullanılabilir. **Entegrasyonlar › API anahtarları**'ndan
`hooks:write` (ve okuma için `employees:read` vb.) yetkili bir anahtar açın; istekler
`X-Api-Key` başlığıyla gönderilir.

| Uç | Ne yapar |
|---|---|
| `POST /hooks` `{"target_url": "...", "event": "leave.approved"}` | Abone olur (Zapier `hookUrl`, `url` de kabul edilir). Yanıtta `id` ve imza anahtarı (`signingSecret`, yalnızca bir kez) döner. |
| `DELETE /hooks/{id}` | Aboneliği kaldırır (Zapier "unsubscribe"). |
| `GET /hooks/samples/{event}` | Olayın **sentetik** örnek yükü (Zapier "perform list", n8n alan eşleme); gerçek kişi verisi dönmez. |
| `GET /hooks/events`, `GET /hooks` | Abone olunabilen olaylar ve mevcut abonelikler. |

Teslimat mevcut webhook altyapısıyla yapılır (HMAC-SHA256 imzası `X-HR360-Signature`,
teslim geçmişi, 20 ardışık hatada uç kapanır); yük, İK webhook'larıyla aynı olay zarfıdır, ek kişisel
veri eklenmez.

- **Zapier** (ABD) ve **n8n Cloud** (`*.n8n.cloud`, AB) yurt dışı aktarımdır: KVKK ›
  Yurt dışı aktarım ekranında dayanak kaydı yoksa abonelik `409 transfer_basis_required`
  ile reddedilir; kayıt sonradan silinirse teslimat da durur.
- **Kendi sunucunuzdaki n8n** (iç ağ adresi ya da kendi alan adınız) aktarım sayılmaz,
  ek kayıt gerekmez. n8n'de *Webhook* düğümünün üretim adresini `target_url` olarak
  verin (ör. `http://n8n:5678/webhook/izin-onay`; HR360 ile aynı Docker ağında servis
  adıyla) ya da abone olmadan İK › Webhook'lar ekranından aynı adresi ekleyin. Veri
  çekmek için n8n *HTTP Request* düğümüyle `GET /employees`, `/leaves`, `/events`
  uçlarını `X-Api-Key` başlığıyla çağırın.

Ana İK süreçleri (çalışan, organizasyon, izin, onay akışı, vardiya/puantaj,
performans, eğitim, ücret, masraf, işe alım, işe giriş, bildirimler) dışında:

| Alan | Neler var |
|---|---|
| Çalışan deneyimi | Takdir duvarı (rozet, beğeni, liderlik tablosu), doğum günü ve iş yıl dönümü kutlamaları, self-servis profil (IBAN/TCKN doğrulama ve maskeleme, acil durum kişisi), yetenek dizini, ofis/masa rezervasyonu ve "kim nerede", mentorluk eşleştirme, iç ilan panosu, anket ve eNPS (3 yanıttan az grupta sonuç gösterilmez), ekran paylaşım modu (hassas alanlar bulanıklaşır), PWA olarak kurulum |
| Yönetici | Ekip sağlığı paneli (uzun süre izin kullanmama, fazla mesai, performans düşüşü, 1:1 aralığı ve ruh hâli işaretleri), 1:1 defteri (yöneticiye özel notlar, takvime ekleme), org senaryo planlama (taşıma/ekleme/çıkarma ve maliyet etkisi), ardıl planlama |
| İK ve uyum | Denetim kaydı ekranı (filtre, aynı istekte yapılanlar, CSV), KVKK (uyum durumu, işleme envanteri, yurt dışı aktarım kilidi, açık rıza, veri sahibi talepleri, otomatik analize itiraz, TCKN/IBAN şifreleme ve gerekçeli erişim kaydı, JSON dışa aktarım, anonimleştirme, saklama süreleri ve imha tutanağı; bkz. [docs/kvkk](../kvkk/README.md)), offboarding (kontrol listesi, zimmet iadesi, çıkış görüşmesi, kıdem/ihbar/izin hesabı), belge şablonları ve toplu yazdırma, kural motoru (olay → koşul → bildirim/webhook/Slack), toplu içe/dışa aktarım (Excel/CSV), bordro simülasyonu (2026 parametreleriyle brütten nete/netten brüte), işe alım saga'sı (teklif → çalışan kaydı → işe giriş planı) |
| İçgörü | Analitik (veritabanındaki `analytics_*` görünümlerinden), doğal dilde rapor ("son 6 ayda departmanlara göre izin günleri"), zaman makinesi (geçmiş bir tarihteki org yapısı), canlı olay radarı, İK asistanı (bilgi bankası + kendi verileriniz) |
| Entegrasyon | Webhook (HMAC imzalı, teslim geçmişi), API anahtarları ve açık REST API (`/api/governance/public/v1`, OpenAPI tanımı, dakikada 120 istek), Slack/Teams gelen webhook ve Slack slash komutu, kişisel takvim aboneliği (.ics), Google/Microsoft ile SSO (Keycloak Organizations), TOTP ile iki adımlı doğrulama zorunluluğu |
| Sunum | Org şeması sunum modu (tam ekran, klavye ile gezinme) |

### AI araçları hakkında

"AI" etiketli özelliklerin hiçbiri büyük dil modeli (LLM) kullanmaz ve dışarıya
veri göndermez. Hepsi `ml-inference` içinde kural tabanlı ve istatistiksel
yöntemlerle çalışır:

- **CV ayrıştırma:** PDF/DOCX/TXT'den metin çıkarma; e-posta, telefon, beceri,
  dil, eğitim ve deneyim yılı düzenli ifadeler ve sözlükle bulunur.
- **İlan yazıcı ve ayrımcı ifade uyarısı:** Şablonlu metin üretimi; yaş, cinsiyet,
  medeni durum, askerlik vb. ifadeler kural listesiyle işaretlenir.
- **Performans özeti, aday–ilan eşleşmesi, eğitim önerisi:** TF-IDF benzerliği ve
  ağırlıklı puanlama.
- **İzin tahmini:** Trend + mevsimsellik ile basit zaman serisi tahmini.
- **Doğal dilde rapor ve İK asistanı:** Türkçe anahtar kelime ayrıştırıcısı; tanınan
  kalıpları önceden yazılmış, kiracıya filtreli SQL şablonlarına çevirir. Serbest
  SQL üretilmez.

**İsteğe bağlı dil modeli (LLM).** `.env`'de `LLM_PROVIDER` (`anthropic`, `openai`
ya da yerel model için `ollama`), `LLM_MODEL` ve gerekiyorsa `LLM_API_KEY` tanımlanırsa
AI araçları ekranında ek seçenekler açılır: **yapay zekâ ile ilan yazma**, **kapsayıcı
dille yeniden yazma**, **performans özeti** ve bilgi bankasına dayalı **İK asistanı**.

- Kurulumda tanımlı olsa bile **şirketin İK yöneticisi açmadıkça hiçbir veri modele
  gönderilmez** (varsayılan kapalı).
- Kişisel veri içeren görevler (performans özeti) ayrıca izin ister; çalışanın adı
  modele gönderilmez, takma adla gönderilip yanıtta yerine konur.
- İK asistanında modele yalnızca bilgi bankası makaleleri gider; izin bakiyesi gibi
  kişisel sorular yine veritabanından, modelsiz yanıtlanır.
- Model çıktıları da ayrımcı ifade denetiminden geçer.
- İstek içerikleri kaydedilmez; görev, süre ve jeton sayısı tutulur. Şirket başına
  saatlik kota vardır (`LLM_HOURLY_LIMIT`).
- `ollama` ile model kendi sunucunuzda çalışır, veri kurum dışına çıkmaz.
- Uçtan uca test (sahte model ile): `tests/integration/test_ai_llm.py`.

### Yapılandırma gerektirenler

| Özellik | Durum |
|---|---|
| Slack uygulaması | Entegrasyonlar › Sohbet uygulamaları. HR360'ın verdiği manifestle Slack'te uygulama açılır; bot jetonu ve imzalama anahtarı girilir. Sunucunun **internetten HTTPS ile** erişilebilmesi gerekir. |
| Microsoft Teams botu | Azure'da tek kiracılı bir **Azure Bot** (App ID, gizli anahtar, kiracı kimliği) açılır; HR360'ın ürettiği Teams paketi yönetim merkezinden yüklenir. Teams, bota kişinin ilk mesajından sonra yazmaya izin verir; kullanıcı uygulamayı bir kez ekler. HTTPS gerekir. |
| Google / Microsoft 365 takvim | Google Cloud'da OAuth istemcisi, Entra ID'de uygulama kaydı (Calendars.ReadWrite); yönlendirme adresi ekranda verilir. HTTPS gerekir. |
| Zoom | Zoom Marketplace'te Server-to-Server OAuth uygulaması (Account ID, Client ID, Secret). |
| Teams/Slack kanal bildirimi | Gelen webhook adresi (Teams'te Workflows şablonu) Entegrasyonlar › Kanal bildirimleri'nden girilir. |
| SSO | Google veya Microsoft (Azure AD) OAuth istemci kimliği ve sırrı Güvenlik ekranından girilir; e-posta alan adı organizasyona bağlanır. |
| E-posta | Bildirimler ve parola linkleri için SMTP: Platform › Sistem ayarları (deneme e-postası dahil). Şirket kendi sunucusunu Ayarlar › Marka'dan girebilir. E-posta yoksa parola linkleri yöneticiye gösterilir (kopyala / QR). |

### SSS, gizlilik politikası, kullanım koşulları ve iletişim

Giriş sayfasının altındaki bağlantılar herkese açık **SSS**, **Gizlilik**, **Kullanım koşulları** ve
**İletişim** sayfalarını açar. Platform yöneticisi bu sayfaların içeriğini **Platform › Sistem
ayarları › Herkese açık site** ekranından düzenler: iletişim bilgileri, Markdown ile metinler
(Düzenle / Önizle, "Şablonu düzenlemek için yükle"), SSS soruları (ekle, sırala, sil) ve "Arama
motorlarında görünür" anahtarı (varsayılan kapalı). Boş bırakılan metin için varsayılan şablon
yayımlanır. Gizlilik sayfasının sonunda uygulamanın kullandığı çerezlerin tablosu her zaman yer alır.

### Beyaz etiketleme (şirkete özel marka)

Sistem çok kiracılıdır. Platformu kuran kişi (siz) platform sahibidir; platformu
kullanan her şirket (kiracı) kendi logosunu, ana rengini ve e-posta sunucusunu
tanımlayarak HR360'ın varsayılan markasının **yerine** kendi markasını gösterebilir.
Kod değiştirmeye gerek yoktur ve özellik her şirket için hazır gelir.

#### Kimler kullanabilir

| Koşul | Ayrıntı |
|---|---|
| Ayarı yapan kişi şirket yöneticisi olmalı | `tenant-admin` rolü ya da **Roller** sayfasından "şirket ayarlarını yönetme" (`tenant:manage`) ek izni verilmiş biri. Platform yöneticisi (`platform.admin`) bir şirkete üye olmadığı için bu ayarları yapamaz. |

Marka ayarları ve şirket adı **Ayarlar** sayfasındadır.

#### Neler değiştirilebilir

Hepsi uygulamada **Ayarlar** sayfasındadır (`apps/web/src/features/settings/BrandingPanel.tsx`).

| Ayar | Nerede etkili olur |
|---|---|
| **Logo** | Panelde sol menünün üstü ve şirketin çalışanlarına giden bildirim e-postalarının başlığı. |
| **Ana renk** | Panelin ana rengi: butonlar, vurgular. Yazı rengi okunur kalacak şekilde otomatik seçilir. E-posta şablonları HR360 renginde kalır. |
| **Kendi SMTP sunucusu** | Uygulamanın bildirim e-postaları (izin, onay, hoş geldiniz vb.) şirketin kendi sunucusundan, kendi gönderen adresiyle gider. Parola şifrelenerek saklanır. **Davet ve parola sıfırlama e-postaları** Keycloak'tan, platformun kurulumda girilen SMTP'si üzerinden gitmeye devam eder. |

#### Logo dosyası

- **Format:** PNG, JPEG veya WebP; en fazla **2 MB** (`LogoStorageService.cs`).
  **SVG kabul edilmez**: SVG içinde betik taşınabildiği ve logolar uygulamanın kendi
  alan adından sunulduğu için güvenlik nedeniyle kapatıldı. Dosya türü, dosya adına
  değil içeriğine bakılarak belirlenir.
- **Saklama:** MinIO'daki `tenant-logos` bucket'ı; gateway üzerinden `/logos/` yolundan sunulur.
- **Önerilen ebat:** Şeffaf arka planlı PNG ya da WebP. Kare logo için en az
  **256×256 px**, yatay (wordmark) logo için en az **512×160 px**. Sistem tek dosyayı
  her yerde otomatik küçültür.

| Kullanıldığı yer | Görüntülenen boyut | Kaynak |
|---|---|---|
| Üst menü | 32 px yükseklik, en fazla 72 px genişlik | `components/layout/TopNav.tsx` |
| E-posta başlığı | en fazla 36 px yükseklik, 220 px genişlik | `EmailTemplateRenderer.cs` |

Bildirim servisi şirketin logosunu, adını ve SMTP ayarını `tenant-service`'ten çeker
ve 5 dakika önbellekte tutar. Değişiklikler e-postalara en geç 5 dakikada yansır.

#### Markanın görünmediği yerler

- **Giriş ekranı** (`/giris` ve Keycloak giriş sayfası): Kullanıcı henüz giriş
  yapmadığı için hangi şirkete ait olduğu bilinmez; bu ekranlar platform markasıyla
  (HR360) kalır. İstisna: şirket kendi doğrulanmış alan adını kullanıyorsa (aşağıda
  "Özel alan adı") `/giris` ekranı o şirketin adını, logosunu ve rengini gösterir.
- **Platformun kendi markası** (HR360 adı, varsayılan logo ve renk) ayarlardan
  değiştirilemez; bunun için kod değişikliği gerekir: panel renkleri
  `apps/web/src/styles/index.css`, Keycloak giriş sayfası
  `deploy/keycloak/themes/hr360` klasöründedir.

#### Özel alan adı (ör. `ik.sirket.com.tr`)

Şirket yöneticisi **Güvenlik → Özel alan adı** panelinden kendi alan
adını ekler. Sahiplik DNS TXT kaydıyla kanıtlanır:

```
_hr360-verify.ik.sirket.com.tr.  TXT  "hr360-verify=<paneldeki değer>"
```

**Doğrula** düğmesi kaydı sorgular (DnsClient); eşleşirse durum `Pending` → `Verified` olur.
Bir alan adı aynı anda yalnızca tek şirkette doğrulanmış olabilir; platformun kendi adresi
(`PUBLIC_ORIGIN`, `PLATFORM_HOSTS`) ve alt alanları kullanılamaz. Doğrulanan alan adı
Keycloak'taki `hr360-web` istemcisinin yönlendirme adreslerine eklenir
(`CUSTOM_DOMAIN_KEYCLOAK_SYNC=false` ile kapatılabilir).

Giriş ekranı `GET /api/tenant/public/branding?host=<tarayıcının adresi>` ile şirketi tanır
(yalnızca doğrulanmış alan adı + etkin şirket; aksi halde 404) ve şirket adını, logosunu,
rengini gösterip girişte o şirketi ön seçer.

İşletme adımları (uygulama bunları **yapmaz**):

1. **DNS:** `ik.sirket.com.tr` için platform sunucusunun genel IP'sine `A`/`CNAME` kaydı.
2. **TLS sertifikası:** mevcut ACME betiğiyle alan adını sertifikaya ekleyin, örneğin
   `CERTBOT_EXTRA_ARGS="-d ik.sirket.com.tr --expand" ./scripts/tls.sh enable --letsencrypt --host hr.sirket.com`
   (ya da şirketin verdiği sertifikayı `--cert/--key` ile). Sertifika olmadan tarayıcı
   uyarı verir.
3. **Gateway:** nginx `server_name` yönergesi alan adını kabul etmelidir. Varsayılan
   yapılandırma (`server_name _;`) tüm adları kabul eder; kısıtlı bir yapılandırma
   kullanıyorsanız alan adını ekleyin.

### Dizin sağlama: SCIM 2.0 ve LDAP / Active Directory

Şirket yöneticisi **Güvenlik** sayfasındaki iki panelle çalışan hesaplarını kurumsal
dizinden yönetebilir:

- **SCIM 2.0** (Entra ID, Okta, OneLogin, JumpCloud...): taban adres
  `https://<platform>/api/tenant/scim/v2`, kimlik doğrulama panelde üretilen jeton
  (`Bearer hr360scim_...`; yalnızca bir kez gösterilir, veritabanında SHA-256 özeti tutulur,
  iptal edilebilir). Desteklenen: `ServiceProviderConfig`, `ResourceTypes`, `Schemas`,
  `Users` (liste, `filter=userName eq "..."`, `startIndex`/`count`, GET, POST, PUT, PATCH,
  DELETE). DELETE ve `active=false` hesabı kapatır ve tüm oturumları sonlandırır; çalışan
  kaydı silinmez (işten çıkış ve saklama süresi İK/KVKK süreciyle yürür).
- **LDAP / AD**: `ldaps://` önerilir (`ldap://` kaydedilir ama uyarı verilir); bağlama
  parolası AES-256-GCM ile şifrelenir. "Bağlantıyı test et", "Önizleme (dry-run)" ve
  "Şimdi eşitle"; isteğe bağlı otomatik eşitleme `DIRECTORY_SYNC_MINUTES` (varsayılan 60)
  dakikada bir. Dizinde olup HR360'ta olmayan kullanıcılar oluşturulur, dizinden silinen ya
  da pasifleştirilenler kapatılır (arama boş dönerse ya da aktiflerin yarısından fazlası
  kaybolursa onay olmadan toplu kapatma yapılmaz).

Her iki yolda da hesap Keycloak'ta şirket organizasyonuna `employee` rolüyle açılır ve
çalışan kaydı employee-service'in iç ucuyla (`INTERNAL_SERVICE_TOKEN`) oluşturulur. Roller
dizinden atanmaz. **KVKK veri minimizasyonu:** yalnızca kullanıcı adı, ad, soyad, birincil
e-posta, etkinlik durumu, unvan ve departman saklanır; telefon, adres, fotoğraf, yönetici
gibi diğer nitelikler kabul edilir ama yok sayılır (SCIM `ServiceProviderConfig` bunu ilan
eder; LDAP'tan bu nitelikler hiç istenmez).

### Basit elektronik imza (doküman imzalatma)

İK, **Dokümanlar** sayfasında bir özlük dokümanını çalışana **İmzaya gönder**ir. Çalışan
**İmzalarım** sayfasında belgeyi görür, 6 haneli tek kullanımlık kod ister (uygulama içi
bildirim ve e-posta) ve kodla onaylar. Kod yalnızca özetiyle saklanır, 10 dakika
geçerlidir, en fazla 5 deneme hakkı vardır. Kanıt kaydı: doküman özeti (SHA-256), imzalayan
çalışan, zaman, son okteti maskelenmiş IP, tarayıcı bilgisinin özeti ve kod kanalı;
doküman saklandığı sürece saklanır ve değiştirilemez. İmzalanan doküman değiştirilemez;
yeniden imza için yeni talep gerekir.

> **Basit elektronik imza — 5070 sayılı Kanun kapsamında nitelikli (güvenli) elektronik
> imza değildir.** Kanunen ıslak imza ya da güvenli e-imza gerektiren belgeler (ör. bazı iş
> sözleşmesi değişiklikleri, ibraname) için kullanılmamalıdır.

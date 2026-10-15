using Microsoft.EntityFrameworkCore;
using GovernanceService.Data;
using GovernanceService.Infrastructure.Ai;

namespace GovernanceService.Infrastructure;

/* ======================================================================
 * KVKK temeli:
 *   - Kişisel veri işleme envanteri (VERBİS'e hazırlık; m.16 ve envanter rehberi)
 *   - Yurt dışına aktarım yapan hizmetler ve hukuki dayanak kilidi (m.9)
 *   - Otomatik analiz türleri (m.11/1-g itiraz hakkı)
 * ==================================================================== */

public sealed record ProcessingActivity(
    string Id, string Module, string Activity, string[] Subjects, string[] DataCategories, string Purpose,
    string LegalBasis, bool Special, string Retention, string? RetentionCategory, string[] Recipients,
    string[] TransferProviders, string Measures);

public sealed record TransferProvider(string Key, string Name, string Country, string DataSent, string UsedBy);

public static class PrivacyCatalog
{
    public static readonly TransferProvider[] Providers =
    {
        new("slack", "Slack (Salesforce)", "ABD", "Ad, e-posta, onay talebi özeti, komut yanıtları", "Sohbet botu"),
        new("microsoft", "Microsoft (Teams, Outlook takvimi, Teams toplantısı)", "AB / ABD", "Ad, e-posta, onay kartları, takvim olayları, toplantı katılımcıları", "Sohbet botu, takvim, toplantı"),
        new("google", "Google (Takvim, Meet)", "ABD", "E-posta, takvim olayları (meşgul saatler, onaylı izinler), toplantı katılımcıları", "Takvim, toplantı"),
        new("zoom", "Zoom", "ABD", "Toplantı başlığı, saat, katılımcı e-postaları", "Toplantı"),
        new("anthropic", "Anthropic (yapay zekâ)", "ABD", "Gönderilen metin; kişi adları takma adla değiştirilir", "Yapay zekâ araçları"),
        new("openai", "OpenAI uyumlu yapay zekâ sağlayıcısı", "ABD (sağlayıcıya göre)", "Gönderilen metin; kişi adları takma adla değiştirilir", "Yapay zekâ araçları"),
        new("zapier", "Zapier", "ABD", "Abone olunan olayların yükü (ör. çalışan kimliği, ad, e-posta, izin tarihleri, belge imza kaydı)", "REST hook / webhook otomasyonları"),
        // n8n'in bulut sürümü (*.n8n.cloud) yurt dışıdır; kendi sunucunuzda çalışan n8n aktarım sayılmaz.
        new("n8n", "n8n Cloud (n8n GmbH)", "Almanya (AB)", "Abone olunan olayların yükü (Zapier ile aynı)", "REST hook / webhook otomasyonları (yalnızca bulut sürümü)"),
        // Dalga 5e: kendi sunucunuzdaki Mattermost / Rocket.Chat aktarım sayılmaz; yalnızca bulut sürümleri.
        new("mattermost", "Mattermost Cloud (*.cloud.mattermost.com)", "ABD", "Ad, e-posta, onay talebi özeti, komut yanıtları", "Sohbet botu (yalnızca bulut sürümü)"),
        new("rocketchat", "Rocket.Chat Cloud (*.rocket.chat)", "ABD / AB (bölgeye göre)", "Ad, e-posta, onay talebi özeti, komut yanıtları", "Sohbet botu (yalnızca bulut sürümü)"),
    };

    public static readonly Dictionary<string, string> Mechanisms = new()
    {
        ["StandardContract"] = "Standart sözleşme (imzadan sonra 5 iş günü içinde Kurul'a bildirilir)",
        ["Adequacy"] = "Yeterlilik kararı",
        ["BindingCorporateRules"] = "Bağlayıcı şirket kuralları (Kurul onaylı)",
        ["Undertaking"] = "Yazılı taahhütname (Kurul izinli)",
    };

    /// <summary>Kişinin itiraz edebileceği, yalnızca otomatik sistemlerle yapılan analizler.</summary>
    public static readonly Dictionary<string, string> Analyses = new()
    {
        ["AttritionRisk"] = "İşten ayrılma riski tahmini (makine öğrenmesi)",
        ["PerformanceScore"] = "Otomatik performans puanı",
        ["AiSummary"] = "Yapay zekâ ile üretilen performans özeti",
    };

    public static readonly ProcessingActivity[] Activities =
    {
        new("employee-record", "Çalışan kaydı", "Özlük dosyası ve çalışan kaydı", new[] { "Çalışanlar" },
            new[] { "Kimlik (ad, soyad)", "İletişim (e-posta, telefon)", "Özlük (işe giriş, pozisyon, departman, yönetici)" },
            "İş sözleşmesinin kurulması ve ifası, özlük dosyasının tutulması",
            "m.5/2-c sözleşmenin ifası; m.5/2-ç hukuki yükümlülük (4857 sayılı İş Kanunu m.75)", false,
            "İş ilişkisi sona erdikten sonra saklama politikasındaki süre", "TerminatedEmployees",
            new[] { "SGK ve yetkili kamu kurumları (talep halinde)" }, Array.Empty<string>(),
            "Rol bazlı erişim, denetim kaydı"),
        new("profile", "Profil", "Self-servis profil: TCKN, IBAN, adres, acil durum kişisi", new[] { "Çalışanlar", "Çalışanların yakınları (acil durum kişisi)" },
            new[] { "Kimlik (TCKN, doğum tarihi)", "Finans (IBAN)", "İletişim (adres, acil durum kişisi)", "Mesleki (beceriler)" },
            "Ücret ödemesi, yasal bildirimler, acil durumda yakına ulaşma",
            "m.5/2-c sözleşmenin ifası; m.5/2-ç hukuki yükümlülük; acil durum kişisi için m.5/2-f meşru menfaat", false,
            "İş ilişkisi sona erdikten sonra saklama politikasındaki süre (sonra anonimleştirilir)", "TerminatedEmployees",
            new[] { "Bankalar (ücret ödemesi)", "SGK" }, Array.Empty<string>(),
            "TCKN ve IBAN veritabanında şifreli, ekranda maskeli; açılması gerekçe ister ve kaydedilir"),
        new("leave", "İzin", "İzin talepleri ve bakiyeleri", new[] { "Çalışanlar" },
            new[] { "İzin kayıtları (tür, tarih, gün)", "Hastalık izni tarihleri (tanı tutulmaz)" },
            "Yıllık ücretli izin ve diğer izinlerin yönetimi",
            "m.5/2-ç hukuki yükümlülük (İş Kanunu m.53-60); hastalık izni için m.6/3 (istihdam ve iş sağlığı yükümlülükleri)", true,
            "İş ilişkisi süresince ve sonrasında yasal süre", null,
            new[] { "SGK (rapor ve eksik gün bildirimleri)" }, Array.Empty<string>(),
            "Hastalık izninde tanı bilgisi tutulmaz; erişim İK ve yöneticiyle sınırlı"),
        new("time", "Puantaj ve vardiya", "Çalışma saatleri, vardiya ve fazla mesai", new[] { "Çalışanlar" },
            new[] { "Çalışma süreleri, vardiya, fazla mesai" },
            "Çalışma süresinin ve fazla mesainin yasal sınırlar içinde yönetimi",
            "m.5/2-ç hukuki yükümlülük (İş Kanunu m.41, m.63)", false,
            "Yasal saklama süresi", null, new[] { "SGK" }, Array.Empty<string>(),
            "Biyometrik veri (parmak izi, yüz) kullanılmaz; konum sürekli izlenmez"),
        new("compensation", "Ücret", "Ücret kayıtları ve ücret bantları", new[] { "Çalışanlar" },
            new[] { "Finans (ücret, yan haklar)" },
            "Ücretin hesaplanması ve ödenmesi, yasal bildirimler",
            "m.5/2-c sözleşmenin ifası; m.5/2-ç hukuki yükümlülük (VUK, 5510 sayılı Kanun)", false,
            "Yasal saklama süresi (vergi ve SGK mevzuatı)", null,
            new[] { "SGK", "Gelir İdaresi Başkanlığı", "Bankalar" }, Array.Empty<string>(),
            "Yalnızca İK; her görüntüleme kaydedilir"),
        new("payroll", "Bordro", "Aylık bordro hesabı ve bordro pusulası", new[] { "Çalışanlar" },
            new[] { "Finans (brüt/net ücret, SGK ve vergi kesintileri, ek ödeme ve kesintiler)", "Eksik gün ve fazla mesai süreleri" },
            "Ücretin hesaplanması ve ödenmesi, SGK ve vergi bildirimleri",
            "m.5/2-ç hukuki yükümlülük (İş Kanunu m.37, 5510 s. K., GVK); m.5/2-c sözleşmenin ifası", false,
            "10 yıl (5510 s. K. m.86, VUK m.253), sonra silinir", "Payslips",
            new[] { "SGK", "Gelir İdaresi Başkanlığı", "Bankalar" }, Array.Empty<string>(),
            "Pusulayı yalnızca çalışan (dönem kapanınca) ve bordro yetkilisi görür; İK görüntülemeleri kaydedilir; kapanmış dönem değiştirilemez"),
        new("payroll-files", "Bordro", "SGK bildirge, banka ödeme ve muhasebe dosyaları", new[] { "Çalışanlar" },
            new[] { "Kimlik (T.C. kimlik no, ad soyad)", "Finans (IBAN, net ücret, prime esas kazanç)" },
            "SGK bildirimi, maaş ödemesi ve muhasebe kaydı",
            "m.5/2-ç hukuki yükümlülük (5510 s. K. m.86, İş Kanunu m.32); m.5/2-c sözleşmenin ifası", false,
            "Dosya içeriği en geç 24 saatte silinir (banka dosyası tek indirmeden sonra); kayıt bilgisi denetim süresince", null,
            new[] { "SGK", "Bankalar", "Muhasebe" }, Array.Empty<string>(),
            "Şifreli saklanır, yalnızca bordro yetkilisi indirir, her indirme erişim kaydına yazılır; muhasebe dosyası kişi verisi içermez"),
        new("advances", "Bordro", "Maaş avansı ve personel borcu", new[] { "Çalışanlar" },
            new[] { "Finans (avans tutarı, taksitler)" }, "Avans ödemesi ve bordrodan kesinti",
            "m.5/2-c sözleşmenin ifası", false, "Kapanıştan sonra bordro saklama süresi (10 yıl)", "Payslips",
            new[] { "Bordro yetkilisi" }, Array.Empty<string>(),
            "Yalnızca çalışan ve bordro yetkilisi görür; bildirimde tutar yazılmaz"),
        new("benefits", "Bordro", "Esnek yan hak seçimleri", new[] { "Çalışanlar" },
            new[] { "Seçilen yan haklar ve tutarları" }, "Yan hakların sağlanması",
            "m.5/2-c sözleşmenin ifası", false, "Plan yılı + 2 yıl", null,
            new[] { "Yan hak sağlayıcıları (seçilen hizmet için)" }, Array.Empty<string>(),
            "Özel sağlık sigortası seçiminde sağlık beyanı istenmez; özet raporda kişi görünmez"),
        new("travel", "Masraf", "Seyahat talebi ve harcırah", new[] { "Çalışanlar" },
            new[] { "Seyahat bilgisi (yer, tarih, amaç)", "Kimlik (pasaport no — yalnızca yurt dışı)" }, "İş seyahatinin planlanması ve harcırah ödemesi",
            "m.5/2-c sözleşmenin ifası; m.5/2-ç (GVK harcırah istisnası)", false,
            "Pasaport no seyahat bitiminden 7 gün sonra silinir; seyahat kaydı masraf saklama süresince", null,
            new[] { "Seyahat acentesi/havayolu (gerektiğinde)" }, Array.Empty<string>(),
            "Pasaport no şifreli, yalnızca çalışan ve İK açabilir, her açılış kaydedilir; reddedilen/iptal edilen seyahatte hemen silinir"),
        new("documents", "Belge talebi", "Çalışma belgesi, maaş yazısı gibi belgelerin talebi ve düzenlenmesi", new[] { "Çalışanlar" },
            new[] { "Kimlik (ad, soyad)", "Özlük (işe giriş, pozisyon)", "Finans (maaş yazısında brüt ücret)", "Doğrulama kodu" },
            "Çalışanın talep ettiği belgenin düzenlenmesi", "m.5/2-c sözleşmenin ifası; m.5/2-ç hukuki yükümlülük (İş Kanunu m.28)", false,
            "Saklama politikasındaki süre (varsayılan 24 ay)", "DocumentRequests", Array.Empty<string>(), Array.Empty<string>(),
            "Belge şifreli saklanır, yalnızca çalışan ve İK açar; doğrulama sayfası yalnızca belge türü, tarih ve baş harfleri gösterir"),
        new("expense", "Masraf", "Masraf talepleri ve fişler", new[] { "Çalışanlar" },
            new[] { "Finans (tutar, fiş)" }, "Masrafların geri ödenmesi ve muhasebeleştirilmesi",
            "m.5/2-c sözleşmenin ifası; m.5/2-ç hukuki yükümlülük (VUK)", false,
            "Yasal saklama süresi (VUK)", null, new[] { "Muhasebe" }, Array.Empty<string>(), "Rol bazlı erişim"),
        new("performance", "Performans", "Hedefler, değerlendirmeler ve geri bildirim", new[] { "Çalışanlar" },
            new[] { "Performans değerlendirmeleri, hedefler, geri bildirim" },
            "Performans yönetimi ve gelişim planlaması", "m.5/2-f meşru menfaat", false,
            "İş ilişkisi süresince ve sonrasında saklama politikasındaki süre", "TerminatedEmployees",
            Array.Empty<string>(), Array.Empty<string>(), "Sonuçlar çalışana açık; otomatik puana itiraz edilebilir"),
        new("learning", "Eğitim", "Eğitim kayıtları ve sertifikalar", new[] { "Çalışanlar" },
            new[] { "Eğitim ve sertifika kayıtları" }, "Mesleki gelişim, zorunlu İSG eğitimlerinin takibi",
            "m.5/2-c sözleşmenin ifası; m.5/2-ç hukuki yükümlülük (6331 sayılı İSG Kanunu)", false,
            "İş ilişkisi süresince ve sonrasında yasal süre", null, Array.Empty<string>(), Array.Empty<string>(), "Rol bazlı erişim"),
        new("recruitment", "İşe alım", "Aday başvuruları, CV, mülakat notları", new[] { "Çalışan adayları" },
            new[] { "Kimlik", "İletişim", "Mesleki deneyim (CV)", "Mülakat değerlendirmeleri" },
            "İşe alım sürecinin yürütülmesi",
            "m.5/2-c sözleşmenin kurulması; aday havuzunda tutma için açık rıza", false,
            "Olumsuz sonuçlanan başvurularda saklama politikasındaki süre", "RejectedCandidates",
            Array.Empty<string>(), Array.Empty<string>(), "Süre dolunca otomatik anonimleştirme ya da silme"),
        new("onboarding", "İşe başlama ve zimmet", "Onboarding görevleri ve zimmetli varlıklar", new[] { "Çalışanlar" },
            new[] { "Görev kayıtları", "Zimmet kayıtları" }, "İşe başlama sürecinin ve şirket varlıklarının yönetimi",
            "m.5/2-c sözleşmenin ifası", false, "İş ilişkisi süresince", null, Array.Empty<string>(), Array.Empty<string>(), "Rol bazlı erişim"),
        new("notification", "Bildirim", "E-posta ve uygulama içi bildirimler", new[] { "Çalışanlar" },
            new[] { "İletişim (e-posta)", "Bildirim içeriği" }, "İş süreçleriyle ilgili bilgilendirme",
            "m.5/2-c sözleşmenin ifası", false, "Saklama politikasındaki süre", "Notifications",
            new[] { "E-posta sağlayıcısı (SMTP)" }, Array.Empty<string>(),
            "SMTP sağlayıcısı yurt dışındaysa (ör. Gmail, Microsoft 365) yurt dışı aktarım dayanağı gerekir"),
        new("audit", "Denetim kaydı", "Kim, ne zaman, hangi işlemi yaptı", new[] { "Kullanıcılar" },
            new[] { "İşlem güvenliği (kullanıcı kimliği, IP adresi, işlem)" },
            "Veri güvenliğinin sağlanması ve hesap verebilirlik",
            "m.5/2-ç hukuki yükümlülük (m.12 veri güvenliği); m.5/2-f meşru menfaat", false,
            "Saklama politikasındaki süre", "AuditLog", Array.Empty<string>(), Array.Empty<string>(),
            "Hassas alan değerleri kaydedilmez, yalnızca değiştiği bilgisi tutulur"),
        new("engagement", "Etkileşim", "Takdir, anket, mentorluk, 1:1, ofis rezervasyonu", new[] { "Çalışanlar" },
            new[] { "Takdir mesajları", "Anket yanıtları (anonim)", "1:1 notları", "Masa rezervasyonu" },
            "Çalışan bağlılığı ve iç iletişim", "m.5/2-f meşru menfaat", false,
            "İş ilişkisi süresince", null, Array.Empty<string>(), Array.Empty<string>(),
            "Anket yanıtları kimlikle eşleştirilmez; küçük gruplar raporlanmaz"),
        new("calendar", "Takvim ve toplantı", "Takvim bağlantısı, toplantı oluşturma, uygun saat önerisi", new[] { "Çalışanlar", "Adaylar (mülakat)" },
            new[] { "Takvim olayları (meşgul saatler)", "Toplantı katılımcıları (e-posta)" },
            "Toplantıların planlanması ve onaylı izinlerin takvime yazılması",
            "m.5/2-f meşru menfaat; bağlantıyı çalışan kendisi kurar", false,
            "Bağlantı kaldırılınca", null, Array.Empty<string>(), new[] { "google", "microsoft", "zoom" },
            "OAuth jetonları şifreli; yalnızca bağlantıyı kuran kişinin takvimi okunur"),
        new("chat", "Sohbet botu", "Slack, Microsoft Teams, Mattermost ve Rocket.Chat üzerinden onay, sorgu ve self-servis işlemler", new[] { "Çalışanlar" },
            new[] { "Ad, e-posta", "Onay talebi özeti (kalan bakiye ve ekipten izinli SAYISI; başkalarının adı yok)", "İzin bakiyesi, vardiya, masa, bordro özeti (yalnızca kişiye özel DM; bordro ek doğrulamayla)",
                    "Fiş görüntüsü (yalnızca bellekte okunur, saklanmaz)", "Kullanım sayaçları (kişisiz)" },
            "Onay süreçlerinin hızlandırılması ve çalışan self-servis işlemleri", "m.5/2-f meşru menfaat; m.5/2-c sözleşmenin ifası (izin, masraf, puantaj)", false,
            "Saklama politikasındaki süre", "ChatMessages", Array.Empty<string>(), new[] { "slack", "microsoft", "mattermost", "rocketchat" },
            "Hassas veri bota yazılmaz; yanıtlar yalnızca soran kişiye görünür; kişisel yanıt kanala yazılmaz; belge içeriği değil bağlantısı gönderilir; sessiz saate uyulur; kişi ve kiracı başına hız sınırı; ücretle ilgili onaylar ve toplu onay HR360'ta ek doğrulama ister; kendi sunucunuzdaki Mattermost/Rocket.Chat yurt dışı aktarım sayılmaz"),
        new("chat-context", "Sohbet botu", "İK asistanı konuşma bağlamı (devam soruları için son sorular)", new[] { "Çalışanlar" },
            new[] { "Asistana sorulan son sorular ve kısa yanıtlar" },
            "\"peki geçen ay?\" gibi devam sorularının anlaşılması", "m.5/2-f meşru menfaat", false,
            "30 gün (kişi \"geçmişimi sil\" ile istediği an siler)", "ChatContext", Array.Empty<string>(), Array.Empty<string>(),
            "AES-256-GCM şifreli; kişi başına son 6 soru; 30 günde otomatik silinir; yalnızca soran kişinin yanıtlarında kullanılır"),
        new("chat-surveys", "Sohbet botu", "Nabız anketi ve çıkış anketi (sohbetten)", new[] { "Çalışanlar", "Ayrılan çalışanlar" },
            new[] { "Nabız yanıtı (anonim: kimlik, departman ve saat tutulmaz)", "Yanıtladı bilgisi (ayrı tabloda, tekrarı önlemek için)", "Çıkış anketi yanıtları (yalnızca İK)" },
            "Çalışan bağlılığının ölçülmesi ve ayrılış nedenlerinin anlaşılması", "m.5/2-f meşru menfaat; katılım isteğe bağlıdır", false,
            "Nabız: anket saklama süresi; çıkış anketi: ayrılış kaydıyla birlikte", null, Array.Empty<string>(), Array.Empty<string>(),
            "Sonuçlar en az 5 yanıtla gösterilir; çıkış anketi ara yanıtları şifreli ve tamamlanınca silinir; yöneticiyle paylaşılmaz"),
        new("ai", "Yapay zekâ araçları", "İlan taslağı, özet, İK asistanı", new[] { "Çalışanlar", "Adaylar" },
            new[] { "Gönderilen metin", "Kişi adları (takma adla)" },
            "Metin üretimi ve özetleme", "m.5/2-f meşru menfaat; kişisel veri gönderimi için ayrıca kiracı izni", false,
            "Kullanım kayıtları saklama politikasındaki süre", "AiUsage", Array.Empty<string>(), new[] { "anthropic", "openai" },
            "Yerel model (Ollama) önerilir; yurt dışı sağlayıcı dayanak kaydı olmadan çalışmaz"),
        new("webhooks", "Otomasyon", "Webhook ve REST hook (Zapier, n8n) ile olay aktarımı", new[] { "Çalışanlar" },
            new[] { "Abone olunan olayın yükü (kimlik, iletişim, izin/onay/belge olayları)" },
            "İK süreçlerinin şirketin diğer sistemleriyle otomasyonu", "m.5/2-f meşru menfaat; m.5/2-c sözleşmenin ifası", false,
            "Gönderim kayıtları saklama politikasındaki süre", "WebhookDeliveries", new[] { "Kiracının tanımladığı alıcı sistemler" }, new[] { "zapier", "n8n" },
            "HMAC-SHA256 imzalı teslimat; API anahtarı yetkisiyle abonelik; Zapier ve n8n Cloud gibi yurt dışı hedefler aktarım dayanağı kaydı olmadan açılamaz ve teslim edilmez; kendi sunucunuzdaki (iç ağ) n8n serbesttir"),
        new("automated-analysis", "Otomatik analiz", "İşten ayrılma riski tahmini", new[] { "Çalışanlar" },
            new[] { "Kıdem ve iş verilerinden türetilen özellikler", "Risk skoru" },
            "İK'nın elde tutma çalışmalarına destek", "m.5/2-f meşru menfaat", false,
            "Skor saklanmaz; istek anında hesaplanır", null, Array.Empty<string>(), Array.Empty<string>(),
            "Skor tek başına karar için kullanılamaz; çalışan itiraz edebilir (m.11/1-g), itirazda skor üretilmez"),
        // ---------------------------------------------------------------- Dalga 10: ML
        new("attrition-training", "Otomatik analiz", "Devir riski modelinin şirket verisiyle eğitimi (kiracı izniyle)", new[] { "Çalışanlar", "Ayrılmış çalışanlar" },
            new[] { "Kıdem, ücret/bant oranı, son performans puanı, son terfiden geçen süre, fazla mesai, eğitim saati (kimliksiz, toplu satırlar)", "Ayrıldı/kalmadı etiketi" },
            "Paylaşılan devir riski modelinin doğruluğunun artırılması", "m.5/2-f meşru menfaat; kiracının açık eğitim izni (varsayılan kapalı)", false,
            "Eğitim satırları saklanmaz (yalnızca özellik başına toplu histogram); model sürümü MLflow'da", null,
            new[] { "Platform işletmecisi (model eğitimi, yalnızca kimliksiz satırlar)" }, Array.Empty<string>(),
            "Satırlarda kimlik, ad, cinsiyet, yaş yok; en az 200 çalışan ve 20 ayrılan şartı; zaman bazlı doğrulama; aday model insan onayıyla yayına girer"),
        new("growth-recommendations", "Gelişim", "\"Sana uygun\" iç ilan, mentor ve eğitim önerileri", new[] { "Çalışanlar" },
            new[] { "Beceriler (profil)", "Yetkinlik değerlendirmeleri ve rol beklentileri", "Tamamlanan eğitimler" },
            "Çalışanın kendi gelişimi için iç fırsatların önerilmesi", "m.5/2-f meşru menfaat", false,
            "Öneri saklanmaz; istek anında hesaplanır", null, Array.Empty<string>(), Array.Empty<string>(),
            "Öneriler yalnızca kişinin kendisine gösterilir; otomatik başvuru/kayıt yapılmaz; mentor önerisinde yalnızca mentor olmayı seçmiş kişiler"),
        new("candidate-fit", "İşe alım", "Aday–ilan uygunluk puanı (işe alım uzmanına yardımcı)", new[] { "Çalışan adayları" },
            new[] { "CV'deki beceriler, deneyim yılı, nitelikler (ad, cinsiyet, yaş, fotoğraf, adres modele girmez)" },
            "İşe alım uzmanının başvuruları incelemesine yardım", "m.5/2-c sözleşmenin kurulması; m.5/2-f meşru menfaat", false,
            "Puan saklanmaz; istek anında hesaplanır", null, Array.Empty<string>(), Array.Empty<string>(),
            "Otomatik ret yok (karar insanda); kimlik/demografik alanlar ve metindeki kişisel ifadeler puanlamadan önce çıkarılır; her hesaplama denetim kaydına yazılır"),
        new("privacy", "KVKK kayıtları", "Aydınlatma, açık rıza, ilgili kişi başvuruları, imha tutanakları", new[] { "Çalışanlar", "Kullanıcılar" },
            new[] { "Rıza ve başvuru kayıtları", "IP adresi" }, "KVKK yükümlülüklerinin yerine getirilmesi ve ispatı",
            "m.5/2-ç hukuki yükümlülük", false, "İmha tutanakları en az 3 yıl", null,
            new[] { "Kişisel Verileri Koruma Kurulu (talep halinde)" }, Array.Empty<string>(), "Yalnızca İK"),
        new("identity", "Kimlik doğrulama", "Giriş, oturum ve iki adımlı doğrulama", new[] { "Kullanıcılar" },
            new[] { "Kullanıcı adı, parola özeti, oturum kayıtları" }, "Sisteme güvenli erişim",
            "m.5/2-c sözleşmenin ifası; m.5/2-f meşru menfaat", false, "Hesap kapatılınca", null,
            Array.Empty<string>(), Array.Empty<string>(), "Parola düz metin tutulmaz; isteğe bağlı TOTP ve SSO"),
        // ---------------------------------------------------------------- Dalga 5c: işyeri uyumu
        new("announcements", "Duyurular", "Şirket duyuruları ve \"okudum\" kayıtları", new[] { "Çalışanlar" },
            new[] { "Okuma kaydı (kullanıcı, ad, tarih)" }, "İç iletişim; zorunlu bilgilendirmelerin okunduğunun ispatı",
            "m.5/2-f meşru menfaat; m.5/2-ç hukuki yükümlülük (zorunlu bilgilendirmeler)", false,
            "Duyurunun süresi dolduktan sonra saklama politikasındaki süre (varsayılan 12 ay)", "Announcements",
            Array.Empty<string>(), Array.Empty<string>(),
            "Okudum kaydı rıza değildir, rıza kayıtlarından ayrı tutulur; bildirimde yalnızca başlık; okumayanlar listesi yalnızca onay gerektiren duyuruda İK'ya"),
        new("policy-library", "Doküman kütüphanesi", "Politika, el kitabı ve prosedürler; sürüm bazlı okuma/kabul kayıtları", new[] { "Çalışanlar" },
            new[] { "Kabul kaydı (kullanıcı, ad, belge sürümü, tarih)" }, "Şirket politikalarının duyurulması ve kabulünün ispatı",
            "m.5/2-c sözleşmenin ifası; m.5/2-f meşru menfaat", false,
            "İş ilişkisi süresince (kabul kaydı politika uyumunun ispatıdır)", null, Array.Empty<string>(), Array.Empty<string>(),
            "Arama yalnızca kişinin görme yetkisi olan belgelerde yapılır; yeni sürüm yeniden kabul ister"),
        new("ethics-hotline", "Etik hattı", "Anonim etik/ihbar bildirimleri ve yazışmalar", new[] { "İhbarcılar (anonim)", "Bildirimde adı geçen kişiler" },
            new[] { "Bildirim metni", "İhbarcının isteğe bağlı bıraktığı iletişim bilgisi" },
            "Etik ve uyum ihlallerinin bildirilmesi ve soruşturulması", "m.5/2-f meşru menfaat; m.5/2-ç hukuki yükümlülük (sektörel uyum mevzuatı)", false,
            "Kapatılan bildirimler saklama politikasındaki süre sonunda silinir (varsayılan 24 ay)", "EthicsReports",
            new[] { "Etik kurulu (şirket yöneticisinin atadığı üyeler)" }, Array.Empty<string>(),
            "IP, kullanıcı kimliği ve tarayıcı bilgisi tutulmaz; gateway erişim kaydı kapalı; alınma tarihi gün hassasiyetinde; takip kodunun yalnızca SHA-256 özeti; iletişim bilgisi şifreli ve her açılışı kaydedilir; İK otomatik erişemez"),
        new("osh", "İş sağlığı ve güvenliği", "İş kazası / ramak kala kayıtları ve İSG eğitimleri", new[] { "Çalışanlar" },
            new[] { "Olay kaydı (tarih, yer, açıklama, kayıp gün, kök neden)", "Eğitim katılımı" },
            "6331 sayılı Kanun kapsamında kaza kaydı, SGK iş kazası bildirimi ve zorunlu eğitimlerin takibi",
            "m.5/2-ç hukuki yükümlülük (6331 s. K. m.14, m.17; 5510 s. K. m.13)", false,
            "Mevzuattaki süre (iş kazası ve eğitim kayıtları en az 15 yıl önerilir); otomatik imha yok", null,
            new[] { "SGK (iş kazası bildirimi)", "İSG profesyonelleri" }, Array.Empty<string>(),
            "Olay açıklamasına yaralanmanın tıbbi ayrıntısı yazılmaz (arayüz uyarısı); erişim İK ve İSG uzmanı ile sınırlı"),
        new("osh-health", "İş sağlığı ve güvenliği", "İşe giriş ve periyodik sağlık muayeneleri", new[] { "Çalışanlar" },
            new[] { "Muayene tarihi ve sonucu (uygun/uygun değil/şartlı)", "Sağlık notları (özel nitelikli)" },
            "Çalışanın işe uygunluğunun belirlenmesi ve periyodik muayenelerin takibi",
            "m.6/3 özel nitelikli veri — iş sağlığı ve güvenliği yükümlülükleri (6331 s. K. m.15); sır saklama yükümlülüğü altındaki işyeri hekimi", true,
            "15 yıl (İşyeri Sağlık ve Güvenlik Birimleri Yönetmeliği); otomatik imha yok", null,
            new[] { "İşyeri hekimi" }, Array.Empty<string>(),
            "Sağlık notları AES-256-GCM şifreli; yalnızca işyeri hekimi rolü okur/yazar ve her okuma kaydedilir; İK ve yönetici yalnızca sonuç ve tarihleri görür"),
        new("disciplinary", "Disiplin", "Disiplin vakası, savunma istemi, savunma, tutanak ve karar", new[] { "Çalışanlar", "Tanıklar (yalnızca ad)" },
            new[] { "Olay açıklaması", "Yazılı savunma", "Tutanak ve tanık adları", "Disiplin kararı" },
            "İş Kanunu kapsamında disiplin sürecinin yürütülmesi ve savunma hakkının sağlanması",
            "m.5/2-ç hukuki yükümlülük (İş Kanunu m.19, m.25); m.5/2-e bir hakkın tesisi, kullanılması veya korunması", false,
            "Kapanıştan sonra saklama politikasındaki süre (varsayılan 24 ay)", "DisciplinaryCases",
            Array.Empty<string>(), Array.Empty<string>(),
            "Adli sicil / mahkûmiyet bilgisi tutulmaz (arayüz ve sunucu uyarısı); erişim İK, departman başı ve çalışanın kendisi (tutanak/tanık hariç); İK ve yönetici görüntülemeleri kaydedilir"),
    };

    /// <summary>İmzadan sonraki n'inci iş gününü verir (hafta sonu hariç).</summary>
    public static DateOnly AddBusinessDays(DateOnly start, int days)
    {
        var d = start;
        while (days > 0)
        {
            d = d.AddDays(1);
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) days--;
        }
        return d;
    }
}

/// <summary>Yurt dışı aktarım kilidi: hukuki dayanak kaydı olmayan hizmete veri gönderilmez.</summary>
public static class TransferGuard
{
    public static string ProviderName(string key) =>
        PrivacyCatalog.Providers.FirstOrDefault(p => p.Key == key)?.Name ?? key;

    public static string MessageEn(string provider) =>
        $"KVKK art. 9: {ProviderName(provider)} transfers personal data abroad. First record the legal basis (e.g. standard contract) under KVKK › Cross-border transfers.";

    public static string Message(string provider) =>
        $"KVKK m.9: {ProviderName(provider)} kişisel veriyi yurt dışına aktarır. Önce KVKK › Yurt dışı aktarım ekranında hukuki dayanağı (ör. standart sözleşme) kaydedin.";

    /// <summary>Kiracının hukuki dayanak kaydı olan hizmetleri (arka plan işlerinde de çalışır: kiracı açıkça verilir).</summary>
    public static async Task<HashSet<string>> AllowedAsync(GovernanceDbContext db, string tenant, CancellationToken ct) =>
        (await db.TransferAgreements.IgnoreQueryFilters().AsNoTracking().Where(a => a.TenantSlug == tenant).Select(a => a.Provider).ToListAsync(ct))
        .ToHashSet();

    /// <summary>Kayıt varsa null, yoksa kullanıcıya gösterilecek hata.</summary>
    public static async Task<string?> MissingAsync(GovernanceDbContext db, string tenant, string provider, CancellationToken ct) =>
        (await AllowedAsync(db, tenant, ct)).Contains(provider) ? null : Message(provider);

    /// <summary>
    /// Webhook/REST hook hedef adresinin yurt dışı aktarım anahtarı: zapier.com ve alt alan
    /// adları "zapier", n8n bulut (n8n.cloud) "n8n"; diğer adresler (ör. kendi sunucunuzdaki
    /// n8n, iç ağ) null — kiracının kendi altyapısı aktarım sayılmaz.
    /// </summary>
    public static string? HookProvider(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
        var host = u.Host.TrimEnd('.').ToLowerInvariant();
        if (host == "zapier.com" || host.EndsWith(".zapier.com")) return "zapier";
        if (host == "n8n.cloud" || host.EndsWith(".n8n.cloud")) return "n8n";
        return null;
    }

    /// <summary>
    /// Hedef iç ağda mı (kendi sunucunuzdaki n8n gibi): localhost, tek etiketli (docker/servis
    /// adı) ya da .local/.internal/.lan adları ve RFC 1918 / loopback / link-local / ULA adresleri.
    /// Yalnızca bilgilendirme amaçlıdır; DNS çözülmez.
    /// </summary>
    public static bool IsInternalHost(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        var host = u.Host.TrimEnd('.').ToLowerInvariant();
        if (System.Net.IPAddress.TryParse(host.Trim('[', ']'), out var ip))
        {
            if (System.Net.IPAddress.IsLoopback(ip)) return true;
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;
            var b = ip.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
        }
        return host == "localhost" || !host.Contains('.') || host.EndsWith(".local") || host.EndsWith(".internal") || host.EndsWith(".lan")
            || host.EndsWith(".localhost") || host.EndsWith(".home.arpa");
    }

    /// <summary>Sohbet platformu / takvim sağlayıcısı adından aktarım anahtarı.</summary>
    public static string KeyOf(string platformOrProvider) => platformOrProvider.ToLowerInvariant() switch
    {
        "slack" => "slack",
        "teams" or "microsoft" => "microsoft",
        "google" => "google",
        "zoom" => "zoom",
        var x => x,
    };

    /// <summary>Hizmet şu an kullanımda mı (açık entegrasyon / yapay zekâ sağlayıcısı).</summary>
    public static async Task<Dictionary<string, bool>> InUseAsync(GovernanceDbContext db, LlmClient llm, CancellationToken ct)
    {
        var apps = await db.ChatApps.AsNoTracking().Where(a => a.IsEnabled).Select(a => a.Platform).ToListAsync(ct);
        var providers = await db.ProviderConfigs.AsNoTracking().Where(c => c.IsEnabled).Select(c => c.Provider).ToListAsync(ct);
        var ai = await db.AiSettings.AsNoTracking().AnyAsync(s => s.Enabled, ct);
        var llmKey = LlmProviderKey(llm);
        var hookUrls = await db.Webhooks.AsNoTracking().Where(w => w.IsEnabled).Select(w => w.Url).ToListAsync(ct);
        var chatHosts = await db.ChatApps.AsNoTracking().Where(a => a.IsEnabled && (a.Platform == "Mattermost" || a.Platform == "RocketChat"))
            .Select(a => new { a.Platform, a.ServerUrl }).ToListAsync(ct);
        return PrivacyCatalog.Providers.ToDictionary(p => p.Key, p => p.Key switch
        {
            "zapier" or "n8n" => hookUrls.Any(u => HookProvider(u) == p.Key),
            "mattermost" or "rocketchat" => chatHosts.Any(h => Chat.ChatHosts.SaasKey(h.Platform, h.ServerUrl) == p.Key),
            "slack" => apps.Contains("Slack"),
            "microsoft" => apps.Contains("Teams") || providers.Contains("Microsoft"),
            "google" => providers.Contains("Google"),
            "zoom" => providers.Contains("Zoom"),
            _ => ai && llmKey == p.Key,
        });
    }

    /// <summary>Yapay zekâ sağlayıcısının aktarım anahtarı; yerel modelde null.</summary>
    public static string? LlmProviderKey(LlmClient llm) =>
        !llm.Configured || llm.IsLocal ? null : llm.Provider is "anthropic" or "openai" ? llm.Provider : null;

    public static string Status(Models.TransferAgreement? a, DateOnly today)
    {
        if (a is null) return "Missing";
        if (a.Mechanism != "StandardContract" || a.NotifiedAt is not null) return "Ok";
        return today > PrivacyCatalog.AddBusinessDays(a.SignedAt, 5) ? "NotifyOverdue" : "NotifyPending";
    }
}

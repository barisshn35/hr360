# Makine öğrenmesi — model kartları

Bu belge üç ML özelliğinin amacını, girdilerini, korumalarını ve sınırlarını
model kartı biçiminde anlatır. Tümü yereldir (`apps/ml-inference`, scikit-learn/sözlük tabanlı; LLM
yok, veri yurt dışına çıkmaz) ve yalnızca **öneri/işaret** üretir; hiçbir kayıt otomatik değişmez.

---

## 1. Devir riski modelinin şirket verisiyle eğitimi

| | |
|---|---|
| **Amaç** | Paylaşılan devir riski modelini (`hr360-attrition-risk`) sentetik veri yerine gerçek, toplu ve kimliksiz şirket verisiyle eğitmek. |
| **İzin** | Varsayılan **kapalı**. Şirket yöneticisi (tenant-admin) Model kartı › "Modeli şirket verisiyle eğit (izin)" ile açar/kapatır; değişiklik denetim kaydına yazılır (`TenantTrainingConsentGranted/Revoked`). İzin yoksa çıkarım bile yapılmaz (`409 no_consent`). |
| **Özellikler** | Kıdem (yıl), ücret / bant ortası oranı (`compensation_records` + `compensation_salary_bands`), son performans puanı (`performance_snapshots`, geçici olmayan; 0-100 ise 1-5'e çevrilir), son terfiden bu yana ay (pozisyon unvanı değişikliği), son 3 ayın aylık ortalama fazla mesaisi (`timeshift_time_entries`), son 12 ayın tamamlanan eğitim saati (`learning_enrollments` × `learning_courses.DurationHours`). |
| **Etiket** | Anlık görüntü tarihinden sonraki 12 ayda ayrıldı mı (ayrılış: `engagement_offboarding_cases.LastWorkingDay`, yoksa son görev bitişi). Ayrılış tarihi bilinmeyen ayrılmış çalışan dışarıda kalır. |
| **Takma adlama** | Satırda kimlik, ad, departman, cinsiyet, yaş YOKTUR (ML şeması ek alanı reddeder); satırlar karıştırılarak gönderilir; kiracı adı yerine 16 haneli özet (`tenant_ref`) gider. Eksik ücret oranı 1,0, eksik puan 3 ile doldurulur; doldurma sayıları raporlanır. |
| **Zaman bazlı doğrulama** | Eğitim: bugün − 24 ay anlık görüntüsü (etiket penceresi −24…−12 ay). Değerlendirme: bugün − 12 ay anlık görüntüsü (etiket penceresi −12 ay…bugün). Etiket pencereleri çakışmaz; model geçmişle eğitilip yakın dönemde sınanır. |
| **Asgari veri** | Eğitimde en az 200 çalışan, 20 ayrılan ve 20 kalan; değerlendirmede en az 50 çalışan ve her sınıftan 5. Sağlanmazsa `422 insufficient_data` ve gerekçeler; ML'e satır gönderilmez. Önizleme (`GET /model/tenant-training/preview`) hiçbir şey göndermeden sayıları gösterir. |
| **Yayın** | ml-inference adayı mevcut modelle AYNI değerlendirme kümesinde karşılaştırır; geçse bile champion/challenger kuralıyla **onay bekler** (platform yöneticisi Sürümler panelinden onaylar). Paylaşılan modelin yayındaki sürümü kiracı işlemiyle değişmez. Model kartında kaynak "kiracı izniyle, zaman bazlı doğrulama" olarak görünür (`provenance`). |
| **Sıklık** | Kiracı başına günde en çok bir eğitim. |
| **Saklama** | Eğitim satırları saklanmaz; modelle yalnızca özellik başına toplu histogram tutulur. Son eğitimin özeti (sayılar, aday sürüm, gerekçe) `governance_ml_model_settings.LastTenantTraining`'de. |
| **Uçlar** | `GET /api/governance/model/tenant-training`, `PUT …/consent`, `GET …/preview`, `POST …/run` (İK; izin yalnızca tenant-admin). ml-inference: `POST /model/retrain` (`source=rows`, `evaluation_rows`, `provenance`; servisler arası anahtar + çağıranın jetonu). |
| **Sınırlar** | Küçük şirketlerde eşik sağlanamaz (demo şirketi sağlamaz). Paylaşılan model, izin veren kiracıların dağılımına kayabilir; adillik denetimi ve kayma izleme sürer. |

## 2. "Sana uygun" — iç ilan, mentor ve eğitim önerileri

| | |
|---|---|
| **Amaç** | Çalışana kendi gelişimi için iç fırsatları önermek. |
| **Girdiler** | Profil becerileri (`engagement_profiles.Skills`, sözlük + eş anlamlılarla kanonik anahtara indirgenir), yetkinlik açıkları (pozisyon/departman rol profilindeki beklenen seviye − son değerlendirme), tamamlanan eğitimler; yayındaki iç ilanların metni, mentor olmayı seçmiş kişilerin sunduğu konular ve boş kapasitesi, etkin eğitimler ve geliştirdikleri yetkinlikler. ML'e mentor/ilan opak kimlikle gider; ad gitmez. |
| **Yöntem** | İlan: ilandaki becerilerin benimkilerle örtüşmesi. Mentor: hedeflerim (açıklar + en uygun ilanların eksik becerileri) ile mentorun konularının örtüşmesi, boş kapasite. Eğitim: yetkinlik açığını kapatma büyüklüğü + hedef konularla metin örtüşmesi; tamamlananlar önerilmez. Her öneri gerekçesiyle döner. |
| **Görünürlük** | Yalnızca kişinin kendisi (`GET /api/governance/growth/recommendations`). Yalnızca zaten görünür kayıtlar kullanılır (yayındaki ilanlar, mentor olmayı seçenler, etkin eğitimler). |
| **Otomatik işlem** | Yok: başvuru, mentorluk talebi, eğitim kaydı kişinin kendi işlemidir. Öneri saklanmaz. |
| **Arayüz** | İç ilanlar › "Sana uygun" sekmesi. |

## 3. Aday–ilan uygunluk puanı

| | |
|---|---|
| **Amaç** | İşe alım uzmanının başvuruları incelerken öncelik vermesine yardım. **Karar desteğidir, karar değildir.** |
| **Kullanılmaz** | Otomatik eleme/ret, tek başına işe alım kararı, çalışan değerlendirmesi. Yanıt her adayda `decision: "none"` taşır. |
| **Girdiler** | İlan başlığı ve metni (zorunlu/tercih edilen beceriler, "en az N yıl" deneyim ve "Aranan nitelikler" satırları metinden çıkarılır); adayın özgeçmiş metni ve beceri listesi. |
| **Bileşenler** | Zorunlu beceri örtüşmesi (%50), deneyim yılı (açık ifade ya da çakışmaları birleştirilmiş tarih aralıkları; %20), nitelik satırı örtüşmesi (kök eşleşmesi; %20), tercih edilen beceriler (%10). İlanda olmayan bileşenin ağırlığı diğerlerine dağılır. Her bileşen kanıtıyla döner. |
| **Önyargı koruması** | Ad, cinsiyet, yaş/doğum tarihi, fotoğraf, adres, medeni durum, uyruk, iletişim bilgileri ve KVKK m.6 özel nitelikli veriler modele girmez: aday opak sıra numarasıyla gönderilir; governance adayın adını/e-posta yerel kısmını metinden siler; ml-inference e-posta, telefon, TCKN, IBAN, URL, adres satırı/parçaları, yaş ve doğum tarihi, cinsiyet/medeni durum/uyruk satırları ve sözcükleri, fotoğraf anmaları ile m.6 sözcüklerini (pii.py kuralları) puanlamadan önce çıkarır ve kaç ifadenin çıkarıldığını (değerini değil) bildirir. Bileşen adları dışlanan nitelik köklerine karşı otomatik denetlenir. Okul adı, şehir gibi dolaylı göstergeler puana girmez. |
| **Sınırlar** | Sözlükte olmayan beceri eşleşmez; deneyim süresi metinden çıkarıldığından kariyer arası/yarı zamanlı ayırt edilmez; kısa ya da taranmış özgeçmiş düşük puan alır (`insufficient_text` işareti); Türkçe/İngilizce dışında zayıf. |
| **Denetim** | Her hesaplama `audit_log`'a `CandidateFit / AutomatedAnalysis` olarak (ilan kimliği, aday sayısı) yazılır. Puan ve metin saklanmaz. |
| **Uçlar** | `GET /api/governance/ai/recruit-fit/{ilan}` (yönetici ve üstü), `GET …/card` (model kartı). ml-inference: `POST /recruit/fit`, `GET /recruit/fit/card`. |
| **Arayüz** | İlan ayrıntısı › Başvurular › "Uygunluk puanı (yardımcı)". |

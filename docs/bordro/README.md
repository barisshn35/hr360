# Türkiye bordrosu

Bu belge Türkiye bordrosu özelliklerinin kapsamını, dosya düzenlerini ve **doğrulanmamış** noktaları anlatır.
Kod: `apps/services/compensation-service` (`Payroll/`, `Controllers/PayrollTrController.cs`),
arayüz: Ücret › Bordro (`/panel/bordro`, sekmeler) ve dönem sayfası. Şema:
`scripts/sql/2026-10-21_payroll_tr.sql`.

> **Dış sistemler:** HR360 SGK e-Bildirge'ye, bankalara, KEP'e ya da muhasebe yazılımına
> **bağlanmaz**. Yalnızca dosya üretir; e-posta bildirimleri notification-service kuyruğuna yazılır
> (test ortamında mailpit). Hiçbir dosya düzeni resmî sistemde gerçek hesapla denenmedi.

## Temel bordro altyapısı

| Konu | Temel bordro altyapısı |
|---|---|
| Brütten nete hesap | `PayrollCalculator` (SGK/işsizlik, kümülatif GV, asgari ücret istisnası, damga). Değişmedi; yalnızca istisna aç/kapa bayrağı eklendi. |
| Parametreler | Yıl başına tek satır (asgari ücret, işveren SGK, teşvik, damga, tavan çarpanı, dilimler); işçi oranları kodda sabitti. |
| SGK APHB | Basit XML taslağı (eksik gün nedeni sabit 13, meslek kodu yok, tek belge). |
| Banka dosyası | Genel CSV, TR IBAN mod-97, toplam satırı. |
| Muhasebe | Logo/Mikro/Netsis/genel CSV, istek başına hesap kodları (kalıcı değildi), dengesizlikte yalnızca uyarı. |
| Kıdem/ihbar | offboarding'de tahmini hak ediş (engagement-service `SettlementCalculator`; tavan ortam değişkeni, ihbar GV'si yok, onay/belge yok). Artık tavan ve damga bordro parametrelerinden; `SEVERANCE_CEILING_TRY` kaldırıldı. |
| Pusula | İstemci tarafında yazdırma/PDF (`payslipPrint.ts`). |
| Ücret bantları | Kademe + yıl (alt/orta/üst); zam dönemi çalışma sayfasında compa-ratio. |
| Fark bordrosu | Yoktu. |

## Yıllık / yarıyıllık parametreler (bordro yetkilisi girer)

**Ürün kararı:** yıllık bordro parametrelerini kullanıcı (bordro yetkilisi: İK yöneticisi / şirket
yöneticisi) girer. Uygulama doğrulanmamış değer **varsaymaz**: kodda yıllık varsayılan yoktur
(`PayrollDefaults` kaldırıldı; yalnızca kanunla sabit iki değer `PayrollStatutory`'de: fazla mesai
%150, aylık 225 saat), yeni kurulumda tohum satırı yoktur.

`compensation_payroll_parameters`: yıl + **yürürlük ayı** (`ValidFromMonth`, ör. 7 = Temmuz) başına
satır. Dönem hesaplanırken ayına göre en son yürürlüğe giren satır kullanılır. Alanlar: brüt/net asgari
ücret, SGK işçi/işveren, işsizlik işçi/işveren, teşvik puanı, damga, SGK tavan çarpanı (taban = asgari
ücret), GV dilimleri, asgari ücret istisnası (aç/kapa), AGİ (2022 öncesi, **yalnızca bilgi**), kıdem
tavanı (Ocak–Haziran / Temmuz–Aralık), kaynak notu, giren kişi ve zaman. Değişiklik önce/sonra
değerleriyle denetim kaydına yazılır (`PayrollParameterSet`).

**Parametre yoksa hesaplama yapılmaz:** dönem hesaplama, simülasyon (`payroll/preview`), APHB doğrulama
ve dosyası, fark bordrosu adayları/uygulama ve kıdem/ihbar hesabı dönemin ayı için satır bulamazsa
`409 {"code":"payroll_parameters_missing","year":..,"month":..,"missing":"parameters"|"severanceCeiling","message":"<yıl> yılı ... Bordro › Parametreler ..."}`
döner. Kıdem tavanı ilgili yarıyıl için girilmemişse kıdem hesabı da 409 verir (H2 boşsa H1'e
düşülmez). Offboarding'deki tahmini hak ediş (engagement) tavan/damga girilmemişse kıdem tutarını
hesaplamaz (`severanceCeilingMissing: true`), ihbar ve izin ücreti yine gösterilir.

Uçlar (`/api/compensation/compensation/...`):

| Uç | Yetki | Not |
|---|---|---|
| `GET payroll/parameters` | bordro görüntüleyici | parametresi girilmiş yıllar ve yürürlük ayları |
| `GET payroll/parameters/{yıl}?month=` | bordro görüntüleyici | ayın geçerli değerleri (`configured=false` ise null), yılın satırları, `previous` (önceki yılların en son satırı) |
| `PUT payroll/parameters/{yıl}` | bordro yetkilisi | tüm zorunlu alanlar gelir; eksik/geçersizse `400 invalid_parameters` + alan listesi (`errors[].field`) |
| `POST payroll/parameters/{yıl}/preview` | bordro yetkilisi | kaydetmeden örnek hesap (verilen brüt + asgari ücret neti) |
| `DELETE payroll/parameters/{yıl}?validFromMonth=` | bordro yetkilisi | satırı siler; o aylar önceki satıra ya da "girilmedi"ye döner |
| `GET payroll/periods` | bordro görüntüleyici | her dönemde `parametersConfigured` |

Doğrulama (sunucu `PayrollParameterResolver.ValidateInput`, arayüz `lib/payrollParams.ts` aynı kurallar):
oranlar 0–1 kesir (SGK işçi/işveren ≤ %50, işsizlik ≤ %10, damga ≤ %10), teşvik 0–20 puan ve işveren
oranını aşamaz, tavan çarpanı 1–20 (tavan ≥ taban), dilimler 1–10, üst sınırlar artan, son dilim
"üzeri", oranlar azalmayan, net asgari ücret ≤ brüt, kıdem tavanı > 0 ya da boş, kaynak ≤ 300 karakter.

Arayüz (Bordro › Parametreler): yıl seçici (girildi/girilmedi), yürürlük satırları ve 12 ayın durumu
("Girildi (Ocak)" / "Girilmedi"), satır başına giren/zaman/kaynak; gruplu form (asgari ücret, GV dilimleri
tablosu, SGK oranları, taban/tavan, damga ve istisnalar, kıdem tavanı H1/H2), satır içi doğrulama,
virgüllü ondalık, yüzdeler yüzde olarak girilir; kaydetmeden örnek net/brüt hesap; "Önceki yıldan
doldur" (resmî kaynaklarla kontrol uyarısıyla). "Doğrulandı" bayrağı arayüzden kalktı: kaydetmek
onaylamaktır. Dönem ekranı, ayın parametresi yoksa uyarı + "Parametreleri gir" bağlantısı gösterir ve
Hesapla düğmesini kapatır.

**Eski kurulumlar:** önceki sürüm (2026-10-21 göçü) 2025/2026 için tohum satırları eklemişti
(`UpdatedBy = 'tohum'`). Bu satırlar **silinmez**; arayüz "Örnek değer — kontrol edip kaydedin" diye
işaretler, kullanıcı kontrol edip kaydedince "Girildi" olur. Göç dosyası artık tohum eklemez.

## SGK Aylık Prim ve Hizmet Belgesi (APHB)

Kapanmış dönemden XML ya da TXT. Dönem sayfasında **Doğrula** raporu (indirmeden önce): geçersiz/eksik
TCKN (hata, dosyaya alınmaz), eksik meslek kodu, prim günü 0–30 dışı, PEK asgari ücretin altında /
tavanın üstünde, nedeni bulunamayan eksik gün, SGDP, işten çıkış kodu bulunamadı (uyarı).

Alanlar: TCKN (engagement profilindeki şifreli alandan açılır), ad/soyad (Türkçe büyük harf), prim
günü, hak edilen ücret (dönem ücreti + fazla mesai, PEK'i aşmaz), prim/ikramiye (PEK − ücret), PEK,
eksik gün sayısı ve nedeni, işe giriş/işten çıkış günü, çıkış nedeni kodu (offboarding nedeninden),
meslek kodu, belge türü ve kanun (çalışan > şirket varsayılanı; SGDP işaretliyse belge türü 02).
XML'de belge türü + kanun başına bir `BELGE` grubu (kişi sayısı, toplam PEK ve gün).

TXT sütun sırası (noktalı virgül, başlıksız, CRLF, virgüllü ondalık):
`BelgeTuru;KanunNo;TCKimlikNo;Ad;Soyad;PrimGun;HakEdilenUcret;PrimIkramiye;EksikGunSayisi;EksikGunNedeni;IseGirisGun;IstenCikisGun;IstenCikisNedeni;MeslekKodu`

**Doğrulanmamış / belirsiz noktalar** (yüklemeden önce SGK'nın güncel e-Bildirge toplu dosya
kılavuzuyla karşılaştırın):

- XML eleman adları ve TXT sütun sırası kamuya açık örneklerden derlenmiştir; resmî şema değildir.
  "İlk soyadı", "uzaktan çalışma günü", "tahakkuk nedeni", "hizmet dönemi" gibi alanlar yazılmaz.
- Eksik gün kodları (varsayılan eşleme, şirket değiştirebilir): ücretsiz izin **21** (diğer ücretsiz
  izin), istirahat **01** (hastalık/doğum; varsayılan olarak ücretten düşmez), birden fazla neden
  **12**, eşleme yok **13** (diğer nedenler). Önceki sürüm ücretsiz izne 13 yazıyordu.
- İşten çıkış kodları (varsayılan): istifa 03, işveren feshi 04, belirli süreli sözleşme sonu 05,
  emeklilik 08, diğer 22.
- Belge türü varsayılanı 01, SGDP 02; kanun varsayılanı 05510 (teşviksiz). Teşvik kanunları ve
  belge türü kombinasyonları denetlenmez.
- SGDP'li çalışanın bordrosu SGDP oranlarıyla **hesaplanmaz** (uyarı verilir).
- Meslek kodu biçimi `0000.00` denetlenir; SGK meslek kodu listesinde varlığı denetlenmez.

"Ücretten düşer" işaretli izin türleri bordroda eksik gün sayılır (varsayılan yalnızca ücretsiz izin
— önceki davranışla aynı).

## Banka toplu ödeme dosyası

Şablonlar: `generic` (HR360 genel CSV, önceki biçim), **örnek şablon A** (noktalı virgüllü CSV,
virgüllü ondalık, ASCII büyük harf ad, sıra no ve TOPLAM satırı, ISO-8859-9), **örnek şablon B**
(sabit uzunluklu TXT: `H` başlık = firma kodu 10 + tarih 8 + borçlu IBAN 26 + kayıt sayısı 6 + toplam
kuruş 15; `D` detay = sıra 6 + IBAN 26 + ad 40 + tutar kuruş 15 + döviz 3 + açıklama 30; `T` toplam =
kayıt 6 + toplam kuruş 15) ve **özel düzen** (sütunlar: name, iban, amount, currency, description,
date, tckn, employeeNo, seq; ayırıcı, ondalık, başlık/toplam satırı, kodlama, ASCII, tarih biçimi).

> Örnek şablonlar yaygın banka dosyalarına benzer ama **hiçbir bankanın resmî biçimi olarak
> doğrulanmamıştır**. Bankanızın teknik dokümanıyla karşılaştırın; gerekirse özel düzeni kullanın.

IBAN: TR, 26 karakter, mod-97 = 1; geçersiz IBAN'lı kişi dosyaya alınmaz (uyarı). Dosyanın SHA-256
özeti dışa aktarım kaydında ve oluşturma/indirme denetim kayıtlarında tutulur. Banka dosyası tek
indirmeliktir, 24 saatte silinir (önceki kural).

## Muhasebe fişi

Şirketin hesap planı (ücret gideri, işveren SGK gideri, personele borçlar, ödenecek GV/damga/SGK,
avans ve diğer kesintiler) ve bölüm → masraf merkezi kodu eşlemesi (SGK ve dosya ayarları sekmesi).
Fiş borç = alacak denetiminden geçmezse dosya **üretilmez** (422 `journal_unbalanced`). Biçimler:
genel, Logo, Mikro, Netsis sütun düzeni (yazılımların içe aktarma şablonuyla birebir doğrulanmadı).
Not: tek kişilik bölümde masraf merkezi toplamı kişinin ücretini gösterir; dosya yalnızca muhasebeye
gider (yetkili alıcı).

## Kıdem ve ihbar

Ücret › Bordro › Kıdem ve ihbar (ayrılış kaydından ya da elle; offboarding ekranından bağlantı).
Giydirilmiş aylık brüt = aylık brüt + düzenli ek ödemeler (öneri: son 12 kapanmış pusulanın ek ödeme
ortalaması; İK değiştirir) + diğer sürekli yan haklar (elle). Kıdem = min(giydirilmiş, kıdem tavanı)
× hizmet günü / 365; GV'den istisna, damga vergisi. İhbar = giydirilmiş / 30 × 7 × hafta (2/4/6/8);
SGK yok, gelir vergisi (yılın kümülatif matrahıyla) ve damga. Kullanılmayan izin brüt gösterilir (son
bordroda ek ödeme olarak işlenir). Kıdem hakkı ve ihbar ödemesi ayrılış nedeninden önerilir, İK
değiştirebilir (otomatik karar yok). Akış: hazırla (taslak) → **başka** bir bordro yetkilisi onaylar
(görevler ayrılığı) → ibraname. İbraname metni governance belge şablonlarındaki "İbraname"
şablonundan (örnek şablonlara eklendi) ya da varsayılan metinden; hukuk birimince uyarlanmalıdır.
offboarding'deki tahmini hesap da kıdem tavanını artık bordro parametrelerinden okur.

## Fark bordrosu

Son 24 aydaki kapanmış dönemlerde, pusuladaki aylık brüt ile o dönem için geçerli ücret kaydı
farklıysa (geriye dönük zam) pusula saklı girdileriyle yeniden hesaplanır; brüt farkından önceden
ödenen farklar düşülür. İK seçtiği farkları açık bir döneme **"Fark: YYYY/AA"** ek ödemesi olarak
onaylar; kapanmış dönem açılmaz. Onaylayan o dönemin hazırlayanı sayılır (kapatamaz). Negatif fark
yalnızca gösterilir.

**Sınırlama:** fark ödendiği ayın bordrosunda vergilendirilir ve o ayın PEK'ine girer. SGK açısından
ücret farkının ait olduğu aya ek APHB ile bildirilmesi gerekebilir; bu sürüm ek belge üretmez.

## e-Bordro

Kapanmış dönemde **Yayımla**: her pusulanın kanonik JSON'u (sabit alan sırası) SHA-256 ile özetlenir,
kopyası AES-256-GCM ile şifreli saklanır (`TENANT_SECRET_KEYS`, anahtar yenilemede yeniden yazılır),
çalışana uygulama içi + (e-postası varsa) e-posta bildirimi gider — **tutar yazmaz**. Çalışanın açması
"okundu" (ilk/son açılış, sayaç); "Okudum, teslim aldım" onaylanan özet, zaman ve IP /24 önekiyle
kaydedilir (denetim kaydı). Dönem yeniden açılıp pusula değişirse özet tutmaz; yeniden yayımlama onayı
sıfırlar. Sunucu tarafında PDF üretilmez (gömülü Türkçe yazı tipi gerektirir); PDF tarayıcının
yazdırma penceresinden alınır. Bu bir **e-imza değildir** (e-imza motoru tek: governance
`SignatureEngine`); gerekirse teslim onayı ileride bu motora bağlanabilir.

## Ücret bantları ve compa-ratio

Bantlara yürürlük tarihi eklendi (aynı kademede yıl içinde yeni bant). Bant eşlemesi: çalışanın ücret
kaydındaki kademe + para birimi, bugün yürürlükte olan en yeni bant. Bant uyumu sekmesi: bant ve
bölüm bazında altında/içinde/üstünde sayıları ve ortalama compa-ratio (5 kişiden az grup gizli);
kişi bazlı compa-ratio yalnızca İK/ücret görme izni, görüntüleme kaydedilir. Ücret adaleti analizi
zaten kademe ve unvanı meşru etken olarak kullanıyor; ML tarafında değişiklik yapılmadı.

## Uçlar (gateway öneki `/api/compensation/compensation`)

| Uç | Yetki |
|---|---|
| `GET/PUT/DELETE payroll/parameters/{yıl}` (`?month=`, `?validFromMonth=`) | okuma: bordro görüntüleyen; yazma: bordro yetkilisi |
| `GET/PUT payroll/settings` | okuma/yazma |
| `GET payroll/sgk/employees`, `PUT payroll/sgk/employees/{id}` | okuma/yazma |
| `GET payroll/periods/{id}/sgk/validation` | bordro yetkilisi |
| `POST payroll/periods/{id}/exports` (`kind`, `format`, `payDate`) | bordro yetkilisi |
| `GET payroll/retro/candidates`, `POST payroll/retro/apply`, `GET payroll/retro` | yetkili / görüntüleyen |
| `POST severance/preview`, `POST severance`, `GET severance`, `POST severance/{id}/decide`, `GET severance/{id}/document` | bordro yetkilisi (liste: görüntüleyen) |
| `POST payroll/periods/{id}/e-payslips/publish`, `GET payroll/periods/{id}/e-payslips` | yetkili / görüntüleyen |
| `GET payslips/{id}/e-payslip`, `POST payslips/{id}/acknowledge` | çalışanın kendisi (görüntüleme İK da) |
| `GET bands/compa-ratios`, `GET bands/coverage` | İK ve ücret görme izni |

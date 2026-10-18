# HR360 — Yenilikler

Bu dosya uygulamadaki **Yenilikler** panelinin kaynağıdır. Her dalga tek başlık altında, sade Türkçeyle
özetlenir. Biçim: `## <dalga> · <YYYY-AA-GG> · <başlık>` ve altında `- ` ile başlayan maddeler.
Değiştirdikten sonra arayüz verisini üretin ve çevirileri tamamlayın:

```
cd apps/web && node scripts/changelog.mjs && node scripts/i18n-check.mjs --write && node scripts/i18n-check.mjs --missing
```

## 16 · 2026-10-18 · SMS ve güvenlik iyileştirmeleri
- SMS bildirimleri: şirket yöneticisi Ayarlar'da Netgsm ya da İleti Merkezi hesabını bağlar, deneme SMS'i gönderir ve günlük gönderim sınırı koyar. Telefon numaraları yurt dışına aktarılmaz.
- Çalışanlar Profilim › Bildirimler'den SMS almayı kapatabilir; sessiz saatlerde SMS ertelenir, güvenlik iletileri her zaman gider.
- Şirket verileri artık veritabanı düzeyinde de ayrılır: her servis kendi en az yetkili kullanıcısıyla bağlanır, bir şirketin kayıtları başka şirketin oturumunda görünmez.
- Platform › Bakım'da güvenlik durumu kartı: veritabanı yalıtımı, HTTPS, yedekler ve izleme açık mı tek bakışta; kapalıysa tek tıkla açılır.
- Zamanlanmış işler, servis birden çok kopya çalışsa bile yalnızca bir kopyada yürür; güncelleme servislerin veritabanına gerçekten bağlandığını bekler.
- Yeni kurulumlarda eksik kalan 1.1 tabloları (ayın çalışanı, etkinlikler, bakım ekranı) eklendi; güncellemeler yalnızca yeni veritabanı değişikliklerini uygular.
- Android uygulamasında konumla giriş-çıkış kaydı çalışır; büyük vardiya ve zam işlemleri daha hızlı.

## 15 · 2026-10-16 · Topluluk, günlük işler ve bakım
- Yapılacaklarım: onay, imza, eğitim, anket, eksik belge ve oylama gibi sizden beklenen her şey tek listede, son tarihe göre sıralı.
- İzin takvimi: ekibin kim ne zaman izinde olduğunu ayın takviminde görün; köprü günleri önerilir, takvimden doğrudan izin isteyebilirsiniz.
- Yöneticiler için "Ekibim bugün" kartı: izindekiler, yaklaşan 1:1'ler, ekipteki kutlamalar, bekleyen onaylar ve gecikmiş eğitimler.
- Fikir kutusu: öneri paylaşın (isterseniz adsız), destekleyin, yorum yapın; İK durumunu ve yanıtını yazar.
- Etkinlikler ve gönüllülük: kontenjan ve bekleme listesi, takvime ekleme, gönüllülük saatleri ve yıllık özet.
- Takdir puanları ve ödül kataloğu (isteğe bağlı): takdirle puan gönderin, puanları ödüllerle takas edin.
- Grup kartı: doğum günü ya da veda için herkesin imzaladığı kart; teslim edilene kadar alıcıdan gizli.
- Kahve eşleştirme (isteğe bağlı): katılanlar düzenli olarak farklı departmanlardan biriyle eşleşir.
- Rozet koleksiyonu ve kilometre taşları profilde; duyurulara tepki ve yorum eklenebilir.
- İlk 90 gün uyum formları, haftalık check-in, adayın mülakat saatini kendisinin seçmesi, norm kadro ve boş kadrodan ilan.
- Eksik belge takibi, kullanılmamış izin raporu, toplu ücret güncelleme, yıllık toplam kazanç beyanı ve hazır kural tarifleri.
- Ofis planında geçen haftayı kopyalama; "kim nerede" listesinde izin türü artık gizli.
- Platform yöneticisi güncelleme, yedek, doğrulama ve geri yüklemeyi Platform > Bakım ekranından yapar.
- Boş bir şirket tek tıkla örnek verilerle doldurulup sonra temizlenebilir; Android uygulaması Ayarlar'dan indirilebilir.
- Ana renk değiştirildiğinde logo da yeni renge uyar; yeni sürüm yayımlanınca sayfayı yenileme önerisi gösterilir.

## 14 · 2026-10-16 · Ayın çalışanı, fotoğraflar ve toplu içe aktarma
- Ayın çalışanı geldi: çalışanlar departmanlarında aday gösterip oy verir, kazanan bir sonraki oylamaya kadar herkesin ana sayfasında fotoğrafıyla yer alır.
- Oylama ana departman, her departman ya da iki aşamalı (önce alt departmanlar, ertesi gün ana departman) yapılabilir; İK takvimi, oy katsayılarını ve gizlilik kuralını ayarlar.
- Oylar varsayılan olarak gizlidir; kural oy verirken gösterilir ve açılmış bir oylamada değiştirilemez. Oylama yaklaşınca, açılınca ve kapanmadan önce e-posta gelir.
- Kimin kaç kez kazandığını gösteren şirket geneli ve departman içi sıralama eklendi.
- Çalışanlar profil fotoğrafı yükleyebilir (İK da yükleyebilir); fotoğraf yalnızca şirket içinde avatarlarda görünür.
- Çalışan, departman ve ekipler Excel'den ya da uygulama içindeki tabloya yazarak/yapıştırarak toplu eklenebilir; çalışan dosyasında unvan da okunur.
- Yeni şirketlerde iki adımlı doğrulama herkes için varsayılan olarak zorunlu; şirket yöneticisi Güvenlik ekranından gevşetebilir.
- Aday önerirken özgeçmiş eklenebilir; çalışan kaydı olmayan hesapların profil sayfası düzeltildi; sunucudan kaldırma betiği (uninstall.sh) eklendi.

## 13 · 2026-10-14 · Son düzenleme: hata düzeltmeleri ve temizlik
- Yöneticiler yalnızca kendi bölümlerine ve bugünden sonraki günlere vardiya atayabilir; yanlış atama geri alınabilir.
- Vardiya çizelgesi, ekipler ve takas listesi yalnızca kişinin kendi ekibini ve sorumlu olduğu bölümleri gösterir.
- Organizasyon senaryosu taslaklarını yalnızca sahibi ve İK görür; ücret görme yetkisi olmayanlara planlanan maaş gösterilmez.
- Kör mülakat değerlendirmesinde ortalama puan, panel tamamlanana kadar aday panosunda da gizlenir.
- Bağlantı yokken izin ve masraf talepleri gerçekten taslak olarak saklanır; aydınlatma metni bandındaki bağlantı düzeltildi.
- Kıdem ve ihbar vergisinde yalnızca kapanmış dönemler sayılır; yıl ortası başlayanlar için devreden vergi matrahı girilebilir.
- Onaylanmış ileri tarihli izin İK ya da yönetici tarafından gerekçeyle iptal edilebilir; 28 Ekim ve arifeler yarım gün sayılır.
- KVKK veri paketi tüm modüllerdeki kişisel verileri kapsar; kapanmış başvurular değiştirilemez.
- İngilizce arayüzde model kartı, envanter ve imha ekranları çevrildi; imzalı teklif mektubu artık hiç değiştirilmez.
- Gereksiz dosyalar ve kullanılmayan paketler kaldırıldı, güvenlik güncellemeleri alındı.

## 12 · 2026-10-07 · Mobil, kişiselleştirme ve işletim
- Telefonda bağlantı koptuğunda izin ve masraf talepleri cihazda taslak olarak saklanır, bağlantı gelince kendiliğinden gönderilir.
- Uygulamayı ana ekrana ekleme önerisi geldi; iPhone ve iPad için adım adım yönerge gösterilir.
- Komut paleti (Ctrl+K) artık eylem de yapar: izin talebi oluştur, masraf ekle, kişi ara, ana paneli düzenle.
- Liste ekranlarında arama ve filtreleri görünüm olarak kaydedebilir, aynı filtrelerle açılan bağlantıyı paylaşabilirsiniz.
- Onay kutusunda toplu onay ve retten sonra her talebin sonucu tek tek gösterilir.
- Ana paneldeki kartları gösterip gizleyebilir ve sıralayabilirsiniz; düzeniniz her cihazda aynıdır.
- Bu "Yenilikler" paneli eklendi; okumadığınız notlar rozetle gösterilir.
- Entegrasyonlar için doğrulama listesi, hesap açma/kapama otomasyonu, webhook yeniden gönderimi ve kapsamlı API anahtarları geldi.
- İzleme alarmları, kayıt saklama sınırları, veritabanı indeksleri ve daha hızlı tarayıcı testleri eklendi.

## 11 · 2026-10-07 · İşe alım ve gelişim
- Çalışanlar açık pozisyonlara aday önerebilir; öneri programı ve ödüller İK tarafından yönetilir.
- Adaylar başvuru durumunu kişisel bağlantıdan izleyebilir; ilanlar arama motorları için yapılandırılmış veriyle yayımlanır.
- İş teklifleri e-imzayla imzalanır; mülakat puan kartları ve işe alım hunisi raporu eklendi.
- Performans kalibrasyonu, OKR hizalama ve anonim 360 derece geri bildirim geldi.
- Kariyer yolları ve zorunlu eğitimler için son tarih ile hatırlatmalar eklendi.

## 10 · 2026-10-07 · KVKK ve makine öğrenmesi
- VERBİS bilgileri, başvuruların yasal süre takibi ve kişisel veri paketi indirme eklendi.
- Aydınlatma metni değişince yeniden rıza istenir; imha kapsamı ve tutanakları genişletildi.
- Devir riski modeli kiracının izniyle kendi verisiyle eğitilebilir; öneriler her zaman insan onayıyla uygulanır.
- Aday uygunluk puanı açıklamasıyla birlikte gösterilir.

## 9 · 2026-10-07 · İzin, puantaj ve vardiya
- İzin kuralları, saatlik izin ve resmî tatil yönetimi genişletildi.
- Puantaj ve vardiya planlamasında dinlenme süreleri ve haftalık çalışma sınırları denetlenir.
- Toplu görüntüleme kaydı ve imha tutanağındaki hatalar giderildi.

## 8 · 2026-10-06 · Türkiye bordrosu
- 2026 bordro parametreleri, APHB, banka ve muhasebe dosyaları üretimi eklendi.
- Kıdem ve ihbar tazminatı, fark bordrosu ve e-bordro geldi.
- Ücret bantları bordro ile bağlantılı çalışır.

## 7 · 2026-10-06 · Güvenlik, organizasyon şeması ve yapay zekâ
- Denetim kaydı zinciri, iç anahtar değişimi, dosya türü denetimi ve konteyner sertleştirme yapıldı.
- Çok adımlı kimlik doğrulama politikası, şüpheli giriş uyarısı, süreli platform erişimi ve görevler ayrılığı eklendi.
- Organizasyon şeması 2B ve 3B görünümler, zaman kaydırıcısı ve senaryo karşılaştırması kazandı.
- Serbest metinde kişisel veri uyarısı, masraf ve bordro anomalisi, anlamsal arama ve ücret adaleti analizi eklendi.

## 6 · 2026-10-05 · Kalite ve test
- Tek e-imza motoru tüm belgelerde kullanılır.
- Temiz kurulum için demo verisi betiği ve test kalıntılarının otomatik temizliği eklendi.
- Rol bazlı kalite kontrolünde bulunan arayüz ve yetki hataları düzeltildi.

## 5 · 2026-10-04 · KVKK operasyonları, bordro ekosistemi ve İK süreçleri
- KVKK başvuruları, erişim kayıtları ve erişim güvenliği araçları eklendi.
- Masraf, seyahat ve harcırah süreçleri bordroyla bütünleşti.
- İSG, etik hattı, disiplin ve belge talebi gibi İK süreçleri geldi.
- Sohbet botu genişletildi; servisler arası istekler uçtan uca izlenebilir.

## 4 · 2026-10-03 · Bordro, mesai ve onay akışları
- Bordro, fazla mesai, QR/kart/PIN ile giriş-çıkış ve belge talebi eklendi.
- Çok adımlı onay akışları, koşullar ve vekâlet tanımlanabilir.
- Telefona anlık bildirim (Web Push) ve uygulama olarak yükleme desteği geldi.
- Yedekleme ve güncelleme betikleri iyileştirildi.

## 3 · 2026-10-03 · Entegrasyonlar ve İngilizce arayüz
- Slack ve Microsoft Teams'te kişiye özel Onayla/Reddet mesajları ve komutlar eklendi.
- Google Takvim ve Outlook bağlantısı; 1:1 ve mülakatlar için Zoom, Teams ve Meet toplantıları geldi.
- İsteğe bağlı dil modeli desteği eklendi (varsayılan kapalı, kişisel veri için ayrı izin).
- Arayüzün tamamı İngilizce kullanılabilir; KVKK envanteri, yurt dışı aktarım kilidi ve imha tutanağı eklendi.

## 2 · 2026-10-02 · Yeni arayüz, modüller ve izleme
- Arayüz baştan tasarlandı: koyu tema, üst menü, rıhtım ve animasyonlar.
- Topluluk, büyüme, işe alım ve içgörü modülleri eklendi.
- İzleme (Prometheus, Grafana, Loki) ve e-posta, Slack, Teams alarmları geldi.
- Sürekli entegrasyon, otomatik testler ve yük testleri kuruldu.

## 1 · 2026-09-25 · Temel platform
- Tek sunucuya kendi kendine kurulan, çok kiracılı HR360 yayımlandı.
- Kurulum Docker'ı ve HTTPS sertifikasını kendisi ayarlar; SMTP kurulumda sorulur.
- Kiracı yalıtımı, kendi talebini onaylama ve yetki açıkları kapatıldı; askıya alınan kiracının erişimi anında kesilir.

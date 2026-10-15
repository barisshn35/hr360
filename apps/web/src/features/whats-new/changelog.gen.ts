// OTOMATİK ÜRETİLDİ — elle değiştirmeyin. Kaynak: docs/CHANGELOG-tr.md
// Yeniden üretmek için: cd apps/web && node scripts/changelog.mjs
import { tx } from '@/lib/i18n'

export interface ChangelogEntry { id: string; date: string; title: string; items: string[] }

/** En yeni dalga başta. Metinler arayüz dilinde döner. */
export const changelog = (): ChangelogEntry[] => [
  { id: '13', date: '2026-10-14', title: tx('Son düzenleme: hata düzeltmeleri ve temizlik'), items: [
    tx('Yöneticiler yalnızca kendi bölümlerine ve bugünden sonraki günlere vardiya atayabilir; yanlış atama geri alınabilir.'),
    tx('Vardiya çizelgesi, ekipler ve takas listesi yalnızca kişinin kendi ekibini ve sorumlu olduğu bölümleri gösterir.'),
    tx('Organizasyon senaryosu taslaklarını yalnızca sahibi ve İK görür; ücret görme yetkisi olmayanlara planlanan maaş gösterilmez.'),
    tx('Kör mülakat değerlendirmesinde ortalama puan, panel tamamlanana kadar aday panosunda da gizlenir.'),
    tx('Bağlantı yokken izin ve masraf talepleri gerçekten taslak olarak saklanır; aydınlatma metni bandındaki bağlantı düzeltildi.'),
    tx('Kıdem ve ihbar vergisinde yalnızca kapanmış dönemler sayılır; yıl ortası başlayanlar için devreden vergi matrahı girilebilir.'),
    tx('Onaylanmış ileri tarihli izin İK ya da yönetici tarafından gerekçeyle iptal edilebilir; 28 Ekim ve arifeler yarım gün sayılır.'),
    tx('KVKK veri paketi tüm modüllerdeki kişisel verileri kapsar; kapanmış başvurular değiştirilemez.'),
    tx('İngilizce arayüzde model kartı, envanter ve imha ekranları çevrildi; imzalı teklif mektubu artık hiç değiştirilmez.'),
    tx('Gereksiz dosyalar ve kullanılmayan paketler kaldırıldı, güvenlik güncellemeleri alındı.'),
  ] },
  { id: '12', date: '2026-10-07', title: tx('Mobil, kişiselleştirme ve işletim'), items: [
    tx('Telefonda bağlantı koptuğunda izin ve masraf talepleri cihazda taslak olarak saklanır, bağlantı gelince kendiliğinden gönderilir.'),
    tx('Uygulamayı ana ekrana ekleme önerisi geldi; iPhone ve iPad için adım adım yönerge gösterilir.'),
    tx('Komut paleti (Ctrl+K) artık eylem de yapar: izin talebi oluştur, masraf ekle, kişi ara, ana paneli düzenle.'),
    tx('Liste ekranlarında arama ve filtreleri görünüm olarak kaydedebilir, aynı filtrelerle açılan bağlantıyı paylaşabilirsiniz.'),
    tx('Onay kutusunda toplu onay ve retten sonra her talebin sonucu tek tek gösterilir.'),
    tx('Ana paneldeki kartları gösterip gizleyebilir ve sıralayabilirsiniz; düzeniniz her cihazda aynıdır.'),
    tx('Bu "Yenilikler" paneli eklendi; okumadığınız notlar rozetle gösterilir.'),
    tx('Entegrasyonlar için doğrulama listesi, hesap açma/kapama otomasyonu, webhook yeniden gönderimi ve kapsamlı API anahtarları geldi.'),
    tx('İzleme alarmları, kayıt saklama sınırları, veritabanı indeksleri ve daha hızlı tarayıcı testleri eklendi.'),
  ] },
  { id: '11', date: '2026-10-07', title: tx('İşe alım ve gelişim'), items: [
    tx('Çalışanlar açık pozisyonlara aday önerebilir; öneri programı ve ödüller İK tarafından yönetilir.'),
    tx('Adaylar başvuru durumunu kişisel bağlantıdan izleyebilir; ilanlar arama motorları için yapılandırılmış veriyle yayımlanır.'),
    tx('İş teklifleri e-imzayla imzalanır; mülakat puan kartları ve işe alım hunisi raporu eklendi.'),
    tx('Performans kalibrasyonu, OKR hizalama ve anonim 360 derece geri bildirim geldi.'),
    tx('Kariyer yolları ve zorunlu eğitimler için son tarih ile hatırlatmalar eklendi.'),
  ] },
  { id: '10', date: '2026-10-07', title: tx('KVKK ve makine öğrenmesi'), items: [
    tx('VERBİS bilgileri, başvuruların yasal süre takibi ve kişisel veri paketi indirme eklendi.'),
    tx('Aydınlatma metni değişince yeniden rıza istenir; imha kapsamı ve tutanakları genişletildi.'),
    tx('Devir riski modeli kiracının izniyle kendi verisiyle eğitilebilir; öneriler her zaman insan onayıyla uygulanır.'),
    tx('Aday uygunluk puanı açıklamasıyla birlikte gösterilir.'),
  ] },
  { id: '9', date: '2026-10-07', title: tx('İzin, puantaj ve vardiya'), items: [
    tx('İzin kuralları, saatlik izin ve resmî tatil yönetimi genişletildi.'),
    tx('Puantaj ve vardiya planlamasında dinlenme süreleri ve haftalık çalışma sınırları denetlenir.'),
    tx('Toplu görüntüleme kaydı ve imha tutanağındaki hatalar giderildi.'),
  ] },
  { id: '8', date: '2026-10-06', title: tx('Türkiye bordrosu'), items: [
    tx('2026 bordro parametreleri, APHB, banka ve muhasebe dosyaları üretimi eklendi.'),
    tx('Kıdem ve ihbar tazminatı, fark bordrosu ve e-bordro geldi.'),
    tx('Ücret bantları bordro ile bağlantılı çalışır.'),
  ] },
  { id: '7', date: '2026-10-06', title: tx('Güvenlik, organizasyon şeması ve yapay zekâ'), items: [
    tx('Denetim kaydı zinciri, iç anahtar değişimi, dosya türü denetimi ve konteyner sertleştirme yapıldı.'),
    tx('Çok adımlı kimlik doğrulama politikası, şüpheli giriş uyarısı, süreli platform erişimi ve görevler ayrılığı eklendi.'),
    tx('Organizasyon şeması 2B ve 3B görünümler, zaman kaydırıcısı ve senaryo karşılaştırması kazandı.'),
    tx('Serbest metinde kişisel veri uyarısı, masraf ve bordro anomalisi, anlamsal arama ve ücret adaleti analizi eklendi.'),
  ] },
  { id: '6', date: '2026-10-05', title: tx('Kalite ve test'), items: [
    tx('Tek e-imza motoru tüm belgelerde kullanılır.'),
    tx('Temiz kurulum için demo verisi betiği ve test kalıntılarının otomatik temizliği eklendi.'),
    tx('Rol bazlı kalite kontrolünde bulunan arayüz ve yetki hataları düzeltildi.'),
  ] },
  { id: '5', date: '2026-10-04', title: tx('KVKK operasyonları, bordro ekosistemi ve İK süreçleri'), items: [
    tx('KVKK başvuruları, erişim kayıtları ve erişim güvenliği araçları eklendi.'),
    tx('Masraf, seyahat ve harcırah süreçleri bordroyla bütünleşti.'),
    tx('İSG, etik hattı, disiplin ve belge talebi gibi İK süreçleri geldi.'),
    tx('Sohbet botu genişletildi; servisler arası istekler uçtan uca izlenebilir.'),
  ] },
  { id: '4', date: '2026-10-03', title: tx('Bordro, mesai ve onay akışları'), items: [
    tx('Bordro, fazla mesai, QR/kart/PIN ile giriş-çıkış ve belge talebi eklendi.'),
    tx('Çok adımlı onay akışları, koşullar ve vekâlet tanımlanabilir.'),
    tx('Telefona anlık bildirim (Web Push) ve uygulama olarak yükleme desteği geldi.'),
    tx('Yedekleme ve güncelleme betikleri iyileştirildi.'),
  ] },
  { id: '3', date: '2026-10-03', title: tx('Entegrasyonlar ve İngilizce arayüz'), items: [
    tx('Slack ve Microsoft Teams\'te kişiye özel Onayla/Reddet mesajları ve komutlar eklendi.'),
    tx('Google Takvim ve Outlook bağlantısı; 1:1 ve mülakatlar için Zoom, Teams ve Meet toplantıları geldi.'),
    tx('İsteğe bağlı dil modeli desteği eklendi (varsayılan kapalı, kişisel veri için ayrı izin).'),
    tx('Arayüzün tamamı İngilizce kullanılabilir; KVKK envanteri, yurt dışı aktarım kilidi ve imha tutanağı eklendi.'),
  ] },
  { id: '2', date: '2026-10-02', title: tx('Yeni arayüz, modüller ve izleme'), items: [
    tx('Arayüz baştan tasarlandı: koyu tema, üst menü, rıhtım ve animasyonlar.'),
    tx('Topluluk, büyüme, işe alım ve içgörü modülleri eklendi.'),
    tx('İzleme (Prometheus, Grafana, Loki) ve e-posta, Slack, Teams alarmları geldi.'),
    tx('Sürekli entegrasyon, otomatik testler ve yük testleri kuruldu.'),
  ] },
  { id: '1', date: '2026-09-25', title: tx('Temel platform'), items: [
    tx('Tek sunucuya kendi kendine kurulan, çok kiracılı HR360 yayımlandı.'),
    tx('Kurulum Docker\'ı ve HTTPS sertifikasını kendisi ayarlar; SMTP kurulumda sorulur.'),
    tx('Kiracı yalıtımı, kendi talebini onaylama ve yetki açıkları kapatıldı; askıya alınan kiracının erişimi anında kesilir.'),
  ] },
]

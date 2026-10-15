# Arayüz dili (Türkçe / İngilizce)

Panel Türkçe ve İngilizce kullanılabilir. Dil üst çubuktaki **EN / TR** düğmesiyle
değiştirilir; seçim tarayıcıda saklanır. Bağlantıya `?lang=en` eklemek de dili seçer.
Keycloak giriş ekranı da seçili dilde açılır.

## Nasıl çalışır

- Kodda metinler Türkçe yazılır ve `tx()` ile sarılır; Türkçe metnin kendisi anahtardır:

  ```tsx
  <Button>{tx('Kaydet')}</Button>
  toast.ok(tx('{0} kayıt içe aktarıldı', [n]))
  ```

- İngilizce karşılıklar `apps/web/src/locales/en.json` dosyasındadır. Sözlük yalnızca
  İngilizce seçildiğinde, uygulama yüklenmeden önce indirilir. Türkçe kullanıcılar için ek
  yük yoktur.
- Sayı ilk yer tutucudaysa tekil ve çoğul `|` ile ayrılır: `"{0} record|{0} records"`.
- Yüzde için `pct(40)` kullanılır (Türkçe `%40`, İngilizce `40%`). Tarih ve sayı
  biçimleri `appLocale` ile seçili dile göre yapılır.
- **Sunucu iletileri** (hata iletileri, bildirim konusu ve metni, ekip sağlığı uyarıları,
  olay radarı özetleri, rozet adları…) servislerden Türkçe gelir. İngilizce arayüzde API
  yanıtları ve hata iletileri sözlükteki `@server:` kayıtlarıyla çevrilir. Değişken içeren
  iletiler kalıpla eşleşir: `"@server:{0} gündür izin kullanmadı"` → `"No leave taken for {0} days"`.
  Sözlükte karşılığı olmayan ileti olduğu gibi gösterilir.
- Kullanıcı verisi çevrilmez: çalışan adı, departman, pozisyon, kural adı, gerekçe gibi
  alanlar girildikleri dilde kalır.

## Yeni metin eklerken

1. Metni `tx('…')` ile sarın. Değişkenler için `tx('… {0} …', [değer])` kullanın; metni
   değişkenle birleştirmeyin, çünkü İngilizcede sözcük sırası farklı olabilir.
2. `npm run i18n:check -- --write` eksik anahtarları `en.json`'a boş değerle ekler.
   Karşılıklarını yazın.
3. `npm run i18n:check` eksik ya da yer tutucusu uyumsuz anahtar kalmadığını doğrular.
   CI bu denetimi her push'ta çalıştırır.

Tarayıcı testi `tests/e2e/test_english.py` tüm ekranları İngilizce açar. Hata
olmamasını ve sözlükteki bir Türkçe metnin çevrilmeden görünmemesini denetler.

## Rapor asistanı ve İK asistanı

İstekler `X-HR360-Lang: tr|en` başlığıyla gider (`apiFetch` ekler). Governance servisi
yanıtı bu dilde üretir:

- **Rapor asistanı** soruları Türkçe ya da İngilizce anlar ("Leave days by department in
  the last 6 months", "Son 6 ayda departmanlara göre izin günleri"). Yorum, sütun başlıkları,
  dönem adları ve örnek sorular arayüz dilindedir. İngilizce arayüzde Türkçe soru da anlaşılır,
  yanıt İngilizce gelir.
- **İK asistanı** (izin bakiyesi, bekleyen onaylar, tatiller, kim izinde, masraflar, bordro,
  yöneticinin analitik soruları) aynı şekilde iki dilde yanıt verir.

Test: `tests/integration/test_report_lang.py`.

## E-postalar

Her kullanıcının dil tercihi sunucuda saklanır. Üst çubuktaki **EN / TR** düğmesi seçimi
hem tarayıcıya hem sunucuya yazar:

- `PUT /api/notification/notifications/preferences/me` (`notification_preferences` tablosu,
  kiracı + çalışan anahtarlı). Bildirim servisi e-postanın konusunu, gövdesini ve çerçevesini
  (`<html lang>`, düğme, alt bilgi) **alıcının** diline göre üretir. Tarih biçimi de dile göre
  değişir. Kayıtlı bildirimler (`notification_messages."Language"`) üretildikleri dili saklar.
- `PUT /api/tenant/my-tenant/me/locale` Keycloak kullanıcısının `locale` özniteliğini yazar;
  giriş ekranı ve parola sıfırlama e-postaları da bu dilde gelir.
- Başka bir tarayıcıda (dil seçimi yokken) giriş yapılınca sunucudaki tercih uygulanır.

Tercih kaydı olmayan kullanıcıya e-posta Türkçe gider. Testler:
`tests/integration/test_email_lang.py` (Mailpit'te İngilizce ve Türkçe e-posta),
`tests/e2e/test_english.py::test_dil_tercihi_sunucuda_saklanir`.

## Bilinen sınırlar

- **Sohbet botu** komutları (`onaylarım`, `bakiye`, `izindekiler`, `kimnerede`) Türkçedir.
- **API ile doğrudan gönderilen bildirimlerde** (`POST /api/notifications`) metni çağıran
  verir; yalnızca e-posta çerçevesi alıcının dilindedir.
- **Sistem uyarıları** (Alertmanager) Türkçedir.
- **Yapay zekâ çıktıları** (ilan taslağı, özet) Türkçe istenir.

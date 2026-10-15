# HR360 testleri

| Katman | Yer | Ne sınar | Nasıl |
|---|---|---|---|
| .NET birim | `tests/dotnet/` (13 proje, ~730 test) | Her servisin iş kuralları: bordro ve Türkiye dosyaları, izin hakkı, vardiya kuralları, KVKK imha planları, görevler ayrılığı, anahtar halkası, imza motoru, kural motoru, sohbet biçimleri | `scripts/test.sh unit` (CI'da her push) |
| Arayüz birim | `apps/web/src/**/*.test.ts` (~37 dosya) | Bordro hesabı, org şeması yerleşimleri (5000 kişilik sentetik), adres durumu, kişisel veri uyarısı, filigran, çevrimdışı kuyruk, çeviri bütünlüğü | `cd apps/web && npm test` |
| ML birim | `apps/ml-inference/tests/` (~15 dosya) | Devir modeli ve kalibrasyon, adillik, kayma, kişisel veri dedektörü, masraf/bordro anomalisi, izin tahmini, beceri çıkarımı, ücret adaleti, anlamsal arama, vardiya optimizasyonu, aday uygunluğu | `scripts/test.sh unit` |
| API entegrasyon | `tests/integration/` (36 betik) | Uçtan uca API akışları (KVKK, bordro, izin, işe alım, e-imza, performans, kimlik, parola linkleri, herkese açık site, webhook, ML, sohbet/takvim/LLM, önbellek); ardından servis günlüklerinde hata taraması | `HR360_TEST_ENV=1 scripts/test.sh integration` |
| Tarayıcı (e2e) | `tests/e2e/` | Beş rolle tüm ekranlarda hata olmaması, İngilizce arayüz, erişilebilirlik; izin talebi → yönetici onayı → Kafka → onaylı izin ve bildirim | `HR360_TEST_ENV=1 scripts/test.sh e2e` |

Entegrasyon ve e2e testleri **çalışan bir test kurulumu** ister ve yalnızca `HR360_TEST_ENV=1` ile çalışır
(`scripts/test.sh integration|e2e|all`; değişken yoksa betik açıklamayla durur, `unit` her yerde çalışır).
Bu testler demo şirketine test verisi yazar ve servisleri sahte sağlayıcı katmanıyla
(`deploy/testing/chat-mock.yml`) yeniden oluşturur; **gerçek müşteri verisi olan bir kurulumda çalıştırmayın**.
Demo şirketi gerekir: kurulum `./install.sh --demo` ile yapılmış olmalı (ardından `tests/support/seed_demo.py`).

Ayrıca:

- `tests/credentials.json` dosyası gerekir (`tests/credentials.example.json`'dan kopyalayın). Bu dosya git'e girmez. Alternatif: `HR360_TEST_USERS` ortam değişkeni.
- Adres `HR360_BASE_URL` ile verilir; varsayılanı `http://localhost`.
- Python bağımlılıkları: `pip install pytest playwright pytest-xdist` ve `playwright install chromium`.
- e2e paralel çalışır (pytest-xdist varsa): önce `-n ${HR360_E2E_WORKERS:-4} -m "not serial"`, sonra
  `-m serial` (kullanıcının sunucudaki ortak durumunu değiştiren testler, ör. dil tercihi). Oturum durumu
  rol başına bir kez alınıp (`storage_state`) yeniden kullanılır. `HR360_E2E_WORKERS=0` sıralı çalıştırır.
  Yeni test bir kullanıcının sunucudaki ayarını (dil, tercih vb.) geçici değiştiriyorsa `@pytest.mark.serial`
  ekleyin. Ayrıntı: `tests/e2e/conftest.py`.

Erişim jetonları test kullanıcılarıyla tarayıcıdan giriş yapılarak alınır ve süreleri dolana kadar önbellekte tutulur.

Gerçek Slack, Teams, Google, Microsoft ve Zoom hesabı gerekmez:

- `tests/integration/chatmock.py` bu servislerin ve LLM'in API'lerini taklit eder.
- `deploy/testing/chat-mock.yml` governance-service'i bu sahte servislere yönlendirir.
- `scripts/test.sh integration` ikisini de otomatik kurar, bitince her şeyi eski haline getirir.

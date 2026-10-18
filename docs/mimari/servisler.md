# HR360 — Backend mikroservisleri

15 .NET mikroservisi aynı kalıpla yazıldı: ASP.NET Core 9 + EF Core + Npgsql, Keycloak JWT
doğrulaması, rol ve sahiplik bazlı yetkilendirme, `/health` ve `/metrics` uçları, Docker.

> Sistemin bütünü (bağlam, olay akışı, diyagramlar) için bkz. [docs/mimari](../mimari/README.md).
> Bu doküman tek sunucu (Docker Compose) kurulumunu anlatır. İlk 7 sanal sunuculu tasarım için
> bkz. kök `README.md` › "Orijinal prodüksiyon mimarisi hakkında not".

---

## Servisler

Her servis container içinde **8080** portunu dinler. Dışarıya açılmaz; gateway (nginx)
`/api/<servis>/X` isteğini servisin `/api/X` ucuna yönlendirir.

| Servis | Gateway yolu | Kapsam |
|---|---|---|
| `tenant-service` | `/api/tenant/` | Kiracı kaydı, Keycloak organizasyonu, plan, askıya alma, marka, ekip/rol yönetimi |
| `organization-service` | `/api/organization/` | Şirket, departman (departman başı), ekip |
| `employee-service` | `/api/employee/` | Çalışan ana verisi, atamalar, `/employees/me` |
| `workflow-service` | `/api/workflow/` | Onay akışları, sıralı adımlar, vekâlet, SLA |
| `leave-service` | `/api/leave/` | İzin bakiyesi, izin talebi, resmi tatil takvimi |
| `timeshift-service` | `/api/timeshift/` | Vardiya motoru, giriş/çıkış, puantaj |
| `performance-service` | `/api/performance/` | Dönem, hedef, değerlendirme, metrik, geri bildirim, analiz, öneri |
| `learning-service` | `/api/learning/` | Eğitim kataloğu, kayıt, sertifika, zorunlu eğitim uyumu |
| `compensation-service` | `/api/compensation/` | Ücret bandı, ücret geçmişi, zam simülasyonu |
| `expense-service` | `/api/expense/` | Masraf beyanı, özlük dokümanı, İK vakası |
| `recruitment-service` | `/api/recruitment/` | İlan, aday, başvuru, mülakat |
| `onboarding-service` | `/api/onboarding/` | İşe başlangıç planı ve görevleri, zimmet |
| `notification-service` | `/api/notification/` | Bildirim şablonu, kuyruk, e-posta gönderimi |
| `engagement-service` | `/api/engagement/` | Takdir, kutlama, anket, 1:1, mentorluk, ofis/masa, iç ilan, ayrılış, ardıl planlama, ekip sağlığı |
| `governance-service` | `/api/governance/` | Denetim, olay radarı, kural motoru, webhook ve açık API, Slack/Teams, takvim ve toplantı, AI araçları, KVKK, analitik |

Ayrıca `ml-inference` (FastAPI, `/ml/`, port 8000) işten ayrılma riski tahmini yapar.

---

## Yetkilendirme modeli

- **Roller** (Keycloak realm rolleri, birleşik değil): `employee`, `manager`, `accounting`,
  `hr-admin`, `tenant-admin`, `platform-admin`. Web istemcisindeki karşılığı
  `apps/web/src/auth/roles.ts`.
- **Politikalar:** Her serviste en az `RequireHrAdmin` (hr-admin, tenant-admin, platform-admin)
  ve `RequireManagerOrAbove` (+ manager) vardır. Bazı servislerde işe özel politikalar
  bulunur: masraf yönetimi, ödeme işaretleme, ücret yazma, vaka yönetimi gibi.
- **Ek izinler:** Şirket yöneticisi, Roller sayfasından bir kişiye rolünden bağımsız tek bir
  izin verebilir. Bu izin Keycloak'ta `ext-<izin>` rolü olarak durur, ilgili servisin
  politikası da bu rolü tanır.
- **Sahiplik kontrolleri:** Politika yalnızca rolü sorar. "Bu kayıt senin mi?" sorusu kodda
  ayrıca kontrol edilir; kişi `/employees/me` ile çalışan kaydına eşlenir. Örnekler:
  - Çalışan yalnızca kendi izin, masraf ve hedeflerini görür.
  - Talep sahibi kendi talebine onay veremez.
  - Değerlendirmeyi yalnızca yazan kişi doldurabilir.
- **Kiracı izolasyonu:** Her tabloda `TenantSlug` vardır. EF global sorgu filtresi, kayıtları
  JWT'deki `organization` bilgisine göre süzer.
- **Askıya alınan kiracı:** `TenantStatusGate` bu kiracının isteklerini 403 ile reddeder.

---

## Kurulum

Ayrı bir kurulum adımı yoktur; kök dizindeki `./install.sh` hepsini yapar:

- **Şema:** `data/migrations/sql-all-schemas.sql` dosyasıdır. Veritabanı ilk kez oluşurken
  otomatik uygulanır.
- **Güncelleme:** Mevcut kurulumda şema değişiklikleri `scripts/sql/*.sql` göçleriyle gelir.
  `install.sh`'ın güncelleme yolu bunları otomatik çalıştırır.
- **Gateway:** Yönlendirmeler `deploy/nginx/nginx.conf` dosyasında hazırdır.
- **Servis adresleri:** Servisler birbirine `http://<servis>:8080` adresiyle ulaşır. Adresler
  `docker-compose.yml` içindeki ortam değişkenlerinden gelir.

EF `EnsureCreated()`/Migrations kullanılmaz: veritabanı paylaşımlı olduğu için şema tek SQL
dosyasında tutulur.

---

## Servisler arası bağlantılar

Gerçek yabancı anahtar yoktur, ID referansı kullanılır. Bağlantı iki yoldan kurulur.

**Senkron (HTTP, çağıranın JWT'si iletilir):**
- Birçok servis kişiyi çalışan kaydına eşlemek ve kayıt doğrulamak için `employee-service`
  çağırır.
- `leave-service` ve `expense-service`, departman başını bulmak için `organization-service`,
  onay akışı başlatmak için `workflow-service` çağırır.
- `notification-service`, kiracının logosu, adı ve SMTP ayarı için `tenant-service` çağırır.

**Asenkron (Kafka, outbox/inbox deseniyle):**
- **Yayınlayanlar:** `employee`, `workflow`, `leave` ve `expense` servisleri olayları
  `messaging_outbox` tablosuna iş verisiyle aynı transaction içinde yazar. Arka plan
  yayıncısı bunları Kafka'ya iletir.
- **Konular:** `hr360.employee.events`, `hr360.workflow.events`, `hr360.leave.events`.
  `kafka-init` bunları kurulumda oluşturur.
- **Tüketenler:** `leave`, `expense`, `timeshift` ve `notification` servisleri.
  `messaging_processed_events` tablosu aynı olayın iki kez işlenmesini önler. Örnekler:
  - Onay kararı izin veya masraf kaydını sonuçlandırır.
  - Onaylanan izin vardiya planına yansır.
  - Bildirim e-postaları bu olaylardan üretilir.

---

## Öne çıkan iş kuralları

**leave-service**
- İzin günü sunucuda hesaplanır: hafta sonları ve şirketin resmi tatil takvimi düşülür.
- Talep bakiyeden `PendingDays` olarak düşer. Onaylanırsa `UsedDays`'e geçer, reddedilirse iade
  edilir.
- Bakiye yetersizse talep reddedilir. Yıllık izin bakiye tanımı olmadan açılamaz.

**recruitment-service**
- Yalnızca yayındaki ilana başvuru alınır ve aynı aday aynı ilana iki kez başvuramaz.
- Sonuçlanmış başvurunun durumu değiştirilemez.
- Mülakat planlanınca başvuru otomatik `Interview` durumuna geçer.

**onboarding-service**
- Plan oluşturulurken 8 standart görev, işe başlama tarihine göre tarihlendirilir: IT hesabı,
  zimmet, giriş kartı, özlük evrakı, sözleşme, uyum eğitimi, ekip tanışma ve İSG.
- Tüm görevler bitince plan otomatik kapanır.
- Görevi atanan kişi ve planın sahibi (hukuki görevler hariç) durumu güncelleyebilir.

**timeshift-service**
- Çıkış girişten önce olamaz ve çalışılan süre otomatik hesaplanır.
- 8 saati (480 dk) aşan kısım fazla mesai olarak ayrılır.
- İş günü işletmenin saat dilimine göre belirlenir.

**performance-service**
- Nihai puan hedef ve metrik puanlarının ağırlıklı toplamıdır. Puanın nasıl oluştuğu
  bileşenleriyle birlikte döner.
- Dönem kapanınca puanlar sabitlenir.
- Dönemleri yalnızca İK açıp kapatır; hedef ve ilerleme yöneticiye açıktır.

**compensation-service**
- Yeni ücret kaydı öncekini otomatik kapatır (`EffectiveTo` = yeni başlangıç − 1 gün).
- Zam simülasyonu, bant dışına çıkan önerileri raporlar.

**notification-service**
- Şablonlar `{{degisken}}` yer tutucularını destekler.
- E-postalar kiracının kendi SMTP'siyle (Enterprise) ya da platformun SMTP'siyle gönderilir.
- Gönderim durumu ve deneme sayısı izlenir.

---

## Bilinen sınırlar

- **EF Migrations yok:** Şema temel SQL dosyası (`data/migrations/sql-all-schemas.sql`) ve tarihli göç
  betikleriyle (`scripts/sql`) yönetilir; uygulanan göçler `schema_migrations` defterinde tutulur
  (`scripts/migrate.sh`).
- **Yük dengeleme ve yedeklilik yok:** Tek sunucu kurulumunda her servisin tek kopyası çalışır. Zamanlanmış
  arka plan işleri çok kopyada yalnızca bir kopyada yürür (`LeaderGate`, PostgreSQL danışma kilidi).
  İzleme (Prometheus/Grafana) isteğe bağlı profildir; servisler `/metrics` uçlarını sunar.
- **Sağlık uçları:** `/health` canlılık, `/health/ready` veritabanı bağlantısı (iç ağ; ağ geçidi dışarı açmaz).
- **Tam bitemporal model yok:** Teklif → işe alım sagası var (`api/sagas/offer-to-hire`); tarihçe zaman
  makinesi belirli bir anın görünümüdür, geçerlilik + kayıt zamanlı (bitemporal) model değildir.

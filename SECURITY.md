# Güvenlik politikası

## Desteklenen sürümler

| Sürüm | Güvenlik düzeltmesi |
|---|---|
| 1.0.x | Evet |
| 1.0 öncesi | Hayır |

Kurulu sürümü `VERSION` dosyasında, uygulamada hesap menüsünde ya da `scripts/update.sh status`
ile görebilirsiniz. Düzeltmeler yeni bir yama sürümü olarak yayımlanır;
`scripts/update.sh --ref vX.Y.Z` ile güncelleyin.

## Güvenlik açığı bildirme

Açığı **herkese açık bir Issue, tartışma ya da çekme isteğinde paylaşmayın.** Bunun yerine GitHub
Security Advisories üzerinden özel bildirim gönderin:

https://github.com/barisshn35/hr360/security/advisories/new

Bildirimde mümkünse şunlar olsun:

- Etkilenen bileşen (servis, uç, ekran, betik) ve sürüm (`VERSION` ya da commit),
- Açığı yeniden üretme adımları ya da kavram kanıtı,
- Olası etki (ör. kiracılar arası veri erişimi, yetki yükseltme, kişisel veri sızıntısı),
- Varsa önerdiğiniz düzeltme.

Gerçek kişisel veri, müşteri verisi ya da canlı ortam parolası göndermeyin; örnek veri kullanın.

## Yanıt süreleri

| Adım | Hedef süre |
|---|---|
| Bildirimin alındığının teyidi | 3 iş günü |
| İlk değerlendirme (geçerlilik ve önem derecesi) | 10 iş günü |
| Düzeltme — kritik | 7 gün |
| Düzeltme — yüksek | 30 gün |
| Düzeltme — orta / düşük | Sonraki planlı sürüm (en geç 90 gün) |

Bu süreler hedeftir, taahhüt değildir. Süreç boyunca bildirimin durumu advisory üzerinden
paylaşılır. Düzeltme yayımlanana kadar ayrıntıları açıklamamanızı rica ederiz; düzeltmeden sonra
advisory yayımlanır ve isterseniz adınız teşekkür olarak anılır.

## Kapsam

Kapsamda: bu repodaki kaynak kod, `install.sh` ve `scripts/` altındaki betikler,
`docker-compose.yml` ve `deploy/` altındaki yapılandırmalar (gateway, Keycloak realm şablonu,
izleme). Özellikle kiracı yalıtımı, kimlik doğrulama ve yetki, şifreleme, denetim kaydı ve KVKK
korumalarını atlatan açıklar önceliklidir.

Kapsam dışı: üçüncü taraf bileşenlerin (Keycloak, PostgreSQL, Kafka, nginx vb.) kendi açıkları —
bunları ilgili projeye bildirin; HR360'ın onları güvensiz biçimde yapılandırması ise kapsamdadır.
Sizin yönetmediğiniz kurulumlara yönelik saldırı, hizmet engelleme denemeleri ve sosyal mühendislik
kabul edilmez.

## Kurulum sıkılaştırma

Bir kurulumu canlıya almadan önce uygulanacak kontrol listesi ve korumaların ayrıntısı:
[docs/guvenlik/README.md](docs/guvenlik/README.md#kurulum-kontrol-listesi).

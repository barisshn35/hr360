# HTTPS'i açma

HTTPS gateway'de (nginx) sonlanır; arkadaki servisler ve Keycloak iç ağda HTTP konuşur.
Tek komutla açılır, adres ve Keycloak ayarları betik tarafından güncellenir.

`install.sh` kurulumda HTTPS'i sorar ve seçilen yöntemle bu betiği kendisi çalıştırır;
aşağıdaki komutlar kurulumdan sonra değiştirmek için.

```bash
# Let's Encrypt: ücretsiz sertifika + otomatik yenileme (önerilen). Olmazsa geçici
# sertifikayla açar ve Let's Encrypt'i saatte bir kendiliğinden yeniden dener.
scripts/tls.sh auto --host hr.sirket.com --email it@sirket.com

# Yalnızca Let's Encrypt (başarısızsa hiçbir şey değişmez)
scripts/tls.sh enable --letsencrypt --host hr.sirket.com --email it@sirket.com

# Kendi sertifikanız (kurumsal CA, satın alınmış sertifika)
scripts/tls.sh enable --cert /yol/fullchain.pem --key /yol/privkey.pem --host hr.sirket.com

# Test için kendinden imzalı sertifika
scripts/tls.sh enable --self-signed --host hr.sirket.local

scripts/tls.sh status
scripts/tls.sh check     # DNS bu sunucuyu gösteriyor mu, 80/443 boş mu
scripts/tls.sh verify    # HTTPS, sertifika, HTTP->HTTPS yönlendirmesi, giriş adresi
scripts/tls.sh disable
```

Açınca:
- 443'te HTTPS açılır, HTTP istekleri HTTPS'e yönlendirilir.
- `PUBLIC_URL`/`PUBLIC_ORIGIN` `https://<host>` olur; Keycloak'ın genel adresi, uygulama
  istemcisinin yönlendirme adresleri ve servislerin beklediği jeton üreticisi (issuer) buna göre
  güncellenir. Açık oturumlar yeniden giriş ister (eski jetonlar farklı issuer taşır).
- Realm'de "SSL gerekli: dış istekler" (`sslRequired=external`) açılır.
- Yönetim paneli ayrı porttaysa (`keycloak-admin-access.sh port`) adresi yeni kökene göre yenilenir.
  Not: ayrı yönetim portu (8090) düz HTTP kalır ve yönetim girişi orada HTTPS istemez;
  bu portu firewall ile yalnızca yönetim IP'lerine ya da VPN'e açın.

## Let's Encrypt

Koşullar:
- Alan adının DNS A/AAAA kaydı sunucunun genel IP'sini göstermeli.
- 80/tcp ve 443/tcp internete açık olmalı (firewall / bulut güvenlik grubu).
- `GATEWAY_PORT=80` olmalı; doğrulama 80. porttan yapılır.

Nasıl çalışır:
- `certbot` servisi, doğrulama dosyasını `deploy/nginx/acme/` klasörüne yazar; gateway bu
  dosyayı `/.well-known/acme-challenge/` yolundan sunar. Bu yol HTTPS'e yönlendirilmez.
- Sertifikalar `deploy/letsencrypt/` klasöründe durur (git'e girmez; yedeğe alın).
- `certbot` servisi 12 saatte bir kontrol eder ve süresi 30 günden az kalan sertifikayı
  yeniler. Servis `.env`'deki `COMPOSE_PROFILES=letsencrypt` ile açılır.
- Gateway 6 saatte bir nginx'i yeniden yükler, böylece yeni sertifika kesinti olmadan devreye girer.

Denetim:
- `scripts/tls.sh status` sertifikanın bitiş tarihini ve yenileme servisinin çalışıp
  çalışmadığını gösterir.
- `docker compose logs certbot` yenileme kayıtlarını gösterir.
- Önce denemek isterseniz `--staging` ekleyin. Bu, Let's Encrypt'in test ortamıdır;
  tarayıcı sertifikaya güvenmez ama istek sınırlarına takılmazsınız. Sonra `--staging`
  olmadan tekrar çalıştırın.

Ön denetimler (`enable --letsencrypt` ve `auto`):
- Sunucunun kendi güvenlik duvarına (ufw ya da firewalld) hiçbir zaman dokunulmaz: devreye
  alınmaz, kural eklenmez. Çalışıyor ve 80/443'ü engelliyorsa yalnızca uyarı basılır.
  Erişim izni dış güvenlik duvarında verilir (bulut güvenlik grubu, vCloud Edge Gateway
  firewall + DNAT).
- Alan adının A kaydı, sunucunun genel IPv4 adresiyle karşılaştırılır. Sorgu
  DNS-over-HTTPS ile yapılır (Cloudflare, Google), yani Let's Encrypt'in gördüğü genel DNS
  denetlenir; yerel `/etc/hosts` yanıltmaz. Kayıt yanlışsa certbot hiç çalıştırılmaz:
  Let's Encrypt alan adı başına saatte 5 başarısız doğrulamaya izin verir.
- Genel IP belirlenemezse (internet çıkışı yok) uyarı verilir ve yine de denenir. Çıkış
  trafiği farklı bir IP'den gidiyorsa `HR360_PUBLIC_IP=203.0.113.10` ile verin. DNS'i
  bölünmüş ufukla yönetiyorsanız denetimi `TLS_SKIP_DNS_CHECK=1` ile atlayın.

Kurulumda (ve `tls.sh auto` ile) Let's Encrypt başarısız olursa kurulum durmaz:
1. HTTPS geçici, kendinden imzalı bir sertifikayla açılır. Tarayıcı uyarı verir; HSTS
   bu sertifikada açılmaz.
2. `.env`'e `TLS_LE_PENDING=<alan adı>` yazılır ve saatlik bir zamanlayıcı kurulur:
   systemd varsa `hr360-tls-retry.timer` (günlük: `journalctl -u hr360-tls-retry`), yoksa
   cron (günlük: `deploy/letsencrypt/retry.log`).
3. Zamanlayıcı her saat `scripts/tls.sh retry` çalıştırır. DNS hâlâ yanlışsa certbot'a
   gitmeden çıkar. Sertifika alınınca Let's Encrypt'e geçilir, geçici sertifika silinir,
   zamanlayıcı kaldırılır.
4. Elle yapılan her TLS değişikliği (`enable`, `disable`, `external`) bekleyen denemeyi iptal eder.

## Kendi sertifikanız

Sertifika yenilendiğinde aynı `enable --cert ... --key ...` komutunu yeni dosyalarla tekrar
çalıştırın.

HTTPS'i 443 dışında bir portta açmak için `--port 8443` ekleyin; HTTP istekleri o porta
yönlendirilir.

## Önde TLS'i sonlandıran bir yük dengeleyici varsa

`enable` kullanmayın (HTTP→HTTPS yönlendirmesi döngüye girer). Onun yerine:

```bash
scripts/tls.sh external --host hr.sirket.com
```

Gateway HTTP'de kalır. Uygulama adresi, Keycloak istemcisinin yönlendirme adresleri ve
`sslRequired` ise `https://hr.sirket.com` için ayarlanır. Dengeleyici gateway'e
`X-Forwarded-Proto: https` başlığını göndermelidir.

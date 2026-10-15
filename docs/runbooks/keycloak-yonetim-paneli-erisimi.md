# Keycloak yönetim paneline erişimi kısıtlama

Keycloak yönetim konsolu (`/auth/admin`) ve yönetici girişinin yapıldığı master realm
(`/auth/realms/master`) varsayılan olarak uygulamayla aynı adresten herkese açıktır.
Uygulama girişi (hr360 realm) bu ayardan hiç etkilenmez.

Erişim tek komutla değiştirilir; ayar `.env`'e yazılır ve kalıcıdır:

| Mod | Komut | Sonuç |
| --- | --- | --- |
| open (varsayılan) | `scripts/keycloak-admin-access.sh open` | Panel `https://<adres>/auth/admin/` üzerinden herkese açık |
| ip | `scripts/keycloak-admin-access.sh ip 203.0.113.10,10.20.0.0/16` | Panel aynı adreste, yalnızca listedeki IP/CIDR'lere açık; diğerleri 403 alır |
| port (önerilen) | `scripts/keycloak-admin-access.sh port` | Panel ana adreste tamamen kapalı; yalnızca `https://<alan adı>:8090/auth/admin/` üzerinden. Ana adres HTTPS ise bu port da aynı sertifikayla TLS'lidir (HTTPS kapalıysa düz HTTP). Portu **dış güvenlik duvarında** yalnızca yönetim IP'lerine ya da VPN'e açın |
| port + ip | `scripts/keycloak-admin-access.sh port 203.0.113.10` | Port modu + nginx ayrıca o portta IP kısıtı (iki katman) |

Geçerli ayarı görmek için: `scripts/keycloak-admin-access.sh status`.
Kurulum sırasında da `install.sh` bu seçimi sorar; Enter ile geçilirse `open` olur. Paneli sonradan
dış güvenlik duvarından kısıtlamak isterseniz `port` moduna geçip aşağıdaki kuralı ekleyin.

## Neden firewall için ayrı port?

Firewall IP ve port üzerinden çalışır, URL yolunu (`/auth/admin`) göremez. Panel uygulamayla
aynı portta kalırsa firewall ya uygulamayı da kapatır ya da paneli açık bırakır. Port modunda
panel ayrı bir portta yayınlanır; o portu yalnızca yönetim IP'lerine açarsınız.

Port modunda betik ayrıca master realm'in "Frontend URL" ayarını yönetim portuna çevirir:
yönetim konsolunun girişi master realm üzerinden yapıldığı için, aksi halde giriş ekranı
kapalı olan ana adrese gider ve açılmaz. Diğer modlara dönünce bu ayar temizlenir.

## Dış güvenlik duvarı (port modu, varsayılan port 8090)

Kısıt sunucunun **kendi** güvenlik duvarında (ufw/firewalld) değil, önündeki dış güvenlik duvarında
yapılır: bulut sağlayıcının güvenlik grubu / ağ ACL'i, vCloud/NSX Edge, kurum firewall'u. Betikler
sunucu güvenlik duvarına dokunmaz. Docker yayınladığı portları zaten host firewall kurallarından
önce açtığı için ufw kuralları bu portu güvenilir biçimde kapatamaz.

Adımlar:

1. `scripts/keycloak-admin-access.sh port` — panel ana adreste kapanır, sunucu 8090/tcp'de dinler.
2. Dış güvenlik duvarında **8090/tcp** için kural: kaynak = yalnızca yönetim IP'leri ya da VPN
   aralığı, hedef = sunucu. NAT kullanılıyorsa (ör. Edge DNAT) 8090'ı sunucunun iç adresine
   yönlendirin; 80/443 kurallarına dokunmayın.
3. Yönetim IP'sinden `https://<alan adı>:8090/auth/admin/` açılmalı, diğer adreslerden bağlantı
   kurulamamalı.
4. HTTPS sonradan açılır ya da sertifika yolu değişirse betiği yeniden çalıştırın
   (`scripts/keycloak-admin-access.sh port`); TLS ayarı o an `deploy/nginx/tls/listen.conf`'tan alınır.

İkinci katman istenirse nginx'te de IP listesi verilebilir: `scripts/keycloak-admin-access.sh port 203.0.113.10`.

## Dikkat

- nginx istemci IP'sini doğrudan bağlantıdan alır. Gateway'in önünde başka bir yük
  dengeleyici/ters vekil varsa herkes onun IP'si gibi görünür: kısıtı o cihazda yapın ya da
  nginx'te `real_ip` modülünü yapılandırın.
- Kendinizi dışarıda bıraktıysanız sunucuda `scripts/keycloak-admin-access.sh open` çalıştırmak
  paneli yeniden açar (sunucu kabuğu yeterli, panele erişim gerekmez).
- Uygulamanın kendi servisleri Keycloak yönetim API'sine iç ağdan (`keycloak:8080`) erişir;
  bu ayar onları etkilemez.
- tenant-service yönetim API'sine hr360 realm'indeki `hr360-tenant-admin` servis hesabıyla girer
  (`scripts/keycloak-service-account.sh`). Bu hesap `.env`'de tanımlı ve Keycloak'ta varsa betik
  master realm'de kaba kuvvet korumasını da açar (10 hatalı denemede artan bekleme, en fazla 15 dk).

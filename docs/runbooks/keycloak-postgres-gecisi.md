# Keycloak: konteyner içi dosya veritabanından Postgres'e geçiş

**Neden:** Önceden Keycloak `start-dev` ile, `KC_DB` tanımlanmadan ve volume olmadan
çalışıyordu (konteyner içindeki `dev-file` H2 veritabanı). Konteyner her yeniden
oluşturulduğunda (imaj güncellemesi, `docker compose up` ile config değişikliği)
**tüm kullanıcılar, parolalar ve şirket organizasyonları siliniyor**; `platform_tenants`
ise artık var olmayan `KeycloakOrgId`'leri tutmaya devam ediyordu.

Yeni kurulumlar etkilenmez: `deploy/postgres/init-databases.sh` `keycloak` veritabanını
oluşturur, `docker-compose.yml` Keycloak'ı ona bağlar ve realm şablondan içe aktarılır.

**Bu runbook, bu değişiklikten ÖNCE kurulmuş ve içinde gerçek kullanıcı olan ortamlar
içindir.** Yeni `docker-compose.yml`'yi çekip doğrudan `docker compose up -d` çalıştırmayın:
Keycloak boş bir veritabanıyla açılır ve mevcut kullanıcıları görmez.

## Adımlar

```bash
# 0) Eski compose ile Keycloak hâlâ çalışıyor olmalı (yeni compose'u henüz uygulamayın).

# 1) Durdur ve geri dönüş noktası al (konteynerin yazılabilir katmanı = tüm kimlik verisi)
docker compose stop keycloak
docker commit hr360-keycloak-1 hr360-keycloak-snapshot:pre-postgres

# 2) Realm'i kullanıcılar ve organizasyonlarla birlikte dışa aktar
mkdir -p /tmp/kcexport && chmod 777 /tmp/kcexport
docker run --rm --entrypoint /opt/keycloak/bin/kc.sh -v /tmp/kcexport:/export \
  hr360-keycloak-snapshot:pre-postgres \
  export --dir /export --realm hr360 --users realm_file --features=organization

# 3) Keycloak 25 hatası: organizasyonlar "identityProviders" alanı OLMADAN dışa aktarılır
#    ve içe aktarım NullPointerException ile düşer. Boş liste ekleyin:
python3 - <<'PY'
import json; p='/tmp/kcexport/hr360-realm.json'; d=json.load(open(p))
for o in d.get('organizations', []):
    o['identityProviders'] = o.get('identityProviders') or []
json.dump(d, open(p, 'w'))
PY

# 4) Veritabanını oluştur, yeni compose'u çek, realm'i Postgres'e içe aktar
docker compose exec -T postgres psql -U hr360admin -d postgres -c "CREATE DATABASE keycloak OWNER hr360admin;"
git pull
docker compose run --rm --no-deps -v /tmp/kcexport:/export --entrypoint /opt/keycloak/bin/kc.sh \
  keycloak import --file /export/hr360-realm.json --features=organization --override true

# 5) Keycloak'ı yeni yapılandırmayla başlat (--import-realm mevcut realm'i atlar)
docker compose up -d keycloak
```

## 6) Organizasyon kimliklerini yeniden bağla (ZORUNLU)

İçe aktarımda **kullanıcı kimlikleri korunur, organizasyon kimlikleri YENİDEN ÜRETİLİR.**
`platform_tenants."KeycloakOrgId"` güncellenmezse davet, rol yönetimi ve kiracılar arası
rol koruması çalışmaz. Keycloak admin API'sinden yeni kimlikleri alıp slug'a göre eşleyin:

```sql
-- her organizasyon için (ad = kiracı slug'ı):
UPDATE platform_tenants SET "KeycloakOrgId" = '<yeni-org-id>' WHERE "Slug" = '<slug>';
```

## 7) Giriş akışındaki çift "Organization" adımını kapat (ZORUNLU)

Dışa aktarılan realm, organizasyon adımını içeren tarayıcı akışını zaten taşır; özellik
açık olduğu için Keycloak içe aktarımda bir tane daha ekler. İki "Organization
Identity-First Login" adımı olunca kullanıcı adı ekranı **sonsuz döngüye** girer, parola
ekranı hiç gelmez (kimse giriş yapamaz). Yerleşik akıştan adım silinemez (400); ikinci
adımı devre dışı bırakın:

Keycloak Admin → hr360 → Authentication → browser → ikinci **Organization** satırı →
Requirement: **Disabled**. (API: `PUT /admin/realms/hr360/authentication/flows/browser/executions`
gövdede ilgili execution ile `"requirement":"DISABLED"`.)

## Doğrulama

- Mevcut kullanıcılar eski parolalarıyla giriş yapabiliyor; jetonda `organization` doğru.
- `docker compose up -d --force-recreate keycloak` sonrası kullanıcılar hâlâ duruyor.
- Bir çalışanı davet etmek e-posta gönderiyor ("Update Your Account"; SMTP ayarlanmadıysa Mailpit'te, ayarlandıysa gerçek posta kutusunda).

## Geri dönüş

Eski compose'a dönüp anlık görüntüyü çalıştırın: `docker-compose.yml`'de keycloak
imajını geçici olarak `hr360-keycloak-snapshot:pre-postgres` yapın ve `KC_DB*`
değişkenlerini kaldırın. Doğrulama tamamlanınca anlık görüntü silinebilir:
`docker image rm hr360-keycloak-snapshot:pre-postgres`.

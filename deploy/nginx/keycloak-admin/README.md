Bu klasördeki `main-access.conf` ve `port-access.conf` dosyaları
`scripts/keycloak-admin-access.sh` tarafından üretilir (git'e girmez) ve
gateway'e `/etc/nginx/keycloak-admin/` olarak bağlanır.

Dosya yoksa: ana adresteki yönetim paneli açıktır (varsayılan).
Ayrıntılar: `docs/runbooks/keycloak-yonetim-paneli-erisimi.md`

-- Güvenlik dalgası 2A (tenant-service + tüm servislerin kiracı kapısı):
--   * iki adımlı doğrulama politikası (şirket başına),
--   * şüpheli giriş tespiti (Keycloak LOGIN / LOGIN_ERROR olayları),
--   * platform yöneticisinin süreli kiracı erişim izni (break-glass).
-- Idempotent.

-- İki adımlı doğrulama zorunluluğu: NULL/'off' = yok, 'privileged' = İK, şirket yöneticisi ve
-- yönetici rolleri, 'all' = tüm şirket kullanıcıları. tenant-service düzenli olarak uygular.
ALTER TABLE platform_tenants ADD COLUMN IF NOT EXISTS "MfaPolicy" character varying(16);

-- Şüpheli giriş: kullanıcının daha önce giriş yaptığı ağlar. KVKK: ham IP tutulmaz; ağ öneki
-- (IPv4 /24, IPv6 /48) anahtarlı özetle (HMAC-SHA256) saklanır. 180 gün kullanılmayan satır silinir.
CREATE TABLE IF NOT EXISTS tenant_login_networks (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "UserId" character varying(64) NOT NULL,
    "NetworkHash" character varying(64) NOT NULL,
    "FirstSeenAt" timestamptz NOT NULL,
    "LastSeenAt" timestamptz NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_tenant_login_networks_User_Network"
    ON tenant_login_networks ("TenantSlug", "UserId", "NetworkHash");
CREATE INDEX IF NOT EXISTS "IX_tenant_login_networks_LastSeenAt" ON tenant_login_networks ("LastSeenAt");

-- Şüpheli giriş uyarıları (yeni ağdan giriş, art arda hatalı parola). Güvenlik ekranında
-- şirket yöneticisine gösterilir; 180 gün saklanır.
CREATE TABLE IF NOT EXISTS tenant_security_alerts (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Kind" character varying(32) NOT NULL,
    "UserId" character varying(64),
    "Username" character varying(256),
    "NetworkHash" character varying(64),
    "Count" integer NOT NULL DEFAULT 1,
    "CreatedAt" timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_tenant_security_alerts_TenantSlug" ON tenant_security_alerts ("TenantSlug", "CreatedAt" DESC);

-- Platform yöneticisinin süreli kiracı erişim izni: gerekçe zorunlu, en fazla 4 saat. Servislerin
-- kiracı kapısı (TenantStatusGate) platform yöneticisinin kiracı verisi isteklerinde etkin izin arar.
CREATE TABLE IF NOT EXISTS platform_access_grants (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "GrantedToUserId" character varying(64) NOT NULL,
    "GrantedToName" character varying(256),
    "Reason" text NOT NULL,
    "CreatedAt" timestamptz NOT NULL,
    "ExpiresAt" timestamptz NOT NULL,
    "RevokedAt" timestamptz,
    "RevokedByUserId" character varying(64),
    "RevokedByName" character varying(256)
);
CREATE INDEX IF NOT EXISTS "IX_platform_access_grants_Active"
    ON platform_access_grants ("TenantSlug", "GrantedToUserId", "ExpiresAt");
CREATE INDEX IF NOT EXISTS "IX_platform_access_grants_TenantSlug" ON platform_access_grants ("TenantSlug", "CreatedAt" DESC);

-- Süreli davet linki (parola belirleme/sıfırlama) ve platform sistem ayarları. İdempotent.
--
-- tenant_password_invites: HR360'ın kendi tek kullanımlık, süreli parola linkleri. Token
-- veritabanında TUTULMAZ, yalnızca SHA-256 özeti ("TokenHash", hex). Yeni link aynı hesabın
-- açık linklerini iptal eder; kullanılınca "UsedAt" dolar.
CREATE TABLE IF NOT EXISTS tenant_password_invites (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" varchar(64) NULL,
    "KeycloakUserId" varchar(64) NOT NULL,
    "Email" varchar(320) NOT NULL,
    "FullName" varchar(300) NULL,
    "Purpose" varchar(16) NOT NULL,
    "TokenHash" varchar(64) NULL,
    "Channel" varchar(16) NOT NULL,
    "EmailStatus" varchar(16) NOT NULL DEFAULT 'none',
    "CreatedAt" timestamptz NOT NULL,
    "ExpiresAt" timestamptz NOT NULL,
    "UsedAt" timestamptz NULL,
    "RevokedAt" timestamptz NULL,
    "CreatedBy" varchar(64) NULL,
    "CreatedByName" varchar(300) NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_tenant_password_invites_TokenHash"
    ON tenant_password_invites ("TokenHash") WHERE "TokenHash" IS NOT NULL;
CREATE INDEX IF NOT EXISTS "IX_tenant_password_invites_User" ON tenant_password_invites ("KeycloakUserId");
CREATE INDEX IF NOT EXISTS "IX_tenant_password_invites_Tenant" ON tenant_password_invites ("TenantSlug", "CreatedAt" DESC);

-- platform_settings: arayüzden değiştirilen uygulama içi ayarlar (tek satır, "Id" = 1). Boş alan
-- = .env'deki ilk değer geçerli. SMTP parolası tenant anahtarıyla şifreli (anahtar yenilemesinde
-- KeyRotationJob yeniden yazar).
CREATE TABLE IF NOT EXISTS platform_settings (
    "Id" integer PRIMARY KEY,
    "SmtpMode" varchar(16) NULL,
    "SmtpHost" varchar(253) NULL,
    "SmtpPort" integer NULL,
    "SmtpUser" varchar(320) NULL,
    "SmtpPasswordEncrypted" text NULL,
    "SmtpFromAddress" varchar(320) NULL,
    "SmtpFromName" varchar(120) NULL,
    "SignupEnabled" boolean NULL,
    "PasswordMethod" varchar(16) NULL,
    "InviteTtlHours" integer NULL,
    "UpdatedAt" timestamptz NULL,
    "UpdatedBy" varchar(300) NULL
);

-- Şirket düzeyi parola belirleme yöntemi: NULL = platform varsayılanı; 'invite' | 'keycloak'.
ALTER TABLE platform_tenants ADD COLUMN IF NOT EXISTS "PasswordSetupMethod" varchar(16) NULL;

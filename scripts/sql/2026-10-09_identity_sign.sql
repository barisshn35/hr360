-- Dalga 5d: Y26 (SCIM 2.0 + LDAP/AD dizin sağlama), G28 (kiracıya özel alan adı + marka),
-- Y28 (OTP ile basit elektronik imza). Idempotent: tekrar çalıştırılabilir.
--
-- KVKK (veri minimizasyonu): dizinden yalnızca kullanıcı adı, ad, soyad, birincil e-posta, unvan,
-- departman ve etkinlik durumu saklanır (+ IdP eşleştirmesi için teknik dış kimlik). Telefon,
-- adres, fotoğraf, yönetici vb. nitelikler yok sayılır.

/* ============================================================ SCIM erişim jetonları (tenant-service) */

CREATE TABLE IF NOT EXISTS tenant_scim_tokens (
    "Id"          uuid PRIMARY KEY,
    "TenantSlug"  character varying(64) NOT NULL,
    "Name"        text NOT NULL,
    -- Jetonun kendisi ASLA saklanmaz: yalnızca SHA-256 özeti (hex) ve görüntüleme için ilk karakterleri.
    "TokenHash"   character varying(64) NOT NULL,
    "TokenPrefix" character varying(24) NOT NULL,
    "CreatedAt"   timestamptz NOT NULL DEFAULT now(),
    "CreatedBy"   text NULL,
    "LastUsedAt"  timestamptz NULL,
    "RevokedAt"   timestamptz NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_tenant_scim_tokens_TokenHash" ON tenant_scim_tokens ("TokenHash");
CREATE INDEX IF NOT EXISTS "IX_tenant_scim_tokens_TenantSlug" ON tenant_scim_tokens ("TenantSlug");

/* ============================================================ dizin ayarları (SCIM + LDAP) */

CREATE TABLE IF NOT EXISTS tenant_directory_settings (
    "Id"                        uuid PRIMARY KEY,
    "TenantSlug"                character varying(64) NOT NULL,
    -- Yeni oluşturulan hesaplara parola belirleme e-postası gönderilsin mi (SSO kullanan kiracılar kapatır).
    "SendInvitations"           boolean NOT NULL DEFAULT true,
    "LdapEnabled"               boolean NOT NULL DEFAULT false,
    "LdapAutoSync"              boolean NOT NULL DEFAULT false,
    "LdapUrl"                   text NULL,
    "LdapBindDn"                text NULL,
    -- AES-256-GCM (TENANT_SECRET_KEY, SmtpCredentialProtector) ile şifreli; düz metin asla tutulmaz.
    "LdapBindPasswordEncrypted" text NULL,
    "LdapBaseDn"                text NULL,
    "LdapUserFilter"            text NULL,
    "LdapUsernameAttr"          text NOT NULL DEFAULT 'uid',
    "LdapEmailAttr"             text NOT NULL DEFAULT 'mail',
    "LdapDepartmentAttr"        text NULL,
    "LdapTitleAttr"             text NULL,
    "LdapDisabledAttr"          text NULL,
    "LastSyncAt"                timestamptz NULL,
    "LastSyncTrigger"           text NULL,
    "LastSyncStatus"            text NULL,
    "LastSyncSummary"           text NULL,
    "UpdatedAt"                 timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_tenant_directory_settings_TenantSlug" ON tenant_directory_settings ("TenantSlug");
-- Önceki taslaktan kalan sütunlar (telefon izni) kaldırılır; yeni sütunlar eklenir.
ALTER TABLE tenant_directory_settings DROP COLUMN IF EXISTS "ScimAllowPhone";
ALTER TABLE tenant_directory_settings ADD COLUMN IF NOT EXISTS "LdapTitleAttr" text NULL;

/* ============================================================ dizinden sağlanan kullanıcılar */

CREATE TABLE IF NOT EXISTS tenant_directory_users (
    "Id"             uuid PRIMARY KEY,
    "TenantSlug"     character varying(64) NOT NULL,
    -- scim | ldap
    "Source"         text NOT NULL,
    "ExternalId"     text NULL,
    "UserName"       text NOT NULL,
    "GivenName"      text NULL,
    "FamilyName"     text NULL,
    "Email"          text NOT NULL,
    "Title"          text NULL,
    "Department"     text NULL,
    "Active"         boolean NOT NULL DEFAULT true,
    "KeycloakUserId" text NULL,
    "EmployeeId"     uuid NULL,
    -- Pending | Linked | Failed
    "EmployeeState"  text NOT NULL DEFAULT 'Pending',
    "EmployeeError"  text NULL,
    "CreatedAt"      timestamptz NOT NULL DEFAULT now(),
    "UpdatedAt"      timestamptz NOT NULL DEFAULT now(),
    "DeactivatedAt"  timestamptz NULL
);
ALTER TABLE tenant_directory_users DROP COLUMN IF EXISTS "Phone";
ALTER TABLE tenant_directory_users DROP COLUMN IF EXISTS "HireDate";
ALTER TABLE tenant_directory_users DROP COLUMN IF EXISTS "Roles";
ALTER TABLE tenant_directory_users ADD COLUMN IF NOT EXISTS "Title" text NULL;
CREATE INDEX IF NOT EXISTS "IX_tenant_directory_users_TenantSlug" ON tenant_directory_users ("TenantSlug");
CREATE UNIQUE INDEX IF NOT EXISTS "IX_tenant_directory_users_Tenant_UserName"
    ON tenant_directory_users ("TenantSlug", lower("UserName"));
CREATE UNIQUE INDEX IF NOT EXISTS "IX_tenant_directory_users_Tenant_Email"
    ON tenant_directory_users ("TenantSlug", lower("Email"));
CREATE INDEX IF NOT EXISTS "IX_tenant_directory_users_Tenant_ExternalId"
    ON tenant_directory_users ("TenantSlug", "Source", "ExternalId");

/* ============================================================ G28 özel alan adı */

CREATE TABLE IF NOT EXISTS tenant_custom_domains (
    "Id"                uuid PRIMARY KEY,
    "TenantSlug"        character varying(64) NOT NULL,
    -- Küçük harf, IDN ise punycode (xn--) biçiminde.
    "Domain"            text NOT NULL,
    -- DNS TXT kaydı: _hr360-verify.<alan adı> = bu değer.
    "VerificationToken" text NOT NULL,
    -- Pending | Verified
    "Status"            text NOT NULL DEFAULT 'Pending',
    "CreatedAt"         timestamptz NOT NULL DEFAULT now(),
    "VerifiedAt"        timestamptz NULL,
    "LastCheckedAt"     timestamptz NULL,
    "LastCheckError"    text NULL
);
CREATE INDEX IF NOT EXISTS "IX_tenant_custom_domains_TenantSlug" ON tenant_custom_domains ("TenantSlug");
CREATE UNIQUE INDEX IF NOT EXISTS "IX_tenant_custom_domains_Tenant_Domain"
    ON tenant_custom_domains ("TenantSlug", "Domain");
-- Bir alan adı aynı anda yalnızca TEK kiracıda doğrulanmış olabilir.
CREATE UNIQUE INDEX IF NOT EXISTS "IX_tenant_custom_domains_Verified"
    ON tenant_custom_domains ("Domain") WHERE "Status" = 'Verified';

/* ============================================================ Y28 basit e-imza (expense-service) */

-- İmza talebi: İK bir özlük dokümanını çalışana imzaya gönderir. Tek kullanımlık kod (OTP)
-- yalnızca SHA-256 (HMAC, TENANT_SECRET_KEY türevli anahtar) özetiyle tutulur; 10 dk geçerli, 5 deneme.
CREATE TABLE IF NOT EXISTS expense_document_signatures (
    "Id"                     uuid PRIMARY KEY,
    "TenantSlug"             character varying(64) NOT NULL,
    "DocumentId"             uuid NOT NULL,
    "EmployeeId"             uuid NOT NULL,
    "RequestedByEmployeeId"  uuid NULL,
    "RequestedByUserId"      text NULL,
    -- Pending | Signed | Cancelled | Superseded
    "Status"                 text NOT NULL DEFAULT 'Pending',
    -- Talep anındaki belge özeti; imzada yeniden hesaplanır, farklıysa imza reddedilir.
    "DocumentHash"           character varying(64) NOT NULL,
    "Message"                text NULL,
    "CreatedAt"              timestamptz NOT NULL DEFAULT now(),
    "OtpHash"                character varying(64) NULL,
    "OtpChannel"             text NULL,
    "OtpExpiresAt"           timestamptz NULL,
    "OtpAttempts"            integer NOT NULL DEFAULT 0,
    "OtpSentCount"           integer NOT NULL DEFAULT 0,
    "OtpLastSentAt"          timestamptz NULL,
    "SignedAt"               timestamptz NULL,
    "CancelledAt"            timestamptz NULL
);
CREATE INDEX IF NOT EXISTS "IX_expense_document_signatures_TenantSlug" ON expense_document_signatures ("TenantSlug");
CREATE INDEX IF NOT EXISTS "IX_expense_document_signatures_DocumentId" ON expense_document_signatures ("DocumentId");
CREATE INDEX IF NOT EXISTS "IX_expense_document_signatures_EmployeeId" ON expense_document_signatures ("EmployeeId", "Status");
-- Bir dokümanın aynı anda tek açık talebi olabilir.
CREATE UNIQUE INDEX IF NOT EXISTS "IX_expense_document_signatures_OnePending"
    ON expense_document_signatures ("DocumentId") WHERE "Status" = 'Pending';

-- İmza kanıtı: değiştirilemez (UPDATE tetikleyiciyle engellenir). Doküman silindiğinde
-- (saklama süresi dolduğunda imha) kanıt da birlikte silinir - doküman kadar saklanır.
CREATE TABLE IF NOT EXISTS expense_signature_evidence (
    "Id"                uuid PRIMARY KEY,
    "TenantSlug"        character varying(64) NOT NULL,
    "SignatureId"       uuid NOT NULL,
    "DocumentId"        uuid NOT NULL,
    "SignerEmployeeId"  uuid NOT NULL,
    "SignedAt"          timestamptz NOT NULL,
    "DocumentHash"      character varying(64) NOT NULL,
    -- Son okteti maskelenmiş IP (IPv4 a.b.c.0, IPv6 /48).
    "IpMasked"          text NULL,
    -- Kullanıcı aracısının SHA-256 özeti (düz metin tutulmaz).
    "UserAgentHash"     character varying(64) NULL,
    "OtpChannel"        text NOT NULL,
    -- Yukarıdaki alanların kanonik SHA-256 özeti (bütünlük kontrolü).
    "EvidenceHash"      character varying(64) NOT NULL,
    "Method"            text NOT NULL DEFAULT 'SimpleElectronicSignature-OTP'
);
CREATE INDEX IF NOT EXISTS "IX_expense_signature_evidence_TenantSlug" ON expense_signature_evidence ("TenantSlug");
CREATE UNIQUE INDEX IF NOT EXISTS "IX_expense_signature_evidence_SignatureId" ON expense_signature_evidence ("SignatureId");
CREATE INDEX IF NOT EXISTS "IX_expense_signature_evidence_DocumentId" ON expense_signature_evidence ("DocumentId");

CREATE OR REPLACE FUNCTION expense_signature_evidence_immutable() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION 'expense_signature_evidence kayıtları değiştirilemez';
END;
$$ LANGUAGE plpgsql;
DROP TRIGGER IF EXISTS trg_expense_signature_evidence_immutable ON expense_signature_evidence;
CREATE TRIGGER trg_expense_signature_evidence_immutable
    BEFORE UPDATE ON expense_signature_evidence
    FOR EACH ROW EXECUTE FUNCTION expense_signature_evidence_immutable();

-- İmzalanmış doküman kilidi: imzalanan doküman satırı değiştirilemez (yeniden imza = yeni talep).
ALTER TABLE expense_documents ADD COLUMN IF NOT EXISTS "SignedAt" timestamptz NULL;
CREATE OR REPLACE FUNCTION expense_documents_signed_lock() RETURNS trigger AS $$
BEGIN
    IF OLD."SignedAt" IS NOT NULL THEN
        RAISE EXCEPTION 'İmzalanmış doküman değiştirilemez';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;
DROP TRIGGER IF EXISTS trg_expense_documents_signed_lock ON expense_documents;
CREATE TRIGGER trg_expense_documents_signed_lock
    BEFORE UPDATE ON expense_documents
    FOR EACH ROW EXECUTE FUNCTION expense_documents_signed_lock();

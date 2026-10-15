-- Dalga 12 (madde 92): Google Workspace / Microsoft 365 hesap açma (işe giriş) ve askıya alma (ayrılış).
-- Kurulum düzeyinde ACCOUNT_PROVISIONING_ENABLED=true ile açılır; her istek İK onayından sonra gönderilir,
-- her adım audit_log'a yazılır. Sağlayıcı sırları (servis hesabı anahtarı / client secret) şifreli (SecretBox).
-- İdempotent; mevcut veriyi değiştirmez.
-- Canlıya: docker exec -i hr360-postgres-1 psql -v ON_ERROR_STOP=1 -q -U hr360admin -d hr360_operational < bu_dosya

-- Kiracı başına sağlayıcı ayarı (Provider: Google | Microsoft).
--   Google   : CredentialsEnc = servis hesabı JSON anahtarı (alan genelinde yetki), AdminSubject = adına işlem yapılan yönetici
--   Microsoft: ClientId + CredentialsEnc (client secret) + MsTenant (dizin kimliği), uygulama izni User.ReadWrite.All
CREATE TABLE IF NOT EXISTS governance_provisioning_configs (
    "Id"             uuid PRIMARY KEY,
    "TenantSlug"     character varying(64) NOT NULL,
    "Provider"       character varying(16) NOT NULL,
    "IsEnabled"      boolean NOT NULL DEFAULT false,
    "Domain"         character varying(253) NOT NULL,
    "AutoCreate"     boolean NOT NULL DEFAULT true,
    "AutoSuspend"    boolean NOT NULL DEFAULT true,
    "ClientId"       character varying(256) NULL,
    "CredentialsEnc" text NULL,
    "AdminSubject"   character varying(320) NULL,
    "OrgUnit"        character varying(256) NULL,
    "MsTenant"       character varying(64) NULL,
    "UsageLocation"  character varying(2) NULL,
    "LastTestAt"     timestamptz NULL,
    "LastError"      character varying(500) NULL,
    "CreatedAt"      timestamptz NOT NULL DEFAULT now(),
    "UpdatedAt"      timestamptz NOT NULL DEFAULT now(),
    "UpdatedBy"      character varying(128) NULL
);
CREATE INDEX IF NOT EXISTS "IX_governance_provisioning_configs_TenantSlug" ON governance_provisioning_configs ("TenantSlug");
CREATE UNIQUE INDEX IF NOT EXISTS "UX_governance_provisioning_configs" ON governance_provisioning_configs ("TenantSlug", "Provider");

-- İstek: Action Create | Suspend; Status Pending | Processing | Done | Failed | Rejected; Source Auto | Manual.
-- Kişisel veri en aza: ad/soyad tutulmaz (çalışan kaydından okunur), yalnızca iş hesabı adresi.
CREATE TABLE IF NOT EXISTS governance_provisioning_requests (
    "Id"              uuid PRIMARY KEY,
    "TenantSlug"      character varying(64) NOT NULL,
    "EmployeeId"      uuid NOT NULL,
    "Provider"        character varying(16) NOT NULL,
    "Action"          character varying(16) NOT NULL,
    "Status"          character varying(16) NOT NULL DEFAULT 'Pending',
    "Source"          character varying(16) NOT NULL DEFAULT 'Auto',
    "AccountEmail"    character varying(320) NOT NULL,
    "ExternalId"      character varying(128) NULL,
    "Note"            character varying(500) NULL,
    "Error"           character varying(500) NULL,
    "RequestedBy"     character varying(128) NULL,
    "RequestedByName" character varying(200) NULL,
    "DecidedBy"       character varying(128) NULL,
    "DecidedByName"   character varying(200) NULL,
    "DecidedAt"       timestamptz NULL,
    "Attempts"        integer NOT NULL DEFAULT 0,
    "CreatedAt"       timestamptz NOT NULL DEFAULT now(),
    "CompletedAt"     timestamptz NULL
);
CREATE INDEX IF NOT EXISTS "IX_governance_provisioning_requests_TenantSlug" ON governance_provisioning_requests ("TenantSlug", "Status");
-- Aynı çalışan/sağlayıcı/işlem için tek açık ya da tamamlanmış istek (otomatik tarama idempotent kalır).
CREATE UNIQUE INDEX IF NOT EXISTS "UX_governance_provisioning_requests_open" ON governance_provisioning_requests
    ("TenantSlug", "EmployeeId", "Provider", "Action") WHERE "Status" IN ('Pending', 'Processing', 'Done');

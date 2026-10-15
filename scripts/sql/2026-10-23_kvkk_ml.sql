-- Dalga 10: KVKK (VERBİS envanteri, başvuru süre takibi ve veri paketi, rıza yenileme kampanyası,
-- imha kapsamı ve nesne deposu doğrulaması) + ML (kiracı verisiyle devir modeli eğitimi izni).
-- İdempotent; tüm tablolar "TenantSlug" ile kiracıya bağlı.

-- 53) VERBİS envanteri: ürünün yerleşik kataloğundan tohumlanır, İK/KVKK sorumlusu düzenler.
CREATE TABLE IF NOT EXISTS governance_privacy_inventory (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" varchar(64) NOT NULL,
    "ActivityKey" varchar(80) NOT NULL,
    "Source" varchar(16) NOT NULL DEFAULT 'Catalog',
    "Module" varchar(120) NOT NULL,
    "Activity" varchar(300) NOT NULL,
    "Subjects" text[] NOT NULL DEFAULT '{}',
    "DataCategories" text[] NOT NULL DEFAULT '{}',
    "Purpose" text NOT NULL DEFAULT '',
    "LegalBasis" text NOT NULL DEFAULT '',
    "Special" boolean NOT NULL DEFAULT false,
    "Retention" text NOT NULL DEFAULT '',
    "RetentionCategory" varchar(64),
    "Recipients" text[] NOT NULL DEFAULT '{}',
    "TransferProviders" text[] NOT NULL DEFAULT '{}',
    "Measures" text NOT NULL DEFAULT '',
    "IsActive" boolean NOT NULL DEFAULT true,
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
    "UpdatedBy" varchar(128),
    "UpdatedByName" varchar(256),
    CONSTRAINT "CK_governance_privacy_inventory_Source" CHECK ("Source" IN ('Catalog','Custom'))
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_governance_privacy_inventory_key" ON governance_privacy_inventory ("TenantSlug", "ActivityKey");

-- 54) İlgili kişi başvurusu: 20./27. gün hatırlatması ve süre aşımı uyarısı (her biri bir kez).
ALTER TABLE governance_data_requests ADD COLUMN IF NOT EXISTS "Reminder20At" timestamptz;
ALTER TABLE governance_data_requests ADD COLUMN IF NOT EXISTS "Reminder27At" timestamptz;
ALTER TABLE governance_data_requests ADD COLUMN IF NOT EXISTS "OverdueAlertAt" timestamptz;

-- 54) Erişim başvurusu veri paketi (ZIP, AES-256-GCM şifreli; süre sonunda silinir).
CREATE TABLE IF NOT EXISTS governance_data_request_packages (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" varchar(64) NOT NULL,
    "RequestId" uuid NOT NULL REFERENCES governance_data_requests("Id") ON DELETE CASCADE,
    "EmployeeId" uuid NOT NULL,
    "FileName" varchar(200) NOT NULL,
    "ContentEnc" text NOT NULL,
    "Sha256" varchar(64) NOT NULL,
    "SizeBytes" integer NOT NULL,
    "Sections" jsonb NOT NULL DEFAULT '{}'::jsonb,
    "CreatedBy" varchar(256) NOT NULL,
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "ExpiresAt" timestamptz NOT NULL,
    "DownloadCount" integer NOT NULL DEFAULT 0,
    "LastDownloadedAt" timestamptz
);
CREATE INDEX IF NOT EXISTS "IX_governance_data_request_packages_req" ON governance_data_request_packages ("TenantSlug", "RequestId");

-- 55) Rıza/aydınlatma metni yeni sürümünde yeniden onay kampanyası.
CREATE TABLE IF NOT EXISTS governance_consent_campaigns (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" varchar(64) NOT NULL,
    "ConsentType" varchar(64) NOT NULL,
    "Version" varchar(40) NOT NULL,
    "PreviousVersion" varchar(40),
    "Status" varchar(16) NOT NULL DEFAULT 'Open',
    "StartedBy" varchar(256) NOT NULL,
    "StartedAt" timestamptz NOT NULL DEFAULT now(),
    "ClosedAt" timestamptz,
    "ClosedBy" varchar(256),
    "ReminderCount" integer NOT NULL DEFAULT 0,
    "LastReminderAt" timestamptz,
    CONSTRAINT "CK_governance_consent_campaigns_Status" CHECK ("Status" IN ('Open','Closed'))
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_governance_consent_campaigns_version" ON governance_consent_campaigns ("TenantSlug", "ConsentType", "Version");

-- 56) İmha turunun tablo bazında dökümü (kaç satır, hangi işlem).
ALTER TABLE governance_destruction_logs ADD COLUMN IF NOT EXISTS "Details" jsonb;

-- 57) Silinen/anonimleştirilen kayıtların başvurduğu nesne deposu anahtarları ve silme doğrulaması.
-- Anahtarın kendisi (kişi adı içerebilir) işlendikten sonra NULL yapılır; yalnızca SHA-256 özeti kalır.
CREATE TABLE IF NOT EXISTS governance_storage_deletions (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" varchar(64) NOT NULL,
    "Category" varchar(64) NOT NULL,
    "SourceTable" varchar(80) NOT NULL,
    "SourceColumn" varchar(80) NOT NULL,
    "StorageKey" text,
    "KeyHash" varchar(64) NOT NULL,
    "Status" varchar(16) NOT NULL DEFAULT 'Pending',
    "Detail" text,
    "Attempts" integer NOT NULL DEFAULT 0,
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "ProcessedAt" timestamptz,
    CONSTRAINT "CK_governance_storage_deletions_Status" CHECK ("Status" IN ('Pending','Deleted','Absent','NotStored','Failed'))
);
CREATE INDEX IF NOT EXISTS "IX_governance_storage_deletions_tenant" ON governance_storage_deletions ("TenantSlug", "CreatedAt");
CREATE INDEX IF NOT EXISTS "IX_governance_storage_deletions_pending" ON governance_storage_deletions ("Status") WHERE "Status" IN ('Pending','Failed');

-- ML 36) Kiracı verisiyle devir modeli eğitimi: kiracının açık izni (varsayılan kapalı) ve son eğitim özeti.
ALTER TABLE governance_ml_model_settings ADD COLUMN IF NOT EXISTS "TrainOnTenantData" boolean NOT NULL DEFAULT false;
ALTER TABLE governance_ml_model_settings ADD COLUMN IF NOT EXISTS "TrainConsentBy" varchar(256);
ALTER TABLE governance_ml_model_settings ADD COLUMN IF NOT EXISTS "TrainConsentAt" timestamptz;
ALTER TABLE governance_ml_model_settings ADD COLUMN IF NOT EXISTS "LastTenantTrainingAt" timestamptz;
ALTER TABLE governance_ml_model_settings ADD COLUMN IF NOT EXISTS "LastTenantTraining" jsonb;

-- Yeni tablolara kiracı yalıtım politikası (RLS isteğe bağlı; açık değilse davranış değişmez).
DO $$ BEGIN
    IF to_regprocedure('hr360_rls_apply_policies()') IS NOT NULL THEN PERFORM hr360_rls_apply_policies(); END IF;
END $$;

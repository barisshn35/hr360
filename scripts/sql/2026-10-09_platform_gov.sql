-- Dalga 5d (governance-service): özel alanlar (Y24), kayıtlı/zamanlanmış raporlar (Y25/G4),
-- OTP ile basit elektronik imza (Y28), REST hook abonelikleri (G29). İdempotent.

-- ---------------------------------------------------------------- Y24 özel alanlar
CREATE TABLE IF NOT EXISTS governance_custom_fields (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "Target" text NOT NULL DEFAULT 'Employee',
    "Key" character varying(64) NOT NULL,
    "Label" text NOT NULL,
    "Type" text NOT NULL,
    "Options" jsonb NOT NULL DEFAULT '[]'::jsonb,
    "Required" boolean NOT NULL DEFAULT false,
    "Visibility" text NOT NULL DEFAULT 'hr',
    "SelfEditable" boolean NOT NULL DEFAULT false,
    "IsSpecialCategory" boolean NOT NULL DEFAULT false,
    "LegalBasis" text NOT NULL,
    "Purpose" text NOT NULL,
    "RetentionMonths" integer NOT NULL,
    "AssessmentId" uuid NULL,
    "IsActive" boolean NOT NULL DEFAULT true,
    "SortOrder" integer NOT NULL DEFAULT 0,
    "CreatedBy" text NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
    "UpdatedAt" timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT "PK_governance_custom_fields" PRIMARY KEY ("Id")
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_governance_custom_fields_key" ON governance_custom_fields ("TenantSlug", "Target", "Key");

CREATE TABLE IF NOT EXISTS governance_custom_field_values (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "FieldId" uuid NOT NULL REFERENCES governance_custom_fields ("Id") ON DELETE CASCADE,
    "EmployeeId" uuid NOT NULL,
    -- Özel nitelikli alanlarda "enc1:" + AES-256-GCM (TENANT_SECRET_KEY)
    "Value" text NOT NULL,
    "UpdatedBy" text NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT "PK_governance_custom_field_values" PRIMARY KEY ("Id")
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_governance_custom_field_values" ON governance_custom_field_values ("FieldId", "EmployeeId");
CREATE INDEX IF NOT EXISTS "IX_governance_custom_field_values_emp" ON governance_custom_field_values ("TenantSlug", "EmployeeId");

-- ---------------------------------------------------------------- Y25 / G4 kayıtlı raporlar
CREATE TABLE IF NOT EXISTS governance_saved_reports (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "OwnerUserId" text NOT NULL,
    "OwnerEmployeeId" uuid NULL,
    "OwnerIsHr" boolean NOT NULL DEFAULT false,
    "Name" text NOT NULL,
    "Question" text NOT NULL,
    "Lang" text NOT NULL DEFAULT 'tr',
    "DepartmentId" uuid NULL,
    "FromDate" date NULL,
    "ToDate" date NULL,
    "Compare" boolean NULL,
    "Metric" text NOT NULL,
    "GroupBy" text NOT NULL,
    "PersonLevel" boolean NOT NULL DEFAULT false,
    "Pinned" boolean NOT NULL DEFAULT false,
    "Schedule" text NOT NULL DEFAULT 'None',
    "NextRunAt" timestamp with time zone NULL,
    "LastRunAt" timestamp with time zone NULL,
    "DeliveryCount" integer NOT NULL DEFAULT 0,
    "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
    "UpdatedAt" timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT "PK_governance_saved_reports" PRIMARY KEY ("Id")
);
CREATE INDEX IF NOT EXISTS "IX_governance_saved_reports_owner" ON governance_saved_reports ("TenantSlug", "OwnerUserId");
CREATE INDEX IF NOT EXISTS "IX_governance_saved_reports_due" ON governance_saved_reports ("NextRunAt") WHERE "Schedule" <> 'None';
-- Teslim saati (Europe/Istanbul, SS:dd) ve günü (haftalık 1–7 pazartesi = 1; aylık 1–28; boşsa pazartesi / ayın 1'i).
ALTER TABLE governance_saved_reports ADD COLUMN IF NOT EXISTS "ScheduleTime" character varying(5) NOT NULL DEFAULT '07:00';
ALTER TABLE governance_saved_reports ADD COLUMN IF NOT EXISTS "ScheduleDay" integer NULL;

-- ---------------------------------------------------------------- Y28 OTP ile basit elektronik imza
CREATE TABLE IF NOT EXISTS governance_signature_otps (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "DocumentId" uuid NOT NULL REFERENCES governance_document_requests ("Id") ON DELETE CASCADE,
    "EmployeeId" uuid NOT NULL,
    -- HMAC-SHA256(TENANT_SECRET_KEY, Id + kod); kodun kendisi saklanmaz
    "CodeHash" text NOT NULL,
    "Channel" text NOT NULL,
    "Attempts" integer NOT NULL DEFAULT 0,
    "ExpiresAt" timestamp with time zone NOT NULL,
    "ConsumedAt" timestamp with time zone NULL,
    "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT "PK_governance_signature_otps" PRIMARY KEY ("Id")
);
CREATE INDEX IF NOT EXISTS "IX_governance_signature_otps_doc" ON governance_signature_otps ("DocumentId");

-- İmza kanıtı belgeye bağlıdır: belge saklama süresi sonunda silinince kanıt da silinir (CASCADE).
CREATE TABLE IF NOT EXISTS governance_signatures (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "DocumentType" text NOT NULL DEFAULT 'DocumentRequest',
    "DocumentId" uuid NOT NULL REFERENCES governance_document_requests ("Id") ON DELETE CASCADE,
    "DocumentVersion" integer NOT NULL DEFAULT 1,
    "DocumentSha256" text NOT NULL,
    "SignerEmployeeId" uuid NOT NULL,
    "SignedAt" timestamp with time zone NOT NULL,
    "Method" text NOT NULL,
    "IpPrefix" text NULL,
    "Disclaimer" text NOT NULL,
    "EvidenceSha256" text NOT NULL,
    CONSTRAINT "PK_governance_signatures" PRIMARY KEY ("Id")
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_governance_signatures_doc" ON governance_signatures ("DocumentId", "DocumentVersion", "SignerEmployeeId");

-- ---------------------------------------------------------------- G29 REST hook abonelikleri (Zapier / n8n)
ALTER TABLE governance_webhooks ADD COLUMN IF NOT EXISTS "Source" text NULL;
ALTER TABLE governance_webhooks ADD COLUMN IF NOT EXISTS "ApiKeyId" uuid NULL;

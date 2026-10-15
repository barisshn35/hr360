-- Dalga 5c (G14, G15, G16, G18, G6, G7): işe alışma şablonları ve "buddy", ilk gün karşılama,
-- zimmet QR/iade hatırlatma/bakım, offboarding hesap kapatma + zimmet kontrolü + imha planı,
-- vardiya tercihleri ve takas, puantaj geç kalma/fazla mesai ayarları.
-- Idempotent: tekrar çalıştırılabilir.

/* ============================================================ G14 onboarding */

CREATE TABLE IF NOT EXISTS onboarding_task_templates (
    "Id"            uuid PRIMARY KEY,
    "TenantSlug"    character varying(64) NOT NULL,
    "Name"          text NOT NULL,
    -- Eşleşme: unvan ve/veya departman (ikisi de boşsa herkese uygulanır).
    "PositionTitle" text NULL,
    "DepartmentId"  uuid NULL,
    "IsActive"      boolean NOT NULL DEFAULT true,
    "CreatedAt"     timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_onboarding_task_templates_TenantSlug" ON onboarding_task_templates ("TenantSlug");

CREATE TABLE IF NOT EXISTS onboarding_task_template_items (
    "Id"          uuid PRIMARY KEY,
    "TenantSlug"  character varying(64) NOT NULL,
    "TemplateId"  uuid NOT NULL REFERENCES onboarding_task_templates("Id") ON DELETE CASCADE,
    "Title"       text NOT NULL,
    "Category"    text NOT NULL DEFAULT 'Other',
    -- HR | Manager | IT | Buddy | Employee
    "OwnerRole"   text NOT NULL DEFAULT 'HR',
    -- Başlangıç tarihine göre gün (negatif: başlamadan önce).
    "OffsetDays"  integer NOT NULL DEFAULT 0,
    "Order"       integer NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS "IX_onboarding_task_template_items_TemplateId" ON onboarding_task_template_items ("TemplateId");
CREATE INDEX IF NOT EXISTS "IX_onboarding_task_template_items_TenantSlug" ON onboarding_task_template_items ("TenantSlug");

ALTER TABLE onboarding_tasks ADD COLUMN IF NOT EXISTS "OwnerRole" text NULL;

ALTER TABLE onboarding_plans ADD COLUMN IF NOT EXISTS "BuddyEmployeeId" uuid NULL;
ALTER TABLE onboarding_plans ADD COLUMN IF NOT EXISTS "Location" text NULL;
ALTER TABLE onboarding_plans ADD COLUMN IF NOT EXISTS "AppliedTemplates" text NULL;
ALTER TABLE onboarding_plans ADD COLUMN IF NOT EXISTS "WelcomeSentAt" timestamptz NULL;

-- Kiracı ayarları: ilk gün karşılama şablonu ve zimmet hatırlatmalarının İK sorumlusu.
CREATE TABLE IF NOT EXISTS onboarding_settings (
    "Id"                  uuid PRIMARY KEY,
    "TenantSlug"          character varying(64) NOT NULL,
    "WelcomeSubject"      text NULL,
    "WelcomeBody"         text NULL,
    "HrContactEmployeeId" uuid NULL,
    "ReminderDaysBefore"  integer NOT NULL DEFAULT 3,
    "UpdatedAt"           timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_onboarding_settings_TenantSlug" ON onboarding_settings ("TenantSlug");

/* ============================================================ G16 zimmet */

-- QR etiketi: kişisel veri içermeyen, tahmin edilemez demirbaş kodu.
ALTER TABLE onboarding_assets ADD COLUMN IF NOT EXISTS "QrCode" text NULL;
UPDATE onboarding_assets SET "QrCode" = upper(substr(md5(random()::text || "Id"::text || clock_timestamp()::text), 1, 16))
 WHERE "QrCode" IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS "UX_onboarding_assets_QrCode" ON onboarding_assets ("TenantSlug", "QrCode") WHERE "QrCode" IS NOT NULL;

ALTER TABLE onboarding_asset_assignments ADD COLUMN IF NOT EXISTS "ExpectedReturnOn" date NULL;
ALTER TABLE onboarding_asset_assignments ADD COLUMN IF NOT EXISTS "ReminderBeforeSentAt" timestamptz NULL;
ALTER TABLE onboarding_asset_assignments ADD COLUMN IF NOT EXISTS "ReminderOverdueSentAt" timestamptz NULL;

CREATE TABLE IF NOT EXISTS onboarding_asset_maintenance (
    "Id"                uuid PRIMARY KEY,
    "TenantSlug"        character varying(64) NOT NULL,
    "AssetId"           uuid NOT NULL REFERENCES onboarding_assets("Id") ON DELETE CASCADE,
    "Date"              date NOT NULL,
    -- Periodic | Repair | Inspection | Other
    "Type"              text NOT NULL DEFAULT 'Periodic',
    "Cost"              numeric(12,2) NULL,
    "Vendor"            text NULL,
    "Notes"             text NULL,
    "NextMaintenanceOn" date NULL,
    "CreatedBy"         text NULL,
    "CreatedAt"         timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_onboarding_asset_maintenance_AssetId" ON onboarding_asset_maintenance ("AssetId");
CREATE INDEX IF NOT EXISTS "IX_onboarding_asset_maintenance_TenantSlug" ON onboarding_asset_maintenance ("TenantSlug");

/* ============================================================ G15 offboarding */

ALTER TABLE engagement_offboarding_cases ADD COLUMN IF NOT EXISTS "AssetChecks" jsonb NOT NULL DEFAULT '[]'::jsonb;
-- Disabled | NoAccount | Failed | Skipped
ALTER TABLE engagement_offboarding_cases ADD COLUMN IF NOT EXISTS "AccountStatus" text NULL;
ALTER TABLE engagement_offboarding_cases ADD COLUMN IF NOT EXISTS "AccountDisabledAt" timestamptz NULL;
ALTER TABLE engagement_offboarding_cases ADD COLUMN IF NOT EXISTS "AccountNote" text NULL;
ALTER TABLE engagement_offboarding_cases ADD COLUMN IF NOT EXISTS "RetentionMonths" integer NULL;
ALTER TABLE engagement_offboarding_cases ADD COLUMN IF NOT EXISTS "PlannedAnonymizationOn" date NULL;

/* ============================================================ G6 vardiya tercihleri ve takas */

CREATE TABLE IF NOT EXISTS timeshift_shift_preferences (
    "Id"                  uuid PRIMARY KEY,
    "TenantSlug"          character varying(64) NOT NULL,
    "EmployeeId"          uuid NOT NULL,
    -- ISO gün numaraları: 1 = Pazartesi ... 7 = Pazar
    "PreferredDays"       integer[] NOT NULL DEFAULT '{}',
    "UnavailableDays"     integer[] NOT NULL DEFAULT '{}',
    -- Day | Night
    "PreferredShiftTypes" text[] NOT NULL DEFAULT '{}',
    "AvoidShiftTypes"     text[] NOT NULL DEFAULT '{}',
    "MaxNightsPerWeek"    integer NULL,
    "Note"                text NULL,
    "UpdatedAt"           timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_timeshift_shift_preferences_emp" ON timeshift_shift_preferences ("TenantSlug", "EmployeeId");

CREATE TABLE IF NOT EXISTS timeshift_swap_requests (
    "Id"                    uuid PRIMARY KEY,
    "TenantSlug"            character varying(64) NOT NULL,
    "RequesterEmployeeId"   uuid NOT NULL,
    "RequesterAssignmentId" uuid NOT NULL,
    "TargetEmployeeId"      uuid NOT NULL,
    -- NULL: devretme (karşılığında vardiya alınmaz)
    "TargetAssignmentId"    uuid NULL,
    -- PendingPeer | PendingApproval | Approved | Rejected | Declined | Cancelled
    "Status"                text NOT NULL,
    "Note"                  text NULL,
    "PeerRespondedAt"       timestamptz NULL,
    "DecidedBy"             text NULL,
    "DecidedAt"             timestamptz NULL,
    "RejectReason"          text NULL,
    "CreatedAt"             timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_timeshift_swap_requests_TenantSlug" ON timeshift_swap_requests ("TenantSlug");
CREATE INDEX IF NOT EXISTS "IX_timeshift_swap_requests_people" ON timeshift_swap_requests ("RequesterEmployeeId", "TargetEmployeeId");

/* ============================================================ G7 puantaj ayarları */

CREATE TABLE IF NOT EXISTS timeshift_settings (
    "Id"                  uuid PRIMARY KEY,
    "TenantSlug"          character varying(64) NOT NULL,
    "LateGraceMinutes"    integer NOT NULL DEFAULT 5,
    "DefaultStart"        time without time zone NOT NULL DEFAULT '09:00',
    "DefaultEnd"          time without time zone NOT NULL DEFAULT '18:00',
    "DefaultBreakMinutes" integer NOT NULL DEFAULT 60,
    "UpdatedAt"           timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_timeshift_settings_TenantSlug" ON timeshift_settings ("TenantSlug");

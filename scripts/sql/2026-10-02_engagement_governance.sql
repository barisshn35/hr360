-- =============================================================================
-- 2026-10-02 — Çalışan deneyimi (engagement-service), yönetişim
-- (governance-service) ve denetim kaydı (tüm servisler) tabloları.
--
-- Bu dosya IDEMPOTENT'tir: hem install.sh güncellemesinde (scripts/sql/*.sql)
-- hem de ilk kurulumda (data/migrations/sql-all-schemas.sql sonuna eklenmiş
-- kopyası) güvenle çalışır.
-- =============================================================================

-- ---------------------------------------------------------------- denetim kaydı
-- Her servisin EF SaveChanges kesicisi (Auditing/AuditInterceptor.cs) değişen
-- her varlık için bir satır yazar: eski/yeni değer, kullanıcı, zaman, istek
-- kimliği. Hassas alanlar (maaş, IBAN, TCKN, parola…) "***" olarak maskelenir.
CREATE TABLE IF NOT EXISTS audit_log (
    "Id" bigserial PRIMARY KEY,
    "TenantSlug" character varying(64),
    "Service" text NOT NULL,
    "EntityType" text NOT NULL,
    "EntityId" text,
    "Action" text NOT NULL,
    "Changes" jsonb,
    "UserId" text,
    "UserName" text,
    "CorrelationId" text,
    "IpAddress" text,
    "OccurredAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_audit_log_Tenant_Occurred" ON audit_log ("TenantSlug", "OccurredAt" DESC);
CREATE INDEX IF NOT EXISTS "IX_audit_log_Entity" ON audit_log ("EntityType", "EntityId");
CREATE INDEX IF NOT EXISTS "IX_audit_log_Correlation" ON audit_log ("CorrelationId");

-- ------------------------------------------------------------- engagement-service
CREATE TABLE IF NOT EXISTS engagement_kudos (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "FromUserId" text NOT NULL,
    "FromEmployeeId" uuid,
    "FromName" text NOT NULL,
    "ToEmployeeId" uuid NOT NULL,
    "ToName" text NOT NULL,
    "Badge" text NOT NULL,
    "Message" text NOT NULL,
    "LikedBy" text[] NOT NULL DEFAULT '{}',
    "CreatedAt" timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_engagement_kudos_TenantSlug" ON engagement_kudos ("TenantSlug", "CreatedAt" DESC);

CREATE TABLE IF NOT EXISTS engagement_profiles (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "BirthDate" date,
    "ShowBirthday" boolean NOT NULL DEFAULT true,
    "Bio" text,
    "Pronouns" text,
    "Skills" text[] NOT NULL DEFAULT '{}',
    "Interests" text[] NOT NULL DEFAULT '{}',
    "Address" text,
    "EmergencyContactName" text,
    "EmergencyContactPhone" text,
    "Iban" text,
    "NationalId" text,
    "LinkedInUrl" text,
    "UpdatedAt" timestamptz NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_engagement_profiles_Employee" ON engagement_profiles ("TenantSlug", "EmployeeId");

CREATE TABLE IF NOT EXISTS engagement_desks (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Code" text NOT NULL,
    "Name" text NOT NULL,
    "Kind" text NOT NULL,
    "Floor" text,
    "Zone" text,
    "Capacity" integer NOT NULL DEFAULT 1,
    "Features" text[] NOT NULL DEFAULT '{}',
    "IsActive" boolean NOT NULL DEFAULT true,
    "CreatedAt" timestamptz NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_engagement_desks_Code" ON engagement_desks ("TenantSlug", "Code");

CREATE TABLE IF NOT EXISTS engagement_desk_bookings (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "DeskId" uuid NOT NULL REFERENCES engagement_desks("Id") ON DELETE CASCADE,
    "UserId" text NOT NULL,
    "EmployeeId" uuid,
    "PersonName" text NOT NULL,
    "Date" date NOT NULL,
    "StartMinute" integer NOT NULL,
    "EndMinute" integer NOT NULL,
    "Title" text,
    "CreatedAt" timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_engagement_desk_bookings_Date" ON engagement_desk_bookings ("TenantSlug", "Date");

CREATE TABLE IF NOT EXISTS engagement_presence (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "UserId" text NOT NULL,
    "EmployeeId" uuid,
    "PersonName" text NOT NULL,
    "Date" date NOT NULL,
    "Mode" text NOT NULL,
    "Note" text,
    "CreatedAt" timestamptz NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_engagement_presence_User_Date" ON engagement_presence ("TenantSlug", "UserId", "Date");

CREATE TABLE IF NOT EXISTS engagement_mentor_profiles (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "UserId" text NOT NULL,
    "EmployeeId" uuid,
    "PersonName" text NOT NULL,
    "IsMentor" boolean NOT NULL,
    "IsMentee" boolean NOT NULL,
    "Offers" text[] NOT NULL DEFAULT '{}',
    "Wants" text[] NOT NULL DEFAULT '{}',
    "Capacity" integer NOT NULL DEFAULT 2,
    "Bio" text,
    "UpdatedAt" timestamptz NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_engagement_mentor_profiles_User" ON engagement_mentor_profiles ("TenantSlug", "UserId");

CREATE TABLE IF NOT EXISTS engagement_mentorships (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "MentorUserId" text NOT NULL,
    "MentorName" text NOT NULL,
    "MenteeUserId" text NOT NULL,
    "MenteeName" text NOT NULL,
    "Goal" text,
    "Status" text NOT NULL,
    "MatchScore" integer NOT NULL DEFAULT 0,
    "CreatedAt" timestamptz NOT NULL,
    "StartedAt" timestamptz,
    "EndedAt" timestamptz
);
CREATE INDEX IF NOT EXISTS "IX_engagement_mentorships_TenantSlug" ON engagement_mentorships ("TenantSlug");

CREATE TABLE IF NOT EXISTS engagement_internal_applications (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "JobPostingId" uuid NOT NULL,
    "JobTitle" text NOT NULL,
    "UserId" text NOT NULL,
    "EmployeeId" uuid,
    "PersonName" text NOT NULL,
    "Motivation" text,
    "Status" text NOT NULL,
    "CreatedAt" timestamptz NOT NULL,
    "UpdatedAt" timestamptz NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_engagement_internal_app" ON engagement_internal_applications ("TenantSlug", "JobPostingId", "UserId");

CREATE TABLE IF NOT EXISTS engagement_one_on_ones (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "ManagerUserId" text NOT NULL,
    "ManagerName" text NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "EmployeeUserId" text,
    "EmployeeName" text NOT NULL,
    "ScheduledAt" timestamptz NOT NULL,
    "Status" text NOT NULL,
    "Agenda" jsonb NOT NULL DEFAULT '[]',
    "SharedNotes" text,
    "PrivateNotes" text,
    "ActionItems" jsonb NOT NULL DEFAULT '[]',
    "Mood" integer,
    "CreatedAt" timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_engagement_one_on_ones_Tenant" ON engagement_one_on_ones ("TenantSlug", "ScheduledAt");

CREATE TABLE IF NOT EXISTS engagement_succession_plans (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "PositionTitle" text NOT NULL,
    "DepartmentName" text,
    "IncumbentEmployeeId" uuid,
    "IncumbentName" text,
    "Criticality" text NOT NULL,
    "VacancyRisk" text NOT NULL,
    "Candidates" jsonb NOT NULL DEFAULT '[]',
    "Notes" text,
    "CreatedAt" timestamptz NOT NULL,
    "UpdatedAt" timestamptz NOT NULL
);

CREATE TABLE IF NOT EXISTS engagement_surveys (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Title" text NOT NULL,
    "Description" text,
    "Kind" text NOT NULL,
    "Questions" jsonb NOT NULL DEFAULT '[]',
    "IsAnonymous" boolean NOT NULL DEFAULT true,
    "Status" text NOT NULL,
    "ClosesAt" timestamptz,
    "CreatedByName" text,
    "CreatedAt" timestamptz NOT NULL
);

CREATE TABLE IF NOT EXISTS engagement_survey_responses (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "SurveyId" uuid NOT NULL REFERENCES engagement_surveys("Id") ON DELETE CASCADE,
    "RespondentKey" text NOT NULL,
    "DepartmentName" text,
    "Answers" jsonb NOT NULL,
    "SubmittedAt" timestamptz NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_engagement_survey_resp" ON engagement_survey_responses ("SurveyId", "RespondentKey");

CREATE TABLE IF NOT EXISTS engagement_offboarding_cases (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "EmployeeName" text NOT NULL,
    "LastWorkingDay" date NOT NULL,
    "Reason" text NOT NULL,
    "Status" text NOT NULL,
    "Checklist" jsonb NOT NULL DEFAULT '[]',
    "ExitInterview" jsonb,
    "RehireEligible" boolean,
    "CreatedAt" timestamptz NOT NULL,
    "CompletedAt" timestamptz
);

CREATE TABLE IF NOT EXISTS engagement_org_scenarios (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Name" text NOT NULL,
    "Description" text,
    "Moves" jsonb NOT NULL DEFAULT '[]',
    "Status" text NOT NULL,
    "CreatedByName" text,
    "CreatedAt" timestamptz NOT NULL,
    "UpdatedAt" timestamptz NOT NULL
);

-- ------------------------------------------------------------- governance-service
CREATE TABLE IF NOT EXISTS governance_events (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64),
    "Topic" text NOT NULL,
    "EventType" text NOT NULL,
    "Payload" jsonb,
    "OccurredAt" timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_governance_events_Tenant" ON governance_events ("TenantSlug", "OccurredAt" DESC);

CREATE TABLE IF NOT EXISTS governance_consents (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "UserId" text NOT NULL,
    "EmployeeId" uuid,
    "PersonName" text NOT NULL,
    "ConsentType" text NOT NULL,
    "Version" text NOT NULL,
    "Granted" boolean NOT NULL,
    "IpAddress" text,
    "RecordedAt" timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_governance_consents_User" ON governance_consents ("TenantSlug", "UserId");

CREATE TABLE IF NOT EXISTS governance_data_requests (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "UserId" text NOT NULL,
    "EmployeeId" uuid,
    "PersonName" text NOT NULL,
    "Kind" text NOT NULL,
    "Details" text,
    "Status" text NOT NULL,
    "Response" text,
    "DueAt" timestamptz NOT NULL,
    "CreatedAt" timestamptz NOT NULL,
    "CompletedAt" timestamptz
);

CREATE TABLE IF NOT EXISTS governance_retention_policies (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Category" text NOT NULL,
    "RetentionMonths" integer NOT NULL,
    "Action" text NOT NULL,
    "IsEnabled" boolean NOT NULL,
    "LastRunAt" timestamptz,
    "LastAffected" integer NOT NULL DEFAULT 0
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_governance_retention" ON governance_retention_policies ("TenantSlug", "Category");

CREATE TABLE IF NOT EXISTS governance_doc_templates (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Name" text NOT NULL,
    "Category" text NOT NULL,
    "Body" text NOT NULL,
    "CreatedAt" timestamptz NOT NULL,
    "UpdatedAt" timestamptz NOT NULL
);

CREATE TABLE IF NOT EXISTS governance_rules (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Name" text NOT NULL,
    "Description" text,
    "Trigger" text NOT NULL,
    "Conditions" jsonb NOT NULL DEFAULT '[]',
    "Actions" jsonb NOT NULL DEFAULT '[]',
    "IsEnabled" boolean NOT NULL,
    "FireCount" integer NOT NULL DEFAULT 0,
    "LastFiredAt" timestamptz,
    "CreatedAt" timestamptz NOT NULL
);

CREATE TABLE IF NOT EXISTS governance_rule_runs (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "RuleId" uuid NOT NULL,
    "RuleName" text NOT NULL,
    "EventType" text NOT NULL,
    "Result" text NOT NULL,
    "OccurredAt" timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_governance_rule_runs" ON governance_rule_runs ("TenantSlug", "OccurredAt" DESC);

CREATE TABLE IF NOT EXISTS governance_webhooks (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Name" text NOT NULL,
    "Url" text NOT NULL,
    "Secret" text NOT NULL,
    "Events" text[] NOT NULL DEFAULT '{}',
    "IsEnabled" boolean NOT NULL,
    "LastStatus" integer,
    "LastDeliveredAt" timestamptz,
    "FailureCount" integer NOT NULL DEFAULT 0,
    "CreatedAt" timestamptz NOT NULL
);

CREATE TABLE IF NOT EXISTS governance_webhook_deliveries (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "WebhookId" uuid NOT NULL,
    "EventType" text NOT NULL,
    "StatusCode" integer,
    "Error" text,
    "DurationMs" integer NOT NULL,
    "OccurredAt" timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_governance_webhook_deliveries" ON governance_webhook_deliveries ("WebhookId", "OccurredAt" DESC);

CREATE TABLE IF NOT EXISTS governance_api_keys (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Name" text NOT NULL,
    "Prefix" text NOT NULL,
    "KeyHash" text NOT NULL,
    "Scopes" text[] NOT NULL DEFAULT '{}',
    "CreatedByName" text,
    "CreatedAt" timestamptz NOT NULL,
    "LastUsedAt" timestamptz,
    "RevokedAt" timestamptz
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_governance_api_keys_Hash" ON governance_api_keys ("KeyHash");

CREATE TABLE IF NOT EXISTS governance_integrations (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Kind" text NOT NULL,
    "Name" text NOT NULL,
    "WebhookUrl" text NOT NULL,
    "Events" text[] NOT NULL DEFAULT '{}',
    "SigningSecret" text,
    "IsEnabled" boolean NOT NULL,
    "LastStatus" integer,
    "CreatedAt" timestamptz NOT NULL
);

CREATE TABLE IF NOT EXISTS governance_invoices (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Number" text NOT NULL,
    "Period" text NOT NULL,
    "Plan" text NOT NULL,
    "Seats" integer NOT NULL,
    "UnitPrice" numeric(12,2) NOT NULL,
    "Amount" numeric(12,2) NOT NULL,
    "TaxAmount" numeric(12,2) NOT NULL,
    "Total" numeric(12,2) NOT NULL,
    "Currency" text NOT NULL,
    "Status" text NOT NULL,
    "IssuedAt" timestamptz NOT NULL,
    "DueAt" timestamptz NOT NULL,
    "PaidAt" timestamptz,
    "PaymentRef" text
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_governance_invoices_Period" ON governance_invoices ("TenantSlug", "Period");

CREATE TABLE IF NOT EXISTS governance_calendar_feeds (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "UserId" text NOT NULL,
    "EmployeeId" uuid,
    "Token" text NOT NULL,
    "CreatedAt" timestamptz NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_governance_calendar_feeds_Token" ON governance_calendar_feeds ("Token");
CREATE UNIQUE INDEX IF NOT EXISTS "UX_governance_calendar_feeds_User" ON governance_calendar_feeds ("TenantSlug", "UserId");

CREATE TABLE IF NOT EXISTS governance_kb_articles (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Title" text NOT NULL,
    "Body" text NOT NULL,
    "Tags" text[] NOT NULL DEFAULT '{}',
    "CreatedAt" timestamptz NOT NULL,
    "UpdatedAt" timestamptz NOT NULL
);

-- ------------------------------------------------- analitik (hafif veri ambarı)
-- Operasyonel tabloların üstünde, raporlama için sabitlenmiş görünümler. BI
-- aracı (Metabase/Power BI) bağlanacaksa bu görünümler okunur; operasyonel
-- şemadaki değişiklik raporları kırmaz.
CREATE OR REPLACE VIEW analytics_hires_monthly AS
SELECT "TenantSlug" AS tenant_slug,
       date_trunc('month', "HireDate")::date AS month,
       count(*) AS hires
FROM employee_employees
GROUP BY 1, 2;

CREATE OR REPLACE VIEW analytics_leave_monthly AS
SELECT "TenantSlug" AS tenant_slug,
       date_trunc('month', "StartDate")::date AS month,
       "Type" AS leave_type,
       "Status" AS status,
       count(*) AS requests,
       sum("Days") AS days
FROM leave_requests
GROUP BY 1, 2, 3, 4;

CREATE OR REPLACE VIEW analytics_department_headcount AS
SELECT a."TenantSlug" AS tenant_slug,
       coalesce(d."Name", 'Atanmamış') AS department,
       count(DISTINCT a."EmployeeId") AS headcount
FROM employee_assignments a
JOIN employee_employees e ON e."Id" = a."EmployeeId" AND e."Status" <> 'Terminated'
LEFT JOIN organization_departments d ON d."Id" = a."DepartmentId"
WHERE a."EffectiveFrom" <= current_date AND (a."EffectiveTo" IS NULL OR a."EffectiveTo" >= current_date)
GROUP BY 1, 2;

CREATE OR REPLACE VIEW analytics_overtime_monthly AS
SELECT "TenantSlug" AS tenant_slug,
       date_trunc('month', "Date")::date AS month,
       sum("WorkedMinutes") AS worked_minutes,
       sum("OvertimeMinutes") AS overtime_minutes
FROM timeshift_time_entries
GROUP BY 1, 2;

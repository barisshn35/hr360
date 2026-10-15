-- Güvenlik dalgası 2B — veri koruma. İdempotent: tekrar çalıştırılabilir.
-- Servisler (compensation, governance) bu dosya uygulanmadan dağıtılmamalı: compensation-service'in
-- EF modeli yeni sütunları okur.

-- ---------------------------------------------------------------- 1) Bordro görevler ayrılığı
-- Dönemi hesaplayan ve elle ek ödeme/kesinti giren kişi kayda geçer; aynı kişi dönemi kapatamaz
-- (compensation-service Payroll/SegregationOfDuties.cs). Eski satırlarda boş (kural uygulanmaz).
ALTER TABLE compensation_payroll_periods ADD COLUMN IF NOT EXISTS "CalculatedBy" text;
ALTER TABLE compensation_payroll_periods ADD COLUMN IF NOT EXISTS "CalculatedByName" text;
ALTER TABLE compensation_payroll_adjustments ADD COLUMN IF NOT EXISTS "CreatedBy" text;

-- ---------------------------------------------------------------- 2) Kiracı veri koruma ayarları
-- Kiracı başına tek satır; satır yoksa varsayılanlar (görevler ayrılığı AÇIK, 10 dakikada 50'den fazla
-- farklı kayıt = uyarı, engel kapalı). Görevler ayrılığını yalnızca şirket yöneticisi gerekçeyle kapatır.
CREATE TABLE IF NOT EXISTS governance_security_settings (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" varchar(64) NOT NULL,
    "PayrollSod" boolean NOT NULL DEFAULT true,
    "PayrollSodReason" text,
    "MassViewThreshold" integer NOT NULL DEFAULT 50,
    "MassViewWindowMinutes" integer NOT NULL DEFAULT 10,
    "MassViewBlock" boolean NOT NULL DEFAULT false,
    "AlertEmployeeIds" uuid[] NOT NULL DEFAULT '{}',
    "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
    "UpdatedBy" text,
    CONSTRAINT "CK_governance_security_settings_MassView" CHECK ("MassViewThreshold" BETWEEN 10 AND 1000 AND "MassViewWindowMinutes" BETWEEN 1 AND 60)
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_governance_security_settings_TenantSlug" ON governance_security_settings ("TenantSlug");

-- ---------------------------------------------------------------- 3) Güvenlik uyarıları (toplu görüntüleme)
-- governance MassViewWorker yazar. "BlockedUntil" yalnızca kiracı "geçici engel" ayarını açtıysa dolar;
-- engagement-service (TCKN/IBAN açma) ve governance (özel nitelikli ek alanlar) bu süre boyunca açmaz.
CREATE TABLE IF NOT EXISTS governance_security_alerts (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" varchar(64) NOT NULL,
    "Kind" varchar(32) NOT NULL,
    "UserId" text NOT NULL,
    "UserName" text,
    "DistinctCount" integer NOT NULL,
    "WindowMinutes" integer NOT NULL,
    "Threshold" integer NOT NULL,
    "DetectedAt" timestamptz NOT NULL DEFAULT now(),
    "BlockedUntil" timestamptz,
    "AcknowledgedAt" timestamptz,
    "AcknowledgedBy" text
);
CREATE INDEX IF NOT EXISTS "IX_governance_security_alerts_Tenant" ON governance_security_alerts ("TenantSlug", "DetectedAt" DESC);
CREATE INDEX IF NOT EXISTS "IX_governance_security_alerts_Block" ON governance_security_alerts ("TenantSlug", "UserId", "BlockedUntil");

-- ---------------------------------------------------------------- 4) İz kodu araması
-- Filigranlı çıktıların iz kodu audit_log."Changes"->>'traceCode' içinde; kodla kaynak bulma için kısmi indeks.
-- Toplu görüntüleme dedektörü son 60 dakikanın erişim satırlarını okur.
CREATE INDEX IF NOT EXISTS "IX_audit_log_TraceCode" ON audit_log ((("Changes"->>'traceCode'))) WHERE "Changes" ? 'traceCode';
CREATE INDEX IF NOT EXISTS "IX_audit_log_Access_Occurred" ON audit_log ("OccurredAt")
    WHERE "Action" IN ('Revealed', 'SensitiveViewed', 'Exported', 'Viewed');

-- ---------------------------------------------------------------- 5) Erişim gözden geçirme kampanyaları
-- İK başlatır (çeyreklik ya da istendiğinde); her satır bir kişinin o anki rol/izinleri ve gözden
-- geçirenin (departman başı; yoksa İK) kararıdır. Ad tutulmaz (çalışan tablosundan okunur).
CREATE TABLE IF NOT EXISTS governance_access_reviews (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" varchar(64) NOT NULL,
    "Title" text NOT NULL,
    "Status" varchar(16) NOT NULL DEFAULT 'Open',
    "DueDate" date,
    "CreatedBy" text NOT NULL,
    "CreatedByName" text,
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "ClosedAt" timestamptz,
    "LastReminderAt" timestamptz,
    "QuarterReminderAt" timestamptz,
    CONSTRAINT "CK_governance_access_reviews_Status" CHECK ("Status" IN ('Open', 'Closed'))
);
CREATE INDEX IF NOT EXISTS "IX_governance_access_reviews_Tenant" ON governance_access_reviews ("TenantSlug", "CreatedAt" DESC);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_governance_access_reviews_OneOpen" ON governance_access_reviews ("TenantSlug") WHERE "Status" = 'Open';

CREATE TABLE IF NOT EXISTS governance_access_review_items (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" varchar(64) NOT NULL,
    "ReviewId" uuid NOT NULL REFERENCES governance_access_reviews ("Id") ON DELETE CASCADE,
    "EmployeeId" uuid NOT NULL,
    "KeycloakUserId" text NOT NULL,
    "Roles" text[] NOT NULL DEFAULT '{}',
    "Permissions" text[] NOT NULL DEFAULT '{}',
    "ReviewerEmployeeId" uuid,
    "Decision" varchar(16),
    "RemoveRoles" text[] NOT NULL DEFAULT '{}',
    "Note" text,
    "DecidedBy" text,
    "DecidedByName" text,
    "DecidedAt" timestamptz,
    "AppliedAt" timestamptz,
    "AppliedBy" text,
    "ApplyResult" text,
    CONSTRAINT "CK_governance_access_review_items_Decision" CHECK ("Decision" IS NULL OR "Decision" IN ('Keep', 'Remove'))
);
CREATE INDEX IF NOT EXISTS "IX_governance_access_review_items_Review" ON governance_access_review_items ("ReviewId");
CREATE INDEX IF NOT EXISTS "IX_governance_access_review_items_Reviewer" ON governance_access_review_items ("TenantSlug", "ReviewerEmployeeId");

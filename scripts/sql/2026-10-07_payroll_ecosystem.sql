-- Dalga 5b: bordro ekosistemi ve masraf
--   Y1/Y3/Y4 bordro dosyaları, Y11 avans/borç, Y13 esnek yan haklar, Y21 zam dönemi,
--   G9 döviz kuru/km/limit, Y12 seyahat ve harcırah. Idempotent.

CREATE TABLE IF NOT EXISTS compensation_payroll_exports (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "PeriodId" uuid NOT NULL,
    "Kind" text NOT NULL,
    "FileName" text NOT NULL,
    "ContentType" text NOT NULL,
    "Cipher" bytea,
    "RowCount" integer NOT NULL DEFAULT 0,
    "SingleUse" boolean NOT NULL DEFAULT false,
    "CreatedBy" text NOT NULL DEFAULT '',
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "ExpiresAt" timestamptz NOT NULL,
    "DownloadedAt" timestamptz,
    "DownloadedBy" text,
    "DownloadCount" integer NOT NULL DEFAULT 0,
    "PurgedAt" timestamptz
);
CREATE INDEX IF NOT EXISTS "IX_payroll_exports_period" ON compensation_payroll_exports ("TenantSlug", "PeriodId");

CREATE TABLE IF NOT EXISTS compensation_advances (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "Kind" text NOT NULL DEFAULT 'Advance',
    "Amount" numeric(18,2) NOT NULL,
    "Installments" integer NOT NULL DEFAULT 1,
    "StartYear" integer NOT NULL,
    "StartMonth" integer NOT NULL,
    "Reason" text,
    "Status" text NOT NULL DEFAULT 'Pending',
    "RepaidAmount" numeric(18,2) NOT NULL DEFAULT 0,
    "DecidedBy" text,
    "DecisionNote" text,
    "DecidedAt" timestamptz,
    "CreatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_advances_employee" ON compensation_advances ("TenantSlug", "EmployeeId");

CREATE TABLE IF NOT EXISTS compensation_benefit_plans (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Year" integer NOT NULL,
    "BudgetPerEmployee" numeric(18,2) NOT NULL,
    "WindowStart" date NOT NULL,
    "WindowEnd" date NOT NULL,
    "UpdatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_benefit_plans" ON compensation_benefit_plans ("TenantSlug", "Year");
CREATE TABLE IF NOT EXISTS compensation_benefit_options (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "PlanId" uuid NOT NULL,
    "Name" text NOT NULL,
    "Category" text NOT NULL,
    "AnnualCost" numeric(18,2) NOT NULL,
    "Description" text,
    "IsActive" boolean NOT NULL DEFAULT true
);
CREATE TABLE IF NOT EXISTS compensation_benefit_elections (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "PlanId" uuid NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "OptionIdsJson" text NOT NULL DEFAULT '[]',
    "Total" numeric(18,2) NOT NULL DEFAULT 0,
    "UpdatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_benefit_elections" ON compensation_benefit_elections ("PlanId", "EmployeeId");

CREATE TABLE IF NOT EXISTS compensation_raise_cycles (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Name" text NOT NULL,
    "Year" integer NOT NULL,
    "BudgetPercent" numeric(9,4) NOT NULL,
    "EffectiveDate" date NOT NULL,
    "Status" text NOT NULL DEFAULT 'Draft',
    "CreatedBy" text NOT NULL DEFAULT '',
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "AppliedAt" timestamptz
);
CREATE TABLE IF NOT EXISTS compensation_raise_proposals (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "CycleId" uuid NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "CurrentSalary" numeric(18,2) NOT NULL,
    "ProposedPercent" numeric(9,4) NOT NULL,
    "ProposedSalary" numeric(18,2) NOT NULL,
    "Note" text,
    "Status" text NOT NULL DEFAULT 'Proposed',
    "ProposedBy" text NOT NULL DEFAULT '',
    "DecidedBy" text,
    "UpdatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_raise_proposals" ON compensation_raise_proposals ("CycleId", "EmployeeId");

-- G9 masraf
ALTER TABLE expense_items ADD COLUMN IF NOT EXISTS "OriginalCurrency" text;
ALTER TABLE expense_items ADD COLUMN IF NOT EXISTS "OriginalAmount" numeric(18,2);
ALTER TABLE expense_items ADD COLUMN IF NOT EXISTS "FxRate" numeric(18,6);
ALTER TABLE expense_items ADD COLUMN IF NOT EXISTS "Km" numeric(10,2);
ALTER TABLE expense_items ADD COLUMN IF NOT EXISTS "TravelRequestId" uuid;
CREATE TABLE IF NOT EXISTS expense_fx_rates (
    "Id" uuid PRIMARY KEY,
    "Date" date NOT NULL,
    "Currency" text NOT NULL,
    "Rate" numeric(18,6) NOT NULL,
    "Source" text NOT NULL DEFAULT 'TCMB',
    "TenantSlug" character varying(64),
    "FetchedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_fx_rates" ON expense_fx_rates ("Date", "Currency", "TenantSlug");
CREATE TABLE IF NOT EXISTS expense_policies (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "LimitsJson" text NOT NULL DEFAULT '{}',
    "KmRate" numeric(10,2) NOT NULL DEFAULT 8,
    "PerDiemDomestic" numeric(18,2) NOT NULL DEFAULT 1000,
    "PerDiemAbroad" numeric(18,2) NOT NULL DEFAULT 100,
    "PerDiemAbroadCurrency" text NOT NULL DEFAULT 'EUR',
    "UpdatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_expense_policies" ON expense_policies ("TenantSlug");

-- Y12 seyahat
CREATE TABLE IF NOT EXISTS expense_travel_requests (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "Destination" text NOT NULL,
    "Abroad" boolean NOT NULL DEFAULT false,
    "StartDate" date NOT NULL,
    "EndDate" date NOT NULL,
    "Purpose" text NOT NULL,
    "Transport" text NOT NULL DEFAULT 'Plane',
    "NeedsAccommodation" boolean NOT NULL DEFAULT false,
    "PerDiemDays" integer NOT NULL DEFAULT 0,
    "PerDiemRate" numeric(18,2) NOT NULL DEFAULT 0,
    "PerDiemCurrency" text NOT NULL DEFAULT 'TRY',
    "PerDiemTotal" numeric(18,2) NOT NULL DEFAULT 0,
    "AdvanceRequested" numeric(18,2),
    "PassportCipher" text,
    "PassportPurgedAt" timestamptz,
    "Status" text NOT NULL DEFAULT 'Submitted',
    "WorkflowRequestId" uuid,
    "DecidedByEmployeeId" uuid,
    "CreatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_travel_employee" ON expense_travel_requests ("TenantSlug", "EmployeeId");

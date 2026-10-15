-- Bordro dönemi (compensation-service), fazla mesai talebi ve giriş-çıkış (timeshift-service).
CREATE TABLE IF NOT EXISTS compensation_payroll_periods (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Year" integer NOT NULL,
    "Month" integer NOT NULL,
    "Status" text NOT NULL,
    "CalculatedAt" timestamptz,
    "ClosedAt" timestamptz,
    "ClosedBy" text,
    "CreatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_compensation_payroll_periods_TenantSlug_Year_Month" ON compensation_payroll_periods ("TenantSlug", "Year", "Month");

CREATE TABLE IF NOT EXISTS compensation_payroll_parameters (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Year" integer NOT NULL,
    "MinimumWageGross" numeric NOT NULL,
    "SgkEmployerRate" numeric NOT NULL,
    "EmployerIncentivePoints" numeric NOT NULL,
    "StampTaxRate" numeric NOT NULL,
    "SgkCeilingMultiplier" numeric NOT NULL,
    "BracketsJson" text NOT NULL,
    "UpdatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_compensation_payroll_parameters_TenantSlug_Year" ON compensation_payroll_parameters ("TenantSlug", "Year");

CREATE TABLE IF NOT EXISTS compensation_payroll_adjustments (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "PeriodId" uuid NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "Kind" text NOT NULL,
    "Amount" numeric NOT NULL,
    "Description" text NOT NULL,
    "SourceId" uuid,
    "CreatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_compensation_payroll_adjustments_PeriodId_EmployeeId" ON compensation_payroll_adjustments ("PeriodId", "EmployeeId");

CREATE TABLE IF NOT EXISTS compensation_payslips (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "PeriodId" uuid NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "Year" integer NOT NULL,
    "Month" integer NOT NULL,
    "Currency" text NOT NULL,
    "MonthlyBaseGross" numeric NOT NULL,
    "PaidDays" integer NOT NULL,
    "UnpaidDays" integer NOT NULL,
    "OvertimeHours" numeric NOT NULL,
    "BaseGross" numeric NOT NULL,
    "OvertimePay" numeric NOT NULL,
    "Additions" numeric NOT NULL,
    "Gross" numeric NOT NULL,
    "SgkBase" numeric NOT NULL,
    "SgkEmployee" numeric NOT NULL,
    "UnemploymentEmployee" numeric NOT NULL,
    "TaxBase" numeric NOT NULL,
    "CumulativeTaxBase" numeric NOT NULL,
    "IncomeTax" numeric NOT NULL,
    "IncomeTaxExemption" numeric NOT NULL,
    "StampTax" numeric NOT NULL,
    "StampTaxExemption" numeric NOT NULL,
    "Deductions" numeric NOT NULL,
    "Net" numeric NOT NULL,
    "SgkEmployer" numeric NOT NULL,
    "UnemploymentEmployer" numeric NOT NULL,
    "EmployerCost" numeric NOT NULL,
    "CreatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_compensation_payslips_PeriodId_EmployeeId" ON compensation_payslips ("PeriodId", "EmployeeId");
CREATE INDEX IF NOT EXISTS "IX_compensation_payslips_EmployeeId_Year" ON compensation_payslips ("EmployeeId", "Year");

CREATE TABLE IF NOT EXISTS timeshift_overtime_requests (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "Date" date NOT NULL,
    "Hours" numeric NOT NULL,
    "Reason" text,
    "Status" text NOT NULL,
    "WorkflowRequestId" uuid,
    "CreatedBy" text,
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "DecidedAt" timestamptz
);
CREATE INDEX IF NOT EXISTS "IX_timeshift_overtime_requests_EmployeeId_Date" ON timeshift_overtime_requests ("EmployeeId", "Date");
CREATE INDEX IF NOT EXISTS "IX_timeshift_overtime_requests_WorkflowRequestId" ON timeshift_overtime_requests ("WorkflowRequestId");

CREATE TABLE IF NOT EXISTS timeshift_clock_sites (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Name" text NOT NULL,
    "AllowQr" boolean NOT NULL DEFAULT true,
    "AllowTerminal" boolean NOT NULL DEFAULT true,
    "CheckLocation" boolean NOT NULL DEFAULT false,
    "Latitude" double precision,
    "Longitude" double precision,
    "RadiusMeters" integer NOT NULL DEFAULT 200,
    "QrSecret" text NOT NULL,
    "DeviceKeyHash" text,
    "IsActive" boolean NOT NULL DEFAULT true,
    "CreatedAt" timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS timeshift_clock_credentials (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "BadgeCode" text,
    "CardHash" text,
    "PinHash" text,
    "FailedPinAttempts" integer NOT NULL DEFAULT 0,
    "LockedUntil" timestamptz,
    "UpdatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_timeshift_clock_credentials_TenantSlug_EmployeeId" ON timeshift_clock_credentials ("TenantSlug", "EmployeeId");
CREATE INDEX IF NOT EXISTS "IX_timeshift_clock_credentials_TenantSlug_BadgeCode" ON timeshift_clock_credentials ("TenantSlug", "BadgeCode");
CREATE INDEX IF NOT EXISTS "IX_timeshift_clock_credentials_TenantSlug_CardHash" ON timeshift_clock_credentials ("TenantSlug", "CardHash");

-- KVKK: koordinat sütunu bilinçli olarak yoktur; yalnızca "noktada mı" (OnSite) tutulur.
CREATE TABLE IF NOT EXISTS timeshift_clock_punches (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "SiteId" uuid,
    "Kind" text NOT NULL,
    "Method" text NOT NULL,
    "OnSite" boolean,
    "At" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_timeshift_clock_punches_EmployeeId_At" ON timeshift_clock_punches ("EmployeeId", "At");

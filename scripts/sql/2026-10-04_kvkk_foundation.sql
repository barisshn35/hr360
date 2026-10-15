-- KVKK temeli: yurt dışı aktarım kayıtları (m.9), imha tutanakları, otomatik analize itiraz (m.11/1-g).
CREATE TABLE IF NOT EXISTS governance_transfer_agreements (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Provider" text NOT NULL,
    "Mechanism" text NOT NULL,
    "SignedAt" date NOT NULL,
    "NotifiedAt" date,
    "Reference" text,
    "Notes" text,
    "UpdatedBy" text NOT NULL DEFAULT '',
    "UpdatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_governance_transfer_agreements" ON governance_transfer_agreements ("TenantSlug", "Provider");

CREATE TABLE IF NOT EXISTS governance_destruction_logs (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Category" text NOT NULL,
    "Action" text NOT NULL,
    "Affected" integer NOT NULL,
    "RetentionMonths" integer NOT NULL DEFAULT 0,
    "Trigger" text NOT NULL,
    "Actor" text NOT NULL DEFAULT '',
    "Method" text NOT NULL DEFAULT '',
    "RanAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_governance_destruction_logs_tenant_ran" ON governance_destruction_logs ("TenantSlug", "RanAt");

CREATE TABLE IF NOT EXISTS governance_analysis_objections (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "UserId" text NOT NULL,
    "PersonName" text NOT NULL,
    "Analysis" text NOT NULL,
    "Reason" text,
    "Status" text NOT NULL DEFAULT 'Open',
    "Response" text,
    "DecidedBy" text,
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "DecidedAt" timestamptz,
    "DueAt" timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_governance_analysis_objections_emp" ON governance_analysis_objections ("TenantSlug", "EmployeeId", "Analysis");

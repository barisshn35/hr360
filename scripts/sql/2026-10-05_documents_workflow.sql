-- Çalışan belge talebi (governance-service).
ALTER TABLE governance_doc_templates ADD COLUMN IF NOT EXISTS "SelfService" boolean NOT NULL DEFAULT false;
ALTER TABLE governance_doc_templates ADD COLUMN IF NOT EXISTS "RequiresApproval" boolean NOT NULL DEFAULT true;
UPDATE governance_doc_templates SET "SelfService" = true, "RequiresApproval" = false WHERE "Name" = 'Çalışma belgesi' AND "SelfService" = false;

CREATE TABLE IF NOT EXISTS governance_document_requests (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "TemplateId" uuid NOT NULL,
    "TemplateName" text NOT NULL,
    "Purpose" text,
    "Status" text NOT NULL DEFAULT 'Pending',
    "DecisionNote" text,
    "DecidedBy" text,
    "VerificationCode" text,
    "DocumentEnc" text,
    "DocumentHash" text,
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "IssuedAt" timestamptz
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_governance_document_requests_VerificationCode" ON governance_document_requests ("VerificationCode");
CREATE INDEX IF NOT EXISTS "IX_governance_document_requests_TenantSlug_EmployeeId" ON governance_document_requests ("TenantSlug", "EmployeeId");

-- Onay akışı: görsel akış tanımları, vekâlet, süre aşımında üst yöneticiye iletme, e-postadan tek tıkla karar.
ALTER TABLE workflow_approval_steps ADD COLUMN IF NOT EXISTS "SlaHours" integer;
ALTER TABLE workflow_approval_steps ADD COLUMN IF NOT EXISTS "DelegationId" uuid;
ALTER TABLE workflow_approval_steps ADD COLUMN IF NOT EXISTS "EscalatedAt" timestamptz;
ALTER TABLE workflow_approval_steps ADD COLUMN IF NOT EXISTS "ActionTokenHash" text;
ALTER TABLE workflow_approval_steps ADD COLUMN IF NOT EXISTS "ActionTokenExpiresAt" timestamptz;
CREATE INDEX IF NOT EXISTS "IX_workflow_approval_steps_DelegationId" ON workflow_approval_steps ("DelegationId") WHERE "DelegationId" IS NOT NULL;

CREATE TABLE IF NOT EXISTS workflow_definitions (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Type" text NOT NULL,
    "Name" text NOT NULL,
    "IsActive" boolean NOT NULL DEFAULT true,
    "StepsJson" text NOT NULL DEFAULT '[]',
    "HiddenFieldsJson" text NOT NULL DEFAULT '[]',
    "UpdatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_workflow_definitions_TenantSlug_Type" ON workflow_definitions ("TenantSlug", "Type");

CREATE TABLE IF NOT EXISTS workflow_delegations (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "FromEmployeeId" uuid NOT NULL,
    "ToEmployeeId" uuid NOT NULL,
    "StartDate" date NOT NULL,
    "EndDate" date NOT NULL,
    "Reason" text,
    "CreatedBy" text,
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "RevokedAt" timestamptz
);
CREATE INDEX IF NOT EXISTS "IX_workflow_delegations_TenantSlug_FromEmployeeId" ON workflow_delegations ("TenantSlug", "FromEmployeeId");

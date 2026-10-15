-- Yapay zeka (LLM) kiraci ayari ve kullanim kaydi. Idempotent.

CREATE TABLE IF NOT EXISTS governance_ai_settings (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Enabled" boolean NOT NULL DEFAULT false,
    "AllowPersonalData" boolean NOT NULL DEFAULT false,
    "UpdatedBy" text,
    "UpdatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_governance_ai_settings_tenant ON governance_ai_settings ("TenantSlug");

CREATE TABLE IF NOT EXISTS governance_ai_usage (
    "Id" bigserial PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "UserId" text,
    "Task" text NOT NULL,
    "Provider" text NOT NULL,
    "Model" text NOT NULL,
    "InputTokens" integer NOT NULL DEFAULT 0,
    "OutputTokens" integer NOT NULL DEFAULT 0,
    "DurationMs" integer NOT NULL DEFAULT 0,
    "Success" boolean NOT NULL,
    "At" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_governance_ai_usage_tenant_at ON governance_ai_usage ("TenantSlug", "At");

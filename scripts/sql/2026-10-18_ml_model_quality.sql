-- ML dalgası 1 (madde 37 + 39): devir riski modelinin kiracı başına inceleme eşiği ve
-- adillik denetimi raporları. Eşik İK'ca kalibrasyon tablosuna bakılarak seçilir; eşik üstü
-- skor bir KARAR değildir, "insan incelemesi önerilir" işaretidir. Adillik raporu yalnızca toplu
-- grup oranlarını tutar (5'ten küçük gruplar ML servisinde gizlenir; kişi/kimlik yok). İdempotent.
CREATE TABLE IF NOT EXISTS governance_ml_model_settings (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" varchar(64) NOT NULL,
    "ModelName" varchar(128) NOT NULL,
    "RiskThreshold" numeric(4,3) NOT NULL,
    "UpdatedBy" varchar(128),
    "UpdatedByName" varchar(256),
    "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "CK_governance_ml_model_settings_Threshold" CHECK ("RiskThreshold" >= 0.01 AND "RiskThreshold" <= 0.99)
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_governance_ml_model_settings_Tenant_Model"
    ON governance_ml_model_settings ("TenantSlug", "ModelName");

CREATE TABLE IF NOT EXISTS governance_ml_fairness_reports (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" varchar(64) NOT NULL,
    "ModelName" varchar(128) NOT NULL,
    "ModelVersion" varchar(32),
    "Threshold" numeric(4,3) NOT NULL,
    "Rows" integer NOT NULL,
    "SkippedRows" integer NOT NULL DEFAULT 0,
    "Report" jsonb NOT NULL,
    "CreatedBy" varchar(128),
    "CreatedByName" varchar(256),
    "CreatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_governance_ml_fairness_reports_Tenant_Created"
    ON governance_ml_fairness_reports ("TenantSlug", "CreatedAt" DESC);

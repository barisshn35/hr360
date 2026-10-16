-- Grup kartı (yenilik haritası #26): doğum günü, veda, hoş geldin vb. için herkesin imzaladığı kart.
-- Sahibi engagement-service. Kart, teslim anına kadar alıcıdan gizlidir; teslim zamanı gelince
-- (CardWorker) ya da açan kişi erken teslim edince alıcıya bildirim gider. İdempotent.
CREATE TABLE IF NOT EXISTS engagement_cards (
    "Id"                   uuid PRIMARY KEY,
    "TenantSlug"           character varying(64) NOT NULL,
    "RecipientEmployeeId"  uuid NOT NULL,
    "RecipientName"        text NOT NULL,
    -- Birthday | Farewell | Welcome | Anniversary | Congrats | GetWell | Other
    "Occasion"             character varying(16) NOT NULL,
    "Title"                character varying(120) NOT NULL,
    "DeliverAt"            timestamptz NOT NULL,
    "DeliveredAt"          timestamptz,
    "CreatedByEmployeeId"  uuid,
    "CreatedByUserId"      character varying(64) NOT NULL,
    "CreatedByName"        text,
    "CreatedAt"            timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_engagement_cards_TenantSlug" ON engagement_cards ("TenantSlug", "DeliveredAt");
CREATE INDEX IF NOT EXISTS "IX_engagement_cards_Due" ON engagement_cards ("DeliverAt") WHERE "DeliveredAt" IS NULL;

CREATE TABLE IF NOT EXISTS engagement_card_signatures (
    "Id"          uuid PRIMARY KEY,
    "TenantSlug"  character varying(64) NOT NULL,
    "CardId"      uuid NOT NULL REFERENCES engagement_cards("Id") ON DELETE CASCADE,
    "EmployeeId"  uuid NOT NULL,
    "Name"        text NOT NULL,
    "Message"     character varying(500) NOT NULL,
    "Color"       character varying(16) NOT NULL DEFAULT 'yellow',
    "CreatedAt"   timestamptz NOT NULL DEFAULT now(),
    "UpdatedAt"   timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_engagement_card_signatures_Card_Emp" ON engagement_card_signatures ("CardId", "EmployeeId");
CREATE INDEX IF NOT EXISTS "IX_engagement_card_signatures_TenantSlug" ON engagement_card_signatures ("TenantSlug");
DO $$ BEGIN
    IF to_regprocedure('hr360_rls_apply_policies()') IS NOT NULL THEN PERFORM hr360_rls_apply_policies(); END IF;
END $$;

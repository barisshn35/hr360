-- Rozet koleksiyonu ve kilometre taşları (yenilik haritası #29). Sahibi engagement-service.
-- Rozetler mevcut verilerden hesaplanır; burada yalnızca kazanma anı tutulur (bildirim bir kez gitsin,
-- koleksiyonda tarih görünsün). "_init" satırı kişinin ilk eşitlemesinin sessiz yapıldığını gösterir.
CREATE TABLE IF NOT EXISTS engagement_badge_awards (
    "Id"          uuid PRIMARY KEY,
    "TenantSlug"  character varying(64) NOT NULL,
    "EmployeeId"  uuid NOT NULL,
    "BadgeId"     character varying(32) NOT NULL,
    "AwardedAt"   timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_engagement_badge_awards" ON engagement_badge_awards ("TenantSlug", "EmployeeId", "BadgeId");
DO $$ BEGIN
    IF to_regprocedure('hr360_rls_apply_policies()') IS NOT NULL THEN PERFORM hr360_rls_apply_policies(); END IF;
END $$;

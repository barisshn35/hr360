-- Haftalık check-in (yenilik haritası #9). Sahibi engagement-service.
-- Çalışan haftada bir kısa not yazar (yaptıkları, engeller, gelecek hafta, ruh hâli 1-5); yöneticisi
-- (departman başı ya da ekip lideri) görür, engeli bir sonraki 1:1 gündemine ekleyebilir. Anonim değildir.
CREATE TABLE IF NOT EXISTS engagement_weekly_checkins (
    "Id"         uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "WeekStart"  date NOT NULL,
    "Progress"   character varying(1000),
    "Blockers"   character varying(1000),
    "NextWeek"   character varying(1000),
    "Mood"       smallint NOT NULL,
    "AddedToOneOnOne" boolean NOT NULL DEFAULT false,
    "CreatedAt"  timestamptz NOT NULL DEFAULT now(),
    "UpdatedAt"  timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_engagement_weekly_checkins_Emp_Week" ON engagement_weekly_checkins ("TenantSlug", "EmployeeId", "WeekStart");
CREATE INDEX IF NOT EXISTS "IX_engagement_weekly_checkins_TenantSlug" ON engagement_weekly_checkins ("TenantSlug");
DO $$ BEGIN
    IF to_regprocedure('hr360_rls_apply_policies()') IS NOT NULL THEN PERFORM hr360_rls_apply_policies(); END IF;
END $$;

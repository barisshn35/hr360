-- İlk 90 gün takibi (yenilik haritası #8). Sahibi engagement-service.
-- Yeni çalışana işe girişinin 30., 60. ve 90. gününde kısa uyum formu gönderilir; yöneticisi (departman
-- başı) hatırlatılır, yanıt yöneticiye ve İK'ya açıktır (form bunu çalışana söyler; anonim değildir).
-- Kilometre taşı son 7 gün içindeyse kayıt açılır: eski çalışanlara geriye dönük form gitmez. İdempotent.
CREATE TABLE IF NOT EXISTS engagement_newhire_checkins (
    "Id"                uuid PRIMARY KEY,
    "TenantSlug"        character varying(64) NOT NULL,
    "EmployeeId"        uuid NOT NULL,
    "Milestone"         integer NOT NULL,
    "DueOn"             date NOT NULL,
    "ManagerEmployeeId" uuid,
    "SentAt"            timestamptz NOT NULL DEFAULT now(),
    "AnsweredAt"        timestamptz,
    "Belonging"         smallint,
    "Clarity"           smallint,
    "Support"           smallint,
    "Comment"           character varying(1000)
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_engagement_newhire_checkins_Emp_Milestone" ON engagement_newhire_checkins ("TenantSlug", "EmployeeId", "Milestone");
CREATE INDEX IF NOT EXISTS "IX_engagement_newhire_checkins_TenantSlug" ON engagement_newhire_checkins ("TenantSlug");
DO $$ BEGIN
    IF to_regprocedure('hr360_rls_apply_policies()') IS NOT NULL THEN PERFORM hr360_rls_apply_policies(); END IF;
END $$;

-- Ürün içi geri bildirim ve ekran kullanım ölçümü (yenilik haritası #33). Sahibi engagement-service.
-- KVKK: kişi kimliği tutulmaz. Geri bildirim isimsizdir (yalnızca ekran, oy, isteğe bağlı yorum ve rol
-- grubu); kullanım ölçümü gün ve ekran şablonu başına sayaçtır (adres içindeki kimlikler istemcide
-- ":id" ile değiştirilir). İdempotent.
CREATE TABLE IF NOT EXISTS engagement_product_feedback (
    "Id"         uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Path"       character varying(200) NOT NULL,
    "Rating"     smallint NOT NULL,
    "Comment"    character varying(1000),
    "RoleGroup"  character varying(16) NOT NULL,
    "CreatedAt"  timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_engagement_product_feedback_TenantSlug" ON engagement_product_feedback ("TenantSlug", "CreatedAt");

CREATE TABLE IF NOT EXISTS engagement_page_views (
    "TenantSlug" character varying(64) NOT NULL,
    "Day"        date NOT NULL,
    "Path"       character varying(200) NOT NULL,
    "Views"      integer NOT NULL DEFAULT 0,
    PRIMARY KEY ("TenantSlug", "Day", "Path")
);

DO $$ BEGIN
    IF to_regprocedure('hr360_rls_apply_policies()') IS NOT NULL THEN PERFORM hr360_rls_apply_policies(); END IF;
END $$;

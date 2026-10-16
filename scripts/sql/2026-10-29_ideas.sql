-- Fikir kutusu (yenilik haritası #24). Sahibi engagement-service.
-- Çalışan fikir yazar (isteğe bağlı anonim), diğerleri destekler ve yorumlar, İK durum ve yanıt verir.
-- KVKK / anonimlik: anonim fikirde yazarın kimliği tutulmaz; yalnızca kendi fikrini tanıyabilmesi için
-- kiracı + kullanıcıdan türetilen tek yönlü anahtar ("AuthorKey") saklanır. Fikir satırları denetim
-- kaydına yazılmaz (yazar ve destekleyenler oradan çıkarılabilirdi). İdempotent.
-- Canlıya: docker exec -i hr360-postgres-1 psql -v ON_ERROR_STOP=1 -q -U hr360admin -d hr360_operational < bu_dosya

CREATE TABLE IF NOT EXISTS engagement_ideas (
    "Id"               uuid PRIMARY KEY,
    "TenantSlug"       character varying(64) NOT NULL,
    "AuthorKey"        character varying(64) NOT NULL,
    "AuthorEmployeeId" uuid,
    "AuthorName"       text,
    "Anonymous"        boolean NOT NULL DEFAULT false,
    "Title"            character varying(120) NOT NULL,
    "Body"             character varying(2000) NOT NULL,
    "Category"         character varying(40) NOT NULL,
    "Status"           character varying(16) NOT NULL DEFAULT 'New',
    "Response"         character varying(2000),
    "RespondedByName"  text,
    "RespondedAt"      timestamptz,
    "Voters"           text[] NOT NULL DEFAULT '{}',
    "CreatedAt"        timestamptz NOT NULL DEFAULT now(),
    "UpdatedAt"        timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_engagement_ideas_TenantSlug" ON engagement_ideas ("TenantSlug");

CREATE TABLE IF NOT EXISTS engagement_idea_comments (
    "Id"               uuid PRIMARY KEY,
    "TenantSlug"       character varying(64) NOT NULL,
    "IdeaId"           uuid NOT NULL REFERENCES engagement_ideas("Id") ON DELETE CASCADE,
    "AuthorUserId"     character varying(64) NOT NULL,
    "AuthorEmployeeId" uuid,
    "AuthorName"       text NOT NULL,
    "Body"             character varying(1000) NOT NULL,
    "CreatedAt"        timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_engagement_idea_comments_IdeaId" ON engagement_idea_comments ("IdeaId");
CREATE INDEX IF NOT EXISTS "IX_engagement_idea_comments_TenantSlug" ON engagement_idea_comments ("TenantSlug");

DO $$ BEGIN
    IF to_regprocedure('hr360_rls_apply_policies()') IS NOT NULL THEN PERFORM hr360_rls_apply_policies(); END IF;
END $$;

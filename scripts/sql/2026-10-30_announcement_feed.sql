-- Şirket içi akış (yenilik haritası #31): duyurulara tepki ve yorum. Sahibi governance-service.
-- Kişi başına tek tepki (değiştirilebilir/geri alınabilir). Yorumlar duyuruyu görebilenlere açıktır;
-- İK duyuru bazında yorumları kapatabilir ("AllowComments"). Duyuru silinince tepki/yorum da silinir.
ALTER TABLE governance_announcements ADD COLUMN IF NOT EXISTS "AllowComments" boolean NOT NULL DEFAULT true;

CREATE TABLE IF NOT EXISTS governance_announcement_reactions (
    "Id"              uuid PRIMARY KEY,
    "TenantSlug"      character varying(64) NOT NULL,
    "AnnouncementId"  uuid NOT NULL REFERENCES governance_announcements("Id") ON DELETE CASCADE,
    "UserId"          character varying(64) NOT NULL,
    "EmployeeId"      uuid,
    -- like | love | celebrate | clap | insightful | funny
    "Kind"            character varying(16) NOT NULL,
    "CreatedAt"       timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_governance_announcement_reactions_User" ON governance_announcement_reactions ("AnnouncementId", "UserId");
CREATE INDEX IF NOT EXISTS "IX_governance_announcement_reactions_TenantSlug" ON governance_announcement_reactions ("TenantSlug");

CREATE TABLE IF NOT EXISTS governance_announcement_comments (
    "Id"               uuid PRIMARY KEY,
    "TenantSlug"       character varying(64) NOT NULL,
    "AnnouncementId"   uuid NOT NULL REFERENCES governance_announcements("Id") ON DELETE CASCADE,
    "AuthorUserId"     character varying(64) NOT NULL,
    "AuthorEmployeeId" uuid,
    "AuthorName"       text NOT NULL,
    "Body"             character varying(1000) NOT NULL,
    "CreatedAt"        timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_governance_announcement_comments_Ann" ON governance_announcement_comments ("AnnouncementId", "CreatedAt");
CREATE INDEX IF NOT EXISTS "IX_governance_announcement_comments_TenantSlug" ON governance_announcement_comments ("TenantSlug");
DO $$ BEGIN
    IF to_regprocedure('hr360_rls_apply_policies()') IS NOT NULL THEN PERFORM hr360_rls_apply_policies(); END IF;
END $$;

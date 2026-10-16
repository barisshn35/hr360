-- Adayın mülakat saatini kendisi seçmesi (yenilik haritası #10). Sahibi recruitment-service.
-- İşe alım sorumlusu 2-10 uygun saat önerir; aday tek kullanımlık bağlantıyla birini seçer, mülakat o saate
-- kurulur. Bağlantının yalnızca SHA-256 özeti saklanır. İdempotent.
CREATE TABLE IF NOT EXISTS recruitment_interview_proposals (
    "Id"              uuid PRIMARY KEY,
    "TenantSlug"      character varying(64) NOT NULL,
    "ApplicationId"   uuid NOT NULL,
    "TokenHash"       character varying(64) NOT NULL,
    "Type"            text NOT NULL,
    "DurationMinutes" integer NOT NULL,
    "InterviewerIds"  uuid[] NOT NULL,
    "Location"        character varying(300),
    "MeetingUrl"      character varying(500),
    "Slots"           timestamptz[] NOT NULL,
    "Status"          character varying(16) NOT NULL DEFAULT 'Open',
    "ExpiresAt"       timestamptz NOT NULL,
    "ChosenAt"        timestamptz,
    "InterviewId"     uuid,
    "CreatedByName"   text,
    "CreatedAt"       timestamptz NOT NULL DEFAULT now()
);
-- Başvuru silinince (KVKK imhası dahil) öneri de silinir.
DELETE FROM recruitment_interview_proposals p WHERE NOT EXISTS (SELECT 1 FROM recruitment_applications a WHERE a."Id" = p."ApplicationId");
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_recruitment_interview_proposals_App') THEN
        ALTER TABLE recruitment_interview_proposals ADD CONSTRAINT "FK_recruitment_interview_proposals_App"
            FOREIGN KEY ("ApplicationId") REFERENCES recruitment_applications("Id") ON DELETE CASCADE;
    END IF;
END $$;
CREATE UNIQUE INDEX IF NOT EXISTS "UX_recruitment_interview_proposals_Token" ON recruitment_interview_proposals ("TokenHash");
CREATE INDEX IF NOT EXISTS "IX_recruitment_interview_proposals_App" ON recruitment_interview_proposals ("ApplicationId");
CREATE INDEX IF NOT EXISTS "IX_recruitment_interview_proposals_TenantSlug" ON recruitment_interview_proposals ("TenantSlug");
DO $$ BEGIN
    IF to_regprocedure('hr360_rls_apply_policies()') IS NOT NULL THEN PERFORM hr360_rls_apply_policies(); END IF;
END $$;

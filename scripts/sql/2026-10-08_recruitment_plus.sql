-- Dalga 5c — İşe alım+: kariyer sayfası ve aday öz-hizmeti (Y16), tekrar aday tespiti ve
-- kanban (G13), mülakat puan kartı ve planlama (Y17), teklif mektubu ve onayı (Y18).
-- İdempotent: tekrar çalıştırılabilir.

-- ------------------------------------------------------------------ adaylar
ALTER TABLE recruitment_candidates ADD COLUMN IF NOT EXISTS "NormalizedEmail" text;
ALTER TABLE recruitment_candidates ADD COLUMN IF NOT EXISTS "NormalizedPhone" text;
ALTER TABLE recruitment_candidates ADD COLUMN IF NOT EXISTS "Skills" text[] NOT NULL DEFAULT '{}';
ALTER TABLE recruitment_candidates ADD COLUMN IF NOT EXISTS "ResumeText" text;
-- KVKK m.5/1: aday havuzunda saklama yalnızca AÇIK RIZA ile (aydınlatmadan ayrı).
ALTER TABLE recruitment_candidates ADD COLUMN IF NOT EXISTS "TalentPoolConsent" boolean NOT NULL DEFAULT false;
ALTER TABLE recruitment_candidates ADD COLUMN IF NOT EXISTS "TalentPoolConsentAt" timestamptz;
ALTER TABLE recruitment_candidates ADD COLUMN IF NOT EXISTS "AnonymizedAt" timestamptz;

-- Mevcut kayıtların karşılaştırma anahtarları (uygulama ile aynı kural: küçük harf, +etiket atılır;
-- telefon yalnızca rakam, son 10 hane).
UPDATE recruitment_candidates
   SET "NormalizedEmail" = lower(regexp_replace(trim("Email"), '\+[^@]*@', '@'))
 WHERE "NormalizedEmail" IS NULL AND "Email" NOT LIKE 'anon-%';
UPDATE recruitment_candidates
   SET "NormalizedPhone" = right(regexp_replace("Phone", '\D', '', 'g'), 10)
 WHERE "NormalizedPhone" IS NULL AND "Phone" IS NOT NULL AND length(regexp_replace("Phone", '\D', '', 'g')) >= 7;

CREATE INDEX IF NOT EXISTS "IX_recruitment_candidates_norm_email" ON recruitment_candidates ("TenantSlug", "NormalizedEmail");
CREATE INDEX IF NOT EXISTS "IX_recruitment_candidates_norm_phone" ON recruitment_candidates ("TenantSlug", "NormalizedPhone");

-- ------------------------------------------------------------------ başvurular
ALTER TABLE recruitment_applications ADD COLUMN IF NOT EXISTS "Channel" text NOT NULL DEFAULT 'Manual';
ALTER TABLE recruitment_applications ADD COLUMN IF NOT EXISTS "CoverNote" text;
-- Öz-hizmet bağlantısının yalnızca SHA-256 özeti tutulur; bağlantının kendisi hiçbir yerde saklanmaz.
ALTER TABLE recruitment_applications ADD COLUMN IF NOT EXISTS "SelfServiceTokenHash" text;
ALTER TABLE recruitment_applications ADD COLUMN IF NOT EXISTS "OwnsCandidate" boolean NOT NULL DEFAULT false;
ALTER TABLE recruitment_applications ADD COLUMN IF NOT EXISTS "PrivacyNoticeVersion" text;
ALTER TABLE recruitment_applications ADD COLUMN IF NOT EXISTS "DuplicateReason" text;
CREATE UNIQUE INDEX IF NOT EXISTS "IX_recruitment_applications_token"
    ON recruitment_applications ("SelfServiceTokenHash") WHERE "SelfServiceTokenHash" IS NOT NULL;

-- ------------------------------------------------------------------ mülakatlar
ALTER TABLE recruitment_interviews ADD COLUMN IF NOT EXISTS "DurationMinutes" integer NOT NULL DEFAULT 60;
ALTER TABLE recruitment_interviews ADD COLUMN IF NOT EXISTS "Location" text;
ALTER TABLE recruitment_interviews ADD COLUMN IF NOT EXISTS "MeetingUrl" text;
ALTER TABLE recruitment_interviews ADD COLUMN IF NOT EXISTS "InterviewerIds" uuid[] NOT NULL DEFAULT '{}';
ALTER TABLE recruitment_interviews ADD COLUMN IF NOT EXISTS "CandidateNotifiedAt" timestamptz;
UPDATE recruitment_interviews SET "InterviewerIds" = ARRAY["InterviewerEmployeeId"]
 WHERE cardinality("InterviewerIds") = 0;
CREATE INDEX IF NOT EXISTS "IX_recruitment_interviews_tenant_at" ON recruitment_interviews ("TenantSlug", "ScheduledAt");

-- ------------------------------------------------------------------ puan kartları
CREATE TABLE IF NOT EXISTS recruitment_scorecard_templates (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "JobPostingId" uuid NOT NULL REFERENCES recruitment_job_postings ("Id") ON DELETE CASCADE,
    "CriteriaJson" text NOT NULL DEFAULT '[]',
    "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "PK_recruitment_scorecard_templates" PRIMARY KEY ("Id")
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_recruitment_scorecard_templates_posting" ON recruitment_scorecard_templates ("TenantSlug", "JobPostingId");

CREATE TABLE IF NOT EXISTS recruitment_scorecards (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "InterviewId" uuid NOT NULL REFERENCES recruitment_interviews ("Id") ON DELETE CASCADE,
    "InterviewerEmployeeId" uuid NOT NULL,
    "ScoresJson" text NOT NULL DEFAULT '[]',
    "OverallScore" numeric(4,2),
    "Recommendation" text,
    "Notes" text,
    "SubmittedAt" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "PK_recruitment_scorecards" PRIMARY KEY ("Id")
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_recruitment_scorecards_interview_person" ON recruitment_scorecards ("InterviewId", "InterviewerEmployeeId");
CREATE INDEX IF NOT EXISTS "IX_recruitment_scorecards_TenantSlug" ON recruitment_scorecards ("TenantSlug");

-- ------------------------------------------------------------------ teklifler
CREATE TABLE IF NOT EXISTS recruitment_offer_templates (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "Body" text NOT NULL,
    "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "PK_recruitment_offer_templates" PRIMARY KEY ("Id")
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_recruitment_offer_templates_tenant" ON recruitment_offer_templates ("TenantSlug");

CREATE TABLE IF NOT EXISTS recruitment_offers (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "ApplicationId" uuid NOT NULL REFERENCES recruitment_applications ("Id") ON DELETE CASCADE,
    "PositionTitle" text NOT NULL,
    "GrossSalary" numeric(14,2) NOT NULL,
    "Currency" text NOT NULL DEFAULT 'TRY',
    "StartDate" date NOT NULL,
    "Benefits" text,
    "ExpiresAt" date NOT NULL,
    "LetterText" text NOT NULL,
    "Status" text NOT NULL,
    "WorkflowRequestId" uuid,
    "ApproverEmployeeId" uuid,
    "DecidedByEmployeeId" uuid,
    "DecidedByUserId" text,
    "DecisionNote" text,
    "DecidedAt" timestamptz,
    "SentAt" timestamptz,
    "RespondedAt" timestamptz,
    "CreatedByUserId" text,
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "PK_recruitment_offers" PRIMARY KEY ("Id")
);
CREATE INDEX IF NOT EXISTS "IX_recruitment_offers_TenantSlug" ON recruitment_offers ("TenantSlug");
CREATE INDEX IF NOT EXISTS "IX_recruitment_offers_ApplicationId" ON recruitment_offers ("ApplicationId");
CREATE INDEX IF NOT EXISTS "IX_recruitment_offers_Workflow" ON recruitment_offers ("WorkflowRequestId") WHERE "WorkflowRequestId" IS NOT NULL;

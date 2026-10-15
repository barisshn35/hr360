-- Dalga 11 (işe alım): çalışan aday önerisi (73), aday durum bağlantısı (74), Google for Jobs
-- alanları (75), aşama geçmişi (78 — huni analizi). İdempotent.
-- Canlıya: docker exec -i hr360-postgres-1 psql -v ON_ERROR_STOP=1 -q -U hr360admin -d hr360_operational < bu_dosya

-- Kiracı başına işe alım program ayarları: öneri ödülü ve ilan yayın tercihleri.
CREATE TABLE IF NOT EXISTS recruitment_program_settings (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" varchar(64) NOT NULL,
    "ReferralEnabled" boolean NOT NULL DEFAULT true,
    "ReferralRewardAmount" numeric(14,2) NULL,
    "ReferralRewardCurrency" varchar(3) NOT NULL DEFAULT 'TRY',
    -- Ödül, işe girişten bu kadar gün sonra (deneme süresi; İş K. m.15 en çok 2 ay) hak edilir.
    "ReferralProbationDays" integer NOT NULL DEFAULT 60,
    "ReferralRewardNote" varchar(500) NULL,
    -- Google for Jobs (JobPosting JSON-LD) çıktısında ücret aralığı yalnızca kiracı açarsa yer alır.
    "PublishSalaryInJobPostings" boolean NOT NULL DEFAULT false,
    "UpdatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_recruitment_program_settings_tenant ON recruitment_program_settings ("TenantSlug");

-- İlanın herkese açık yapılandırılmış veri alanları (75).
ALTER TABLE recruitment_job_postings ADD COLUMN IF NOT EXISTS "Location" varchar(120) NULL;
ALTER TABLE recruitment_job_postings ADD COLUMN IF NOT EXISTS "Region" varchar(120) NULL;
ALTER TABLE recruitment_job_postings ADD COLUMN IF NOT EXISTS "Country" varchar(2) NULL;
ALTER TABLE recruitment_job_postings ADD COLUMN IF NOT EXISTS "RemoteAllowed" boolean NOT NULL DEFAULT false;
ALTER TABLE recruitment_job_postings ADD COLUMN IF NOT EXISTS "ValidThrough" timestamptz NULL;
ALTER TABLE recruitment_job_postings ADD COLUMN IF NOT EXISTS "SalaryMin" numeric(14,2) NULL;
ALTER TABLE recruitment_job_postings ADD COLUMN IF NOT EXISTS "SalaryMax" numeric(14,2) NULL;
ALTER TABLE recruitment_job_postings ADD COLUMN IF NOT EXISTS "SalaryCurrency" varchar(3) NULL;
ALTER TABLE recruitment_job_postings ADD COLUMN IF NOT EXISTS "SalaryPeriod" varchar(10) NULL;

-- Çalışan aday önerisi (73). Aday kişisel verisi burada TUTULMAZ (aday kaydına bağlanır); aday
-- silinince/anonimleşince bağlantı boşalır, öneri yalnızca öneren + ilan + ödül durumu olarak kalır.
CREATE TABLE IF NOT EXISTS recruitment_referrals (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" varchar(64) NOT NULL,
    "JobPostingId" uuid NOT NULL REFERENCES recruitment_job_postings("Id") ON DELETE CASCADE,
    "ReferrerEmployeeId" uuid NOT NULL,
    "CandidateId" uuid NULL REFERENCES recruitment_candidates("Id") ON DELETE SET NULL,
    "ApplicationId" uuid NULL REFERENCES recruitment_applications("Id") ON DELETE SET NULL,
    "Relationship" varchar(40) NULL,
    "Note" varchar(1000) NULL,
    -- Öneren, adayın önerilmeyi kabul ettiğini beyan eder (onay kutusu); aday ayrıca KVKK m.10 e-postası alır.
    "CandidateConsentConfirmed" boolean NOT NULL,
    "ConsentConfirmedAt" timestamptz NOT NULL,
    "NoticeSentAt" timestamptz NULL,
    -- None | Waiting | Eligible | Approved | Paid | Forfeited | NotEligible
    "RewardStatus" varchar(20) NOT NULL DEFAULT 'None',
    "HiredAt" timestamptz NULL,
    "RewardEligibleAt" timestamptz NULL,
    "RewardAmount" numeric(14,2) NULL,
    "RewardCurrency" varchar(3) NULL,
    "RewardDecidedAt" timestamptz NULL,
    "RewardDecidedByUserId" varchar(64) NULL,
    "RewardNote" varchar(500) NULL,
    "CreatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_recruitment_referrals_referrer ON recruitment_referrals ("TenantSlug", "ReferrerEmployeeId");
CREATE UNIQUE INDEX IF NOT EXISTS ux_recruitment_referrals_application ON recruitment_referrals ("ApplicationId") WHERE "ApplicationId" IS NOT NULL;

-- Aday durum bağlantısı (74): yalnızca kaba aşama + sonraki adım gösterir (kişisel veri yok).
-- Jetonun yalnızca SHA-256 özeti saklanır; İK ya da aday iptal edebilir; süresi dolar.
CREATE TABLE IF NOT EXISTS recruitment_status_links (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" varchar(64) NOT NULL,
    "ApplicationId" uuid NOT NULL REFERENCES recruitment_applications("Id") ON DELETE CASCADE,
    "TokenHash" varchar(64) NOT NULL,
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "ExpiresAt" timestamptz NOT NULL,
    "RevokedAt" timestamptz NULL,
    -- Hr | Candidate | Reissued
    "RevokedBy" varchar(20) NULL,
    "LastViewedAt" timestamptz NULL,
    "ViewCount" integer NOT NULL DEFAULT 0,
    "CreatedByUserId" varchar(64) NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_recruitment_status_links_hash ON recruitment_status_links ("TokenHash");
CREATE INDEX IF NOT EXISTS ix_recruitment_status_links_app ON recruitment_status_links ("ApplicationId");

-- Aşama geçmişi (78): aşamada geçen süre ve işe alım süresi için. Tetikleyiciyle doldurulur, böylece
-- durumu değiştiren her yol (kanban, teklif yanıtı, olay tüketicisi...) kaydedilir. Kişisel veri yok;
-- başvuru silinince birlikte silinir.
CREATE TABLE IF NOT EXISTS recruitment_application_stage_events (
    "Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "TenantSlug" varchar(64) NOT NULL,
    "ApplicationId" uuid NOT NULL REFERENCES recruitment_applications("Id") ON DELETE CASCADE,
    "FromStatus" varchar(20) NULL,
    "ToStatus" varchar(20) NOT NULL,
    "ChangedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_recruitment_stage_events_app ON recruitment_application_stage_events ("ApplicationId", "ChangedAt");
CREATE INDEX IF NOT EXISTS ix_recruitment_stage_events_tenant ON recruitment_application_stage_events ("TenantSlug", "ChangedAt");

-- SECURITY DEFINER: servis rolüne (deploy/postgres/roles.sql) bu tabloya yazma yetkisi gerekmeden çalışır.
CREATE OR REPLACE FUNCTION recruitment_stage_event() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        INSERT INTO recruitment_application_stage_events ("TenantSlug","ApplicationId","FromStatus","ToStatus","ChangedAt")
        VALUES (NEW."TenantSlug", NEW."Id", NULL, NEW."Status", coalesce(NEW."AppliedAt", now()));
    ELSIF NEW."Status" IS DISTINCT FROM OLD."Status" THEN
        INSERT INTO recruitment_application_stage_events ("TenantSlug","ApplicationId","FromStatus","ToStatus","ChangedAt")
        VALUES (NEW."TenantSlug", NEW."Id", OLD."Status", NEW."Status", now());
    END IF;
    RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS trg_recruitment_stage_event ON recruitment_applications;
CREATE TRIGGER trg_recruitment_stage_event AFTER INSERT OR UPDATE OF "Status" ON recruitment_applications
    FOR EACH ROW EXECUTE FUNCTION recruitment_stage_event();

-- Geçmişi olmayan mevcut başvurular için yaklaşık geçmiş: başvuru anı + son durum değişikliği.
INSERT INTO recruitment_application_stage_events ("TenantSlug","ApplicationId","FromStatus","ToStatus","ChangedAt")
SELECT a."TenantSlug", a."Id", NULL, 'Applied', a."AppliedAt"
  FROM recruitment_applications a
 WHERE NOT EXISTS (SELECT 1 FROM recruitment_application_stage_events e WHERE e."ApplicationId" = a."Id");
INSERT INTO recruitment_application_stage_events ("TenantSlug","ApplicationId","FromStatus","ToStatus","ChangedAt")
SELECT a."TenantSlug", a."Id", 'Applied', a."Status", coalesce(a."StatusChangedAt", a."AppliedAt")
  FROM recruitment_applications a
 WHERE a."Status" <> 'Applied'
   AND NOT EXISTS (SELECT 1 FROM recruitment_application_stage_events e WHERE e."ApplicationId" = a."Id" AND e."ToStatus" <> 'Applied');

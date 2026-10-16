-- "Ayın çalışanı" oylaması (isteğe bağlı; kiracı ayarıyla açılır, varsayılan kapalı). Sahibi engagement-service.
--
-- engagement_eom_settings  : kiracı başına tek satır (takvim, kapsam, süreç, görünürlük, katsayılar).
-- engagement_eom_periods   : oylama dönemi. Açıldığı andaki ayarlar "Config" içinde dondurulur: dönem
--                            sürerken ayar değişikliği (özellikle görünürlük) o dönemi etkilemez.
-- engagement_eom_units     : dönem içindeki oylama birimleri (departman grupları); üyeler açılışta sabitlenir.
--                            İki aşamalı kipte 2. aşama birimi, alt birimlerin kazananları arasından seçer.
-- engagement_eom_nominations / engagement_eom_votes : aday gösterme ve oylar.
--
-- KVKK / oy gizliliği: görünürlük "gizli" ise oy satırında kişi kimliği tutulmaz ("VoterEmployeeId" boş);
-- yalnızca dönem tuzuyla üretilmiş özet ("VoterKey", tekrar oy ve oy değiştirme için) saklanır. Dönem
-- kapanınca tuz silinir: sonrasında veritabanından bile kimin kime oy verdiği çıkarılamaz. Oylar denetim
-- kaydına (audit_log) yazılmaz. Kimliğin görünür olduğu kiplerde kural oy verirken kişiye gösterilir.
-- İdempotent; mevcut veriyi değiştirmez.
-- Canlıya: docker exec -i hr360-postgres-1 psql -v ON_ERROR_STOP=1 -q -U hr360admin -d hr360_operational < bu_dosya

CREATE TABLE IF NOT EXISTS engagement_eom_settings (
    "Id"                 uuid PRIMARY KEY,
    "TenantSlug"         character varying(64) NOT NULL,
    "Enabled"            boolean NOT NULL DEFAULT false,
    "Cadence"            character varying(16) NOT NULL DEFAULT 'Monthly',
    "Scope"              character varying(24) NOT NULL DEFAULT 'TopLevel',
    "Process"            character varying(16) NOT NULL DEFAULT 'Direct',
    "Visibility"         character varying(24) NOT NULL DEFAULT 'WinnerOnly',
    "RevealTo"           character varying(16) NOT NULL DEFAULT 'HrAndHead',
    "StartDay"           integer NOT NULL DEFAULT 1,
    "OpenHour"           integer NOT NULL DEFAULT 9,
    "NominationDays"     integer NOT NULL DEFAULT 2,
    "FinalistCount"      integer NOT NULL DEFAULT 5,
    "VotingDays"         integer NOT NULL DEFAULT 3,
    "Stage2Days"         integer NOT NULL DEFAULT 1,
    "ReminderDaysBefore" integer NOT NULL DEFAULT 2,
    "CooldownPeriods"    integer NOT NULL DEFAULT 3,
    "WeightEmployee"     numeric(4,2) NOT NULL DEFAULT 1,
    "WeightManager"      numeric(4,2) NOT NULL DEFAULT 2,
    "WeightHr"           numeric(4,2) NOT NULL DEFAULT 2,
    "AllowComment"       boolean NOT NULL DEFAULT true,
    "UpdatedAt"          timestamptz NOT NULL DEFAULT now(),
    "UpdatedByName"      text
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_engagement_eom_settings_TenantSlug" ON engagement_eom_settings ("TenantSlug");

CREATE TABLE IF NOT EXISTS engagement_eom_periods (
    "Id"                uuid PRIMARY KEY,
    "TenantSlug"        character varying(64) NOT NULL,
    "PeriodKey"         character varying(32) NOT NULL,
    "Status"            character varying(16) NOT NULL,
    "Config"            jsonb NOT NULL,
    "NominationStartsAt" timestamptz,
    "VotingStartsAt"    timestamptz NOT NULL,
    "Stage1EndsAt"      timestamptz NOT NULL,
    "Stage2EndsAt"      timestamptz,
    "Salt"              bytea,
    "UpcomingSentAt"    timestamptz,
    "OpenedSentAt"      timestamptz,
    "VotingSentAt"      timestamptz,
    "LastCallSentAt"    timestamptz,
    "Stage2SentAt"      timestamptz,
    "AnnouncedAt"       timestamptz,
    "CreatedAt"         timestamptz NOT NULL DEFAULT now(),
    "ClosedAt"          timestamptz
);
-- Aynı dönem iki kez açılmaz (işçi yarışına karşı); iptal edilen dönem yeniden başlatılabilir.
-- Elle başlatılan dönem anahtarı "M-yyyyMMddHHmmssfff" (19 karakter); ilk sürüm 16 karakterdi.
ALTER TABLE engagement_eom_periods ALTER COLUMN "PeriodKey" TYPE character varying(32);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_engagement_eom_periods_Key" ON engagement_eom_periods ("TenantSlug", "PeriodKey") WHERE "Status" <> 'Cancelled';
CREATE INDEX IF NOT EXISTS "IX_engagement_eom_periods_TenantSlug" ON engagement_eom_periods ("TenantSlug");

CREATE TABLE IF NOT EXISTS engagement_eom_units (
    "Id"            uuid PRIMARY KEY,
    "TenantSlug"    character varying(64) NOT NULL,
    "PeriodId"      uuid NOT NULL REFERENCES engagement_eom_periods("Id") ON DELETE CASCADE,
    "Stage"         integer NOT NULL,
    "DepartmentId"  uuid NOT NULL,
    "Name"          text NOT NULL,
    "ParentUnitId"  uuid,
    "HeadEmployeeId" uuid,
    "MemberIds"     uuid[] NOT NULL,
    "CandidateIds"  uuid[] NOT NULL,
    "Status"        character varying(16) NOT NULL,
    "WinnerEmployeeId" uuid,
    "WinnerScore"   numeric(10,2),
    "TiedIds"       uuid[],
    "ClosedAt"      timestamptz
);
CREATE INDEX IF NOT EXISTS "IX_engagement_eom_units_PeriodId" ON engagement_eom_units ("PeriodId");
CREATE INDEX IF NOT EXISTS "IX_engagement_eom_units_TenantSlug" ON engagement_eom_units ("TenantSlug");

CREATE TABLE IF NOT EXISTS engagement_eom_nominations (
    "Id"              uuid PRIMARY KEY,
    "TenantSlug"      character varying(64) NOT NULL,
    "UnitId"          uuid NOT NULL REFERENCES engagement_eom_units("Id") ON DELETE CASCADE,
    "NominatorKey"    character varying(64) NOT NULL,
    "NominatorEmployeeId" uuid,
    "NomineeEmployeeId" uuid NOT NULL,
    "CreatedAt"       timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_engagement_eom_nominations_Unit_Nominator" ON engagement_eom_nominations ("UnitId", "NominatorKey");
CREATE INDEX IF NOT EXISTS "IX_engagement_eom_nominations_TenantSlug" ON engagement_eom_nominations ("TenantSlug");

CREATE TABLE IF NOT EXISTS engagement_eom_votes (
    "Id"              uuid PRIMARY KEY,
    "TenantSlug"      character varying(64) NOT NULL,
    "UnitId"          uuid NOT NULL REFERENCES engagement_eom_units("Id") ON DELETE CASCADE,
    "VoterKey"        character varying(64) NOT NULL,
    "VoterEmployeeId" uuid,
    "NomineeEmployeeId" uuid NOT NULL,
    "Weight"          numeric(4,2) NOT NULL,
    "Comment"         character varying(280),
    "CreatedAt"       timestamptz NOT NULL DEFAULT now(),
    "UpdatedAt"       timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_engagement_eom_votes_Unit_Voter" ON engagement_eom_votes ("UnitId", "VoterKey");
CREATE INDEX IF NOT EXISTS "IX_engagement_eom_votes_TenantSlug" ON engagement_eom_votes ("TenantSlug");

DO $$ BEGIN
    IF to_regprocedure('hr360_rls_apply_policies()') IS NOT NULL THEN PERFORM hr360_rls_apply_policies(); END IF;
END $$;

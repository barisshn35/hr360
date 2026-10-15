-- Dalga 11 (madde 82–84): öğrenme ve gelişim.
--  82) yetkinlik açığı → eğitim önerisi: yeni tablo yok (mevcut learning_course_competencies + ML /skills/recommend)
--  83) kariyer yolları: rol basamakları (pozisyon unvanı) ve basamak başına beklenen yetkinlik seviyeleri
--  84) zorunlu eğitim son tarihi (learning_enrollments."DueOn") ve eğitim/İSG eğitimi hatırlatmaları
--      (30/7/0 gün; çalışana ve yöneticisine) — gönderilen hatırlatma tekrar gitmesin diye kayıt tablosu.
-- İdempotent; mevcut veriyi değiştirmez.
-- Canlıya: docker exec -i hr360-postgres-1 psql -v ON_ERROR_STOP=1 -q -U hr360admin -d hr360_operational < bu_dosya

-- ---------------------------------------------------------------- 84) son tarih + hatırlatma kaydı
ALTER TABLE learning_enrollments ADD COLUMN IF NOT EXISTS "DueOn" date NULL;
CREATE INDEX IF NOT EXISTS "IX_learning_enrollments_DueOn" ON learning_enrollments ("TenantSlug", "DueOn") WHERE "DueOn" IS NOT NULL;

-- SourceType: Training (learning_enrollments) | Osh (governance_osh_trainings, katılımcı başına).
-- DueOn anahtarda: son tarih değişirse (yenileme/erteleme) yeni hatırlatma döngüsü başlar.
CREATE TABLE IF NOT EXISTS learning_due_reminders (
    "Id"                  uuid PRIMARY KEY,
    "TenantSlug"          character varying(64) NOT NULL,
    "SourceType"          character varying(16) NOT NULL,
    "SourceId"            uuid NOT NULL,
    "SubjectEmployeeId"   uuid NOT NULL,
    "Kind"                character varying(16) NOT NULL,
    "RecipientEmployeeId" uuid NOT NULL,
    "DueOn"               date NOT NULL,
    "SentAt"              timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_learning_due_reminders_TenantSlug" ON learning_due_reminders ("TenantSlug");
CREATE UNIQUE INDEX IF NOT EXISTS "UX_learning_due_reminders" ON learning_due_reminders
    ("TenantSlug", "SourceType", "SourceId", "SubjectEmployeeId", "Kind", "RecipientEmployeeId", "DueOn");

-- ---------------------------------------------------------------- 83) kariyer yolları
CREATE TABLE IF NOT EXISTS learning_career_paths (
    "Id"          uuid PRIMARY KEY,
    "TenantSlug"  character varying(64) NOT NULL,
    "Name"        character varying(150) NOT NULL,
    "Description" character varying(1000) NULL,
    "IsActive"    boolean NOT NULL DEFAULT true,
    "CreatedAt"   timestamptz NOT NULL DEFAULT now(),
    "UpdatedAt"   timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_learning_career_paths_TenantSlug" ON learning_career_paths ("TenantSlug");

CREATE TABLE IF NOT EXISTS learning_career_steps (
    "Id"            uuid PRIMARY KEY,
    "TenantSlug"    character varying(64) NOT NULL,
    "PathId"        uuid NOT NULL REFERENCES learning_career_paths("Id") ON DELETE CASCADE,
    "StepOrder"     integer NOT NULL,
    "PositionTitle" character varying(150) NOT NULL,
    "Description"   character varying(1000) NULL,
    "MinMonths"     integer NULL
);
CREATE INDEX IF NOT EXISTS "IX_learning_career_steps_TenantSlug" ON learning_career_steps ("TenantSlug");
CREATE INDEX IF NOT EXISTS "IX_learning_career_steps_PathId" ON learning_career_steps ("PathId", "StepOrder");

CREATE TABLE IF NOT EXISTS learning_career_step_requirements (
    "Id"            uuid PRIMARY KEY,
    "TenantSlug"    character varying(64) NOT NULL,
    "StepId"        uuid NOT NULL REFERENCES learning_career_steps("Id") ON DELETE CASCADE,
    "CompetencyId"  uuid NOT NULL REFERENCES learning_competencies("Id") ON DELETE CASCADE,
    "RequiredLevel" integer NOT NULL CHECK ("RequiredLevel" BETWEEN 1 AND 5)
);
CREATE INDEX IF NOT EXISTS "IX_learning_career_step_requirements_TenantSlug" ON learning_career_step_requirements ("TenantSlug");
CREATE UNIQUE INDEX IF NOT EXISTS "UX_learning_career_step_requirements" ON learning_career_step_requirements ("StepId", "CompetencyId");

-- Yeni tablolara kiracı yalıtım politikası (RLS isteğe bağlı; açık değilse davranış değişmez).
DO $$ BEGIN
    IF to_regprocedure('hr360_rls_apply_policies()') IS NOT NULL THEN PERFORM hr360_rls_apply_policies(); END IF;
END $$;

-- Dalga 5c: yetkinlik matrisi ve eğitim önerisi (Y19), eğitim içeriği / sınav / SCORM 1.2 (Y20),
-- sertifika bitiş hatırlatmaları (G17), performans 9-kutu ve dönem şablonları (G12).
-- Idempotent: tekrar çalıştırılabilir.

/* ============================================================ Y19 yetkinlik matrisi */

CREATE TABLE IF NOT EXISTS learning_competencies (
    "Id"          uuid PRIMARY KEY,
    "TenantSlug"  character varying(64) NOT NULL,
    "Name"        character varying(150) NOT NULL,
    "Description" text NULL,
    "Category"    character varying(80) NULL,
    "IsActive"    boolean NOT NULL DEFAULT true,
    "CreatedAt"   timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_learning_competencies_TenantSlug" ON learning_competencies ("TenantSlug");
CREATE UNIQUE INDEX IF NOT EXISTS "UX_learning_competencies_name" ON learning_competencies ("TenantSlug", lower("Name"));

-- Rol profili: bir pozisyon unvanı YA DA departman için yetkinlik başına beklenen seviye (1-5).
CREATE TABLE IF NOT EXISTS learning_role_profiles (
    "Id"            uuid PRIMARY KEY,
    "TenantSlug"    character varying(64) NOT NULL,
    "CompetencyId"  uuid NOT NULL REFERENCES learning_competencies("Id") ON DELETE CASCADE,
    "PositionTitle" character varying(150) NULL,
    "DepartmentId"  uuid NULL,
    "RequiredLevel" integer NOT NULL CHECK ("RequiredLevel" BETWEEN 1 AND 5),
    "CreatedAt"     timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "CK_learning_role_profiles_target" CHECK (("PositionTitle" IS NULL) <> ("DepartmentId" IS NULL))
);
CREATE INDEX IF NOT EXISTS "IX_learning_role_profiles_TenantSlug" ON learning_role_profiles ("TenantSlug");
CREATE UNIQUE INDEX IF NOT EXISTS "UX_learning_role_profiles_target"
    ON learning_role_profiles ("TenantSlug", "CompetencyId", coalesce(lower("PositionTitle"), ''), coalesce("DepartmentId", '00000000-0000-0000-0000-000000000000'::uuid));

-- Değerlendirme geçmişi: öz / yönetici / İK; güncel seviye = en son kayıt.
CREATE TABLE IF NOT EXISTS learning_competency_assessments (
    "Id"                   uuid PRIMARY KEY,
    "TenantSlug"           character varying(64) NOT NULL,
    "EmployeeId"           uuid NOT NULL,
    "CompetencyId"         uuid NOT NULL REFERENCES learning_competencies("Id") ON DELETE CASCADE,
    "Level"                integer NOT NULL CHECK ("Level" BETWEEN 1 AND 5),
    "Source"               character varying(16) NOT NULL,
    "AssessedByEmployeeId" uuid NULL,
    "AssessedByName"       character varying(200) NULL,
    "Note"                 character varying(500) NULL,
    "AssessedAt"           timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_learning_competency_assessments_TenantSlug" ON learning_competency_assessments ("TenantSlug");
CREATE INDEX IF NOT EXISTS "IX_learning_competency_assessments_emp" ON learning_competency_assessments ("EmployeeId", "CompetencyId", "AssessedAt" DESC);

-- Eğitimin geliştirdiği yetkinlik ve hedef seviye.
CREATE TABLE IF NOT EXISTS learning_course_competencies (
    "Id"           uuid PRIMARY KEY,
    "TenantSlug"   character varying(64) NOT NULL,
    "CourseId"     uuid NOT NULL REFERENCES learning_courses("Id") ON DELETE CASCADE,
    "CompetencyId" uuid NOT NULL REFERENCES learning_competencies("Id") ON DELETE CASCADE,
    "TargetLevel"  integer NOT NULL CHECK ("TargetLevel" BETWEEN 1 AND 5)
);
CREATE INDEX IF NOT EXISTS "IX_learning_course_competencies_TenantSlug" ON learning_course_competencies ("TenantSlug");
CREATE UNIQUE INDEX IF NOT EXISTS "UX_learning_course_competencies" ON learning_course_competencies ("CourseId", "CompetencyId");

/* ============================================================ Y20 içerik, sınav, SCORM */

ALTER TABLE learning_courses ADD COLUMN IF NOT EXISTS "CertificateValidityMonths" integer NULL;

-- Sertifika kaydı: eğitim tamamlanınca otomatik üretilir (doğrulama kodu ile).
ALTER TABLE learning_certifications ADD COLUMN IF NOT EXISTS "CourseId" uuid NULL;
ALTER TABLE learning_certifications ADD COLUMN IF NOT EXISTS "EnrollmentId" uuid NULL;
ALTER TABLE learning_certifications ADD COLUMN IF NOT EXISTS "VerificationCode" character varying(32) NULL;
ALTER TABLE learning_certifications ADD COLUMN IF NOT EXISTS "IsMandatory" boolean NOT NULL DEFAULT false;
CREATE UNIQUE INDEX IF NOT EXISTS "UX_learning_certifications_code" ON learning_certifications ("VerificationCode") WHERE "VerificationCode" IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS "UX_learning_certifications_enrollment" ON learning_certifications ("EnrollmentId") WHERE "EnrollmentId" IS NOT NULL;

CREATE TABLE IF NOT EXISTS learning_scorm_packages (
    "Id"                 uuid PRIMARY KEY,
    "TenantSlug"         character varying(64) NOT NULL,
    "Title"              character varying(200) NOT NULL,
    "ManifestIdentifier" character varying(200) NULL,
    "EntryPoint"         character varying(500) NOT NULL,
    "FileCount"          integer NOT NULL,
    "TotalBytes"         bigint NOT NULL,
    "UploadedBy"         character varying(200) NULL,
    "CreatedAt"          timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_learning_scorm_packages_TenantSlug" ON learning_scorm_packages ("TenantSlug");

-- Paket dosyaları (MinIO/S3 olmadığı için Postgres bytea; paket + yol anahtarlı).
CREATE TABLE IF NOT EXISTS learning_scorm_files (
    "Id"          uuid PRIMARY KEY,
    "TenantSlug"  character varying(64) NOT NULL,
    "PackageId"   uuid NOT NULL REFERENCES learning_scorm_packages("Id") ON DELETE CASCADE,
    "Path"        character varying(500) NOT NULL,
    "ContentType" character varying(120) NOT NULL,
    "Size"        integer NOT NULL,
    "Content"     bytea NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_learning_scorm_files_TenantSlug" ON learning_scorm_files ("TenantSlug");
CREATE UNIQUE INDEX IF NOT EXISTS "UX_learning_scorm_files_path" ON learning_scorm_files ("PackageId", "Path");

CREATE TABLE IF NOT EXISTS learning_course_modules (
    "Id"              uuid PRIMARY KEY,
    "TenantSlug"      character varying(64) NOT NULL,
    "CourseId"        uuid NOT NULL REFERENCES learning_courses("Id") ON DELETE CASCADE,
    "Position"        integer NOT NULL DEFAULT 0,
    "Title"           character varying(200) NOT NULL,
    "Kind"            character varying(16) NOT NULL,
    "VideoUrl"        character varying(1000) NULL,
    "TextBody"        text NULL,
    "PassMarkPercent" integer NULL,
    "MaxAttempts"     integer NULL,
    "ScormPackageId"  uuid NULL REFERENCES learning_scorm_packages("Id") ON DELETE SET NULL,
    "CreatedAt"       timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_learning_course_modules_TenantSlug" ON learning_course_modules ("TenantSlug");
CREATE INDEX IF NOT EXISTS "IX_learning_course_modules_course" ON learning_course_modules ("CourseId", "Position");

-- Sınav soruları. Doğru seçenekler ("CorrectJson") istemciye hiçbir uçta gönderilmez (yalnızca İK cevap anahtarı).
CREATE TABLE IF NOT EXISTS learning_quiz_questions (
    "Id"          uuid PRIMARY KEY,
    "TenantSlug"  character varying(64) NOT NULL,
    "ModuleId"    uuid NOT NULL REFERENCES learning_course_modules("Id") ON DELETE CASCADE,
    "Position"    integer NOT NULL DEFAULT 0,
    "Text"        character varying(1000) NOT NULL,
    "Kind"        character varying(16) NOT NULL,
    "OptionsJson" jsonb NOT NULL,
    "CorrectJson" jsonb NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_learning_quiz_questions_TenantSlug" ON learning_quiz_questions ("TenantSlug");
CREATE INDEX IF NOT EXISTS "IX_learning_quiz_questions_module" ON learning_quiz_questions ("ModuleId", "Position");

CREATE TABLE IF NOT EXISTS learning_quiz_attempts (
    "Id"           uuid PRIMARY KEY,
    "TenantSlug"   character varying(64) NOT NULL,
    "ModuleId"     uuid NOT NULL REFERENCES learning_course_modules("Id") ON DELETE CASCADE,
    "EnrollmentId" uuid NOT NULL REFERENCES learning_enrollments("Id") ON DELETE CASCADE,
    "EmployeeId"   uuid NOT NULL,
    "AttemptNo"    integer NOT NULL,
    "AnswersJson"  jsonb NOT NULL,
    "ScorePercent" numeric(5,2) NOT NULL,
    "Passed"       boolean NOT NULL,
    "SubmittedAt"  timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_learning_quiz_attempts_TenantSlug" ON learning_quiz_attempts ("TenantSlug");
CREATE UNIQUE INDEX IF NOT EXISTS "UX_learning_quiz_attempts_no" ON learning_quiz_attempts ("EnrollmentId", "ModuleId", "AttemptNo");

CREATE TABLE IF NOT EXISTS learning_module_progress (
    "Id"           uuid PRIMARY KEY,
    "TenantSlug"   character varying(64) NOT NULL,
    "EnrollmentId" uuid NOT NULL REFERENCES learning_enrollments("Id") ON DELETE CASCADE,
    "ModuleId"     uuid NOT NULL REFERENCES learning_course_modules("Id") ON DELETE CASCADE,
    "EmployeeId"   uuid NOT NULL,
    "Status"       character varying(16) NOT NULL,
    "Score"        numeric(6,2) NULL,
    "CompletedAt"  timestamptz NULL,
    "UpdatedAt"    timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_learning_module_progress_TenantSlug" ON learning_module_progress ("TenantSlug");
CREATE UNIQUE INDEX IF NOT EXISTS "UX_learning_module_progress" ON learning_module_progress ("EnrollmentId", "ModuleId");

-- SCORM 1.2 çalışma zamanı değerleri (cmi.core.lesson_status, score.raw, suspend_data, lesson_location).
CREATE TABLE IF NOT EXISTS learning_scorm_runtime (
    "Id"             uuid PRIMARY KEY,
    "TenantSlug"     character varying(64) NOT NULL,
    "EnrollmentId"   uuid NOT NULL REFERENCES learning_enrollments("Id") ON DELETE CASCADE,
    "ModuleId"       uuid NOT NULL REFERENCES learning_course_modules("Id") ON DELETE CASCADE,
    "PackageId"      uuid NOT NULL,
    "EmployeeId"     uuid NOT NULL,
    "LessonStatus"   character varying(32) NOT NULL DEFAULT 'not attempted',
    "ScoreRaw"       numeric(6,2) NULL,
    "SuspendData"    text NULL,
    "LessonLocation" character varying(255) NULL,
    "SessionCount"   integer NOT NULL DEFAULT 0,
    "UpdatedAt"      timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_learning_scorm_runtime_TenantSlug" ON learning_scorm_runtime ("TenantSlug");
CREATE UNIQUE INDEX IF NOT EXISTS "UX_learning_scorm_runtime" ON learning_scorm_runtime ("EnrollmentId", "ModuleId");

/* ============================================================ G17 sertifika hatırlatmaları */

-- Gönderilmiş hatırlatmalar: aynı sertifika + tür + alıcı için ikinci bildirim gitmez.
CREATE TABLE IF NOT EXISTS learning_cert_reminders (
    "Id"                  uuid PRIMARY KEY,
    "TenantSlug"          character varying(64) NOT NULL,
    "CertificationId"     uuid NOT NULL REFERENCES learning_certifications("Id") ON DELETE CASCADE,
    "Kind"                character varying(16) NOT NULL,
    "RecipientEmployeeId" uuid NOT NULL,
    "ExpiresOn"           date NOT NULL,
    "SentAt"              timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_learning_cert_reminders_TenantSlug" ON learning_cert_reminders ("TenantSlug");
-- ExpiresOn anahtarda: sertifika yenilenip bitiş tarihi değişirse yeni hatırlatmalar yeniden gider.
CREATE UNIQUE INDEX IF NOT EXISTS "UX_learning_cert_reminders" ON learning_cert_reminders ("CertificationId", "Kind", "RecipientEmployeeId", "ExpiresOn");

/* ============================================================ G12 9-kutu, dönem şablonları */

ALTER TABLE performance_cycles ADD COLUMN IF NOT EXISTS "TemplateId" uuid NULL;
ALTER TABLE performance_cycles ADD COLUMN IF NOT EXISTS "ConfigJson" jsonb NULL;

CREATE TABLE IF NOT EXISTS performance_cycle_templates (
    "Id"           uuid PRIMARY KEY,
    "TenantSlug"   character varying(64) NOT NULL,
    "Name"         character varying(150) NOT NULL,
    "Description"  character varying(500) NULL,
    "Period"       character varying(16) NOT NULL,
    "DurationDays" integer NOT NULL,
    "ConfigJson"   jsonb NOT NULL,
    "CreatedBy"    character varying(200) NULL,
    "CreatedAt"    timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_performance_cycle_templates_TenantSlug" ON performance_cycle_templates ("TenantSlug");
CREATE UNIQUE INDEX IF NOT EXISTS "UX_performance_cycle_templates_name" ON performance_cycle_templates ("TenantSlug", lower("Name"));

-- Potansiyel (1-3): yöneticinin girdiği değerlendirme; çalışana varsayılan olarak GÖSTERİLMEZ.
CREATE TABLE IF NOT EXISTS performance_potential_ratings (
    "Id"                  uuid PRIMARY KEY,
    "TenantSlug"          character varying(64) NOT NULL,
    "CycleId"             uuid NOT NULL REFERENCES performance_cycles("Id") ON DELETE CASCADE,
    "EmployeeId"          uuid NOT NULL,
    "Rating"              integer NOT NULL CHECK ("Rating" BETWEEN 1 AND 3),
    "Note"                character varying(500) NULL,
    "RatedByEmployeeId"   uuid NULL,
    "RatedByName"         character varying(200) NULL,
    "PublishedToEmployee" boolean NOT NULL DEFAULT false,
    "UpdatedAt"           timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_performance_potential_ratings_TenantSlug" ON performance_potential_ratings ("TenantSlug");
CREATE UNIQUE INDEX IF NOT EXISTS "UX_performance_potential_ratings" ON performance_potential_ratings ("CycleId", "EmployeeId");

-- Kalibrasyon: İK'nın hücre düzeltmesi (gerekçeli, denetim kaydına yazılır; geçmiş korunur, en son geçerli).
CREATE TABLE IF NOT EXISTS performance_ninebox_overrides (
    "Id"               uuid PRIMARY KEY,
    "TenantSlug"       character varying(64) NOT NULL,
    "CycleId"          uuid NOT NULL REFERENCES performance_cycles("Id") ON DELETE CASCADE,
    "EmployeeId"       uuid NOT NULL,
    "PerformanceBand"  integer NOT NULL CHECK ("PerformanceBand" BETWEEN 1 AND 3),
    "PotentialBand"    integer NOT NULL CHECK ("PotentialBand" BETWEEN 1 AND 3),
    "Reason"           character varying(1000) NOT NULL,
    "OverriddenBy"     character varying(200) NOT NULL,
    "CreatedAt"        timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_performance_ninebox_overrides_TenantSlug" ON performance_ninebox_overrides ("TenantSlug");
CREATE INDEX IF NOT EXISTS "IX_performance_ninebox_overrides_emp" ON performance_ninebox_overrides ("CycleId", "EmployeeId", "CreatedAt" DESC);

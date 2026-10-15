-- Dalga 5c (governance-service): işyeri uyumu
--   Y14 duyurular + okudum onayı, G19 doküman kütüphanesi (sürüm + tam metin arama),
--   Y15 etik/ihbar hattı (anonim), Y6 iş sağlığı ve güvenliği (İSG), Y7 disiplin süreci.
-- Idempotent: tekrar çalıştırılabilir.

-- ---------------------------------------------------------------- Y14 duyurular
CREATE TABLE IF NOT EXISTS governance_announcements (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Title" text NOT NULL,
    "Body" text NOT NULL,
    -- All | Departments
    "Audience" text NOT NULL DEFAULT 'All',
    "DepartmentIds" uuid[] NOT NULL DEFAULT '{}',
    "PublishAt" timestamptz NOT NULL DEFAULT now(),
    "ExpireAt" timestamptz,
    "RequiresAck" boolean NOT NULL DEFAULT false,
    -- Yayım bildirimi bir kez gönderilir (ileri tarihli duyurularda yayım anında).
    "NotifiedAt" timestamptz,
    "CreatedBy" text NOT NULL DEFAULT '',
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "UpdatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_announcements_tenant" ON governance_announcements ("TenantSlug", "PublishAt" DESC);

-- ---------------------------------------------------------------- G19 doküman kütüphanesi
CREATE TABLE IF NOT EXISTS governance_library_documents (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Title" text NOT NULL,
    "Category" text NOT NULL DEFAULT 'Policy',
    -- All | Managers | Hr | Departments
    "Audience" text NOT NULL DEFAULT 'All',
    "DepartmentIds" uuid[] NOT NULL DEFAULT '{}',
    "RequiresAck" boolean NOT NULL DEFAULT false,
    "CurrentVersionId" uuid,
    "CurrentVersionNo" integer NOT NULL DEFAULT 0,
    "Archived" boolean NOT NULL DEFAULT false,
    "CreatedBy" text NOT NULL DEFAULT '',
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "UpdatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_library_documents_tenant" ON governance_library_documents ("TenantSlug", "Title");

CREATE TABLE IF NOT EXISTS governance_library_versions (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "DocumentId" uuid NOT NULL REFERENCES governance_library_documents("Id") ON DELETE CASCADE,
    "VersionNo" integer NOT NULL,
    "Title" text NOT NULL,
    "Body" text NOT NULL DEFAULT '',
    "ExternalUrl" text,
    "StorageKey" text,
    "ChangeNote" text,
    "PublishedBy" text NOT NULL DEFAULT '',
    "PublishedAt" timestamptz NOT NULL DEFAULT now(),
    -- Türkçe noktasız ı / noktalı İ farkı aramayı bozmasın: dizinde ve sorguda ı→i katlanır.
    "SearchVector" tsvector GENERATED ALWAYS AS (
        setweight(to_tsvector('simple'::regconfig, translate(lower(coalesce("Title", '')), 'ı', 'i')), 'A') ||
        setweight(to_tsvector('simple'::regconfig, translate(lower(coalesce("Body", '')), 'ı', 'i')), 'B')
    ) STORED
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_library_versions_no" ON governance_library_versions ("DocumentId", "VersionNo");
CREATE INDEX IF NOT EXISTS "IX_library_versions_tenant" ON governance_library_versions ("TenantSlug");
CREATE INDEX IF NOT EXISTS "IX_library_versions_search" ON governance_library_versions USING GIN ("SearchVector");

-- ---------------------------------------------------------------- Y14 + G19 okudum / kabul kayıtları
-- Bu bir RIZA kaydı DEĞİLDİR (governance_consents ayrı): yalnızca "okudum/kabul ettim" beyanı.
CREATE TABLE IF NOT EXISTS governance_acknowledgements (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    -- Announcement | LibraryDocument
    "SubjectType" text NOT NULL,
    "SubjectId" uuid NOT NULL,
    -- Doküman sürüm numarası (duyurularda 0). Yeni sürüm yeniden onay ister.
    "Version" integer NOT NULL DEFAULT 0,
    "UserId" text NOT NULL,
    "EmployeeId" uuid,
    "PersonName" text NOT NULL DEFAULT '',
    "AcknowledgedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_acknowledgements" ON governance_acknowledgements ("TenantSlug", "SubjectType", "SubjectId", "Version", "UserId");

-- ---------------------------------------------------------------- Y15 etik hattı
CREATE TABLE IF NOT EXISTS governance_ethics_committee (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "UserId" text NOT NULL,
    "EmployeeId" uuid,
    "Name" text NOT NULL DEFAULT '',
    "AddedBy" text NOT NULL DEFAULT '',
    "AddedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_ethics_committee_user" ON governance_ethics_committee ("TenantSlug", "UserId");

-- ANONİMLİK: IP, kullanıcı kimliği, tarayıcı bilgisi tutulmaz. Alındığı an gün
-- hassasiyetinde saklanır (saat bilgisi diğer kayıtlarla eşleştirmeye yaramasın).
-- Takip kodunun yalnızca SHA-256 özeti saklanır.
CREATE TABLE IF NOT EXISTS governance_ethics_reports (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Category" text NOT NULL,
    "Description" text NOT NULL,
    -- İhbarcı isterse bıraktığı iletişim bilgisi (şifreli).
    "ContactEnc" text,
    "CodeHash" text NOT NULL,
    -- Received | InReview | Closed
    "Status" text NOT NULL DEFAULT 'Received',
    "Outcome" text,
    "ReceivedOn" date NOT NULL DEFAULT current_date,
    "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
    "ClosedAt" timestamptz
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_ethics_reports_code" ON governance_ethics_reports ("CodeHash");
CREATE INDEX IF NOT EXISTS "IX_ethics_reports_tenant" ON governance_ethics_reports ("TenantSlug", "Status");

CREATE TABLE IF NOT EXISTS governance_ethics_messages (
    "Id" uuid PRIMARY KEY,
    "Seq" bigserial,
    "TenantSlug" character varying(64) NOT NULL,
    "ReportId" uuid NOT NULL REFERENCES governance_ethics_reports("Id") ON DELETE CASCADE,
    "FromReporter" boolean NOT NULL,
    -- Kurul üyesi yanıtında görünen ad ("Etik Kurulu"); ihbarcıda boş.
    "Author" text,
    "Body" text NOT NULL,
    -- İhbarcı mesajlarında gün hassasiyeti.
    "CreatedOn" date NOT NULL DEFAULT current_date
);
CREATE INDEX IF NOT EXISTS "IX_ethics_messages_report" ON governance_ethics_messages ("ReportId", "Seq");

-- ---------------------------------------------------------------- Y6 İSG
CREATE TABLE IF NOT EXISTS governance_osh_incidents (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    -- Accident | NearMiss
    "Kind" text NOT NULL,
    "OccurredOn" date NOT NULL,
    "OccurredTime" text,
    "Location" text NOT NULL DEFAULT '',
    "Description" text NOT NULL,
    "InjuredEmployeeId" uuid,
    "LostDays" integer NOT NULL DEFAULT 0,
    "RootCause" text,
    "CorrectiveActions" text,
    "SgkNotifiedOn" date,
    "SgkReference" text,
    -- Open | Closed
    "Status" text NOT NULL DEFAULT 'Open',
    "CreatedBy" text NOT NULL DEFAULT '',
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "UpdatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_osh_incidents_tenant" ON governance_osh_incidents ("TenantSlug", "OccurredOn" DESC);

-- Sağlık notları ÖZEL NİTELİKLİ veridir (KVKK m.6): şifreli, yalnızca işyeri hekimi okur.
CREATE TABLE IF NOT EXISTS governance_osh_exams (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    -- PreEmployment | Periodic | ReturnToWork | JobChange
    "ExamType" text NOT NULL DEFAULT 'Periodic',
    "ExamDate" date NOT NULL,
    "NextDueDate" date,
    -- Fit | Unfit | Conditional
    "Result" text NOT NULL,
    "NotesEnc" text,
    "RecordedBy" text NOT NULL DEFAULT '',
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "UpdatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_osh_exams_employee" ON governance_osh_exams ("TenantSlug", "EmployeeId", "ExamDate" DESC);

CREATE TABLE IF NOT EXISTS governance_osh_trainings (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Topic" text NOT NULL,
    "TrainingDate" date NOT NULL,
    "DurationHours" numeric(6,1) NOT NULL DEFAULT 0,
    "ValidityMonths" integer,
    "ExpiresOn" date,
    "Trainer" text,
    "ParticipantIds" uuid[] NOT NULL DEFAULT '{}',
    "CreatedBy" text NOT NULL DEFAULT '',
    "CreatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_osh_trainings_tenant" ON governance_osh_trainings ("TenantSlug", "TrainingDate" DESC);

-- ---------------------------------------------------------------- Y7 disiplin
-- Adli sicil / mahkûmiyet bilgisi TUTULMAZ (arayüz uyarısı + sunucu uyarısı).
CREATE TABLE IF NOT EXISTS governance_disciplinary_cases (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "IncidentDate" date NOT NULL,
    "Category" text NOT NULL,
    "Description" text NOT NULL,
    -- Open | DefenceRequested | DefenceReceived | Decided | Closed
    "Status" text NOT NULL DEFAULT 'Open',
    "DefenceNotice" text,
    "DefenceRequestedAt" timestamptz,
    "DefenceDeadline" date,
    "DefenceText" text,
    "DefenceSubmittedAt" timestamptz,
    "MinutesText" text,
    "Witnesses" text,
    -- Warning | WrittenWarning | NoAction | TerminationRecommendation
    "Decision" text,
    "DecisionNote" text,
    "DecidedBy" text,
    "DecidedAt" timestamptz,
    "ClosedAt" timestamptz,
    "CreatedBy" text NOT NULL DEFAULT '',
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "UpdatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_disciplinary_cases_employee" ON governance_disciplinary_cases ("TenantSlug", "EmployeeId");
CREATE INDEX IF NOT EXISTS "IX_disciplinary_cases_status" ON governance_disciplinary_cases ("TenantSlug", "Status");

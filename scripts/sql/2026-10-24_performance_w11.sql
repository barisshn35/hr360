-- Dalga 11 / performans ve gelişim: kalibrasyon oturumu (79), OKR hizalama ağacı (80),
-- anonim 360 derece geri bildirim (81). İdempotent: tekrar çalıştırılabilir.
-- Canlıya: docker exec -i hr360-postgres-1 psql -v ON_ERROR_STOP=1 -q -U hr360admin -d hr360_operational < bu_dosya

/* ============================================================ 79 kalibrasyon oturumu */

-- İK'nın bir dönem için yürüttüğü kalibrasyon toplantısı. Oturum sonuçlanınca (Finalized) değişen
-- hücreler performance_ninebox_overrides'a gerekçeyle yazılır. Otomatik karar yok: her nihai hücre
-- İK onayı (Confirmed) ister.
CREATE TABLE IF NOT EXISTS performance_calibration_sessions (
    "Id"              uuid PRIMARY KEY,
    "TenantSlug"      varchar(64) NOT NULL,
    "CycleId"         uuid NOT NULL REFERENCES performance_cycles("Id") ON DELETE CASCADE,
    "Name"            varchar(200) NOT NULL,
    "Status"          varchar(20) NOT NULL DEFAULT 'Open' CHECK ("Status" IN ('Open','Finalized','Cancelled')),
    "CreatedBy"       varchar(200) NOT NULL,
    "CreatedAt"       timestamptz NOT NULL DEFAULT now(),
    "FinalizedBy"     varchar(200),
    "FinalizedAt"     timestamptz
);
CREATE INDEX IF NOT EXISTS "IX_performance_calibration_sessions_TenantSlug" ON performance_calibration_sessions ("TenantSlug");
-- Bir dönemde aynı anda tek açık oturum.
CREATE UNIQUE INDEX IF NOT EXISTS "UX_performance_calibration_sessions_open" ON performance_calibration_sessions ("TenantSlug", "CycleId") WHERE "Status" = 'Open';

CREATE TABLE IF NOT EXISTS performance_calibration_items (
    "Id"                       uuid PRIMARY KEY,
    "TenantSlug"               varchar(64) NOT NULL,
    "SessionId"                uuid NOT NULL REFERENCES performance_calibration_sessions("Id") ON DELETE CASCADE,
    "EmployeeId"               uuid NOT NULL,
    "OriginalPerformanceBand"  integer NOT NULL CHECK ("OriginalPerformanceBand" BETWEEN 1 AND 3),
    "OriginalPotentialBand"    integer NOT NULL CHECK ("OriginalPotentialBand" BETWEEN 1 AND 3),
    "PerformanceBand"          integer NOT NULL CHECK ("PerformanceBand" BETWEEN 1 AND 3),
    "PotentialBand"            integer NOT NULL CHECK ("PotentialBand" BETWEEN 1 AND 3),
    "Score"                    numeric(6,2),
    "DecisionNote"             varchar(1000),
    "Confirmed"                boolean NOT NULL DEFAULT false,
    "ConfirmedBy"              varchar(200),
    "ConfirmedAt"              timestamptz,
    "UpdatedAt"                timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_performance_calibration_items_TenantSlug" ON performance_calibration_items ("TenantSlug");
CREATE UNIQUE INDEX IF NOT EXISTS "UX_performance_calibration_items_emp" ON performance_calibration_items ("SessionId", "EmployeeId");

-- Değişiklik günlüğü (yalnızca ekleme): her taşıma/onay kim, ne zaman, hangi hücreden hangisine.
CREATE TABLE IF NOT EXISTS performance_calibration_changes (
    "Id"          uuid PRIMARY KEY,
    "TenantSlug"  varchar(64) NOT NULL,
    "SessionId"   uuid NOT NULL REFERENCES performance_calibration_sessions("Id") ON DELETE CASCADE,
    "EmployeeId"  uuid NOT NULL,
    "Action"      varchar(20) NOT NULL CHECK ("Action" IN ('Moved','Confirmed','Unconfirmed','Finalized')),
    "FromCell"    integer,
    "ToCell"      integer,
    "Note"        varchar(1000),
    "ChangedBy"   varchar(200) NOT NULL,
    "ChangedAt"   timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_performance_calibration_changes_TenantSlug" ON performance_calibration_changes ("TenantSlug");
CREATE INDEX IF NOT EXISTS "IX_performance_calibration_changes_session" ON performance_calibration_changes ("SessionId", "ChangedAt" DESC);

/* ============================================================ 80 OKR hizalama ağacı */

-- Şirket ve departman amaçları (kişisel hedefler performance_goals'ta kalır).
CREATE TABLE IF NOT EXISTS performance_objectives (
    "Id"            uuid PRIMARY KEY,
    "TenantSlug"    varchar(64) NOT NULL,
    "CycleId"       uuid NOT NULL REFERENCES performance_cycles("Id") ON DELETE CASCADE,
    "Level"         varchar(20) NOT NULL CHECK ("Level" IN ('Company','Department')),
    "DepartmentId"  uuid,
    "ParentId"      uuid REFERENCES performance_objectives("Id") ON DELETE SET NULL,
    "Title"         varchar(200) NOT NULL,
    "Description"   varchar(2000),
    "Weight"        integer NOT NULL DEFAULT 100 CHECK ("Weight" BETWEEN 1 AND 100),
    "CreatedBy"     varchar(200),
    "CreatedAt"     timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "CK_performance_objectives_dept" CHECK (("Level" = 'Department') = ("DepartmentId" IS NOT NULL))
);
CREATE INDEX IF NOT EXISTS "IX_performance_objectives_TenantSlug" ON performance_objectives ("TenantSlug");
CREATE INDEX IF NOT EXISTS "IX_performance_objectives_cycle" ON performance_objectives ("CycleId");

-- Kişisel hedefin bağlı olduğu şirket/departman amacı (isteğe bağlı).
ALTER TABLE performance_goals ADD COLUMN IF NOT EXISTS "ParentObjectiveId" uuid;
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_performance_goals_objective') THEN
        ALTER TABLE performance_goals ADD CONSTRAINT "FK_performance_goals_objective"
            FOREIGN KEY ("ParentObjectiveId") REFERENCES performance_objectives("Id") ON DELETE SET NULL;
    END IF;
END $$;
CREATE INDEX IF NOT EXISTS "IX_performance_goals_parent_objective" ON performance_goals ("ParentObjectiveId") WHERE "ParentObjectiveId" IS NOT NULL;

/* ============================================================ 81 anonim 360 geri bildirim */

-- 360 talebi: değerlendirilen kişi, yetkinlikler, kapanış. Sonuçlar yalnızca KAPANMIŞ ve en az
-- "MinResponses" (≥5) yanıt almış talepte gösterilir.
CREATE TABLE IF NOT EXISTS performance_f360_requests (
    "Id"                 uuid PRIMARY KEY,
    "TenantSlug"         varchar(64) NOT NULL,
    "CycleId"            uuid REFERENCES performance_cycles("Id") ON DELETE SET NULL,
    "SubjectEmployeeId"  uuid NOT NULL,
    "Title"              varchar(200) NOT NULL,
    "CompetenciesJson"   jsonb NOT NULL DEFAULT '[]',
    "Status"             varchar(20) NOT NULL DEFAULT 'Open' CHECK ("Status" IN ('Open','Closed')),
    "DueDate"            date,
    "MinResponses"       integer NOT NULL DEFAULT 5 CHECK ("MinResponses" >= 5),
    "ReleasedToSubject"  boolean NOT NULL DEFAULT false,
    "CreatedByEmployeeId" uuid,
    "CreatedBy"          varchar(200) NOT NULL,
    "CreatedAt"          timestamptz NOT NULL DEFAULT now(),
    "ClosedAt"           timestamptz
);
CREATE INDEX IF NOT EXISTS "IX_performance_f360_requests_TenantSlug" ON performance_f360_requests ("TenantSlug");
CREATE INDEX IF NOT EXISTS "IX_performance_f360_requests_subject" ON performance_f360_requests ("SubjectEmployeeId");

-- Katılım: kim davet edildi, yanıtladı mı. Yanıtla İLİŞKİLENDİRİLMEZ (zaman damgası da tutulmaz).
CREATE TABLE IF NOT EXISTS performance_f360_participants (
    "Id"                  uuid PRIMARY KEY,
    "TenantSlug"          varchar(64) NOT NULL,
    "RequestId"           uuid NOT NULL REFERENCES performance_f360_requests("Id") ON DELETE CASCADE,
    "ReviewerEmployeeId"  uuid NOT NULL,
    "Relationship"        varchar(20) NOT NULL CHECK ("Relationship" IN ('Manager','Peer','DirectReport','Other')),
    "Submitted"           boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS "IX_performance_f360_participants_TenantSlug" ON performance_f360_participants ("TenantSlug");
CREATE UNIQUE INDEX IF NOT EXISTS "UX_performance_f360_participants" ON performance_f360_participants ("RequestId", "ReviewerEmployeeId");
CREATE INDEX IF NOT EXISTS "IX_performance_f360_participants_reviewer" ON performance_f360_participants ("ReviewerEmployeeId");

-- Yanıt: değerlendiren kimliği, ilişki türü ve zaman damgası YOK (anonimlik).
CREATE TABLE IF NOT EXISTS performance_f360_responses (
    "Id"           uuid PRIMARY KEY,
    "TenantSlug"   varchar(64) NOT NULL,
    "RequestId"    uuid NOT NULL REFERENCES performance_f360_requests("Id") ON DELETE CASCADE,
    "RatingsJson"  jsonb NOT NULL DEFAULT '{}',
    "Comment"      varchar(2000)
);
CREATE INDEX IF NOT EXISTS "IX_performance_f360_responses_TenantSlug" ON performance_f360_responses ("TenantSlug");
CREATE INDEX IF NOT EXISTS "IX_performance_f360_responses_request" ON performance_f360_responses ("RequestId");

-- Yeni tablolara kiracı yalıtım politikası (RLS isteğe bağlı; açık değilse davranış değişmez).
DO $$ BEGIN
    IF to_regprocedure('hr360_rls_apply_policies()') IS NOT NULL THEN PERFORM hr360_rls_apply_policies(); END IF;
END $$;

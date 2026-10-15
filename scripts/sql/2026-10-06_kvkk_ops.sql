-- Dalga 5a: KVKK operasyonları ve güvenlik
--   K2 aydınlatma metni sürümleri, K3 veri ihlali, K8 başvuru kimlik doğrulama,
--   K9 gizlilik etki değerlendirmesi, G21 değiştirilemez denetim kaydı (hash zinciri),
--   G20 alan düzeyinde yetki, G22 kiracı IP kısıtı.
-- Idempotent: tekrar çalıştırılabilir.

-- ---------------------------------------------------------------- K8 başvuru
ALTER TABLE governance_data_requests ADD COLUMN IF NOT EXISTS "Channel" text NOT NULL DEFAULT 'Panel';
ALTER TABLE governance_data_requests ADD COLUMN IF NOT EXISTS "Contact" text;
-- Mevcut (panelden gelmiş) başvurular oturumla doğrulanmış sayılır.
ALTER TABLE governance_data_requests ADD COLUMN IF NOT EXISTS "IdentityVerified" boolean NOT NULL DEFAULT true;
ALTER TABLE governance_data_requests ALTER COLUMN "IdentityVerified" SET DEFAULT false;
ALTER TABLE governance_data_requests ADD COLUMN IF NOT EXISTS "VerificationMethod" text;
ALTER TABLE governance_data_requests ADD COLUMN IF NOT EXISTS "VerifiedBy" text;
ALTER TABLE governance_data_requests ADD COLUMN IF NOT EXISTS "VerifiedAt" timestamptz;

-- ---------------------------------------------------------------- K2 aydınlatma metinleri
CREATE TABLE IF NOT EXISTS governance_privacy_notices (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Type" text NOT NULL,
    "Version" text NOT NULL,
    "Title" text NOT NULL,
    "Text" text NOT NULL,
    "ChangeNote" text,
    "PublishedBy" text NOT NULL DEFAULT '',
    "PublishedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_privacy_notices_version" ON governance_privacy_notices ("TenantSlug", "Type", "Version");

-- ---------------------------------------------------------------- K3 veri ihlali
CREATE TABLE IF NOT EXISTS governance_data_breaches (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Title" text NOT NULL,
    "Description" text NOT NULL,
    "DetectedAt" timestamptz NOT NULL,
    "OccurredAt" timestamptz,
    "DataCategories" text,
    "AffectedCount" integer,
    "AffectedEmployeesJson" text NOT NULL DEFAULT '[]',
    "Severity" text NOT NULL DEFAULT 'Medium',
    "Cause" text,
    "Measures" text,
    "Status" text NOT NULL DEFAULT 'Open',
    "ReportedToBoardAt" timestamptz,
    "BoardReference" text,
    "SubjectsNotifiedAt" timestamptz,
    "CreatedBy" text NOT NULL DEFAULT '',
    "CreatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_data_breaches_tenant" ON governance_data_breaches ("TenantSlug", "DetectedAt" DESC);

-- ---------------------------------------------------------------- K9 gizlilik etki değerlendirmesi
CREATE TABLE IF NOT EXISTS governance_privacy_assessments (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Subject" text NOT NULL,
    "Kind" text NOT NULL DEFAULT 'Integration',
    "ProviderKey" text,
    "AnswersJson" text NOT NULL DEFAULT '{}',
    "Risk" text NOT NULL DEFAULT 'Low',
    "Status" text NOT NULL DEFAULT 'Draft',
    "CreatedBy" text NOT NULL DEFAULT '',
    "ApprovedBy" text,
    "ApprovedAt" timestamptz,
    "UpdatedAt" timestamptz NOT NULL DEFAULT now()
);

-- ---------------------------------------------------------------- G20 alan düzeyinde yetki
-- Kiracı, profil alanlarının hangi rollere görüneceğini daraltabilir (genişletemez).
CREATE TABLE IF NOT EXISTS governance_field_policies (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Field" text NOT NULL,
    -- self | manager | hr  (alanı görebilecek en düşük düzey)
    "MinLevel" text NOT NULL DEFAULT 'hr',
    "SelfVisible" boolean NOT NULL DEFAULT true,
    "UpdatedBy" text NOT NULL DEFAULT '',
    "UpdatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_field_policies" ON governance_field_policies ("TenantSlug", "Field");

-- ---------------------------------------------------------------- G22 IP kısıtı
ALTER TABLE platform_tenants ADD COLUMN IF NOT EXISTS "IpAllowlist" text;

-- ---------------------------------------------------------------- G21 hash zinciri
-- Her kiracının denetim kaydı ayrı bir zincirdir: satırın özeti (SHA-256) önceki satırın
-- özetini içerir; geçmişteki bir satırın değiştirilmesi ya da araya satır silinmesi
-- doğrulamada görünür. UPDATE tümden engellenir; DELETE yalnızca saklama süresi dolan
-- en eski kayıtlar içindir (zincirin başı kısalır, ortası kopmaz).
ALTER TABLE audit_log ADD COLUMN IF NOT EXISTS "ChainSeq" bigint;
ALTER TABLE audit_log ADD COLUMN IF NOT EXISTS "PrevHash" text;
ALTER TABLE audit_log ADD COLUMN IF NOT EXISTS "Hash" text;
CREATE INDEX IF NOT EXISTS "IX_audit_log_chain" ON audit_log ((coalesce("TenantSlug", '')), "ChainSeq");

CREATE OR REPLACE FUNCTION audit_row_hash(prev text, seq bigint, tenant text, service text, entity_type text, entity_id text,
    action text, changes jsonb, user_id text, user_name text, correlation text, ip text, occurred timestamptz)
RETURNS text LANGUAGE sql IMMUTABLE AS $$
    SELECT encode(sha256(convert_to(
        coalesce(prev,'') || '|' || seq::text || '|' || coalesce(tenant,'') || '|' || coalesce(service,'') || '|' ||
        coalesce(entity_type,'') || '|' || coalesce(entity_id,'') || '|' || coalesce(action,'') || '|' ||
        coalesce(changes::text,'') || '|' || coalesce(user_id,'') || '|' || coalesce(user_name,'') || '|' ||
        coalesce(correlation,'') || '|' || coalesce(ip,'') || '|' ||
        to_char(occurred AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US'), 'UTF8')), 'hex')
$$;

-- Mevcut kayıtlar zincire bir kez dizilir (tetikleyiciler kurulmadan önce).
DO $$
DECLARE t text; r record; prev text; seq bigint;
BEGIN
    IF EXISTS (SELECT 1 FROM audit_log WHERE "Hash" IS NULL) THEN
        FOR t IN SELECT DISTINCT coalesce("TenantSlug", '') FROM audit_log WHERE "Hash" IS NULL LOOP
            SELECT "Hash", "ChainSeq" INTO prev, seq FROM audit_log
             WHERE coalesce("TenantSlug", '') = t AND "Hash" IS NOT NULL ORDER BY "ChainSeq" DESC LIMIT 1;
            prev := coalesce(prev, 'GENESIS'); seq := coalesce(seq, 0);
            FOR r IN SELECT * FROM audit_log WHERE coalesce("TenantSlug", '') = t AND "Hash" IS NULL ORDER BY "Id" LOOP
                seq := seq + 1;
                UPDATE audit_log SET "ChainSeq" = seq, "PrevHash" = prev,
                    "Hash" = audit_row_hash(prev, seq, r."TenantSlug", r."Service", r."EntityType", r."EntityId", r."Action",
                        r."Changes", r."UserId", r."UserName", r."CorrelationId", r."IpAddress", r."OccurredAt")
                 WHERE "Id" = r."Id"
                RETURNING "Hash" INTO prev;
            END LOOP;
        END LOOP;
    END IF;
END $$;

CREATE OR REPLACE FUNCTION audit_log_chain() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE prev text; seq bigint;
BEGIN
    -- Aynı kiracıya eşzamanlı yazımlar sıraya girer; sıra numarası kilit içinde verilir.
    PERFORM pg_advisory_xact_lock(hashtext('hr360-audit:' || coalesce(NEW."TenantSlug", '')));
    SELECT "Hash", "ChainSeq" INTO prev, seq FROM audit_log
     WHERE coalesce("TenantSlug", '') = coalesce(NEW."TenantSlug", '') AND "ChainSeq" IS NOT NULL
     ORDER BY "ChainSeq" DESC LIMIT 1;
    NEW."PrevHash" := coalesce(prev, 'GENESIS');
    NEW."ChainSeq" := coalesce(seq, 0) + 1;
    NEW."OccurredAt" := coalesce(NEW."OccurredAt", now());
    NEW."Hash" := audit_row_hash(NEW."PrevHash", NEW."ChainSeq", NEW."TenantSlug", NEW."Service", NEW."EntityType", NEW."EntityId",
        NEW."Action", NEW."Changes", NEW."UserId", NEW."UserName", NEW."CorrelationId", NEW."IpAddress", NEW."OccurredAt");
    RETURN NEW;
END $$;

CREATE OR REPLACE FUNCTION audit_log_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'audit_log satırları değiştirilemez (değiştirilemez denetim kaydı)' USING ERRCODE = 'insufficient_privilege';
END $$;

DROP TRIGGER IF EXISTS trg_audit_log_chain ON audit_log;
CREATE TRIGGER trg_audit_log_chain BEFORE INSERT ON audit_log FOR EACH ROW EXECUTE FUNCTION audit_log_chain();
DROP TRIGGER IF EXISTS trg_audit_log_immutable ON audit_log;
CREATE TRIGGER trg_audit_log_immutable BEFORE UPDATE ON audit_log FOR EACH ROW EXECUTE FUNCTION audit_log_immutable();

-- SIEM aktarımında kaldığı yer (kiracıdan bağımsız, tek satır).
CREATE TABLE IF NOT EXISTS governance_siem_cursor (
    "Id" integer PRIMARY KEY DEFAULT 1,
    "LastAuditId" bigint NOT NULL DEFAULT 0,
    "LastSentAt" timestamptz,
    "Sent" bigint NOT NULL DEFAULT 0,
    "LastError" text
);

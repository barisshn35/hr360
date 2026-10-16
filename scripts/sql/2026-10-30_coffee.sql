-- Kahve eşleştirme (yenilik haritası #27). Sahibi engagement-service. İsteğe bağlı (varsayılan kapalı):
-- isteyen çalışan katılır; seçilen günde (Türkiye saatiyle 09:00 sonrası) katılımcılar rastgele ikişerli
-- eşlenir (tek sayıda ise bir grup üç kişi), son eşleşmeler ve istenirse aynı departman kaçınılır.
-- Dönem başına tek tur (benzersiz dönem anahtarı). İdempotent.
CREATE TABLE IF NOT EXISTS engagement_coffee_settings (
    "Id"              uuid PRIMARY KEY,
    "TenantSlug"      character varying(64) NOT NULL,
    "Enabled"         boolean NOT NULL DEFAULT false,
    "Cadence"         character varying(16) NOT NULL DEFAULT 'Biweekly',
    "DayOfWeek"       integer NOT NULL DEFAULT 1,
    "CrossDepartment" boolean NOT NULL DEFAULT true,
    "UpdatedAt"       timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_engagement_coffee_settings_TenantSlug" ON engagement_coffee_settings ("TenantSlug");

CREATE TABLE IF NOT EXISTS engagement_coffee_participants (
    "Id"          uuid PRIMARY KEY,
    "TenantSlug"  character varying(64) NOT NULL,
    "EmployeeId"  uuid NOT NULL,
    "JoinedAt"    timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_engagement_coffee_participants_Emp" ON engagement_coffee_participants ("TenantSlug", "EmployeeId");

CREATE TABLE IF NOT EXISTS engagement_coffee_rounds (
    "Id"          uuid PRIMARY KEY,
    "TenantSlug"  character varying(64) NOT NULL,
    "PeriodKey"   character varying(16) NOT NULL,
    "CreatedAt"   timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_engagement_coffee_rounds_Period" ON engagement_coffee_rounds ("TenantSlug", "PeriodKey");

CREATE TABLE IF NOT EXISTS engagement_coffee_matches (
    "Id"          uuid PRIMARY KEY,
    "TenantSlug"  character varying(64) NOT NULL,
    "RoundId"     uuid NOT NULL REFERENCES engagement_coffee_rounds("Id") ON DELETE CASCADE,
    "MemberIds"   uuid[] NOT NULL,
    "Met"         boolean NOT NULL DEFAULT false,
    "CreatedAt"   timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_engagement_coffee_matches_TenantSlug" ON engagement_coffee_matches ("TenantSlug", "CreatedAt");
CREATE INDEX IF NOT EXISTS "IX_engagement_coffee_matches_Members" ON engagement_coffee_matches USING gin ("MemberIds");
DO $$ BEGIN
    IF to_regprocedure('hr360_rls_apply_policies()') IS NOT NULL THEN PERFORM hr360_rls_apply_policies(); END IF;
END $$;

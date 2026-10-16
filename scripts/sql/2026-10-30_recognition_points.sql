-- Takdir puanları ve ödül kataloğu (yenilik haritası #25). Sahibi engagement-service. İsteğe bağlı
-- (varsayılan kapalı): her çalışanın aylık verebileceği puan bütçesi vardır; takdirle birlikte puan
-- gönderilir, alınan puan katalogdaki ödüllerle takas edilir. Bakiye hareket defterinden hesaplanır
-- (engagement_points_ledger); takas anında puan düşülür, İK reddederse iade edilir. İdempotent.
ALTER TABLE engagement_kudos ADD COLUMN IF NOT EXISTS "Points" integer NOT NULL DEFAULT 0;

CREATE TABLE IF NOT EXISTS engagement_recognition_settings (
    "Id"               uuid PRIMARY KEY,
    "TenantSlug"       character varying(64) NOT NULL,
    "Enabled"          boolean NOT NULL DEFAULT false,
    "MonthlyAllowance" integer NOT NULL DEFAULT 100,
    "MaxPerKudos"      integer NOT NULL DEFAULT 50,
    "UpdatedAt"        timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_engagement_recognition_settings_TenantSlug" ON engagement_recognition_settings ("TenantSlug");

CREATE TABLE IF NOT EXISTS engagement_points_ledger (
    "Id"          uuid PRIMARY KEY,
    "TenantSlug"  character varying(64) NOT NULL,
    "EmployeeId"  uuid NOT NULL,
    "Delta"       integer NOT NULL,
    -- Kudos | KudosReversed | Redeem | Refund | Grant
    "Reason"      character varying(16) NOT NULL,
    "RefId"       uuid,
    "Note"        character varying(200),
    "CreatedAt"   timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_engagement_points_ledger_Emp" ON engagement_points_ledger ("TenantSlug", "EmployeeId");

CREATE TABLE IF NOT EXISTS engagement_rewards (
    "Id"          uuid PRIMARY KEY,
    "TenantSlug"  character varying(64) NOT NULL,
    "Title"       character varying(120) NOT NULL,
    "Description" character varying(1000),
    "Cost"        integer NOT NULL,
    "Stock"       integer,
    "Active"      boolean NOT NULL DEFAULT true,
    "CreatedAt"   timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_engagement_rewards_TenantSlug" ON engagement_rewards ("TenantSlug");

CREATE TABLE IF NOT EXISTS engagement_reward_redemptions (
    "Id"          uuid PRIMARY KEY,
    "TenantSlug"  character varying(64) NOT NULL,
    "RewardId"    uuid NOT NULL REFERENCES engagement_rewards("Id") ON DELETE RESTRICT,
    "RewardTitle" character varying(120) NOT NULL,
    "EmployeeId"  uuid NOT NULL,
    "Name"        text NOT NULL,
    "Cost"        integer NOT NULL,
    -- Pending | Fulfilled | Rejected
    "Status"      character varying(16) NOT NULL DEFAULT 'Pending',
    "Note"        character varying(500),
    "CreatedAt"   timestamptz NOT NULL DEFAULT now(),
    "DecidedAt"   timestamptz
);
CREATE INDEX IF NOT EXISTS "IX_engagement_reward_redemptions_TenantSlug" ON engagement_reward_redemptions ("TenantSlug", "Status");
DO $$ BEGIN
    IF to_regprocedure('hr360_rls_apply_policies()') IS NOT NULL THEN PERFORM hr360_rls_apply_policies(); END IF;
END $$;

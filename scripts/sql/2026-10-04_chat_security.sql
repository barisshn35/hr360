-- Sohbet botu güvenliği: hesap doğrulama (bağlama kodu), mesajlarda veri en aza indirme,
-- gönderilemeyen mesajlar için yeniden deneme kuyruğu.
ALTER TABLE governance_chat_identities ADD COLUMN IF NOT EXISTS "VerifiedAt" timestamptz;
ALTER TABLE governance_chat_identities ADD COLUMN IF NOT EXISTS "LinkCodeHash" text;
ALTER TABLE governance_chat_identities ADD COLUMN IF NOT EXISTS "LinkCodeExpiresAt" timestamptz;
ALTER TABLE governance_chat_apps ADD COLUMN IF NOT EXISTS "RequireVerifiedIdentity" boolean NOT NULL DEFAULT true;
ALTER TABLE governance_chat_apps ADD COLUMN IF NOT EXISTS "MessageDetail" text NOT NULL DEFAULT 'Minimal';

CREATE TABLE IF NOT EXISTS governance_chat_outbox (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "AppId" uuid NOT NULL,
    "Kind" text NOT NULL,
    "Payload" text NOT NULL,
    "Attempts" integer NOT NULL DEFAULT 0,
    "NextAttemptAt" timestamptz NOT NULL DEFAULT now(),
    "LastError" text,
    "CreatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_governance_chat_outbox_due" ON governance_chat_outbox ("NextAttemptAt");

-- Sabah özeti (uygulama ayarı) ve kişi başına günde bir gönderim takibi.
ALTER TABLE governance_chat_apps ADD COLUMN IF NOT EXISTS "DailyDigest" boolean NOT NULL DEFAULT true;
ALTER TABLE governance_chat_identities ADD COLUMN IF NOT EXISTS "LastDigestOn" date;

-- PWA anlık bildirim (Web Push) abonelikleri ve VAPID anahtarı (notification-service).
ALTER TABLE notification_messages ADD COLUMN IF NOT EXISTS "PushedAt" timestamptz;
CREATE INDEX IF NOT EXISTS "IX_notification_messages_push_pending" ON notification_messages ("CreatedAt") WHERE "Channel" = 'InApp' AND "PushedAt" IS NULL;

CREATE TABLE IF NOT EXISTS notification_push_subscriptions (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "Endpoint" text NOT NULL,
    "P256dh" text NOT NULL,
    "Auth" text NOT NULL,
    "Device" text,
    "FailureCount" integer NOT NULL DEFAULT 0,
    "LastSuccessAt" timestamptz,
    "CreatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_notification_push_subscriptions_Endpoint" ON notification_push_subscriptions ("Endpoint");
CREATE INDEX IF NOT EXISTS "IX_notification_push_subscriptions_TenantSlug_EmployeeId" ON notification_push_subscriptions ("TenantSlug", "EmployeeId");

CREATE TABLE IF NOT EXISTS notification_vapid_keys (
    "Id" integer PRIMARY KEY,
    "PublicKey" text NOT NULL,
    "PrivateKeyEnc" text NOT NULL
);
-- Eski bildirimler anlık bildirim olarak yeniden gönderilmesin.
UPDATE notification_messages SET "PushedAt" = "CreatedAt" WHERE "PushedAt" IS NULL AND "CreatedAt" < now() - interval '30 minutes';

-- E-postadaki eylem düğmesi (ör. tek kullanımlık karar sayfası).
ALTER TABLE notification_messages ADD COLUMN IF NOT EXISTS "ActionUrl" text;
ALTER TABLE notification_messages ADD COLUMN IF NOT EXISTS "ActionLabel" text;

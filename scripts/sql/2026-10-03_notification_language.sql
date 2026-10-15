-- Bildirim dili: çalışan tercihi ve her iletinin dili (e-posta çerçevesi de bu dilde).
ALTER TABLE notification_messages ADD COLUMN IF NOT EXISTS "Language" text NOT NULL DEFAULT 'tr';

CREATE TABLE IF NOT EXISTS notification_preferences (
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "Language" text NOT NULL DEFAULT 'tr',
    "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "PK_notification_preferences" PRIMARY KEY ("TenantSlug", "EmployeeId")
);

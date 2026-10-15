-- Dalga 5d / G11: bildirim tercihleri (kategori x kanal), sessiz saatler, günlük özet.
-- İdempotent; tekrar çalıştırılabilir.

-- Kişi başı genel ayarlar (dil satırı zaten vardı): sessiz saatler ve günlük özet.
ALTER TABLE notification_preferences ADD COLUMN IF NOT EXISTS "QuietHoursEnabled" boolean NOT NULL DEFAULT false;
ALTER TABLE notification_preferences ADD COLUMN IF NOT EXISTS "QuietStart" time without time zone NOT NULL DEFAULT '22:00';
ALTER TABLE notification_preferences ADD COLUMN IF NOT EXISTS "QuietEnd" time without time zone NOT NULL DEFAULT '08:00';
-- Bit maskesi: 1 << DayOfWeek (Pazar = 0). 127 = her gün. Gün, pencerenin BAŞLADIĞI gündür.
ALTER TABLE notification_preferences ADD COLUMN IF NOT EXISTS "QuietDays" integer NOT NULL DEFAULT 127;
ALTER TABLE notification_preferences ADD COLUMN IF NOT EXISTS "DigestEnabled" boolean NOT NULL DEFAULT false;
ALTER TABLE notification_preferences ADD COLUMN IF NOT EXISTS "DigestHour" integer NOT NULL DEFAULT 18;
ALTER TABLE notification_preferences ADD COLUMN IF NOT EXISTS "DigestLastSentAt" timestamp with time zone NULL;

-- Kategori x kanal tercihleri. Satır yoksa tüm kanallar açık (varsayılan).
CREATE TABLE IF NOT EXISTS notification_category_prefs (
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "Category" character varying(32) NOT NULL,
    "InApp" boolean NOT NULL DEFAULT true,
    "Email" boolean NOT NULL DEFAULT true,
    "Push" boolean NOT NULL DEFAULT true,
    "Chat" boolean NOT NULL DEFAULT true,
    "UpdatedAt" timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT "PK_notification_category_prefs" PRIMARY KEY ("TenantSlug", "EmployeeId", "Category")
);
CREATE INDEX IF NOT EXISTS "IX_notification_category_prefs_EmployeeId" ON notification_category_prefs ("EmployeeId");

-- E-posta / anlık bildirim ertelemesi (sessiz saatler): bu andan önce gönderilmez (düşürülmez).
ALTER TABLE notification_messages ADD COLUMN IF NOT EXISTS "DeferredUntil" timestamp with time zone NULL;
CREATE INDEX IF NOT EXISTS "IX_notification_messages_email_pending"
    ON notification_messages ("CreatedAt") WHERE "Channel" = 'Email' AND "Status" = 'Pending';
CREATE INDEX IF NOT EXISTS "IX_notification_messages_digest_queued"
    ON notification_messages ("RecipientEmployeeId") WHERE "Status" = 'DigestQueued';

-- SMS kanalı (notification-service): kiracının yurt içi SMS sağlayıcısı (Netgsm / İleti Merkezi; Ayarlar › SMS),
-- bekleyen SMS'in alıcı numarası (gönderim sonuçlanınca silinir) ve kişinin SMS tercihi. İdempotent.
CREATE TABLE IF NOT EXISTS notification_sms_settings (
    "TenantSlug" character varying(64) PRIMARY KEY,
    "Enabled"    boolean NOT NULL DEFAULT false,
    "Provider"   text NOT NULL DEFAULT 'netgsm',
    "Username"   text NOT NULL DEFAULT '',
    -- KeyRing ile şifreli (KeyRotationJob yeniler); yanıtta dönmez.
    "SecretEnc"  text NOT NULL DEFAULT '',
    "Sender"     text NOT NULL DEFAULT '',
    "DailyLimit" integer NOT NULL DEFAULT 200,
    "UpdatedAt"  timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_notification_sms_settings_TenantSlug" ON notification_sms_settings ("TenantSlug");
ALTER TABLE notification_messages ADD COLUMN IF NOT EXISTS "RecipientPhone" text NULL;
ALTER TABLE notification_preferences ADD COLUMN IF NOT EXISTS "SmsEnabled" boolean NOT NULL DEFAULT true;
DO $$ BEGIN
    IF to_regprocedure('hr360_rls_apply_policies()') IS NOT NULL THEN PERFORM hr360_rls_apply_policies(); END IF;
END $$;

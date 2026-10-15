-- Dalga 9 (madde 44, 66–72): izin, puantaj ve vardiya.
--  66) çalışma süresi kuralları (11 saat dinlenme, haftalık 45 / günlük 11 saat, gece 7,5 saat, ardışık gün) kiracı başına
--  67) konum doğrulamalı giriş-çıkış: yalnızca "noktada mı + uzaklık aralığı"; ham koordinat yalnızca şirket açarsa, süreli
--  68) QR kiosk: (yeni tablo yok; kiosk terminal anahtarıyla çalışır)
--  69) ekip izin çakışması eşiği, 70) saatlik izinde günlük çalışma saati, 71) devir üst sınırı: leave_settings
--  70) kısmi gün izni vardiya takviminde (timeshift_shift_overrides."IsPartial")
--  72) yarım gün resmî tatil (arife, 28 Ekim) ve puantaj dönemi kilidi
-- İdempotent; mevcut veriyi değiştirmez (yeni sütunlar varsayılanla gelir, davranış eskisiyle aynı kalır).
-- Canlıya: docker exec -i hr360-postgres-1 psql -v ON_ERROR_STOP=1 -q -U hr360admin -d hr360_operational < bu_dosya

-- ---------------------------------------------------------------- 66) çalışma süresi kuralları
-- Varsayılanlar 4857 s. İş Kanunu m.63/m.69 ve Çalışma Süreleri Yönetmeliği değerleridir; sektörel istisnalar
-- (ör. sağlık, güvenlik) için şirket değiştirebilir. Takas onayında kural ihlali engeldir; atamada uyarıdır.
ALTER TABLE timeshift_settings ADD COLUMN IF NOT EXISTS "MinRestHours" numeric NOT NULL DEFAULT 11;
ALTER TABLE timeshift_settings ADD COLUMN IF NOT EXISTS "WeeklyMaxHours" numeric NOT NULL DEFAULT 45;
ALTER TABLE timeshift_settings ADD COLUMN IF NOT EXISTS "DailyMaxHours" numeric NOT NULL DEFAULT 11;
ALTER TABLE timeshift_settings ADD COLUMN IF NOT EXISTS "NightMaxHours" numeric NOT NULL DEFAULT 7.5;
ALTER TABLE timeshift_settings ADD COLUMN IF NOT EXISTS "MaxConsecutiveDays" integer NOT NULL DEFAULT 6;

-- ---------------------------------------------------------------- 67) konum doğrulama
-- "GeoOutsidePolicy": Block = nokta dışından giriş reddedilir (önceki davranış), Flag = kaydedilir, "noktada değil" işaretlenir.
-- "GeoStoreRaw": ham koordinat saklama (varsayılan kapalı); açıksa "GeoRawRetentionDays" gün sonra silinir.
ALTER TABLE timeshift_settings ADD COLUMN IF NOT EXISTS "GeoOutsidePolicy" character varying(16) NOT NULL DEFAULT 'Block';
ALTER TABLE timeshift_settings ADD COLUMN IF NOT EXISTS "GeoStoreRaw" boolean NOT NULL DEFAULT false;
ALTER TABLE timeshift_settings ADD COLUMN IF NOT EXISTS "GeoRawRetentionDays" integer NOT NULL DEFAULT 30;
ALTER TABLE timeshift_clock_punches ADD COLUMN IF NOT EXISTS "DistanceBucket" character varying(16);
ALTER TABLE timeshift_clock_punches ADD COLUMN IF NOT EXISTS "RawLatitude" double precision;
ALTER TABLE timeshift_clock_punches ADD COLUMN IF NOT EXISTS "RawLongitude" double precision;
ALTER TABLE timeshift_clock_punches ADD COLUMN IF NOT EXISTS "RawExpiresAt" timestamptz;
CREATE INDEX IF NOT EXISTS "IX_timeshift_clock_punches_RawExpiresAt" ON timeshift_clock_punches ("RawExpiresAt") WHERE "RawExpiresAt" IS NOT NULL;

-- ---------------------------------------------------------------- 70) kısmi gün izni takvimde
ALTER TABLE timeshift_shift_overrides ADD COLUMN IF NOT EXISTS "IsPartial" boolean NOT NULL DEFAULT false;

-- ---------------------------------------------------------------- 72) puantaj dönemi kilidi
-- Kapalı dönemde giriş-çıkış, puantaj düzeltmesi ve fazla mesai talebi/kararı yapılamaz; bordro bu dönemin
-- onaylı fazla mesaisini okur. Yeniden açma gerekçeyle ve denetim kaydıyla.
CREATE TABLE IF NOT EXISTS timeshift_timesheet_periods (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Year" integer NOT NULL,
    "Month" integer NOT NULL,
    "Status" character varying(16) NOT NULL DEFAULT 'Open',
    "ClosedAt" timestamptz,
    "ClosedBy" text,
    "ReopenedAt" timestamptz,
    "ReopenedBy" text,
    "ReopenReason" text,
    "CreatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_timeshift_timesheet_periods_month" ON timeshift_timesheet_periods ("TenantSlug", "Year", "Month");

-- ---------------------------------------------------------------- 72) yarım gün resmî tatil
-- Arife günleri ve 28 Ekim öğleden sonra tatildir: izin gün hesabında 0,5 gün sayılır.
ALTER TABLE leave_public_holidays ADD COLUMN IF NOT EXISTS "IsHalfDay" boolean NOT NULL DEFAULT false;

-- ---------------------------------------------------------------- 69–71) izin ayarları (kiracı başına tek satır)
CREATE TABLE IF NOT EXISTS leave_settings (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    -- Saatlik izinde gün = saat / günlük çalışma saati (boşsa LEAVE_DAY_HOURS, o da yoksa 7,5).
    "DayHours" numeric,
    -- Ekip çakışma uyarısı: aynı departmanda aynı günlerde izinli/izin bekleyen oranı bu yüzdeyi aşarsa.
    "ConflictWarnEnabled" boolean NOT NULL DEFAULT true,
    "ConflictThresholdPercent" integer NOT NULL DEFAULT 30,
    -- Yıllık izin devrinde varsayılan üst sınır (boş = sınırsız; yasal olarak yıllık izin yanmaz).
    "CarryOverMaxDays" numeric,
    "UpdatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_leave_settings_TenantSlug" ON leave_settings ("TenantSlug");

-- Yeni tablolara kiracı yalıtım politikası (RLS isteğe bağlı; açık değilse davranış değişmez).
DO $$ BEGIN
    IF to_regprocedure('hr360_rls_apply_policies()') IS NOT NULL THEN PERFORM hr360_rls_apply_policies(); END IF;
END $$;

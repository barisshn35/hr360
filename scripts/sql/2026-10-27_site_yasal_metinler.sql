-- Herkese açık site bilgileri (Platform › Sistem ayarları › Yasal metinler ve iletişim): işletmeci
-- ve iletişim bilgileri, gizlilik politikası, kullanım koşulları, SSS, arama motoru görünürlüğü.
-- Boş alan = arayüzdeki varsayılan şablon. SearchIndexing NULL/false = arama motorlarına kapalı
-- (robots.txt "Disallow: /", sayfalarda noindex).
ALTER TABLE platform_settings ADD COLUMN IF NOT EXISTS "SiteOperatorName" varchar(200) NULL;
ALTER TABLE platform_settings ADD COLUMN IF NOT EXISTS "SiteAddress" varchar(500) NULL;
ALTER TABLE platform_settings ADD COLUMN IF NOT EXISTS "SiteEmail" varchar(320) NULL;
ALTER TABLE platform_settings ADD COLUMN IF NOT EXISTS "SitePhone" varchar(40) NULL;
ALTER TABLE platform_settings ADD COLUMN IF NOT EXISTS "SiteKep" varchar(320) NULL;
ALTER TABLE platform_settings ADD COLUMN IF NOT EXISTS "SiteVerbisNo" varchar(64) NULL;
ALTER TABLE platform_settings ADD COLUMN IF NOT EXISTS "PrivacyPolicyMd" text NULL;
ALTER TABLE platform_settings ADD COLUMN IF NOT EXISTS "TermsMd" text NULL;
ALTER TABLE platform_settings ADD COLUMN IF NOT EXISTS "FaqJson" text NULL;
ALTER TABLE platform_settings ADD COLUMN IF NOT EXISTS "SearchIndexing" boolean NULL;

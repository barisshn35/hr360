-- CTO denetimi (2026-09-25) - mevcut veritabanlari icin sema degisiklikleri.
-- Yeni kurulumlar data/migrations/sql-all-schemas.sql'den ayni semayi alir.
-- Idempotent: tekrar calistirilabilir. install.sh (mevcut .env ile guncelleme yolu)
-- scripts/sql/*.sql dosyalarini sirayla ve ON_ERROR_STOP ile otomatik uygular.
--
-- Elle calistirma: psql -v ON_ERROR_STOP=1 -d hr360_operational -f scripts/sql/2026-09-25_cto_audit_hardening.sql
--
-- NOT: Kolon ekleme, indekslerden AYRI bir adimdir - bir indeks mevcut veride
-- cakisma nedeniyle olusturulamasa bile servislerin ihtiyac duydugu kolon eklenir
-- (aksi halde masraf servisi "kolon yok" hatasiyla tum beyan sorgularinda 500 verir).
-- Benzersiz indeksler, cakisan veri varsa ATLANIR ve bir UYARI yazdirilir; veriyi
-- temizleyip betigi yeniden calistirin.

-- 1) Masraf beyani: gorev ayriligi icin onaylayan kisi (onaylayan "odendi" isaretleyemez).
ALTER TABLE expense_claims ADD COLUMN IF NOT EXISTS "ApprovedByEmployeeId" uuid;

-- 1b) Kiraci askiya alma: bu islemle kapatilan hesaplar (yeniden etkinlestirmede yalnizca bunlar acilir).
ALTER TABLE platform_tenants ADD COLUMN IF NOT EXISTS "SuspendedUserIdsJson" text;

-- Resmi tatil takvimi (leave-service): izin gunu hesabinda dusulur.
CREATE TABLE IF NOT EXISTS leave_public_holidays (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "Date" date NOT NULL,
    "Name" text NOT NULL,
    "CreatedAt" timestamptz NOT NULL,
    CONSTRAINT "PK_leave_public_holidays" PRIMARY KEY ("Id")
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_leave_public_holidays_TenantSlug_Date"
    ON leave_public_holidays ("TenantSlug", "Date");

-- 2) Deger bazli benzersiz indeksler kiraci bazli olur. Global olanlar bir kiracinin
--    baska bir kiracidaki e-postayi/etiketi "var mi" diye yoklamasina (500 = var) ve
--    ayni degeri kullanan mesru musterilerin engellenmesine yol aciyordu. Kiraci bazli
--    indeks eskisinden GEVSEK oldugu icin mevcut veride cakisma yaratmaz.
DROP INDEX IF EXISTS "IX_compensation_salary_bands_Grade_Year";
CREATE UNIQUE INDEX IF NOT EXISTS "IX_compensation_salary_bands_TenantSlug_Grade_Year"
    ON compensation_salary_bands ("TenantSlug", "Grade", "Year");

DROP INDEX IF EXISTS "IX_notification_templates_Code_Channel_Locale";
CREATE UNIQUE INDEX IF NOT EXISTS "IX_notification_templates_TenantSlug_Code_Channel_Locale"
    ON notification_templates ("TenantSlug", "Code", "Channel", "Locale");

DROP INDEX IF EXISTS "IX_onboarding_assets_AssetTag";
CREATE UNIQUE INDEX IF NOT EXISTS "IX_onboarding_assets_TenantSlug_AssetTag"
    ON onboarding_assets ("TenantSlug", "AssetTag");

DROP INDEX IF EXISTS "IX_recruitment_candidates_Email";
CREATE UNIQUE INDEX IF NOT EXISTS "IX_recruitment_candidates_TenantSlug_Email"
    ON recruitment_candidates ("TenantSlug", "Email");

-- 3) Yeni (daha SIKI) kisitlar: eski hatalarin uretmis olabilecegi cakisan veri varsa
--    indeks olusturulmaz, uyari verilir. Eski global e-posta indeksi yalnizca yenisi
--    basariyla olustugunda kaldirilir (ara durumda hic kisit kalmasin).
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM employee_employees
               GROUP BY "TenantSlug", lower("Email") HAVING count(*) > 1) THEN
        RAISE WARNING 'employee_employees: ayni kiracida buyuk/kucuk harf farkiyla tekrarlanan e-postalar var; IX_employee_employees_TenantSlug_Email OLUSTURULMADI. Tekrarlari birlestirip betigi yeniden calistirin.';
    ELSE
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_employee_employees_TenantSlug_Email"
            ON employee_employees ("TenantSlug", lower("Email"));
        DROP INDEX IF EXISTS "IX_employee_employees_Email";
    END IF;

    IF EXISTS (SELECT 1 FROM employee_employees WHERE "KeycloakUserId" IS NOT NULL
               GROUP BY "KeycloakUserId" HAVING count(*) > 1) THEN
        RAISE WARNING 'employee_employees: ayni giris hesabina (KeycloakUserId) bagli birden fazla calisan kaydi var; IX_employee_employees_KeycloakUserId OLUSTURULMADI. Fazla baglantilari kaldirip betigi yeniden calistirin.';
    ELSE
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_employee_employees_KeycloakUserId"
            ON employee_employees ("KeycloakUserId") WHERE "KeycloakUserId" IS NOT NULL;
    END IF;

    IF EXISTS (SELECT 1 FROM onboarding_asset_assignments WHERE "ReturnedOn" IS NULL
               GROUP BY "AssetId" HAVING count(*) > 1) THEN
        RAISE WARNING 'onboarding_asset_assignments: ayni zimmetin birden fazla acik atamasi var; UX_onboarding_asset_assignments_open OLUSTURULMADI. Fazla atamalari iade edip betigi yeniden calistirin.';
    ELSE
        CREATE UNIQUE INDEX IF NOT EXISTS "UX_onboarding_asset_assignments_open"
            ON onboarding_asset_assignments ("AssetId") WHERE "ReturnedOn" IS NULL;
    END IF;
END $$;

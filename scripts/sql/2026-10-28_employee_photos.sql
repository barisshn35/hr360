-- Çalışan fotoğrafı (profil, ana sayfa "Ayın çalışanı" kartı, avatarlar).
-- Sahibi employee-service. Fotoğraf kişisel veridir: herkese açık nesne deposuna (logolar gibi) konmaz,
-- veritabanında kiracı yalıtımı altında tutulur ve yalnızca oturum açmış aynı şirket kullanıcılarına
-- servis ucundan verilir. Tarayıcı yüklemeden önce en çok 512 px'e küçültür; sunucu 512 KB'ı reddeder.
-- Çalışan silinince fotoğraf da silinir (ON DELETE CASCADE); KVKK anonimleştirmesi de siler.
-- "PhotoVersion" önbellek anahtarıdır: fotoğraf değişince artar, istemci yeni adresi ister.
-- İdempotent; mevcut veriyi değiştirmez.
-- Canlıya: docker exec -i hr360-postgres-1 psql -v ON_ERROR_STOP=1 -q -U hr360admin -d hr360_operational < bu_dosya

ALTER TABLE employee_employees ADD COLUMN IF NOT EXISTS "PhotoVersion" integer;

CREATE TABLE IF NOT EXISTS employee_photos (
    "EmployeeId"  uuid PRIMARY KEY REFERENCES employee_employees("Id") ON DELETE CASCADE,
    "TenantSlug"  character varying(64) NOT NULL,
    "ContentType" character varying(32) NOT NULL,
    "Data"        bytea NOT NULL,
    "UpdatedAt"   timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_employee_photos_TenantSlug" ON employee_photos ("TenantSlug");

DO $$ BEGIN
    IF to_regprocedure('hr360_rls_apply_policies()') IS NOT NULL THEN PERFORM hr360_rls_apply_policies(); END IF;
END $$;

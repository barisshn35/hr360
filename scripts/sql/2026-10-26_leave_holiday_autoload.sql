-- İzin: resmî tatil otomatik yükleme işareti (şirket × yıl × veri sürümü).
-- HolidayAutoLoader, Türkiye takvimini kullanan şirketlerde bu yılın ve gelecek yılın EKSİK tatillerini
-- (yarım gün arifeler ve 28 Ekim dahil) gömülü tablodan bir kez ekler ve buraya yazar. Sürüm artınca
-- (ör. yarım gün desteği) eksikler bir kez daha tamamlanır. Şirketin sonradan sildiği/düzenlediği tatil
-- yeniden eklenmez ve mevcut kayıtlar değiştirilmez. Sahibi leave-service. Kişisel veri içermez.
-- İdempotent; mevcut veriyi değiştirmez.
-- Canlıya: docker exec -i hr360-postgres-1 psql -v ON_ERROR_STOP=1 -q -U hr360admin -d hr360_operational < bu_dosya

CREATE TABLE IF NOT EXISTS leave_holiday_autoloads (
    "Id"         uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Year"       integer NOT NULL,
    "Version"    integer NOT NULL,
    "Added"      integer NOT NULL DEFAULT 0,
    "AppliedAt"  timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_leave_holiday_autoloads_Tenant_Year"
    ON leave_holiday_autoloads ("TenantSlug", "Year");

-- Yeni tablolara kiracı yalıtım politikası (RLS isteğe bağlı; açık değilse davranış değişmez).
DO $$ BEGIN
    IF to_regprocedure('hr360_rls_apply_policies()') IS NOT NULL THEN PERFORM hr360_rls_apply_policies(); END IF;
END $$;

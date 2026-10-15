-- Bordro: devreden kümülatif gelir vergisi matrahı (çalışan × yıl).
-- Yıl ortasında HR360'a geçen şirket ya da yıl içinde başka işverenden gelen çalışan için o yılın HR360
-- dışındaki GV matrahı toplamı; bordro hesabında ve ihbar GV'sinde kümülatif matraha eklenir.
-- Sahibi compensation-service. KVKK: ücret verisi; yalnızca bordro yetkilisi görür/yazar, audit_log'a yazılır.
-- İdempotent; mevcut veriyi değiştirmez.
-- Canlıya: docker exec -i hr360-postgres-1 psql -v ON_ERROR_STOP=1 -q -U hr360admin -d hr360_operational < bu_dosya

CREATE TABLE IF NOT EXISTS compensation_opening_tax_bases (
    "Id"         uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "Year"       integer NOT NULL,
    "Amount"     numeric(18,2) NOT NULL DEFAULT 0,
    "Note"       character varying(300),
    "UpdatedBy"  text,
    "UpdatedAt"  timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_compensation_opening_tax_bases_Emp_Year"
    ON compensation_opening_tax_bases ("TenantSlug", "EmployeeId", "Year");

-- Yeni tablolara kiracı yalıtım politikası (RLS isteğe bağlı; açık değilse davranış değişmez).
DO $$ BEGIN
    IF to_regprocedure('hr360_rls_apply_policies()') IS NOT NULL THEN PERFORM hr360_rls_apply_policies(); END IF;
END $$;

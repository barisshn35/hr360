-- Eksik belge takibi (yenilik haritası #11). Sahibi expense-service (özlük dokümanları).
--  * expense_documents."ValidUntil": belgenin geçerlilik bitişi (ör. sağlık raporu, kimlik). Boşsa ve
--    gereksinimde süre tanımlıysa yükleme tarihinden hesaplanır.
--  * expense_document_requirements: şirkette (DepartmentId boş) ya da bir departmanda zorunlu belge türleri.
-- İdempotent. Canlıya: docker exec -i hr360-postgres-1 psql -v ON_ERROR_STOP=1 -q -U hr360admin -d hr360_operational < bu_dosya
ALTER TABLE expense_documents ADD COLUMN IF NOT EXISTS "ValidUntil" date;

CREATE TABLE IF NOT EXISTS expense_document_requirements (
    "Id"             uuid PRIMARY KEY,
    "TenantSlug"     character varying(64) NOT NULL,
    "Type"           text NOT NULL,
    "DepartmentId"   uuid,
    "ValidityMonths" integer,
    "CreatedAt"      timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_expense_document_requirements_TenantSlug" ON expense_document_requirements ("TenantSlug");

DO $$ BEGIN
    IF to_regprocedure('hr360_rls_apply_policies()') IS NOT NULL THEN PERFORM hr360_rls_apply_policies(); END IF;
END $$;

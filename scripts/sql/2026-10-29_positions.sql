-- Norm kadro ve pozisyon yönetimi (yenilik haritası #12). Sahibi organization-service.
-- Departman başına onaylı pozisyon (unvan + kadro sayısı, isteğe bağlı aylık bütçe ve kademe). Dolu kadro,
-- departmanda o unvanla atanmış aktif çalışanlardan hesaplanır (ayrı bağ tutulmaz). Bütçe yalnızca İK'ya döner.
CREATE TABLE IF NOT EXISTS organization_positions (
    "Id"            uuid PRIMARY KEY,
    "TenantSlug"    character varying(64) NOT NULL,
    "DepartmentId"  uuid NOT NULL REFERENCES organization_departments("Id") ON DELETE CASCADE,
    "Title"         character varying(120) NOT NULL,
    "Headcount"     integer NOT NULL,
    "Grade"         character varying(40),
    "BudgetMonthly" numeric(14,2),
    "Currency"      character varying(3) NOT NULL DEFAULT 'TRY',
    "Notes"         character varying(500),
    "IsActive"      boolean NOT NULL DEFAULT true,
    "CreatedAt"     timestamptz NOT NULL DEFAULT now(),
    "UpdatedAt"     timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_organization_positions_TenantSlug" ON organization_positions ("TenantSlug");
CREATE INDEX IF NOT EXISTS "IX_organization_positions_DepartmentId" ON organization_positions ("DepartmentId");
CREATE UNIQUE INDEX IF NOT EXISTS "UX_organization_positions_Dept_Title" ON organization_positions ("DepartmentId", lower("Title")) WHERE "IsActive";
DO $$ BEGIN
    IF to_regprocedure('hr360_rls_apply_policies()') IS NOT NULL THEN PERFORM hr360_rls_apply_policies(); END IF;
END $$;

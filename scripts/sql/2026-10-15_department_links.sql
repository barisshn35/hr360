-- Dalga 2 (şema görünümleri): matris organizasyon — departmanlar arası noktalı çizgi bağları.
-- organization-service DepartmentLinksController. "From" departmanı "To" departmanına fonksiyonel
-- ya da proje bağıyla (ikincil) raporlar. Departman silinince bağları da silinir.
CREATE TABLE IF NOT EXISTS organization_department_links (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" varchar(64) NOT NULL,
    "FromDepartmentId" uuid NOT NULL REFERENCES organization_departments ("Id") ON DELETE CASCADE,
    "ToDepartmentId" uuid NOT NULL REFERENCES organization_departments ("Id") ON DELETE CASCADE,
    "Kind" varchar(32) NOT NULL DEFAULT 'Functional',
    "Note" text,
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "CK_organization_department_links_NotSelf" CHECK ("FromDepartmentId" <> "ToDepartmentId"),
    CONSTRAINT "CK_organization_department_links_Kind" CHECK ("Kind" IN ('Functional', 'Project'))
);
CREATE INDEX IF NOT EXISTS "IX_organization_department_links_TenantSlug" ON organization_department_links ("TenantSlug");
CREATE INDEX IF NOT EXISTS "IX_organization_department_links_To" ON organization_department_links ("ToDepartmentId");
CREATE UNIQUE INDEX IF NOT EXISTS "UX_organization_department_links_Pair"
    ON organization_department_links ("TenantSlug", "FromDepartmentId", "ToDepartmentId", "Kind");

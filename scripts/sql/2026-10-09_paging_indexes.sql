-- Dalga 5d / G24: sunucu tarafı sayfalama için indeksler (employee, leave, organization, performance).
-- İdempotent; canlıya: docker exec -i hr360-postgres-1 psql -v ON_ERROR_STOP=1 -q -U hr360admin -d hr360_operational < bu_dosya
--
-- Tipik sorgular (planlar: tests/perf/bench_lists.py - geri alınan bir işlemde TEST kiracısıyla EXPLAIN ANALYZE):
--   çalışan listesi   : WHERE "TenantSlug"=$1 [AND "Status"=$2] ORDER BY "FirstName","LastName","Id" LIMIT/OFFSET
--   izin talepleri    : WHERE "TenantSlug"=$1 [AND "EmployeeId"=$2] [AND "Status"=$3] ORDER BY "CreatedAt" DESC LIMIT/OFFSET
--   tarih çakışması   : WHERE "TenantSlug"=$1 AND "StartDate" <= $2 AND "EndDate" >= $3
--   izin bakiyeleri   : WHERE "TenantSlug"=$1 AND "Year"=$2 ORDER BY "Type","EmployeeId" LIMIT/OFFSET
--   departman üyeleri : WHERE "TenantSlug"=$1 AND "DepartmentId"=$2 AND "EffectiveTo" IS NULL
--   departman/ekip    : WHERE "TenantSlug"=$1 [AND "CompanyId"=$2] ORDER BY "Name","Id" LIMIT/OFFSET
--   değerlendirmeler  : WHERE "TenantSlug"=$1 [AND "EmployeeId"=$2 | "ReviewerEmployeeId"=$3] ORDER BY "CreatedAt" DESC
--   hedefler          : WHERE "TenantSlug"=$1 AND "EmployeeId"=$2 [AND "CycleId"=$3] ORDER BY "CreatedAt" DESC

-- employee-service
CREATE INDEX IF NOT EXISTS "IX_employee_employees_TenantSlug_Name"
    ON employee_employees ("TenantSlug", "FirstName", "LastName", "Id");
CREATE INDEX IF NOT EXISTS "IX_employee_employees_TenantSlug_Status"
    ON employee_employees ("TenantSlug", "Status");
CREATE INDEX IF NOT EXISTS "IX_employee_employees_TenantSlug_HireDate"
    ON employee_employees ("TenantSlug", "HireDate");
CREATE INDEX IF NOT EXISTS "IX_employee_assignments_TenantSlug_Department_Current"
    ON employee_assignments ("TenantSlug", "DepartmentId") WHERE "EffectiveTo" IS NULL;

-- leave-service
CREATE INDEX IF NOT EXISTS "IX_leave_requests_TenantSlug_CreatedAt"
    ON leave_requests ("TenantSlug", "CreatedAt" DESC);
CREATE INDEX IF NOT EXISTS "IX_leave_requests_TenantSlug_Status_CreatedAt"
    ON leave_requests ("TenantSlug", "Status", "CreatedAt" DESC);
CREATE INDEX IF NOT EXISTS "IX_leave_requests_TenantSlug_Employee_CreatedAt"
    ON leave_requests ("TenantSlug", "EmployeeId", "CreatedAt" DESC);
CREATE INDEX IF NOT EXISTS "IX_leave_requests_TenantSlug_StartDate"
    ON leave_requests ("TenantSlug", "StartDate");
-- Tarih çakışması (StartDate <= bitiş AND EndDate >= başlangıç): güncel aya bakan takvim
-- sorgularında EndDate alt sınırı seçicidir (geçmiş izinler elenir).
CREATE INDEX IF NOT EXISTS "IX_leave_requests_TenantSlug_EndDate_StartDate"
    ON leave_requests ("TenantSlug", "EndDate", "StartDate");
CREATE INDEX IF NOT EXISTS "IX_leave_balances_TenantSlug_Year_Type_Employee"
    ON leave_balances ("TenantSlug", "Year", "Type", "EmployeeId");

-- organization-service
CREATE INDEX IF NOT EXISTS "IX_organization_departments_TenantSlug_Name"
    ON organization_departments ("TenantSlug", "Name", "Id");
CREATE INDEX IF NOT EXISTS "IX_organization_teams_TenantSlug_Name"
    ON organization_teams ("TenantSlug", "Name", "Id");

-- performance-service
CREATE INDEX IF NOT EXISTS "IX_performance_reviews_TenantSlug_CreatedAt"
    ON performance_reviews ("TenantSlug", "CreatedAt" DESC);
CREATE INDEX IF NOT EXISTS "IX_performance_reviews_TenantSlug_Employee_CreatedAt"
    ON performance_reviews ("TenantSlug", "EmployeeId", "CreatedAt" DESC);
CREATE INDEX IF NOT EXISTS "IX_performance_reviews_TenantSlug_Reviewer"
    ON performance_reviews ("TenantSlug", "ReviewerEmployeeId");
CREATE INDEX IF NOT EXISTS "IX_performance_goals_TenantSlug_Employee_CreatedAt"
    ON performance_goals ("TenantSlug", "EmployeeId", "CreatedAt" DESC);
CREATE INDEX IF NOT EXISTS "IX_performance_goals_TenantSlug_CreatedAt"
    ON performance_goals ("TenantSlug", "CreatedAt" DESC);

ANALYZE employee_employees;
ANALYZE employee_assignments;
ANALYZE leave_requests;
ANALYZE leave_balances;
ANALYZE organization_departments;
ANALYZE organization_teams;
ANALYZE performance_reviews;
ANALYZE performance_goals;

-- Sunucu tarafı arama arayüzdeki gibi Türkçe harf katlamalı olsun ("ayse" → "Ayşe").
-- employee-service / leave-service EF sorgularında DbContext.Fold olarak eşlenir.
CREATE OR REPLACE FUNCTION public.hr360_fold(value text) RETURNS text
    LANGUAGE sql IMMUTABLE PARALLEL SAFE
    AS $$ SELECT translate(lower(translate(value, 'İI', 'ii')), 'ışğüöçâîû', 'isguocaiu') $$;

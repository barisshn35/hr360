-- Paketsiz ürün: plan/paket sistemi, çalışan kotası ve faturalandırma kaldırıldı. Her kiracı bütün
-- modülleri ve özellikleri sınırsız kullanır. İdempotent.
ALTER TABLE platform_tenants DROP COLUMN IF EXISTS "Plan";
ALTER TABLE platform_tenants DROP COLUMN IF EXISTS "MaxEmployees";
DROP TABLE IF EXISTS governance_invoices;

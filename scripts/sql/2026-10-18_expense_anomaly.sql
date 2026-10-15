-- Masraf denetimi (ML dalgası 1, madde 41 + 43): e-Fatura/e-Arşiv karekod alanları ve
-- gönderimde üretilen denetim işaretleri (olağan dışı tutar, olası mükerrer fiş).
-- İşaretler yalnızca onaycıya bilgidir; beyan otomatik reddedilmez. İdempotent.
ALTER TABLE expense_items ADD COLUMN IF NOT EXISTS "SupplierTaxId" varchar(11);
ALTER TABLE expense_items ADD COLUMN IF NOT EXISTS "InvoiceNo" varchar(32);
ALTER TABLE expense_items ADD COLUMN IF NOT EXISTS "Ettn" varchar(36);
ALTER TABLE expense_items ADD COLUMN IF NOT EXISTS "VatAmount" numeric(18,2);
ALTER TABLE expense_items ADD COLUMN IF NOT EXISTS "AnomalyFlags" jsonb;
ALTER TABLE expense_claims ADD COLUMN IF NOT EXISTS "AnomalyCheckedAt" timestamptz;
-- Mükerrer fiş araması (aynı ETTN) için kiracı + ETTN dizini.
CREATE INDEX IF NOT EXISTS "IX_expense_items_TenantSlug_Ettn" ON expense_items ("TenantSlug", "Ettn") WHERE "Ettn" IS NOT NULL;

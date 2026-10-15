-- Bordro denetimi (ML dalgası 2, madde 42): dönem hesaplanınca ml-inference'ın ürettiği işaretler
-- (çalışanın kendi geçmişine / eş grubuna göre olağan dışı fazla mesai, ek ödeme, kesinti, brüt;
-- yıllık 270 saat fazla mesai sınırı). Yalnızca bordroyu hazırlayan/onaylayana bilgi; hesaplamayı ve
-- kapatmayı engellemez, çalışanın kendi pusulasında gösterilmez. İdempotent.
ALTER TABLE compensation_payslips ADD COLUMN IF NOT EXISTS "AnomalyFlags" jsonb;
ALTER TABLE compensation_payroll_periods ADD COLUMN IF NOT EXISTS "AnomalyCheckedAt" timestamptz;

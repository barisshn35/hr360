-- Erişim gözden geçirme: çalışan kaydı olmayan hesaplar (ör. yalnızca yönetim için açılmış şirket
-- yöneticisi) da kampanyaya girer. Bu satırlarda "EmployeeId" boştur; ad Keycloak hesabından
-- ("AccountName": ad soyad ya da kullanıcı adı) alınır ve gözden geçiren İK'dır.
ALTER TABLE governance_access_review_items ALTER COLUMN "EmployeeId" DROP NOT NULL;
ALTER TABLE governance_access_review_items ADD COLUMN IF NOT EXISTS "AccountName" text NULL;

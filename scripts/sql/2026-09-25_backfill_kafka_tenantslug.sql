-- Veri onarimi: Kafka tuketicilerinin TenantSlug damgalamadan olusturdugu kayitlar
-- (bkz. commit "Kafka tuketicilerinde olusturulan kayitlara TenantSlug damgalanmiyordu").
--
-- Duzeltmeden ONCE olusmus ShiftOverride ve Notification satirlari TenantSlug=''
-- ile kaldi ve gercek (platform-admin olmayan) kiraci kullanicilarina gorunmuyor.
-- Kiraci, satirin ait oldugu calisanin kaydindan turetilir. Idempotent: yalnizca
-- hala bos olan satirlara dokunur; calisani bulunamayan satirlar oldugu gibi kalir.
--
-- Calistirma: psql -d hr360_operational -f scripts/sql/2026-09-25_backfill_kafka_tenantslug.sql

BEGIN;

UPDATE timeshift_shift_overrides o
SET "TenantSlug" = e."TenantSlug"
FROM employee_employees e
WHERE o."TenantSlug" = ''
  AND e."Id" = o."EmployeeId"
  AND e."TenantSlug" <> '';

UPDATE notification_messages n
SET "TenantSlug" = e."TenantSlug"
FROM employee_employees e
WHERE n."TenantSlug" = ''
  AND e."Id" = n."RecipientEmployeeId"
  AND e."TenantSlug" <> '';

COMMIT;

-- Kontrol (0 beklenir):
SELECT 'timeshift_shift_overrides' AS tbl, count(*) FROM timeshift_shift_overrides WHERE "TenantSlug" = ''
UNION ALL
SELECT 'notification_messages', count(*) FROM notification_messages WHERE "TenantSlug" = '';

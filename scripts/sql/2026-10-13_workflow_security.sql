-- Onay akışı ve olay güvenliği (dalga 5, grup A). İdempotent: tekrar çalıştırılabilir.

-- ---------------------------------------------------------------- 1) Sızmış e-posta karar jetonları
-- workflow.submitted olayı tek kullanımlık e-posta karar jetonunu (ActionToken) ve onaycı
-- e-postasını taşıyordu; governance bunları governance_events'e ham yazıyor, olay radarı
-- (/api/events/recent, yöneticilere açıktı) ve açık API (events:read) döndürüyordu.
-- governance artık saklamadan önce ayıklıyor; mevcut kayıtlardan da silinir.
UPDATE governance_events
   SET "Payload" = "Payload" - 'ActionToken' - 'actionToken' - 'ApproverEmail' - 'approverEmail'
 WHERE jsonb_typeof("Payload") = 'object'
   AND "Payload" ?| ARRAY['ActionToken', 'actionToken', 'ApproverEmail', 'approverEmail'];

-- Sızmış olabilecek jetonlar geçersiz kılınır: e-postadaki "İncele ve karar ver" bağlantıları
-- artık çalışmaz, onaycılar uygulamadan karar verir. Yeni jeton yalnızca adım yeniden
-- atandığında (sıra o adıma geldiğinde, vekâlet başladığında, süre aşımıyla iletildiğinde)
-- workflow-service tarafından üretilir.
UPDATE workflow_approval_steps
   SET "ActionTokenHash" = NULL, "ActionTokenExpiresAt" = NULL
 WHERE "ActionTokenHash" IS NOT NULL OR "ActionTokenExpiresAt" IS NOT NULL;

-- ---------------------------------------------------------------- 2) İK onaycısı (onay ayarları)
-- Üst onaycısı bulunamayan talepler (departman başının kendi izni vb.) ve süre aşımında
-- iletilecek üst yöneticisi olmayan adımlar bu kişiye gider. Kiracı başına tek satır.
CREATE TABLE IF NOT EXISTS workflow_settings (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "HrApproverEmployeeId" uuid,
    "UpdatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_workflow_settings_TenantSlug" ON workflow_settings ("TenantSlug");

-- ---------------------------------------------------------------- 3) Eski izin başlıkları
-- "2 günlük Annual talebi (...)" → "2 günlük yıllık izin talebi (...)". Eşleme leave-service
-- LeaveRequestsController.TypeLabel ile aynı. Yalnızca bu kalıba uyan başlıklar değişir.
UPDATE workflow_requests w
   SET "Subject" = regexp_replace(w."Subject", '^([0-9.,]+ (günlük|saatlik)) ' || m.en || ' talebi', '\1 ' || m.tr || ' talebi')
  FROM (VALUES ('Annual', 'yıllık izin'), ('Sick', 'hastalık izni'), ('Unpaid', 'ücretsiz izin'),
               ('Maternity', 'doğum izni'), ('Paternity', 'babalık izni'), ('Marriage', 'evlilik izni'),
               ('Bereavement', 'vefat izni')) AS m(en, tr)
 WHERE w."Type" = 'LeaveRequest'
   AND w."Subject" ~ ('^[0-9.,]+ (günlük|saatlik) ' || m.en || ' talebi');

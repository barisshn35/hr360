#!/usr/bin/env python3
"""Entegrasyon (tests/integration) ve e2e (tests/e2e) testlerinin canlı demo veritabanında
bıraktığı kalıntıları temizler.

Testlerin çoğu kendi kayıtlarını siler; ama bazı kayıtlar (izin talepleri, onay akışları,
bildirimler, toplantılar...) her koşuda birikir. Bu betik bunları YALNIZCA test kaynağında
görülen işaretlerle (gerekçe/başlık önekleri, sahte sağlayıcı değerleri, RUN kimlikleri) ya da
silinen bir test kaydına bağlılıkla bulur; genel kalıp ("test" içeren her şey) kullanmaz.

  python3 tests/support/cleanup_test_data.py            # siler (tek transaction, COMMIT)
  python3 tests/support/cleanup_test_data.py --dry-run  # yalnızca sayar (ROLLBACK)

Kurallar:
  * Demo verisine dokunmaz: Ayşe/Mehmet/Zeynep/Elif, departmanlar, şirket, seed_demo.py'nin
    kayıtları (maaş, yıllık izin bakiyesi, Ayşe'nin gerekçesi "demo" olan izni, tatiller, masalar,
    bilgi bankası, workflow_settings). Silinecek kümede korunan kayıt varsa transaction hata
    verip geri alınır.
  * audit_log (değiştirilemez) ve messaging_outbox'a dokunmaz.
  * Silmeden sonra Ayşe/Mehmet/Elif'in izin bakiyelerindeki UsedDays/PendingDays kalan
    taleplerden yeniden hesaplanır (leave-service ile aynı kural: bakiye yılı = StartDate yılı,
    aynı tür; "Days" saatlik izinlerde gün kesridir; Approved → UsedDays, Submitted → PendingDays).
  * İdempotent: ikinci çalıştırmada her tablo 0 döner.
  * Elle QA testi: "TEST-QA" önekiyle (büyük/küçük harf duyarsız) girilen izin/fazla mesai gerekçeleri ve
    bunların akış/bildirimleri, KVKK başvuru açıklamaları, erişim gözden geçirme kampanya başlıkları, API
    anahtarı adları, kapanmış platform erişim izni gerekçeleri, ihlal başlıkları ve belge talebi amaçları
    temizlenir (her demo çalışanı için). Elle test ederken bu öneki kullanın.
"""

import argparse
import os
import subprocess
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))

sys.path.insert(0, os.path.dirname(__file__))
from demo_ids import AYSE, ELIF, MEHMET, ZEYNEP  # noqa: E402  (kurulumdan kuruluma değişir)

# ---------------------------------------------------------------------------------------------
# Silinecek kümeler önce geçici tablolara alınır (bağlantılar silmeden ÖNCE çözülür), sonra
# korunan kayıt denetimi yapılır, en sonda silinir. Tüm sorgular "TenantSlug" = 'demo' ile sınırlı.
# ---------------------------------------------------------------------------------------------
SQL = r"""
\set ON_ERROR_STOP 1
BEGIN;
SET LOCAL lock_timeout = '10s';
-- Payload metin sütunu: JSON değilse NULL (tür dönüşümü hatası transaction'ı düşürmesin).
CREATE FUNCTION pg_temp.js(t text) RETURNS jsonb LANGUAGE plpgsql IMMUTABLE AS $f$
BEGIN RETURN t::jsonb; EXCEPTION WHEN others THEN RETURN NULL; END $f$;

-- ============================================================== İzin talepleri (Ayşe)
-- Gerekçe işaretleri:
--   test_chat.py            : slack-baglama-istemi, slack-onay-testi, slack-komuttan-red,
--                             slack-formdan-izin, ters-tarih, sabah-ozeti, slack-yeniden-deneme,
--                             teams-red-testi, teams-kartindan
--   test_calendar.py        : takvim-testi
--   test_chat_plus.py       : TAG = "TEST5E-" + 6 hex (RUN)
--   test_email_lang.py      : email-en-NNNN / email-tr-NNNN
--   test_workflow_docs.py   : test-saatlik
--   test_telemetry.py       : TEST-telemetry
--   e2e/test_leave_flow.py  : e2e-NNNNNN
--   test_time_leave.py      : test-yarim-gun
--   elle QA testi (her çalışan): gerekçe öneki "TEST-QA" (büyük/küçük harf duyarsız)
CREATE TEMP TABLE c_leave ON COMMIT DROP AS
SELECT l."Id", l."WorkflowRequestId", l."EmployeeId"
FROM leave_requests l
WHERE l."TenantSlug" = 'demo' AND l."Reason" ILIKE 'TEST-QA%'
UNION
SELECT l."Id", l."WorkflowRequestId", l."EmployeeId"
FROM leave_requests l
WHERE l."TenantSlug" = 'demo' AND l."EmployeeId" = '{AYSE}'
  AND (l."Reason" IN ('slack-baglama-istemi','slack-onay-testi','slack-komuttan-red','slack-formdan-izin','ters-tarih',
                      'sabah-ozeti','slack-yeniden-deneme','teams-red-testi','teams-kartindan',
                      'takvim-testi','test-saatlik','TEST-telemetry','test-yarim-gun')
       OR l."Reason" ~ '^TEST5E-[0-9a-f]{{6}}$'
       OR l."Reason" ~ '^email-(en|tr)-[0-9]{{4}}$'
       OR l."Reason" ~ '^e2e-[0-9]{{6}}$');

-- ============================================================== Fazla mesai talepleri (Ayşe)
--   test_payroll_time.py  : test-fm-*   (test, "test-fm-bekleyen"i iptal edip bırakıyor)
--   test_workflow_docs.py : test-wf-*
--   test_ops_plus.py      : "TEST puantajdan tespit edilen fazla mesai"
--   elle QA testi (her çalışan): gerekçe öneki "TEST-QA"
CREATE TEMP TABLE c_ot ON COMMIT DROP AS
SELECT o."Id", o."WorkflowRequestId"
FROM timeshift_overtime_requests o
WHERE o."TenantSlug" = 'demo'
  AND ((o."EmployeeId" = '{AYSE}'
        AND (o."Reason" LIKE 'test-fm-%' OR o."Reason" LIKE 'test-wf-%' OR o."Reason" = 'TEST puantajdan tespit edilen fazla mesai'))
       OR o."Reason" ILIKE 'TEST-QA%');

-- ============================================================== Onay akışları
CREATE TEMP TABLE c_wf ("Id" uuid PRIMARY KEY, "Why" text) ON COMMIT DROP;
-- a) silinen izin / fazla mesai taleplerinin akışları
INSERT INTO c_wf SELECT DISTINCT w."Id", 'izin talebi' FROM workflow_requests w
 WHERE w."TenantSlug" = 'demo' AND w."Type" = 'LeaveRequest'
   AND (w."Id" IN (SELECT "WorkflowRequestId" FROM c_leave WHERE "WorkflowRequestId" IS NOT NULL)
        OR (pg_temp.js(w."Payload")->>'leaveRequestId') IN (SELECT "Id"::text FROM c_leave))
ON CONFLICT DO NOTHING;
INSERT INTO c_wf SELECT DISTINCT w."Id", 'fazla mesai' FROM workflow_requests w
 WHERE w."TenantSlug" = 'demo' AND w."Type" = 'Overtime'
   AND (w."Id" IN (SELECT "WorkflowRequestId" FROM c_ot WHERE "WorkflowRequestId" IS NOT NULL)
        OR (pg_temp.js(w."Payload")->>'overtimeRequestId') IN (SELECT "Id"::text FROM c_ot))
ON CONFLICT DO NOTHING;
-- b) yükünde (payload.reason) izin işareti geçen izin akışları (izni test kendisi silmiş olabilir: test_telemetry)
INSERT INTO c_wf SELECT w."Id", 'payload işareti' FROM workflow_requests w
 WHERE w."TenantSlug" = 'demo' AND w."Type" = 'LeaveRequest'
   AND ((pg_temp.js(w."Payload")->>'reason') IN ('slack-baglama-istemi','slack-onay-testi','slack-komuttan-red','slack-formdan-izin',
                                            'sabah-ozeti','slack-yeniden-deneme','teams-red-testi','teams-kartindan',
                                            'takvim-testi','test-saatlik','TEST-telemetry','test-yarim-gun')
        OR (pg_temp.js(w."Payload")->>'reason') ~ '^(TEST5E-[0-9a-f]{{6}}|email-(en|tr)-[0-9]{{4}}|e2e-[0-9]{{6}})$'
        OR (pg_temp.js(w."Payload")->>'reason') ILIKE 'TEST-QA%')
ON CONFLICT DO NOTHING;
-- c) konu işaretleri: test_workflow_docs.py serbest talepleri (vekâlet), test_chat_plus.py SQL ile açtıkları
INSERT INTO c_wf SELECT w."Id", 'konu işareti' FROM workflow_requests w
 WHERE w."TenantSlug" = 'demo'
   AND ((w."Type" = 'Other' AND w."Subject" IN ('test-vekalet','test-vekalet-2','test-modul','test-hesapsiz'))
        OR w."Subject" ~ '^TEST5E-[0-9a-f]{{6}} ')
ON CONFLICT DO NOTHING;
-- d) yetim akışlar: talep sahibi demo kişisi, türünün kaynak tablosunda karşılığı yok
--    (testler kaynak kaydı siliyor, akışı bırakıyor: ör. test_payroll_time fazla mesaileri).
--    Kaynağı olmayan türlere (Other, PositionChange, AssetRequest) yetim kuralı uygulanmaz.
--    Yeni açılmış akış (kaynak satırı henüz bağlanmamış olabilir) 2 dakika beklenir.
INSERT INTO c_wf SELECT w."Id", 'yetim (' || w."Type" || ')' FROM workflow_requests w
 WHERE w."TenantSlug" = 'demo'
   AND w."RequesterEmployeeId" IN ('{AYSE}','{MEHMET}','{ZEYNEP}','{ELIF}')
   AND w."CreatedAt" < now() - interval '2 minutes'
   AND (
     (w."Type" = 'LeaveRequest' AND NOT EXISTS (SELECT 1 FROM leave_requests s WHERE s."WorkflowRequestId" = w."Id"
          OR s."Id"::text = pg_temp.js(w."Payload")->>'leaveRequestId'))
     OR (w."Type" = 'Overtime' AND NOT EXISTS (SELECT 1 FROM timeshift_overtime_requests s WHERE s."WorkflowRequestId" = w."Id"
          OR s."Id"::text = pg_temp.js(w."Payload")->>'overtimeRequestId'))
     OR (w."Type" = 'ExpenseClaim' AND NOT EXISTS (SELECT 1 FROM expense_claims s WHERE s."WorkflowRequestId" = w."Id"
          OR s."Id"::text = pg_temp.js(w."Payload")->>'claimId'))
     OR (w."Type" = 'Travel' AND NOT EXISTS (SELECT 1 FROM expense_travel_requests s WHERE s."WorkflowRequestId" = w."Id"
          OR s."Id"::text = pg_temp.js(w."Payload")->>'travelRequestId'))
     OR (w."Type" = 'OfferApproval' AND NOT EXISTS (SELECT 1 FROM recruitment_offers s WHERE s."WorkflowRequestId" = w."Id"))
   )
ON CONFLICT DO NOTHING;

CREATE TEMP TABLE c_step ON COMMIT DROP AS
SELECT s."Id" FROM workflow_approval_steps s WHERE s."WorkflowRequestId" IN (SELECT "Id" FROM c_wf);

-- ============================================================== Diğer tablolar
-- Sistemin onaylanan izin için açtığı vardiya istisnası ("İzin talebi <izinId>", LeaveEventConsumer)
CREATE TEMP TABLE c_override ON COMMIT DROP AS
SELECT o."Id" FROM timeshift_shift_overrides o
 WHERE o."TenantSlug" = 'demo' AND o."IsSystemManaged" AND o."Type" = 'Leave'
   AND (o."Note" IN (SELECT 'İzin talebi ' || "Id"::text FROM c_leave)
        -- izni testin kendisi silmiş (test_telemetry vb.): kaynağı olmayan sistem istisnası
        OR (o."EmployeeId" IN ('{AYSE}','{MEHMET}','{ZEYNEP}','{ELIF}') AND o."Note" ~ '^İzin talebi [0-9a-f-]{{36}}$'
            AND NOT EXISTS (SELECT 1 FROM leave_requests l WHERE l."Id"::text = substring(o."Note" from 13))));

-- test_calendar.py: 1:1 testi toplantıları; 1:1 kaydı test sonunda silinir, toplantı kalır.
-- İşaret: sahte sağlayıcının (chatmock.py) katılım adresleri + kaynak 1:1 artık yok.
CREATE TEMP TABLE c_meeting ON COMMIT DROP AS
SELECT m."Id", m."StartsAt", m."ParticipantEmployeeIds", m."OrganizerEmployeeId" FROM governance_meetings m
 WHERE m."TenantSlug" = 'demo' AND m."SourceType" = 'one-on-one'
   AND (m."JoinUrl" ~ '^https://zoom\.us/j/831[0-9]{{8}}\?pwd=test$' OR m."JoinUrl" ~ '^https://teams\.microsoft\.com/l/meetup-join/msev-[0-9]+$')
   AND NOT EXISTS (SELECT 1 FROM engagement_one_on_ones o WHERE o."Id" = m."SourceId");

-- test_workflow_docs.py: Ayşe'nin belge talepleri (çalışma belgesi "Banka kredi başvurusu"; amaçsız maaş yazısı)
CREATE TEMP TABLE c_docreq ON COMMIT DROP AS
SELECT d."Id", d."EmployeeId", d."TemplateName" FROM governance_document_requests d
 WHERE d."TenantSlug" = 'demo' AND d."EmployeeId" = '{AYSE}'
   AND ((d."TemplateName" = 'Çalışma belgesi' AND d."Purpose" = 'Banka kredi başvurusu')
        OR (d."TemplateName" = 'Maaş yazısı' AND d."Purpose" IS NULL));

-- ============================================================== Bildirimler
CREATE TEMP TABLE c_notif ("Id" uuid PRIMARY KEY, "Why" text) ON COMMIT DROP;
-- 1) Onaycı e-postası: karar bağlantısındaki adım kimliği (ActionUrl t=<adım>.<gizli>) silinen
--    bir akışa ait ya da adım artık hiç yok (akışı test kendisi silmiş).
INSERT INTO c_notif SELECT n."Id", 'workflow.submitted (silinen akış)' FROM notification_messages n
 WHERE n."TenantSlug" = 'demo' AND n."TemplateCode" = 'workflow.submitted'
   AND substring(n."ActionUrl" from 't=([0-9a-f]{{32}})\.') IS NOT NULL
   AND (substring(n."ActionUrl" from 't=([0-9a-f]{{32}})\.')::uuid IN (SELECT "Id" FROM c_step)
        OR NOT EXISTS (SELECT 1 FROM workflow_approval_steps s WHERE s."Id" = substring(n."ActionUrl" from 't=([0-9a-f]{{32}})\.')::uuid))
ON CONFLICT DO NOTHING;
-- 2) Talep sahibine karar bildirimi: gövdede silinen akışın konusu ("<konu>") geçer. Eski konu
--    biçimi ("1 günlük Unpaid talebi (...)") için tarih aralığı eşleştirilir.
INSERT INTO c_notif SELECT DISTINCT n."Id", 'workflow karar (silinen akış)' FROM notification_messages n
  JOIN workflow_requests w ON w."Id" IN (SELECT "Id" FROM c_wf) AND w."RequesterEmployeeId" = n."RecipientEmployeeId"
 WHERE n."TenantSlug" = 'demo' AND n."TemplateCode" IN ('workflow.approved','workflow.rejected')
   AND (strpos(n."Body", '"' || w."Subject" || '"') > 0
        OR (w."Type" = 'LeaveRequest' AND n."Body" ~ '^"[0-9.,]+ (günlük|saatlik) .* talebi \([0-9. -]+\)"'
            AND substring(n."Body" from '^"[^"]*\(([0-9. -]+)\)"') = substring(w."Subject" from '\(([0-9. -]+)\)$')))
ON CONFLICT DO NOTHING;
-- 3) Gövde/konu işaretleri
--    test_chat_plus.py : TAG "TEST5E-xxxxxx" (duyuru konusu), test çalışanları "T5e<i> Test<RUN>" (takas)
--    test_payroll_eco.py: seyahat akışı konusu "Seyahat: TEST <yer>" (akışı test siliyor, bildirim kalıyor)
--    test_telemetry.py : ret gerekçesi "TEST-telemetry"
--    test_sms.py       : konu "TEST-SMS-xxxxxx"
INSERT INTO c_notif SELECT n."Id", 'işaret' FROM notification_messages n
 WHERE n."TenantSlug" = 'demo'
   AND (n."Subject" ~ '^TEST5E-[0-9a-f]{{6}} ' OR n."Body" ~ 'TEST5E-[0-9a-f]{{6}}'
        OR n."Body" ~ 'T5e[1-4] Test[0-9a-f]{{6}}'
        OR n."Body" LIKE '%"Seyahat: TEST %'
        OR n."Body" LIKE '%TEST-telemetry%'
        -- test_sms.py: konu "TEST-SMS-xxxxxx"
        OR n."Subject" ~ '^TEST-SMS-[0-9a-f]{{6}}'
        -- elle QA testi: konu/gövdede "TEST-QA" (izin gerekçesi, ret/iptal gerekçesi, başlık)
        OR n."Subject" ILIKE '%TEST-QA%' OR n."Body" ILIKE '%TEST-QA%')
ON CONFLICT DO NOTHING;
-- 4) test_chat_plus.py SQL ile açtığı vardiya takası: kabul bildirimiyle aynı anda (10 sn) üretilen
--    yönetici onay ve karar bildirimleri (gövdede kişi adı yok; takas kaydı test sonunda silinir).
INSERT INTO c_notif SELECT n."Id", 'takas (test çalışanı)' FROM notification_messages n
 WHERE n."TenantSlug" = 'demo' AND n."TemplateCode" IN ('shift.swap.approval','shift.swap.decision')
   AND EXISTS (SELECT 1 FROM notification_messages a
                WHERE a."TenantSlug" = 'demo' AND a."TemplateCode" = 'shift.swap.accepted' AND a."Body" ~ 'T5e[1-4] Test[0-9a-f]{{6}}'
                  AND abs(extract(epoch FROM a."CreatedAt" - n."CreatedAt")) < 10)
ON CONFLICT DO NOTHING;
-- 5) "Belgeniz hazır: <şablon>" — çalışanın o şablonla başka (kalan) talebi yoksa (test_workflow_docs.py)
INSERT INTO c_notif SELECT n."Id", 'belge talebi (silinen)' FROM notification_messages n
 WHERE n."TenantSlug" = 'demo' AND n."TemplateCode" = 'document.request'
   AND n."RecipientEmployeeId" = '{AYSE}'
   AND substring(n."Subject" from '^Belgeniz hazır: (.*)$') IN (SELECT "TemplateName" FROM c_docreq UNION SELECT 'Maaş yazısı' UNION SELECT 'Çalışma belgesi')
   AND NOT EXISTS (SELECT 1 FROM governance_document_requests d
                    WHERE d."TenantSlug" = 'demo' AND d."EmployeeId" = n."RecipientEmployeeId"
                      AND d."TemplateName" = substring(n."Subject" from '^Belgeniz hazır: (.*)$')
                      AND d."Id" NOT IN (SELECT "Id" FROM c_docreq))
ON CONFLICT DO NOTHING;
-- 6) test_calendar.py 1:1 planlama bildirimi: silinen test toplantısıyla aynı tarih-saat (İstanbul) ve katılımcı
INSERT INTO c_notif SELECT DISTINCT n."Id", '1:1 (silinen test toplantısı)' FROM notification_messages n
  JOIN c_meeting m ON n."RecipientEmployeeId" = ANY (m."ParticipantEmployeeIds")
 WHERE n."TenantSlug" = 'demo' AND n."TemplateCode" = 'engagement.one-on-one'
   AND n."Body" LIKE to_char(m."StartsAt" AT TIME ZONE 'Europe/Istanbul', 'DD.MM.YYYY HH24:MI') || ' —%'
   AND NOT EXISTS (SELECT 1 FROM engagement_one_on_ones o WHERE o."TenantSlug" = 'demo' AND o."EmployeeId" = n."RecipientEmployeeId")
ON CONFLICT DO NOTHING;
-- 8) test_payroll_eco.py (bordro dalgası 8): test yılı (2031) dönemleri için e-bordro bildirimleri
INSERT INTO c_notif SELECT n."Id", 'e-bordro (test dönemi)' FROM notification_messages n
 WHERE n."TenantSlug" = 'demo' AND n."TemplateCode" = 'compensation.epayslip' AND n."Subject" LIKE 'e-Bordro: 2031/%'
ON CONFLICT DO NOTHING;
-- 7) test_push.py: Ayşe'ye "Deneme bildirimi" (POST /push/test)
INSERT INTO c_notif SELECT n."Id", 'deneme bildirimi' FROM notification_messages n
 WHERE n."TenantSlug" = 'demo' AND n."TemplateCode" IS NULL AND n."RecipientEmployeeId" = '{AYSE}'
   AND n."Subject" IN ('Deneme bildirimi','Test notification')
ON CONFLICT DO NOTHING;

-- ============================================================== Korunan kayıt denetimi
DO $$
BEGIN
  -- Ayşe dışındaki çalışanlardan yalnızca "TEST-QA" gerekçeli talepler silinebilir.
  IF EXISTS (SELECT 1 FROM leave_requests l JOIN c_leave c ON c."Id" = l."Id"
              WHERE l."Reason" = 'demo' OR (l."EmployeeId" <> '{AYSE}' AND l."Reason" NOT ILIKE 'TEST-QA%')) THEN
    RAISE EXCEPTION 'KORUMA: demo izin talebi silinecek kümede';
  END IF;
  IF EXISTS (SELECT 1 FROM timeshift_overtime_requests o JOIN c_ot c ON c."Id" = o."Id"
              WHERE o."EmployeeId" <> '{AYSE}' AND o."Reason" NOT ILIKE 'TEST-QA%') THEN
    RAISE EXCEPTION 'KORUMA: test dışı fazla mesai talebi silinecek kümede';
  END IF;
  IF EXISTS (SELECT 1 FROM leave_requests l WHERE l."Reason" = 'demo'
               AND (l."WorkflowRequestId" IN (SELECT "Id" FROM c_wf))) THEN
    RAISE EXCEPTION 'KORUMA: demo izninin onay akışı silinecek kümede';
  END IF;
  IF EXISTS (SELECT 1 FROM c_notif c JOIN notification_messages n ON n."Id" = c."Id"
              WHERE n."TemplateCode" IN ('employee.hired','employee.assigned')) THEN
    RAISE EXCEPTION 'KORUMA: seed çalışan bildirimi silinecek kümede';
  END IF;
END $$;

-- ============================================================== Silme (tablo | silinen satır)
WITH d AS (DELETE FROM notification_messages WHERE "Id" IN (SELECT "Id" FROM c_notif) RETURNING 1)
SELECT 'notification_messages', count(*) FROM d;
WITH d AS (DELETE FROM workflow_approval_steps WHERE "Id" IN (SELECT "Id" FROM c_step) RETURNING 1)
SELECT 'workflow_approval_steps', count(*) FROM d;
WITH d AS (DELETE FROM workflow_requests WHERE "Id" IN (SELECT "Id" FROM c_wf) RETURNING 1)
SELECT 'workflow_requests', count(*) FROM d;
SELECT '  akış nedeni: ' || "Why", count(*) FROM c_wf GROUP BY "Why" ORDER BY 1;
WITH d AS (DELETE FROM timeshift_shift_overrides WHERE "Id" IN (SELECT "Id" FROM c_override) RETURNING 1)
SELECT 'timeshift_shift_overrides', count(*) FROM d;
WITH d AS (DELETE FROM leave_requests WHERE "Id" IN (SELECT "Id" FROM c_leave) RETURNING 1)
SELECT 'leave_requests', count(*) FROM d;
WITH d AS (DELETE FROM timeshift_overtime_requests WHERE "Id" IN (SELECT "Id" FROM c_ot) RETURNING 1)
SELECT 'timeshift_overtime_requests', count(*) FROM d;
-- test_workflow_docs.py vekâletleri ("test-izin"; "test-cakisan" reddedilir ama yine de)
WITH d AS (DELETE FROM workflow_delegations WHERE "TenantSlug" = 'demo' AND "FromEmployeeId" = '{MEHMET}'
                                             AND "Reason" IN ('test-izin','test-cakisan') RETURNING 1)
SELECT 'workflow_delegations', count(*) FROM d;
WITH d AS (DELETE FROM governance_meetings WHERE "Id" IN (SELECT "Id" FROM c_meeting) RETURNING 1)
SELECT 'governance_meetings', count(*) FROM d;
WITH d AS (DELETE FROM governance_document_requests WHERE "Id" IN (SELECT "Id" FROM c_docreq) RETURNING 1)
SELECT 'governance_document_requests', count(*) FROM d;
-- test_cache.py: yer güncellemesi notu ve API anahtarı adı "önbellek testi"
WITH d AS (DELETE FROM engagement_presence WHERE "TenantSlug" = 'demo' AND "EmployeeId" = '{AYSE}' AND "Note" = 'önbellek testi' RETURNING 1)
SELECT 'engagement_presence', count(*) FROM d;
WITH d AS (DELETE FROM governance_api_keys WHERE "TenantSlug" = 'demo' AND "Name" = 'önbellek testi' RETURNING 1)
SELECT 'governance_api_keys', count(*) FROM d;
-- test_kvkk_ops.py ihlal kaydı (bir sonraki koşunun başına kadar kalıyor); e2e/test_kvkk.py "E2E ihlal NNNNNN"
WITH d AS (DELETE FROM governance_data_breaches WHERE "TenantSlug" = 'demo'
             AND ("Title" IN ('TEST bordro e-postası yanlış kişiye','TEST değişiklik') OR "Title" ~ '^E2E ihlal [0-9]{{6}}$') RETURNING 1)
SELECT 'governance_data_breaches', count(*) FROM d;
-- test_kvkk_ops.py aydınlatma metni sürümü "test-2" ve bu sürüme verilen rızalar
WITH d AS (DELETE FROM governance_consents WHERE "TenantSlug" = 'demo' AND "ConsentType" = 'GORSEL_KULLANIM' AND "Version" = 'test-2' RETURNING 1)
SELECT 'governance_consents', count(*) FROM d;
WITH d AS (DELETE FROM governance_privacy_notices WHERE "TenantSlug" = 'demo' AND "Type" = 'GORSEL_KULLANIM' AND "Version" = 'test-2' RETURNING 1)
SELECT 'governance_privacy_notices', count(*) FROM d;
-- test_webhooks_apikeys.py (dalga 12): "TEST-w12" anahtarları ve kullanım sayaçları, "test-w12" kancaları, "test.w12" olayları
-- Kullanım tablosu 2026-10-25_webhook_retry_api_key_scopes.sql ile gelir; uygulanmamış kurulumda atlanır.
CREATE FUNCTION pg_temp.del_keyusage() RETURNS bigint LANGUAGE plpgsql AS $f$
DECLARE n bigint := 0;
BEGIN
  IF to_regclass('governance_api_key_usage') IS NOT NULL THEN
    EXECUTE $q$DELETE FROM governance_api_key_usage WHERE "TenantSlug" = 'demo'
                 AND "KeyId" IN (SELECT "Id" FROM governance_api_keys WHERE "TenantSlug" = 'demo' AND "Name" LIKE 'TEST-w12%')$q$;
    GET DIAGNOSTICS n = ROW_COUNT;
  END IF;
  RETURN n;
END $f$;
SELECT 'governance_api_key_usage', pg_temp.del_keyusage();
WITH d AS (DELETE FROM governance_api_keys WHERE "TenantSlug" = 'demo' AND "Name" LIKE 'TEST-w12%' RETURNING 1)
SELECT 'governance_api_keys', count(*) FROM d;
WITH d AS (DELETE FROM governance_webhooks WHERE "TenantSlug" = 'demo' AND "Url" LIKE '%test-w12%' RETURNING 1)
SELECT 'governance_webhooks', count(*) FROM d;
WITH d AS (DELETE FROM governance_events WHERE "TenantSlug" = 'demo' AND "Topic" = 'test.w12' RETURNING 1)
SELECT 'governance_events', count(*) FROM d;
-- test_ui_prefs.py (dalga 12): "test-w12" önekli arayüz tercihleri (tablo 2026-10-25_ui_prefs.sql ile gelir)
CREATE FUNCTION pg_temp.del_uiprefs() RETURNS bigint LANGUAGE plpgsql AS $f$
DECLARE n bigint := 0;
BEGIN
  IF to_regclass('notification_ui_prefs') IS NOT NULL THEN
    EXECUTE $q$DELETE FROM notification_ui_prefs WHERE "TenantSlug" = 'demo' AND "Key" LIKE 'test-w12%'$q$;
    GET DIAGNOSTICS n = ROW_COUNT;
  END IF;
  RETURN n;
END $f$;
SELECT 'notification_ui_prefs', pg_temp.del_uiprefs();
-- test_platform_reports.py: kancalar public API'den silinir, teslim kayıtları yetim kalır
WITH d AS (DELETE FROM governance_webhook_deliveries x WHERE x."TenantSlug" = 'demo'
             AND NOT EXISTS (SELECT 1 FROM governance_webhooks h WHERE h."Id" = x."WebhookId") RETURNING 1)
SELECT 'governance_webhook_deliveries', count(*) FROM d;
-- test_payroll_time.py: QR giriş-çıkış puantajı (hareketler noktayla birlikte silinir, puantaj kalır)
WITH d AS (DELETE FROM timeshift_time_entries t WHERE t."TenantSlug" = 'demo' AND t."EmployeeId" = '{AYSE}' AND t."Source" = 'Qr'
             AND NOT EXISTS (SELECT 1 FROM timeshift_clock_punches p WHERE p."EmployeeId" = t."EmployeeId"
                               AND (p."At" AT TIME ZONE 'Europe/Istanbul')::date = t."Date") RETURNING 1)
SELECT 'timeshift_time_entries', count(*) FROM d;
-- test_time_leave.py: kapatılıp yeniden açılan puantaj dönemi satırı (açık dönem = satırsız dönemle aynı davranış).
-- Tablo 2026-10-22_time_leave.sql ile gelir; uygulanmamış kurulumda atlanır.
CREATE FUNCTION pg_temp.del_periods() RETURNS bigint LANGUAGE plpgsql AS $f$
DECLARE n bigint := 0;
BEGIN
  IF to_regclass('timeshift_timesheet_periods') IS NOT NULL THEN
    EXECUTE $q$DELETE FROM timeshift_timesheet_periods WHERE "TenantSlug" = 'demo' AND "Status" = 'Open'
                 AND "ReopenReason" = 'Entegrasyon testi dönem kilidi'$q$;
    GET DIAGNOSTICS n = ROW_COUNT;
  END IF;
  RETURN n;
END $f$;
SELECT 'timeshift_timesheet_periods', pg_temp.del_periods();
-- test_payroll_eco.py: sahte TCMB (chatmock.py) kurları genel önbellekte: USD 41.5, EUR 48.25
WITH d AS (DELETE FROM expense_fx_rates WHERE "TenantSlug" IS NULL AND "Source" = 'TCMB'
             AND (("Currency" = 'USD' AND "Rate" = 41.5) OR ("Currency" = 'EUR' AND "Rate" = 48.25)) RETURNING 1)
SELECT 'expense_fx_rates', count(*) FROM d;
-- test_identity_security.py: platform yöneticisi test erişim izinleri (gerekçe öneki "TEST-2A").
-- Tablo 2026-10-16_identity_security.sql ile gelir; uygulanmamış kurulumda atlanır.
CREATE FUNCTION pg_temp.del_grants() RETURNS bigint LANGUAGE plpgsql AS $f$
DECLARE n bigint := 0;
BEGIN
  IF to_regclass('platform_access_grants') IS NOT NULL THEN
    EXECUTE $q$DELETE FROM platform_access_grants WHERE "TenantSlug" = 'demo' AND "Reason" LIKE 'TEST-2A%'$q$;
    GET DIAGNOSTICS n = ROW_COUNT;
  END IF;
  RETURN n;
END $f$;
SELECT 'platform_access_grants', pg_temp.del_grants();
-- test_identity_security.py: izin açılınca şirket yöneticilerine giden bildirim/e-postalar (metinde gerekçe "TEST-2A").
WITH d AS (DELETE FROM notification_messages WHERE "TenantSlug" = 'demo' AND "TemplateCode" = 'security.platform_access'
             AND "Body" LIKE '%TEST-2A%' RETURNING 1)
SELECT 'notification_messages (platform erişim izni)', count(*) FROM d;
-- test_org_scenarios.py: org senaryo taslakları (ad öneki "TEST-OS").
WITH d AS (DELETE FROM engagement_org_scenarios WHERE "TenantSlug" = 'demo' AND "Name" LIKE 'TEST-OS%' RETURNING 1)
SELECT 'engagement_org_scenarios', count(*) FROM d;
-- test_payroll_eco.py (bordro dalgası 8): test yılı 2031 kalıntıları — dönemi silinmiş e-bordro teslim kayıtları,
-- fark bordrosu, Ayşe'nin 2031 kıdem/ihbar hesapları, 2031 parametre satırları (test kendi girer; tohum yoktur)
-- ve test_payroll_time.py'nin işaretli örnek parametre satırı. Tablolar
-- 2026-10-21_payroll_tr.sql ile gelir; uygulanmamış kurulumda atlanır.
CREATE FUNCTION pg_temp.del_payroll8() RETURNS bigint LANGUAGE plpgsql AS $f$
DECLARE n bigint := 0; k bigint;
BEGIN
  IF to_regclass('compensation_payslip_deliveries') IS NOT NULL THEN
    EXECUTE $q$DELETE FROM compensation_payslip_deliveries d WHERE d."TenantSlug" = 'demo'
               AND NOT EXISTS (SELECT 1 FROM compensation_payroll_periods p WHERE p."Id" = d."PeriodId")$q$;
    GET DIAGNOSTICS k = ROW_COUNT; n := n + k;
    EXECUTE $q$DELETE FROM compensation_retro_diffs WHERE "TenantSlug" = 'demo' AND "SourceYear" = 2031$q$;
    GET DIAGNOSTICS k = ROW_COUNT; n := n + k;
    EXECUTE $q$DELETE FROM compensation_severance_calcs WHERE "TenantSlug" = 'demo' AND "EmployeeId" = '{AYSE}'
               AND extract(year FROM "LastWorkingDay") = 2031$q$;
    GET DIAGNOSTICS k = ROW_COUNT; n := n + k;
    EXECUTE $q$DELETE FROM compensation_payroll_parameters WHERE "TenantSlug" = 'demo' AND "Year" = 2031$q$;
    GET DIAGNOSTICS k = ROW_COUNT; n := n + k;
    -- test_payroll_time.py: dönem yılı için parametre yoksa girdiği örnek satır (kaynak notu işaretli).
    EXECUTE $q$DELETE FROM compensation_payroll_parameters WHERE "TenantSlug" = 'demo'
               AND "Source" = 'TEST bordro parametreleri (test_payroll_time)'$q$;
    GET DIAGNOSTICS k = ROW_COUNT; n := n + k;
  END IF;
  RETURN n;
END $f$;
SELECT 'compensation (bordro dalgası 8)', pg_temp.del_payroll8();

-- test_kvkk_ml10.py (dalga 10): başvuru açıklaması "TEST-W10" (veri paketleri ON DELETE CASCADE), yanıt ve süre
-- bildirimleri, "test-w10" metin sürümü ve kampanyası (test_kvkk_ops.py "test-2" kampanyası dahil), hatırlatma
-- bildirimleri, "TEST W10" VERBİS faaliyeti ve test özel alanlarından tohumlanan satırlar, "test-w10/" dosya anahtarı.
-- Tablolar 2026-10-23_kvkk_ml.sql ile gelir; uygulanmamış kurulumda atlanır.
CREATE FUNCTION pg_temp.del_w10() RETURNS bigint LANGUAGE plpgsql AS $f$
DECLARE n bigint := 0; k bigint;
BEGIN
  EXECUTE $q$DELETE FROM notification_messages m USING governance_data_requests r
             WHERE r."TenantSlug" = 'demo' AND r."Details" LIKE 'TEST-W10%' AND m."TenantSlug" = 'demo'
               AND ((m."TemplateCode" = 'privacy.request.answered' AND m."RecipientEmployeeId" = r."EmployeeId"
                     AND r."CompletedAt" IS NOT NULL AND m."CreatedAt" BETWEEN r."CompletedAt" - interval '5 seconds' AND r."CompletedAt" + interval '2 minutes')
                 OR (m."TemplateCode" IN ('privacy.request.d20','privacy.request.d27','privacy.request.overdue')
                     AND m."Body" LIKE '%' || to_char(r."DueAt" AT TIME ZONE 'Europe/Istanbul', 'DD.MM.YYYY') || '%'
                     AND m."CreatedAt" >= now() - interval '1 day'))$q$;
  GET DIAGNOSTICS k = ROW_COUNT; n := n + k;
  EXECUTE $q$DELETE FROM notification_messages WHERE "TenantSlug" = 'demo' AND "TemplateCode" = 'privacy.reconsent'
               AND ("Body" LIKE '%(sürüm test-%' OR "Body" LIKE '%(version test-%')$q$;
  GET DIAGNOSTICS k = ROW_COUNT; n := n + k;
  EXECUTE $q$DELETE FROM governance_data_requests WHERE "TenantSlug" = 'demo' AND "Details" LIKE 'TEST-W10%'$q$;
  GET DIAGNOSTICS k = ROW_COUNT; n := n + k;
  EXECUTE $q$DELETE FROM governance_consents WHERE "TenantSlug" = 'demo' AND "Version" = 'test-w10'$q$;
  GET DIAGNOSTICS k = ROW_COUNT; n := n + k;
  EXECUTE $q$DELETE FROM governance_privacy_notices WHERE "TenantSlug" = 'demo' AND "Version" = 'test-w10'$q$;
  GET DIAGNOSTICS k = ROW_COUNT; n := n + k;
  IF to_regclass('governance_consent_campaigns') IS NOT NULL THEN
    EXECUTE $q$DELETE FROM governance_consent_campaigns WHERE "TenantSlug" = 'demo' AND "Version" IN ('test-w10','test-2')$q$;
    GET DIAGNOSTICS k = ROW_COUNT; n := n + k;
    EXECUTE $q$DELETE FROM governance_privacy_inventory WHERE "TenantSlug" = 'demo'
                 AND ("Activity" LIKE 'TEST W10%' OR "ActivityKey" LIKE 'custom-field-test%')$q$;
    GET DIAGNOSTICS k = ROW_COUNT; n := n + k;
    EXECUTE $q$DELETE FROM governance_storage_deletions WHERE "TenantSlug" = 'demo'
                 AND "KeyHash" = encode(sha256(convert_to('test-w10/yok.pdf', 'UTF8')), 'hex')$q$;
    GET DIAGNOSTICS k = ROW_COUNT; n := n + k;
  END IF;
  RETURN n;
END $f$;
SELECT 'governance (dalga 10)', pg_temp.del_w10();

-- Dalga 11 testleri: test_recruitment_w11.py ("TEST11r" ilan, "test11r-" aday e-postası), test_offer_esign.py ("TEST11e",
-- "test11e-"), test_performance_w11.py ("TEST11P"), test_learning_w11.py ("TEST11L"). Aday silinince başvuru, aşama olayı,
-- durum bağlantısı ve teklif CASCADE gider; teklif silinince imza kodu/kanıtı tetikleyiciyle silinir. Tablolar
-- 2026-10-24_*.sql ile gelir; uygulanmamış kurulumda atlanır.
CREATE FUNCTION pg_temp.del_w11() RETURNS bigint LANGUAGE plpgsql AS $f$
DECLARE n bigint := 0; k bigint;
BEGIN
  IF to_regclass('recruitment_referrals') IS NOT NULL THEN
    EXECUTE $q$DELETE FROM recruitment_candidates WHERE "TenantSlug" = 'demo' AND ("Email" LIKE 'test11r-%@example.com' OR "Email" LIKE 'test11e-%@example.com')$q$;
    GET DIAGNOSTICS k = ROW_COUNT; n := n + k;
    EXECUTE $q$DELETE FROM recruitment_job_postings WHERE "TenantSlug" = 'demo' AND ("Title" LIKE 'TEST11r%' OR "Title" LIKE 'TEST11e%')$q$;
    GET DIAGNOSTICS k = ROW_COUNT; n := n + k;
    EXECUTE $q$DELETE FROM notification_messages WHERE "TenantSlug" = 'demo'
               AND ("RecipientEmail" LIKE 'test11r-%' OR "RecipientEmail" LIKE 'test11e-%'
                    OR "Body" LIKE '%TEST11r%' OR "Subject" LIKE '%TEST11r%' OR "Body" LIKE '%TEST11e%' OR "Subject" LIKE '%TEST11e%')$q$;
    GET DIAGNOSTICS k = ROW_COUNT; n := n + k;
    -- Ödül bildirimi ilan adı taşımaz: Ayşe'nin onaylı/ödenmiş önerisi kalmadıysa test kalıntısıdır.
    EXECUTE $q$DELETE FROM notification_messages m WHERE m."TenantSlug" = 'demo' AND m."TemplateCode" = 'recruitment.referral.reward'
               AND m."RecipientEmployeeId" = '{AYSE}'
               AND NOT EXISTS (SELECT 1 FROM recruitment_referrals r WHERE r."TenantSlug" = 'demo' AND r."ReferrerEmployeeId" = '{AYSE}'
                                 AND r."RewardStatus" IN ('Approved','Paid'))$q$;
    GET DIAGNOSTICS k = ROW_COUNT; n := n + k;
  END IF;
  IF to_regclass('performance_f360_requests') IS NOT NULL THEN
    EXECUTE $q$DELETE FROM performance_f360_requests WHERE "TenantSlug" = 'demo' AND "Title" LIKE 'TEST11P%'$q$;
    GET DIAGNOSTICS k = ROW_COUNT; n := n + k;
    EXECUTE $q$DELETE FROM performance_goals WHERE "TenantSlug" = 'demo' AND "Title" LIKE 'TEST11P%'$q$;
    GET DIAGNOSTICS k = ROW_COUNT; n := n + k;
    EXECUTE $q$DELETE FROM performance_objectives WHERE "TenantSlug" = 'demo' AND "Title" LIKE 'TEST11P%'$q$;
    GET DIAGNOSTICS k = ROW_COUNT; n := n + k;
    EXECUTE $q$DELETE FROM performance_calibration_sessions WHERE "TenantSlug" = 'demo' AND "Name" LIKE 'TEST11P%'$q$;
    GET DIAGNOSTICS k = ROW_COUNT; n := n + k;
    EXECUTE $q$DELETE FROM performance_review_scores WHERE "ReviewId" IN (SELECT r."Id" FROM performance_reviews r
               JOIN performance_cycles c ON c."Id" = r."CycleId" WHERE c."TenantSlug" = 'demo' AND c."Name" LIKE 'TEST11P%')$q$;
    GET DIAGNOSTICS k = ROW_COUNT; n := n + k;
    EXECUTE $q$DELETE FROM performance_reviews WHERE "CycleId" IN (SELECT "Id" FROM performance_cycles WHERE "TenantSlug" = 'demo' AND "Name" LIKE 'TEST11P%')$q$;
    GET DIAGNOSTICS k = ROW_COUNT; n := n + k;
    EXECUTE $q$DELETE FROM performance_snapshots WHERE "CycleId" IN (SELECT "Id" FROM performance_cycles WHERE "TenantSlug" = 'demo' AND "Name" LIKE 'TEST11P%')$q$;
    GET DIAGNOSTICS k = ROW_COUNT; n := n + k;
    EXECUTE $q$DELETE FROM performance_cycles WHERE "TenantSlug" = 'demo' AND "Name" LIKE 'TEST11P%'$q$;
    GET DIAGNOSTICS k = ROW_COUNT; n := n + k;
    -- test_performance_w11.py'nin geçici çalışanları (soyadı "TEST11P", e-posta "t11p<n>.x@demo.hr360").
    EXECUTE $q$DELETE FROM performance_f360_participants WHERE "ReviewerEmployeeId" IN (SELECT "Id" FROM employee_employees
               WHERE "TenantSlug" = 'demo' AND "LastName" = 'TEST11P' AND "Email" LIKE 't11p%.x@demo.hr360')$q$;
    GET DIAGNOSTICS k = ROW_COUNT; n := n + k;
    EXECUTE $q$DELETE FROM employee_assignments WHERE "EmployeeId" IN (SELECT "Id" FROM employee_employees
               WHERE "TenantSlug" = 'demo' AND "LastName" = 'TEST11P' AND "Email" LIKE 't11p%.x@demo.hr360')$q$;
    GET DIAGNOSTICS k = ROW_COUNT; n := n + k;
    EXECUTE $q$DELETE FROM employee_employees WHERE "TenantSlug" = 'demo' AND "LastName" = 'TEST11P' AND "Email" LIKE 't11p%.x@demo.hr360'$q$;
    GET DIAGNOSTICS k = ROW_COUNT; n := n + k;
  END IF;
  IF to_regclass('learning_due_reminders') IS NOT NULL THEN
    EXECUTE $q$DELETE FROM learning_due_reminders WHERE "TenantSlug" = 'demo' AND "SourceId" IN (
               SELECT e."Id" FROM learning_enrollments e JOIN learning_courses c ON c."Id" = e."CourseId" WHERE c."Title" LIKE 'TEST11L%'
               UNION SELECT "Id" FROM governance_osh_trainings WHERE "Topic" LIKE 'TEST11L%')$q$;
    GET DIAGNOSTICS k = ROW_COUNT; n := n + k;
    EXECUTE $q$DELETE FROM learning_career_paths WHERE "TenantSlug" = 'demo' AND "Name" LIKE 'TEST11L%'$q$;
    GET DIAGNOSTICS k = ROW_COUNT; n := n + k;
    EXECUTE $q$DELETE FROM learning_courses WHERE "TenantSlug" = 'demo' AND "Title" LIKE 'TEST11L%'$q$;
    GET DIAGNOSTICS k = ROW_COUNT; n := n + k;
    EXECUTE $q$DELETE FROM learning_competency_assessments WHERE "CompetencyId" IN
               (SELECT "Id" FROM learning_competencies WHERE "TenantSlug" = 'demo' AND "Name" LIKE 'TEST11L%')$q$;
    GET DIAGNOSTICS k = ROW_COUNT; n := n + k;
    EXECUTE $q$DELETE FROM learning_competencies WHERE "TenantSlug" = 'demo' AND "Name" LIKE 'TEST11L%'$q$;
    GET DIAGNOSTICS k = ROW_COUNT; n := n + k;
    EXECUTE $q$DELETE FROM governance_osh_trainings WHERE "TenantSlug" = 'demo' AND "Topic" LIKE 'TEST11L%'$q$;
    GET DIAGNOSTICS k = ROW_COUNT; n := n + k;
    EXECUTE $q$DELETE FROM notification_messages WHERE "TenantSlug" = 'demo' AND ("Body" LIKE '%TEST11L%' OR "Subject" LIKE '%TEST11L%')$q$;
    GET DIAGNOSTICS k = ROW_COUNT; n := n + k;
  END IF;
  RETURN n;
END $f$;
SELECT 'dalga 11 (işe alım, imza, performans, eğitim)', pg_temp.del_w11();

-- Dalga 12 test_account_provisioning.py: hesap sağlama ayarları ve istekleri alan adı "prov-test.hr360.example"
-- (istek notu "TEST-PROV"). Tablolar 2026-10-25_account_provisioning.sql ile gelir; uygulanmamış kurulumda atlanır.
CREATE FUNCTION pg_temp.del_w12_prov() RETURNS bigint LANGUAGE plpgsql AS $f$
DECLARE n bigint := 0; k bigint;
BEGIN
  IF to_regclass('governance_provisioning_requests') IS NOT NULL THEN
    EXECUTE $q$DELETE FROM governance_provisioning_requests WHERE "TenantSlug" = 'demo'
               AND ("AccountEmail" LIKE '%@prov-test.hr360.example' OR "Note" LIKE 'TEST-PROV%')$q$;
    GET DIAGNOSTICS k = ROW_COUNT; n := n + k;
    EXECUTE $q$DELETE FROM governance_provisioning_configs WHERE "TenantSlug" = 'demo' AND "Domain" = 'prov-test.hr360.example'$q$;
    GET DIAGNOSTICS k = ROW_COUNT; n := n + k;
  END IF;
  RETURN n;
END $f$;
SELECT 'dalga 12 (hesap sağlama)', pg_temp.del_w12_prov();

-- test_password_links.py / test_ops_plus.py / test_identity_sign.py: test hesaplarının parola linkleri
-- (e-posta "test." ya da "testldap." ile başlar). Tablo 2026-10-15_davet_ve_sistem_ayarlari.sql ile gelir.
CREATE FUNCTION pg_temp.del_pw_links() RETURNS bigint LANGUAGE plpgsql AS $f$
DECLARE n bigint := 0; k bigint;
BEGIN
  IF to_regclass('tenant_password_invites') IS NOT NULL THEN
    EXECUTE $q$DELETE FROM tenant_password_invites WHERE "TenantSlug" = 'demo'
               AND ("Email" LIKE 'test.%' OR "Email" LIKE 'testldap.%')$q$;
    GET DIAGNOSTICS k = ROW_COUNT; n := n + k;
  END IF;
  RETURN n;
END $f$;
SELECT 'parola linkleri (test hesapları)', pg_temp.del_pw_links();

-- Elle QA testi kalıntıları: "TEST-QA" öneki (büyük/küçük harf duyarsız). İzin/fazla mesai talepleri, akışları ve
-- bildirimleri yukarıdaki kümelerde; burada ilgili kişisel veri başvuruları (açıklama; veri paketleri CASCADE) ve
-- yanıt/süre bildirimleri, erişim gözden geçirme kampanyaları (başlık; satırlar CASCADE), API anahtarları (ad) ve
-- kullanım sayaçları, KAPANMIŞ (iptal edilmiş ya da süresi dolmuş) platform erişim izinleri (gerekçe), veri ihlali
-- kayıtları (başlık), belge talepleri (amaç). Açık (etkin) platform erişim izni bilerek silinmez. Kampanya silinse de
-- uygulanmış rol kaldırmaları geri alınmaz (yalnızca kayıt gider; denetim kaydı audit_log'da kalır).
-- Tablo yoksa (uygulanmamış migration) atlanır. Demo seed verisinde "TEST-QA" geçmez; korunan kayıt denetimi aşağıda.
CREATE FUNCTION pg_temp.del_qa() RETURNS bigint LANGUAGE plpgsql AS $f$
DECLARE n bigint := 0; k bigint;
BEGIN
  IF to_regclass('governance_data_requests') IS NOT NULL THEN
    EXECUTE $q$DELETE FROM notification_messages m USING governance_data_requests r
               WHERE r."TenantSlug" = 'demo' AND r."Details" ILIKE 'TEST-QA%' AND m."TenantSlug" = 'demo'
                 AND ((m."TemplateCode" = 'privacy.request.answered' AND m."RecipientEmployeeId" = r."EmployeeId"
                       AND r."CompletedAt" IS NOT NULL AND m."CreatedAt" BETWEEN r."CompletedAt" - interval '5 seconds' AND r."CompletedAt" + interval '2 minutes')
                   OR (m."TemplateCode" IN ('privacy.request.d20','privacy.request.d27','privacy.request.overdue')
                       AND m."Body" LIKE '%' || to_char(r."DueAt" AT TIME ZONE 'Europe/Istanbul', 'DD.MM.YYYY') || '%'
                       AND NOT EXISTS (SELECT 1 FROM governance_data_requests o WHERE o."TenantSlug" = 'demo' AND o."Id" <> r."Id"
                                         AND (o."Details" IS NULL OR o."Details" NOT ILIKE 'TEST-QA%')
                                         AND to_char(o."DueAt" AT TIME ZONE 'Europe/Istanbul', 'DD.MM.YYYY') = to_char(r."DueAt" AT TIME ZONE 'Europe/Istanbul', 'DD.MM.YYYY'))))$q$;
    GET DIAGNOSTICS k = ROW_COUNT; n := n + k;
    EXECUTE $q$DELETE FROM governance_data_requests WHERE "TenantSlug" = 'demo' AND "Details" ILIKE 'TEST-QA%'$q$;
    GET DIAGNOSTICS k = ROW_COUNT; n := n + k;
  END IF;
  IF to_regclass('governance_access_reviews') IS NOT NULL THEN
    EXECUTE $q$DELETE FROM governance_access_reviews WHERE "TenantSlug" = 'demo' AND "Title" ILIKE 'TEST-QA%'$q$;
    GET DIAGNOSTICS k = ROW_COUNT; n := n + k;
  END IF;
  IF to_regclass('governance_api_key_usage') IS NOT NULL THEN
    EXECUTE $q$DELETE FROM governance_api_key_usage WHERE "TenantSlug" = 'demo'
                 AND "KeyId" IN (SELECT "Id" FROM governance_api_keys WHERE "TenantSlug" = 'demo' AND "Name" ILIKE 'TEST-QA%')$q$;
    GET DIAGNOSTICS k = ROW_COUNT; n := n + k;
  END IF;
  EXECUTE $q$DELETE FROM governance_api_keys WHERE "TenantSlug" = 'demo' AND "Name" ILIKE 'TEST-QA%'$q$;
  GET DIAGNOSTICS k = ROW_COUNT; n := n + k;
  IF to_regclass('platform_access_grants') IS NOT NULL THEN
    EXECUTE $q$DELETE FROM platform_access_grants WHERE "TenantSlug" = 'demo' AND "Reason" ILIKE 'TEST-QA%'
                 AND ("RevokedAt" IS NOT NULL OR "ExpiresAt" < now())$q$;
    GET DIAGNOSTICS k = ROW_COUNT; n := n + k;
  END IF;
  EXECUTE $q$DELETE FROM governance_data_breaches WHERE "TenantSlug" = 'demo' AND "Title" ILIKE 'TEST-QA%'$q$;
  GET DIAGNOSTICS k = ROW_COUNT; n := n + k;
  EXECUTE $q$DELETE FROM governance_document_requests WHERE "TenantSlug" = 'demo' AND "Purpose" ILIKE 'TEST-QA%'$q$;
  GET DIAGNOSTICS k = ROW_COUNT; n := n + k;
  RETURN n;
END $f$;
SELECT 'elle QA testi (TEST-QA)', pg_temp.del_qa();

-- ============================================================== İzin bakiyeleri yeniden hesap
WITH calc AS (
  SELECT b."Id",
         coalesce((SELECT sum(l."Days") FROM leave_requests l WHERE l."TenantSlug" = b."TenantSlug" AND l."EmployeeId" = b."EmployeeId"
                     AND l."Type" = b."Type" AND extract(year FROM l."StartDate") = b."Year" AND l."Status" = 'Approved'), 0) AS used,
         coalesce((SELECT sum(l."Days") FROM leave_requests l WHERE l."TenantSlug" = b."TenantSlug" AND l."EmployeeId" = b."EmployeeId"
                     AND l."Type" = b."Type" AND extract(year FROM l."StartDate") = b."Year" AND l."Status" = 'Submitted'), 0) AS pending
  FROM leave_balances b
  WHERE b."TenantSlug" = 'demo'
    AND (b."EmployeeId" IN ('{AYSE}','{MEHMET}','{ELIF}') OR b."EmployeeId" IN (SELECT "EmployeeId" FROM c_leave))
), u AS (
  UPDATE leave_balances b SET "UsedDays" = c.used, "PendingDays" = c.pending, "UpdatedAt" = now()
    FROM calc c WHERE c."Id" = b."Id" AND (b."UsedDays" <> c.used OR b."PendingDays" <> c.pending)
  RETURNING 1)
SELECT 'leave_balances (güncellenen)', count(*) FROM u;
SELECT '  bakiye: ' || e."FirstName" || ' ' || b."Year" || ' ' || b."Type" || ' hak=' || b."EntitledDays"
       || ' kullanılan=' || b."UsedDays" || ' bekleyen=' || b."PendingDays", 0
  FROM leave_balances b JOIN employee_employees e ON e."Id" = b."EmployeeId"
 WHERE b."TenantSlug" = 'demo' AND b."EmployeeId" IN ('{AYSE}','{MEHMET}','{ELIF}') ORDER BY 1;

{END};
"""


def main():
    ap = argparse.ArgumentParser(description="Test kalıntılarını demo veritabanından temizler.")
    ap.add_argument("--dry-run", action="store_true", help="silmeden say (transaction ROLLBACK edilir)")
    args = ap.parse_args()

    sql = SQL.format(AYSE=AYSE, MEHMET=MEHMET, ZEYNEP=ZEYNEP, ELIF=ELIF, END="ROLLBACK" if args.dry_run else "COMMIT")
    out = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", "hr360_operational",
                          "-v", "ON_ERROR_STOP=1", "-q", "-At", "-F", "\t", "-f", "-"],
                         input=sql, capture_output=True, text=True, cwd=ROOT)
    if out.returncode != 0:
        print("Temizlik BAŞARISIZ (transaction geri alındı):", file=sys.stderr)
        print((out.stderr or out.stdout).strip()[:2000], file=sys.stderr)
        return 1

    print("Test kalıntıları temizliği" + (" — DRY-RUN (hiçbir şey silinmedi, ROLLBACK)" if args.dry_run else " (COMMIT)"))
    total = 0
    for line in out.stdout.splitlines():
        if "\t" not in line:
            continue
        name, n = line.split("\t", 1)
        if name.startswith("  "):
            print(f"    {name.strip()}" + (f": {n}" if n != "0" else ""))
            continue
        print(f"  {name:<34} {n:>6}")
        if not name.startswith("leave_balances"):
            total += int(n)
    print(f"  {'toplam silinen satır':<34} {total:>6}")
    return 0


if __name__ == "__main__":
    sys.exit(main())

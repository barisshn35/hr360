#!/usr/bin/env python3
"""Dalga 5d (governance-service): özel alanlar + KVKK üst verisi (Y24), kayıtlı/sabitlenmiş/zamanlanmış
raporlar (Y25/G4), rapor asistanı yeni ölçütleri (G3), AI/rapor/bildirim dili (G2), OTP ile basit
elektronik imza (Y28), Zapier/n8n REST hook aboneliği ve yurt dışı aktarım kilidi (G29).

Ön koşul: HR360 çalışıyor, deploy/testing/chat-mock.yml katmanı açık (sahte LLM, chatmock,
SAVED_REPORTS_INTERVAL_SECONDS kısa).

Çalıştırma: timeout 600 python3 tests/integration/test_platform_reports.py
"""
import datetime as dt
import hashlib
import json
import os
import re
import subprocess
import sys
import time
import uuid

sys.path.insert(0, os.path.dirname(__file__))
from common import AYSE, FAIL, api, check, ensure_transfers, http, mock_calls, tok, wait_for  # noqa: E402

G = "/api/governance"
DISCLAIMER_TR = "Basit elektronik imza — 5070 sayılı Kanun kapsamında nitelikli (güvenli) elektronik imza değildir."
DISCLAIMER_EN = "Simple electronic signature — not a qualified (secure) electronic signature under Turkish Law No. 5070."
ROOT = os.path.join(os.path.dirname(__file__), "..", "..")
MEHMET = "3ab24e3e-cb06-40ab-934c-9ff7eab91fb6"
FAIL.clear()
T0 = dt.datetime.now(dt.timezone.utc) - dt.timedelta(seconds=5)
GHOST = str(uuid.uuid4())  # saklama testi için geçici, ayrılmış çalışan
RDEPT = str(uuid.uuid4())  # rapor testi için geçici departman (5 kişi: küçük grup eşiği)
RPEOPLE = [str(uuid.uuid4()) for _ in range(5)]


def psql(sql):
    out = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", "hr360_operational", "-Atc", sql],
                         capture_output=True, text=True, cwd=ROOT)
    return (out.stdout + out.stderr).strip()


def lang(who, method, path, body, lg):
    return http(method, path, body, {"Authorization": "Bearer " + tok(who), "X-HR360-Lang": lg})


def key_call(key, method, path, body=None):
    return http(method, path, body, {"X-Api-Key": key})


def cleanup():
    psql("""DELETE FROM governance_custom_fields WHERE "Key" LIKE 'test5d_%';
            DELETE FROM governance_saved_reports WHERE "Name" LIKE 'TEST-5d%';
            DELETE FROM governance_document_requests WHERE "TemplateName" LIKE 'TEST-5d%';
            DELETE FROM governance_doc_templates WHERE "Name" LIKE 'TEST-5d%';
            DELETE FROM governance_privacy_assessments WHERE "Subject" LIKE 'test5d_%';
            DELETE FROM governance_webhook_deliveries WHERE "WebhookId" IN (SELECT "Id" FROM governance_webhooks WHERE "Url" LIKE '%test5d%');
            DELETE FROM governance_webhooks WHERE "Url" LIKE '%test5d%';
            DELETE FROM governance_api_keys WHERE "Name" LIKE 'TEST-5d%';
            DELETE FROM governance_transfer_agreements WHERE "Provider" IN ('zapier', 'n8n');
            DELETE FROM compensation_records WHERE "Note" = 'test5d';
            DELETE FROM employee_employees WHERE "Email" LIKE 'test5d-%@example.invalid';
            DELETE FROM organization_departments WHERE "Name" = 'TEST5D Rapor';""")


def report_people(add):
    """Geçici 5 kişilik departman (ücretleri 30–70 bin): ≥5 grubun gösterildiğini sınamak için; hemen silinir."""
    if not add:
        psql(f"""DELETE FROM compensation_records WHERE "Note" = 'test5d';
                 DELETE FROM employee_employees WHERE "Id" IN ({",".join(f"'{x}'" for x in RPEOPLE)});
                 DELETE FROM organization_departments WHERE "Id" = '{RDEPT}';""")
        return
    rows = []
    for i, e in enumerate(RPEOPLE):
        rows.append(f"""INSERT INTO employee_employees ("Id","TenantSlug","FirstName","LastName","Email","HireDate","Status","CreatedAt")
                        VALUES ('{e}','demo','TEST','Rapor{i}','test5d-r{i}-{e[:6]}@example.invalid','2024-01-01','Active',now());
                        INSERT INTO employee_assignments ("Id","TenantSlug","EmployeeId","DepartmentId","PositionTitle","EffectiveFrom","CreatedAt")
                        VALUES (gen_random_uuid(),'demo','{e}','{RDEPT}','TEST','2024-01-01',now());
                        INSERT INTO compensation_records ("Id","TenantSlug","EmployeeId","BaseSalary","Currency","Reason","EffectiveFrom","Note","CreatedAt")
                        VALUES (gen_random_uuid(),'demo','{e}',{30000 + i * 10000},'TRY','Hire','2024-01-01','test5d',now());""")
    psql(f"""INSERT INTO organization_departments ("Id","TenantSlug","Name","CompanyId","CreatedAt")
             SELECT '{RDEPT}','demo','TEST5D Rapor',"Id",now() FROM organization_companies WHERE "TenantSlug" = 'demo' LIMIT 1;""" + "".join(rows))


cleanup()
orig_lang = {e: psql(f"""SELECT "Language" FROM notification_preferences WHERE "TenantSlug" = 'demo' AND "EmployeeId" = '{e}'""") for e in (AYSE, MEHMET)}

try:
    # ======================================================================= Y24 özel alanlar
    code, meta = api("admin", "GET", f"{G}/custom-fields/meta")
    check("Y24: meta — türler ve KVKK m.5/m.6 hukuki sebepleri", code == 200 and "select" in meta["types"]
          and any(b["value"] == "m6-3-e" and b["special"] for b in meta["legalBases"]), meta)

    base = {"key": "test5d_tshirt", "label": "TEST tişört bedeni", "type": "select", "options": ["S", "M", "L"], "required": False,
            "visibility": "everyone", "selfEditable": True}
    code, r = api("admin", "POST", f"{G}/custom-fields", base)
    check("Y24: KVKK üst verisi olmadan alan açılamaz (400)", code == 400 and r.get("code") == "kvkk_metadata_required", (code, r))
    tshirt_def = {**base, "isSpecialCategory": False, "legalBasis": "m5-2-c", "purpose": "Şirket kıyafeti tedariki", "retentionMonths": 12}
    code, tshirt = api("admin", "POST", f"{G}/custom-fields", tshirt_def)
    check("Y24: normal alan oluşturuldu", code == 200 and tshirt["key"] == "test5d_tshirt" and not tshirt["isSpecialCategory"], (code, tshirt))
    code, r = api("ayse", "POST", f"{G}/custom-fields", {**tshirt_def, "key": "test5d_x"})
    check("Y24: çalışan alan tanımlayamaz", code == 403, code)

    blood_def = {"key": "test5d_blood", "label": "TEST kan grubu", "type": "select", "options": ["A Rh+", "0 Rh+", "B Rh-"], "required": False,
                 "visibility": "hr", "selfEditable": False, "isSpecialCategory": True, "legalBasis": "m6-3-e",
                 "purpose": "Acil durumda sağlık müdahalesi (İSG)", "retentionMonths": 6}
    code, r = api("admin", "POST", f"{G}/custom-fields", {**blood_def, "legalBasis": "m5-2-c"})
    check("Y24: özel nitelikli alan m.5 şartıyla açılamaz", code == 400, (code, r))
    code, r = api("admin", "POST", f"{G}/custom-fields", {**blood_def, "visibility": "everyone"})
    check("Y24: özel nitelikli alan herkese açık olamaz", code == 400, (code, r))
    code, r = api("admin", "POST", f"{G}/custom-fields", blood_def)
    check("Y24: onaylı etki değerlendirmesi yoksa 409 pia_required", code == 409 and r.get("code") == "pia_required", (code, r))

    answers = {q: {"answer": a} for q, a in [("special", "yes"), ("abroad", "no"), ("automated", "no"), ("large", "no"), ("monitoring", "no"),
                                              ("minimal", "yes"), ("basis", "yes"), ("notice", "yes"), ("retention", "yes"), ("access", "yes"),
                                              ("encryption", "yes"), ("contract", "yes")]}
    code, pia = api("admin", "POST", f"{G}/privacy/assessments", {"subject": "test5d_blood", "kind": "CustomField", "answers": answers})
    check("Y24: etki değerlendirmesi (Özel alan) hazırlandı", code == 200, (code, pia))
    code, r = api("admin", "POST", f"{G}/custom-fields", blood_def)
    check("Y24: taslak (onaysız) değerlendirme yetmez", code == 409, code)
    code, pia = api("admin", "POST", f"{G}/privacy/assessments/{pia['id']}/approve")
    check("Y24: değerlendirme onaylandı", code == 200 and pia["status"] == "Approved", (code, pia))
    code, blood = api("admin", "POST", f"{G}/custom-fields", blood_def)
    check("Y24: onaylı değerlendirmeyle özel nitelikli alan açıldı (bağlı)", code == 200 and blood["isSpecialCategory"] and blood["assessmentId"] == pia["id"], (code, blood))
    code, r = api("admin", "POST", f"{G}/custom-fields", tshirt_def)
    check("Y24: aynı anahtar ikinci kez açılamaz", code == 409, code)

    code, inv = api("admin", "GET", f"{G}/privacy/inventory")
    acts = {a["id"]: a for a in inv["activities"]} if code == 200 else {}
    b_act = acts.get("custom-field-test5d_blood")
    check("Y24: özel alanlar KVKK envanterinde dinamik faaliyet", "custom-field-test5d_tshirt" in acts and b_act and b_act["special"]
          and "m.6/3-e" in b_act["legalBasis"] and b_act["retentionCategory"] == "CustomFieldValues", b_act)

    code, r = api("ayse", "PUT", f"{G}/custom-fields/values/{AYSE}", {"values": {"test5d_tshirt": "M"}})
    check("Y24: çalışan kendi düzenlenebilir alanını yazar", code == 200 and any(f["key"] == "test5d_tshirt" and f["value"] == "M" for f in r["fields"]), (code, r))
    code, r = api("ayse", "PUT", f"{G}/custom-fields/values/{AYSE}", {"values": {"test5d_blood": "A Rh+"}})
    check("Y24: çalışan İK alanını yazamaz (403)", code == 403, code)
    code, r = api("ayse", "PUT", f"{G}/custom-fields/values/{AYSE}", {"values": {"test5d_tshirt": "XL"}})
    check("Y24: seçenek dışı değer reddedilir", code == 400, code)
    code, r = api("mehmet", "PUT", f"{G}/custom-fields/values/{AYSE}", {"values": {"test5d_tshirt": "L"}})
    check("Y24: yönetici ekibinin değerini yazamaz", code == 403, code)
    code, r = api("admin", "PUT", f"{G}/custom-fields/values/{AYSE}", {"values": {"test5d_blood": "A Rh+"}})
    check("Y24: İK özel nitelikli değeri yazar", code == 200, (code, r))

    stored = psql(f"""SELECT v."Value" FROM governance_custom_field_values v JOIN governance_custom_fields f ON f."Id" = v."FieldId"
                      WHERE f."Key" = 'test5d_blood' AND v."EmployeeId" = '{AYSE}'""")
    check("Y24: özel nitelikli değer veritabanında şifreli (enc1:/enc2:)", stored.startswith(("enc1:", "enc2:")) and "Rh" not in stored, stored[:40])
    plain = psql(f"""SELECT v."Value" FROM governance_custom_field_values v JOIN governance_custom_fields f ON f."Id" = v."FieldId"
                     WHERE f."Key" = 'test5d_tshirt' AND v."EmployeeId" = '{AYSE}'""")
    check("Y24: normal alan düz metin", plain == "M", plain)

    def fields_of(who, emp):
        c, r = api(who, "GET", f"{G}/custom-fields/values/{emp}")
        return c, {f["key"]: f for f in r["fields"]} if c == 200 else {}

    c, own = fields_of("ayse", AYSE)
    check("Y24: kişi kendi özel nitelikli değerini görür", c == 200 and own.get("test5d_blood", {}).get("value") == "A Rh+", own.keys())
    c, mgr = fields_of("mehmet", AYSE)
    check("Y24: yönetici 'everyone' alanı görür, İK düzeyindeki özel alanı görmez", c == 200 and "test5d_tshirt" in mgr and "test5d_blood" not in mgr, mgr.keys())
    c, other = fields_of("ayse", MEHMET)
    check("Y24: başka çalışan yalnızca 'everyone' alanları görür", c == 200 and "test5d_tshirt" in other and "test5d_blood" not in other, other.keys())
    c, hr = fields_of("admin", AYSE)
    check("Y24: İK özel nitelikli değeri görür", c == 200 and hr.get("test5d_blood", {}).get("value") == "A Rh+", hr.keys())
    audit = psql(f"""SELECT count(*) FROM audit_log WHERE "EntityType" = 'CustomField' AND "Action" = 'SensitiveViewed' AND "EntityId" = '{AYSE}'
                     AND "Changes"->>'field' = 'custom:test5d_blood' AND "OccurredAt" > now() - interval '5 minutes'""")
    check("Y24: İK'nın özel nitelikli değeri görüntülemesi erişim kaydına yazıldı", audit not in ("", "0"), audit)

    # Saklama: ayrılmış çalışanın değeri, alanın süresi (12 ay) dolunca imha edilir.
    psql(f"""INSERT INTO employee_employees ("Id","TenantSlug","FirstName","LastName","Email","HireDate","Status","CreatedAt")
             VALUES ('{GHOST}','demo','TEST','Ayrılan','test5d-{GHOST[:8]}@example.invalid','2020-01-01','Terminated', now() - interval '3 years');
             INSERT INTO governance_custom_field_values ("Id","TenantSlug","FieldId","EmployeeId","Value","UpdatedBy","UpdatedAt")
             VALUES (gen_random_uuid(),'demo','{tshirt['id']}','{GHOST}','L','test',now());""")
    code, pols = api("admin", "GET", f"{G}/privacy/retention")
    pol = next((p for p in pols if p["category"] == "CustomFieldValues"), None) if code == 200 else None
    check("Y24: saklama kategorisi CustomFieldValues var", pol is not None, pols if code == 200 else code)
    if pol:
        code, run = api("admin", "POST", f"{G}/privacy/retention/{pol['id']}/run")
        left = psql(f"""SELECT count(*) FROM governance_custom_field_values WHERE "EmployeeId" = '{GHOST}'""")
        still = psql(f"""SELECT count(*) FROM governance_custom_field_values WHERE "EmployeeId" = '{AYSE}' AND "FieldId" = '{tshirt['id']}'""")
        check("Y24: ayrılıştan 12 ay sonra değer silindi, aktif çalışanınki kaldı", code == 200 and run["affected"] >= 1 and left == "0" and still == "1",
              (code, run, left, still))

    # ======================================================================= G3 rapor asistanı
    code, r = lang("admin", "POST", f"{G}/insights/report", {"question": "Departmanlara göre maaş dağılımı"}, "tr")
    check("G3: ücret dağılımı (İK) — çeyrek sütunları", code == 200 and r["understood"] and r["metric"] == "salary" and r["columns"][3] == "Medyan", (code, r))
    if code == 200:
        sizes = {row[0]: row for row in r["rows"]}
        check("G3: 5 kişiden az grup gizli (kişi sayısı dahil)", all(v is None for row in r["rows"] for v in row[1:]) and r["suppressed"] == len(r["rows"])
              and r["note"], r["rows"])
    a = psql("""SELECT count(*) FROM audit_log WHERE "EntityType" = 'Report' AND "Action" = 'SensitiveViewed' AND "Changes"->>'field' = 'salaryDistribution'
                AND "OccurredAt" > now() - interval '2 minutes'""")
    check("G3: ücret dağılımı görüntülemesi erişim kaydında", a not in ("", "0"), a)
    code, r = lang("mehmet", "POST", f"{G}/insights/report", {"question": "Salary distribution by department"}, "en")
    check("G3: ücret dağılımı yöneticiye kapalı (EN ileti)", code == 200 and not r["understood"] and "HR only" in r["interpretation"], r)
    code, r = lang("admin", "POST", f"{G}/insights/report", {"question": "maaş bantları"}, "tr")
    check("G3: ücret bantları", code == 200 and r["groupBy"] == "band" and "bandı" in r["columns"][0], (code, r.get("columns")))

    code, r = lang("admin", "POST", f"{G}/insights/report", {"question": "Bu yıl ilanlara göre işe alım hunisi"}, "tr")
    check("G3: işe alım hunisi ilan bazında (aşamalar)", code == 200 and r["metric"] == "funnel" and r["groupBy"] == "posting"
          and r["columns"][1:4] == ["Başvuru", "Ön eleme+", "Mülakat+"] and all(len(x) == 7 for x in r["rows"]), (code, r.get("columns"), r.get("rows")))
    if code == 200 and r["rows"]:
        shown = [x for x in r["rows"] if x[1] is not None]
        check("G3: huni monoton (başvuru ≥ ön eleme ≥ mülakat ≥ teklif ≥ işe alım)", all(x[1] >= x[2] >= x[3] >= x[4] >= x[5] for x in shown), r["rows"])
        check("G3: 5'ten az başvurulu ilanın aşamaları gizli", all(all(v is None for v in x[1:]) for x in r["rows"] if x[1] is None)
              and r["suppressed"] == len(r["rows"]) - len(shown), r["rows"])
    code, r = lang("mehmet", "POST", f"{G}/insights/report", {"question": "Recruitment funnel this year"}, "en")
    check("G3: huni EN (yönetici, tek satır)", code == 200 and r["chart"] == "funnel" and r["columns"][1] == "Applications" and len(r["rows"]) == 1, (code, r))

    code, r = lang("admin", "POST", f"{G}/insights/report", {"question": "Bu yıl izin günleri geçen yıla göre"}, "tr")
    check("G3: geçen yılla karşılaştırma (izin) — dönem bu yıl, 4 sütun", code == 200 and r["compare"] and r["from"].startswith(f"{dt.date.today().year}-01-01")
          and r["columns"][2:] == ["Geçen yıl", "Değişim %"], (code, r.get("columns"), r.get("from")))
    code, r = lang("admin", "POST", f"{G}/insights/report", {"question": "Headcount by department compared to last year"}, "en")
    check("G3: YoY EN (kadro)", code == 200 and r["compare"] and r["columns"][2] == "Previous year" and "compared with the same period last year" in r["interpretation"], (code, r))
    code, r = lang("admin", "POST", f"{G}/insights/report", {"question": "Bu yıl ayrılan çalışan sayısı yıllık karşılaştırma"}, "tr")
    check("G3: YoY ayrılış (turnover)", code == 200 and r["metric"] == "exits" and r["compare"], (code, r.get("metric")))

    code, r = lang("admin", "POST", f"{G}/insights/report", {"question": "Mühendislik departmanında çalışan sayısı"}, "tr")
    dept_id = r.get("departmentId") if code == 200 else None
    check("G3: departman süzgeci sorudan", code == 200 and r["department"] == "Mühendislik" and dept_id and r["groupBy"] == "none"
          and "Mühendislik departmanı" in r["interpretation"], (code, r))
    code, f_hc = lang("admin", "POST", f"{G}/insights/report", {"question": "Çalışan sayısı", "departmentId": dept_id}, "tr")
    check("G3: departman süzgeci parametreyle; <5 kişilik departmanın sayısı canlı raporda da gizli", code == 200 and f_hc["department"] == "Mühendislik"
          and f_hc["rows"][0][1] is None and f_hc["suppressed"] == 1 and f_hc["note"], f_hc)
    code, r = lang("admin", "POST", f"{G}/insights/report", {"question": "Son 6 ayda departmanlara göre izin günleri"}, "tr")
    check("G3: canlı raporda da küçük grup gizleme (<5)", code == 200 and r["understood"] and all(row[1] is None for row in r["rows"])
          and r["suppressed"] == len(r["rows"]), (code, r.get("rows")))

    # ≥5 kişilik grup gösterilir (geçici departman; birkaç saniye içinde silinir).
    report_people(True)
    try:
        code, sal = lang("admin", "POST", f"{G}/insights/report", {"question": "maaş dağılımı", "departmentId": RDEPT}, "tr")
        code2, by_dept = lang("admin", "POST", f"{G}/insights/report", {"question": "Departmanlara göre çalışan sayısı"}, "tr")
        code3, f5 = lang("admin", "POST", f"{G}/insights/report", {"question": "Çalışan sayısı", "departmentId": RDEPT}, "tr")
        code4, total = lang("admin", "POST", f"{G}/insights/report", {"question": "Çalışan sayısı"}, "tr")
    finally:
        report_people(False)
    check("G3: ≥5 kişilik grupta çeyrekler (100'e yuvarlı, kişi düzeyi yok)", code == 200 and sal["rows"] == [["Tüm şirket", 5, 40000, 50000, 60000]]
          and sal["suppressed"] == 0, (code, sal.get("rows")))
    dmap = {row[0]: row[1] for row in by_dept.get("rows", [])} if code2 == 200 else {}
    check("G3: departman kırılımında ≥5 grup görünür, <5 grup gizli", dmap.get("TEST5D Rapor") == 5 and dmap.get("Mühendislik", "x") is None
          and by_dept["suppressed"] >= 1, by_dept.get("rows"))
    check("G3: departman süzgeci (≥5) sayı ≤ tüm şirket", code3 == 200 and code4 == 200 and f5["rows"][0][1] == 5 and total["rows"][0][1] >= 5,
          (f5.get("rows"), total.get("rows")))
    code, r = lang("admin", "POST", f"{G}/insights/report", {"question": "01.01.2026 ile 31.03.2026 arasında izin günleri"}, "tr")
    check("G3: tarih aralığı", code == 200 and r["from"] == "2026-01-01" and r["to"] == "2026-03-31" and "01.01.2026" in r["interpretation"], (code, r.get("from"), r.get("to")))
    code, r = lang("admin", "POST", f"{G}/insights/report", {"question": "leave days", "from": "2026-02-01", "to": "2026-02-28"}, "en")
    check("G3: tarih aralığı parametreyle", code == 200 and r["from"] == "2026-02-01" and "between 2026-02-01 and 2026-02-28" in r["interpretation"], (code, r))

    # ======================================================================= Y25/G4 kayıtlı raporlar
    code, r = api("ayse", "POST", f"{G}/saved-reports", {"name": "TEST-5d x", "question": "Departmanlara göre çalışan sayısı"})
    check("Y25: çalışan rapor kaydedemez", code == 403, code)
    code, saved = lang("mehmet", "POST", f"{G}/saved-reports", {"name": "TEST-5d kadro", "question": "Departmanlara göre çalışan sayısı",
                                                                 "pinned": True, "schedule": "Weekly"}, "tr")
    rid = saved["report"]["id"] if code == 200 else None
    check("Y25: kayıtlı + sabitlenmiş + haftalık rapor", code == 200 and saved["report"]["pinned"] and saved["report"]["schedule"] == "Weekly"
          and saved["report"]["nextRunAt"], (code, saved))
    check("Y25: kayıtlı raporda küçük grup gizleme (<5)", code == 200 and saved["result"]["suppressed"] >= 1 and all(row[1] is None for row in saved["result"]["rows"]),
          saved.get("result", {}).get("rows"))
    code, r = api("admin", "POST", f"{G}/saved-reports", {"name": "TEST-5d kişi", "question": "Geçen ay en çok fazla mesai yapan çalışanlar", "schedule": "Daily"})
    check("Y25: kişi bazında rapor zamanlanamaz", code == 400 and r.get("code") == "person_level_not_schedulable", (code, r))
    code, person = api("admin", "POST", f"{G}/saved-reports", {"name": "TEST-5d kişi", "question": "Geçen ay en çok fazla mesai yapan çalışanlar"})
    check("Y25: kişi bazında rapor zamansız kaydedilebilir (İK)", code == 200 and person["report"]["personLevel"] and not person["report"]["schedulable"], (code, person))
    if code == 200:
        code, r = api("admin", "PUT", f"{G}/saved-reports/{person['report']['id']}", {"schedule": "Monthly"})
        check("Y25: sonradan da zamanlanamaz", code == 400 and r.get("code") == "person_level_not_schedulable", (code, r))
    code, daily = api("mehmet", "POST", f"{G}/saved-reports", {"name": "TEST-5d saatli", "question": "Aylık işe alım sayısı", "schedule": "Daily", "time": "08:30"})
    nr = daily["report"]["nextRunAt"] if code == 200 else ""
    check("Y25: günlük 08:30 (İstanbul) → sonraki çalışma 05:30Z", code == 200 and daily["report"]["time"] == "08:30" and "T05:30:00" in nr
          and daily["report"]["timeZone"] == "Europe/Istanbul", (code, daily))
    if code == 200:
        code, w = api("mehmet", "PUT", f"{G}/saved-reports/{daily['report']['id']}", {"schedule": "Weekly", "day": 5, "time": "09:15"})
        nxt_dow = dt.datetime.fromisoformat(w["nextRunAt"].replace("Z", "+00:00")).astimezone(dt.timezone(dt.timedelta(hours=3))) if code == 200 else None
        check("Y25: haftalık cuma 09:15", code == 200 and w["day"] == 5 and nxt_dow and nxt_dow.weekday() == 4 and nxt_dow.strftime("%H:%M") == "09:15", (code, w))
    code, r = api("mehmet", "POST", f"{G}/saved-reports", {"name": "TEST-5d bozuk saat", "question": "Aylık işe alım sayısı", "schedule": "Daily", "time": "25:00"})
    check("Y25: geçersiz saat reddedilir", code == 400 and r.get("code") == "invalid_time", (code, r))
    code, r = api("admin", "POST", f"{G}/saved-reports", {"name": "TEST-5d bozuk", "question": "hava nasıl"})
    check("Y25: anlaşılmayan soru kaydedilmez", code == 400, code)
    code, pins = api("mehmet", "GET", f"{G}/saved-reports/pinned")
    check("Y25: pano bileşeni sabitlenmiş raporun güncel sonucunu döner", code == 200 and any(p["report"]["id"] == rid and p["result"]["understood"] for p in pins), (code, pins))
    code, mine = api("admin", "GET", f"{G}/saved-reports")
    check("Y25: kayıtlar kişiye özel (İK başkasınınkini görmez)", code == 200 and all(x["id"] != rid for x in mine), code)
    code, r = api("admin", "POST", f"{G}/saved-reports/{rid}/run")
    check("Y25: başkasının kayıtlı raporu çalıştırılamaz", code == 404, code)
    code, run = lang("mehmet", "POST", f"{G}/saved-reports/{rid}/run", None, "en")
    check("Y25: kayıtlı rapor açılışta arayüz dilinde çalışır", code == 200 and run["result"]["columns"][0] == "Department", (code, run))

    # Zamanlanmış teslim: alıcının dili İngilizce; bildirimde rakam yok, oturum gerektiren bağlantı var.
    # G2: dil tercihi governance ucu üzerinden (notification_preferences ile aynı satır).
    code, r = api("mehmet", "PUT", f"{G}/me/language", {"language": "en"})
    code2, g = api("mehmet", "GET", f"{G}/me/language")
    check("G2: dil tercihi kaydedilir (governance /me/language)", code == 200 and r["stored"] and code2 == 200 and g["language"] == "en", (code, r, g))
    code, r = api("mehmet", "PUT", f"{G}/me/language", {"language": "de"})
    check("G2: desteklenmeyen dil reddedilir", code == 400, code)
    code, r = api("admin", "PUT", f"{G}/me/language", {"language": "en"})
    check("G2: çalışan kaydı olmayan kullanıcıda tercih saklanmaz", code == 200 and r["stored"] is False, (code, r))
    t_sched = psql("SELECT now()")
    psql(f"""UPDATE governance_saved_reports SET "NextRunAt" = now() - interval '1 minute' WHERE "Id" = '{rid}'""")
    rows = []
    for _ in range(30):
        rows = psql(f"""SELECT "Channel" || '|' || coalesce("Subject",'') || '|' || replace("Body", E'\\n', ' ') || '|' || coalesce("ActionUrl",'') || '|' || "Language"
                        FROM notification_messages WHERE "TemplateCode" = 'report.scheduled' AND "RecipientEmployeeId" = '{MEHMET}' AND "CreatedAt" >= '{t_sched}'""").splitlines()
        if len(rows) >= 2:
            break
        time.sleep(1)
    api("mehmet", "PUT", f"{G}/me/language", {"language": orig_lang[MEHMET] or "tr"})
    chans = sorted(r.split("|")[0] for r in rows)
    check("Y25: zamanlanmış teslim — uygulama içi + e-posta bildirimi", chans == ["Email", "InApp"], rows)
    if rows:
        _, subj, body, url, lg = rows[0].split("|")
        body_wo_link = (body.replace(url, "") if url else body).replace("HR360", "")  # ürün adı rakam değildir
        check("Y25: bildirimde bağlantı var (oturum gerektiren panel yolu)", f"/panel/rapor-asistani?kayitli={rid}" in body and url.endswith(f"kayitli={rid}"), body)
        check("Y25: bildirimde rakam/veri yok", not re.search(r"\d", body_wo_link), body_wo_link)
        check("G2: zamanlanmış rapor bildirimi alıcının dilinde (en)", lg == "en" and subj.startswith("Scheduled report ready"), (lg, subj))
    nxt = psql(f"""SELECT ("NextRunAt" > now())::text || '|' || "DeliveryCount" FROM governance_saved_reports WHERE "Id" = '{rid}'""")
    check("Y25: sonraki çalışma ileri alındı, teslim sayıldı", nxt == "true|1", nxt)
    code, r = api("mehmet", "PUT", f"{G}/saved-reports/{rid}", {"pinned": False, "schedule": "None"})
    check("Y25: sabitleme ve zamanlama kaldırılabilir", code == 200 and not r["pinned"] and r["nextRunAt"] is None, (code, r))

    # ======================================================================= G2 AI çıktı dili
    ensure_transfers("anthropic")
    api("admin", "PUT", f"{G}/ai/settings", {"enabled": True, "allowPersonalData": False})
    t1 = time.time()
    code, r = lang("admin", "POST", f"{G}/ai/job-draft", {"title": "TEST-5d Backend Developer", "skills": ["C#"]}, "en")
    call = wait_for("/anthropic/v1/messages", since=t1, pred=lambda c: "TEST-5d Backend" in c["body"])
    sysmsg = json.loads(call[0]["body"])["system"] if call else ""
    check("G2: AI ilan taslağı EN isteğinde İngilizce yönerge", code == 200 and "Write in English" in sysmsg and "Türkçe yaz" not in sysmsg, (code, sysmsg[:120]))
    t2 = time.time()
    code, r = lang("admin", "POST", f"{G}/ai/job-draft", {"title": "TEST-5d Arka uç geliştirici"}, "tr")
    call = wait_for("/anthropic/v1/messages", since=t2, pred=lambda c: "TEST-5d Arka" in c["body"])
    sysmsg = json.loads(call[0]["body"])["system"] if call else ""
    check("G2: AI ilan taslağı TR isteğinde Türkçe yönerge", code == 200 and "Türkçe yaz" in sysmsg, sysmsg[:120])
    t3 = time.time()
    code, r = lang("ayse", "POST", f"{G}/ai/assistant", {"question": "TEST-5d what is the remote work policy?"}, "en")
    call = wait_for("/anthropic/v1/messages", since=t3, pred=lambda c: "TEST-5d what" in c["body"])
    sysmsg = json.loads(call[0]["body"])["system"] if call else ""
    check("G2: İK asistanı (AI) EN isteğinde İngilizce yanıt yönergesi", code == 200 and "Write in English" in sysmsg and "KNOWLEDGE BASE" in sysmsg, sysmsg[:120])
    api("admin", "PUT", f"{G}/ai/settings", {"enabled": False, "allowPersonalData": False})
    code, r = lang("admin", "POST", f"{G}/ai/job-draft", {"title": "TEST-5d"}, "en")
    check("G2: AI hata iletisi istek dilinde (EN)", code == 403 and r["message"].startswith("AI is turned off"), (code, r))
    code, r = lang("admin", "POST", f"{G}/ai/job-draft", {"title": "TEST-5d"}, "tr")
    check("G2: AI hata iletisi Türkçe (varsayılan)", code == 403 and "kapalı" in r["message"], (code, r))
    code, r = lang("ayse", "POST", f"{G}/insights/assistant", {"question": "who is on leave today?"}, "en")
    check("G2: İK asistanı veri yanıtı EN", code == 200 and ("leave" in r["reply"].lower() or "nobody" in r["reply"].lower()) and "izinde" not in r["reply"], r)

    # ======================================================================= G29 REST hook (Zapier / n8n)
    code, k1 = api("admin", "POST", f"{G}/api-keys", {"name": "TEST-5d hooks", "scopes": ["hooks:write"]})
    code2, k2 = api("admin", "POST", f"{G}/api-keys", {"name": "TEST-5d read", "scopes": ["events:read"]})
    check("G29: hooks:write yetkili API anahtarı", code == 200 and code2 == 200 and "hooks:write" in k1["scopes"], (code, k1))
    hook_key, read_key = k1["key"], k2["key"]
    code, r = key_call(read_key, "POST", f"{G}/public/v1/hooks", {"target_url": "http://chatmock:8000/hooks/test5d-n8n", "event": "document.signed"})
    check("G29: hooks:write yetkisi olmayan anahtar abone olamaz", code == 403, code)
    code, r = key_call(hook_key, "POST", f"{G}/public/v1/hooks", {"target_url": "https://hooks.zapier.com/hooks/catch/1/test5d", "event": "document.signed"})
    check("G29: Zapier hedefi aktarım dayanağı olmadan 409 transfer_basis_required", code == 409 and r.get("code") == "transfer_basis_required" and r.get("provider") == "zapier", (code, r))
    code, r = api("admin", "POST", f"{G}/webhooks", {"name": "TEST-5d", "url": "https://hooks.zapier.com/hooks/catch/2/test5d", "events": ["*"], "isEnabled": True})
    check("G29: İK webhook ekranında da aynı kilit", code == 409 and r.get("code") == "transfer_basis_required", (code, r))
    code, r = key_call(hook_key, "POST", f"{G}/public/v1/hooks", {"target_url": "http://chatmock:8000/hooks/test5d-n8n", "event": "nope"})
    check("G29: bilinmeyen olay reddedilir", code == 400 and r.get("code") == "invalid_event", (code, r))
    code, n8n = key_call(hook_key, "POST", f"{G}/public/v1/hooks", {"hookUrl": "http://chatmock:8000/hooks/test5d-n8n", "event": "document.signed"})
    check("G29: n8n (iç ağ) aboneliği aktarım dayanağı olmadan 201 + imza anahtarı", code == 201 and n8n["id"] and n8n["signingSecret"].startswith("whsec_")
          and n8n["deployment"] == "internal", (code, n8n))
    code, r = key_call(hook_key, "POST", f"{G}/public/v1/hooks", {"target_url": "https://acme.app.n8n.cloud/webhook/test5d", "event": "leave.approved", "self_hosted": True})
    check("G29: n8n Cloud yurt dışıdır (self_hosted beyanı bunu aşmaz) → 409", code == 409 and r.get("provider") == "n8n" and r.get("hint"), (code, r))
    code, own = key_call(hook_key, "POST", f"{G}/public/v1/hooks", {"target_url": "https://n8n.test5d.example.com/webhook/x", "event": "leave.approved", "self_hosted": True})
    check("G29: kendi alan adındaki n8n (self-hosted) serbest", code == 201 and own["deployment"] == "self-hosted", (code, own))
    if code == 201:
        key_call(hook_key, "DELETE", f"{G}/public/v1/hooks/{own['id']}")
    ensure_transfers("zapier")
    code, zap = key_call(hook_key, "POST", f"{G}/public/v1/hooks", {"target_url": "https://hooks.zapier.com/hooks/catch/1/test5d", "event": "document.signed"})
    check("G29: aktarım dayanağı kaydı sonrası Zapier aboneliği açılır", code == 201, (code, zap))
    code, lst = key_call(hook_key, "GET", f"{G}/public/v1/hooks")
    check("G29: abonelik listesi", code == 200 and {n8n["id"], zap.get("id")} <= {h["id"] for h in lst["data"]}, (code, lst))
    code, sample = key_call(hook_key, "GET", f"{G}/public/v1/hooks/samples/document.signed")
    check("G29: olay için sentetik örnek veri (gerçek kişi verisi yok)", code == 200 and isinstance(sample, list) and sample[0]["sample"] is True
          and sample[0]["type"] == "document.signed" and "DocumentSha256" in sample[0]["data"], (code, sample))
    code, _ = key_call(hook_key, "GET", f"{G}/public/v1/hooks/samples/nope")
    check("G29: bilinmeyen olay örneği 404", code == 404, code)
    code, inv = api("admin", "GET", f"{G}/privacy/transfers")
    zp = next((p for p in inv.get("providers", []) if p.get("key") == "zapier"), None) if code == 200 else None
    check("G29: Zapier yurt dışı aktarım kataloğunda ve kullanımda", zp is not None and zp.get("inUse") is True, zp)
    # Dayanak sonradan kaldırılırsa (ekran kilidini atlayarak) Zapier'e veri gitmez.
    psql("""DELETE FROM governance_transfer_agreements WHERE "Provider" = 'zapier'""")

    # ======================================================================= Y28 OTP ile basit e-imza
    code, tpl = api("admin", "POST", f"{G}/documents/templates", {"name": "TEST-5d çalışma belgesi", "category": "Genel",
                                                                   "body": "<p>{{calisan.adSoyad}} şirketimizde çalışmaktadır.</p>",
                                                                   "selfService": True, "requiresApproval": False})
    check("Y28: test şablonu", code == 200, (code, tpl))
    code, req = api("ayse", "POST", f"{G}/documents/requests", {"templateId": tpl["id"], "purpose": "TEST"})
    doc_id = req.get("id") if code == 200 else None
    check("Y28: belge düzenlendi", code == 200 and req["status"] == "Issued", (code, req))
    code, doc = api("ayse", "GET", f"{G}/documents/requests/{doc_id}/document")
    html_hash = hashlib.sha256(doc["html"].encode()).hexdigest() if code == 200 else None

    code, r = api("mehmet", "POST", f"{G}/documents/requests/{doc_id}/sign/otp", {"channel": "InApp"})
    check("Y28: başkası imza kodu isteyemez", code == 403, code)

    def new_otp(skip_cooldown=True):
        if skip_cooldown:
            # Tek imza motoru: aynı belge için iki kod arasında 30 sn beklenir — testte önceki kodları geri tarihle.
            psql(f"""UPDATE governance_signature_otps SET "CreatedAt" = "CreatedAt" - interval '1 minute' WHERE "DocumentId" = '{doc_id}'""")
        c, o = lang("ayse", "POST", f"{G}/documents/requests/{doc_id}/sign/otp", {"channel": "InApp"}, "tr")
        code_txt = None
        for _ in range(5):
            body = psql(f"""SELECT "Body" FROM notification_messages WHERE "TemplateCode" = 'signature.otp' AND "RecipientEmployeeId" = '{AYSE}'
                            AND "Body" LIKE '%TEST-5d çalışma belgesi%' ORDER BY "CreatedAt" DESC LIMIT 1""")
            m = re.search(r"\b(\d{6})\b", body)
            if m:
                code_txt = m.group(1)
                break
            time.sleep(0.5)
        return c, o, code_txt

    c, otp1, code1 = new_otp()
    check("Y28: kod istendi (yanıtta kod yok, 10 dk, 5 deneme, tek uyarı metni)", c == 200 and code1 and code1 not in json.dumps(otp1) and otp1["maxAttempts"] == 5
          and otp1["disclaimer"] == DISCLAIMER_TR and otp1["sendsLeft"] == 4, (c, otp1))
    c, r = lang("ayse", "POST", f"{G}/documents/requests/{doc_id}/sign/otp", {"channel": "InApp"}, "tr")
    check("Y28: 30 sn dolmadan yeni kod istenemez (otp_cooldown)", c == 429 and r.get("code") == "otp_cooldown", (c, r))
    check("Y28: kod kaydı belge türüyle (DocumentRequest)", psql(f"""SELECT "DocumentType" FROM governance_signature_otps WHERE "Id" = '{otp1.get('otpId')}'""") == "DocumentRequest")
    stored_hash = psql(f"""SELECT "CodeHash" FROM governance_signature_otps WHERE "Id" = '{otp1.get('otpId')}'""")
    check("Y28: kod veritabanında özetli (düz değil)", len(stored_hash) == 64 and code1 not in stored_hash, stored_hash)
    subj = psql(f"""SELECT "Subject" || '|' || "Language" FROM notification_messages WHERE "TemplateCode" = 'signature.otp' AND "RecipientEmployeeId" = '{AYSE}' ORDER BY "CreatedAt" DESC LIMIT 1""")
    exp_subj = "Document signing code|en" if orig_lang[AYSE] == "en" else "Belge imza kodu|tr"
    check("G2: imza kodu bildirimi alıcının dilinde", subj == exp_subj, subj)
    wrong = "000000" if code1 != "000000" else "111111"
    lefts = []
    for _ in range(4):
        c, r = api("ayse", "POST", f"{G}/documents/requests/{doc_id}/sign", {"otpId": otp1["otpId"], "code": wrong})
        lefts.append((c, r.get("attemptsLeft")))
    check("Y28: hatalı kod — kalan deneme azalır", lefts == [(400, 4), (400, 3), (400, 2), (400, 1)], lefts)
    c, r = api("ayse", "POST", f"{G}/documents/requests/{doc_id}/sign", {"otpId": otp1["otpId"], "code": wrong})
    check("Y28: 5. hatalı denemede kod kilitlenir", c == 429 and r.get("code") == "otp_locked", (c, r))
    c, r = api("ayse", "POST", f"{G}/documents/requests/{doc_id}/sign", {"otpId": otp1["otpId"], "code": code1})
    check("Y28: kilitli kod doğru olsa da kabul edilmez", c == 429, (c, r))

    c, otp2, code2 = new_otp()
    psql(f"""UPDATE governance_signature_otps SET "ExpiresAt" = now() - interval '1 second' WHERE "Id" = '{otp2['otpId']}'""")
    c, r = api("ayse", "POST", f"{G}/documents/requests/{doc_id}/sign", {"otpId": otp2["otpId"], "code": code2})
    check("Y28: süresi dolan kod 410 otp_expired", c == 410 and r.get("code") == "otp_expired", (c, r))

    c, otp3, code3 = new_otp()
    c_old, r_old = api("ayse", "POST", f"{G}/documents/requests/{doc_id}/sign", {"otpId": otp2["otpId"], "code": code2})
    check("Y28: yeni kod istenince eskisi geçersiz", c_old == 410, (c_old, r_old))
    t_sign = time.time()
    c, ev = lang("ayse", "POST", f"{G}/documents/requests/{doc_id}/sign", {"otpId": otp3["otpId"], "code": code3}, "tr")
    check("Y28: doğru kodla imzalandı — kanıt", c == 200 and ev["method"] == "OTP-InApp" and ev["documentId"] == doc_id and ev["documentVersion"] == 1
          and ev["signerEmployeeId"] == AYSE and len(ev["evidenceSha256"]) == 64 and ev["disclaimer"] == DISCLAIMER_TR
          and ev["documentType"] == "DocumentRequest" and ev["integrityOk"] is True and ev["title"] == "TEST-5d çalışma belgesi", (c, ev))
    check("Y28: kanıttaki belge özeti belge içeriğinin SHA-256'sı", c == 200 and ev["documentSha256"] == html_hash, (ev.get("documentSha256"), html_hash))
    check("Y28: IP /24'e kısaltılmış (ya da yok)", c == 200 and (ev["ipPrefix"] is None or ev["ipPrefix"].endswith(".0/24") or ev["ipPrefix"].endswith("/48")), ev.get("ipPrefix"))
    c, r = api("ayse", "POST", f"{G}/documents/requests/{doc_id}/sign", {"otpId": otp3["otpId"], "code": code3})
    check("Y28: aynı belge ikinci kez imzalanamaz / kod tekrar kullanılamaz", c == 409, (c, r))
    c, r = api("ayse", "POST", f"{G}/documents/requests/{doc_id}/sign/otp", {})
    check("Y28: imzalı belge için yeni kod istenemez", c == 409 and r.get("code") == "already_signed", (c, r))
    c, d2 = lang("ayse", "GET", f"{G}/documents/requests/{doc_id}/document", None, "en")
    check("Y28: belge görünümünde imza kanıtı + İngilizce uyarı", c == 200 and d2["signature"]["evidenceSha256"] == ev.get("evidenceSha256")
          and d2["signature"]["disclaimer"] == DISCLAIMER_EN, (c, d2.get("signature")))
    c, sm = api("ayse", "GET", f"{G}/documents/signatures/mine")
    row = next((x for x in (sm or {}).get("items", []) if x["id"] == ev.get("id")), None)
    check("Y28: İmzaladığım belgeler listesinde (tür, başlık, bütünlük)", c == 200 and row and row["documentType"] == "DocumentRequest"
          and row["title"] == "TEST-5d çalışma belgesi" and row["integrityOk"] is True and sm["disclaimer"] == DISCLAIMER_TR, (c, row))
    c, sm2 = api("mehmet", "GET", f"{G}/documents/signatures/mine")
    check("Y28: başkasının imzası listede yok", c == 200 and all(x["id"] != ev.get("id") for x in sm2.get("items", [])), c)
    err = psql(f"""UPDATE governance_signatures SET "Method" = 'X' WHERE "Id" = '{ev.get('id')}'""")
    check("Y28: imza kanıtı değiştirilemez (tetikleyici)", "değiştirilemez" in err, err)
    c, mine = api("ayse", "GET", f"{G}/documents/requests/mine")
    check("Y28: taleplerim listesinde imzalı işareti", c == 200 and any(x["id"] == doc_id and x["signed"] for x in mine), c)
    c, v = http("GET", f"{G}/documents/verify/{req['verificationCode']}")
    check("Y28: doğrulama sayfasında imza kanıtı (kişisel veri yok)", c == 200 and v["signature"]["signed"] and v["signature"]["matchesDocument"]
          and v["signature"]["disclaimer"] == DISCLAIMER_TR and "signerEmployeeId" not in v["signature"], (c, v))
    c, s = api("admin", "GET", f"{G}/documents/requests/{doc_id}/signature")
    check("Y28: İK imza kanıtını görür", c == 200 and s["signed"], (c, s))
    c, s = api("mehmet", "GET", f"{G}/documents/requests/{doc_id}/signature")
    check("Y28: yönetici başkasının imza kanıtını göremez", c == 404, c)

    # G29 teslim: imza olayı n8n aboneliğine gitti, Zapier'e (dayanak kaldırıldı) gitmedi.
    hits = wait_for("/hooks/test5d-n8n", pred=lambda x: "document.signed" in (x["body"] or "") and doc_id in x["body"], since=t_sign - 2, timeout=30)
    check("G29: REST hook teslimatı (n8n → chatmock) olay zarfıyla", bool(hits) and json.loads(hits[0]["body"])["data"]["DocumentId"] == doc_id, hits[:1])
    if hits:
        check("G29: teslimatta ad/e-posta yok (yalnızca kimlikler)", "Yılmaz" not in hits[0]["body"] and "@" not in hits[0]["body"], hits[0]["body"][:200])
    zerr = ""
    for _ in range(10):
        zerr = psql(f"""SELECT "Error" FROM governance_webhook_deliveries WHERE "WebhookId" = '{zap.get('id')}' ORDER BY "OccurredAt" DESC LIMIT 1""")
        if zerr:
            break
        time.sleep(1)
    check("G29: dayanağı kaldırılan Zapier hedefine veri gönderilmedi", zerr == "transfer_basis_required", zerr)
    code, _ = key_call(hook_key, "DELETE", f"{G}/public/v1/hooks/{n8n['id']}")
    code2, _ = key_call(hook_key, "DELETE", f"{G}/public/v1/hooks/{n8n['id']}")
    check("G29: abonelik kaldırma (tekrarında 404)", code == 200 and code2 == 404, (code, code2))

    # Y28 saklama: belge saklama süresiyle silinince kanıt da silinir.
    psql(f"""DELETE FROM governance_document_requests WHERE "Id" = '{doc_id}'""")
    left = psql(f"""SELECT count(*) FROM governance_signatures WHERE "DocumentId" = '{doc_id}'""") + "/" + \
        psql(f"""SELECT count(*) FROM governance_signature_otps WHERE "DocumentId" = '{doc_id}'""")
    check("Y28: kanıt ve kodlar belgeyle birlikte silinir", left == "0/0", left)

    # Y24 temizlik API'si: alan silinince değerleri de silinir.
    code, _ = api("admin", "DELETE", f"{G}/custom-fields/{blood['id']}")
    left = psql(f"""SELECT count(*) FROM governance_custom_field_values WHERE "FieldId" = '{blood['id']}'""")
    check("Y24: alan silinince değerleri imha edildi", code == 204 and left == "0", (code, left))
finally:
    for e, lg in orig_lang.items():
        if lg:
            psql(f"""UPDATE notification_preferences SET "Language" = '{lg}' WHERE "TenantSlug" = 'demo' AND "EmployeeId" = '{e}'""")
    psql(f"""DELETE FROM notification_messages WHERE "TemplateCode" IN ('signature.otp','report.scheduled') AND "CreatedAt" >= '{T0.isoformat()}'
                AND "RecipientEmployeeId" IN ('{AYSE}','{MEHMET}')""")
    api("admin", "PUT", f"{G}/ai/settings", {"enabled": False, "allowPersonalData": False})
    cleanup()

print(f"\nFAILS: {len(FAIL)}")
for f in FAIL:
    print("  -", f)
sys.exit(1 if FAIL else 0)

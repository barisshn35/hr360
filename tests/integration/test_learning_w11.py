#!/usr/bin/env python3
# hr360-modules: learning  (kapalıysa scripts/test.sh bu betiği atlar)
"""Dalga 11 öğrenme ve gelişim: yetkinlik açığı → eğitim önerisi bağlantısı (82), kariyer yolları (83),
zorunlu eğitim son tarihi + İSG eğitimi hatırlatmaları ve gecikme panosu (84).

Ön koşul: HR360 çalışıyor, deploy/testing/chat-mock.yml katmanı açık (CERT_REMINDER_SECONDS=3),
scripts/sql/2026-10-24_learning_w11.sql uygulanmış. Test kayıtları "TEST11L" önekiyle işaretlidir.
"""
import datetime as dt
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(__file__))
from common import AYSE, FAIL, G, api, check  # noqa: E402

L = "/api/learning"
ROOT = os.path.join(os.path.dirname(__file__), "..", "..")
FAIL.clear()
TODAY = dt.date.today()


def psql(sql):
    out = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", "hr360_operational", "-Atc", sql],
                         capture_output=True, text=True, cwd=ROOT)
    return (out.stdout + out.stderr).strip()


def cleanup():
    psql("""DELETE FROM learning_due_reminders WHERE "SourceId" IN (
                SELECT e."Id" FROM learning_enrollments e JOIN learning_courses c ON c."Id" = e."CourseId" WHERE c."Title" LIKE 'TEST11L%'
                UNION SELECT "Id" FROM governance_osh_trainings WHERE "Topic" LIKE 'TEST11L%');
            DELETE FROM learning_career_paths WHERE "Name" LIKE 'TEST11L%';
            DELETE FROM learning_courses WHERE "Title" LIKE 'TEST11L%';
            DELETE FROM learning_competency_assessments WHERE "CompetencyId" IN (SELECT "Id" FROM learning_competencies WHERE "Name" LIKE 'TEST11L%');
            DELETE FROM learning_competencies WHERE "Name" LIKE 'TEST11L%';
            DELETE FROM governance_osh_trainings WHERE "Topic" LIKE 'TEST11L%';
            DELETE FROM notification_messages WHERE "Body" LIKE '%TEST11L%';""")


cleanup()
DEPT = psql("""SELECT "Id" FROM organization_departments WHERE "TenantSlug" = 'demo' AND "Name" = 'Mühendislik'""")
MEHMET = psql("""SELECT "HeadEmployeeId" FROM organization_departments WHERE "Id" = '%s'""" % DEPT)
AYSE_TITLE = psql(f"""SELECT "PositionTitle" FROM employee_assignments WHERE "EmployeeId" = '{AYSE}' AND "EffectiveTo" IS NULL LIMIT 1""")

try:
    # ------------------------------------------------------------------ 83) kariyer yolları
    code, comp = api("admin", "POST", f"{L}/competencies", {"name": "TEST11L Sistem tasarımı", "category": "Teknik"})
    check("Yetkinlik oluşturulur", code == 200, (code, comp))
    code, course = api("admin", "POST", f"{L}/courses", {"title": "TEST11L Sistem Tasarımı İleri", "durationHours": 6, "category": "Technical", "isMandatory": False})
    check("Eğitim oluşturulur", code in (200, 201), (code, course))
    api("admin", "PUT", f"{L}/courses/{course['id']}/competencies", [{"competencyId": comp["id"], "targetLevel": 5}])

    path_body = {"name": "TEST11L Mühendislik merdiveni", "description": "Test yolu", "steps": [
        {"positionTitle": AYSE_TITLE or "TEST11L Mühendis", "requirements": [{"competencyId": comp["id"], "requiredLevel": 2}]},
        {"positionTitle": "TEST11L Kıdemli Mühendis", "minMonths": 24, "requirements": [{"competencyId": comp["id"], "requiredLevel": 5}]},
    ]}
    code, _ = api("ayse", "POST", f"{L}/career-paths", path_body)
    check("Çalışan kariyer yolu tanımlayamaz", code == 403, code)
    bad = dict(path_body, steps=[path_body["steps"][0], dict(path_body["steps"][1], positionTitle=path_body["steps"][0]["positionTitle"].upper())])
    code, _ = api("admin", "POST", f"{L}/career-paths", bad)
    check("Aynı unvan bir yolda iki kez kullanılamaz", code == 400, code)
    code, path = api("admin", "POST", f"{L}/career-paths", path_body)
    check("İK kariyer yolu oluşturur (2 basamak)", code == 200 and len(path["steps"]) == 2 and path["steps"][1]["minMonths"] == 24, (code, path))
    code, _ = api("admin", "POST", f"{L}/career-paths", path_body)
    check("Aynı adla ikinci yol reddedilir", code == 409, code)
    code, lst = api("ayse", "GET", f"{L}/career-paths")
    check("Yollar herkese açık", code == 200 and any(p["id"] == path["id"] for p in lst), code)

    api("ayse", "POST", f"{L}/competencies/assessments", {"employeeId": AYSE, "competencyId": comp["id"], "level": 3, "note": "TEST11L"})
    code, prog = api("ayse", "GET", f"{L}/career-paths/progress/me")
    mine = next((p for p in (prog or {}).get("matched", []) if p["pathId"] == path["id"]), None)
    check("Çalışan kendi basamağını unvanından bulur", code == 200 and mine and mine["currentStepId"] == path["steps"][0]["id"], (code, prog))
    tgt = (mine or {}).get("target") or {}
    check("Sonraki basamak açığı ve hazırlık (%60)", tgt.get("positionTitle") == "TEST11L Kıdemli Mühendis" and tgt.get("readiness") == 60
          and tgt["items"][0]["gap"] == 2, tgt)
    check("Açığı kapatan eğitim önerilir", any(c["courseId"] == course["id"] for c in (tgt.get("courses") or [])), tgt.get("courses"))
    check("Bilgilendirme notu (otomatik karar yok)", "otomatik" in (prog or {}).get("notice", ""), prog)

    code, mp = api("mehmet", "GET", f"{L}/career-paths/progress/{AYSE}")
    check("Departman başkanı ekip üyesinin ilerlemesini görür", code == 200 and mp["employeeId"] == AYSE, code)
    aud = psql(f"""SELECT count(*) FROM audit_log WHERE "EntityType" = 'CareerProgress' AND "EntityId" = '{AYSE}' AND "Action" = 'SensitiveViewed'""")
    check("Başkasının ilerlemesini görüntüleme denetim kaydında", aud.isdigit() and int(aud) >= 1, aud)
    code, _ = api("ayse", "GET", f"{L}/career-paths/progress/{MEHMET}")
    check("Çalışan yöneticisinin ilerlemesini göremez", code == 404, code)

    upd = dict(path_body, steps=path_body["steps"] + [{"positionTitle": "TEST11L Baş Mühendis", "requirements": []}])
    code, up = api("admin", "PUT", f"{L}/career-paths/{path['id']}", upd)
    check("İK yolu basamaklarıyla günceller", code == 200 and len(up["steps"]) == 3, (code, up))

    # ------------------------------------------------------------------ 82) yetkinlik açığı → eğitim
    code, en = api("ayse", "POST", f"{L}/courses/{course['id']}/enroll", {"employeeId": AYSE, "dueOn": str(TODAY + dt.timedelta(days=3))})
    check("Çalışan önerilen eğitime kendini kaydeder", code in (200, 201), (code, en))
    check("Çalışanın kendi kaydında son tarih yok sayılır", en.get("dueOn") is None, en)

    # ------------------------------------------------------------------ 84) son tarih + hatırlatma
    code, mand = api("admin", "POST", f"{L}/courses", {"title": "TEST11L Bilgi Güvenliği", "durationHours": 1, "category": "Compliance", "isMandatory": True})
    check("Zorunlu eğitim oluşturulur", code in (200, 201), (code, mand))
    code, _ = api("ayse", "POST", f"{L}/learning-due/assign", {"courseId": mand["id"], "employeeIds": [AYSE], "dueOn": str(TODAY + dt.timedelta(days=5))})
    check("Çalışan eğitim atayamaz", code == 403, code)
    code, _ = api("mehmet", "POST", f"{L}/learning-due/assign", {"courseId": mand["id"], "employeeIds": [AYSE], "dueOn": str(TODAY - dt.timedelta(days=1))})
    check("Geçmiş son tarih reddedilir", code == 400, code)
    code, asg = api("mehmet", "POST", f"{L}/learning-due/assign", {"courseId": mand["id"], "employeeIds": [AYSE], "dueOn": str(TODAY + dt.timedelta(days=5))})
    check("Yönetici ekibine son tarihli eğitim atar", code == 200 and asg["created"] == 1, (code, asg))
    code, asg2 = api("mehmet", "POST", f"{L}/learning-due/assign", {"courseId": mand["id"], "employeeIds": [AYSE], "dueOn": str(TODAY + dt.timedelta(days=5))})
    check("Aynı atama tekrarlanınca değişmez", code == 200 and asg2["created"] == 0 and asg2["skipped"] == 1, asg2)
    n = psql(f"""SELECT count(*) FROM notification_messages WHERE "RecipientEmployeeId" = '{AYSE}' AND "TemplateCode" = 'learning.assigned' AND "Body" LIKE '%TEST11L Bilgi%'""")
    check("Atanan çalışana bildirim gider (bir kez)", n == "1", n)

    # Yönetici "departmandaki herkese" atar: kendisi aynı departmanda olsa da 403 yok, kendisi hedeflenmez.
    code, dcourse = api("admin", "POST", f"{L}/courses", {"title": "TEST11L Departman eğitimi", "durationHours": 1, "category": "Compliance", "isMandatory": True})
    code, dasg = api("mehmet", "POST", f"{L}/learning-due/assign", {"courseId": dcourse["id"], "departmentId": DEPT})
    check("Yönetici departmandaki herkese atar (kendisi hariç)", code == 200 and dasg["created"] >= 1, (code, dasg))
    self_enr = psql(f"""SELECT count(*) FROM learning_enrollments WHERE "CourseId" = '{dcourse['id']}' AND "EmployeeId" = '{MEHMET}'""")
    check("Yönetici kendine atama almaz", self_enr == "0", self_enr)
    code, people = api("mehmet", "GET", f"{L}/learning-due/assignable")
    ids = {p["id"] for p in (people or [])}
    other = psql(f"""SELECT a."EmployeeId" FROM employee_assignments a JOIN employee_employees e ON e."Id" = a."EmployeeId"
                     WHERE a."TenantSlug" = 'demo' AND a."EffectiveTo" IS NULL AND a."DepartmentId" <> '{DEPT}' AND e."Status" <> 'Terminated'
                       AND a."DepartmentId" NOT IN (SELECT "Id" FROM organization_departments WHERE "HeadEmployeeId" = '{MEHMET}') LIMIT 1""")
    check("'Bir çalışana' seçicisi: yöneticiye yalnızca kendi departmanı, kendisi hariç",
          code == 200 and AYSE in ids and MEHMET not in ids and (not other or other not in ids), (code, len(ids), other))
    code, _ = api("ayse", "GET", f"{L}/learning-due/assignable")
    check("Çalışan atanabilir kişi listesini göremez", code == 403, code)
    code, people = api("admin", "GET", f"{L}/learning-due/assignable")
    check("İK tüm aktif çalışanları görür", code == 200 and AYSE in {p["id"] for p in people} and len(people) >= len(ids), code)

    tr_date = TODAY - dt.timedelta(days=365) + dt.timedelta(days=20)
    code, osh = api("admin", "POST", f"{G}/osh/trainings", {"topic": "TEST11L Yüksekte çalışma", "trainingDate": str(tr_date), "durationHours": 2,
                                                            "validityMonths": 12, "trainer": "İSG uzmanı", "participantIds": [AYSE]})
    check("İSG eğitimi kaydı (yaklaşık 20 gün sonra bitiyor)", code == 200 and osh.get("expiresOn"), (code, osh))

    code, me = api("ayse", "GET", f"{L}/learning-due/me")
    kinds = {(i["kind"], i["title"]) for i in (me or {}).get("items", [])}
    check("Çalışan son tarihli eğitimini ve İSG eğitimini görür",
          code == 200 and ("Training", "TEST11L Bilgi Güvenliği") in kinds and ("Osh", "TEST11L Yüksekte çalışma") in kinds, (code, kinds))
    code, _ = api("ayse", "GET", f"{L}/learning-due/overdue")
    check("Çalışan gecikme panosunu göremez", code == 403, code)
    code, ov = api("mehmet", "GET", f"{L}/learning-due/overdue")
    check("Yönetici ekibinin yaklaşan kalemlerini görür",
          code == 200 and any(i["employeeId"] == AYSE and i["title"] == "TEST11L Bilgi Güvenliği" and i["state"] == "DueSoon" for i in ov["items"]), code)
    code, ova = api("admin", "GET", f"{L}/learning-due/overdue?departmentId={DEPT}&withinDays=30")
    check("İK departman seçerek panoyu görür (özet)", code == 200 and ova["summary"]["dueSoon"] >= 2, (code, ova and ova.get("summary")))

    enr_id = psql(f"""SELECT e."Id" FROM learning_enrollments e WHERE e."CourseId" = '{mand['id']}' AND e."EmployeeId" = '{AYSE}'""")
    deadline = time.time() + 40
    want = f"""SELECT count(*) FROM learning_due_reminders WHERE "SourceId" IN ('{enr_id}', '{osh.get('id')}')"""
    while time.time() < deadline and psql(want) != "4":
        time.sleep(2)
    rows = psql(f"""SELECT string_agg("SourceType" || ':' || "Kind" || ':' || CASE WHEN "RecipientEmployeeId" = '{AYSE}' THEN 'emp' ELSE 'mgr' END, ',')
                   FROM learning_due_reminders WHERE "SourceId" IN ('{enr_id}', '{osh.get('id')}')""")
    check("Hatırlatmalar: eğitim 7 gün, İSG 30 gün; çalışana ve yöneticisine", sorted(rows.split(",")) == ["Osh:D30:emp", "Osh:D30:mgr", "Training:D7:emp", "Training:D7:mgr"], rows)
    time.sleep(7)
    check("Aynı hatırlatma ikinci kez gitmez", psql(want) == "4", psql(want))
    mgr = psql(f"""SELECT count(*) FROM notification_messages WHERE "RecipientEmployeeId" = '{MEHMET}' AND "TemplateCode" LIKE 'learning.due.%' AND "Body" LIKE '%TEST11L%'""")
    check("Yönetici bildirimleri yazıldı", mgr == "2", mgr)

    code, _ = api("admin", "DELETE", f"{L}/career-paths/{path['id']}")
    check("İK kariyer yolunu siler", code == 204, code)
finally:
    cleanup()

print(f"FAILS: {len(FAIL)}")
for f in FAIL:
    print("  -", f)
sys.exit(1 if FAIL else 0)

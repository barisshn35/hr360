#!/usr/bin/env python3
"""Dalga 5c operasyon eklentileri:
G14 işe alışma rol şablonları + yol arkadaşı (buddy) + ilk gün karşılama (arka plan işi, tekil),
G15 offboarding: zimmet iade kontrolü + İK istisnası, Keycloak hesabını kapatma (atılabilir test
     kullanıcısıyla), imha planı (TerminatedEmployees saklama süresi), denetim kaydı,
G16 zimmet QR okutma (İK/BT ve yalnızca kendi zimmetini gören çalışan), iade hatırlatma işi, bakım,
G18 anket anonimliği (en küçük grup 5, ikincil gizleme), eNPS eğilimi, yerel duygu özeti,
G6  vardiya tercihleri + takas (aynı ekip, çakışma, 11 saat dinlenme, haftalık 45 saat, onay),
G7  puantaj geç kalma / erken çıkış / olası fazla mesai ve fazla mesai talebi taslağı.

Ön koşul: HR360 çalışıyor, deploy/testing/chat-mock.yml katmanı açık (ONBOARDING_JOBS_SECONDS kısa).
"""
import datetime as dt
import json
import os
import subprocess
import sys
import time
import uuid

sys.path.insert(0, os.path.dirname(__file__))
from common import AYSE, FAIL, api, check  # noqa: E402

ROOT = os.path.join(os.path.dirname(__file__), "..", "..")
FAIL.clear()
RUN = uuid.uuid4().hex[:6].upper()
MEHMET = "3ab24e3e-cb06-40ab-934c-9ff7eab91fb6"
ON = "/api/onboarding"
EN = "/api/engagement"
TS = "/api/timeshift"
TODAY = dt.date.today()
START = dt.datetime.now(dt.timezone.utc) - dt.timedelta(seconds=5)
STARTS = START.strftime("%Y-%m-%d %H:%M:%S+00")


def psql(sql, db="hr360_operational"):
    out = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", db, "-Atc", sql],
                         capture_output=True, text=True, cwd=ROOT)
    return (out.stdout + out.stderr).strip()


def kcadm(*args):
    script = ('/opt/keycloak/bin/kcadm.sh config credentials --config /tmp/kc-ops.cfg --server http://localhost:8080/auth '
              '--realm master --user "$KEYCLOAK_ADMIN" --password "$KEYCLOAK_ADMIN_PASSWORD" >/dev/null 2>&1 && '
              '/opt/keycloak/bin/kcadm.sh ' + " ".join(args) + ' --config /tmp/kc-ops.cfg -r hr360')
    return subprocess.run(["docker", "exec", "hr360-keycloak-1", "sh", "-c", script], capture_output=True, text=True)


def notes(recipient, code, like=None):
    extra = f" AND \"Body\" LIKE '%{like}%'" if like else ""
    return int(psql(f"""SELECT count(*) FROM notification_messages WHERE "RecipientEmployeeId" = '{recipient}'
                        AND "TemplateCode" = '{code}' AND "CreatedAt" >= '{STARTS}'{extra}""") or 0)


def wait(pred, timeout=25):
    end = time.time() + timeout
    while time.time() < end:
        if pred():
            return True
        time.sleep(1)
    return pred()


DEPT = psql("""SELECT "Id" FROM organization_departments WHERE "TenantSlug" = 'demo' AND "Name" = 'Mühendislik' LIMIT 1""")
settings_existed = psql("""SELECT count(*) FROM onboarding_settings WHERE "TenantSlug" = 'demo'""") == "1"
orig_settings = psql("""SELECT row_to_json(s) FROM onboarding_settings s WHERE "TenantSlug" = 'demo'""")
ts_settings_existed = psql("""SELECT count(*) FROM timeshift_settings WHERE "TenantSlug" = 'demo'""") == "1"
pref_existed = psql(f"""SELECT count(*) FROM timeshift_shift_preferences WHERE "EmployeeId" = '{AYSE}'""") == "1"
created = {"plans": [], "templates": [], "assets": [], "surveys": [], "shifts": [], "swaps": [], "punches": [], "overtime": [],
           "employee": None, "kc_user": None, "case": None}


def cleanup():
    for p in created["plans"]:
        psql(f"""DELETE FROM onboarding_plans WHERE "Id" = '{p}'""")
    for t in created["templates"]:
        psql(f"""DELETE FROM onboarding_task_templates WHERE "Id" = '{t}'""")
    for a in created["assets"]:
        psql(f"""DELETE FROM onboarding_assets WHERE "Id" = '{a}'""")
    psql("""DELETE FROM onboarding_assets WHERE "AssetTag" LIKE 'TEST-QR-%' AND "TenantSlug" = 'demo'""")
    if not settings_existed:
        psql("""DELETE FROM onboarding_settings WHERE "TenantSlug" = 'demo'""")
    elif orig_settings:
        o = json.loads(orig_settings)
        def q(v):
            return "NULL" if v is None else "'" + str(v).replace("'", "''") + "'"
        psql(f"""UPDATE onboarding_settings SET "WelcomeSubject" = {q(o['WelcomeSubject'])}, "WelcomeBody" = {q(o['WelcomeBody'])},
                 "HrContactEmployeeId" = {q(o['HrContactEmployeeId'])}, "ReminderDaysBefore" = {o['ReminderDaysBefore']} WHERE "TenantSlug" = 'demo'""")
    for s in created["surveys"]:
        psql(f"""DELETE FROM engagement_surveys WHERE "Id" = '{s}'""")
    if created["case"]:
        psql(f"""DELETE FROM engagement_offboarding_cases WHERE "Id" = '{created['case']}'""")
    for sw in created["swaps"]:
        psql(f"""DELETE FROM timeshift_swap_requests WHERE "Id" = '{sw}'""")
    psql(f"""DELETE FROM timeshift_swap_requests WHERE "Note" LIKE 'TEST {RUN}%'""")
    for s in created["shifts"]:
        psql(f"""DELETE FROM timeshift_shifts WHERE "Id" = '{s}'""")
    for p in created["punches"]:
        psql(f"""DELETE FROM timeshift_clock_punches WHERE "Id" = '{p}'""")
    for o in created["overtime"]:
        wf = psql(f"""SELECT "WorkflowRequestId" FROM timeshift_overtime_requests WHERE "Id" = '{o}'""")
        psql(f"""DELETE FROM timeshift_overtime_requests WHERE "Id" = '{o}'""")
        if wf:
            psql(f"""DELETE FROM workflow_approval_steps WHERE "WorkflowRequestId" = '{wf}'; DELETE FROM workflow_requests WHERE "Id" = '{wf}'""")
    if not pref_existed:
        psql(f"""DELETE FROM timeshift_shift_preferences WHERE "EmployeeId" = '{AYSE}'""")
    if not ts_settings_existed:
        psql("""DELETE FROM timeshift_settings WHERE "TenantSlug" = 'demo'""")
    if created["kc_user"]:
        kcadm("delete", f"users/{created['kc_user']}")
    if created["employee"]:
        e = created["employee"]
        psql(f"""DELETE FROM notification_messages WHERE "RecipientEmployeeId" = '{e}'; DELETE FROM employee_employees WHERE "Id" = '{e}'""")
    psql(f"""DELETE FROM notification_messages WHERE "CreatedAt" >= '{STARTS}' AND ("TemplateCode" LIKE 'onboarding.%'
             OR "TemplateCode" LIKE 'asset.return.%' OR "TemplateCode" LIKE 'shift.swap.%')
             AND "RecipientEmployeeId" IN ('{AYSE}', '{MEHMET}')""")


try:
    # ================================================================== G14 şablonlar, buddy, karşılama
    code, _ = api("ayse", "PUT", f"{ON}/onboarding-config/settings", {"welcomeSubject": "x"})
    check("G14 çalışan karşılama ayarını değiştiremez", code == 403, code)
    code, st = api("admin", "PUT", f"{ON}/onboarding-config/settings", {
        "welcomeSubject": f"TEST {RUN} Hoş geldin {{ad}}",
        "welcomeBody": "Merhaba {ad}, ilk günün {baslangic}. Yöneticin {yonetici}, yol arkadaşın {buddy}. Buluşma: {konum}.",
        "hrContactEmployeeId": MEHMET, "reminderDaysBefore": 3})
    check("G14 İK karşılama şablonu ve İK sorumlusunu kaydeder", code == 200, (code, st))

    items = [
        {"title": f"TEST {RUN} Kod deposu erişimi", "category": "IT", "ownerRole": "IT", "offsetDays": 0},
        {"title": f"TEST {RUN} Mimari tanıtım", "category": "Training", "ownerRole": "Manager", "offsetDays": 2},
        {"title": f"TEST {RUN} Geliştirme ortamı kurulumu", "category": "IT", "ownerRole": "Employee", "offsetDays": 1},
        {"title": f"TEST {RUN} Kod inceleme eşliği", "category": "Training", "ownerRole": "Buddy", "offsetDays": 3},
    ]
    code, _ = api("ayse", "POST", f"{ON}/onboarding-config/templates", {"name": "TEST x", "isActive": True, "items": items})
    check("G14 çalışan şablon oluşturamaz", code == 403, code)
    code, _ = api("admin", "POST", f"{ON}/onboarding-config/templates", {"name": "TEST y", "isActive": True,
                  "items": [{"title": "a", "category": "IT", "ownerRole": "Patron", "offsetDays": 0}]})
    check("G14 geçersiz görev sahibi rolü reddedilir", code == 400, code)
    code, t1 = api("admin", "POST", f"{ON}/onboarding-config/templates", {"name": f"TEST {RUN} Yazılım", "positionTitle": "Yazılım Mühendisi",
                   "departmentId": DEPT, "isActive": True, "items": items})
    check("G14 unvan+departman şablonu oluşturulur", code == 200 and len(t1["items"]) == 4, (code, t1))
    created["templates"].append(t1["id"])
    code, t2 = api("admin", "POST", f"{ON}/onboarding-config/templates", {"name": f"TEST {RUN} Satış", "positionTitle": "Satış Temsilcisi",
                   "isActive": True, "items": [{"title": f"TEST {RUN} CRM eğitimi", "category": "Training", "ownerRole": "HR", "offsetDays": 0}]})
    created["templates"].append(t2["id"])

    code, plan = api("admin", "POST", f"{ON}/onboarding-plans", {"employeeId": AYSE, "startDate": TODAY.isoformat(), "useDefaultTasks": False,
                     "buddyEmployeeId": MEHMET, "location": f"TEST {RUN} İstanbul ofis, 3. kat"})
    check("G14 plan oluşturulur", code == 201, (code, plan))
    created["plans"].append(plan["id"])
    tasks = {t["title"].replace(f"TEST {RUN} ", ""): t for t in plan["tasks"]}
    check("G14 eşleşen şablon otomatik uygulanır, eşleşmeyen uygulanmaz",
          set(tasks) == {"Kod deposu erişimi", "Mimari tanıtım", "Geliştirme ortamı kurulumu", "Kod inceleme eşliği"}
          and f"TEST {RUN} Yazılım" in (plan.get("appliedTemplates") or "") and "Satış" not in (plan.get("appliedTemplates") or ""), plan)
    check("G14 görev sahipleri: yönetici→bölüm başı, çalışan→kendisi, buddy→yol arkadaşı, BT→kuyruk",
          tasks["Mimari tanıtım"]["assigneeEmployeeId"] == MEHMET and tasks["Geliştirme ortamı kurulumu"]["assigneeEmployeeId"] == AYSE
          and tasks["Kod inceleme eşliği"]["assigneeEmployeeId"] == MEHMET and tasks["Kod deposu erişimi"]["assigneeEmployeeId"] is None
          and tasks["Kod inceleme eşliği"]["ownerRole"] == "Buddy", tasks)
    check("G14 göreli teslim tarihleri", tasks["Mimari tanıtım"]["dueDate"] == (TODAY + dt.timedelta(days=2)).isoformat(), tasks["Mimari tanıtım"])
    check("G14 yol arkadaşına uygulama içi bildirim", wait(lambda: notes(MEHMET, "onboarding.buddy") == 1, 5), notes(MEHMET, "onboarding.buddy"))

    code, p2 = api("admin", "PUT", f"{ON}/onboarding-plans/{plan['id']}/buddy", {"buddyEmployeeId": None})
    bt = [t for t in p2["tasks"] if t["ownerRole"] == "Buddy"]
    check("G14 yol arkadaşı kaldırılınca buddy görevleri boşa düşer", code == 200 and all(t["assigneeEmployeeId"] is None for t in bt), (code, bt))
    code, p2 = api("admin", "PUT", f"{ON}/onboarding-plans/{plan['id']}/buddy", {"buddyEmployeeId": AYSE})
    check("G14 yeni çalışan kendi yol arkadaşı olamaz", code == 400, code)
    code, p2 = api("admin", "PUT", f"{ON}/onboarding-plans/{plan['id']}/buddy", {"buddyEmployeeId": MEHMET})
    check("G14 yol arkadaşı yeniden atanır ve bildirilir", code == 200 and all(t["assigneeEmployeeId"] == MEHMET for t in p2["tasks"] if t["ownerRole"] == "Buddy")
          and wait(lambda: notes(MEHMET, "onboarding.buddy") == 2, 5), code)

    code, pv = api("admin", "GET", f"{ON}/onboarding-plans/{plan['id']}/welcome-preview")
    check("G14 karşılama önizlemesi şablondan", code == 200 and pv["subject"] == f"TEST {RUN} Hoş geldin Ayşe" and "Mehmet Demir" in pv["body"], (code, pv))
    # Yarın başlayan ikinci plan: bugün karşılama gönderilmez.
    code, plan2 = api("admin", "POST", f"{ON}/onboarding-plans", {"employeeId": AYSE, "startDate": (TODAY + dt.timedelta(days=1)).isoformat(),
                      "useDefaultTasks": False, "applyTemplates": False})
    created["plans"].append(plan2["id"])

    ok = wait(lambda: notes(AYSE, "onboarding.welcome", f"TEST {RUN} İstanbul") >= 2, 25)
    inapp = int(psql(f"""SELECT count(*) FROM notification_messages WHERE "RecipientEmployeeId" = '{AYSE}' AND "TemplateCode" = 'onboarding.welcome'
                         AND "Channel" = 'InApp' AND "CreatedAt" >= '{STARTS}' AND "Body" LIKE '%TEST {RUN}%'"""))
    mail = psql(f"""SELECT "RecipientEmail" || '|' || "Subject" FROM notification_messages WHERE "RecipientEmployeeId" = '{AYSE}' AND "TemplateCode" = 'onboarding.welcome'
                    AND "Channel" = 'Email' AND "CreatedAt" >= '{STARTS}' AND "Body" LIKE '%TEST {RUN}%'""")
    check("G14 başlangıç gününde uygulama içi + e-posta karşılama", ok and inapp == 1 and mail == f"ayse.yilmaz@demo.hr360|TEST {RUN} Hoş geldin Ayşe", (inapp, mail))
    body = psql(f"""SELECT "Body" FROM notification_messages WHERE "RecipientEmployeeId" = '{AYSE}' AND "TemplateCode" = 'onboarding.welcome'
                    AND "Channel" = 'InApp' AND "Body" LIKE '%TEST {RUN}%' LIMIT 1""")
    check("G14 karşılama metni yer tutucularla (yönetici, buddy, konum, tarih)", "Mehmet Demir" in body and TODAY.strftime("%d.%m.%Y") in body
          and f"TEST {RUN} İstanbul ofis" in body, body)
    time.sleep(8)
    check("G14 karşılama tekil (iş birkaç kez çalıştı, yine 1+1 ileti)", notes(AYSE, "onboarding.welcome", f"TEST {RUN}") == 2,
          notes(AYSE, "onboarding.welcome", f"TEST {RUN}"))
    check("G14 yarın başlayan plana bugün karşılama gönderilmez",
          psql(f"""SELECT "WelcomeSentAt" IS NULL FROM onboarding_plans WHERE "Id" = '{plan2['id']}'""") == "t")

    # ================================================================== G16 QR, hatırlatma, bakım
    def mk_asset(n):
        c, a = api("admin", "POST", f"{ON}/assets", {"assetTag": f"TEST-QR-{RUN}-{n}", "type": "Laptop", "model": "TEST Model"})
        created["assets"].append(a["id"])
        return c, a

    code, a1 = mk_asset(1)
    check("G16 demirbaşa opak QR kodu verilir", code == 201 and len(a1.get("qrCode") or "") == 16 and RUN not in a1["qrCode"], (code, a1))
    _, a2 = mk_asset(2)
    _, a3 = mk_asset(3)
    code, _ = api("admin", "POST", f"{ON}/assets/{a1['id']}/assign", {"employeeId": AYSE, "assignedOn": (TODAY - dt.timedelta(days=5)).isoformat(),
                  "expectedReturnOn": (TODAY + dt.timedelta(days=1)).isoformat()})
    check("G16 beklenen iade tarihiyle zimmet", code == 201, code)
    code, _ = api("admin", "POST", f"{ON}/assets/{a2['id']}/assign", {"employeeId": AYSE, "assignedOn": (TODAY - dt.timedelta(days=10)).isoformat(),
                  "expectedReturnOn": (TODAY - dt.timedelta(days=1)).isoformat()})
    code2, _ = api("admin", "POST", f"{ON}/assets/{a3['id']}/assign", {"employeeId": MEHMET, "assignedOn": TODAY.isoformat()})
    check("G16 zimmet atamaları", code == 201 and code2 == 201, (code, code2))

    code, sc = api("admin", "GET", f"{ON}/assets/scan?kod={a1['qrCode'].lower()}")
    check("G16 İK okutunca demirbaş ve zimmetli kişi görünür", code == 200 and sc["holder"]["name"] == "Ayşe Yılmaz" and sc["asset"]["assetTag"] == f"TEST-QR-{RUN}-1", (code, sc))
    check("G16 kimin elinde olduğu görüntülemesi denetim kaydına yazılır",
          psql(f"""SELECT count(*) FROM audit_log WHERE "EntityType" = 'Asset' AND "EntityId" = '{a1['id']}' AND "Action" = 'SensitiveViewed'""") != "0")
    code, sc = api("ayse", "GET", f"{ON}/assets/scan?kod={a1['qrCode']}")
    check("G16 çalışan kendi zimmetini okutabilir", code == 200 and sc["holder"]["employeeId"] == AYSE and sc["canManage"] is False, (code, sc))
    code, _ = api("ayse", "GET", f"{ON}/assets/scan?kod={a3['qrCode']}")
    check("G16 çalışan başkasının zimmetini göremez (404)", code == 404, code)
    code, _ = api("mehmet", "GET", f"{ON}/assets/scan?kod={a1['qrCode']}")
    check("G16 zimmet yetkisi olmayan yönetici başkasınınkini göremez", code == 404, code)
    code, _ = api("admin", "GET", f"{ON}/assets/scan?kod=ZZZZZZZZZZZZZZZZ")
    check("G16 bilinmeyen etiket 404", code == 404, code)

    ok = wait(lambda: notes(AYSE, "asset.return.reminder", f"TEST-QR-{RUN}-1") == 1 and notes(AYSE, "asset.return.overdue", f"TEST-QR-{RUN}-2") == 1
              and notes(MEHMET, "asset.return.reminder.hr", f"TEST-QR-{RUN}-1") == 1 and notes(MEHMET, "asset.return.overdue.hr", f"TEST-QR-{RUN}-2") == 1, 25)
    check("G16 iade hatırlatması (yaklaşan + geciken) sahibine ve İK sorumlusuna", ok,
          (notes(AYSE, "asset.return.reminder"), notes(AYSE, "asset.return.overdue"), notes(MEHMET, "asset.return.reminder.hr"), notes(MEHMET, "asset.return.overdue.hr")))
    time.sleep(7)
    check("G16 hatırlatmalar tekil", notes(AYSE, "asset.return.reminder", f"TEST-QR-{RUN}") == 1 and notes(AYSE, "asset.return.overdue", f"TEST-QR-{RUN}") == 1
          and notes(MEHMET, "asset.return.overdue.hr", f"TEST-QR-{RUN}") == 1)
    code, due = api("admin", "GET", f"{ON}/assets/returns-due?days=7")
    mine = [r for r in (due or []) if r["assetTag"] and r["assetTag"].startswith(f"TEST-QR-{RUN}")]
    check("G16 iadesi yaklaşan/geciken listesi", code == 200 and {r["assetTag"][-1]: r["overdue"] for r in mine} == {"1": False, "2": True}, (code, mine))
    code, _ = api("ayse", "GET", f"{ON}/assets/returns-due")
    check("G16 çalışan iade listesini göremez", code == 403, code)

    code, m = api("admin", "POST", f"{ON}/assets/{a1['id']}/maintenance", {"date": (TODAY - dt.timedelta(days=30)).isoformat(), "type": "Periodic",
                  "cost": 1500.5, "vendor": "TEST Servis", "notes": "Fan temizliği", "nextMaintenanceOn": (TODAY + dt.timedelta(days=10)).isoformat()})
    check("G16 bakım kaydı (tarih, tür, maliyet, servis, sonraki bakım)", code == 200 and m["cost"] == 1500.5, (code, m))
    code, _ = api("admin", "POST", f"{ON}/assets/{a1['id']}/maintenance", {"date": TODAY.isoformat(), "type": "Repair",
                  "nextMaintenanceOn": (TODAY - dt.timedelta(days=1)).isoformat()})
    check("G16 sonraki bakım bakım tarihinden önce olamaz", code == 400, code)
    code, _ = api("ayse", "POST", f"{ON}/assets/{a1['id']}/maintenance", {"date": TODAY.isoformat(), "type": "Repair"})
    check("G16 çalışan bakım kaydı giremez", code == 403, code)
    _, d30 = api("admin", "GET", f"{ON}/assets/maintenance/due?days=30")
    _, d5 = api("admin", "GET", f"{ON}/assets/maintenance/due?days=5")
    check("G16 bakımı gelenler listesi", any(x["assetId"] == a1["id"] for x in d30) and not any(x["assetId"] == a1["id"] for x in d5), (d30, d5))
    code, er = api("admin", "PUT", f"{ON}/assets/{a1['id']}/expected-return", {"expectedReturnOn": (TODAY + dt.timedelta(days=30)).isoformat()})
    check("G16 beklenen iade tarihi güncellenir, hatırlatma sıfırlanır", code == 200 and er["reminderBeforeSentAt"] is None, (code, er))

    # ================================================================== G15 offboarding
    code, emp = api("admin", "POST", "/api/employee/employees", {"firstName": "TEST", "lastName": f"Ayrilan {RUN}",
                    "email": f"test.ayrilan.{RUN.lower()}@demo.hr360", "hireDate": "2023-01-02"})
    check("G15 hazırlık: atılabilir test çalışanı", code == 201, (code, emp))
    created["employee"] = emp["id"]
    E = emp["id"]
    api("admin", "POST", f"/api/employee/employees/{E}/assignments", {"departmentId": DEPT, "positionTitle": "TEST", "effectiveFrom": "2023-01-02"})
    code, inv = api("admin", "POST", f"/api/tenant/my-tenant/members/{E}/invite")
    check("G15 hazırlık: test çalışanına Keycloak hesabı (ekip üyeleri API'si)", code == 200 and inv.get("keycloakUserId"), (code, inv))
    created["kc_user"] = inv.get("keycloakUserId")
    K = created["kc_user"]
    check("G15 hazırlık: hesap açık", psql(f"SELECT enabled FROM user_entity WHERE id = '{K}'", db="keycloak") == "t")
    _, a4 = mk_asset(4)
    api("admin", "POST", f"{ON}/assets/{a4['id']}/assign", {"employeeId": E, "assignedOn": TODAY.isoformat()})

    code, case = api("admin", "POST", f"{EN}/offboarding", {"employeeId": E, "lastWorkingDay": TODAY.isoformat(), "reason": "Resignation"})
    check("G15 süreç başlar, açık zimmet iade listesinde", code == 200 and [a["assetTag"] for a in case["assetChecks"]] == [f"TEST-QR-{RUN}-4"]
          and case["assetChecks"][0]["resolution"] == "Open", (code, case))
    created["case"] = case["id"]
    C = case["id"]
    months = int(psql("""SELECT coalesce((SELECT "RetentionMonths" FROM governance_retention_policies WHERE "TenantSlug" = 'demo' AND "Category" = 'TerminatedEmployees'), 120)"""))
    y, mo = divmod(TODAY.month - 1 + months, 12)
    import calendar
    planned = dt.date(TODAY.year + y, mo + 1, min(TODAY.day, calendar.monthrange(TODAY.year + y, mo + 1)[1]))
    check("G15 imha planı: TerminatedEmployees saklama süresi kadar sonrası", case["retentionMonths"] == months and case["plannedAnonymizationOn"] == planned.isoformat(),
          (case["retentionMonths"], case["plannedAnonymizationOn"], planned))
    # Dalga 5: süreç yönetimi yalnızca İK; departman başı (Mehmet) ekibindeki süreci görür,
    # yalnızca sorumlusu "Yönetici" olan adımı işaretler.
    code, _ = api("mehmet", "POST", f"{EN}/offboarding", {"employeeId": E, "lastWorkingDay": TODAY.isoformat(), "reason": "Resignation"})
    check("G15 yönetici süreç başlatamaz", code == 403, code)
    code, lst = api("mehmet", "GET", f"{EN}/offboarding")
    check("G15 departman başı ekibindeki süreci görür", code == 200 and any(x["id"] == C for x in lst), code)
    code, _ = api("mehmet", "PATCH", f"{EN}/offboarding/{C}/checklist/sgk", {"done": True})
    check("G15 yönetici İK adımını işaretleyemez", code == 403, code)
    code, _ = api("mehmet", "PATCH", f"{EN}/offboarding/{C}/checklist/handover", {"done": True})
    check("G15 yönetici devir-teslim adımını işaretler", code == 200, code)
    for path in ("complete", "cancel"):
        code, _ = api("mehmet", "POST", f"{EN}/offboarding/{C}/{path}")
        check(f"G15 yönetici {path} yapamaz", code == 403, code)
    for it in case["checklist"]:
        if it["key"] == "exit-interview":
            api("admin", "PUT", f"{EN}/offboarding/{C}/exit-interview", {"interview": {"primaryReason": "Diğer"}, "rehireEligible": True})
        else:
            api("admin", "PATCH", f"{EN}/offboarding/{C}/checklist/{it['key']}", {"done": True})
    code, r = api("admin", "POST", f"{EN}/offboarding/{C}/complete")
    check("G15 iade edilmemiş zimmet varken tamamlanamaz", code == 400 and r.get("code") == "assets_open", (code, r))
    asg = a4["id"]
    assignment_id = psql(f"""SELECT "Id" FROM onboarding_asset_assignments WHERE "AssetId" = '{asg}' AND "ReturnedOn" IS NULL""")
    code, _ = api("mehmet", "PATCH", f"{EN}/offboarding/{C}/assets/{assignment_id}", {"resolution": "Lost", "note": "TEST kayıp bildirimi"})
    check("G15 zimmet istisnasını yalnızca İK işaretler", code == 403, code)
    code, _ = api("admin", "PATCH", f"{EN}/offboarding/{C}/assets/{assignment_id}", {"resolution": "Lost", "note": "x"})
    check("G15 istisna gerekçesiz olmaz", code == 400, code)
    code, r = api("admin", "PATCH", f"{EN}/offboarding/{C}/assets/{assignment_id}", {"resolution": "Lost", "note": "TEST çalışan kaybettiğini bildirdi"})
    check("G15 İK istisnası: kayıp + gerekçe", code == 200 and r["assetChecks"][0]["resolution"] == "Lost" and not r.get("warning"), (code, r))
    check("G15 istisna zimmet kaydına da işlenir (onboarding: Lost, atama kapalı)",
          psql(f"""SELECT a."Status" || '|' || (x."ReturnedOn" IS NOT NULL) FROM onboarding_assets a JOIN onboarding_asset_assignments x ON x."AssetId" = a."Id"
                   WHERE a."Id" = '{asg}'""") == "Lost|true")
    code, r = api("admin", "POST", f"{EN}/offboarding/{C}/complete")
    check("G15 tamamlanır; hesap kapatılır", code == 200 and r["accountStatus"] == "Disabled" and r["plannedAnonymizationOn"] == planned.isoformat(), (code, r))
    check("G15 Keycloak hesabı kapalı", wait(lambda: psql(f"SELECT enabled FROM user_entity WHERE id = '{K}'", db="keycloak") == "f", 10),
          psql(f"SELECT enabled FROM user_entity WHERE id = '{K}'", db="keycloak"))
    code, kcu = 0, kcadm("get", f"users/{K}", "--fields", "email,firstName")
    check("G15 hesap kapatma e-posta/ad niteliklerini silmez", f"test.ayrilan.{RUN.lower()}@demo.hr360" in kcu.stdout, kcu.stdout[:200])
    check("G15 çalışan durumu Ayrıldı", psql(f"""SELECT "Status" FROM employee_employees WHERE "Id" = '{E}'""") == "Terminated")
    code, c2 = api("admin", "GET", f"{EN}/offboarding/{C}")
    check("G15 kayıtta hesap durumu ve imha tarihi görünür", code == 200 and c2["accountStatus"] == "Disabled" and c2["accountDisabledAt"]
          and c2["plannedAnonymizationOn"] == planned.isoformat(), c2)
    acts = set(psql(f"""SELECT DISTINCT "Action" FROM audit_log WHERE "EntityType" = 'OffboardingCase' AND "EntityId" = '{C}'""").split())
    check("G15 her adım denetim kaydında", {"RetentionPlanned", "AssetOverride", "AssetsVerified", "AccountDisabled", "Completed"} <= acts, acts)
    wget = subprocess.run(["docker", "exec", "hr360-gateway-1", "wget", "-qO-", "--header", "X-Internal-Token: yanlis", "--header", "Content-Type: application/json",
                           "--post-data", json.dumps({"tenantSlug": "demo", "keycloakUserId": "e093f5bb-67a4-4061-acdc-ce0c8860a48b"}),
                           "http://tenant-service:8080/internal/users/disable"], capture_output=True, text=True)
    check("G15 iç uç yanlış jetonu reddeder (404)", "404" in wget.stderr and psql("SELECT enabled FROM user_entity WHERE id = 'e093f5bb-67a4-4061-acdc-ce0c8860a48b'", db="keycloak") == "t",
          wget.stderr[:200])
    wget = subprocess.run(["docker", "exec", "hr360-gateway-1", "wget", "-qO-", "--header", "Content-Type: application/json", "--post-data",
                           json.dumps({"tenantSlug": "demo", "keycloakUserId": "e093f5bb-67a4-4061-acdc-ce0c8860a48b"}),
                           "http://tenant-service:8080/internal/users/disable"], capture_output=True, text=True)
    check("G15 iç uç jetonsuz çağrıyı reddeder", "404" in wget.stderr, wget.stderr[:200])
    code, body = api("admin", "POST", "/internal/users/disable", {"tenantSlug": "demo", "keycloakUserId": "e093f5bb-67a4-4061-acdc-ce0c8860a48b"})
    check("G15 iç uç ağ geçidinden erişilemez", not (isinstance(body, dict) and "disabled" in body)
          and psql("SELECT enabled FROM user_entity WHERE id = 'e093f5bb-67a4-4061-acdc-ce0c8860a48b'", db="keycloak") == "t", (code, str(body)[:100]))

    # ================================================================== G18 anket anonimliği, eNPS, duygu
    def mk_survey(title, closes=None):
        c, s = api("admin", "POST", f"{EN}/surveys", {"title": title, "kind": "eNPS", "isAnonymous": True, "closesAt": closes, "questions": [
            {"id": "enps", "text": "Önerir misiniz?", "type": "Nps"},
            {"id": "scale", "text": "Destek alıyorum", "type": "Scale"},
            {"id": "comment", "text": "Yorum", "type": "Text", "required": False},
            {"id": "extra", "text": "Ek yorum", "type": "Text", "required": False}]})
        created["surveys"].append(s["id"])
        api("admin", "PATCH", f"{EN}/surveys/{s['id']}", {"status": "Open"})
        return s["id"]

    def add_responses(sid, rows):
        vals = []
        for i, (dept, nps, text, extra) in enumerate(rows):
            ans = [{"QuestionId": "enps", "Score": nps}, {"QuestionId": "scale", "Score": 4}]
            if text:
                ans.append({"QuestionId": "comment", "Text": text})
            if extra:
                ans.append({"QuestionId": "extra", "Text": extra})
            d = "NULL" if dept is None else f"'{dept}'"
            vals.append(f"""('{uuid.uuid4()}','demo','{sid}','TEST{RUN}{i}{uuid.uuid4().hex[:8]}',{d},'{json.dumps(ans, ensure_ascii=False).replace("'", "''")}'::jsonb,now())""")
        psql(f"""INSERT INTO engagement_survey_responses ("Id","TenantSlug","SurveyId","RespondentKey","DepartmentName","Answers","SubmittedAt") VALUES {','.join(vals)}""")

    S1 = mk_survey(f"TEST {RUN} eNPS A")
    for who, nps, text in (("ayse", 9, "Esnek çalışma harika, ekip çok iyi"), ("mehmet", 10, "Yöneticim destekliyor, esnek çalışma güzel"),
                           ("admin", 3, "Maaş düşük ve iş yükü çok yoğun")):
        code, _ = api(who, "POST", f"{EN}/surveys/{S1}/responses", {"answers": [{"questionId": "enps", "score": nps}, {"questionId": "scale", "score": 4},
                      {"questionId": "comment", "text": text}, {"questionId": "extra", "text": "TEST tek kişilik not"}]})
        check(f"G18 yanıt ({who})", code == 200, code)
    code, res = api("admin", "GET", f"{EN}/surveys/{S1}/results")
    check("G18 5'ten az yanıtta hiçbir sonuç gösterilmez", code == 200 and res["hidden"] is True and res["questions"] == [] and res["byDepartment"] == []
          and res["anonymityThreshold"] == 5, res)
    code, _ = api("ayse", "GET", f"{EN}/surveys/{S1}/results")
    check("G18 çalışan sonuçları göremez", code == 403, code)
    add_responses(S1, [("Mühendislik", 9, "Esnek çalışma saatleri çok iyi", None), ("Mühendislik", 8, "Maaş düşük, terfi belirsiz", None),
                       ("Mühendislik", 10, "Ortam harika ama maaş düşük", None), ("TEST Satış", 6, "Kantin menüsü", None)])
    code, res = api("admin", "GET", f"{EN}/surveys/{S1}/results")
    qs = {q["id"]: q for q in res.get("questions", [])}
    check("G18 5+ yanıtta toplam sonuç ve eNPS", code == 200 and res["hidden"] is False and res["responseCount"] == 7 and qs["enps"]["enps"] is not None, res)
    depts = {d["department"]: d for d in res["byDepartment"]}
    check("G18 küçük departman hücreleri gizli, ikincil gizleme ile çıkarma engellenir",
          depts["TEST Satış"]["hidden"] and depts["TEST Satış"]["count"] is None and depts["TEST Satış"]["enps"] is None
          and depts["Mühendislik"]["hidden"] and depts["Mühendislik"]["enps"] is None, depts)
    cm = qs["comment"]
    check("G18 en az 5 metinde yorumlar ve yerel duygu özeti", len(cm["texts"]) == 7 and cm["sentiment"] is not None
          and cm["sentiment"]["positive"] + cm["sentiment"]["negative"] + cm["sentiment"]["neutral"] == 7
          and cm["sentiment"]["positive"] >= 3 and cm["sentiment"]["negative"] >= 2
          and any(k["word"] == "esnek" for k in cm["sentiment"]["topKeywords"]) and any(k["word"] == "maaş" for k in cm["sentiment"]["topKeywords"]), cm)
    ex = qs["extra"]
    check("G18 5'ten az metinli soruda bireysel yorum ve duygu gösterilmez", ex["texts"] == [] and ex["hiddenForAnonymity"] is True and ex["sentiment"] is None, ex)
    check("G18 yorum görüntüleme denetim kaydında",
          psql(f"""SELECT count(*) FROM audit_log WHERE "EntityType" = 'Survey' AND "EntityId" = '{S1}' AND "Action" = 'SensitiveViewed'""") != "0")

    S2 = mk_survey(f"TEST {RUN} eNPS B", (dt.datetime.utcnow() - dt.timedelta(days=60)).isoformat() + "Z")
    add_responses(S2, [("Mühendislik", n, None, None) for n in (10, 9, 9, 7, 2, 10)])
    S3 = mk_survey(f"TEST {RUN} eNPS C")
    add_responses(S3, [("Mühendislik", 10, None, None), ("Mühendislik", 1, None, None)])
    code, tr = api("admin", "GET", f"{EN}/surveys/enps-trend")
    pts = {p["surveyId"]: p for p in tr.get("points", [])}
    check("G18 eNPS eğilimi: yalnızca 5+ yanıtlı anketler, zamana göre", code == 200 and S1 in pts and S2 in pts and S3 not in pts
          and tr["excluded"] >= 1 and pts[S2]["enps"] == 50 and tr["minResponses"] == 5
          and [p["surveyId"] for p in tr["points"]].index(S2) < [p["surveyId"] for p in tr["points"]].index(S1), tr)
    code, _ = api("ayse", "GET", f"{EN}/surveys/enps-trend")
    check("G18 çalışan eNPS eğilimini göremez", code == 403, code)

    # ================================================================== G6 tercihler ve takas
    code, _ = api("ayse", "PUT", f"{TS}/shift-preferences/me", {"preferredDays": [8]})
    check("G6 geçersiz gün reddedilir", code == 400, code)
    code, pr = api("ayse", "PUT", f"{TS}/shift-preferences/me", {"preferredDays": [1, 2, 3, 4, 5], "unavailableDays": [7], "preferredShiftTypes": ["Day"],
                   "avoidShiftTypes": ["Night"], "maxNightsPerWeek": 1, "note": "TEST tercih"})
    check("G6 çalışan tercihini kaydeder", code == 200 and pr["unavailableDays"] == [7], (code, pr))
    code, _ = api("ayse", "GET", f"{TS}/shift-preferences?employeeIds={AYSE}")
    check("G6 çalışan başkalarının tercihlerini listeleyemez", code == 403, code)
    code, lst = api("mehmet", "GET", f"{TS}/shift-preferences?employeeIds={AYSE}")
    check("G6 planlayıcı tercihleri görür", code == 200 and lst and lst[0]["avoidShiftTypes"] == ["Night"], (code, lst))

    def mk_shift(name, s, e, brk):
        c, sh = api("admin", "POST", f"{TS}/shifts", {"name": f"TEST {RUN} {name}", "startTime": s, "endTime": e, "breakMinutes": brk})
        created["shifts"].append(sh["id"])
        return sh["id"]

    DAY = mk_shift("Gündüz", "08:00:00", "16:00:00", 60)
    NIGHT = mk_shift("Gece", "22:00:00", "06:00:00", 30)
    EVE = mk_shift("Akşam", "16:00:00", "00:00:00", 30)
    LONG = mk_shift("Uzun", "08:00:00", "20:00:00", 60)
    base = dt.date(2032, 2, 2)
    D0 = base + dt.timedelta(days=(7 - base.weekday()) % 7)  # Pazartesi

    def d(n):
        return (D0 + dt.timedelta(days=n)).isoformat()

    code, ck = api("mehmet", "POST", f"{TS}/shift-preferences/check", {"employeeId": AYSE, "shiftId": NIGHT, "date": d(6)})
    codes = {c["code"] for c in ck.get("conflicts", [])}
    check("G6 atama tercih uyuşmazlığı: müsait değil + istenmeyen gece", code == 200 and {"unavailable_day", "avoided_type"} <= codes, (code, ck))
    code, ck = api("mehmet", "POST", f"{TS}/shift-preferences/check", {"employeeId": AYSE, "shiftId": DAY, "date": d(0)})
    check("G6 uygun atamada uyuşmazlık yok", code == 200 and ck["conflicts"] == [], ck)

    def assign(shift, emp, day):
        # Geçmiş güne API ile atama yapılamaz (400); geçmiş puantaj senaryoları için kayıt doğrudan eklenir
        # (vardiya tanımı silinince cascade ile gider).
        if dt.date.fromisoformat(day) < TODAY:
            aid = str(uuid.uuid4())
            psql(f"""INSERT INTO timeshift_assignments ("Id","TenantSlug","EmployeeId","ShiftId","Date","CreatedAt")
                     VALUES ('{aid}','demo','{emp}','{shift}','{day}',now())""")
            return aid
        c, a = api("mehmet", "POST", f"{TS}/shifts/{shift}/assign", {"employeeId": emp, "date": day})
        return a["id"] if c in (200, 201) else None

    A_tue = assign(DAY, AYSE, d(1))
    M_wed = assign(DAY, MEHMET, d(2))
    code, sw = api("ayse", "POST", f"{TS}/shift-swaps", {"myAssignmentId": A_tue, "targetEmployeeId": MEHMET, "targetAssignmentId": M_wed, "note": f"TEST {RUN} takas"})
    check("G6 takas talebi", code == 200 and sw["status"] == "PendingPeer", (code, sw))
    created["swaps"].append(sw["id"])
    check("G6 karşı tarafa bildirim", wait(lambda: notes(MEHMET, "shift.swap.request") >= 1, 5))
    code, _ = api("ayse", "POST", f"{TS}/shift-swaps", {"myAssignmentId": A_tue, "targetEmployeeId": MEHMET, "targetAssignmentId": M_wed, "note": f"TEST {RUN} ikinci"})
    check("G6 aynı vardiya için ikinci bekleyen talep olmaz", code == 409, code)
    code, _ = api("ayse", "POST", f"{TS}/shift-swaps/{sw['id']}/respond", {"accept": True})
    check("G6 talep eden kendi talebini kabul edemez", code == 404, code)
    code, r = api("mehmet", "POST", f"{TS}/shift-swaps/{sw['id']}/respond", {"accept": True})
    check("G6 karşı taraf kabul eder → onay bekler", code == 200 and r["status"] == "PendingApproval", (code, r))
    check("G6 talep edene kabul bildirimi", wait(lambda: notes(AYSE, "shift.swap.accepted") >= 1, 5))
    code, _ = api("mehmet", "POST", f"{TS}/shift-swaps/{sw['id']}/decide", {"approve": True})
    check("G6 taraf olan yönetici kendi takasını onaylayamaz", code == 403, code)
    code, lst = api("admin", "GET", f"{TS}/shift-swaps?scope=approvals")
    check("G6 onay listesinde görünür", code == 200 and any(x["id"] == sw["id"] and x["canApprove"] for x in lst), code)
    code, r = api("admin", "POST", f"{TS}/shift-swaps/{sw['id']}/decide", {"approve": True})
    check("G6 planlayıcı/İK onaylar", code == 200 and r["status"] == "Approved", (code, r))
    owners = psql(f"""SELECT string_agg("Id"::text || '=' || "EmployeeId"::text, ',') FROM timeshift_assignments WHERE "Id" IN ('{A_tue}','{M_wed}')""")
    check("G6 atamalar tek işlemde yer değiştirir", f"{A_tue}={MEHMET}" in owners and f"{M_wed}={AYSE}" in owners, owners)
    check("G6 iki tarafa karar bildirimi", wait(lambda: notes(AYSE, "shift.swap.decision") >= 1 and notes(MEHMET, "shift.swap.decision") >= 1, 5))

    # 11 saat dinlenme: Ayşe Salı 16-24, Perşembe gündüzünü Mehmet'in Çarşamba 08-16'sıyla değiştirmek ister.
    assign(EVE, AYSE, d(8))
    A_thu = assign(DAY, AYSE, d(10))
    M_wed2 = assign(DAY, MEHMET, d(9))
    code, r = api("ayse", "POST", f"{TS}/shift-swaps", {"myAssignmentId": A_thu, "targetEmployeeId": MEHMET, "targetAssignmentId": M_wed2, "note": f"TEST {RUN} dinlenme"})
    check("G6 11 saat dinlenme kuralı ihlali gerekçeyle reddedilir", code == 400 and r.get("code") == "rule_violation" and "11 saat" in r["message"], (code, r))

    # Haftalık 45 saat: Ayşe 3. hafta Pzt-Per 4×11 saat; Mehmet'in Cuma uzun vardiyasını alırsa 55 saat.
    for k in range(4):
        assign(LONG, AYSE, d(14 + k))
    M_fri = assign(LONG, MEHMET, d(18))
    A_w4 = assign(DAY, AYSE, d(21))
    code, r = api("ayse", "POST", f"{TS}/shift-swaps", {"myAssignmentId": A_w4, "targetEmployeeId": MEHMET, "targetAssignmentId": M_fri, "note": f"TEST {RUN} 45"})
    check("G6 haftalık 45 saat sınırı ihlali reddedilir", code == 400 and "45" in r.get("message", ""), (code, r))

    # Aynı ekip değil (Zeynep'in departmanı yok).
    zeynep = psql("""SELECT "Id" FROM employee_employees WHERE "TenantSlug" = 'demo' AND "Email" = 'zeynep.kaya@demo.hr360'""")
    code, r = api("ayse", "POST", f"{TS}/shift-swaps", {"myAssignmentId": A_w4, "targetEmployeeId": zeynep, "note": f"TEST {RUN} ekip"})
    check("G6 farklı ekiple takas reddedilir", code == 400 and "ekip" in r.get("message", ""), (code, r))

    # Devretme + ret.
    A_give = assign(DAY, AYSE, d(28))
    code, g = api("ayse", "POST", f"{TS}/shift-swaps", {"myAssignmentId": A_give, "targetEmployeeId": MEHMET, "note": f"TEST {RUN} devir"})
    check("G6 vardiya devretme talebi", code == 200, (code, g))
    created["swaps"].append(g.get("id"))
    code, r = api("mehmet", "POST", f"{TS}/shift-swaps/{g['id']}/respond", {"accept": False})
    check("G6 karşı taraf reddeder; talep edene bildirim", code == 200 and r["status"] == "Declined" and wait(lambda: notes(AYSE, "shift.swap.declined") >= 1, 5), (code, r))

    # Onay anında yeniden denetim: kabulden sonra çakışan atama eklenir → onayda gerekçeyle ret.
    A_x = assign(DAY, AYSE, d(35))
    M_x = assign(DAY, MEHMET, d(36))
    code, x = api("ayse", "POST", f"{TS}/shift-swaps", {"myAssignmentId": A_x, "targetEmployeeId": MEHMET, "targetAssignmentId": M_x, "note": f"TEST {RUN} yeniden"})
    created["swaps"].append(x.get("id"))
    api("mehmet", "POST", f"{TS}/shift-swaps/{x['id']}/respond", {"accept": True})
    # Kabulden sonra Mehmet'e d34 gece vardiyası (22:00-06:00) eklenir: takasla d35 08:00'de başlarsa 2 saat dinlenme.
    assign(NIGHT, MEHMET, d(34))
    code, r = api("admin", "POST", f"{TS}/shift-swaps/{x['id']}/decide", {"approve": True})
    st_x = psql(f"""SELECT "Status" || '|' || coalesce("RejectReason", '') FROM timeshift_swap_requests WHERE "Id" = '{x['id']}'""")
    check("G6 onay anında kurallar yeniden denetlenir; ihlalde gerekçeli ret", code == 400 and st_x.startswith("Rejected|") and len(st_x) > 10, (code, r, st_x))
    check("G6 reddedilen takasta atamalar değişmez",
          psql(f"""SELECT "EmployeeId" FROM timeshift_assignments WHERE "Id" = '{A_x}'""") == AYSE)

    # İptal ve geçmiş vardiya.
    A_c = assign(DAY, AYSE, d(42))
    code, c = api("ayse", "POST", f"{TS}/shift-swaps", {"myAssignmentId": A_c, "targetEmployeeId": MEHMET, "note": f"TEST {RUN} iptal"})
    created["swaps"].append(c.get("id"))
    code, r = api("ayse", "POST", f"{TS}/shift-swaps/{c['id']}/cancel")
    check("G6 talep eden iptal eder", code == 200 and r["status"] == "Cancelled", (code, r))
    past = assign(DAY, AYSE, (TODAY - dt.timedelta(days=40)).isoformat())
    code, r = api("ayse", "POST", f"{TS}/shift-swaps", {"myAssignmentId": past, "targetEmployeeId": MEHMET, "note": f"TEST {RUN} geçmiş"})
    check("G6 geçmiş vardiya takas edilemez", code == 400, (code, r))

    # ---------------------------------------------------------- atama kapsamı, geri alma, çizelge görünürlüğü
    outsider = psql(f"""SELECT e."Id" FROM employee_employees e
                        JOIN employee_assignments a ON a."EmployeeId" = e."Id" AND a."EffectiveTo" IS NULL
                        JOIN organization_departments dd ON dd."Id" = a."DepartmentId"
                        WHERE e."TenantSlug" = 'demo' AND e."Status" <> 'Terminated' AND e."Id" NOT IN ('{AYSE}','{MEHMET}')
                          AND dd."HeadEmployeeId" IS DISTINCT FROM '{MEHMET}' LIMIT 1""")
    if outsider:
        code, r = api("mehmet", "POST", f"{TS}/shifts/{DAY}/assign", {"employeeId": outsider, "date": d(50)})
        check("Vardiya: yönetici başka bölümün çalışanına atayamaz", code == 403, (code, r))
        code, r = api("mehmet", "POST", f"{TS}/shifts/rule-check", {"employeeId": outsider, "date": d(50), "shiftId": DAY})
        check("Vardiya: yönetici başka bölüm için kural denetimi yapamaz", code == 403, (code, r))
        code, r = api("admin", "POST", f"{TS}/shifts/{DAY}/assign", {"employeeId": outsider, "date": d(50)})
        check("Vardiya: İK her bölüme atar", code in (200, 201), (code, r))
        OUT_A = (r or {}).get("id")
        code, _ = api("mehmet", "DELETE", f"{TS}/shifts/assignments/{OUT_A}")
        check("Vardiya: yönetici başka bölümün atamasını kaldıramaz", code == 403, code)
        code, ros = api("mehmet", "GET", f"{TS}/shifts/roster?from={d(50)}&to={d(50)}")
        check("Çizelge: yönetici başka bölümün atamasını görmez", code == 200 and all(x["employeeId"] != outsider for x in ros), (code, ros))
        code, _ = api("ayse", "GET", f"{TS}/shifts/roster?from={d(50)}&to={d(50)}&employeeId={outsider}")
        check("Çizelge: çalışan ekip dışındaki kişinin vardiyasını isteyemez", code == 403, code)
        code, ros = api("ayse", "GET", f"{TS}/shifts/roster?from={d(50)}&to={d(50)}")
        check("Çizelge: çalışan şirketin tamamını görmez", code == 200 and all(x["employeeId"] != outsider for x in ros), (code, ros))
        code, ros = api("admin", "GET", f"{TS}/shifts/roster?from={d(50)}&to={d(50)}")
        check("Çizelge: İK tümünü görür", code == 200 and any(x["employeeId"] == outsider for x in ros), code)
        code, _ = api("admin", "DELETE", f"{TS}/shifts/assignments/{OUT_A}")
        check("Vardiya: İK atamayı kaldırır", code == 204, code)
        code, peers = api("ayse", "GET", f"{TS}/shift-swaps/peers")
        check("Takas: ekip arkadaşı listesi şirketin tamamı değil", code == 200 and outsider not in {p["id"] for p in peers}
              and AYSE not in {p["id"] for p in peers}, (code, peers))
    code, r = api("mehmet", "POST", f"{TS}/shifts/{DAY}/assign", {"employeeId": AYSE, "date": (TODAY - dt.timedelta(days=1)).isoformat()})
    check("Vardiya: geçmiş güne atama 400", code == 400, (code, r))
    TODAY_A = assign(DAY, AYSE, TODAY.isoformat()) if psql(f"""SELECT count(*) FROM timeshift_assignments WHERE "EmployeeId" = '{AYSE}' AND "Date" = '{TODAY}'""") == "0" else None
    if TODAY_A:
        check("Vardiya: bugüne atama serbest", TODAY_A is not None)
        code, _ = api("mehmet", "DELETE", f"{TS}/shifts/assignments/{TODAY_A}")
        check("Vardiya: yönetici kendi bölümündeki hatalı atamayı kaldırır", code == 204, code)
    code, _ = api("mehmet", "DELETE", f"{TS}/shifts/assignments/{past}")
    check("Vardiya: geçmiş atama kaldırılamaz", code == 400, code)
    code, _ = api("ayse", "DELETE", f"{TS}/shifts/assignments/{A_c}")
    check("Vardiya: çalışan atama kaldıramaz", code == 403, code)
    code, ros = api("ayse", "GET", f"{TS}/shifts/roster?from={d(1)}&to={d(2)}")
    check("Çizelge: çalışan kendini ve ekip arkadaşını görür", code == 200 and {x["employeeId"] for x in ros} >= {AYSE}, (code, ros))

    # ================================================================== G7 puantaj
    code, _ = api("ayse", "PUT", f"{TS}/timesheet-report/settings", {"lateGraceMinutes": 0, "defaultStart": "09:00:00", "defaultEnd": "18:00:00", "defaultBreakMinutes": 60})
    check("G7 çalışan tolerans ayarını değiştiremez", code == 403, code)
    code, s = api("admin", "PUT", f"{TS}/timesheet-report/settings", {"lateGraceMinutes": 5, "defaultStart": "09:00:00", "defaultEnd": "18:00:00", "defaultBreakMinutes": 60})
    check("G7 İK tolerans ve varsayılan mesaiyi kaydeder", code == 200 and s["lateGraceMinutes"] == 5, (code, s))

    def free_weekday(start_back):
        for back in range(start_back, start_back + 25):
            day = TODAY - dt.timedelta(days=back)
            if day.weekday() >= 5:
                continue
            busy = psql(f"""SELECT (SELECT count(*) FROM timeshift_clock_punches WHERE "EmployeeId" = '{AYSE}' AND "At" >= '{day - dt.timedelta(days=1)}' AND "At" < '{day + dt.timedelta(days=2)}')
                          + (SELECT count(*) FROM timeshift_time_entries WHERE "EmployeeId" = '{AYSE}' AND "Date" = '{day}')
                          + (SELECT count(*) FROM timeshift_assignments WHERE "EmployeeId" = '{AYSE}' AND "Date" BETWEEN '{day - dt.timedelta(days=1)}' AND '{day + dt.timedelta(days=1)}')
                          + (SELECT count(*) FROM timeshift_shift_overrides WHERE "EmployeeId" = '{AYSE}' AND "Date" = '{day}')
                          + (SELECT count(*) FROM timeshift_overtime_requests WHERE "EmployeeId" = '{AYSE}' AND "Date" = '{day}' AND "Status" IN ('Pending','Approved'))""")
            if busy == "0":
                return day
        return None

    D1 = free_weekday(3)
    D2 = free_weekday((TODAY - D1).days + 3)

    def punch(day, hh, mm, kind):
        pid = str(uuid.uuid4())
        utc = dt.datetime(day.year, day.month, day.day, hh, mm) - dt.timedelta(hours=3)  # Europe/Istanbul UTC+3
        psql(f"""INSERT INTO timeshift_clock_punches ("Id","TenantSlug","EmployeeId","SiteId","Kind","Method","OnSite","At")
                 VALUES ('{pid}','demo','{AYSE}',NULL,'{kind}','Import',NULL,'{utc.isoformat()}+00')""")
        created["punches"].append(pid)

    punch(D1, 9, 20, "In")
    punch(D1, 20, 0, "Out")
    assign(DAY, AYSE, D2.isoformat())
    punch(D2, 7, 58, "In")
    punch(D2, 15, 0, "Out")

    code, rep = api("ayse", "GET", f"{TS}/timesheet-report?from={D1}&to={D1}")
    row = next((r for r in rep.get("rows", []) if r["date"] == D1.isoformat()), None) if code == 200 else None
    check("G7 çalışan kendi puantajı: 20 dk geç, 580 dk çalışma, 100 dk olası fazla mesai",
          code == 200 and rep["scope"] == "self" and {r["employeeId"] for r in rep["rows"]} == {AYSE} and row
          and row["lateMinutes"] == 20 and row["workedMinutes"] == 580 and row["overtimeMinutes"] == 100
          and row["suggestedOvertimeHours"] == 1.5 and row["source"] == "Default" and row["status"] == "Late", (code, row))
    code, rep2 = api("ayse", "GET", f"{TS}/timesheet-report?from={D2}&to={D2}")
    row2 = next((r for r in rep2.get("rows", []) if r["date"] == D2.isoformat()), None) if code == 200 else None
    check("G7 vardiyaya göre: tolerans içinde erken giriş, 60 dk erken çıkış",
          row2 and row2["source"] == "Shift" and row2["lateMinutes"] == 0 and row2["earlyLeaveMinutes"] == 60 and row2["overtimeMinutes"] == 0, row2)
    code, _ = api("ayse", "GET", f"{TS}/timesheet-report?from={D1}&to={D1}&employeeId={MEHMET}")
    check("G7 çalışan başkasının puantajını göremez", code == 403, code)
    code, rep3 = api("mehmet", "GET", f"{TS}/timesheet-report?from={D1}&to={D1}")
    check("G7 yönetici kendi departmanını görür", code == 200 and rep3["scope"] == "department" and AYSE in {r["employeeId"] for r in rep3["rows"]}, (code, rep3.get("scope")))
    code, rep4 = api("admin", "GET", f"{TS}/timesheet-report?from={D1}&to={D1}")
    check("G7 İK herkesi görür", code == 200 and rep4["scope"] == "all", code)
    code, _ = api("admin", "GET", f"{TS}/timesheet-report?from={TODAY - dt.timedelta(days=40)}&to={TODAY}")
    check("G7 en çok 31 gün", code == 400, code)
    code, ot = api("ayse", "POST", f"{TS}/overtime", {"date": D1.isoformat(), "hours": row["suggestedOvertimeHours"] if row else 1.5,
                   "reason": "TEST puantajdan tespit edilen fazla mesai"})
    check("G7 tespit edilen fazla mesaiden talep taslağı (mevcut onay akışı)", code == 200 and ot["status"] == "Pending" and ot["hours"] == 1.5, (code, ot))
    if code == 200:
        created["overtime"].append(ot["id"])
    code, rep = api("ayse", "GET", f"{TS}/timesheet-report?from={D1}&to={D1}")
    row = next((r for r in rep.get("rows", []) if r["date"] == D1.isoformat()), {})
    check("G7 raporda fazla mesai talebi görünür; otomatik yaptırım yok", (row.get("overtimeRequest") or {}).get("status") == "Pending" and "otomatik" in rep["note"], row)
finally:
    cleanup()

print(f"FAILS: {len(FAIL)}")
for f in FAIL:
    print("  -", f)
sys.exit(1 if FAIL else 0)

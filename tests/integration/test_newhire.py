#!/usr/bin/env python3
"""İlk 90 gün takibi: /api/engagement/newhire.

Denetlenenler: 30. gününe gelen çalışana form açılır ve yöneticisi (departman başı) hatırlatılır; ikinci
çalıştırma tekrar göndermez; eski çalışana geriye dönük form açılmaz; çalışan kendi formunu yanıtlar
(geçersiz puan reddedilir, ikinci yanıt reddedilir); yönetici ekibinin yanıtını düşük puan işaretiyle görür;
çalışan başkasının formunu göremez. Geçici çalışan ve kayıtlar sonda silinir ("TEST-NH").
Çalıştırma: timeout 120 python3 tests/integration/test_newhire.py
"""
import datetime as dt
import os
import subprocess
import sys
import uuid

sys.path.insert(0, os.path.dirname(__file__))
from common import FAIL, api, check  # noqa: E402
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "support"))
from demo_ids import AYSE, ENG, MEHMET  # noqa: E402

ROOT = os.path.join(os.path.dirname(__file__), "..", "..")
FAIL.clear()
B = "/api/engagement/newhire"


def psql(sql):
    out = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", "hr360_operational", "-Atc", sql],
                         capture_output=True, text=True, cwd=ROOT)
    return (out.stdout + out.stderr).strip()


today = dt.date.today()
emp = None
try:
    code, e = api("ik", "POST", "/api/employee/employees", {"firstName": "TEST-NH", "lastName": "Yeni", "email": f"test-nh-{uuid.uuid4().hex[:6]}@demo.hr360",
                                                           "hireDate": (today - dt.timedelta(days=30)).isoformat()})
    emp = e["id"]
    api("ik", "POST", f"/api/employee/employees/{emp}/assignments", {"departmentId": ENG, "effectiveFrom": (today - dt.timedelta(days=30)).isoformat()})

    code, r = api("ik", "POST", f"{B}/run")
    check("NH1: zamanlayıcı çalışır", code == 200, (code, r))
    row = psql(f"SELECT \"Milestone\" || '|' || coalesce(\"ManagerEmployeeId\"::text,'') FROM engagement_newhire_checkins WHERE \"EmployeeId\" = '{emp}'")
    check("NH1: 30. gün formu açılır, yönetici departman başı", row == f"30|{MEHMET}", row)
    n = psql(f"SELECT count(*) FROM notification_messages WHERE \"TemplateCode\" = 'newhire.manager' AND \"RecipientEmployeeId\" = '{MEHMET}' AND \"CreatedAt\" > now() - interval '5 minutes'")
    check("NH1: yöneticiye hatırlatma", n not in ("", "0"), n)
    n = psql(f"SELECT count(DISTINCT \"Channel\") FROM notification_messages WHERE \"TemplateCode\" = 'newhire.checkin' AND \"RecipientEmployeeId\" = '{emp}'")
    check("NH1: çalışana uygulama içi + e-posta", n == "2", n)
    api("ik", "POST", f"{B}/run")
    check("NH2: ikinci çalıştırma tekrar göndermez", psql(f"SELECT count(*) FROM engagement_newhire_checkins WHERE \"EmployeeId\" = '{emp}'") == "1")
    check("NH2: eski çalışana geriye dönük form yok", psql(f"SELECT count(*) FROM engagement_newhire_checkins WHERE \"EmployeeId\" = '{AYSE}'") == "0")

    # Ayşe için yanıt akışı (zamanlayıcının açacağı satırın aynısı)
    cid = str(uuid.uuid4())
    psql(f"INSERT INTO engagement_newhire_checkins (\"Id\",\"TenantSlug\",\"EmployeeId\",\"Milestone\",\"DueOn\",\"ManagerEmployeeId\") VALUES ('{cid}','demo','{AYSE}',60,current_date,'{MEHMET}')")
    _, mine = api("ayse", "GET", f"{B}/me")
    check("NH3: çalışan kendi formunu görür", any(x["id"] == cid and x["answeredAt"] is None for x in mine), mine)
    code, _ = api("ayse", "POST", f"{B}/{cid}/answer", {"belonging": 7, "clarity": 3, "support": 3})
    check("NH3: geçersiz puan reddedilir", code == 400, code)
    code, _ = api("mehmet", "POST", f"{B}/{cid}/answer", {"belonging": 3, "clarity": 3, "support": 3})
    check("NH3: başkasının formu yanıtlanamaz", code == 400, code)
    code, _ = api("ayse", "POST", f"{B}/{cid}/answer", {"belonging": 4, "clarity": 2, "support": 5, "comment": "TEST-NH erişimler gecikti"})
    check("NH3: çalışan yanıtlar", code == 200, code)
    code, _ = api("ayse", "POST", f"{B}/{cid}/answer", {"belonging": 4, "clarity": 4, "support": 4})
    check("NH3: ikinci yanıt reddedilir", code == 400, code)
    _, team = api("mehmet", "GET", f"{B}/team")
    t = next((x for x in team if x["id"] == cid), None)
    check("NH4: yönetici yanıtı düşük puan işaretiyle görür", t and t["lowScore"] and t["comment"], t)
    _, other = api("ayse", "GET", f"{B}/team")
    check("NH4: çalışan ekip listesini göremez", not any(x["id"] == cid for x in other), other)
finally:
    psql("DELETE FROM engagement_newhire_checkins WHERE \"TenantSlug\" = 'demo'")
    psql("DELETE FROM notification_messages WHERE \"TenantSlug\" = 'demo' AND \"TemplateCode\" LIKE 'newhire.%'")
    if emp:
        psql(f"DELETE FROM employee_employees WHERE \"Id\" = '{emp}'")

print("\nSONUÇ:", "BAŞARILI" if not FAIL else f"{len(FAIL)} HATA: {FAIL}")
sys.exit(1 if FAIL else 0)

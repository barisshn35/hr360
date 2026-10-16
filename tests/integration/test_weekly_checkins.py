#!/usr/bin/env python3
"""Haftalık check-in: /api/engagement/checkins.

Denetlenenler: doğrulama (ruh hâli 1-5, boş not); haftada tek kayıt (güncellenir); yönetici ekibinin
check-in'ini görür, ekibi dışındaki göremez; engel sıradaki planlı 1:1 gündemine eklenir; 1:1 yoksa açık
hata. Test kayıtları sonda silinir. Çalıştırma: timeout 120 python3 tests/integration/test_weekly_checkins.py
"""
import datetime as dt
import os
import subprocess
import sys

sys.path.insert(0, os.path.dirname(__file__))
from common import FAIL, api, check  # noqa: E402
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "support"))
from demo_ids import AYSE  # noqa: E402

ROOT = os.path.join(os.path.dirname(__file__), "..", "..")
FAIL.clear()
B = "/api/engagement/checkins"
oo = None


def psql(sql):
    out = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", "hr360_operational", "-Atc", sql],
                         capture_output=True, text=True, cwd=ROOT)
    return (out.stdout + out.stderr).strip()


try:
    code, _ = api("ayse", "PUT", f"{B}/me", {"progress": "x", "mood": 9})
    check("WC1: geçersiz ruh hâli reddedilir", code == 400, code)
    code, _ = api("ayse", "PUT", f"{B}/me", {"mood": 3})
    check("WC1: boş not reddedilir", code == 400, code)
    code, c1 = api("ayse", "PUT", f"{B}/me", {"progress": "TEST-WC API bitti", "blockers": "TEST-WC test ortamı yavaş", "mood": 3})
    code2, c2 = api("ayse", "PUT", f"{B}/me", {"progress": "TEST-WC API bitti, dokümantasyon", "blockers": "TEST-WC test ortamı yavaş", "mood": 4})
    check("WC2: haftada tek kayıt, güncellenir", code == 200 and code2 == 200 and c1["id"] == c2["id"] and c2["mood"] == 4, (c1, c2))
    _, team = api("mehmet", "GET", f"{B}/team")
    row = next((p for p in team["people"] if p["employeeId"] == AYSE), None)
    check("WC3: yönetici ekibinin check-in'ini görür", row and row["checkins"][0] and row["checkins"][0]["mood"] == 4, row)
    _, other = api("ik", "GET", f"{B}/team")
    check("WC3: ekibi dışındaki görmez", not any(p["employeeId"] == AYSE for p in other["people"]), other)
    code, _ = api("mehmet", "POST", f"{B}/{c2['id']}/to-one-on-one")
    check("WC4: 1:1 yokken açık hata", code == 400, code)
    when = (dt.datetime.utcnow() + dt.timedelta(days=2)).replace(microsecond=0).isoformat() + "Z"
    code, o = api("mehmet", "POST", "/api/engagement/one-on-ones", {"employeeId": AYSE, "scheduledAt": when, "agenda": ["TEST-WC gündem"]})
    oo = o.get("id") if isinstance(o, dict) else None
    code, r = api("mehmet", "POST", f"{B}/{c2['id']}/to-one-on-one")
    check("WC4: engel 1:1 gündemine eklenir", code == 200 and r["oneOnOneId"] == oo, (code, r))
    agenda = psql(f"SELECT \"Agenda\"::text FROM engagement_one_on_ones WHERE \"Id\" = '{oo}'")
    check("WC4: gündemde engel metni", "test ortamı yavaş" in agenda, agenda)
    code, _ = api("ik", "POST", f"{B}/{c2['id']}/to-one-on-one")
    check("WC4: ekibi dışındaki yönetici ekleyemez", code == 403, code)
finally:
    psql("DELETE FROM engagement_weekly_checkins WHERE \"Progress\" LIKE 'TEST-WC%'")
    if oo:
        psql(f"DELETE FROM engagement_one_on_ones WHERE \"Id\" = '{oo}'")

print("\nSONUÇ:", "BAŞARILI" if not FAIL else f"{len(FAIL)} HATA: {FAIL}")
sys.exit(1 if FAIL else 0)

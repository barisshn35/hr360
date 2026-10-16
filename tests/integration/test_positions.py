#!/usr/bin/env python3
"""Norm kadro: /api/organization/positions.

Denetlenenler: İK pozisyon tanımlar; dolu/boş kadro unvandan hesaplanır (büyük/küçük harf duyarsız);
kadro dışı çalışanlar listelenir; aynı departmanda aynı unvan reddedilir; bütçe yalnızca İK'ya döner;
departman başı yalnızca kendi departmanını görür; çalışan göremez. Test pozisyonları sonda silinir.
Çalıştırma: timeout 60 python3 tests/integration/test_positions.py
"""
import os
import subprocess
import sys

sys.path.insert(0, os.path.dirname(__file__))
from common import FAIL, api, check  # noqa: E402
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "support"))
from demo_ids import AYSE, ENG, MEHMET  # noqa: E402

ROOT = os.path.join(os.path.dirname(__file__), "..", "..")
FAIL.clear()
B = "/api/organization/positions"


def psql(sql):
    out = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", "hr360_operational", "-Atc", sql],
                         capture_output=True, text=True, cwd=ROOT)
    return (out.stdout + out.stderr).strip()


title = psql(f"SELECT \"PositionTitle\" FROM employee_assignments WHERE \"EmployeeId\" = '{AYSE}' AND \"EffectiveTo\" IS NULL ORDER BY \"EffectiveFrom\" DESC LIMIT 1")
try:
    code, p = api("ik", "POST", B, {"departmentId": ENG, "title": title.upper(), "headcount": 3, "budgetMonthly": 300000, "currency": "TRY"})
    check("PO1: İK pozisyon tanımlar", code == 200, (code, p))
    code, _ = api("ik", "POST", B, {"departmentId": ENG, "title": title.lower(), "headcount": 1})
    check("PO1: aynı unvan reddedilir", code == 409, code)
    _, r = api("ik", "GET", f"{B}?departmentId={ENG}")
    row = next(x for x in r["positions"] if x["id"] == p["id"])
    check("PO2: dolu/boş kadro unvandan (harf duyarsız)", row["filled"] == 1 and row["open"] == 2 and row["holders"][0]["employeeId"] == AYSE, row)
    check("PO2: kadro dışı çalışan listelenir", any(o["employeeId"] == MEHMET for o in r["outside"]), r["outside"])
    check("PO2: İK bütçeyi görür", row["budgetMonthly"] == 300000 and r["summary"]["budgetMonthly"], row)
    code, m = api("mehmet", "GET", B)
    mrow = next((x for x in m["positions"] if x["id"] == p["id"]), None)
    check("PO3: departman başı kendi departmanını görür, bütçe yok", code == 200 and mrow and mrow["budgetMonthly"] is None and m["summary"]["budgetMonthly"] is None, mrow)
    code, _ = api("ayse", "GET", B)
    check("PO3: çalışan göremez", code == 403, code)
    code, _ = api("mehmet", "POST", B, {"departmentId": ENG, "title": "TEST-PO Stajyer", "headcount": 1})
    check("PO3: yönetici tanımlayamaz", code == 403, code)
    code, _ = api("ik", "DELETE", f"{B}/{p['id']}")
    check("PO4: pozisyon kaldırılır", code == 204, code)
finally:
    psql(f"DELETE FROM organization_positions WHERE \"DepartmentId\" = '{ENG}'")

print("\nSONUÇ:", "BAŞARILI" if not FAIL else f"{len(FAIL)} HATA: {FAIL}")
sys.exit(1 if FAIL else 0)

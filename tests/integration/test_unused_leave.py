#!/usr/bin/env python3
# hr360-modules: compensation  (kapalıysa scripts/test.sh bu betiği atlar)
"""Kullanılmayan izin karşılığı: GET /api/compensation/compensation/reports/unused-leave.

Denetlenenler: yalnızca İK; tutar = kalan gün × (aylık brüt / 30); görüntüleme ücret erişim kaydına yazılır.
Çalıştırma: timeout 60 python3 tests/integration/test_unused_leave.py
"""
import os
import subprocess
import sys

sys.path.insert(0, os.path.dirname(__file__))
from common import FAIL, api, check  # noqa: E402

ROOT = os.path.join(os.path.dirname(__file__), "..", "..")
FAIL.clear()
P = "/api/compensation/compensation/reports/unused-leave"


def psql(sql):
    out = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", "hr360_operational", "-Atc", sql],
                         capture_output=True, text=True, cwd=ROOT)
    return (out.stdout + out.stderr).strip()


code, _ = api("ayse", "GET", P)
check("UL1: çalışan raporu göremez", code == 403, code)
before = psql("SELECT count(*) FROM audit_log WHERE \"EntityId\" = 'unused-leave'")
code, r = api("ik", "GET", P)
check("UL2: İK raporu alır", code == 200 and "items" in r, code)
ok = all(i["amount"] is None or abs(i["amount"] - round(round(i["dailyGross"], 2) * i["remainingDays"], 2)) < 0.02 for i in r["items"])
check("UL2: tutar = kalan gün × günlük brüt", ok, r["items"][:2])
check("UL2: kalan günü olmayan listelenmez", all(i["remainingDays"] > 0 for i in r["items"]))
after = psql("SELECT count(*) FROM audit_log WHERE \"EntityId\" = 'unused-leave'")
check("UL3: görüntüleme erişim kaydına yazılır", int(after or 0) > int(before or 0), (before, after))

print("\nSONUÇ:", "BAŞARILI" if not FAIL else f"{len(FAIL)} HATA: {FAIL}")
sys.exit(1 if FAIL else 0)

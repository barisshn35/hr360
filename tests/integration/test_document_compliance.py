#!/usr/bin/env python3
"""Eksik belge takibi: /api/expense/documents/compliance.

Denetlenenler: gereksinim doğrulaması ve yetki; şirket geneli ve departmana özel gereksinim; çalışan kendi
eksiklerini görür; belge eklenince eksik kalkar; geçerliliği geçmiş belge "Expired"; hatırlatma bildirimi.
Gereksinimler ve test belgeleri sonda eski hâline getirilir (belge adı öneki "TEST-DOC").
Çalıştırma: timeout 120 python3 tests/integration/test_document_compliance.py
"""
import os
import subprocess
import sys

sys.path.insert(0, os.path.dirname(__file__))
from common import FAIL, api, check  # noqa: E402
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "support"))
from demo_ids import AYSE, ENG  # noqa: E402

ROOT = os.path.join(os.path.dirname(__file__), "..", "..")
FAIL.clear()
B = "/api/expense/documents/compliance"


def psql(sql):
    out = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", "hr360_operational", "-Atc", sql],
                         capture_output=True, text=True, cwd=ROOT)
    return (out.stdout + out.stderr).strip()


_, orig = api("ik", "GET", f"{B}/requirements")
try:
    code, _ = api("ik", "PUT", f"{B}/requirements", [{"type": "Health", "departmentId": None, "validityMonths": 0}])
    check("DC1: geçersiz süre reddedilir", code == 400, code)
    code, _ = api("ayse", "PUT", f"{B}/requirements", [])
    check("DC1: çalışan gereksinim tanımlayamaz", code == 403, code)
    code, r = api("ik", "PUT", f"{B}/requirements", [
        {"type": "Contract", "departmentId": None, "validityMonths": None},
        {"type": "Health", "departmentId": ENG, "validityMonths": 12},
    ])
    check("DC1: İK gereksinim tanımlar", code == 200 and len(r) == 2, (code, r))

    _, mine = api("ayse", "GET", f"{B}/me")
    types = {i["type"]: i["state"] for i in mine}
    check("DC2: Mühendislik'teki Ayşe'de sözleşme ve sağlık raporu eksik", types.get("Contract") == "Missing" and types.get("Health") == "Missing", mine)
    _, ov = api("ik", "GET", f"{B}?departmentId={ENG}")
    check("DC2: İK özetinde Ayşe görünür", any(x["employeeId"] == AYSE for x in ov["rows"]), ov["summary"])

    api("ik", "POST", "/api/expense/documents", {"employeeId": AYSE, "type": "Contract", "fileName": "TEST-DOC sözleşme"})
    api("ik", "POST", "/api/expense/documents", {"employeeId": AYSE, "type": "Health", "fileName": "TEST-DOC rapor", "validUntil": "2026-01-31"})
    _, mine = api("ayse", "GET", f"{B}/me")
    types = {i["type"]: i["state"] for i in mine}
    check("DC3: sözleşme eklenince eksik kalkar", "Contract" not in types, mine)
    check("DC3: geçerliliği geçmiş rapor 'süresi doldu'", types.get("Health") == "Expired", mine)

    code, s = api("ik", "POST", f"{B}/remind", {"employeeIds": [AYSE]})
    check("DC4: hatırlatma gönderilir", code == 200 and s["sent"] == 1, (code, s))
    n = psql(f"SELECT count(*) FROM notification_messages WHERE \"TemplateCode\" = 'documents.missing' AND \"RecipientEmployeeId\" = '{AYSE}' AND \"CreatedAt\" > now() - interval '5 minutes'")
    check("DC4: bildirim kaydı", n not in ("", "0"), n)
finally:
    api("ik", "PUT", f"{B}/requirements", [{"type": r["type"], "departmentId": r["departmentId"], "validityMonths": r["validityMonths"]} for r in orig or []])
    psql("DELETE FROM expense_documents WHERE \"FileName\" LIKE 'TEST-DOC%'")
    psql("DELETE FROM notification_messages WHERE \"TemplateCode\" = 'documents.missing' AND \"TenantSlug\" = 'demo'")

print("\nSONUÇ:", "BAŞARILI" if not FAIL else f"{len(FAIL)} HATA: {FAIL}")
sys.exit(1 if FAIL else 0)

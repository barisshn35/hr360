#!/usr/bin/env python3
"""Ürün içi geri bildirim ve ekran kullanımı: /api/engagement/product.

Denetlenenler: yalnızca ekran şablonu kabul edilir (kimlik içeren adres sayılmaz); oy yalnızca +1/-1;
İK içgörüde açılma, oy ve yorumu görür, çalışan göremez; tablolarda kişi kimliği sütunu yok.
Test satırları sonda silinir (ekran "/panel/test-insights").
Çalıştırma: timeout 120 python3 tests/integration/test_product_insights.py
"""
import os
import subprocess
import sys

sys.path.insert(0, os.path.dirname(__file__))
from common import FAIL, api, check  # noqa: E402

ROOT = os.path.join(os.path.dirname(__file__), "..", "..")
FAIL.clear()
B = "/api/engagement/product"
P = "/panel/test-insights"


def psql(sql):
    out = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", "hr360_operational", "-Atc", sql],
                         capture_output=True, text=True, cwd=ROOT)
    return (out.stdout + out.stderr).strip()


try:
    code, _ = api("ayse", "POST", f"{B}/views", {"paths": [P, P, "/panel/test-insights/131ec96a-465a-45ae-87f0-58ed52bb9332", "/disari"]})
    check("PI1: açılışlar kaydedilir", code == 204, code)
    n = psql(f"SELECT coalesce(sum(\"Views\"),0) FROM engagement_page_views WHERE \"Path\" LIKE '{P}%'")
    check("PI1: yalnızca şablon sayılır (kimlikli adres reddedilir)", n == "2", n)
    code, _ = api("ayse", "POST", f"{B}/feedback", {"path": P, "rating": 5})
    check("PI2: geçersiz oy reddedilir", code == 400, code)
    code, _ = api("ayse", "POST", f"{B}/feedback", {"path": P, "rating": -1, "comment": "TEST-PI filtre yok"})
    check("PI2: isimsiz geri bildirim", code == 200, code)
    code, _ = api("ayse", "GET", f"{B}/insights")
    check("PI3: çalışan içgörüyü göremez", code == 403, code)
    code, ins = api("ik", "GET", f"{B}/insights?days=7")
    s = next((x for x in ins["screens"] if x["path"] == P), None)
    check("PI3: İK açılma ve oyu görür", s and s["views"] >= 2 and s["down"] >= 1, s)
    check("PI3: yorum rol grubuyla görünür", any(c["comment"] == "TEST-PI filtre yok" and c["roleGroup"] == "employee" for c in ins["comments"]))
    cols = psql("SELECT string_agg(column_name, ',') FROM information_schema.columns WHERE table_name IN ('engagement_product_feedback','engagement_page_views')")
    check("PI4: tablolarda kişi kimliği sütunu yok", not any(k in cols.lower() for k in ("employee", "user", "email")), cols)
finally:
    psql(f"DELETE FROM engagement_page_views WHERE \"Path\" LIKE '{P}%'; DELETE FROM engagement_product_feedback WHERE \"Path\" LIKE '{P}%'")

print("\nSONUÇ:", "BAŞARILI" if not FAIL else f"{len(FAIL)} HATA: {FAIL}")
sys.exit(1 if FAIL else 0)

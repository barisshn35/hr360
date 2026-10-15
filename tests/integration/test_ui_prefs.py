#!/usr/bin/env python3
"""Dalga 12 (madde 87, 89, 90): kullanıcı başına arayüz tercihleri (kayıtlı liste görünümleri, ana panel
düzeni, "Yenilikler" okundu bilgisi). API: GET/PUT/DELETE /api/notification/notifications/ui-prefs[/<anahtar>].

Denetlenenler: kişiye özel yalıtım (Ayşe'nin tercihi Mehmet'e görünmez), anahtar/değer doğrulaması
(geçersiz anahtar, düz değer, 16 KB sınırı), silme, kayıt yokken 204.

Test kayıtları: anahtar öneki "test-w12" (tests/support/cleanup_test_data.py da siler).
Çalıştırma: timeout 120 python3 tests/integration/test_ui_prefs.py
"""
import os
import sys
import uuid

sys.path.insert(0, os.path.dirname(__file__))
from common import FAIL, api, check  # noqa: E402

FAIL.clear()
P = "/api/notification/notifications/ui-prefs"
KEY = "test-w12." + uuid.uuid4().hex[:8]
VIEWS = "test-w12-views:" + uuid.uuid4().hex[:6]

# 1) Kayıt yokken tekil okuma 204, liste 200 (sözlük)
code, _ = api("ayse", "GET", f"{P}/{KEY}")
check("UP1: kayıt yokken 204", code == 204, code)
code, allp = api("ayse", "GET", P)
check("UP1: liste sözlük döner", code == 200 and isinstance(allp, dict), (code, allp))

# 2) Yazma, okuma, üzerine yazma
layout = {"order": ["queue", "welcome"], "hidden": ["chart"]}
code, _ = api("ayse", "PUT", f"{P}/{KEY}", layout)
check("UP2: yazma 200", code == 200, code)
code, r = api("ayse", "GET", f"{P}/{KEY}")
check("UP2: okunan değer aynı", code == 200 and r["value"] == layout, (code, r))
code, _ = api("ayse", "PUT", f"{P}/{KEY}", {"order": [], "hidden": []})
code, r = api("ayse", "GET", f"{P}/{KEY}")
check("UP2: üzerine yazıldı (upsert)", code == 200 and r["value"] == {"order": [], "hidden": []}, (code, r))
code, allp = api("ayse", "GET", P)
check("UP2: listede görünür", code == 200 and KEY in allp, (code, list((allp or {}).keys())[:5]))

# 3) Kişiye özel: Mehmet Ayşe'nin tercihini görmez, kendi değeri ayrı tutulur
code, r = api("mehmet", "GET", f"{P}/{KEY}")
check("UP3: başka kullanıcıya görünmez", code == 204, (code, r))
code, _ = api("mehmet", "PUT", f"{P}/{KEY}", {"seen": "11"})
code, r = api("ayse", "GET", f"{P}/{KEY}")
check("UP3: Mehmet'in yazımı Ayşe'ninkini değiştirmez", r and r["value"] == {"order": [], "hidden": []}, r)

# 4) Doğrulama
code, _ = api("ayse", "PUT", f"{P}/Bad%20Key", {"a": 1})
check("UP4: geçersiz anahtar 400", code == 400, code)
code, _ = api("ayse", "PUT", f"{P}/{KEY}", 42)
check("UP4: düz değer 400", code == 400, code)
code, _ = api("ayse", "PUT", f"{P}/{KEY}", {"s": "x" * (17 * 1024)})
check("UP4: 16 KB sınırı 400", code == 400, code)

# 5) Kayıtlı görünümler biçimi (arayüzle aynı) ve silme
views = {"views": [{"id": str(uuid.uuid4()), "name": "Bekleyenler", "params": "durum=Submitted&v_q=izin", "createdAt": "2026-10-07T00:00:00Z"}]}
code, _ = api("ayse", "PUT", f"{P}/{VIEWS}", views)
code, r = api("ayse", "GET", f"{P}/{VIEWS}")
check("UP5: görünüm listesi saklanır", code == 200 and r["value"]["views"][0]["name"] == "Bekleyenler", (code, r))
for who, key in (("ayse", KEY), ("ayse", VIEWS), ("mehmet", KEY)):
    code, _ = api(who, "DELETE", f"{P}/{key}")
check("UP5: silme 204", code == 204, code)
code, _ = api("ayse", "GET", f"{P}/{KEY}")
check("UP5: silinen okunmaz", code == 204, code)

# 6) Kimliksiz erişim yok
from common import http  # noqa: E402
code, _ = http("GET", P)
check("UP6: oturumsuz 401", code == 401, code)

print(f"FAILS: {len(FAIL)}")
sys.exit(1 if FAIL else 0)

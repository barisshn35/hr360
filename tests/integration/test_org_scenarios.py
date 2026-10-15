#!/usr/bin/env python3
"""Org senaryo planlama (engagement-service /api/org-scenarios): taslak sahipliği ve maaş görünürlüğü.

Denetlenenler: yönetici yalnızca kendi taslaklarını listeler/düzenler/siler ve etkisini görür; İK
tümünü görür; maaş görme yetkisi olmayan yöneticinin etki yanıtında hamlelerin planlanan maaşı
(plannedSalary) ve maliyet alanları yoktur.

Test kayıtları: senaryo adı öneki "TEST-OS" (test sonunda API ile silinir).
Çalıştırma: timeout 120 python3 tests/integration/test_org_scenarios.py
"""
import os
import sys
import uuid

sys.path.insert(0, os.path.dirname(__file__))
from common import FAIL, api, check  # noqa: E402

FAIL.clear()
E = "/api/engagement/org-scenarios"
RUN = uuid.uuid4().hex[:6]
created = []

code, base = api("mehmet", "GET", f"{E}/baseline")
if code in (402, 403) and (base or {}).get("code") == "plan_required":
    print("Atlandı: demo şirketinin planı org senaryolarını içermiyor")
    print("FAILS: 0")
    sys.exit(0)
check("OS0: yönetici taban kadroyu okur", code == 200, (code, str(base)[:200]))
dept = (base or {}).get("departments", [{}])[0].get("id")

try:
    hire = {"employeeId": "00000000-0000-0000-0000-000000000000", "name": "Yeni kadro", "toDepartmentId": dept, "kind": "Hire", "plannedSalary": 55555}
    code, hr = api("admin", "POST", E, {"name": f"TEST-OS {RUN} İK", "moves": [hire]})
    check("OS1: İK senaryo oluşturur", code == 200, (code, hr))
    created.append(("admin", hr["id"]))
    code, mine = api("mehmet", "POST", E, {"name": f"TEST-OS {RUN} yönetici", "moves": [hire]})
    check("OS1: yönetici senaryo oluşturur", code == 200, (code, mine))
    created.append(("mehmet", mine["id"]))

    code, lst = api("mehmet", "GET", E)
    ids = {s["id"] for s in lst} if code == 200 else set()
    check("OS2: yönetici yalnızca kendi taslağını listeler", mine["id"] in ids and hr["id"] not in ids, (code, ids))
    code, lst = api("admin", "GET", E)
    ids = {s["id"] for s in lst} if code == 200 else set()
    check("OS2: İK tüm taslakları listeler", {mine["id"], hr["id"]} <= ids, (code, ids))

    code, _ = api("mehmet", "PUT", f"{E}/{hr['id']}", {"name": "değiştirildi"})
    check("OS3: yönetici başkasının taslağını düzenleyemez", code == 404, code)
    code, _ = api("mehmet", "GET", f"{E}/{hr['id']}/impact")
    check("OS3: yönetici başkasının taslağının etkisini göremez", code == 404, code)
    code, _ = api("mehmet", "DELETE", f"{E}/{hr['id']}")
    check("OS3: yönetici başkasının taslağını silemez", code == 404, code)
    code, _ = api("ayse", "GET", E)
    check("OS3: çalışan senaryolara erişemez", code == 403, code)

    code, imp = api("mehmet", "GET", f"{E}/{mine['id']}/impact")
    moves = ((imp or {}).get("scenario") or {}).get("moves") or []
    if code == 200 and imp.get("costVisible") is False:
        check("OS4: maaş görünmüyorsa planlanan maaş yanıtta yok", moves and all(m.get("plannedSalary") is None for m in moves)
              and "55555" not in str(imp) and imp["totals"]["hireCost"] is None, imp)
    else:
        check("OS4: yönetici kendi senaryosunun etkisini görür", code == 200, (code, imp))
    code, imp = api("admin", "GET", f"{E}/{mine['id']}/impact")
    check("OS4: İK maliyetle birlikte görür", code == 200 and imp["costVisible"] and imp["scenario"]["moves"][0]["plannedSalary"] == 55555, (code, imp))

    code, upd = api("mehmet", "PUT", f"{E}/{mine['id']}", {"name": f"TEST-OS {RUN} yönetici 2"})
    check("OS5: yönetici kendi taslağını düzenler", code == 200 and upd["name"].endswith("2"), (code, upd))
finally:
    for who, sid in created:
        api(who, "DELETE", f"{E}/{sid}")

print(f"FAILS: {len(FAIL)}")
sys.exit(1 if FAIL else 0)

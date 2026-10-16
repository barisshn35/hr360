#!/usr/bin/env python3
"""Ekip izin takvimi: GET /api/leave/leave-requests/team-calendar.

Denetlenenler: çalışan yalnızca kendisini adıyla görür (ad gösterme ayarı kapalı, varsayılan); departman
başı ekibini türüyle görür; çalışan başka departman seçemez; İK departman seçebilir; ad gösterme ayarı
açılınca çalışan ekip arkadaşını adıyla görür ama izin türünü görmez; 3 aydan uzun aralık reddedilir.
Ayar sonda eski hâline döner. Çalıştırma: timeout 120 python3 tests/integration/test_team_calendar.py
"""
import os
import sys

sys.path.insert(0, os.path.dirname(__file__))
from common import FAIL, api, check  # noqa: E402
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "support"))
from demo_ids import AYSE, ENG, MEHMET  # noqa: E402

FAIL.clear()
P = "/api/leave/leave-requests/team-calendar?from=2026-01-01&to=2026-03-31"
_, orig = api("ik", "GET", "/api/leave/leave-settings")


def save(names):
    body = {"dayHours": orig.get("customDayHours"), "conflictWarnEnabled": orig["conflictWarnEnabled"],
            "conflictThresholdPercent": orig["conflictThresholdPercent"], "carryOverMaxDays": orig.get("carryOverMaxDays"),
            "teamCalendarNames": names}
    return api("ik", "PUT", "/api/leave/leave-settings", body)[0]


try:
    save(False)
    code, a = api("ayse", "GET", P)
    check("TC1: çalışan takvimi açar", code == 200, code)
    check("TC1: ad ayarı kapalıyken yalnızca kendisi adıyla", [p["id"] for p in a["people"]] == [AYSE], a["people"])
    check("TC1: başkasının izni türüyle dönmez", all(e["employeeId"] == AYSE for e in a["entries"]), a["entries"])

    code, m = api("mehmet", "GET", P)
    ids = {p["id"] for p in m["people"]}
    check("TC2: departman başı ekibi görür", code == 200 and {AYSE, MEHMET} <= ids, m.get("people"))
    check("TC2: ayrıntı işaretli", all(p["detail"] for p in m["people"]), m["people"])

    code, _ = api("ayse", "GET", P + f"&departmentId={ENG}")
    check("TC3: çalışan departman seçemez", code == 403, code)
    code, h = api("ik", "GET", P + f"&departmentId={ENG}")
    check("TC3: İK departman seçer", code == 200 and {AYSE, MEHMET} <= {p["id"] for p in h["people"]}, (code, h))

    code, _ = api("ayse", "GET", "/api/leave/leave-requests/team-calendar?from=2026-01-01&to=2026-06-30")
    check("TC4: 3 aydan uzun aralık reddedilir", code == 400, code)

    check("TC5: ad ayarı kaydedilir", save(True) == 200)
    _, a = api("ayse", "GET", P)
    check("TC5: ayar açıkken ekip arkadaşı adıyla görünür", MEHMET in {p["id"] for p in a["people"]}, a["people"])
    check("TC5: ekip arkadaşının izin türü yine gizli",
          all(e["type"] is None for e in a["entries"] if e["employeeId"] != AYSE), a["entries"])
    check("TC5: ekip arkadaşının bekleyen talebi görünmez",
          all(e["status"] == "Approved" for e in a["entries"] if e["employeeId"] != AYSE), a["entries"])
finally:
    save(orig.get("teamCalendarNames", False))

print("\nSONUÇ:", "BAŞARILI" if not FAIL else f"{len(FAIL)} HATA: {FAIL}")
sys.exit(1 if FAIL else 0)

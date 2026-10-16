#!/usr/bin/env python3
"""Etkinlikler ve gönüllülük: /api/engagement/events.

Denetlenenler: doğrulama; yalnızca İK açar; kontenjan dolunca bekleme listesi ve sıra; vazgeçen olunca
sıradakinin otomatik alınması ve bildirimi; iptalde katılanlara bildirim ve yeni katılımın reddi;
katılım işaretleme yalnızca etkinlik başladıktan sonra; gönüllülük saatlerinin yıllık özete yazılması;
.ics çıktısı. Test kayıtları sonda silinir (başlık öneki "TEST-EVT").
Çalıştırma: timeout 120 python3 tests/integration/test_events.py
"""
import os
import subprocess
import sys
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timedelta, timezone

sys.path.insert(0, os.path.dirname(__file__))
from common import FAIL, api, check, tok  # noqa: E402
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "support"))
from demo_ids import AYSE, MEHMET  # noqa: E402

ROOT = os.path.join(os.path.dirname(__file__), "..", "..")
FAIL.clear()
B = "/api/engagement/events"


def psql(sql):
    out = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", "hr360_operational", "-Atc", sql],
                         capture_output=True, text=True, cwd=ROOT)
    return (out.stdout + out.stderr).strip()


def iso(dt):
    return dt.strftime("%Y-%m-%dT%H:%M:%SZ")


now = datetime.now(timezone.utc)
start, end = now + timedelta(days=3), now + timedelta(days=3, hours=3)
base = {"title": "TEST-EVT piknik", "description": "Belgrad ormanı", "kind": "Social", "startsAt": iso(start), "endsAt": iso(end),
        "location": "Belgrad", "onlineUrl": None, "capacity": 1, "rsvpDeadline": None, "volunteerHours": None, "announce": False}

try:
    code, _ = api("ayse", "POST", B, base)
    check("EV1: çalışan etkinlik açamaz", code == 403, code)
    code, _ = api("ik", "POST", B, {**base, "endsAt": iso(start - timedelta(hours=1))})
    check("EV1: bitiş başlangıçtan önce reddedilir", code == 400, code)
    code, _ = api("ik", "POST", B, {**base, "onlineUrl": "javascript:alert(1)"})
    check("EV1: http(s) dışı bağlantı reddedilir", code == 400, code)

    code, ev = api("ik", "POST", B, base)
    check("EV2: İK etkinlik açar", code == 200 and ev["status"] == "Published", (code, ev))
    eid = ev["id"]

    _, r = api("ayse", "POST", f"{B}/{eid}/rsvp", {"going": True})
    check("EV3: ilk kişi katılır", r["status"] == "Going", r)
    _, r = api("mehmet", "POST", f"{B}/{eid}/rsvp", {"going": True})
    check("EV3: kontenjan dolunca bekleme listesi", r["status"] == "Waitlist", r)
    _, lst = api("mehmet", "GET", B)
    mine = next(e for e in lst if e["id"] == eid)
    check("EV3: sıra numarası görünür", mine["myStatus"] == "Waitlist" and mine["myPosition"] == 1 and mine["going"] == 1, mine)

    api("ayse", "POST", f"{B}/{eid}/rsvp", {"going": False})
    _, lst = api("mehmet", "GET", B)
    mine = next(e for e in lst if e["id"] == eid)
    check("EV4: vazgeçince sıradaki alınır", mine["myStatus"] == "Going", mine)
    n = psql(f"SELECT count(*) FROM notification_messages WHERE \"TemplateCode\" = 'events.promoted' AND \"RecipientEmployeeId\" = '{MEHMET}' AND \"CreatedAt\" > now() - interval '5 minutes'")
    check("EV4: sıradakine bildirim gider", n == "1", n)

    code, _ = api("ik", "PUT", f"{B}/{eid}/attendance", {"employeeIds": [MEHMET]})
    check("EV5: başlamamış etkinlikte katılım işaretlenmez", code == 400, code)

    code, ics = api("mehmet", "GET", f"{B}/{eid}/ics")
    check("EV6: .ics indirilir", code == 200 and "BEGIN:VEVENT" in ics and "SUMMARY:TEST-EVT piknik" in ics, (code, str(ics)[:200]))

    code, _ = api("ik", "POST", f"{B}/{eid}/cancel")
    check("EV7: İK iptal eder", code == 200, code)
    n = psql(f"SELECT count(*) FROM notification_messages WHERE \"TemplateCode\" = 'events.cancelled' AND \"RecipientEmployeeId\" = '{MEHMET}' AND \"CreatedAt\" > now() - interval '5 minutes'")
    check("EV7: katılana iptal bildirimi", n == "1", n)
    code, _ = api("ayse", "POST", f"{B}/{eid}/rsvp", {"going": True})
    check("EV7: iptal edilmiş etkinliğe katılım reddedilir", code == 400, code)

    code, vol = api("ik", "POST", B, {**base, "title": "TEST-EVT fidan dikimi", "kind": "Volunteer", "capacity": None, "volunteerHours": 4})
    check("EV8: gönüllülük etkinliği açılır", code == 200 and float(vol["volunteerHours"]) == 4, (code, vol))
    api("ayse", "POST", f"{B}/{vol['id']}/rsvp", {"going": True})
    api("mehmet", "POST", f"{B}/{vol['id']}/rsvp", {"going": True})
    _, before = api("ayse", "GET", f"{B}/volunteering")
    # Etkinliği bu yılın içinde geçmişe taşı (katılım işaretlenebilsin)
    psql(f"UPDATE engagement_events SET \"StartsAt\" = now() - interval '3 hours', \"EndsAt\" = now() - interval '1 hour' WHERE \"Id\" = '{vol['id']}'")
    code, a = api("ik", "PUT", f"{B}/{vol['id']}/attendance", {"employeeIds": [AYSE]})
    check("EV8: katılım işaretlenir", code == 200 and a["attended"] == 1, (code, a))
    code, _ = api("ayse", "PUT", f"{B}/{vol['id']}/attendance", {"employeeIds": [AYSE]})
    check("EV8: çalışan katılım işaretleyemez", code == 403, code)
    _, after = api("ayse", "GET", f"{B}/volunteering")
    check("EV9: gönüllülük saati kişiye yazılır", float(after["mine"]) - float(before["mine"]) == 4, (before["mine"], after["mine"]))
    check("EV9: yalnız katılanlar sayılır", float(after["totalHours"]) - float(before["totalHours"]) == 4, (before["totalHours"], after["totalHours"]))
    _, past = api("ayse", "GET", B + "?past=true")
    check("EV10: geçmiş etkinlikler ayrı listelenir", any(e["id"] == vol["id"] for e in past), len(past))

    # Eşzamanlılık: son yer aynı anda gelen iki katılıma verilmez; çift tıklama ikinci kayıt açmaz.
    for w in ("ayse", "mehmet", "ik"):
        tok(w)
    code, race = api("ik", "POST", B, {**base, "title": "TEST-EVT yarış", "capacity": 1})
    rid = race["id"]
    with ThreadPoolExecutor(3) as ex:
        res = list(ex.map(lambda w: api(w, "POST", f"{B}/{rid}/rsvp", {"going": True}), ("ayse", "mehmet", "ik")))
    statuses = sorted(r[1]["status"] for r in res if r[0] == 200)
    check("EV11: aynı anda üç katılımdan yalnızca biri yer alır", [r[0] for r in res] == [200, 200, 200] and statuses == ["Going", "Waitlist", "Waitlist"], res)
    going = psql(f"SELECT count(*) FROM engagement_event_rsvps WHERE \"EventId\" = '{rid}' AND \"Status\" = 'Going'")
    check("EV11: veritabanında kontenjan aşılmaz", going == "1", going)
    api("ayse", "POST", f"{B}/{rid}/rsvp", {"going": False})
    api("mehmet", "POST", f"{B}/{rid}/rsvp", {"going": False})
    api("ik", "POST", f"{B}/{rid}/rsvp", {"going": False})
    with ThreadPoolExecutor(5) as ex:
        res = list(ex.map(lambda _: api("ayse", "POST", f"{B}/{rid}/rsvp", {"going": True})[0], range(5)))
    rows = psql(f"SELECT count(*) FROM engagement_event_rsvps WHERE \"EventId\" = '{rid}' AND \"EmployeeId\" = '{AYSE}'")
    check("EV12: aynı kişinin eşzamanlı beş isteği tek kayıt, hata yok", res == [200] * 5 and rows == "1", (res, rows))
finally:
    psql("DELETE FROM engagement_events WHERE \"Title\" LIKE 'TEST-EVT%'")
    psql("DELETE FROM notification_messages WHERE \"TemplateCode\" LIKE 'events.%' AND \"TenantSlug\" = 'demo'")

print("\nSONUÇ:", "BAŞARILI" if not FAIL else f"{len(FAIL)} HATA: {FAIL}")
sys.exit(1 if FAIL else 0)

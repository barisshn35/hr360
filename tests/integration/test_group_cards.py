#!/usr/bin/env python3
"""Grup kartı: /api/engagement/cards.

Denetlenenler: doğrulama (kendine kart yok, teslim zamanı aralığı); alıcı teslimden önce kartı
listede ve ayrıntıda göremez, imzalayamaz; aynı kişiye aynı türde ikinci açık kart 409; imza ekle /
güncelle (kişi başı bir); erken teslim yalnızca açan ya da İK, imzasız kart teslim edilemez;
zamanlayıcı teslim eder ve alıcıya bildirim gider, imzasız kart sessizce kapanır; teslimden sonra
imza yok. Test kayıtları sonda silinir (başlık öneki "TEST-CARD").
Çalıştırma: timeout 120 python3 tests/integration/test_group_cards.py
"""
import os
import subprocess
import sys
from datetime import datetime, timedelta, timezone

sys.path.insert(0, os.path.dirname(__file__))
from common import FAIL, api, check  # noqa: E402
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "support"))
from demo_ids import AYSE, MEHMET  # noqa: E402

ROOT = os.path.join(os.path.dirname(__file__), "..", "..")
FAIL.clear()
B = "/api/engagement/cards"


def psql(sql):
    out = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", "hr360_operational", "-Atc", sql],
                         capture_output=True, text=True, cwd=ROOT)
    return (out.stdout + out.stderr).strip()


def iso(dt):
    return dt.strftime("%Y-%m-%dT%H:%M:%SZ")


soon = iso(datetime.now(timezone.utc) + timedelta(days=2))
start = psql("SELECT now()")

try:
    code, _ = api("ayse", "POST", B, {"recipientEmployeeId": AYSE, "occasion": "Birthday", "title": "TEST-CARD kendime", "deliverAt": soon})
    check("GC1: kendine kart açılamaz", code == 400, code)
    code, _ = api("ayse", "POST", B, {"recipientEmployeeId": MEHMET, "occasion": "Birthday", "title": "TEST-CARD uzak", "deliverAt": iso(datetime.now(timezone.utc) + timedelta(days=90))})
    check("GC1: 60 günden uzak teslim reddedilir", code == 400, code)

    code, card = api("ayse", "POST", B, {"recipientEmployeeId": MEHMET, "occasion": "Birthday", "title": "TEST-CARD İyi ki doğdun Mehmet", "deliverAt": soon, "notifyTeam": False})
    check("GC2: kart açılır", code == 200 and card["signers"] == 0, (code, card))
    cid = card["id"]
    code, dup = api("ik", "POST", B, {"recipientEmployeeId": MEHMET, "occasion": "Birthday", "title": "TEST-CARD ikinci", "deliverAt": soon})
    check("GC2: aynı türde ikinci açık kart 409 ve mevcut kartı gösterir", code == 409 and dup.get("id") == cid, (code, dup))

    _, lst = api("mehmet", "GET", B)
    check("GC3: alıcı teslimden önce kartı listede görmez", all(c["id"] != cid for c in lst), [c["title"] for c in lst])
    code, _ = api("mehmet", "GET", f"{B}/{cid}")
    check("GC3: alıcı ayrıntıyı göremez", code == 404, code)
    code, _ = api("mehmet", "PUT", f"{B}/{cid}/sign", {"message": "ben de", "color": "blue"})
    check("GC3: alıcı imzalayamaz", code == 404, code)

    code, _ = api("ik", "POST", f"{B}/{cid}/deliver")
    check("GC4: imzasız kart teslim edilemez", code == 400, code)
    code, _ = api("ik", "PUT", f"{B}/{cid}/sign", {"message": "Nice yıllara! 🎂", "color": "pink"})
    check("GC4: imza eklenir", code == 200, code)
    api("ayse", "PUT", f"{B}/{cid}/sign", {"message": "İlk sürüm", "color": "nope"})
    code, s = api("ayse", "PUT", f"{B}/{cid}/sign", {"message": "Mutlu yıllar, iyi ki varsın!", "color": "green"})
    _, det = api("ayse", "GET", f"{B}/{cid}")
    mine = [x for x in det["signatures"] if x["mine"]]
    check("GC4: kişi başı tek imza, güncellenir", len(det["signatures"]) == 2 and len(mine) == 1 and mine[0]["message"].startswith("Mutlu") and mine[0]["color"] == "green", det["signatures"])
    code, _ = api("mehmet", "POST", f"{B}/{cid}/deliver")
    check("GC4: başkası erken teslim edemez", code in (403, 404), code)

    # Zamanlayıcı: teslim zamanı geçmiş kart teslim edilir, alıcıya haber gider.
    code, empty = api("ik", "POST", B, {"recipientEmployeeId": AYSE, "occasion": "Congrats", "title": "TEST-CARD boş", "deliverAt": soon, "notifyTeam": False})
    psql(f"UPDATE engagement_cards SET \"DeliverAt\" = now() - interval '1 minute' WHERE \"Id\" IN ('{cid}', '{empty['id']}')")
    code, run = api("ik", "POST", f"{B}/run")
    check("GC5: zamanlayıcı teslim eder", code == 200 and run["delivered"] >= 2, (code, run))
    n = psql(f"SELECT count(*) FROM notification_messages WHERE \"TemplateCode\" = 'cards.delivered' AND \"RecipientEmployeeId\" = '{MEHMET}' AND \"CreatedAt\" >= '{start}'")
    check("GC5: alıcıya bildirim gider", n == "1", n)
    n = psql(f"SELECT count(*) FROM notification_messages WHERE \"TemplateCode\" = 'cards.delivered' AND \"RecipientEmployeeId\" = '{AYSE}' AND \"CreatedAt\" >= '{start}'")
    check("GC5: imzasız kart için bildirim gitmez", n == "0", n)
    code, det = api("mehmet", "GET", f"{B}/{cid}")
    check("GC6: teslimden sonra alıcı kartı ve imzaları görür", code == 200 and len(det["signatures"]) == 2 and det["card"]["forMe"], (code, det))
    code, _ = api("ik", "PUT", f"{B}/{cid}/sign", {"message": "geç kaldım", "color": "blue"})
    check("GC6: teslimden sonra imza yok", code == 400, code)

    code, other = api("mehmet", "POST", B, {"recipientEmployeeId": AYSE, "occasion": "Farewell", "title": "TEST-CARD silinecek", "deliverAt": soon})
    code, _ = api("ayse", "DELETE", f"{B}/{other['id']}")
    check("GC7: başkasının kartını silemez", code in (403, 404), code)
    code, _ = api("mehmet", "DELETE", f"{B}/{other['id']}")
    check("GC7: açan kişi siler", code == 204, code)
finally:
    psql("DELETE FROM engagement_cards WHERE \"Title\" LIKE 'TEST-CARD%'")
    psql(f"DELETE FROM notification_messages WHERE \"TemplateCode\" LIKE 'cards.%' AND \"TenantSlug\" = 'demo' AND \"CreatedAt\" >= '{start}'")

print("\nSONUÇ:", "BAŞARILI" if not FAIL else f"{len(FAIL)} HATA: {FAIL}")
sys.exit(1 if FAIL else 0)

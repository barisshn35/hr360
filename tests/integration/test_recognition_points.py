#!/usr/bin/env python3
"""Takdir puanları ve ödül kataloğu: /api/engagement/recognition + /api/engagement/kudos.

Denetlenenler: kapalıyken puan verilemez; takdir başına üst sınır ve aylık bütçe; alıcının bakiyesi;
ayarı yalnızca İK değiştirir; ödül takası (puan düşer, stok azalır, tükenince reddedilir); eşzamanlı iki
takasta çift harcama olmaz; İK reddi puanı ve stoğu iade eder, bildirim gider; puanlı takdiri çalışan
silemez, İK silince puan geri alınır; İK puan ekler. Ayar test öncesi hâline döner, test kayıtları silinir.
Çalıştırma: timeout 120 python3 tests/integration/test_recognition_points.py
"""
import json
import os
import subprocess
import sys
from concurrent.futures import ThreadPoolExecutor

sys.path.insert(0, os.path.dirname(__file__))
from common import FAIL, api, check, tok  # noqa: E402
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "support"))
from demo_ids import AYSE, MEHMET  # noqa: E402

ROOT = os.path.join(os.path.dirname(__file__), "..", "..")
FAIL.clear()
R = "/api/engagement/recognition"
K = "/api/engagement/kudos"


def psql(sql):
    out = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", "hr360_operational", "-Atc", sql],
                         capture_output=True, text=True, cwd=ROOT)
    return (out.stdout + out.stderr).strip()


def balance(who):
    return api(who, "GET", f"{R}/me")[1]["balance"]


for w in ("ik", "ayse", "mehmet"):
    tok(w)
start = psql("SELECT now()")
orig = psql("""SELECT row_to_json(s) FROM engagement_recognition_settings s WHERE "TenantSlug" = 'demo'""")
reward_ids = []

try:
    code, _ = api("ayse", "PUT", f"{R}/settings", {"enabled": True, "monthlyAllowance": 100, "maxPerKudos": 50})
    check("RP1: çalışan ayarı değiştiremez", code == 403, code)
    api("ik", "PUT", f"{R}/settings", {"enabled": False, "monthlyAllowance": 100, "maxPerKudos": 50})
    code, r = api("ayse", "POST", K, {"toEmployeeId": MEHMET, "badge": "thanks", "message": "TEST-RP kapalı", "points": 10})
    check("RP1: kapalıyken puan verilemez", code == 400, (code, r))
    code, _ = api("ayse", "POST", K, {"toEmployeeId": MEHMET, "badge": "thanks", "message": "TEST-RP puansız", "points": 0})
    check("RP1: kapalıyken puansız takdir çalışır", code == 200, code)

    code, s = api("ik", "PUT", f"{R}/settings", {"enabled": True, "monthlyAllowance": 100, "maxPerKudos": 50})
    check("RP2: İK açar", code == 200 and s["enabled"], (code, s))
    m0 = balance("mehmet")
    given0 = api("ayse", "GET", f"{R}/me")[1]["givenThisMonth"]
    code, _ = api("ayse", "POST", K, {"toEmployeeId": MEHMET, "badge": "teamwork", "message": "TEST-RP fazla", "points": 60})
    check("RP2: takdir başına üst sınır", code == 400, code)
    left = 100 - given0
    code, k1 = api("ayse", "POST", K, {"toEmployeeId": MEHMET, "badge": "teamwork", "message": "TEST-RP 40", "points": min(40, left)})
    check("RP2: puanlı takdir gider", code == 200, (code, k1))
    check("RP2: alıcının bakiyesi artar", balance("mehmet") - m0 == min(40, left), (m0, balance("mehmet")))
    me = api("ayse", "GET", f"{R}/me")[1]
    while me["remaining"] >= 50:  # bütçeyi üst sınırın altına indir
        api("ayse", "POST", K, {"toEmployeeId": MEHMET, "badge": "thanks", "message": "TEST-RP harca", "points": 40})
        me = api("ayse", "GET", f"{R}/me")[1]
    code, r = api("ayse", "POST", K, {"toEmployeeId": MEHMET, "badge": "thanks", "message": "TEST-RP bütçe", "points": me["remaining"] + 1})
    check("RP3: aylık bütçe aşılamaz", code == 400 and "kaldı" in (r or {}).get("message", ""), (code, r, me))
    code, _ = api("mehmet", "POST", K, {"toEmployeeId": MEHMET, "badge": "thanks", "message": "TEST-RP kendime", "points": 5})
    check("RP3: kendine puan verilemez", code == 400, code)

    code, rw = api("ik", "POST", f"{R}/rewards", {"title": "TEST-RP Kahve kuponu", "description": "Kafeteryada geçerli", "cost": 30, "stock": 1})
    check("RP4: İK ödül ekler", code == 200 and rw["stock"] == 1, (code, rw))
    reward_ids.append(rw["id"])
    code, _ = api("ayse", "POST", f"{R}/rewards", {"title": "TEST-RP x", "cost": 1, "stock": None})
    check("RP4: çalışan ödül ekleyemez", code == 403, code)

    # Mehmet'in bakiyesi tam bir takasa yetecek kadar olsun; aynı anda iki takas denensin.
    b = balance("mehmet")
    code, rw2 = api("ik", "POST", f"{R}/rewards", {"title": "TEST-RP Yarım gün izin", "description": None, "cost": b if b > 0 else 1, "stock": None})
    reward_ids.append(rw2["id"])
    if b <= 0:
        api("ik", "POST", f"{R}/grant", {"employeeId": MEHMET, "points": 1, "note": "TEST-RP test"})
    with ThreadPoolExecutor(2) as ex:
        res = list(ex.map(lambda _: api("mehmet", "POST", f"{R}/rewards/{rw2['id']}/redeem")[0], range(2)))
    check("RP5: eşzamanlı iki takastan yalnız biri geçer", sorted(res) == [200, 400], res)
    check("RP5: bakiye eksiye düşmez", balance("mehmet") == 0, balance("mehmet"))

    api("ik", "POST", f"{R}/grant", {"employeeId": MEHMET, "points": 30, "note": "TEST-RP yarışma"})
    n = psql(f"SELECT count(*) FROM notification_messages WHERE \"TemplateCode\" = 'recognition.grant' AND \"RecipientEmployeeId\" = '{MEHMET}' AND \"CreatedAt\" >= '{start}'")
    check("RP6: İK puan ekler ve bildirim gider", balance("mehmet") == 30 and n == "1", (balance("mehmet"), n))
    code, red = api("mehmet", "POST", f"{R}/rewards/{rw['id']}/redeem")
    check("RP6: takas puanı düşer", code == 200 and red["balance"] == 0 and balance("mehmet") == 0, (code, red))
    stock = [x for x in api("ik", "GET", f"{R}/rewards?all=true")[1] if x["id"] == rw["id"]][0]["stock"]
    check("RP6: stok azalır", stock == 0, stock)
    api("ik", "POST", f"{R}/grant", {"employeeId": AYSE, "points": 30, "note": "TEST-RP stok"})
    code, r = api("ayse", "POST", f"{R}/rewards/{rw['id']}/redeem")
    check("RP6: tükenen ödül takas edilemez", code == 400 and "tükendi" in r["message"], (code, r))

    code, _ = api("ik", "POST", f"{R}/redemptions/{red['id']}/decide", {"fulfilled": False, "note": None})
    check("RP7: gerekçesiz ret reddedilir", code == 400, code)
    code, _ = api("ik", "POST", f"{R}/redemptions/{red['id']}/decide", {"fulfilled": False, "note": "Kupon dönemi bitti."})
    check("RP7: İK reddeder", code == 200, code)
    stock = [x for x in api("ik", "GET", f"{R}/rewards?all=true")[1] if x["id"] == rw["id"]][0]["stock"]
    check("RP7: ret puanı ve stoğu iade eder", balance("mehmet") == 30 and stock == 1, (balance("mehmet"), stock))
    mine = api("mehmet", "GET", f"{R}/redemptions/me")[1]
    check("RP7: çalışan kendi talebinin durumunu görür", any(x["id"] == red["id"] and x["status"] == "Rejected" for x in mine), mine[:2])
    code, _ = api("ik", "POST", f"{R}/redemptions/{red['id']}/decide", {"fulfilled": True, "note": None})
    check("RP7: sonuçlanan talep yeniden karara bağlanamaz", code == 400, code)

    before = balance("mehmet")
    code, _ = api("ayse", "DELETE", f"{K}/{k1['id']}")
    check("RP8: puanlı takdiri gönderen silemez", code == 400, code)
    code, _ = api("ik", "DELETE", f"{K}/{k1['id']}")
    check("RP8: İK siler, puan geri alınır", code == 204 and before - balance("mehmet") == min(40, left), (code, before, balance("mehmet")))
finally:
    psql("DELETE FROM engagement_kudos WHERE \"Message\" LIKE 'TEST-RP%'")
    psql(f"DELETE FROM engagement_points_ledger WHERE \"TenantSlug\" = 'demo' AND \"CreatedAt\" >= '{start}'")
    psql("DELETE FROM engagement_reward_redemptions WHERE \"RewardTitle\" LIKE 'TEST-RP%'")
    psql("DELETE FROM engagement_rewards WHERE \"Title\" LIKE 'TEST-RP%'")
    psql(f"DELETE FROM notification_messages WHERE \"TenantSlug\" = 'demo' AND \"CreatedAt\" >= '{start}' AND (\"TemplateCode\" LIKE 'recognition.%' OR \"TemplateCode\" = 'engagement.kudos')")
    if orig:
        o = json.loads(orig)
        psql(f"""UPDATE engagement_recognition_settings SET "Enabled" = {str(o['Enabled']).lower()}, "MonthlyAllowance" = {o['MonthlyAllowance']}, "MaxPerKudos" = {o['MaxPerKudos']} WHERE "TenantSlug" = 'demo'""")
    else:
        psql("DELETE FROM engagement_recognition_settings WHERE \"TenantSlug\" = 'demo'")

print("\nSONUÇ:", "BAŞARILI" if not FAIL else f"{len(FAIL)} HATA: {FAIL}")
sys.exit(1 if FAIL else 0)

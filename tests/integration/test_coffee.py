#!/usr/bin/env python3
"""Kahve eşleştirme: /api/engagement/coffee.

Denetlenenler: kapalıyken katılım yok; ayarı yalnızca İK değiştirir; dört katılımcı iki ikiliye
ayrılır, herkes yalnızca kendi eşini görür ve bildirim alır; aynı dönemde ikinci tur açılmaz;
sonraki turda aynı ikili tekrar etmez; "görüştük" yalnızca eşleşmedekiler tarafından işaretlenir;
İK özetinde kimin kiminle eşleştiği yoktur; ayrılan katılımcı listeden çıkar.
Ayar test öncesi hâline döner; test turları, katılımlar ve bildirimler silinir.
Çalıştırma: timeout 120 python3 tests/integration/test_coffee.py
"""
import json
import os
import subprocess
import sys

sys.path.insert(0, os.path.dirname(__file__))
from common import FAIL, api, check, tok  # noqa: E402
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "support"))
from demo_ids import AYSE, ELIF, MEHMET, ZEYNEP  # noqa: E402

ROOT = os.path.join(os.path.dirname(__file__), "..", "..")
FAIL.clear()
B = "/api/engagement/coffee"


def psql(sql):
    out = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", "hr360_operational", "-Atc", sql],
                         capture_output=True, text=True, cwd=ROOT)
    return (out.stdout + out.stderr).strip()


def pairs():
    rows = psql("""SELECT array_to_string("MemberIds", ',') FROM engagement_coffee_matches m JOIN engagement_coffee_rounds r ON r."Id" = m."RoundId"
                   WHERE r."TenantSlug" = 'demo' AND r."PeriodKey" NOT LIKE 'TEST-%'""").splitlines()
    return {frozenset(r.split(",")) for r in rows if r}


for w in ("ik", "ayse", "mehmet"):
    tok(w)
start = psql("SELECT now()")
orig = psql("""SELECT row_to_json(s) FROM engagement_coffee_settings s WHERE "TenantSlug" = 'demo'""")
existing_rounds = psql("""SELECT count(*) FROM engagement_coffee_rounds WHERE "TenantSlug" = 'demo'""")
if existing_rounds != "0":
    # Gerçek turlara dokunmamak için test atlanır (temizlik adımı bu kiracının yeni turlarını siler).
    print(f"ATLANDI: demo kiracısında {existing_rounds} kahve turu var")
    sys.exit(0)

try:
    api("ik", "PUT", f"{B}/settings", {"enabled": False, "cadence": "Biweekly", "dayOfWeek": 1, "crossDepartment": True})
    code, _ = api("ayse", "PUT", f"{B}/me", {"joined": True})
    check("CF1: kapalıyken katılınamaz", code == 400, code)
    code, _ = api("ayse", "PUT", f"{B}/settings", {"enabled": True, "cadence": "Weekly", "dayOfWeek": 1, "crossDepartment": True})
    check("CF1: çalışan ayarı değiştiremez", code == 403, code)
    code, _ = api("ik", "PUT", f"{B}/settings", {"enabled": True, "cadence": "Daily", "dayOfWeek": 1, "crossDepartment": True})
    check("CF1: geçersiz sıklık reddedilir", code == 400, code)
    code, s = api("ik", "PUT", f"{B}/settings", {"enabled": True, "cadence": "Weekly", "dayOfWeek": 1, "crossDepartment": True})
    check("CF1: İK açar", code == 200 and s["enabled"], (code, s))

    for w in ("ayse", "mehmet", "ik"):
        api(w, "PUT", f"{B}/me", {"joined": True})
    psql(f"""INSERT INTO engagement_coffee_participants ("Id","TenantSlug","EmployeeId","JoinedAt") VALUES (gen_random_uuid(),'demo','{ZEYNEP}',now()) ON CONFLICT DO NOTHING""")
    _, me = api("ayse", "GET", f"{B}/me")
    check("CF2: katılım görünür", me["joined"] and me["participants"] == 4, me)

    code, r = api("ik", "POST", f"{B}/run")
    check("CF3: tur iki ikili oluşturur", code == 200 and r["groups"] == 2, (code, r))
    first = pairs()
    check("CF3: herkes tam bir kez eşlendi", len(first) == 2 and set().union(*first) == {AYSE, MEHMET, ELIF, ZEYNEP}, first)
    _, me = api("ayse", "GET", f"{B}/me")
    m = me["matches"][0]
    check("CF3: çalışan yalnızca kendi eşini görür", len(m["with"]) == 1 and m["with"][0]["employeeId"] != AYSE and m["with"][0]["name"] != "—", m)
    n = psql(f"SELECT count(*) FROM notification_messages WHERE \"TemplateCode\" = 'coffee.match' AND \"RecipientEmployeeId\" = '{AYSE}' AND \"CreatedAt\" >= '{start}'")
    check("CF3: eşleşme bildirimi (uygulama içi + e-posta)", n == "2", n)

    code, r = api("ik", "POST", f"{B}/run")
    check("CF4: aynı dönemde ikinci tur açılmaz", code == 200 and r["groups"] == 0, (code, r))

    code, _ = api("ayse", "POST", f"{B}/matches/{m['id']}/met", {"met": True})
    check("CF5: eşleşmedeki kişi 'görüştük' işaretler", code == 200, code)
    outsider = next(w for w, eid in (("mehmet", MEHMET), ("ik", ELIF)) if eid != m["with"][0]["employeeId"])
    code, _ = api(outsider, "POST", f"{B}/matches/{m['id']}/met", {"met": False})
    check("CF5: başkası işaretleyemez", code == 404, code)

    code, hr = api("ik", "GET", f"{B}/settings")
    check("CF6: İK özeti sayıları gösterir", code == 200 and hr["participants"] == 4 and hr["stats"]["groups"] == 2 and hr["stats"]["met"] == 1, hr)
    check("CF6: İK özetinde eşleşme listesi yok", "matches" not in hr and AYSE not in json.dumps(hr), list(hr))

    # Sonraki dönem: önceki turu geçmişe taşı, yeniden çalıştır; ikililer tekrar etmemeli.
    psql("""UPDATE engagement_coffee_rounds SET "PeriodKey" = 'X-' || "PeriodKey", "CreatedAt" = now() - interval '7 days' WHERE "TenantSlug" = 'demo'""")
    code, r = api("ik", "POST", f"{B}/run")
    rows = psql("""SELECT array_to_string(m."MemberIds", ',') FROM engagement_coffee_matches m JOIN engagement_coffee_rounds r ON r."Id" = m."RoundId"
                   WHERE r."TenantSlug" = 'demo' AND r."PeriodKey" NOT LIKE 'X-%'""").splitlines()
    second = {frozenset(x.split(",")) for x in rows if x}
    check("CF7: sonraki turda aynı ikili tekrar etmez", code == 200 and r["groups"] == 2 and not (first & second), (first, second))

    api("ayse", "PUT", f"{B}/me", {"joined": False})
    _, me = api("ayse", "GET", f"{B}/me")
    check("CF8: ayrılan katılımcı listeden çıkar", not me["joined"] and me["participants"] == 3, me)
finally:
    psql(f"""DELETE FROM engagement_coffee_rounds WHERE "TenantSlug" = 'demo' AND "CreatedAt" >= '{start}'::timestamptz - interval '8 days'""")
    psql(f"""DELETE FROM engagement_coffee_participants WHERE "TenantSlug" = 'demo' AND "JoinedAt" >= '{start}'""")
    psql(f"""DELETE FROM notification_messages WHERE "TemplateCode" = 'coffee.match' AND "TenantSlug" = 'demo' AND "CreatedAt" >= '{start}'""")
    if orig:
        o = json.loads(orig)
        psql(f"""UPDATE engagement_coffee_settings SET "Enabled" = {str(o['Enabled']).lower()}, "Cadence" = '{o['Cadence']}', "DayOfWeek" = {o['DayOfWeek']}, "CrossDepartment" = {str(o['CrossDepartment']).lower()} WHERE "TenantSlug" = 'demo'""")
    else:
        psql("DELETE FROM engagement_coffee_settings WHERE \"TenantSlug\" = 'demo'")

print("\nSONUÇ:", "BAŞARILI" if not FAIL else f"{len(FAIL)} HATA: {FAIL}")
sys.exit(1 if FAIL else 0)

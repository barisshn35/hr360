#!/usr/bin/env python3
"""Rozet koleksiyonu: /api/engagement/badges.

Denetlenenler: kişi kendi koleksiyonunu ilerlemesiyle görür (kıdem rozeti yıl dönümü tarihli); yeni
ölçüte ulaşınca rozet kazanılır, tarih kaydedilir ve bir kez bildirim gider (ikinci sorguda tekrar
gitmez); başkasının profilinde yalnızca kazanılmış rozetler, ilerleme sayıları olmadan.
Test etkinlikleri (başlık öneki "TEST-BADGE"), bu testin eklediği rozet kaydı ve bildirimler silinir.
Çalıştırma: timeout 120 python3 tests/integration/test_badges.py
"""
import os
import subprocess
import sys

sys.path.insert(0, os.path.dirname(__file__))
from common import FAIL, api, check  # noqa: E402
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "support"))
from demo_ids import AYSE  # noqa: E402

ROOT = os.path.join(os.path.dirname(__file__), "..", "..")
FAIL.clear()
B = "/api/engagement/badges"


def psql(sql):
    out = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", "hr360_operational", "-Atc", sql],
                         capture_output=True, text=True, cwd=ROOT)
    return (out.stdout + out.stderr).strip()


def badge(r, bid):
    return next((b for b in r["badges"] if b["id"] == bid), None)


start = psql("SELECT now()")
_, mine = api("ayse", "GET", B)
if badge(mine, "events-5")["earned"]:
    print("ATLANDI: Ayşe 'events-5' rozetini zaten kazanmış")
    sys.exit(0)

try:
    check("BD1: kendi koleksiyonu tüm rozetlerle ve ilerlemeyle gelir", mine["mine"] and len(mine["badges"]) >= 20 and all("target" in b for b in mine["badges"]), len(mine["badges"]))
    t1 = badge(mine, "tenure-1")
    check("BD1: kıdem rozeti yıl dönümü tarihli", t1["earned"] and t1["earnedAt"] and t1["earnedAt"] < start, t1)
    ev = badge(mine, "events-5")
    check("BD1: kazanılmamış rozette ilerleme", not ev["earned"] and ev["target"] == 5 and ev["value"] < 5, ev)

    # Beş geçmiş etkinliğe katılım: events-5 rozeti kazanılmalı.
    for i in range(5):
        psql(f"""WITH e AS (INSERT INTO engagement_events ("Id","TenantSlug","Title","Kind","StartsAt","EndsAt","Status","CreatedByUserId")
                 VALUES (gen_random_uuid(),'demo','TEST-BADGE {i}','Social',now() - interval '{i + 2} days',now() - interval '{i + 2} days' + interval '2 hours','Published','test') RETURNING "Id")
                 INSERT INTO engagement_event_rsvps ("Id","TenantSlug","EventId","EmployeeId","Name","Status","Attended") SELECT gen_random_uuid(),'demo',e."Id",'{AYSE}','Ayşe','Going',true FROM e""")
    _, after = api("ayse", "GET", B)
    ev = badge(after, "events-5")
    check("BD2: ölçüte ulaşınca rozet kazanılır ve tarihlenir", ev["earned"] and ev["earnedAt"] and ev["value"] == 5, ev)
    q = f"SELECT count(*) FROM notification_messages WHERE \"TemplateCode\" = 'badges.earned' AND \"RecipientEmployeeId\" = '{AYSE}' AND \"CreatedAt\" >= '{start}'"
    check("BD2: yeni rozet bildirimi gider", psql(q) == "1", psql(q))
    api("ayse", "GET", B)
    check("BD2: bildirim tekrar gitmez", psql(q) == "1", psql(q))

    code, other = api("mehmet", "GET", f"{B}?employeeId={AYSE}")
    check("BD3: başkası yalnızca kazanılmış rozetleri görür", code == 200 and not other["mine"] and other["badges"]
          and all(b["earned"] for b in other["badges"]) and any(b["id"] == "events-5" for b in other["badges"]), other)
    check("BD3: başkasına ilerleme sayıları gitmez", all("value" not in b and "target" not in b for b in other["badges"]), other["badges"][:1])
finally:
    psql("DELETE FROM engagement_events WHERE \"Title\" LIKE 'TEST-BADGE%'")
    psql(f"DELETE FROM engagement_badge_awards WHERE \"TenantSlug\" = 'demo' AND \"EmployeeId\" = '{AYSE}' AND \"BadgeId\" = 'events-5' AND \"AwardedAt\" >= '{start}'")
    psql(f"DELETE FROM notification_messages WHERE \"TemplateCode\" = 'badges.earned' AND \"TenantSlug\" = 'demo' AND \"CreatedAt\" >= '{start}'")

print("\nSONUÇ:", "BAŞARILI" if not FAIL else f"{len(FAIL)} HATA: {FAIL}")
sys.exit(1 if FAIL else 0)

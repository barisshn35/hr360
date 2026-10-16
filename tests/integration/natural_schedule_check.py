#!/usr/bin/env python3
"""Zamanlanmış işlerin kendi zamanlarında çalıştığının gözlemi (elle tetikleme YOK). Uzun sürer
(~1 saat); düzenli test takımında değildir, sürüm öncesi elle çalıştırılır.

  - Grup kartı: teslim zamanı 6 dk sonra; CardWorker (5 dk'da bir) kendiliğinden teslim eder, bildirim gider.
  - Kahve eşleştirme: bugün eşleştirme günü; CoffeeWorker (15 dk'da bir) turu kendiliğinden açar.
  - Rozet: Ayşe'ye 5 etkinlik katılımı; BadgeService (açılıştan 1 dk sonra) rozeti yazar ve bildirir.
  - Ayın çalışanı: açılış bugün bir sonraki tam saatte; EomWorker (5 dk'da bir) dönemi önce kurar,
    saat gelince oylamayı açar ve bildirim gönderir.
Servis bir kez yeniden başlatılır (ilk turların zamanı belli olsun). Sonda her şey eski hâline döner.
Çalıştırma: timeout 5400 python3 tests/integration/natural_schedule_check.py
"""
import datetime as dt
import json
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(__file__))
from common import FAIL, api, check, tok  # noqa: E402
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "support"))
from demo_ids import AYSE, MEHMET, ZEYNEP  # noqa: E402

ROOT = os.path.join(os.path.dirname(__file__), "..", "..")
FAIL.clear()


def psql(sql):
    out = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", "hr360_operational", "-Atc", sql],
                         capture_output=True, text=True, cwd=ROOT)
    return (out.stdout + out.stderr).strip()


def log(msg):
    print(f"{dt.datetime.now():%H:%M:%S} {msg}", flush=True)


for w in ("ik", "ayse", "mehmet"):
    tok(w)
start = psql("SELECT now()")
tr_now = dt.datetime.now(dt.timezone(dt.timedelta(hours=3)))
open_hour = tr_now.hour + 1
if open_hour > 23 or tr_now.minute > 50:
    print("ATLANDI: bugün bir sonraki tam saat yok ya da çok yakın; başka bir saatte çalıştırın")
    sys.exit(0)
if psql("SELECT count(*) FROM engagement_coffee_rounds WHERE \"TenantSlug\" = 'demo'") != "0":
    print("ATLANDI: demo kiracısında gerçek kahve turu var")
    sys.exit(0)
eom_orig = api("ik", "GET", "/api/engagement/eom/settings")[1]["settings"]
coffee_orig = psql("""SELECT row_to_json(s) FROM engagement_coffee_settings s WHERE "TenantSlug" = 'demo'""")

try:
    # Kart: 6 dk sonra teslim
    deliver = (dt.datetime.now(dt.timezone.utc) + dt.timedelta(minutes=6)).strftime("%Y-%m-%dT%H:%M:%SZ")
    code, card = api("ayse", "POST", "/api/engagement/cards", {"recipientEmployeeId": MEHMET, "occasion": "Congrats", "title": "TEST-NAT kart", "deliverAt": deliver, "notifyTeam": False})
    api("ik", "PUT", f"/api/engagement/cards/{card['id']}/sign", {"message": "Tebrikler!", "color": "green"})
    # Kahve: bugün eşleştirme günü
    api("ik", "PUT", "/api/engagement/coffee/settings", {"enabled": True, "cadence": "Weekly", "dayOfWeek": tr_now.isoweekday(), "crossDepartment": True})
    for w in ("ayse", "mehmet", "ik"):
        api(w, "PUT", "/api/engagement/coffee/me", {"joined": True})
    psql(f"""INSERT INTO engagement_coffee_participants VALUES (gen_random_uuid(),'demo','{ZEYNEP}',now()) ON CONFLICT DO NOTHING""")
    # Rozet: ilk eşitleme (sessiz) yapılmış olsun, sonra 5 katılım
    api("ayse", "GET", "/api/engagement/badges")
    had = psql(f"""SELECT count(*) FROM engagement_badge_awards WHERE "EmployeeId" = '{AYSE}' AND "BadgeId" = 'events-5'""")
    for i in range(5):
        psql(f"""WITH e AS (INSERT INTO engagement_events ("Id","TenantSlug","Title","Kind","StartsAt","EndsAt","Status","CreatedByUserId")
                 VALUES (gen_random_uuid(),'demo','TEST-NAT {i}','Social',now() - interval '{i + 2} days',now() - interval '{i + 2} days' + interval '1 hour','Published','test') RETURNING "Id")
                 INSERT INTO engagement_event_rsvps ("Id","TenantSlug","EventId","EmployeeId","Name","Status","Attended") SELECT gen_random_uuid(),'demo',e."Id",'{AYSE}','Ayşe','Going',true FROM e""")
    # Ayın çalışanı: bugün, bir sonraki tam saatte açılış
    s = {**eom_orig, "enabled": True, "startDay": tr_now.day, "openHour": open_hour, "process": "Direct", "scope": "TopLevel", "reminderDaysBefore": 1}
    code, r = api("ik", "PUT", "/api/engagement/eom/settings", s)
    check("Hazırlık: ayın çalışanı ayarı kaydedildi", code == 200, (code, r))
    log(f"Hazırlık tamam; kart teslimi {deliver}, oylama açılışı {open_hour}:00 (TR). Servis yeniden başlatılıyor.")
    subprocess.run(["docker", "compose", "restart", "engagement-service"], cwd=ROOT, capture_output=True)
    t0 = time.time()

    seen = {}
    deadline = t0 + (open_hour * 60 - (tr_now.hour * 60 + tr_now.minute)) * 60 + 7 * 60
    while time.time() < deadline and len(seen) < 5:
        time.sleep(30)
        if "card" not in seen and psql(f"""SELECT "DeliveredAt" IS NOT NULL FROM engagement_cards WHERE "Id" = '{card['id']}'""") == "t":
            seen["card"] = time.time() - t0; log(f"kart teslim edildi (+{seen['card']:.0f} sn)")
        if "coffee" not in seen and psql("SELECT count(*) FROM engagement_coffee_rounds WHERE \"TenantSlug\" = 'demo'") == "1":
            seen["coffee"] = time.time() - t0; log(f"kahve turu açıldı (+{seen['coffee']:.0f} sn)")
        if "badge" not in seen and psql(f"""SELECT count(*) FROM engagement_badge_awards WHERE "EmployeeId" = '{AYSE}' AND "BadgeId" = 'events-5'""") == "1":
            seen["badge"] = time.time() - t0; log(f"rozet yazıldı (+{seen['badge']:.0f} sn)")
        st = psql(f"""SELECT "Status" FROM engagement_eom_periods WHERE "TenantSlug" = 'demo' AND "CreatedAt" >= '{start}' ORDER BY "CreatedAt" DESC LIMIT 1""")
        if "eom_sched" not in seen and st:
            seen["eom_sched"] = time.time() - t0; log(f"ayın çalışanı dönemi kuruldu: {st} (+{seen['eom_sched']:.0f} sn)")
        if "eom_open" not in seen and st == "Voting":
            seen["eom_open"] = time.time() - t0; log(f"oylama kendiliğinden açıldı (+{seen['eom_open']:.0f} sn)")

    n = lambda code, who: psql(f"""SELECT count(*) FROM notification_messages WHERE "TemplateCode" = '{code}' AND "RecipientEmployeeId" = '{who}' AND "CreatedAt" >= '{start}'""")  # noqa: E731
    check("Kart: elle tetiklemeden teslim edildi", "card" in seen, seen)
    check("Kart: alıcıya bildirim", n("cards.delivered", MEHMET) == "1", n("cards.delivered", MEHMET))
    check("Kahve: tur kendiliğinden açıldı, iki ikili", "coffee" in seen and psql("""SELECT count(*) FROM engagement_coffee_matches m JOIN engagement_coffee_rounds r ON r."Id" = m."RoundId" WHERE r."TenantSlug" = 'demo'""") == "2", seen)
    check("Kahve: katılımcılara bildirim", n("coffee.match", AYSE) == "2", n("coffee.match", AYSE))
    check("Rozet: tarama kendiliğinden yazdı", "badge" in seen and had == "0", (seen, had))
    check("Rozet: bildirim gitti", n("badges.earned", AYSE) == "1", n("badges.earned", AYSE))
    check("Ayın çalışanı: dönem açılış saatinden önce kuruldu", "eom_sched" in seen, seen)
    check("Ayın çalışanı: saat gelince oylama kendiliğinden açıldı", "eom_open" in seen, seen)
    mails = psql(f"""SELECT count(*) FROM notification_messages WHERE "TemplateCode" LIKE 'eom.%' AND "TenantSlug" = 'demo' AND "CreatedAt" >= '{start}'""")
    check("Ayın çalışanı: açılış bildirimleri gitti", mails not in ("", "0"), mails)
finally:
    psql(f"""DELETE FROM engagement_eom_periods WHERE "TenantSlug" = 'demo' AND "CreatedAt" >= '{start}'""")
    api("ik", "PUT", "/api/engagement/eom/settings", eom_orig)
    psql("DELETE FROM engagement_cards WHERE \"Title\" LIKE 'TEST-NAT%'")
    psql("DELETE FROM engagement_events WHERE \"Title\" LIKE 'TEST-NAT%'")
    psql(f"""DELETE FROM engagement_badge_awards WHERE "EmployeeId" = '{AYSE}' AND "BadgeId" = 'events-5' AND "AwardedAt" >= '{start}'""")
    psql("DELETE FROM engagement_coffee_rounds WHERE \"TenantSlug\" = 'demo'")
    psql(f"""DELETE FROM engagement_coffee_participants WHERE "TenantSlug" = 'demo' AND "JoinedAt" >= '{start}'""")
    if coffee_orig:
        o = json.loads(coffee_orig)
        psql(f"""UPDATE engagement_coffee_settings SET "Enabled" = {str(o['Enabled']).lower()}, "Cadence" = '{o['Cadence']}', "DayOfWeek" = {o['DayOfWeek']}, "CrossDepartment" = {str(o['CrossDepartment']).lower()} WHERE "TenantSlug" = 'demo'""")
    else:
        psql("DELETE FROM engagement_coffee_settings WHERE \"TenantSlug\" = 'demo'")
    psql(f"""DELETE FROM notification_messages WHERE "TenantSlug" = 'demo' AND "CreatedAt" >= '{start}' AND ("TemplateCode" LIKE 'eom.%' OR "TemplateCode" LIKE 'cards.%' OR "TemplateCode" = 'coffee.match' OR "TemplateCode" = 'badges.earned')""")

print("\nSONUÇ:", "BAŞARILI" if not FAIL else f"{len(FAIL)} HATA: {FAIL}")
sys.exit(1 if FAIL else 0)

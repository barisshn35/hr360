#!/usr/bin/env python3
"""Ayın çalışanı oylaması: /api/engagement/eom/*.

Demo şirketinde Mühendislik birimi Mehmet (departman başı → yönetici katsayısı) ve Ayşe'den oluşur;
Elif İK departmanında tek kişi: oylama birimi kurulmaz, Mühendislik birimine oy veremez.

Denetlenenler:
  EM1  ayar doğrulaması ve yetki (çalışan ayarı değiştiremez)
  EM2  "şimdi başlat": dönem açılır, birim ve adaylar (kendisi aday listesinde yok)
  EM3  oy kuralları: kendine oy yok, aday olmayan yok, üye olmayan yok, gerekçe sınırı, oy değiştirme
  EM4  gizli kip (WinnerOnly): oy satırında kimlik yok, kapanınca dönem tuzu silinir; sonuç açıklanmadan
       çalışan sonuç göremez, İK katılımı görür
  EM5  katsayılı sayım: Mehmet'in (yönetici, 2) oyu Ayşe'nin (1) oyunu geçer; kazanan ve ana sayfa kartı
  EM6  görünürlük: WinnerOnly'de çalışan sıralamayı/oyları görmez, İK sıralamayı görür ama oyları görmez
  EM7  RevealAfter + Department: sonuç açıklanınca kim kime oy verdi birime açılır
  EM8  beraberlik: dönem Tie'da bekler, İK çözer, sonra kapanır
  EM9  bildirimler: oylama açılınca birim üyelerine uygulama içi + e-posta kaydı
  EM10 modül kapalıyken çalışanın ekranı ve ana sayfa kartı boş
  EM11 aday gösterme → finalistlerle oylama
  EM13 kazananlar sıralaması (şirket geneli ve departman)
  EM12 iki aşamalı: alt birim kazananı finale kalır, ana departman kazananı belirlenir (geçici alt departmanla)

Test kaydı bırakmaz: kendi dönemlerini ve eom.* bildirimlerini siler, ayarı eski hâline getirir.
Çalıştırma: timeout 180 python3 tests/integration/test_employee_of_month.py
"""
import json
import os
import subprocess
import sys

sys.path.insert(0, os.path.dirname(__file__))
from common import FAIL, api, check  # noqa: E402
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "support"))
from demo_ids import AYSE, MEHMET  # noqa: E402

ROOT = os.path.join(os.path.dirname(__file__), "..", "..")
FAIL.clear()
E = "/api/engagement/eom"
periods = []


def psql(sql):
    out = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", "hr360_operational", "-Atc", sql],
                         capture_output=True, text=True, cwd=ROOT)
    return (out.stdout + out.stderr).strip()


orig = psql("SELECT row_to_json(s) FROM engagement_eom_settings s WHERE \"TenantSlug\" = 'demo'")
BASE = {"enabled": True, "cadence": "Monthly", "scope": "TopLevel", "process": "Direct", "visibility": "WinnerOnly",
        "revealTo": "HrAndHead", "startDay": 1, "openHour": 9, "nominationDays": 2, "finalistCount": 5, "votingDays": 3,
        "stage2Days": 1, "reminderDaysBefore": 2, "cooldownPeriods": 0, "weightEmployee": 1, "weightManager": 2,
        "weightHr": 2, "allowComment": True}


def settings(**kw):
    code, body = api("ik", "PUT", f"{E}/settings", {**BASE, **kw})
    return code, body


def start():
    code, body = api("ik", "POST", f"{E}/periods/start-now")
    if code == 200:
        periods.append(body["id"])
    return code, body


def unit_of(who, stage=1):
    code, me = api(who, "GET", f"{E}/me")
    units = [u for u in (me or {}).get("units") or [] if u["stage"] == stage]
    return me, (units[0] if units else None)


def skip(pid):
    return api("ik", "POST", f"{E}/periods/{pid}/skip-phase")[0]


try:
    # Önceki koşudan kalan açık dönem olmasın
    for p in (api("ik", "GET", f"{E}/periods")[1] or []):
        if p["period"]["status"] not in ("Closed", "Cancelled"):
            api("ik", "POST", f"{E}/periods/{p['period']['id']}/cancel")

    # EM1
    code, _ = settings(startDay=31)
    check("EM1: geçersiz başlangıç günü reddedilir", code == 400, code)
    code, _ = settings(weightManager=0)
    check("EM1: sıfır katsayı reddedilir", code == 400, code)
    code, _ = api("ayse", "PUT", f"{E}/settings", BASE)
    check("EM1: çalışan ayarı değiştiremez", code == 403, code)
    code, _ = settings()
    check("EM1: İK ayarı kaydeder", code == 200, code)

    # EM2
    code, body = start()
    check("EM2: şimdi başlat", code == 200, (code, body))
    pid = body["id"]
    code, _ = api("ik", "POST", f"{E}/periods/start-now")
    check("EM2: sürmekte olan varken ikinci dönem açılmaz", code == 409, code)
    me, u = unit_of("ayse")
    check("EM2: dönem oylamada", me and me["period"]["status"] == "Voting", me and me.get("period"))
    check("EM2: Ayşe'nin birimi Mühendislik, eylem oy", u and u["name"] == "Mühendislik" and u["action"] == "vote", u)
    cands = [c["id"] for c in (u or {}).get("candidates", [])]
    check("EM2: aday listesinde kendisi yok, Mehmet var", MEHMET in cands and AYSE not in cands, cands)
    check("EM2: Mehmet'in katsayısı 2 (departman başı)", unit_of("mehmet")[0].get("myWeight") == 2, unit_of("mehmet")[0].get("myWeight"))
    uid = u["id"]

    # EM3
    code, _ = api("ayse", "POST", f"{E}/vote", {"unitId": uid, "employeeId": AYSE})
    check("EM3: kendine oy verilemez", code == 400, code)
    code, _ = api("ayse", "POST", f"{E}/vote", {"unitId": uid, "employeeId": "00000000-0000-0000-0000-000000000001"})
    check("EM3: aday olmayana oy verilemez", code == 400, code)
    code, _ = api("ik", "POST", f"{E}/vote", {"unitId": uid, "employeeId": MEHMET})
    check("EM3: birim üyesi olmayan oy veremez", code == 400, code)
    code, _ = api("ayse", "POST", f"{E}/vote", {"unitId": uid, "employeeId": MEHMET, "comment": "x" * 281})
    check("EM3: 280 karakteri aşan gerekçe reddedilir", code == 400, code)
    code, _ = api("ayse", "POST", f"{E}/vote", {"unitId": uid, "employeeId": MEHMET, "comment": "TEST-EOM ilk"})
    check("EM3: Ayşe oy verir", code == 200, code)
    code, _ = api("ayse", "POST", f"{E}/vote", {"unitId": uid, "employeeId": MEHMET, "comment": "TEST-EOM ekip ruhu"})
    _, u = unit_of("ayse")
    check("EM3: oy değiştirilebilir, seçim görünür", code == 200 and u["myChoice"] == MEHMET and u["myComment"] == "TEST-EOM ekip ruhu", u)
    code, _ = api("mehmet", "POST", f"{E}/vote", {"unitId": uid, "employeeId": AYSE, "comment": "TEST-EOM liderlik"})
    check("EM3: Mehmet oy verir", code == 200, code)

    # EM4
    check("EM4: gizli kipte oy satırında kimlik yok",
          psql(f"SELECT count(*) FROM engagement_eom_votes WHERE \"UnitId\" = '{uid}' AND \"VoterEmployeeId\" IS NOT NULL") == "0")
    check("EM4: oy satırında açık kimlik yok (anahtar özet)",
          psql(f"SELECT count(*) FROM engagement_eom_votes WHERE \"UnitId\" = '{uid}' AND \"VoterKey\" IN ('{AYSE.replace('-', '')}','{MEHMET.replace('-', '')}')") == "0")
    code, _ = api("ayse", "GET", f"{E}/periods/{pid}/results")
    check("EM4: açıklanmadan çalışan sonuç göremez", code == 403, code)
    code, r = api("ik", "GET", f"{E}/periods/{pid}/results")
    part = (r or {}).get("units", [{}])[0].get("participation") if code == 200 else None
    check("EM4: İK katılımı görür (2/2)", part and part["voted"] == 2 and part["members"] == 2, part)

    # EM9 (açılış bildirimi)
    n = psql(f"SELECT count(DISTINCT \"Channel\") FROM notification_messages WHERE \"TenantSlug\" = 'demo' AND \"TemplateCode\" = 'eom.vote' AND \"RecipientEmployeeId\" = '{AYSE}' AND \"CreatedAt\" > now() - interval '10 minutes'")
    check("EM9: oylama açılınca uygulama içi + e-posta kaydı", n == "2", n)

    # EM5
    check("EM5: İK aşamayı bitirir", skip(pid) == 200)
    check("EM4: kapanınca dönem tuzu silinir", psql(f"SELECT \"Salt\" IS NULL FROM engagement_eom_periods WHERE \"Id\" = '{pid}'") == "t")
    code, w = api("ayse", "GET", f"{E}/winners")
    mine = [x for x in (w or {}).get("units", []) if x["isMine"]]
    check("EM5: katsayılı sayımda Ayşe kazanır (Mehmet'in oyu 2)", mine and mine[0]["winner"]["id"] == AYSE, w)
    check("EM5: kart dönemi gösterir", w and w["period"]["id"] == pid, w and w.get("period"))

    # EM6
    _, r = api("ayse", "GET", f"{E}/periods/{pid}/results")
    ur = r["units"][0]
    check("EM6: çalışan kazananı görür", ur["winner"]["id"] == AYSE, ur.get("winner"))
    check("EM6: WinnerOnly'de çalışan sıralama ve oy görmez", ur["ranking"] is None and ur["votes"] is None, ur)
    check("EM6: kazanan kendisine yazılan gerekçeyi görür", any(c["text"] == "TEST-EOM liderlik" for c in (ur["comments"] or [])), ur["comments"])
    _, r = api("ik", "GET", f"{E}/periods/{pid}/results")
    ur = r["units"][0]
    check("EM6: İK sıralamayı görür (2 ve 1 puan)", ur["ranking"] and [x["score"] for x in ur["ranking"]] == [2, 1], ur["ranking"])
    check("EM6: WinnerOnly'de İK de kim kime oy verdi görmez", ur["votes"] is None, ur["votes"])
    _, h = api("mehmet", "GET", f"{E}/history")
    check("EM5: geçmişte görünür", any(x["period"]["id"] == pid for x in (h or [])))

    # EM7
    settings(visibility="RevealAfter", revealTo="Department")
    code, body = start()
    pid2 = body["id"]
    _, u = unit_of("ayse")
    api("ayse", "POST", f"{E}/vote", {"unitId": u["id"], "employeeId": MEHMET})
    api("mehmet", "POST", f"{E}/vote", {"unitId": u["id"], "employeeId": AYSE})
    _, r = api("ik", "GET", f"{E}/periods/{pid2}/results")
    check("EM7: oylama sürerken İK de oyları görmez", r["units"][0]["votes"] is None, r["units"][0]["votes"])
    skip(pid2)
    _, r = api("ayse", "GET", f"{E}/periods/{pid2}/results")
    votes = r["units"][0]["votes"] or []
    check("EM7: açıklanınca birim üyesi kim kime oy verdi görür",
          {(v["voter"]["id"], v["nominee"]["id"]) for v in votes} == {(AYSE, MEHMET), (MEHMET, AYSE)}, votes)

    # EM8
    settings(weightManager=1)
    code, body = start()
    pid3 = body["id"]
    _, u = unit_of("ayse")
    api("ayse", "POST", f"{E}/vote", {"unitId": u["id"], "employeeId": MEHMET})
    api("mehmet", "POST", f"{E}/vote", {"unitId": u["id"], "employeeId": AYSE})
    skip(pid3)
    ps = {p["period"]["id"]: p for p in api("ik", "GET", f"{E}/periods")[1]}
    check("EM8: eşit katsayılı eşit oyda dönem beraberlikte bekler", ps[pid3]["period"]["status"] == "Tie" and ps[pid3]["tieCount"] == 1, ps[pid3])
    code, _ = api("ik", "POST", f"{E}/units/{u['id']}/resolve", {"employeeId": "00000000-0000-0000-0000-000000000001"})
    check("EM8: berabere kalmayan kişi seçilemez", code == 400, code)
    code, _ = api("ayse", "POST", f"{E}/units/{u['id']}/resolve", {"employeeId": MEHMET})
    check("EM8: çalışan beraberliği çözemez", code == 403, code)
    code, _ = api("ik", "POST", f"{E}/units/{u['id']}/resolve", {"employeeId": MEHMET})
    _, w = api("ayse", "GET", f"{E}/winners")
    check("EM8: İK çözer, dönem kapanır, kazanan Mehmet",
          code == 200 and w["period"]["id"] == pid3 and [x for x in w["units"] if x["isMine"]][0]["winner"]["id"] == MEHMET, w)

    # EM11: aday gösterme + oylama (finalist sayısı 2: Mühendislik'te iki kişi de finale kalır)
    settings(process="Nominate", finalistCount=2)
    code, body = start()
    pid4 = body["id"]
    me, u = unit_of("ayse")
    check("EM11: aday gösterme aşaması açılır", me["period"]["status"] == "Nominating" and u["action"] == "nominate", (me["period"], u))
    code, _ = api("ayse", "POST", f"{E}/vote", {"unitId": u["id"], "employeeId": MEHMET})
    check("EM11: aday gösterme sırasında oy verilemez", code == 400, code)
    code, _ = api("ayse", "POST", f"{E}/nominate", {"unitId": u["id"], "employeeId": MEHMET})
    _, u = unit_of("ayse")
    check("EM11: aday gösterilir ve seçim görünür", code == 200 and u["myChoice"] == MEHMET, (code, u))
    skip(pid4)
    me, u = unit_of("ayse")
    check("EM11: aday gösterme bitince oylama açılır", me["period"]["status"] == "Voting" and u["action"] == "vote"
          and [c["id"] for c in u["candidates"]] == [MEHMET], (me["period"], u))
    api("ik", "POST", f"{E}/periods/{pid4}/cancel")

    # EM12: iki aşamalı — Mühendislik'in altına geçici boş alt departman: 1. aşamada doğrudan bağlılar
    # birimi, kazanan tek finalist olarak 2. aşamayı oylamasız kazanır.
    code, depts = api("ik", "GET", "/api/organization/departments")
    eng = next(d for d in depts if d["name"] == "Mühendislik")
    code, sub = api("ik", "POST", "/api/organization/departments", {"name": "TEST-EOM Alt", "companyId": eng["companyId"], "parentDepartmentId": eng["id"]})
    try:
        check("EM12: geçici alt departman", code in (200, 201), (code, sub))
        settings(scope="TwoStage", weightManager=2)
        code, body = start()
        pid5 = body["id"]
        since = psql("SELECT now()")
        me, u = unit_of("ayse")
        _, final = unit_of("ayse", stage=2)
        check("EM12: 2. aşama birimi bekliyor", final and final["status"] == "Pending" and final["isFinal"] and final["action"] is None, final)
        check("EM12: 1. aşama birimi doğrudan bağlılar", u and u["stage"] == 1 and u["direct"] and u["action"] == "vote", u)
        api("ayse", "POST", f"{E}/vote", {"unitId": u["id"], "employeeId": MEHMET})
        skip(pid5)
        _, w = api("ayse", "GET", f"{E}/winners")
        fin = [x for x in (w or {}).get("units", []) if x["isFinal"] and x["isMine"]]
        check("EM12: 2. aşama tek finalistle kapanır, ana departman kazananı Mehmet",
              w and w["period"]["id"] == pid5 and fin and fin[0]["stage"] == 2 and fin[0]["winner"]["id"] == MEHMET, w)
        sub1 = [x for x in w["units"] if x["stage"] == 1 and x["isMine"]]
        check("EM12: alt birim kazananı da kartta", sub1 and sub1[0]["winner"]["id"] == MEHMET, w["units"])
        n = psql(f"SELECT count(*) FROM notification_messages WHERE \"TenantSlug\" = 'demo' AND \"TemplateCode\" = 'eom.winner' AND \"RecipientEmployeeId\" = '{MEHMET}' AND \"CreatedAt\" >= '{since}'")
        check("EM12: kazanana tebrik bildirimi", n != "0", n)
    finally:
        if isinstance(sub, dict) and sub.get("id"):
            api("ik", "DELETE", f"/api/organization/departments/{sub['id']}")

    # EM13: kazananlar sıralaması (bu koşunun dönemleri: Ayşe 2, Mehmet 2 + 1 alt birim; Mühendislik'te Mehmet 3)
    code, lb = api("ayse", "GET", f"{E}/leaderboard")
    co = {r["person"]["id"]: r for r in (lb or {}).get("company", [])}
    check("EM13: şirket sıralaması (Mehmet önde, alt birim zaferi ayrı sütun)",
          co.get(MEHMET, {}).get("wins", 0) >= 2 and co[MEHMET]["subWins"] >= 1 and co.get(AYSE, {}).get("wins", 0) >= 2
          and co[MEHMET]["rank"] < co[AYSE]["rank"], lb and lb.get("company"))
    dep = next((d for d in lb.get("departments", []) if d["name"] == "Mühendislik"), None)
    dr = {r["person"]["id"]: r for r in (dep or {}).get("rows", [])}
    check("EM13: departman sıralaması ve kendi departmanı işaretli",
          dep and dep["isMine"] and dr.get(MEHMET, {}).get("wins", 0) >= 3 and dr[MEHMET]["rank"] == 1, dep)

    # EM10
    settings(enabled=False)
    _, me = api("ayse", "GET", f"{E}/me")
    _, w = api("ayse", "GET", f"{E}/winners")
    check("EM10: modül kapalıyken ekran ve kart boş", me.get("enabled") is False and w.get("enabled") is False, (me, w))
finally:
    if periods:
        ids = ",".join(f"'{p}'" for p in periods)
        psql(f"DELETE FROM engagement_eom_periods WHERE \"Id\" IN ({ids})")
    psql("DELETE FROM notification_messages WHERE \"TenantSlug\" = 'demo' AND \"TemplateCode\" LIKE 'eom.%'")
    if orig:
        o = json.loads(orig)
        psql("DELETE FROM engagement_eom_settings WHERE \"TenantSlug\" = 'demo'")
        cols = ",".join(f'"{k}"' for k in o)
        vals = ",".join("NULL" if v is None else (str(v).lower() if isinstance(v, bool) else
                        (str(v) if isinstance(v, (int, float)) else "'" + str(v).replace("'", "''") + "'")) for v in o.values())
        psql(f"INSERT INTO engagement_eom_settings ({cols}) VALUES ({vals})")
    else:
        psql("DELETE FROM engagement_eom_settings WHERE \"TenantSlug\" = 'demo'")

print("\nSONUÇ:", "BAŞARILI" if not FAIL else f"{len(FAIL)} HATA: {FAIL}")
sys.exit(1 if FAIL else 0)

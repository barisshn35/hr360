#!/usr/bin/env python3
"""İşyeri uyumu (Dalga 5c, governance-service): duyurular + okudum (Y14), sürümlü doküman kütüphanesi ve
tam metin arama (G19), anonim etik hattı (Y15), İSG (Y6), disiplin süreci (Y7).

Kapsam: yetkiler (ayse/mehmet/admin), anonimlik (IP/kullanıcı saklanmaz — DB satırı), takip kodu özeti,
FTS yetki süzgeci, sürüm bazlı yeniden onay, sağlık notunun admin'e kapalı olması, SGK 3 iş günü,
savunma süresi ve görünürlük, saklama (imha) kategorileri.
"""
import datetime as dt
import hashlib
import os
import random
import subprocess
import sys
import time
import urllib.parse

sys.path.insert(0, os.path.dirname(__file__))
from common import form_token, AYSE, FAIL, api, check, http  # noqa: E402

G = "/api/governance"
ROOT = os.path.join(os.path.dirname(__file__), "..", "..")
MEHMET = "3ab24e3e-cb06-40ab-934c-9ff7eab91fb6"
ZEYNEP = "7e5bd452-c36f-4555-9b23-06df398e73b6"
ENG = "9d282a19-fe76-40c7-af56-757075718286"  # Mühendislik (ayse + başı mehmet)
OTHER_DEPT = "00000000-0000-4000-8000-00000000d0d0"
TOKEN = "tstkelime%05d" % random.randint(0, 99999)
FAIL.clear()
_now = dt.datetime.now(dt.timezone.utc)
START = _now.strftime("%Y-%m-%d %H:%M:%S+00")
START_ISO = _now.strftime("%Y-%m-%dT%H:%M:%SZ")


def psql(sql):
    out = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", "hr360_operational", "-Atc", sql],
                         capture_output=True, text=True, cwd=ROOT)
    return (out.stdout + out.stderr).strip()


def cleanup():
    psql("""DELETE FROM governance_acknowledgements WHERE "SubjectId" IN (SELECT "Id" FROM governance_announcements WHERE "Title" LIKE 'TEST%')
                OR "SubjectId" IN (SELECT "Id" FROM governance_library_documents WHERE "Title" LIKE 'TEST%');
            DELETE FROM notification_messages WHERE "TenantSlug" = 'demo' AND ("Subject" LIKE 'TEST%' OR "Subject" LIKE '%: TEST%');
            DELETE FROM governance_announcements WHERE "Title" LIKE 'TEST%';
            DELETE FROM governance_library_documents WHERE "Title" LIKE 'TEST%';
            DELETE FROM governance_ethics_reports WHERE "Description" LIKE 'TEST-ETIK%';
            DELETE FROM governance_osh_incidents WHERE "Description" LIKE 'TEST%';
            DELETE FROM governance_osh_exams WHERE "TenantSlug" = 'demo' AND "ExamDate" IN ('2025-02-03','2025-02-04');
            DELETE FROM governance_osh_trainings WHERE "Topic" LIKE 'TEST%';
            DELETE FROM governance_disciplinary_cases WHERE "Description" LIKE 'TEST%';""")


def holidays():
    rows = psql("""SELECT "Date" FROM leave_public_holidays WHERE "TenantSlug" = 'demo'""").splitlines()
    return {dt.date.fromisoformat(r) for r in rows if r[:4].isdigit()}


def add_bd(d, n, hol):
    while n > 0:
        d += dt.timedelta(days=1)
        if d.weekday() < 5 and d not in hol:
            n -= 1
    return d


cleanup()
HOL = holidays()
TODAY = (dt.datetime.now(dt.timezone.utc) + dt.timedelta(hours=3)).date()

# ====================================================================== Y14 duyurular
code, a1 = api("admin", "POST", f"{G}/announcements", {"title": "TEST duyuru herkese", "body": "**Önemli**: yeni çalışma saatleri.", "audience": "All", "requiresAck": True})
check("İK herkese duyuru yayımlar, kitleye bildirim gider", code == 200 and a1["notified"] >= 2, (code, a1))
n = psql("""SELECT count(*) FROM notification_messages WHERE "Subject" = 'TEST duyuru herkese' AND "TemplateCode" = 'announcement.published'""")
check("Bildirimde yalnızca başlık (konu = başlık)", n.isdigit() and int(n) >= 2, n)
code, a2 = api("admin", "POST", f"{G}/announcements", {"title": "TEST duyuru başka departman", "body": "x", "audience": "Departments", "departmentIds": [OTHER_DEPT]})
code3, a3 = api("admin", "POST", f"{G}/announcements", {"title": "TEST duyuru mühendislik", "body": "y", "audience": "Departments", "departmentIds": [ENG]})
future = (dt.datetime.now(dt.timezone.utc) + dt.timedelta(days=2)).isoformat()
code4, a4 = api("admin", "POST", f"{G}/announcements", {"title": "TEST duyuru ileri tarih", "body": "z", "publishAt": future})
check("Departman ve ileri tarihli duyurular oluşur", code == 200 and code3 == 200 and code4 == 200 and a4["notified"] == 0, (code, code3, code4, a4))
code, _ = api("admin", "POST", f"{G}/announcements", {"title": "TEST eksik", "body": "y", "audience": "Departments", "departmentIds": []})
check("Departman kitlesinde departman zorunlu", code == 400, code)
code, _ = api("ayse", "POST", f"{G}/announcements", {"title": "TEST çalışan", "body": "x"})
check("Çalışan duyuru yayımlayamaz", code == 403, code)
code, _ = api("ayse", "GET", f"{G}/announcements/manage")
check("Çalışan yönetim listesini göremez", code == 403, code)

code, mine = api("ayse", "GET", f"{G}/announcements")
ids = {x["id"] for x in mine} if code == 200 else set()
check("Ayşe kendine yönelik duyuruları görür (herkes + Mühendislik)", a1["id"] in ids and a3["id"] in ids, (code, mine))
check("Ayşe başka departmanın ve henüz yayımlanmamış duyuruyu görmez", a2["id"] not in ids and a4["id"] not in ids, ids)
code, _ = api("ayse", "POST", f"{G}/announcements/{a2['id']}/read")
check("Kitlede olmadığı duyuruya okudum diyemez", code == 404, code)
code, _ = api("ayse", "POST", f"{G}/announcements/{a1['id']}/read")
code2, _ = api("ayse", "POST", f"{G}/announcements/{a1['id']}/read")
cnt = psql(f"""SELECT count(*) FROM governance_acknowledgements WHERE "SubjectId" = '{a1['id']}' AND "SubjectType" = 'Announcement'""")
check("Okudum kaydı tek satır (tekrar idempotent)", code == 200 and code2 == 200 and cnt == "1", (code, code2, cnt))
cons = psql(f"""SELECT count(*) FROM governance_consents WHERE "RecordedAt" >= '{START}' AND "ConsentType" ILIKE '%announce%'""")
check("Okudum kaydı rıza tablosuna yazılmaz", cons == "0", cons)
code, mine = api("ayse", "GET", f"{G}/announcements")
check("Okundu bilgisi listede", code == 200 and any(x["id"] == a1["id"] and x["read"] for x in mine), mine)
code, st = api("admin", "GET", f"{G}/announcements/{a1['id']}/stats")
not_read = {x["employeeId"] for x in st.get("notRead", [])} if code == 200 else set()
check("İK okuma istatistiği: okuyan 1, okumayanlarda Mehmet var Ayşe yok", code == 200 and st["read"] == 1 and st["total"] >= 2 and MEHMET in not_read and AYSE not in not_read, st)
code, st3 = api("admin", "GET", f"{G}/announcements/{a3['id']}/stats")
check("Onay gerektirmeyen duyuruda okumayanlar listelenmez", code == 200 and st3["notRead"] == [] and st3["total"] == 2, st3)

# ====================================================================== G19 kütüphane + FTS
def mkdoc(title, audience, depts=None, ack=False, body=""):
    c, d = api("admin", "POST", f"{G}/library", {"title": title, "category": "Policy", "audience": audience, "departmentIds": depts or [],
                                                 "requiresAck": ack, "body": body, "changeNote": "ilk"})
    return d["id"] if c == 200 else None


D_ALL = mkdoc("TEST Çalışma politikası", "All", ack=True, body=f"Bu politika ÇALIŞMA saatlerini ve {TOKEN} kuralını anlatır.")
D_MGR = mkdoc("TEST Yönetici rehberi", "Managers", body=f"Yöneticiler için {TOKEN} rehberi.")
D_HR = mkdoc("TEST İK prosedürü", "Hr", body=f"Yalnız İK: {TOKEN} ücret bantları.")
D_OTHER = mkdoc("TEST Başka departman", "Departments", [OTHER_DEPT], body=f"Satış ekibi {TOKEN} hedefleri.")
D_ENG = mkdoc("TEST Mühendislik el kitabı", "Departments", [ENG], body=f"Kod inceleme {TOKEN} süreci.")
check("İK beş farklı kitleye belge yükler", all([D_ALL, D_MGR, D_HR, D_OTHER, D_ENG]))
code, _ = api("ayse", "POST", f"{G}/library", {"title": "TEST yetkisiz", "body": "x"})
check("Çalışan belge yükleyemez", code == 403, code)


def found(who, q):
    c, rows = api(who, "GET", f"{G}/library/search?q={urllib.parse.quote(q)}")
    return c, {r["id"] for r in rows} if c == 200 else set(), rows


c, s_ayse, rows = found("ayse", TOKEN)
check("FTS: Ayşe yalnızca herkes + kendi departmanı belgelerini bulur", c == 200 and s_ayse == {D_ALL, D_ENG}, (c, rows))
c, s_meh, _ = found("mehmet", TOKEN)
check("FTS: Mehmet (yönetici) yönetici belgesini de bulur, İK/başka departmanı bulmaz", s_meh == {D_ALL, D_MGR, D_ENG}, s_meh)
c, s_adm, _ = found("admin", TOKEN)
check("FTS: İK hepsini bulur", {D_ALL, D_MGR, D_HR, D_OTHER, D_ENG} <= s_adm, s_adm)
c, s_tr, rows = found("ayse", f"{TOKEN} çalışma")
check("FTS: Türkçe ı/İ katlama (ÇALIŞMA ↔ çalışma) ve ⟦⟧ vurgulu parçacık",
      c == 200 and s_tr == {D_ALL} and "⟦" in rows[0]["snippet"], rows)
c, _ = api("ayse", "GET", f"{G}/library/{D_HR}")
c2, _ = api("ayse", "GET", f"{G}/library/{D_MGR}")
c3, _ = api("ayse", "GET", f"{G}/library/{D_OTHER}")
check("Ayşe yetkisiz belgeleri doğrudan da açamaz (404)", c == 404 and c2 == 404 and c3 == 404, (c, c2, c3))
code, lst = api("ayse", "GET", f"{G}/library")
lids = {x["id"] for x in lst} if code == 200 else set()
check("Liste de yetkiye göre süzülür", D_ALL in lids and D_ENG in lids and not lids & {D_HR, D_MGR, D_OTHER}, lids)
check("Onay gerektiren belge 'onay bekliyor'", any(x["id"] == D_ALL and x["needsAck"] for x in lst), lst)

code, r = api("ayse", "POST", f"{G}/library/{D_ALL}/ack", {"version": 1})
code2, lst = api("ayse", "GET", f"{G}/library")
check("Sürüm 1 onaylandı", code == 200 and r["acknowledgedVersion"] == 1 and any(x["id"] == D_ALL and not x["needsAck"] for x in lst), (code, r))
code, v2 = api("admin", "POST", f"{G}/library/{D_ALL}/versions", {"body": f"Güncellenmiş {TOKEN} kuralı: esnek mesai.", "changeNote": "esnek mesai"})
check("İK yeni sürüm yayımlar", code == 200 and v2["version"] == 2, (code, v2))
code, lst = api("ayse", "GET", f"{G}/library")
check("Yeni sürüm yeniden onay ister", any(x["id"] == D_ALL and x["needsAck"] and x["acknowledgedVersion"] == 1 for x in lst), lst)
code, _ = api("ayse", "POST", f"{G}/library/{D_ALL}/ack", {"version": 1})
check("Eski sürümü onaylama girişimi 409", code == 409, code)
code, _ = api("ayse", "POST", f"{G}/library/{D_ALL}/ack", {"version": 2})
rows = psql(f"""SELECT string_agg("Version"::text, ',' ORDER BY "Version") FROM governance_acknowledgements WHERE "SubjectId" = '{D_ALL}' AND "EmployeeId" = '{AYSE}'""")
check("Sürüm bazlı iki ayrı kabul kaydı (1,2)", code == 200 and rows == "1,2", (code, rows))
code, st = api("admin", "GET", f"{G}/library/{D_ALL}/stats")
check("İK kabul istatistiği güncel sürüm için", code == 200 and st["version"] == 2 and st["stats"]["read"] == 1 and MEHMET in {x["employeeId"] for x in st["stats"]["notRead"]}, st)
code, d = api("ayse", "GET", f"{G}/library/{D_ALL}")
check("Çalışan güncel metni ve sürüm geçmişini görür", code == 200 and d["current"]["versionNo"] == 2 and len(d["history"]) == 2 and "body" not in d["history"][0], d)
code, _ = api("ayse", "GET", f"{G}/library/{D_ALL}/versions/1")
check("Eski sürüm metni yalnızca İK'ya", code == 403, code)
code, _ = api("admin", "PUT", f"{G}/library/{D_ENG}", {"archived": True})
c, s_after, _ = found("ayse", TOKEN)
check("Arşivlenen belge çalışan aramasından çıkar", code == 200 and D_ENG not in s_after, s_after)

# ====================================================================== Y15 etik hattı
code, committee = api("admin", "GET", f"{G}/ethics/committee")
had_mehmet = code == 200 and any(m["employeeId"] == MEHMET for m in committee)
code, _ = api("admin", "GET", f"{G}/ethics/reports")
check("Şirket yöneticisi/İK kurul üyesi değilse bildirimleri göremez", code == 403, code)
code, _ = api("ayse", "POST", f"{G}/ethics/committee", {"employeeId": AYSE})
check("Çalışan kurulu düzenleyemez", code == 403, code)
if not had_mehmet:
    code, _ = api("admin", "POST", f"{G}/ethics/committee", {"employeeId": MEHMET})
    check("Şirket yöneticisi Mehmet'i kurula ekler", code == 200, code)
code, info = http("GET", f"{G}/ethics/public/demo")
check("Herkese açık sayfa bilgisi (oturumsuz)", code == 200 and info["enabled"] and info["company"], (code, info))
code, _ = http("GET", f"{G}/ethics/public/yok-boyle-sirket")
check("Bilinmeyen şirket 404", code == 404, code)

UA = "TEST-UA-izlenebilir/9.9"
SPOOF_IP = "203.0.113.77"
CONTACT = "ihbarci-test@example.invalid"
# Güvenlik dalgası 2B: herkese açık form imzalı zaman jetonu ister; görünmez tuzak alanı dolu gelirse kayıt yapılmaz.
code, _ = http("POST", f"{G}/ethics/public/demo/reports", {"category": "Fraud", "description": "TEST-ETIK jetonsuz bildirim denemesi yapılıyor."})
check("Bot koruması: jetonsuz etik bildirimi reddedilir", code == 400, code)
code, bot = http("POST", f"{G}/ethics/public/demo/reports", {"category": "Fraud", "description": "TEST-ETIK bot tuzağı doldurulmuş bildirim.",
                                                              "website": "http://spam.example", "formToken": form_token(f"{G}/ethics/public/demo/form-token")})
bot_hash = hashlib.sha256(bot["followUpCode"].replace("-", "").encode()).hexdigest() if code == 200 and bot.get("followUpCode") else "x"
check("Bot tuzağı: başarı görünür ama kayıt yapılmaz",
      code == 200 and psql(f"""SELECT count(*) FROM governance_ethics_reports WHERE "CodeHash" = '{bot_hash}'""") == "0", (code, bot))
code, rep = http("POST", f"{G}/ethics/public/demo/reports",
                 {"category": "Fraud", "description": "TEST-ETIK fatura usulsüzlüğü şüphesi, ayrıntılar ekte değil.", "contact": CONTACT,
                  "formToken": form_token(f"{G}/ethics/public/demo/form-token")},
                 {"User-Agent": UA, "X-Real-IP": SPOOF_IP, "X-Forwarded-For": SPOOF_IP})
fcode = rep.get("followUpCode") if code == 200 else None
check("Oturumsuz anonim bildirim, takip kodu bir kez döner", code == 200 and fcode and len(fcode) == 19, (code, rep))
h = hashlib.sha256(fcode.replace("-", "").encode()).hexdigest() if fcode else "x"
row = psql(f"""SELECT "Id" || '|' || row_to_json(r)::text FROM governance_ethics_reports r WHERE "CodeHash" = '{h}'""")
rid = row.split("|", 1)[0] if "|" in row else None
check("DB'de takip kodunun yalnızca SHA-256 özeti var", rid is not None and fcode.replace("-", "") not in row and fcode not in row, row[:200])
check("DB satırında IP, tarayıcı, iletişim düz metni yok", rid is not None and SPOOF_IP not in row and UA not in row and CONTACT not in row, row[:300])
cols = set(psql("""SELECT string_agg(column_name, ',') FROM information_schema.columns
                   WHERE table_name IN ('governance_ethics_reports','governance_ethics_messages')""").lower().split(","))
check("Etik tablolarında IP/kullanıcı/tarayıcı sütunu yok", not cols & {"ip", "ipaddress", "userid", "useragent", "employeeid", "createdby", "username"}, cols)
check("Alınma zamanı gün hassasiyetinde (saat yok)", '"ReceivedOn":"' in row and '"UpdatedAt":"' in row and "T00:00:00" in row.split('"UpdatedAt":"')[1][:30], row[-200:])
aud = psql(f"""SELECT count(*) FROM audit_log WHERE "EntityId" = '{rid}' OR ("Changes"::text LIKE '%{SPOOF_IP}%')""")
check("İhbar için denetim kaydına (IP'li) satır yazılmaz", aud == "0", aud)

code, stt = http("GET", f"{G}/ethics/public/demo/reports/status", headers={"X-Follow-Up-Code": fcode})
check("İhbarcı kodla durumu görür", code == 200 and stt["status"] == "Received" and stt["messages"] == [], (code, stt))
code, stt2 = http("GET", f"{G}/ethics/public/demo/reports/status", headers={"X-Follow-Up-Code": " " + fcode.replace("-", "").lower()})
check("Kod tire/küçük harf fark etmeden çalışır", code == 200, code)
code, _ = http("GET", f"{G}/ethics/public/demo/reports/status", headers={"X-Follow-Up-Code": "AAAA-BBBB-CCCC-DDDD"})
check("Yanlış kod 404", code == 404, code)
code, _ = http("POST", f"{G}/ethics/public/demo/reports/messages", {"code": fcode, "body": "TEST ek bilgi: geçen ay da oldu."})
check("İhbarcı kodla ek mesaj gönderir", code == 200, code)
code, rl = api("mehmet", "GET", f"{G}/ethics/reports")
check("Kurul üyesi (Mehmet) bildirimi görür", code == 200 and any(x["id"] == rid and x["awaitingReply"] for x in rl), (code, rl))
code, _ = api("ayse", "GET", f"{G}/ethics/reports")
code2, _ = api("admin", "GET", f"{G}/ethics/reports/{rid}")
check("Üye olmayanlar (çalışan, İK/şirket yöneticisi) bildirimi açamaz", code == 403 and code2 == 403, (code, code2))
code, det = api("mehmet", "GET", f"{G}/ethics/reports/{rid}")
check("Kurul ayrıntıyı ve ihbarcı mesajını görür; iletişim gizli", code == 200 and det["hasContact"] and len(det["messages"]) == 1 and "contact" not in det, det)
code, ct = api("mehmet", "GET", f"{G}/ethics/reports/{rid}/contact")
rev = psql(f"""SELECT count(*) FROM audit_log WHERE "EntityType" = 'EthicsReport' AND "EntityId" = '{rid}' AND "Action" = 'Revealed'""")
check("İletişim bilgisi açılır ve erişim kaydına yazılır", code == 200 and ct["contact"] == CONTACT and rev == "1", (code, rev))
code, _ = api("mehmet", "POST", f"{G}/ethics/reports/{rid}/messages", {"body": "TEST Teşekkürler, inceliyoruz."})
code2, stt = http("GET", f"{G}/ethics/public/demo/reports/status", headers={"X-Follow-Up-Code": fcode})
check("Kurul yanıtı ihbarcıya 'Etik Kurulu' imzasıyla görünür; durum incelemede",
      code == 200 and code2 == 200 and stt["status"] == "InReview" and stt["messages"][-1]["author"] == "Etik Kurulu" and not stt["messages"][-1]["fromReporter"], stt)
code, _ = api("mehmet", "POST", f"{G}/ethics/reports/{rid}/status", {"status": "Closed", "outcome": "TEST: inceleme tamamlandı"})
code2, _ = http("POST", f"{G}/ethics/public/demo/reports/messages", {"code": fcode, "body": "TEST bir şey daha"})
check("Kapatılan bildirime ihbarcı mesaj yazamaz", code == 200 and code2 == 409, (code, code2))
limited = False
for _ in range(35):
    c, _ = http("GET", f"{G}/ethics/public/demo/reports/status", headers={"X-Follow-Up-Code": fcode})
    if c == 429:
        limited = True
        break
check("Takip kodu başına istek sınırı (429)", limited, c)
time.sleep(1)
glog = subprocess.run(["docker", "logs", "--since", START_ISO, "hr360-gateway-1"], capture_output=True, text=True)
glog = glog.stdout + glog.stderr
check("Gateway etik yolu için erişim kaydı yazmaz", "/ethics/public/" not in glog and "/api/governance/ethics/reports" in glog,
      [ln for ln in glog.splitlines() if "ethics" in ln][:3])

# ====================================================================== Y6 İSG
code, _ = api("ayse", "GET", f"{G}/osh/incidents")
code2, _ = api("mehmet", "POST", f"{G}/osh/incidents", {"kind": "NearMiss", "occurredOn": str(TODAY), "description": "TEST yetkisiz"})
check("Çalışan/yönetici olay kayıtlarını göremez/oluşturamaz", code == 403 and code2 == 403, (code, code2))
fri = dt.date(2026, 10, 2)
code, inc = api("admin", "POST", f"{G}/osh/incidents", {"kind": "Accident", "occurredOn": str(fri), "location": "Depo", "description": "TEST forklift çarpması",
                                                        "injuredEmployeeId": AYSE, "lostDays": 2, "rootCause": "Ayna kör nokta"})
exp = add_bd(fri, 3, HOL)
check("İş kazası: SGK bildirimi son günü = 3 iş günü sonra", code == 200 and inc["sgkDeadline"] == str(exp) and inc["injuredName"], (code, inc, exp))
old = dt.date(2026, 9, 1)
code, inc2 = api("admin", "POST", f"{G}/osh/incidents", {"kind": "Accident", "occurredOn": str(old), "description": "TEST eski kaza bildirilmemiş"})
check("Bildirilmemiş eski kaza 'gecikmiş'", code == 200 and inc2["sgkOverdue"] and inc2["sgkDeadline"] == str(add_bd(old, 3, HOL)), inc2)
code, inc3 = api("admin", "PUT", f"{G}/osh/incidents/{inc2['id']}", {"kind": "Accident", "occurredOn": str(old), "description": "TEST eski kaza bildirilmemiş",
                                                                     "sgkNotifiedOn": "2026-09-10", "sgkReference": "TEST-123"})
check("Geç yapılan bildirim 'geç' olarak işaretlenir, gecikme kalkar", code == 200 and not inc3["sgkOverdue"] and inc3["sgkLate"], inc3)
hol_case = dt.date(2026, 10, 27)
if hol_case <= TODAY:
    code, inc4 = api("admin", "POST", f"{G}/osh/incidents", {"kind": "Accident", "occurredOn": str(hol_case), "description": "TEST tatil öncesi kaza"})
    check("Resmî tatil SGK süresinden düşülür", code == 200 and inc4["sgkDeadline"] == str(add_bd(hol_case, 3, HOL)), inc4)
code, nm = api("admin", "POST", f"{G}/osh/incidents", {"kind": "NearMiss", "occurredOn": str(TODAY), "description": "TEST ramak kala: kaygan zemin"})
check("Ramak kalada SGK süresi yok", code == 200 and nm["sgkDeadline"] is None, nm)

code, _ = api("admin", "POST", f"{G}/osh/exams", {"employeeId": AYSE, "examDate": "2025-02-03", "result": "Fit", "notes": "TEST tansiyon"})
check("İK sağlık notu giremez (yalnızca işyeri hekimi)", code == 403, code)
code, ex = api("admin", "POST", f"{G}/osh/exams", {"employeeId": AYSE, "examDate": "2025-02-03", "result": "Conditional", "hazardClass": "Hazardous"})
check("İK muayene sonucunu kaydeder; sonraki muayene 3 yıl (tehlikeli)", code == 200 and ex["nextDueDate"] == "2028-02-03", (code, ex))
# İşyeri hekimi rolü test kullanıcılarında yok: hekimin yazdığı şifreli notu DB'de taklit et.
psql(f"""UPDATE governance_osh_exams SET "NotesEnc" = 'AAAAopaqueTESTciphertext' WHERE "Id" = '{ex['id']}'""")
code, _ = api("admin", "GET", f"{G}/osh/exams/{ex['id']}/notes")
code2, _ = api("ayse", "GET", f"{G}/osh/exams/{ex['id']}/notes")
code3, _ = api("admin", "PUT", f"{G}/osh/exams/{ex['id']}/notes", {"notes": "TEST"})
check("Sağlık notu İK/şirket yöneticisine ve çalışana kapalı (okuma/yazma 403)", code == 403 and code2 == 403 and code3 == 403, (code, code2, code3))
code, exl = api("admin", "GET", f"{G}/osh/exams")
mine_ex = [x for x in exl if x["id"] == ex["id"]] if code == 200 else []
check("İK listesinde not ve notun varlığı bile görünmez", mine_ex and mine_ex[0]["hasNotes"] is None and "notes" not in mine_ex[0] and mine_ex[0]["result"] == "Conditional", mine_ex)
code, exa = api("ayse", "GET", f"{G}/osh/exams")
check("Çalışan yalnızca kendi muayene sonucunu görür", code == 200 and exa and all(x["employeeId"] == AYSE for x in exa), exa)
code, exm = api("mehmet", "GET", f"{G}/osh/exams")
check("Yönetici ekibinin sonuç/tarihlerini görür", code == 200 and any(x["id"] == ex["id"] for x in exm), exm)
code, exz = api("admin", "POST", f"{G}/osh/exams", {"employeeId": ZEYNEP, "examDate": "2025-02-04", "nextDueDate": "2026-02-04", "result": "Fit"})
tr_date = TODAY - dt.timedelta(days=340)
code, tr = api("admin", "POST", f"{G}/osh/trainings", {"topic": "TEST Yangın eğitimi", "trainingDate": str(tr_date), "durationHours": 2,
                                                       "validityMonths": 12, "trainer": "İSG uzmanı", "participantIds": [AYSE, MEHMET]})
check("İSG eğitimi kaydı ve geçerlilik sonu", code == 200 and tr["expiresOn"], (code, tr))
code, rem = api("admin", "GET", f"{G}/osh/reminders")
check("Hatırlatma: süresi geçmiş muayene ve dolmak üzere eğitim",
      code == 200 and any(x["employeeId"] == ZEYNEP and x["state"] == "Overdue" for x in rem["exams"])
      and any(x["employeeId"] == AYSE and x["topic"] == "TEST Yangın eğitimi" for x in rem["trainings"]), rem)
code, tra = api("ayse", "GET", f"{G}/osh/trainings")
check("Çalışan kendi eğitimlerini görür, diğer katılımcıları görmez",
      code == 200 and any(t["id"] == tr["id"] and t["participants"] == [] for t in tra), tra)
code, _ = api("ayse", "GET", f"{G}/osh/reminders")
check("Çalışan hatırlatma listesini göremez", code == 403, code)

# ====================================================================== Y7 disiplin
code, c1 = api("admin", "POST", f"{G}/disciplinary", {"employeeId": AYSE, "incidentDate": str(TODAY - dt.timedelta(days=3)), "category": "Attendance",
                                                      "description": "TEST üç gün geç geldi. Eski sabıka kaydı da varmış."})
check("İK vaka açar; adli sicil ifadesi için uyarı döner", code == 200 and c1["warnings"], (code, c1))
code, cz = api("admin", "POST", f"{G}/disciplinary", {"employeeId": ZEYNEP, "incidentDate": str(TODAY), "category": "Conduct", "description": "TEST departmansız çalışan vakası"})
code2, _ = api("mehmet", "POST", f"{G}/disciplinary", {"employeeId": AYSE, "incidentDate": str(TODAY), "category": "Conduct", "description": "TEST yönetici açamaz"})
check("Yönetici vaka açamaz (yalnızca İK)", code == 200 and code2 == 403, (code, code2))
code, ml = api("mehmet", "GET", f"{G}/disciplinary")
mids = {x["id"] for x in ml} if code == 200 else set()
check("Departman başı yalnızca kendi departmanındaki vakaları görür", c1["id"] in mids and cz["id"] not in mids, mids)
code, md = api("mehmet", "GET", f"{G}/disciplinary/{c1['id']}")
code2, _ = api("mehmet", "GET", f"{G}/disciplinary/{cz['id']}")
aud = psql(f"""SELECT count(*) FROM audit_log WHERE "EntityType" = 'DisciplinaryCase' AND "Action" = 'SensitiveViewed' AND "EntityId" = '{AYSE}' AND "OccurredAt" >= '{START}'""")
check("Yönetici ayrıntıyı açar (erişim kaydı), başka departmanınkini açamaz", code == 200 and code2 == 404 and aud.isdigit() and int(aud) >= 1, (code, code2, aud))
code, _ = api("ayse", "GET", f"{G}/disciplinary")
code2, _ = api("ayse", "GET", f"{G}/disciplinary/{c1['id']}")
code3, mine = api("ayse", "GET", f"{G}/disciplinary/mine")
check("Çalışan listeyi/ayrıntıyı göremez; savunma istenmeden vaka görünmez",
      code == 403 and code2 == 404 and code3 == 200 and all(x["id"] != c1["id"] for x in mine), (code, code2, mine))

code, meta = api("admin", "GET", f"{G}/disciplinary/meta")
min_dl = dt.date.fromisoformat(meta["minDefenceDeadline"])
check("Asgari savunma süresi = 2 iş günü", min_dl == add_bd(TODAY, 2, HOL), (min_dl, TODAY))
code, err = api("admin", "POST", f"{G}/disciplinary/{c1['id']}/defence-request", {"deadline": str(min_dl - dt.timedelta(days=1))})
check("2 iş gününden kısa savunma süresi reddedilir", code == 400 and err.get("minDeadline") == str(min_dl), (code, err))
code, dr = api("admin", "POST", f"{G}/disciplinary/{c1['id']}/defence-request", {})
check("Savunma istemi: varsayılan son gün ve otomatik yazı", code == 200 and dr["deadline"] == str(min_dl) and "Sayın Ayşe" in dr["notice"]
      and min_dl.strftime("%d.%m.%Y") in dr["notice"], (code, dr))
n = psql(f"""SELECT count(*) FROM notification_messages WHERE "RecipientEmployeeId" = '{AYSE}' AND "TemplateCode" = 'disciplinary.defence' AND "CreatedAt" >= '{START}'""")
check("Çalışana uygulama içi bildirim (ayrıntısız)", n.isdigit() and int(n) >= 1, n)
code, _ = api("admin", "POST", f"{G}/disciplinary/{c1['id']}/decision", {"decision": "WrittenWarning"})
check("Savunma alınmadan (süre dolmadan) yaptırım kararı verilemez", code == 409, code)
code, _ = api("admin", "PUT", f"{G}/disciplinary/{c1['id']}/minutes", {"minutesText": "TEST tutanak: giriş kayıtları incelendi.", "witnesses": "Tanık Bir, Tanık İki"})
check("İK tutanak ve tanık adlarını yazar", code == 200, code)
code, mine = api("ayse", "GET", f"{G}/disciplinary/mine")
mc = next((x for x in mine if x["id"] == c1["id"]), None) if code == 200 else None
check("Çalışan savunma istemini görür; tutanak/tanık/açıklama görmez",
      mc and mc["canSubmitDefence"] and mc["defenceNotice"] and "minutesText" not in mc and "witnesses" not in mc and "description" not in mc and mc["decision"] is None, mc)
code, _ = api("mehmet", "POST", f"{G}/disciplinary/mine/{c1['id']}/defence", {"text": "TEST başkası adına savunma"})
check("Başkası çalışanın yerine savunma veremez", code == 404, code)
code, sv = api("ayse", "POST", f"{G}/disciplinary/mine/{c1['id']}/defence", {"text": "TEST Trafik nedeniyle geciktim, telafi ettim."})
code2, _ = api("ayse", "POST", f"{G}/disciplinary/mine/{c1['id']}/defence", {"text": "TEST ikinci savunma denemesi"})
check("Çalışan yazılı savunmasını süresinde verir (bir kez)", code == 200 and sv["view"]["status"] == "DefenceReceived" and code2 == 409, (code, sv, code2))
code, _ = api("mehmet", "POST", f"{G}/disciplinary/{c1['id']}/decision", {"decision": "Warning"})
check("Yönetici karar veremez", code == 403, code)
code, _ = api("admin", "POST", f"{G}/disciplinary/{c1['id']}/decision", {"decision": "WrittenWarning", "note": "TEST tekrarı halinde fesih değerlendirilir"})
code2, mine = api("ayse", "GET", f"{G}/disciplinary/mine")
mc = next((x for x in mine if x["id"] == c1["id"]), {}) if code2 == 200 else {}
check("İK kararı verir; çalışan kararı görür", code == 200 and mc.get("decision") == "WrittenWarning" and mc.get("decisionNote"), (code, mc))
code, _ = api("admin", "POST", f"{G}/disciplinary/{c1['id']}/close")
check("Vaka kapatılır", code == 200, code)

# Saklama/imha: kapanıştan 24 aydan eski vaka "DisciplinaryCases" politikasıyla silinir.
code, pol = api("admin", "GET", f"{G}/privacy/retention")
cats = {p["category"]: p for p in pol} if code == 200 else {}
check("Saklama kategorileri: DisciplinaryCases, EthicsReports, Announcements",
      {"DisciplinaryCases", "EthicsReports", "Announcements"} <= set(cats), list(cats))
if "DisciplinaryCases" in cats:
    psql(f"""UPDATE governance_disciplinary_cases SET "ClosedAt" = now() - interval '30 months' WHERE "Id" = '{c1['id']}'""")
    code, run = api("admin", "POST", f"{G}/privacy/retention/{cats['DisciplinaryCases']['id']}/run")
    left = psql(f"""SELECT count(*) FROM governance_disciplinary_cases WHERE "Id" = '{c1['id']}'""")
    check("Süresi dolan kapalı disiplin vakası imha edilir", code == 200 and run["affected"] >= 1 and left == "0", (code, run, left))
if "EthicsReports" in cats and rid:
    psql(f"""UPDATE governance_ethics_reports SET "ClosedAt" = now() - interval '30 months' WHERE "Id" = '{rid}'""")
    code, run = api("admin", "POST", f"{G}/privacy/retention/{cats['EthicsReports']['id']}/run")
    left = psql(f"""SELECT count(*) FROM governance_ethics_messages WHERE "ReportId" = '{rid}'""")
    check("Süresi dolan kapalı etik bildirimi mesajlarıyla imha edilir", code == 200 and run["affected"] >= 1 and left == "0", (code, run, left))

code, inv = api("admin", "GET", f"{G}/privacy/inventory")
if code == 200:
    ids_inv = {a.get("id") for a in (inv if isinstance(inv, list) else inv.get("activities", []))}
    check("KVKK envanterinde yeni işleme faaliyetleri", {"ethics-hotline", "osh-health", "disciplinary", "policy-library", "announcements"} <= ids_inv, ids_inv)

# ====================================================================== temizlik
if not had_mehmet:
    code, committee = api("admin", "GET", f"{G}/ethics/committee")
    for m in committee or []:
        if m["employeeId"] == MEHMET:
            api("admin", "DELETE", f"{G}/ethics/committee/{m['id']}")
psql(f"""DELETE FROM notification_messages WHERE "TenantSlug" = 'demo' AND "CreatedAt" >= '{START}'
         AND "TemplateCode" IN ('disciplinary.defence','disciplinary.decision','ethics.report','library.ack','announcement.published')
         AND ("TemplateCode" <> 'announcement.published' OR "Subject" LIKE 'TEST%')
         AND ("TemplateCode" <> 'library.ack' OR "Subject" LIKE '%TEST%')""")
cleanup()

print(f"\nFAILS: {len(FAIL)}")
for f in FAIL:
    print("  -", f)
sys.exit(1 if FAIL else 0)

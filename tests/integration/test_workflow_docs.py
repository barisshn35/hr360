#!/usr/bin/env python3
"""Belge talebi (Y10), görsel onay akışı (Y22), vekâlet (Y23), onay geliştirmeleri (G10: toplu onay,
e-postadan tek tıkla karar, süre aşımında iletme) ve izin geliştirmeleri (G8: saatlik izin, yasal
hak, devir) uçtan uca testi.

Önkoşul: workflow-service test katmanıyla (iş aralığı 5 sn):
  docker compose -f docker-compose.yml -f deploy/testing/chat-mock.yml up -d workflow-service
"""
import datetime as dt
import json
import os
import random
import subprocess
import sys
import time
import urllib.parse

from common import AYSE, FAIL, api, check, http  # noqa: E402

G = "/api/governance"
W = "/api/workflow/workflows"
T = "/api/timeshift"
ROOT = os.path.join(os.path.dirname(__file__), "..", "..")


def psql(sql):
    out = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", "hr360_operational", "-Atc", sql],
                         capture_output=True, text=True, cwd=ROOT)
    return out.stdout.strip()


_, d = api("admin", "GET", "/api/employee/employees/directory")
ids = {x["fullName"]: x["id"] for x in d}
MEHMET, ZEYNEP = ids["Mehmet Demir"], ids["Zeynep Kaya"]
today = dt.date.today()


def workday(offset):
    x = today + dt.timedelta(days=offset)
    while x.weekday() >= 5:
        x += dt.timedelta(days=1)
    return x


psql("DELETE FROM timeshift_overtime_requests WHERE \"Reason\" LIKE 'test-wf-%'")
psql("UPDATE workflow_delegations SET \"RevokedAt\" = now() WHERE \"RevokedAt\" IS NULL AND \"Reason\" LIKE 'test-%'")
psql("DELETE FROM workflow_definitions WHERE \"Name\" LIKE 'test-%'")

# ====================================================================== Y10 belge talebi
api("admin", "POST", f"{G}/documents/templates/samples")
code, tpls = api("admin", "GET", f"{G}/documents/templates")
work = next((t for t in tpls if t["name"] == "Çalışma belgesi"), None)
pay = next((t for t in tpls if t["name"] == "Maaş yazısı"), None)
api("admin", "PUT", f"{G}/documents/templates/{work['id']}", {"name": work["name"], "category": work["category"], "body": work["body"], "selfService": True, "requiresApproval": False})
api("admin", "PUT", f"{G}/documents/templates/{pay['id']}", {"name": pay["name"], "category": pay["category"], "body": pay["body"], "selfService": True, "requiresApproval": True})
code, req_t = api("ayse", "GET", f"{G}/documents/requests/templates")
check("Çalışan talep edebileceği belgeleri görür", code == 200 and {"Çalışma belgesi", "Maaş yazısı"} <= {t["name"] for t in req_t}, req_t)
code, _ = api("ayse", "GET", f"{G}/documents/templates")
check("Yetki: çalışan şablon yönetimini göremez", code == 403, code)

psql(f"""DELETE FROM governance_document_requests WHERE "EmployeeId" = '{AYSE}' AND "CreatedAt" > now() - interval '1 day'""")  # günlük 10 sınırı
code, r1 = api("ayse", "POST", f"{G}/documents/requests", {"templateId": work["id"], "purpose": "Banka kredi başvurusu"})
check("Onaysız belge hemen düzenlendi (doğrulama kodu var)", code == 200 and r1["status"] == "Issued" and len(r1.get("verificationCode") or "") == 11, r1)
code, doc = api("ayse", "GET", f"{G}/documents/requests/{r1['id']}/document")
check("Belge: ad, kod ve kullanım amacı içerir", code == 200 and "Ayşe Yılmaz" in doc["html"] and r1["verificationCode"] in doc["html"] and "Banka kredi" in doc["html"], code)
enc = psql(f"SELECT \"DocumentEnc\" FROM governance_document_requests WHERE \"Id\"='{r1['id']}'")
check("KVKK: belge veritabanında şifreli", bool(enc) and "Ayşe" not in enc and "ÇALIŞMA" not in enc, enc[:40])
code, v = http("GET", f"{G}/documents/verify/{r1['verificationCode']}")
check("Doğrulama (oturumsuz): geçerli, yalnızca baş harfler", code == 200 and v["valid"] and v["holder"] == "A. Y." and "Ayşe" not in json.dumps(v, ensure_ascii=False), v)
code, v = http("GET", f"{G}/documents/verify/ABCDE-FGHJK")
check("Doğrulama: geçersiz kod", code == 200 and v["valid"] is False, v)

code, r2 = api("ayse", "POST", f"{G}/documents/requests", {"templateId": pay["id"]})
check("Onaylı belge İK onayında bekler", code == 200 and r2["status"] == "Pending" and r2.get("verificationCode") is None, r2)
code, _ = api("ayse", "GET", f"{G}/documents/requests/{r2['id']}/document")
check("Onaylanmadan belge açılamaz", code == 404, code)
code, _ = api("mehmet", "POST", f"{G}/documents/requests/{r2['id']}/decide", {"approve": True})
check("Yetki: yönetici belge talebine karar veremez (İK)", code == 403, code)
t0 = time.time()
code, r2b = api("admin", "POST", f"{G}/documents/requests/{r2['id']}/decide", {"approve": True})
check("İK onayladı → düzenlendi", code == 200 and r2b["status"] == "Issued", r2b)
time.sleep(1)
note = psql(f"SELECT count(*) FROM notification_messages WHERE \"RecipientEmployeeId\"='{AYSE}' AND \"Subject\" LIKE 'Belgeniz hazır%' AND \"CreatedAt\" > now() - interval '1 minute'")
check("Çalışana 'belgeniz hazır' bildirimi", note not in ("", "0"), note)
code, _ = api("mehmet", "GET", f"{G}/documents/requests/{r2['id']}/document")
check("Yetki: yönetici başkasının belgesini açamaz", code == 404, code)
code, _ = api("admin", "GET", f"{G}/documents/requests/{r2['id']}/document")
logged = psql(f"SELECT count(*) FROM audit_log WHERE \"EntityType\"='DocumentRequest' AND \"EntityId\"='{AYSE}' AND \"Action\"='SensitiveViewed' AND \"OccurredAt\" > now() - interval '1 minute'")
check("KVKK: İK'nın belgeyi açması erişim kaydında", code == 200 and logged not in ("", "0"), logged)

# ====================================================================== Y22 görsel onay akışı
defn = {"type": "Overtime", "name": "test-fm-akisi", "isActive": True, "hiddenFields": ["hours"], "steps": [
    {"approver": "DepartmentHead"},
    {"approver": "Employee", "employeeId": ZEYNEP, "conditionField": "Hours", "conditionOp": ">=", "conditionValue": 3, "slaHours": 24}]}
code, val = api("admin", "POST", f"{W}/definitions/validate", defn)
check("Akış doğrulama: belirli kişiye giden adım için KVKK uyarısı", code == 200 and val["valid"] and any("belirli bir kişiye" in w for w in val["warnings"]), val)
bad = dict(defn, steps=[{"approver": "Employee"}])
code, val = api("admin", "POST", f"{W}/definitions/validate", bad)
check("Akış doğrulama: kişisiz 'belirli kişi' adımı hata", code == 200 and not val["valid"], val)
code, _ = api("mehmet", "POST", f"{W}/definitions", defn)
check("Yetki: yönetici akış tanımlayamaz (İK)", code == 403, code)
code, saved = api("admin", "POST", f"{W}/definitions", defn)
check("Akış kaydedildi", code == 200 and saved["definition"]["isActive"], saved)
def_id = saved["definition"]["id"]
code, pv = api("admin", "POST", f"{W}/definitions/preview", {"type": "Overtime", "requesterEmployeeId": AYSE, "hours": 3})
check("Ön izleme: 3 saatte bölüm başı + Zeynep", code == 200 and [a["employeeId"] for a in pv["approvers"]] == [MEHMET, ZEYNEP], pv)
code, pv = api("admin", "POST", f"{W}/definitions/preview", {"type": "Overtime", "requesterEmployeeId": AYSE, "hours": 2})
check("Ön izleme: 2 saatte yalnızca bölüm başı (koşul sağlanmadı)", code == 200 and [a["employeeId"] for a in pv["approvers"]] == [MEHMET], pv)

# Başka testlerin bıraktığı fazla mesai kayıtlarıyla çakışmayan üç iş günü.
taken = set(psql(f"""SELECT "Date" FROM timeshift_overtime_requests WHERE "EmployeeId" = '{AYSE}' AND "Status" IN ('Pending','Approved')""").split())
free, off = [], 20 + random.randint(0, 20)
while len(free) < 4 and off < 59:
    d = workday(off)
    if d.isoformat() not in taken and d not in free:
        free.append(d)
    off += 1
d1, d2, d3, d4 = free
code, o3 = api("ayse", "POST", f"{T}/overtime", {"date": d1.isoformat(), "hours": 3, "reason": "test-wf-3s"})
_, w3 = api("ayse", "GET", f"{W}/{o3['workflowRequestId']}")
check("Gerçek talep: 3 saat → 2 adımlı zincir (tanımdan)", [s["approverEmployeeId"] for s in w3["steps"]] == [MEHMET, ZEYNEP] and w3["steps"][1].get("slaHours") == 24, w3["steps"])
code, o2 = api("ayse", "POST", f"{T}/overtime", {"date": d2.isoformat(), "hours": 2, "reason": "test-wf-2s"})
_, w2 = api("ayse", "GET", f"{W}/{o2['workflowRequestId']}")
check("Gerçek talep: 2 saat → tek adım", [s["approverEmployeeId"] for s in w2["steps"]] == [MEHMET], w2["steps"])
_, wm = api("mehmet", "GET", f"{W}/{o2['workflowRequestId']}")
check("KVKK: onaycı gizlenen alanı (saat) görmez, talep sahibi görür",
      "hours" not in json.loads(wm.get("payload") or "{}") and "hours" in json.loads(w2.get("payload") or "{}"), (wm.get("payload"), w2.get("payload")))

# ====================================================================== G10 e-postadan tek tıkla karar
time.sleep(3)
# Önceki betiklerin olayları Kafka üzerinden gecikmeli bildirim üretebilir; "en yeni" bildirim başka bir talebin
# olabilir. Bu yüzden son 2 dakikadaki adaylardan önizlemesi bu talebe (workflowId) ait olan seçilir.
rows = psql(f"SELECT \"ActionUrl\" || '|' || replace(\"Body\", E'\\n', ' ') FROM notification_messages WHERE \"RecipientEmployeeId\"='{MEHMET}' AND \"TemplateCode\"='workflow.submitted' "
            f"AND \"CreatedAt\" > now() - interval '2 minutes' ORDER BY \"CreatedAt\" DESC LIMIT 10").splitlines()
row = rows[0] if rows else ""
for cand in rows:
    cu = cand.partition("|")[0]
    if "t=" not in cu:
        continue
    c_code, c_pre = http("GET", f"{W}/email-action/{urllib.parse.quote(urllib.parse.unquote(cu.split('t=', 1)[1]))}")
    if c_code == 200 and str(c_pre.get("workflowId")) == str(o2["workflowRequestId"]):
        row = cand
        break
url, _, body = row.partition("|")
check("Onaycı e-postasında tek kullanımlık karar bağlantısı", "/onay-eposta?t=" in url, row[:120])
check("KVKK: e-postada talep konusu yok, tür ve kişi var", "fazla mesai talebi" in body and "saat fazla mesai (" not in body, body)
token = urllib.parse.unquote(url.split("t=", 1)[1]) if "t=" in url else ""
code, pre = http("GET", f"{W}/email-action/{urllib.parse.quote(token)}")
check("Bağlantı ön izlemesi (oturumsuz): yalnızca tür ve tarih", code == 200 and pre["type"] == "Overtime" and "Ayşe" not in json.dumps(pre, ensure_ascii=False), pre)
code, r = http("POST", f"{W}/email-action", {"token": token[:-4] + "0000", "decision": "Approved"})
check("Sahte jeton reddedilir", code == 404, (code, r))
code, r = http("POST", f"{W}/email-action", {"token": token, "decision": "Approved"})
check("E-postadan onaylandı", code == 200 and r["status"] == "Approved", r)
code, r = http("POST", f"{W}/email-action", {"token": token, "decision": "Rejected"})
check("Bağlantı tek kullanımlık", code == 404, (code, r))
_, w2b = api("ayse", "GET", f"{W}/{o2['workflowRequestId']}")
check("Onay geçmişinde '(e-posta üzerinden)'", w2b["status"] == "Approved" and "e-posta" in (w2b["steps"][0]["comment"] or ""), w2b["steps"][0])

# ====================================================================== Y23 vekâlet
code, r = api("mehmet", "POST", f"{W}/delegations", {"toEmployeeId": MEHMET, "startDate": today.isoformat(), "endDate": today.isoformat()})
check("Vekâlet: kendine verilemez", code == 400, r)
code, dl = api("mehmet", "POST", f"{W}/delegations", {"toEmployeeId": AYSE, "startDate": today.isoformat(), "endDate": (today + dt.timedelta(days=2)).isoformat(), "reason": "test-izin"})
check("Vekâlet verildi", code == 200, dl)
code, r = api("mehmet", "POST", f"{W}/delegations", {"toEmployeeId": ZEYNEP, "startDate": today.isoformat(), "endDate": today.isoformat(), "reason": "test-cakisan"})
check("Vekâlet: çakışan tarih reddedilir", code == 409, r)
_, w3b = api("ayse", "GET", f"{W}/{o3['workflowRequestId']}")
check("Talep sahibi kendi talebinde vekil olamaz (adım Mehmet'te kalır)", w3b["steps"][0].get("delegatedToEmployeeId") is None, w3b["steps"][0])
# Zeynep adına İK'nın açtığı talep → Mehmet'in adımı vekile (Ayşe) geçer
code, wz = api("admin", "POST", W, {"type": "Other", "requesterEmployeeId": ZEYNEP, "subject": "test-vekalet", "approverEmployeeIds": [MEHMET]})
check("Yeni talep vekâlet süresinde vekile atanır", code in (200, 201) and wz["steps"][0].get("delegatedToEmployeeId") == AYSE, wz.get("steps"))
code, _ = api("ayse", "GET", f"{W}/{wz['id']}")
check("Vekil (çalışan rolü) kaydı görür", code == 200, code)
code, dec = api("ayse", "POST", f"{W}/{wz['id']}/steps/{wz['steps'][0]['id']}/decide", {"decision": "Approved", "comment": "uygun"})
check("Vekil karar verdi; geçmişte '(vekâleten)'", code == 200 and "(vekâleten)" in (dec["steps"][0]["comment"] or ""), dec.get("steps") if isinstance(dec, dict) else dec)
code, wz2 = api("admin", "POST", W, {"type": "Other", "requesterEmployeeId": ZEYNEP, "subject": "test-vekalet-2", "approverEmployeeIds": [MEHMET]})
code, _ = api("mehmet", "DELETE", f"{W}/delegations/{dl['id']}")
_, wz2b = api("admin", "GET", f"{W}/{wz2['id']}")
check("Vekâlet geri alınınca bekleyen adım asıl onaycıya döner", code == 204 and wz2b["steps"][0].get("delegatedToEmployeeId") is None, wz2b["steps"][0])
code, _ = api("ayse", "GET", f"{W}/{wz2['id']}")
check("KVKK: vekâlet bitince eski vekil kaydı göremez", code == 404, code)
api("admin", "POST", f"{W}/{wz2['id']}/cancel")

# ====================================================================== G10 toplu onay
code, oa = api("ayse", "POST", f"{T}/overtime", {"date": d3.isoformat(), "hours": 1, "reason": "test-wf-toplu"})
items = [{"workflowId": oa["workflowRequestId"], "stepId": api("ayse", "GET", f"{W}/{oa['workflowRequestId']}")[1]["steps"][0]["id"]},
         {"workflowId": w3["id"], "stepId": w3["steps"][0]["id"]}]
code, _ = api("ayse", "POST", f"{W}/bulk-decide", {"items": items, "decision": "Approved"})
_, br = api("mehmet", "POST", f"{W}/bulk-decide", {"items": items + [{"workflowId": wz["id"], "stepId": wz["steps"][0]["id"]}], "decision": "Approved", "comment": "toplu"})
check("Toplu onay: 2 talep onaylandı, karar verilmiş olan atlandı", br["done"] == 2 and len(br["results"]) == 3, br)
_, w3c = api("ayse", "GET", f"{W}/{w3['id']}")
check("Toplu onaydan sonra ikinci adım (Zeynep) sırada", w3c["status"] == "Pending" and w3c["steps"][0]["decision"] == "Approved" and "(toplu karar)" in w3c["steps"][0]["comment"], w3c["steps"])

# Dalga 5: toplu ret gerekçe ister (tekil retteki kural); modül türleri genel uçtan açılamaz;
# serbest talepte onaycı giriş hesabı olan aktif çalışan olmalı.
code, r = api("mehmet", "POST", f"{W}/bulk-decide", {"items": items, "decision": "Rejected"})
check("Toplu ret: gerekçesiz reddedilir (400)", code == 400, (code, r))
code, r = api("ayse", "POST", W, {"type": "LeaveRequest", "requesterEmployeeId": AYSE, "subject": "test-modul", "approverEmployeeIds": [ZEYNEP]})
check("Modül türü (izin) genel 'Yeni talep' ucundan açılamaz", code == 400 and "kendi ekranından" in (r or {}).get("message", ""), (code, r))
code, r = api("ayse", "POST", W, {"type": "Other", "requesterEmployeeId": AYSE, "subject": "test-hesapsiz", "approverEmployeeIds": ["00000000-0000-0000-0000-000000000001"]})
check("Serbest talep: giriş hesabı olmayan onaycı reddedilir", code == 400, (code, r))
code, cands = api("ayse", "GET", f"{W}/approver-candidates")
check("Onaycı adayları: talep eden hariç, giriş hesabı olanlar", code == 200 and AYSE not in {c["id"] for c in cands} and MEHMET in {c["id"] for c in cands}, (code, cands))

# Dalga 5: olay radarı yalnızca İK; saklanan olaylarda e-posta karar jetonu ve onaycı e-postası yok.
code, _ = api("mehmet", "GET", f"{G}/events/recent?limit=5")
check("Olay radarı: yönetici erişemez (yalnızca İK)", code == 403, code)
code, evs = api("admin", "GET", f"{G}/events/recent?limit=50&type=workflow.submitted")
check("Olay radarı: workflow.submitted yükünde jeton/onaycı e-postası yok",
      code == 200 and all("ActionToken" not in json.dumps(e.get("payload")) and "ApproverEmail" not in json.dumps(e.get("payload")) for e in evs), code)
leaked = psql("SELECT count(*) FROM governance_events WHERE \"Payload\" ? 'ActionToken' AND \"OccurredAt\" > now() - interval '10 minutes'")
check("KVKK/güvenlik: yeni olaylar jetonsuz saklanıyor", leaked == "0", leaked)

# ====================================================================== G10 süre aşımında iletme
code, ox = api("ayse", "POST", f"{T}/overtime", {"date": d4.isoformat(), "hours": 1, "reason": "test-wf-sla"})
wfx = ox["workflowRequestId"]
psql(f"UPDATE workflow_requests SET \"SlaDueAt\" = now() - interval '1 hour' WHERE \"Id\"='{wfx}'")
esc = ""
for _ in range(25):
    esc = psql(f"SELECT coalesce(\"EscalatedAt\"::text,'') FROM workflow_approval_steps WHERE \"WorkflowRequestId\"='{wfx}'")
    if esc:
        break
    time.sleep(1)
check("Süre aşımı işlendi (üst yönetici yoksa bir kez işaretlenir)", bool(esc), esc)
api("ayse", "POST", f"{T}/overtime/{ox['id']}/cancel")

api("admin", "DELETE", f"{W}/definitions/{def_id}")

# ====================================================================== G8 izin
leave = "/api/leave"
for wk in range(0, 60, 3):
    day = (dt.date(2030, 3, 4) + dt.timedelta(weeks=random.randint(0, 150) + wk))
    code, lr = api("ayse", "POST", f"{leave}/leave-requests", {"employeeId": AYSE, "type": "Unpaid", "startDate": day.isoformat(), "endDate": day.isoformat(), "days": 0, "hours": 3, "reason": "test-saatlik"})
    if code != 409:
        break
check("Saatlik izin: 3 saat = 0,4 gün", code in (200, 201) and float(lr["days"]) == 0.4 and float(lr.get("hours") or 0) == 3, lr)
code, r = api("ayse", "POST", f"{leave}/leave-requests", {"employeeId": AYSE, "type": "Unpaid", "startDate": "2031-05-05", "endDate": "2031-05-06", "days": 0, "hours": 2})
check("Saatlik izin yalnızca tek gün", code == 400, r)
code, r = api("ayse", "POST", f"{leave}/leave-requests", {"employeeId": AYSE, "type": "Unpaid", "startDate": "2031-05-05", "endDate": "2031-05-05", "days": 0, "hours": 12.5})
check("Saatlik izin günlük çalışma süresini aşamaz", code == 400, r)
# Madde 44: onaylı izin iptali — çalışan iptal edemez (yalnızca İK/yönetici), bekleyen talep için iptal isteği açılmaz.
if isinstance(lr, dict) and lr.get("id"):
    code, r = api("ayse", "POST", f"{leave}/leave-requests/{lr['id']}/cancel-approved", {"reason": "test-iptal"})
    check("Onaylı izin iptali: çalışan yapamaz", code == 403, r)
    code, r = api("ayse", "POST", f"{leave}/leave-requests/{lr['id']}/request-cancellation", {"reason": "test-iptal"})
    check("İptal isteği yalnızca onaylı izin için (bekleyen talep 409)", code == 409, r)
    code, r = api("admin", "POST", f"{leave}/leave-requests/{lr['id']}/cancel-approved", {"reason": "x"})
    check("Onaylı izin iptali: gerekçe zorunlu", code == 400, r)

code, st = api("admin", "GET", f"{leave}/leave-balances/statutory?year={today.year}")
me_row = next((x for x in st or [] if x["employeeId"] == AYSE), None)
check("Yasal hak ön izlemesi (kıdeme göre)", code == 200 and me_row is not None and me_row["statutoryDays"] in (0, 14, 20, 26) and "birthDate" not in json.dumps(st), me_row)
code, _ = api("ayse", "GET", f"{leave}/leave-balances/statutory?year={today.year}")
check("Yetki: çalışan yasal hak listesini göremez", code == 403, code)

psql(f"DELETE FROM leave_balances WHERE \"EmployeeId\"='{AYSE}' AND \"Year\" IN (2040, 2041)")
api("admin", "POST", f"{leave}/leave-balances", {"employeeId": AYSE, "year": 2040, "type": "Annual", "entitledDays": 14})
code, co = api("admin", "POST", f"{leave}/leave-balances/carry-over", {"fromYear": 2040, "maxDays": 5})
bal = psql(f"SELECT \"Year\" || ':' || \"EntitledDays\" || ':' || \"CarriedOverDays\" || ':' || \"CarriedOutDays\" FROM leave_balances WHERE \"EmployeeId\"='{AYSE}' AND \"Year\" IN (2040,2041) AND \"Type\"='Annual' ORDER BY 1")
check("Devir: en fazla 5 gün sonraki yıla geçti", code == 200 and float(co["days"]) == 5 and bal.split("\n") == ["2040:9:0:5", "2041:5:5:0"], (co, bal))
code, co2 = api("admin", "POST", f"{leave}/leave-balances/carry-over", {"fromYear": 2040, "maxDays": 5})
check("Devir tekrar çalıştırılabilir (fark yoksa değişmez)", code == 200 and co2["employees"] == 0, co2)
code, co3 = api("admin", "POST", f"{leave}/leave-balances/carry-over", {"fromYear": 2040})
bal = psql(f"SELECT \"EntitledDays\" FROM leave_balances WHERE \"EmployeeId\"='{AYSE}' AND \"Year\"=2041 AND \"Type\"='Annual'")
check("Sınırsız devir: kalan 14 günün tamamı", code == 200 and float(bal) == 14, bal)
psql(f"DELETE FROM leave_balances WHERE \"EmployeeId\"='{AYSE}' AND \"Year\" IN (2040, 2041)")

# temizlik: bu testin fazla mesai kayıtları diğer testlerin (bordro) hesabına girmesin
psql("DELETE FROM timeshift_overtime_requests WHERE \"Reason\" LIKE 'test-wf-%'")

print(f"\nFAILS: {len(FAIL)}")
for f in FAIL:
    print("  -", f)
sys.exit(1 if FAIL else 0)

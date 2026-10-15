"""Dalga 5e sohbet botu uçtan uca testi (B6–B20, BG4–BG20) — sahte Slack / Teams / Mattermost /
Rocket.Chat (tests/integration/chatmock.py) ile.

Ön koşullar:
  * governance-service deploy/testing/chat-mock.yml ile (CHAT_JOBS_SECONDS, CHAT_RATE_USER, OCR_CLIENT_SECRET)
  * chatmock konteyneri (fixtures/receipt.png bağlı), scripts/sql/2026-10-10_chat_plus.sql uygulanmış
  * test_chat.py ile AYNI ANDA çalıştırmayın (ikisi de sohbet uygulamalarını sıfırlar)
Çalıştırma: timeout 900 python3 tests/integration/test_chat_plus.py
"""

import datetime as dt
import hashlib
import hmac
import json
import random
import subprocess
import sys
import time
import urllib.parse
import uuid

from common import ensure_transfers, AYSE, FAIL, api, check, http, mock, mock_calls, tok, wait_for  # noqa: E402

G = "/api/governance"
SIGNING = "slack-signing-secret"
MEHMET = "3ab24e3e-cb06-40ab-934c-9ff7eab91fb6"
ENG = "9d282a19-fe76-40c7-af56-757075718286"
RUN = uuid.uuid4().hex[:6]
TAG = f"TEST5E-{RUN}"
TODAY = (dt.datetime.utcnow() + dt.timedelta(hours=3)).date()
ensure_transfers("slack", "microsoft")


def sql(q, *args):
    for a in args:
        q = q.replace("%s", "'" + str(a).replace("'", "''") + "'", 1)
    out = subprocess.run(["docker", "exec", "-i", "hr360-postgres-1", "psql", "-v", "ON_ERROR_STOP=1", "-q", "-At", "-U", "hr360admin",
                          "-d", "hr360_operational", "-c", q], capture_output=True, text=True)
    if out.returncode != 0:
        print("SQL hata:", out.stderr.strip()[:300])
    return out.stdout.strip()


def plain(body):
    t = urllib.parse.unquote_plus(body)
    try:
        return t.encode("latin-1", "backslashreplace").decode("unicode_escape").encode("utf-16", "surrogatepass").decode("utf-16")
    except Exception:
        return t


def slack_post(app_id, kind, form=None, raw=None):
    body = raw if raw is not None else urllib.parse.urlencode(form)
    ts = str(int(time.time()))
    sig = "v0=" + hmac.new(SIGNING.encode(), f"v0:{ts}:{body}".encode(), hashlib.sha256).hexdigest()
    ctype = "application/json" if raw is not None else "application/x-www-form-urlencoded"
    return http("POST", f"{G}/chat/slack/{app_id}/{kind}", raw_body=body,
                headers={"X-Slack-Request-Timestamp": ts, "X-Slack-Signature": sig, "Content-Type": ctype})


def buttons(blocks):
    return [e for b in (blocks or []) if b.get("type") == "actions" for e in b.get("elements", [])]


def btn(blocks, action):
    return next((e for e in buttons(blocks) if e.get("action_id", "").startswith(f"hr360x:{action}:")), None)


def post_blocks(call):
    q = urllib.parse.parse_qs(call["body"])
    return json.loads(q["blocks"][0]) if "blocks" in q else []


SEQ = [0]


def click(app_id, user, button, trigger=None):
    """Slack düğmesine basar; response_url'e gelen yanıtı döndürür."""
    SEQ[0] += 1
    tag = f"5e-{RUN}-{SEQ[0]}"
    payload = {"type": "block_actions", "user": {"id": user}, "actions": [{"action_id": button["action_id"], "value": button.get("value", "")}],
               "response_url": f"http://chatmock:8000/slack-response/{tag}"}
    if trigger:
        payload["trigger_id"] = trigger
    t = time.time()
    code, _ = slack_post(app_id, "interactions", {"payload": json.dumps(payload)})
    if trigger:
        return code
    r = wait_for(f"/slack-response/{tag}", since=t, timeout=60)
    return json.loads(r[0]["body"]) if r else {}


def cmd(app_id, user, text):
    code, r = slack_post(app_id, "commands", {"user_id": user, "text": text})
    return r or {}


def link_code(text):
    import re
    m = re.search(r"/panel/sohbet-bagla\?kod=([A-Za-z0-9_-]+)", urllib.parse.unquote_plus(text or ""))
    return m.group(1) if m else None


def run_jobs():
    code, r = api("admin", "POST", f"{G}/chat-admin/jobs/run")
    check("Zamanlanmış işler çalıştı", code == 200, r)
    return r or {}


# ====================================================================== hazırlık
mock("/_reset", "POST")
code, apps = api("admin", "GET", f"{G}/chat-apps")
for a in apps or []:
    api("admin", "DELETE", f"{G}/chat-apps/{a['id']}")

# Test çalışanları (yalnızca T5E1 Mühendislik'te: takas aynı departman kuralı için).
TE = {}
# Yarıda kalmış önceki koşudan kalan test çalışanları (aynı e-posta benzersiz).
_old = """SELECT "Id" FROM employee_employees WHERE "TenantSlug" = 'demo' AND "Email" LIKE 't5ep_.x@demo.hr360'"""
sql(f"""DELETE FROM employee_assignments WHERE "EmployeeId" IN ({_old})""")
sql(f"""DELETE FROM employee_employees WHERE "Id" IN ({_old})""")
for i in range(1, 5):
    eid = str(uuid.uuid4())
    hire = (TODAY.replace(year=TODAY.year - 3) if i == 3 else TODAY - dt.timedelta(days=30)).isoformat()
    sql(f"""INSERT INTO employee_employees ("Id","TenantSlug","FirstName","LastName","Email","HireDate","Status","CreatedAt")
            VALUES ('{eid}','demo','T5e{i}','Test{RUN}','t5ep{i}.x@demo.hr360','{hire}','Active',now())""")
    TE[i] = eid
sql(f"""INSERT INTO employee_assignments ("Id","TenantSlug","EmployeeId","DepartmentId","PositionTitle","EffectiveFrom","CreatedAt")
        VALUES ('{uuid.uuid4()}','demo','{TE[1]}','{ENG}','Test','2020-01-01',now())""")

code, r = api("admin", "POST", f"{G}/chat-apps", {"platform": "Slack", "name": "HR360 Slack", "isEnabled": True, "notifyApprovals": True, "notifyRequesters": True,
                                                   "slackBotToken": "xoxb-test-token", "slackSigningSecret": SIGNING, "channelId": "C_GENEL", "celebrationsEnabled": True})
check("Slack uygulaması (kanal + kutlama ayarı)", code == 200 and r.get("channelId") == "C_GENEL" and r.get("celebrationsEnabled"), r)
SL = r["id"]

for user, who in (("U_AYSE", "ayse"), ("U_MEHMET", "mehmet")):
    r = cmd(SL, user, "ben")
    c = link_code(r.get("text"))
    api(who, "GET", f"{G}/chat/link/{c}")
    code, _ = api(who, "POST", f"{G}/chat/link", {"code": c})
    check(f"Slack: {who} bağlandı", code == 200, code)
for i in range(1, 5):
    cmd(SL, f"U_T5EP{i}", "ben")
sql(f"""UPDATE governance_chat_identities SET "VerifiedAt" = now() WHERE "AppId" = '{SL}' AND "ExternalUserId" LIKE 'U_T5EP%'""")

# ====================================================================== BG7 yazım hatası + yardım düğmeleri
r = cmd(SL, "U_AYSE", "bakyie")
check("BG7: 'bakyie' → bakiye (yazım hatası düzeltildi)", "izin bakiyeniz" in r.get("text", "") and "olarak anladım" in r.get("text", ""), r.get("text"))
r = cmd(SL, "U_AYSE", "VARDİYAM")
check("BG7: büyük harf + Türkçe karakter ('VARDİYAM' → vardiya)", "vardiya" in r.get("text", "").lower(), r.get("text"))
r = cmd(SL, "U_AYSE", "yardım")
say = [b for b in buttons(r.get("blocks")) if b.get("action_id", "").startswith("hr360x:say:")]
check("BG7: yardım mesajı ana komut düğmeleriyle", len(say) >= 5 and "Komutlar" in r.get("text", ""), r)
bal = next((b for b in say if b["value"].startswith("bakiye~")), None)
res = click(SL, "U_AYSE", bal) if bal else {}
check("BG7: 'Bakiye' düğmesi komutu çalıştırır", "izin bakiyeniz" in res.get("text", ""), res)

# ====================================================================== BG14 hız sınırı
limited = None
for _ in range(45):
    r = cmd(SL, "U_RATE5E", "yardım")
    if "yavaşlayalım" in r.get("text", ""):
        limited = r
        break
check("BG14: kişi başına hız sınırı (dostça mesaj)", limited is not None, r)

# ====================================================================== B10 teşekkür + BG20 kapalı komut
r = cmd(SL, "U_AYSE", f"teşekkür <@U_MEHMET> {TAG} harika sunum")
check("B10: teşekkür iletildi", "iletildi" in r.get("text", ""), r.get("text"))
check("B10: takdir kaydı (engagement)", sql(f"""SELECT count(*) FROM engagement_kudos WHERE "ToEmployeeId" = '{MEHMET}' AND "Message" LIKE '{TAG}%'""") == "1")
dm = wait_for("/api/chat.postMessage", lambda c: "D_U_MEHMET" in c["body"] and TAG in plain(c["body"]) and "teşekkür etti" in plain(c["body"]), since=time.time() - 30)
check("B10: alıcıya DM gitti", bool(dm), mock_calls("chat.postMessage")[-2:])
r = cmd(SL, "U_AYSE", "teşekkür <@U_AYSE> kendime")
check("B10: kendine teşekkür reddedilir", "Kendinize" in r.get("text", ""), r.get("text"))

code, _ = api("admin", "PUT", f"{G}/chat-admin/apps/{SL}/features", {"disabled": ["kudos"]})
r = cmd(SL, "U_AYSE", f"teşekkür <@U_MEHMET> {TAG} ikinci")
check("BG20: kapalı komut 'şirketinizde kapalı'", code == 200 and "şirketinizde kapalı" in r.get("text", ""), r.get("text"))
code, st = api("admin", "GET", f"{G}/chat-admin/apps/{SL}/stats")
check("BG20: kullanım sayıları (yalnızca sayı)", code == 200 and any(x["feature"] == "kudos" and x["outcome"] == "disabled" for x in st["byFeature"])
      and "ayse" not in json.dumps(st).lower(), st)
code, _ = api("ayse", "GET", f"{G}/chat-admin/apps/{SL}/stats")
check("BG20: çalışan yönetim ekranını göremez", code == 403, code)
api("admin", "PUT", f"{G}/chat-admin/apps/{SL}/features", {"disabled": []})
code, feats = api("admin", "GET", f"{G}/chat-admin/features")
check("BG20: özellik listesi", code == 200 and any(f["key"] == "pulse" for f in feats), feats)

# ====================================================================== BG6 sessiz saat
now_tr = dt.datetime.utcnow() + dt.timedelta(hours=3)
quiet = {"enabled": True, "start": (now_tr - dt.timedelta(hours=1)).strftime("%H:%M"), "end": (now_tr + dt.timedelta(hours=2)).strftime("%H:%M"), "days": list(range(7))}
code, _ = api("mehmet", "PUT", "/api/notification/notifications/preferences/me", {"quietHours": quiet})
check("BG6: Mehmet sessiz saati açtı", code == 200, code)
tq = time.time()
cmd(SL, "U_AYSE", f"teşekkür <@U_MEHMET> {TAG} sessiz")
time.sleep(3)
early = [c for c in mock_calls("/api/chat.postMessage", tq) if "D_U_MEHMET" in c["body"] and "sessiz" in plain(c["body"])]
queued = sql(f"""SELECT count(*) FROM governance_chat_outbox WHERE "AppId" = '{SL}' AND "Kind" = 'card' AND "NextAttemptAt" > now()""")
check("BG6: sessiz saatte DM gönderilmedi, kuyruğa ertelendi", not early and queued == "1", (early, queued))
api("mehmet", "PUT", "/api/notification/notifications/preferences/me", {"quietHours": {"enabled": False, "start": "22:00", "end": "08:00", "days": list(range(7))}})
sql(f"""UPDATE governance_chat_outbox SET "NextAttemptAt" = now() WHERE "AppId" = '{SL}' AND "Kind" = 'card'""")
late = wait_for("/api/chat.postMessage", lambda c: "D_U_MEHMET" in c["body"] and "sessiz" in plain(c["body"]), since=tq, timeout=40)
check("BG6: sessiz saat bitince kuyruktan gönderildi", bool(late), late)

# ====================================================================== B13 masa
day = TODAY + dt.timedelta(days=1)
r = cmd(SL, "U_AYSE", "masa yarın")
if "masanız var" in r.get("text", ""):
    click(SL, "U_AYSE", btn(r["blocks"], "desk_cancel"))
    r = cmd(SL, "U_AYSE", "masa yarın")
b = btn(r.get("blocks"), "desk_book")
check("B13: boş masalar düğmeyle listelendi", b is not None, r)
if b:
    res = click(SL, "U_AYSE", b)
    check("B13: masa ayrıldı (kart güncellendi)", "ayrıldı" in res.get("text", "") and res.get("replace_original") is True, res)
    bid = sql(f"""SELECT "Id" FROM engagement_desk_bookings WHERE "EmployeeId" = '{AYSE}' AND "Date" = '{day}'""")
    check("B13: rezervasyon kaydı ve 'ofiste' durumu", bool(bid) and sql(f"""SELECT "Mode" FROM engagement_presence WHERE "EmployeeId" = '{AYSE}' AND "Date" = '{day}'""") == "Office", bid)
    res2 = click(SL, "U_AYSE", btn(res.get("blocks"), "desk_cancel"))
    check("B13: rezervasyon iptal edildi", "iptal" in res2.get("text", "") and not sql(f"""SELECT "Id" FROM engagement_desk_bookings WHERE "Id" = '{bid}'"""), res2)

# ====================================================================== BG10 eski düğme
old = format(int((time.time() - 30 * 86400) // 60), "x")
res = click(SL, "U_AYSE", {"action_id": "hr360x:desk_day:0", "value": f"{day}~{old}"})
check("BG10: süresi dolmuş düğme nazik mesajla reddedildi", "süresi doldu" in res.get("text", "") and res.get("replace_original") is True, res)

# ====================================================================== B17 giriş-çıkış
r = cmd(SL, "U_T5EP1", "geldim")
check("B17: giriş kaydedildi", "Giriş kaydedildi" in r.get("text", ""), r.get("text"))
r2 = cmd(SL, "U_T5EP1", "geldim")
check("B17: açık giriş varken ikinci giriş reddedilir", "Açık bir giriş" in r2.get("text", ""), r2.get("text"))
r3 = cmd(SL, "U_T5EP1", "çıktım")
check("B17: çıkış kaydedildi", "Çıkış kaydedildi" in r3.get("text", ""), r3.get("text"))
check("B17: puantaj kaydı ve 'Chat' yöntemi", sql(f"""SELECT "Source" || ',' || ("ClockOut" IS NOT NULL) FROM timeshift_time_entries WHERE "EmployeeId" = '{TE[1]}'""") == "Chat,true"
      and sql(f"""SELECT count(*) FROM timeshift_clock_punches WHERE "EmployeeId" = '{TE[1]}' AND "Method" = 'Chat'""") == "2")
code, me = http("GET", "/api/timeshift/time-clock/me", headers={"Authorization": "Bearer " + tok("ayse")})
check("B17: timeshift 'Chat' kaynağını okuyabiliyor (enum)", code == 200, (code, me))

# ====================================================================== B14 vardiya + takas
sh = str(uuid.uuid4())
sql(f"""INSERT INTO timeshift_shifts ("Id","TenantSlug","Name","StartTime","EndTime","BreakMinutes","IsNightShift","CreatedAt") VALUES ('{sh}','demo','{TAG} Sabah','08:00','16:00',60,false,now())""")
d1, d2 = TODAY + dt.timedelta(days=20), TODAY + dt.timedelta(days=22)
a1, a2 = str(uuid.uuid4()), str(uuid.uuid4())
sql(f"""INSERT INTO timeshift_assignments ("Id","TenantSlug","EmployeeId","ShiftId","Date","CreatedAt") VALUES ('{a1}','demo','{AYSE}','{sh}','{d1}',now()), ('{a2}','demo','{TE[1]}','{sh}','{d2}',now())""")
swap = str(uuid.uuid4())
sql(f"""INSERT INTO timeshift_swap_requests ("Id","TenantSlug","RequesterEmployeeId","RequesterAssignmentId","TargetEmployeeId","TargetAssignmentId","Status","CreatedAt")
        VALUES ('{swap}','demo','{AYSE}','{a1}','{TE[1]}','{a2}','PendingPeer',now())""")
r = cmd(SL, "U_T5EP1", "vardiyam")
check("B14: 'vardiyam' sonraki vardiyaları gösterir", f"{TAG} Sabah" in r.get("text", ""), r.get("text"))
r = cmd(SL, "U_T5EP1", "takas")
acc = btn(r.get("blocks"), "swap_accept")
check("B14: hedef kişiye Kabul/Reddet düğmeleri", acc is not None and "Ayşe Y." in r.get("text", "") and "Yılmaz" not in r.get("text", ""), r.get("text"))
if acc:
    res = click(SL, "U_T5EP1", acc)
    check("B14: takas kabul edildi → yönetici onayı", "yönetici onayı" in res.get("text", "") and sql(f"""SELECT "Status" FROM timeshift_swap_requests WHERE "Id" = '{swap}'""") == "PendingApproval", res)
r = cmd(SL, "U_MEHMET", "takas")
appr = btn(r.get("blocks"), "swap_approve")
check("B14: bölüm başına Onayla düğmesi", appr is not None, r.get("text"))
r_other = cmd(SL, "U_AYSE", "takas")
check("B14: taraf kendi takasını onaylayamaz (düğme yok)", btn(r_other.get("blocks"), "swap_approve") is None, r_other.get("text"))
if appr:
    res = click(SL, "U_MEHMET", appr)
    check("B14: takas onaylandı ve atamalar değişti", "onaylandı" in res.get("text", "")
          and sql(f"""SELECT "EmployeeId" FROM timeshift_assignments WHERE "Id" = '{a1}'""") == TE[1]
          and sql(f"""SELECT "EmployeeId" FROM timeshift_assignments WHERE "Id" = '{a2}'""") == AYSE, res)

# ====================================================================== B8 izin iptali
RUN_WEEK = random.randint(0, 150)
wf_cancel = None
for extra in range(0, 60, 7):
    start = (dt.date(2029, 3, 5) + dt.timedelta(weeks=RUN_WEEK, days=extra)).isoformat()
    code, lr = api("ayse", "POST", "/api/leave/leave-requests", {"employeeId": AYSE, "type": "Unpaid", "startDate": start, "endDate": start, "days": 0, "reason": TAG})
    if code in (200, 201):
        wf_cancel, leave_id = lr["workflowRequestId"], lr["id"]
        break
check("B8: test izni oluşturuldu", wf_cancel is not None, lr)
r = cmd(SL, "U_AYSE", "izin iptal")
cb = next((b for b in buttons(r.get("blocks")) if b.get("action_id", "").startswith("hr360x:leave_cancel:") and b["value"].startswith(leave_id)), None) if wf_cancel else None
check("B8: iptal edilebilir talepler düğmeyle", cb is not None, r)
if cb:
    res = click(SL, "U_AYSE", cb)
    yes = btn(res.get("blocks"), "leave_cancel_yes")
    check("B8: onay adımı ('Emin misiniz?')", yes is not None and "Emin" in res.get("text", ""), res)
    res2 = click(SL, "U_AYSE", yes)
    check("B8: izin iptal edildi, onay akışı kapandı", "iptal edildi" in res2.get("text", "")
          and sql(f"""SELECT "Status" FROM leave_requests WHERE "Id" = '{leave_id}'""") == "Cancelled"
          and sql(f"""SELECT "Status" FROM workflow_requests WHERE "Id" = '{wf_cancel}'""") == "Cancelled", res2)
    res3 = click(SL, "U_AYSE", yes)
    check("B8: ikinci tıklama tekrar iptal etmez", "artık iptal edilemez" in res3.get("text", ""), res3)

# ====================================================================== BG4 + BG5 + BG13 onay kartı, toplu onay, ek doğrulama
wf_card = None
for extra in range(0, 120, 7):
    start = (dt.date(2029, 6, 4) + dt.timedelta(weeks=RUN_WEEK, days=extra)).isoformat()
    code, lr = api("ayse", "POST", "/api/leave/leave-requests", {"employeeId": AYSE, "type": "Unpaid", "startDate": start, "endDate": start, "days": 0, "reason": TAG})
    if code in (200, 201):
        wf_card, card_leave = lr["workflowRequestId"], lr["id"]
        break
card = wait_for("/api/chat.postMessage", lambda c: "D_U_MEHMET" in c["body"] and wf_card and wf_card in urllib.parse.unquote_plus(c["body"]), since=time.time() - 60)
ctext = plain(card[0]["body"]) if card else ""
check("BG4: onay kartında ekipten izinli SAYISI (ad yok)", "ekipten" in ctext and "kişi izinde" in ctext and "Yılmaz" not in ctext, ctext[:400])
if wf_card:
    api("ayse", "POST", f"/api/leave/leave-requests/{card_leave}/cancel")

# Toplu onay: başka testlerin taleplerini etkilememek için onaycısı Ayşe olan, yalnızca bu teste ait iki talep (SQL).
wfs = []
for k in range(2):
    w, s_ = str(uuid.uuid4()), str(uuid.uuid4())
    sql(f"""INSERT INTO workflow_requests ("Id","TenantSlug","Type","Status","RequesterEmployeeId","Subject","CreatedAt") VALUES ('{w}','demo','Other','Pending','{TE[1]}','{TAG} toplu {k}',now())""")
    sql(f"""INSERT INTO workflow_approval_steps ("Id","TenantSlug","WorkflowRequestId","Order","ApproverEmployeeId","Decision","CreatedAt") VALUES ('{s_}','demo','{w}',1,'{AYSE}','Pending',now())""")
    wfs.append(w)
pending_other = sql(f"""SELECT count(*) FROM workflow_approval_steps s JOIN workflow_requests w ON w."Id" = s."WorkflowRequestId"
    WHERE s."ApproverEmployeeId" = '{AYSE}' AND s."Decision" = 'Pending' AND w."Status" = 'Pending' AND w."Type" = 'Other' AND w."Id" NOT IN ('{wfs[0]}','{wfs[1]}')""")
r = cmd(SL, "U_AYSE", "onaylarım")
allbs = [b for b in buttons(r.get("blocks")) if b.get("action_id", "").startswith("hr360x:approveall:")]
# Başka türde bekleyen onay varsa yalnızca "Other" türünü toplu onaylayan düğme kullanılır.
allb = next((b for b in allbs if b["value"].startswith("Other~")), None) or (allbs[0] if len(allbs) == 1 else None)
check("BG5: 'Tümünü onayla (n)' düğmesi ve özet", allb is not None and "onay" in r.get("text", ""), r.get("text"))
if allb and pending_other == "0":
    res = click(SL, "U_AYSE", allb)
    yes = btn(res.get("blocks"), "approveall_yes")
    check("BG5: onay adımı", yes is not None and "Emin misiniz" in res.get("text", ""), res)
    res2 = click(SL, "U_AYSE", yes)
    import re as _re
    m = _re.search(r"/panel/sohbet-onay\?kod=([A-Za-z0-9_-]+)", res2.get("text", ""))
    check("BG13: toplu onay HR360'ta ek doğrulama ister", m is not None, res2)
    if m:
        code_ = m.group(1)
        c1, _ = api("mehmet", "GET", f"{G}/chat/stepup/{code_}")
        check("BG13: başkası doğrulama bağlantısını kullanamaz", c1 == 404, c1)
        check("BG13: doğrulanmadan onay verilmedi", all(sql(f"""SELECT "Status" FROM workflow_requests WHERE "Id" = '{w}'""") == "Pending" for w in wfs))
        c2, prev = api("ayse", "GET", f"{G}/chat/stepup/{code_}")
        tc = time.time()
        c3, conf = api("ayse", "POST", f"{G}/chat/stepup", {"code": code_})
        check("BG13: oturum açık kişi doğruladı", c2 == 200 and prev["kind"] == "bulk" and c3 == 200, (c2, prev, c3, conf))
        time.sleep(3)
        check("BG5: tüm talepler onaylandı", all(sql(f"""SELECT "Status" FROM workflow_requests WHERE "Id" = '{w}'""") == "Approved" for w in wfs))
        done = wait_for("/api/chat.postMessage", lambda c: "D_U_AYSE" in c["body"] and "talep onaylandı" in plain(c["body"]), since=tc)
        check("BG13: sonuç sohbete yazıldı", bool(done))
        c4, _ = api("ayse", "POST", f"{G}/chat/stepup", {"code": code_})
        check("BG13: doğrulama bağlantısı tek kullanımlık", c4 == 404, c4)
elif allb:
    print(f"SKIP BG5 toplu onay: Ayşe'nin bu teste ait olmayan {pending_other} bekleyen onayı var")

# ücretle ilgili onay (PositionChange) tek tek de ek doğrulama ister
pwf, pstep = str(uuid.uuid4()), str(uuid.uuid4())
sql(f"""INSERT INTO workflow_requests ("Id","TenantSlug","Type","Status","RequesterEmployeeId","Subject","Payload","CreatedAt") VALUES ('{pwf}','demo','PositionChange','Pending','{AYSE}','{TAG} pozisyon','{{"salary": 1}}',now())""")
sql(f"""INSERT INTO workflow_approval_steps ("Id","TenantSlug","WorkflowRequestId","Order","ApproverEmployeeId","Decision","CreatedAt") VALUES ('{pstep}','demo','{pwf}',1,'{MEHMET}','Pending',now())""")
SEQ[0] += 1
tp = time.time()
slack_post(SL, "interactions", {"payload": json.dumps({"type": "block_actions", "user": {"id": "U_MEHMET"}, "actions": [{"action_id": "hr360_approve", "value": f"{pwf}|{pstep}"}],
                                                       "response_url": f"http://chatmock:8000/slack-response/pc-{RUN}"})})
resp = wait_for(f"/slack-response/pc-{RUN}", since=tp)
check("BG13: ücretle ilgili onay sohbetten doğrudan verilmez (ek doğrulama)", bool(resp) and "ek doğrulama" in json.loads(resp[0]["body"]).get("text", "")
      and sql(f"""SELECT "Status" FROM workflow_requests WHERE "Id" = '{pwf}'""") == "Pending", resp)

# bordro özeti: OTP yolu
r = cmd(SL, "U_AYSE", "bordrom")
check("BG13: bordro özeti ek doğrulama ister", "ek doğrulama" in r.get("text", "") and "sohbet-onay" in r.get("text", ""), r.get("text"))
code, otps = api("ayse", "GET", f"{G}/chat/stepup")
check("BG13: panelde 6 haneli kod", code == 200 and otps and len(otps[0]["otp"]) == 6, otps)
r = cmd(SL, "U_AYSE", "kod 000000" if otps and otps[0]["otp"] != "000000" else "kod 111111")
check("BG13: yanlış kod reddedilir", "geçersiz" in r.get("text", ""), r.get("text"))
if otps:
    r = cmd(SL, "U_AYSE", f"kod {otps[0]['otp']}")
    check("BG13: kodla doğrulandı, bordro özeti yalnızca kişiye", "bordro" in r.get("text", "").lower(), r.get("text"))
    check("BG13: bordro görüntüleme denetim kaydına yazıldı", sql(f"""SELECT count(*) FROM audit_log WHERE "Service" = 'governance-service/chat' AND "EntityType" = 'Payslip' AND "EntityId" = '{AYSE}' AND "OccurredAt" > now() - interval '5 minutes'""") != "0")

# eski onay kartlarının süresi (BG10)
wf_old = None
for extra in range(0, 120, 7):
    start = (dt.date(2029, 9, 3) + dt.timedelta(weeks=RUN_WEEK, days=extra)).isoformat()
    code, lr = api("ayse", "POST", "/api/leave/leave-requests", {"employeeId": AYSE, "type": "Unpaid", "startDate": start, "endDate": start, "days": 0, "reason": TAG})
    if code in (200, 201):
        wf_old, old_leave = lr["workflowRequestId"], lr["id"]
        break
wait_for("/api/chat.postMessage", lambda c: "D_U_MEHMET" in c["body"] and wf_old and wf_old in urllib.parse.unquote_plus(c["body"]), since=time.time() - 30)
time.sleep(2)
sql(f"""UPDATE governance_chat_messages SET "CreatedAt" = now() - interval '30 days' WHERE "WorkflowRequestId" = '{wf_old}'""")
te = time.time()
run_jobs()
exp = wait_for("/api/chat.update", lambda c: "süresi doldu" in plain(c["body"]), since=te)
check("BG10: eski onay kartı güncellendi (düğmeler kalktı)", bool(exp) and "hr360_approve" not in plain(exp[0]["body"]), exp)

# ====================================================================== B6 fişten masraf (Slack)
t6 = time.time()
slack_post(SL, "events", raw=json.dumps({"type": "event_callback", "event": {"type": "message", "subtype": "file_share", "channel_type": "im", "user": "U_AYSE",
           "channel": "D_U_AYSE", "files": [{"url_private_download": "http://chatmock:8000/slackfiles/receipt.png", "mimetype": "image/png"}]}}))
dl = wait_for("/slackfiles/receipt.png", since=t6, timeout=30)
check("B6: fiş bot jetonuyla indirildi", bool(dl) and dl[0]["auth"] == "Bearer xoxb-test-token", dl)
card = wait_for("/api/chat.postMessage", lambda c: "D_U_AYSE" in c["body"] and "Fiş" in plain(c["body"]), since=t6, timeout=90)
ctext = plain(card[0]["body"]) if card else ""
check("B6: OCR önerisi (tutar, tarih, kategori)", "245,50" in ctext and "01.10.2026" in ctext and "Yemek" in ctext and "saklanmadı" in ctext, ctext[:500])
if card:
    blocks = post_blocks(card[0])
    edit = btn(blocks, "exp_edit")
    code = click(SL, "U_AYSE", edit, trigger="TRIG5E") if edit else 0
    modal = wait_for("/api/views.open", lambda c: "TRIG5E" in c["body"], t6)
    view = json.loads(urllib.parse.parse_qs(modal[0]["body"])["view"][0]) if modal else {}
    check("B6: 'Düzelt' penceresi önerilerle dolu", view.get("callback_id") == "hr360_expense" and "245.50" in json.dumps(view), view.get("callback_id"))
    conf = btn(blocks, "exp_confirm")
    res = click(SL, "U_AYSE", conf)
    check("B6: taslak masraf oluşturuldu", "Taslak masraf" in res.get("text", "") and res.get("replace_original") is True, res)
    check("B6: expense_claims'te Draft (245,50 TL, Meal)", sql(f"""SELECT c."Status" || ',' || c."TotalAmount" || ',' || i."Category" FROM expense_claims c JOIN expense_items i ON i."ClaimId" = c."Id"
        WHERE c."EmployeeId" = '{AYSE}' AND c."CreatedAt" > now() - interval '5 minutes' AND i."Description" = 'Sohbetten (fiş okuma)' ORDER BY c."CreatedAt" DESC LIMIT 1""") == "Draft,245.50,Meal")
    res2 = click(SL, "U_AYSE", conf)
    check("B6: aynı öneri ikinci kez kullanılamaz", "geçerli değil" in res2.get("text", "") or "kullanıldı" in res2.get("text", ""), res2)
    # düzeltme penceresinden
    r = cmd(SL, "U_AYSE", "masraf 99,90 02.10.2026 taksi")
    conf2 = btn(r.get("blocks"), "exp_confirm")
    check("B6: elle öneri kartı (tutar/tarih/kategori)", conf2 is not None and "99,90" in r.get("text", "") and "Ulaşım" in r.get("text", ""), r.get("text"))
    if conf2:
        pid = conf2["value"].split("~")[0]
        code, rr = slack_post(SL, "interactions", {"payload": json.dumps({"type": "view_submission", "user": {"id": "U_AYSE"}, "view": {
            "callback_id": "hr360_expense", "private_metadata": pid, "state": {"values": {"amount": {"v": {"value": "88,10"}}, "date": {"v": {"selected_date": "2026-10-02"}},
                                                                                        "category": {"v": {"selected_option": {"value": "Transport"}}}}}}})})
        check("B6: düzeltme penceresinden taslak", code == 200 and rr.get("response_action") == "clear"
              and sql(f"""SELECT count(*) FROM expense_claims WHERE "EmployeeId" = '{AYSE}' AND "TotalAmount" = 88.10 AND "CreatedAt" > now() - interval '5 minutes'""") == "1", rr)

# ====================================================================== B15 duyuru + Okudum
ta = time.time()
code, ann = api("admin", "POST", f"{G}/announcements", {"title": f"{TAG} Duyuru", "body": "Yeni ofis kuralları.", "audience": "All", "requiresAck": True})
check("B15: duyuru oluşturuldu", code in (200, 201), ann)
ann_id = sql(f"""SELECT "Id" FROM governance_announcements WHERE "Title" = '{TAG} Duyuru'""")
run_jobs()
ch = wait_for("/api/chat.postMessage", lambda c: "C_GENEL" in c["body"] and f"{TAG} Duyuru" in plain(c["body"]), since=ta)
check("B15: 'herkes' duyurusu kanala yazıldı", bool(ch), mock_calls("chat.postMessage", ta)[-3:])
adm = wait_for("/api/chat.postMessage", lambda c: "D_U_AYSE" in c["body"] and f"{TAG} Duyuru" in plain(c["body"]) and "ann_ack" in plain(c["body"]), since=ta)
check("B15: okuma onayı DM'i 'Okudum' düğmesiyle", bool(adm))
if adm:
    res = click(SL, "U_AYSE", btn(post_blocks(adm[0]), "ann_ack"))
    check("B15: 'Okudum' kaydedildi", "Okundu" in res.get("text", "") and sql(f"""SELECT count(*) FROM governance_acknowledgements WHERE "SubjectId" = '{ann_id}' AND "EmployeeId" = '{AYSE}'""") == "1", res)
    r = cmd(SL, "U_AYSE", "duyurular")
    check("B15: 'duyurular' okundu bilgisini gösterir", f"{TAG} Duyuru" in r.get("text", "") and "okundu" in r.get("text", ""), r.get("text"))

# ====================================================================== B16 belge talebi
tpl = str(uuid.uuid4())
sql(f"""INSERT INTO governance_doc_templates ("Id","TenantSlug","Name","Category","Body","SelfService","RequiresApproval","CreatedAt","UpdatedAt")
        VALUES ('{tpl}','demo','{TAG} Belge','Genel','<p>GIZLI-ICERIK {{{{calisan.ad}}}}</p>',true,false,now(),now())""")
r = cmd(SL, "U_AYSE", "belge")
db_ = next((b for b in buttons(r.get("blocks")) if b.get("action_id", "").startswith("hr360x:doc_req:") and b["value"].startswith(tpl)), None)
check("B16: talep edilebilir belgeler düğmeyle", db_ is not None, r)
if db_:
    res = click(SL, "U_AYSE", db_)
    txt = json.dumps(res, ensure_ascii=False)
    check("B16: belge hazır — yalnızca bağlantı ve doğrulama sayfası, içerik yok", "hazır" in res.get("text", "") and "belge-dogrula/" in txt and "GIZLI-ICERIK" not in txt, res)

# ====================================================================== B9 işe başlama
tb = time.time()
plan0, plan1 = str(uuid.uuid4()), str(uuid.uuid4())
sql(f"""INSERT INTO onboarding_plans ("Id","TenantSlug","EmployeeId","StartDate","Status","CreatedAt","BuddyEmployeeId") VALUES ('{plan0}','demo','{TE[2]}','{TODAY}','InProgress',now(),'{MEHMET}'),
        ('{plan1}','demo','{TE[3]}','{TODAY - dt.timedelta(days=1)}','InProgress',now(),NULL)""")
task = str(uuid.uuid4())
sql(f"""INSERT INTO onboarding_tasks ("Id","TenantSlug","PlanId","Title","Category","Status","Order","OwnerRole") VALUES
        ('{uuid.uuid4()}','demo','{plan0}','{TAG} Laptop teslim al','IT','Pending',1,'Employee'),
        ('{task}','demo','{plan1}','{TAG} KVKK eğitimi','Training','Pending',1,'Employee'),
        ('{uuid.uuid4()}','demo','{plan1}','{TAG} İK görevi','HR','Pending',2,'HR')""")
run_jobs()
w = wait_for("/api/chat.postMessage", lambda c: "D_U_T5EP2" in c["body"] and "hoş geldin" in plain(c["body"]).lower(), since=tb)
check("B9: yeni çalışana 0. gün karşılama DM'i (onboarding arkadaşıyla)", bool(w) and "Mehmet D." in plain(w[0]["body"]), w)
bd = wait_for("/api/chat.postMessage", lambda c: "D_U_MEHMET" in c["body"] and "onboarding arkadaşı" in plain(c["body"]), since=tb)
check("B9: onboarding arkadaşına tanıtım DM'i", bool(bd))
cl = wait_for("/api/chat.postMessage", lambda c: "D_U_T5EP3" in c["body"] and "1. gün" in plain(c["body"]), since=tb)
cltxt = plain(cl[0]["body"]) if cl else ""
check("B9: 1. gün kontrol listesi yalnızca kişinin görevleri", f"{TAG} KVKK eğitimi" in cltxt and "İK görevi" not in cltxt, cltxt[:300])
if cl:
    res = click(SL, "U_T5EP3", btn(post_blocks(cl[0]), "onb_done"))
    check("B9: 'tamam' düğmesi görevi kapattı", "Tamamlandı" in res.get("text", "") and sql(f"""SELECT "Status" FROM onboarding_tasks WHERE "Id" = '{task}'""") == "Done", res)
n_before = len(mock_calls("/api/chat.postMessage", tb))
run_jobs()
time.sleep(2)
check("B9: aynı mesaj ikinci kez gönderilmez", not [c for c in mock_calls("/api/chat.postMessage", tb)[n_before:] if "D_U_T5EP2" in c["body"] and "hoş geldin" in plain(c["body"]).lower()])

# ====================================================================== B11 kutlamalar
tc = time.time()
bday = f"1990-{TODAY.month:02d}-{TODAY.day:02d}" if not (TODAY.month == 2 and TODAY.day == 29) else "1992-02-29"
sql(f"""INSERT INTO engagement_profiles ("Id","TenantSlug","EmployeeId","BirthDate","ShowBirthday","Skills","Interests","UpdatedAt") VALUES
        ('{uuid.uuid4()}','demo','{TE[1]}','{bday}',true,'{{}}','{{}}',now()), ('{uuid.uuid4()}','demo','{TE[2]}','{bday}',false,'{{}}','{{}}',now())""")
r = cmd(SL, "U_T5EP3", "yıldönümü aç")
check("B11: yıldönümü kutlaması için açık izin", "kutlanacak" in r.get("text", ""), r.get("text"))
run_jobs()
cel = wait_for("/api/chat.postMessage", lambda c: "C_GENEL" in c["body"] and "doğum günü" in plain(c["body"]), since=tc)
ctxt = plain(cel[0]["body"]) if cel else ""
check("B11: yalnızca izin verenin doğum günü, yaş/yıl yok", "T5e1" in ctxt and "T5e2" not in ctxt and "1990" not in ctxt and "yaş" not in ctxt, ctxt[:300])
check("B11: iş yıldönümü (kıdem yılı)", "T5e3" in ctxt and "3. yıl" in ctxt, ctxt[:300])

# ====================================================================== B12 nabız anketi
code, p = api("admin", "POST", f"{G}/chat-admin/pulses", {"question": f"{TAG} Bu hafta enerjiniz nasıl?"})
check("B12: nabız planlandı", code == 200, p)
tpu = time.time()
run_jobs()
users = ["U_AYSE", "U_MEHMET", "U_T5EP1", "U_T5EP2", "U_T5EP3"]
answered = 0
for i, u in enumerate(users):
    d = wait_for("/api/chat.postMessage", lambda c, u=u: f"D_{u}" in c["body"] and TAG in plain(c["body"]) and "pulse" in plain(c["body"]), since=tpu)
    if not d:
        continue
    pb = [b for b in buttons(post_blocks(d[0])) if b.get("action_id", "").startswith("hr360x:pulse:")]
    res = click(SL, u, pb[(i % 5)])
    if "anonim" in res.get("text", ""):
        answered += 1
    if i == 1:
        code, pl = api("admin", "GET", f"{G}/chat-admin/pulses")
        mine = next((x for x in pl if x["id"] == p["id"]), {})
        check("B12: 5'ten az yanıtta sonuç gizli", mine.get("hidden") is True and mine.get("distribution") is None, mine)
check("B12: 5 kişi düğmeyle yanıtladı", answered == 5, answered)
d = wait_for("/api/chat.postMessage", lambda c: "D_U_AYSE" in c["body"] and TAG in plain(c["body"]) and "pulse" in plain(c["body"]), since=tpu)
if d:
    res = click(SL, "U_AYSE", [b for b in buttons(post_blocks(d[0])) if b.get("action_id", "").startswith("hr360x:pulse:")][0])
    check("B12: ikinci yanıt alınmaz", "zaten" in res.get("text", ""), res)
code, pl = api("admin", "GET", f"{G}/chat-admin/pulses")
mine = next((x for x in pl if x["id"] == p["id"]), {})
check("B12: 5 yanıtla dağılım görünür", mine.get("hidden") is False and sum(mine.get("distribution") or []) == 5, mine)
survey = sql(f"""SELECT "SurveyId" FROM governance_chat_pulses WHERE "Id" = '{p['id']}'""")
check("B12: yanıtlar kimliksiz (rastgele anahtar, departman yok)", sql(f"""SELECT count(*) FROM engagement_survey_responses WHERE "SurveyId" = '{survey}' AND "RespondentKey" LIKE 'chat-%' AND "DepartmentName" IS NULL""") == "5"
      and sql(f"""SELECT count(*) FROM information_schema.columns WHERE table_name = 'governance_chat_pulse_answered' AND column_name IN ('Score','SurveyResponseId')""") == "0")

# ====================================================================== B18 birebir hatırlatmaları
to = time.time()
oo = str(uuid.uuid4())
mgr_uid = sql(f"""SELECT "KeycloakUserId" FROM employee_employees WHERE "Id" = '{MEHMET}'""")
sql(f"""INSERT INTO engagement_one_on_ones ("Id","TenantSlug","ManagerUserId","ManagerName","EmployeeId","EmployeeUserId","EmployeeName","ScheduledAt","Status","Agenda","ActionItems","CreatedAt")
        VALUES ('{oo}','demo','{mgr_uid}','Mehmet Demir','{AYSE}','x','Ayşe Yılmaz',now() + interval '40 minutes','Planned',
        '[{{"Text":"GIZLI-GUNDEM-1","Done":false}},{{"Text":"GIZLI-GUNDEM-2","Done":false}}]'::jsonb,'[]'::jsonb,now())""")
run_jobs()
r1 = wait_for("/api/chat.postMessage", lambda c: "D_U_AYSE" in c["body"] and "birebir" in plain(c["body"]), since=to)
r2 = wait_for("/api/chat.postMessage", lambda c: "D_U_MEHMET" in c["body"] and "birebir" in plain(c["body"]), since=to)
txt = (plain(r1[0]["body"]) if r1 else "") + (plain(r2[0]["body"]) if r2 else "")
check("B18: yönetici ve çalışana 1 saat kala hatırlatma, gündem sayısı (içerik yok)", bool(r1 and r2) and "2 madde" in txt and "GIZLI" not in txt, txt[:300])

# ====================================================================== B20 çıkış anketi
tx_ = time.time()
case = str(uuid.uuid4())
sql(f"""INSERT INTO engagement_offboarding_cases ("Id","TenantSlug","EmployeeId","EmployeeName","LastWorkingDay","Reason","Status","Checklist","AssetChecks","CreatedAt")
        VALUES ('{case}','demo','{TE[4]}','T5e4 Test','{TODAY + dt.timedelta(days=5)}','Resignation','Open','[]'::jsonb,'[]'::jsonb,now())""")
run_jobs()
q = wait_for("/api/chat.postMessage", lambda c: "D_U_T5EP4" in c["body"] and "çıkış anketi" in plain(c["body"]), since=tx_)
check("B20: ayrılana gizli çıkış anketi DM'i", bool(q))
if q:
    blocks = post_blocks(q[0])
    for step in range(6):
        opts = [b for b in buttons(blocks) if b.get("action_id", "").startswith("hr360x:exit:")]
        if not opts:
            break
        res = click(SL, "U_T5EP4", opts[1 if step == 0 else -1])
        blocks = res.get("blocks", [])
    check("B20: anket tamamlandı, yanıtlar sohbete yazılmadı", "yalnızca İK" in res.get("text", "") and "Ücret" not in res.get("text", ""), res.get("text"))
    iv = json.loads(sql(f"""SELECT "ExitInterview" FROM engagement_offboarding_cases WHERE "Id" = '{case}'""") or "{}")
    check("B20: yanıtlar ayrılış kaydında (yalnızca İK görür)", iv.get("PrimaryReason") == "Ücret" and iv.get("ManagerScore") == 5 and iv.get("WouldRecommend") is False, iv)
    check("B20: ara yanıtlar silindi", sql(f"""SELECT coalesce("AnswersEnc", 'yok') FROM governance_chat_exit_progress WHERE "CaseId" = '{case}'""") == "yok")

# ====================================================================== B19 + BG16 + BG17 + BG18 asistan
r = cmd(SL, "U_MEHMET", "son 6 ayda departmanlara göre izin günleri")
txt = r.get("text", "")
check("B19: yöneticinin rapor sorusu yanıtlandı (kişi adı yok)", "Ayşe" not in txt and "izin" in txt.lower(), txt[:300])
r = cmd(SL, "U_MEHMET", "peki geçen ay?")
check("BG16: devam sorusu önceki soruyla birleşti", "geçen ay" in r.get("text", "").lower() or "last month" in r.get("text", "").lower(), r.get("text", "")[:300])
ctx = sql(f"""SELECT count(*) FROM governance_chat_context WHERE "EmployeeId" = '{MEHMET}'""")
check("BG16: bağlam şifreli saklanıyor", ctx not in ("", "0") and sql(f"""SELECT count(*) FROM governance_chat_context WHERE "EmployeeId" = '{MEHMET}' AND "TextEnc" LIKE '%izin%'""") == "0", ctx)
r = cmd(SL, "U_MEHMET", "geçmişimi sil")
check("BG16: 'geçmişimi sil' bağlamı siler", "silindi" in r.get("text", "") and sql(f"""SELECT count(*) FROM governance_chat_context WHERE "EmployeeId" = '{MEHMET}'""") == "0", r.get("text"))
code, inv = api("admin", "GET", f"{G}/privacy/inventory")
check("BG16: KVKK envanterinde ChatContext (30 gün)", code == 200 and "ChatContext" in json.dumps(inv, ensure_ascii=False), code)
r = cmd(SL, "U_AYSE", "izin bakiyem ne kadar kaldı acaba")
opens = [b for b in buttons(r.get("blocks")) if b.get("url", "").endswith("/panel/izin")]
check("BG17: asistan yanıtında 'Panelde aç' bağlantı düğmesi", bool(opens) and "Panelde aç" in json.dumps(opens, ensure_ascii=False), r.get("blocks"))
r1 = cmd(SL, "U_AYSE", f"xqzv blorp {RUN} kuantum")
r2 = cmd(SL, "U_AYSE", f"zzyx flarn {RUN} nebula")
hc = btn(r2.get("blocks"), "hrcase_open")
if hc is None and "MOCK-LLM" in (r1.get("text", "") + r2.get("text", "")):
    print("SKIP BG18 doğal tetik: yapay zekâ açık (yanıt her zaman üretiliyor); açık komutla sınanıyor")
    r2 = cmd(SL, "U_AYSE", f"ik vakası zzyx flarn {RUN} nebula")
    hc = btn(r2.get("blocks"), "hrcase_open")
check("BG18: iki kez anlaşılamayınca İK vakası önerisi (otomatik açılmaz)", hc is not None and sql(f"""SELECT count(*) FROM expense_hr_cases WHERE "Description" LIKE '%{RUN} nebula%'""") == "0", r2.get("text"))
if hc:
    res = click(SL, "U_AYSE", hc)
    check("BG18: onaylayınca İK vakası açıldı", "açıldı" in res.get("text", "") and sql(f"""SELECT "Status" FROM expense_hr_cases WHERE "EmployeeId" = '{AYSE}' AND "Description" LIKE '%{RUN} nebula%'""") == "Open", res)

# ====================================================================== BG15 ölçümler
out = subprocess.run(["docker", "exec", "hr360-gateway-1", "wget", "-qO-", "http://governance-service:8080/metrics"], capture_output=True, text=True).stdout
chat_lines = [l for l in out.splitlines() if l.startswith("hr360_chat_")]
check("BG15: Prometheus ölçümleri (sağlayıcı/niyet/sonuç, süre, kuyruk)", any("hr360_chat_commands_total{" in l and 'provider="slack"' in l for l in chat_lines)
      and any(l.startswith("hr360_chat_command_duration_seconds_bucket") for l in chat_lines) and any(l.startswith("hr360_chat_outbox_depth") for l in chat_lines), chat_lines[:5])
check("BG15: etiketlerde kullanıcı kimliği ya da metin yok", not any(x in "\n".join(chat_lines) for x in ("U_AYSE", "ayse", "Mehmet", TAG, "bakiye")), [l for l in chat_lines if "ayse" in l.lower()][:3])

# ====================================================================== TEAMS
APP = str(uuid.uuid4())
MSTENANT = str(uuid.uuid4())
SVC = "http://chatmock:8000/teams/"
code, r = api("admin", "POST", f"{G}/chat-apps", {"platform": "Teams", "name": "HR360 Teams", "isEnabled": True, "notifyApprovals": False, "notifyRequesters": False,
                                                   "teamsAppId": APP, "teamsAppPassword": "s3cret", "teamsAzureTenantId": MSTENANT})
TM = r["id"]


def teams_post(activity):
    q = urllib.parse.urlencode({"aud": APP, "serviceurl": SVC})
    token = mock(f"/_jwt?{q}")["token"]
    return http("POST", f"{G}/chat/teams/{TM}/messages", activity, {"Authorization": "Bearer " + token})


def act(user, text=None, value=None, typ="message", reply_to=None, attachments=None):
    a = {"type": typ, "id": "in-" + uuid.uuid4().hex[:6], "serviceUrl": SVC, "channelId": "msteams",
         "from": {"id": f"29:{user}", "name": user}, "recipient": {"id": f"28:{APP}", "name": "HR360"},
         "conversation": {"id": f"a:conv-{user}", "conversationType": "personal", "tenantId": MSTENANT}}
    if text is not None:
        a["text"] = text
    if value is not None:
        a["value"] = value
    if reply_to:
        a["replyToId"] = reply_to
    if attachments:
        a["attachments"] = attachments
    if typ == "conversationUpdate":
        a["membersAdded"] = [{"id": f"28:{APP}"}]
    return a


tt = time.time()
for u, who in (("mehmet.demir", "mehmet"), ("ayse.yilmaz", "ayse")):
    teams_post(act(u, typ="conversationUpdate"))
    w = wait_for(f"/teams/v3/conversations/a:conv-{u}/activities", since=tt)
    c = link_code(w[0]["body"]) if w else None
    api(who, "GET", f"{G}/chat/link/{c}")
    code, _ = api(who, "POST", f"{G}/chat/link", {"code": c})
    check(f"Teams: {who} bağlandı", code == 200, code)

CONV_A = "/teams/v3/conversations/a:conv-ayse.yilmaz/activities"
t1 = time.time()
teams_post(act("ayse.yilmaz", "yardim"))
h = wait_for(CONV_A, lambda c: '"hr360": "x"' in c["body"] or '"hr360":"x"' in c["body"], t1)
hb = json.loads(h[0]["body"]) if h else {}
subs = [a for a in hb.get("attachments", [{}])[0].get("content", {}).get("actions", []) if a.get("type") == "Action.Submit"]
check("Teams BG7: yardım kartında komut düğmeleri", len(subs) >= 5, hb)
t2 = time.time()
teams_post(act("ayse.yilmaz", f"teşekkür @mehmet.demir {TAG} teams"))
kt = wait_for("/teams/v3/conversations/a:conv-mehmet.demir/activities", lambda c: TAG in c["body"] and "teams" in c["body"], t2)
check("Teams B10: teşekkür alıcıya Teams DM'i olarak gitti", bool(kt))

t3 = time.time()
teams_post(act("ayse.yilmaz", "masa yarın"))
dk = wait_for(CONV_A, lambda c: "desk_book" in c["body"] or "masanız var" in c["body"], t3)
if dk and "masanız var" in dk[0]["body"]:
    sql(f"""DELETE FROM engagement_desk_bookings WHERE "EmployeeId" = '{AYSE}' AND "Date" = '{day}'""")
    t3 = time.time()
    teams_post(act("ayse.yilmaz", "masa yarın"))
    dk = wait_for(CONV_A, lambda c: "desk_book" in c["body"], t3)
if dk:
    body = json.loads(dk[0]["body"])
    data = next(a["data"] for a in body["attachments"][0]["content"]["actions"] if a.get("data", {}).get("a") == "desk_book")
    t4 = time.time()
    teams_post(act("ayse.yilmaz", value=data, reply_to="act-desk-1"))
    up = wait_for(CONV_A + "/act-desk-1", lambda c: c["method"] == "PUT" and "ayrıldı" in json.loads(c["body"]).get("summary", ""), t4)
    check("Teams BG10: düğme sonrası kart güncellendi (eski düğmeler kalktı)", bool(up) and "desk_book" not in up[0]["body"], up)
    sql(f"""DELETE FROM engagement_desk_bookings WHERE "EmployeeId" = '{AYSE}' AND "Date" = '{day}'""")

t5 = time.time()
teams_post(act("ayse.yilmaz", attachments=[{"contentType": "image/png", "contentUrl": "http://chatmock:8000/teamsfiles/receipt.png", "name": "fis.png"}]))
dl = wait_for("/teamsfiles/receipt.png", since=t5, timeout=30)
check("Teams B6: satır içi görüntü bot jetonuyla indirildi", bool(dl) and dl[0]["auth"] == "Bearer teams-bot-token", dl)
ec = wait_for(CONV_A, lambda c: "Input.Text" in c["body"] and "amount" in c["body"], t5, timeout=90)
check("Teams B6: düzenlenebilir fiş kartı (tutar dolu)", bool(ec) and "245.50" in ec[0]["body"], ec[0]["body"][:400] if ec else None)
if ec:
    body = json.loads(ec[0]["body"])
    data = next(a["data"] for a in body["attachments"][0]["content"]["actions"] if a.get("data", {}).get("hr360") == "expense")
    t6 = time.time()
    teams_post(act("ayse.yilmaz", value={**data, "amount": "77,70", "date": "2026-10-01", "category": "Meal"}, reply_to="act-exp-1"))
    ok = wait_for(CONV_A + "/act-exp-1", lambda c: c["method"] == "PUT" and "Taslak masraf" in c["body"], t6)
    check("Teams B6: karttan taslak masraf (kart güncellendi)", bool(ok) and sql(f"""SELECT count(*) FROM expense_claims WHERE "EmployeeId" = '{AYSE}' AND "TotalAmount" = 77.70 AND "CreatedAt" > now() - interval '5 minutes'""") == "1", ok)

# ====================================================================== MATTERMOST (kendi sunucunuz)
code, r = api("admin", "POST", f"{G}/chat-apps", {"platform": "Mattermost", "name": "MM bulut", "isEnabled": True, "notifyApprovals": True, "notifyRequesters": True,
                                                   "serverUrl": "https://acme.cloud.mattermost.com", "botToken": "mm-bot-token", "incomingToken": "mm-incoming"})
check("B7: Mattermost bulut adresi yurt dışı aktarım kaydı olmadan reddedilir", code == 400 and "KVKK" in json.dumps(r, ensure_ascii=False), r)
code, r = api("admin", "POST", f"{G}/chat-apps", {"platform": "Mattermost", "name": "MM", "isEnabled": True, "notifyApprovals": True, "notifyRequesters": True,
                                                   "serverUrl": "http://chatmock:8000/mm", "botToken": "wrong", "incomingToken": "mm-incoming"})
check("B7: Mattermost yanlış bot jetonu reddedilir", code == 400, r)
code, r = api("admin", "POST", f"{G}/chat-apps", {"platform": "Mattermost", "name": "MM", "isEnabled": True, "notifyApprovals": True, "notifyRequesters": True,
                                                   "serverUrl": "http://chatmock:8000/mm", "botToken": "mm-bot-token", "incomingToken": "mm-incoming"})
check("B7: kendi sunucunuzdaki Mattermost aktarım kaydı istemez", code == 200 and r.get("selfHosted") is True and r.get("botUserId") == "mmbot"
      and "mm-bot-token" not in json.dumps(r), r)
MM = r.get("id")


def mm(kind, form):
    return http("POST", f"{G}/chat/mattermost/{MM}/{kind}", form=form)


code, _ = mm("commands", {"token": "yanlis", "user_id": "mm_ayse.yilmaz", "text": "bakiye"})
check("B7: Mattermost yanlış jeton 401", code == 401, code)
for uid, who in (("mm_ayse.yilmaz", "ayse"), ("mm_mehmet.demir", "mehmet")):
    code, r = mm("commands", {"token": "mm-incoming", "user_id": uid, "text": "ben"})
    c = link_code(r.get("text") if r else "")
    api(who, "GET", f"{G}/chat/link/{c}")
    code, _ = api(who, "POST", f"{G}/chat/link", {"code": c})
    check(f"B7: Mattermost {who} bağlandı", code == 200, code)
code, r = mm("commands", {"token": "mm-incoming", "user_id": "mm_ayse.yilmaz", "text": "bakiye"})
check("B7: Mattermost slash komutu (yalnızca kişiye)", code == 200 and r.get("response_type") == "ephemeral" and "bakiye" in r.get("text", ""), r)
code, r = mm("commands", {"token": "mm-incoming", "user_id": "mm_ayse.yilmaz", "text": "masa yarın"})
acts = [a for att in (r or {}).get("props", {}).get("attachments", []) for a in att.get("actions", [])]
book = next((a for a in acts if a["integration"]["context"]["a"] == "desk_book"), None)
check("B7: Mattermost etkileşimli düğmeler (imzalı bağlam)", book is not None and book["integration"]["url"].endswith(f"/chat/mattermost/{MM}/actions"), r)
if book:
    ctx_ = dict(book["integration"]["context"])
    code, bad = http("POST", f"{G}/chat/mattermost/{MM}/actions", {"user_id": "mm_ayse.yilmaz", "context": {**ctx_, "v": ctx_["v"] + "x"}})
    check("B7: Mattermost bozulmuş düğme bağlamı 401", code == 401, code)
    code, res = http("POST", f"{G}/chat/mattermost/{MM}/actions", {"user_id": "mm_ayse.yilmaz", "channel_id": "mmdm_mm_ayse.yilmaz", "context": ctx_})
    check("B7: Mattermost düğmesi masayı ayırdı, ileti güncellendi", code == 200 and "ayrıldı" in res.get("update", {}).get("message", ""), res)
    sql(f"""DELETE FROM engagement_desk_bookings WHERE "EmployeeId" = '{AYSE}' AND "Date" = '{day}'""")
tw = time.time()
code, r = mm("webhook", {"token": "mm-incoming", "user_id": "mm_ayse.yilmaz", "channel_id": "town-square", "text": "hr360 bakiye", "trigger_word": "hr360"})
dmp = wait_for("/mm/api/v4/posts", lambda c: "mmdm_mm_ayse.yilmaz" in c["body"] and "bakiye" in c["body"], tw)
check("B7: Mattermost giden webhook: kanala kişisel veri yok, yanıt DM'den", code == 200 and "özel mesajla" in (r or {}).get("text", "") and bool(dmp), (r, dmp))
# onay kartı + karar
tm = time.time()
wf_mm = None
for extra in range(0, 120, 7):
    start = (dt.date(2029, 11, 5) + dt.timedelta(weeks=RUN_WEEK, days=extra)).isoformat()
    code, lr = api("ayse", "POST", "/api/leave/leave-requests", {"employeeId": AYSE, "type": "Unpaid", "startDate": start, "endDate": start, "days": 0, "reason": TAG})
    if code in (200, 201):
        wf_mm = lr["workflowRequestId"]
        break
mmcard = wait_for("/mm/api/v4/posts", lambda c: "mmdm_mm_mehmet.demir" in c["body"] and wf_mm and wf_mm in c["body"], tm)
check("B7: Mattermost onay kartı (Onayla/Reddet)", bool(mmcard) and "decide" in mmcard[0]["body"] and "Yılmaz" not in mmcard[0]["body"], mmcard[:1])
if mmcard:
    post = json.loads(mmcard[0]["body"])
    dec = next(a for a in post["props"]["attachments"][0]["actions"] if a["integration"]["context"]["v"].startswith(f"{wf_mm}|") and a["integration"]["context"]["v"].split("~")[0].endswith("|a"))
    code, res = http("POST", f"{G}/chat/mattermost/{MM}/actions", {"user_id": "mm_mehmet.demir", "channel_id": "mmdm_mm_mehmet.demir", "context": dec["integration"]["context"]})
    time.sleep(3)
    check("B7: Mattermost'tan onaylandı", code == 200 and api("ayse", "GET", f"/api/workflow/workflows/{wf_mm}")[1]["status"] == "Approved", res)

# ====================================================================== ROCKET.CHAT (kendi sunucunuz)
code, r = api("admin", "POST", f"{G}/chat-apps", {"platform": "RocketChat", "name": "RC bulut", "isEnabled": True, "notifyApprovals": False, "notifyRequesters": False,
                                                   "serverUrl": "https://acme.rocket.chat", "botToken": "rc-token", "botUserId": "rcbot", "incomingToken": "rc-incoming"})
check("B7: Rocket.Chat bulut adresi yurt dışı aktarım kaydı olmadan reddedilir", code == 400 and "KVKK" in json.dumps(r, ensure_ascii=False), r)
code, r = api("admin", "POST", f"{G}/chat-apps", {"platform": "RocketChat", "name": "RC", "isEnabled": True, "notifyApprovals": False, "notifyRequesters": False,
                                                   "serverUrl": "http://chatmock:8000/rc", "botToken": "rc-token", "botUserId": "rcbot", "incomingToken": "rc-incoming"})
check("B7: kendi sunucunuzdaki Rocket.Chat eklendi", code == 200 and r.get("selfHosted") is True, r)
RC = r.get("id")
DM_ROOM = "rcbotrc_ayse.yilmaz"


def rc(text, room=DM_ROOM, token="rc-incoming", user="rc_ayse.yilmaz"):
    return http("POST", f"{G}/chat/rocketchat/{RC}/webhook", {"token": token, "user_id": user, "user_name": user[3:], "channel_id": room, "text": text})


code, _ = rc("bakiye", token="yanlis")
check("B7: Rocket.Chat yanlış jeton 401", code == 401, code)
code, r = rc("ben")
c = link_code((r or {}).get("text"))
api("ayse", "GET", f"{G}/chat/link/{c}")
code2, _ = api("ayse", "POST", f"{G}/chat/link", {"code": c})
check("B7: Rocket.Chat ayse bağlandı", code2 == 200, (code, r))
code, r = rc("bakiye")
check("B7: Rocket.Chat DM'de komut yanıtı", code == 200 and "bakiye" in (r or {}).get("text", ""), r)
code, r = rc("masa yarın")
acts = [a for att in (r or {}).get("attachments", []) for a in att.get("actions", [])]
book = next((a for a in acts if a.get("msg", "").startswith("hr360x desk_book ")), None)
check("B7: Rocket.Chat düğmeleri (sohbet penceresine eylem yazar)", book is not None and book.get("msg_in_chat_window") is True, r)
if book:
    code, res = rc(book["msg"])
    check("B7: Rocket.Chat düğmesi masayı ayırdı", code == 200 and "ayrıldı" in (res or {}).get("text", ""), res)
    sql(f"""DELETE FROM engagement_desk_bookings WHERE "EmployeeId" = '{AYSE}' AND "Date" = '{day}'""")
trc = time.time()
code, r = rc("bakiye", room="GENERAL")
pm = wait_for("/rc/api/v1/chat.postMessage", lambda c: DM_ROOM in c["body"] and "bakiye" in c["body"], trc)
check("B7: Rocket.Chat genel kanalda kişisel yanıt yok, DM'den gönderildi", code == 200 and "özel mesajla" in (r or {}).get("text", "") and bool(pm), (r, pm))
code, inv = api("admin", "GET", f"{G}/privacy/transfers")
check("B7: KVKK sağlayıcı listesinde Mattermost/Rocket.Chat bulut", code == 200 and "mattermost" in json.dumps(inv) and "rocketchat" in json.dumps(inv), code)

# ====================================================================== temizlik
if wf_old:
    api("ayse", "POST", f"/api/leave/leave-requests/{old_leave}/cancel")
for a in (SL, TM, MM, RC):
    if a:
        api("admin", "DELETE", f"{G}/chat-apps/{a}")
ids = ",".join(f"'{v}'" for v in TE.values())
sql(f"""DELETE FROM governance_chat_identities WHERE "EmployeeId" IN ({ids}) OR "ExternalUserId" IN ('U_RATE5E')""")
sql(f"""DELETE FROM engagement_kudos WHERE "Message" LIKE '{TAG}%'""")
sql(f"""DELETE FROM notification_messages WHERE "RecipientEmployeeId" IN ({ids}) OR "Body" LIKE '{TAG}%'""")
sql(f"""DELETE FROM timeshift_clock_punches WHERE "EmployeeId" IN ({ids})""")
sql(f"""DELETE FROM timeshift_time_entries WHERE "EmployeeId" IN ({ids})""")
sql(f"""DELETE FROM timeshift_swap_requests WHERE "Id" = '{swap}'""")
sql(f"""DELETE FROM timeshift_assignments WHERE "ShiftId" = '{sh}'""")
sql(f"""DELETE FROM timeshift_shifts WHERE "Id" = '{sh}'""")
for w in [pwf] + wfs:
    sql(f"""DELETE FROM workflow_approval_steps WHERE "WorkflowRequestId" = '{w}'""")
    sql(f"""DELETE FROM workflow_requests WHERE "Id" = '{w}'""")
sql(f"""DELETE FROM expense_claims WHERE "EmployeeId" = '{AYSE}' AND "Status" = 'Draft' AND "CreatedAt" > now() - interval '1 hour'
        AND "Id" IN (SELECT "ClaimId" FROM expense_items WHERE "Description" = 'Sohbetten (fiş okuma)')""")
sql(f"""DELETE FROM governance_acknowledgements WHERE "SubjectId" = '{ann_id}'""") if ann_id else None
sql(f"""DELETE FROM governance_announcements WHERE "Title" = '{TAG} Duyuru'""")
sql(f"""DELETE FROM governance_document_requests WHERE "TemplateId" = '{tpl}'""")
sql(f"""DELETE FROM governance_doc_templates WHERE "Id" = '{tpl}'""")
sql(f"""DELETE FROM onboarding_tasks WHERE "PlanId" IN ('{plan0}','{plan1}')""")
sql(f"""DELETE FROM onboarding_plans WHERE "Id" IN ('{plan0}','{plan1}')""")
sql(f"""DELETE FROM engagement_profiles WHERE "EmployeeId" IN ({ids})""")
sql(f"""DELETE FROM governance_chat_optins WHERE "EmployeeId" IN ({ids})""")
if survey:
    sql(f"""DELETE FROM engagement_survey_responses WHERE "SurveyId" = '{survey}'""")
    sql(f"""DELETE FROM governance_chat_pulse_answered WHERE "PulseId" = '{p['id']}'""")
    sql(f"""DELETE FROM governance_chat_pulses WHERE "Id" = '{p['id']}'""")
    sql(f"""DELETE FROM engagement_surveys WHERE "Id" = '{survey}'""")
sql(f"""DELETE FROM engagement_one_on_ones WHERE "Id" = '{oo}'""")
sql(f"""DELETE FROM governance_chat_exit_progress WHERE "CaseId" = '{case}'""")
sql(f"""DELETE FROM engagement_offboarding_cases WHERE "Id" = '{case}'""")
sql(f"""DELETE FROM expense_hr_cases WHERE "Description" LIKE '%{RUN} nebula%'""")
sql(f"""DELETE FROM governance_chat_pending WHERE "EmployeeId" IN ({ids}, '{AYSE}', '{MEHMET}')""")
sql(f"""DELETE FROM employee_assignments WHERE "EmployeeId" IN ({ids})""")
sql(f"""DELETE FROM employee_employees WHERE "Id" IN ({ids})""")

print(f"\nFAILS: {len(FAIL)}")
for f in FAIL:
    print("  -", f)
sys.exit(1 if FAIL else 0)

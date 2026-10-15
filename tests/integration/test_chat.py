"""Slack uygulaması ve Microsoft Teams botu uçtan uca testi (sahte Slack/Teams ile).

Ön koşullar:
  * HR360 çalışıyor; governance-service deploy/testing/chat-mock.yml ile başlatıldı
  * chatmock konteyneri hr360-net'te (bkz. chatmock.py)
  * tests/credentials.json ya da HR360_TEST_USERS (jetonlar tarayıcıyla otomatik alınır)
Çalıştırma: python3 tests/integration/test_chat.py
"""

import hashlib
import hmac
import json
import sys
import time
import urllib.parse
import urllib.request
import uuid

from common import ensure_transfers, AYSE, BASE, FAIL, api, check, http, mock, mock_calls, tok, wait_for  # noqa: E402

G = "/api/governance"
SIGNING = "slack-signing-secret"
ensure_transfers("slack", "microsoft")



def plain(body):
    """Slack form gövdesi: URL kodu + JSON \\uXXXX kaçışları çözülmüş düz metin."""
    t = urllib.parse.unquote_plus(body)
    try:
        return t.encode("latin-1", "backslashreplace").decode("unicode_escape").encode("utf-16", "surrogatepass").decode("utf-16")
    except Exception:
        return t

def slack_post(app_id, kind, form=None, raw=None, secret=SIGNING, ts=None):
    body = raw if raw is not None else urllib.parse.urlencode(form)
    ts = str(ts or int(time.time()))
    sig = "v0=" + hmac.new(secret.encode(), f"v0:{ts}:{body}".encode(), hashlib.sha256).hexdigest()
    ctype = "application/json" if raw is not None else "application/x-www-form-urlencoded"
    return http("POST", f"{G}/chat/slack/{app_id}/{kind}", raw_body=body,
                headers={"X-Slack-Request-Timestamp": ts, "X-Slack-Signature": sig, "Content-Type": ctype})


RUN_WEEK = __import__("random").randint(0, 150)


def new_leave(day, reason):
    # Ücretsiz izin (bakiye kontrolü yok). Tarihler her çalıştırmada farklı bir haftaya
    # düşer ki önceki çalıştırmaların izinleriyle çakışmasın (Pzt + day%5 gün).
    import datetime as _dt
    # Önceki çalıştırmalardan aynı güne denk gelen izin varsa sonraki haftalar denenir.
    for extra in range(0, 40, 7):
        start = (_dt.date(2028, 1, 3) + _dt.timedelta(weeks=RUN_WEEK + extra, days=day % 5)).isoformat()
        code, r = api("ayse", "POST", "/api/leave/leave-requests",
                      {"employeeId": AYSE, "type": "Unpaid", "startDate": start, "endDate": start, "days": 0, "reason": reason})
        if code not in (400, 409) or "çakışan" not in json.dumps(r, ensure_ascii=False):
            break
    check(f"izin talebi oluşturuldu ({reason})", code in (200, 201), r)
    return r["workflowRequestId"]


def wf_status(wf):
    return api("ayse", "GET", f"/api/workflow/workflows/{wf}")[1]


def link_code(text):
    import re
    m = re.search(r"/panel/sohbet-bagla\?kod=([A-Za-z0-9_-]+)", urllib.parse.unquote_plus(text or ""))
    return m.group(1) if m else None


def confirm_link(code, who):
    c1, prev = api(who, "GET", f"{G}/chat/link/{code}")
    c2, res = api(who, "POST", f"{G}/chat/link", {"code": code})
    return c1, prev, c2, res


# ====================================================================== SLACK
mock("/_reset", "POST")
code, apps = api("admin", "GET", f"{G}/chat-apps")
for a in apps or []:
    api("admin", "DELETE", f"{G}/chat-apps/{a['id']}")

code, r = api("admin", "POST", f"{G}/chat-apps", {"platform": "Slack", "name": "HR360 Slack", "isEnabled": True, "notifyApprovals": True,
                                                   "notifyRequesters": True, "slackBotToken": "xoxb-wrong", "slackSigningSecret": SIGNING})
check("Slack: geçersiz jeton reddedilir", code == 400 and "invalid_auth" in json.dumps(r, ensure_ascii=False), r)
code, r = api("admin", "POST", f"{G}/chat-apps", {"platform": "Slack", "name": "HR360 Slack", "isEnabled": True, "notifyApprovals": True,
                                                   "notifyRequesters": True, "slackBotToken": "xoxb-test-token", "slackSigningSecret": SIGNING})
check("Slack uygulaması eklendi (auth.test)", code == 200 and r.get("slackTeamName") == "Demo Workspace", r)
slack_id = r["id"]
check("yanıtta sır yok", "xoxb" not in json.dumps(r) and SIGNING not in json.dumps(r), r)
code, man = api("admin", "GET", f"{G}/chat-apps/{slack_id}/slack-manifest")
check("Slack manifesti", code == 200 and man["settings"]["interactivity"]["request_url"].endswith(f"/chat/slack/{slack_id}/interactions"), man)

# imza
code, _ = slack_post(slack_id, "commands", {"user_id": "U_AYSE", "text": "bakiye"}, secret="yanlis")
check("Slack: yanlış imza 401", code == 401, code)
code, _ = slack_post(slack_id, "commands", {"user_id": "U_AYSE", "text": "bakiye"}, ts=int(time.time()) - 900)
check("Slack: eski zaman damgası 401", code == 401, code)

# hesap doğrulama: e-posta eşleşmesi tek başına yetmez, kişi HR360'ta onaylar
code, r = slack_post(slack_id, "commands", {"user_id": "U_AYSE", "text": "bakiye"})
ayse_code = link_code(r.get("text"))
check("Slack: doğrulanmamış hesaba veri yok, bağlama bağlantısı var", code == 200 and ayse_code and "izin bakiyeniz" not in r["text"], r)
c1, prev, c2, res = confirm_link(ayse_code, "mehmet")
check("Slack: başkasının bağlantısı e-posta uyuşmazlığıyla reddedilir", c1 == 200 and prev["emailMatches"] is False and c2 == 409 and res.get("code") == "email_mismatch", (c1, prev, c2, res))
c1, prev, c2, res = confirm_link(ayse_code, "ayse")
check("Slack: Ayşe hesabını bağladı", c1 == 200 and prev["emailMatches"] and prev["platform"] == "Slack" and c2 == 200, (c1, prev, c2, res))
c1, _, c2, _ = confirm_link(ayse_code, "ayse")
check("Slack: bağlama kodu tek kullanımlık", c1 == 404 and c2 == 404, (c1, c2))
code, r = slack_post(slack_id, "commands", {"user_id": "U_MEHMET", "text": "ben"})
c1, prev, c2, res = confirm_link(link_code(r.get("text")), "mehmet")
check("Slack: Mehmet hesabını bağladı", c2 == 200, (c1, prev, c2, res))
code, mine = api("ayse", "GET", f"{G}/chat/link/mine")
check("Profil: bağlı sohbet hesabı listelenir", code == 200 and any(i["platform"] == "Slack" and i["verifiedAt"] for i in mine), mine)

# komutlar
code, r = slack_post(slack_id, "commands", {"user_id": "U_AYSE", "text": "bakiye"})
check("Slack /hr360 bakiye", code == 200 and "izin bakiyeniz" in r["text"], r)
code, r = slack_post(slack_id, "commands", {"user_id": "U_AYSE", "text": "ben"})
check("Slack /hr360 ben (eşleşme)", code == 200 and "Ayşe" in r["text"] and "bağlı" in r["text"], r)
code, r = slack_post(slack_id, "commands", {"user_id": "U_AYSE", "text": "izindekiler"})
check("Slack: ekibi olmayana izindekiler yalnızca sayı", code == 200 and ("kişi izinde" in r["text"] or "kimse yok" in r["text"]) and "'e kadar" not in r["text"], r)
code, r = slack_post(slack_id, "commands", {"user_id": "U_BILINMEYEN", "text": "bakiye"})
check("Slack: eşleşmeyen kullanıcı uyarılır", code == 200 and "eşleşmedi" in r["text"], r)
code, r = slack_post(slack_id, "commands", {"user_id": "U_AYSE", "text": "saçma"})
check("Slack: bilinmeyen komut yardım gösterir", code == 200 and "Komutlar" in r["text"], r)
code, r = slack_post(slack_id, "events", raw=json.dumps({"type": "url_verification", "challenge": "abc123"}))
check("Slack Events URL doğrulaması", code == 200 and r.get("challenge") == "abc123", r)

# bağı kaldırılan onaycıya talep içeriği değil, yalnızca bağlama bağlantısı gider
code, ids = api("admin", "GET", f"{G}/chat-apps/{slack_id}/identities")
mehmet_ident = next(i for i in ids if i.get("employeeName") == "Mehmet Demir")
code, _ = api("admin", "POST", f"{G}/chat-apps/{slack_id}/identities/{mehmet_ident['id']}/revoke")
check("Yönetici bağı kaldırabilir", code == 204, code)
tp = time.time()
wf_prompt = new_leave(11, "slack-baglama-istemi")
prompt = wait_for("/api/chat.postMessage", lambda c: "D_U_MEHMET" in c["body"] and "sohbet-bagla" in urllib.parse.unquote_plus(c["body"]), tp)
body_txt = urllib.parse.unquote_plus(prompt[0]["body"]) if prompt else ""
check("Doğrulanmamış onaycıya yalnızca bağlama bağlantısı", bool(prompt) and "hr360_approve" not in body_txt and wf_prompt not in body_txt, body_txt[:300])
c1, prev, c2, res = confirm_link(link_code(body_txt), "mehmet")
check("Mehmet yeniden bağladı", c2 == 200, (c2, res))

# onay akışı
t0 = time.time()
wf = new_leave(12, "slack-onay-testi")
posts = wait_for("/api/chat.postMessage", lambda c: "D_U_MEHMET" in c["body"] and wf in urllib.parse.unquote_plus(c["body"]), t0)
check("Slack: onaycıya düğmeli DM gitti", bool(posts), mock_calls("/api/", t0))
if posts:
    msg = urllib.parse.parse_qs(posts[0]["body"])
    blocks = json.loads(msg["blocks"][0])
    text_all = json.dumps(blocks, ensure_ascii=False)
    check("KVKK: mesajda soyadı kısaltılmış, talep konusu yok", "Ayşe Y." in text_all and "Yılmaz" not in text_all and "slack-onay-testi" not in text_all, text_all[:400])
    acts = [e for b in blocks if b["type"] == "actions" for e in b["elements"]]
    check("Slack: Onayla/Reddet/HR360'ta aç düğmeleri", {e.get("action_id") for e in acts} >= {"hr360_approve", "hr360_reject", "hr360_open"}, acts)
    value = next(e["value"] for e in acts if e["action_id"] == "hr360_approve")
    ts = msg.get("ts", ["1700000000.000001"])[0]

    # yanlış kişi (Ayşe kendi talebine basar) -> reddedilir
    t1 = time.time()
    payload = {"type": "block_actions", "user": {"id": "U_AYSE"}, "actions": [{"action_id": "hr360_approve", "value": value}],
               "response_url": "http://chatmock:8000/slack-response/1", "channel": {"id": "D_U_MEHMET"}}
    code, _ = slack_post(slack_id, "interactions", {"payload": json.dumps(payload)})
    check("Slack: etkileşim 200", code == 200, code)
    resp = wait_for("/slack-response/1", since=t1)
    check("Slack: kendi talebini onaylayamaz (uyarı)", bool(resp) and "onaylayamazsınız" in json.loads(resp[0]["body"])["text"], resp)
    check("Slack: talep hâlâ bekliyor", wf_status(wf)["status"] == "Pending")

    # doğru kişi onaylar
    t2 = time.time()
    payload["user"]["id"] = "U_MEHMET"
    payload["response_url"] = "http://chatmock:8000/slack-response/2"
    code, _ = slack_post(slack_id, "interactions", {"payload": json.dumps(payload)})
    upd = wait_for("/api/chat.update", lambda c: "Onayladınız" in urllib.parse.unquote_plus(c["body"]), t2)
    check("Slack: mesaj 'Onayladınız' olarak güncellendi (düğmeler kalktı)", bool(upd) and "hr360_approve" not in urllib.parse.unquote_plus(upd[0]["body"]), upd)
    time.sleep(2)
    st = wf_status(wf)
    check("Slack: iş akışı onaylandı", st["status"] == "Approved", st.get("status"))
    check("Slack: karar notu kanalı belirtir", any("Slack" in (s.get("comment") or "") for s in st["steps"]), st["steps"])
    req_dm = wait_for("/api/chat.postMessage", lambda c: "D_U_AYSE" in c["body"] and "onaylandı" in urllib.parse.unquote_plus(c["body"]), t2)
    check("Slack: talep sahibine 'onaylandı' DM'i", bool(req_dm), mock_calls("chat.postMessage", t2))

    # ikinci tıklama -> zaten karara bağlanmış
    t3 = time.time()
    payload["response_url"] = "http://chatmock:8000/slack-response/3"
    slack_post(slack_id, "interactions", {"payload": json.dumps(payload)})
    resp = wait_for("/slack-response/3", since=t3)
    check("Slack: tekrar tıklama 'zaten karara bağlanmış'", bool(resp) and "karara" in json.loads(resp[0]["body"])["text"], resp)

# onaylarım komutu düğmeli liste verir; buradan reddedilir
wf2 = new_leave(13, "slack-komuttan-red")
time.sleep(4)
code, r = slack_post(slack_id, "commands", {"user_id": "U_MEHMET", "text": "onaylarım"})
btns = [e for b in (r or {}).get("blocks", []) if b.get("type") == "actions" for e in b["elements"] if e.get("action_id") == "hr360_reject"]
check("Slack /hr360 onaylarım düğmeli liste", code == 200 and any(wf2 in e["value"] for e in btns), r)
val = next((e["value"] for e in btns if wf2 in e["value"]), None)
if val:
    t4 = time.time()
    payload = {"type": "block_actions", "user": {"id": "U_MEHMET"}, "actions": [{"action_id": "hr360_reject", "value": val}],
               "response_url": "http://chatmock:8000/slack-response/4"}
    slack_post(slack_id, "interactions", {"payload": json.dumps(payload)})
    time.sleep(4)
    check("Slack: komuttan reddedildi", wf_status(wf2)["status"] == "Rejected")
    rej = wait_for("/api/chat.postMessage", lambda c: "D_U_AYSE" in c["body"] and "reddedildi" in urllib.parse.unquote_plus(c["body"]), t4)
    check("Slack: talep sahibine 'reddedildi' DM'i", bool(rej))

# ====================================================================== SLACK: dil, doğal dil, form, gerekçe, ana sayfa, özet
def free_day(offset):
    import datetime as _dt
    return (_dt.date(2029, 1, 1) + _dt.timedelta(weeks=RUN_WEEK, days=offset)).isoformat()


def pref(who, lang):
    return api(who, "PUT", "/api/notification/notifications/preferences/me", {"language": lang})


code, r = slack_post(slack_id, "commands", {"user_id": "U_AYSE", "text": "sonraki resmi tatil ne zaman?"})
check("Slack: doğal dil sorusu asistana gider", code == 200 and "tatil" in r["text"].lower() and "tanımadım" not in r["text"], r)
pref("ayse", "en")
code, r = slack_post(slack_id, "commands", {"user_id": "U_AYSE", "text": "balance"})
check("Slack: İngilizce tercihte İngilizce yanıt", code == 200 and "leave balance" in r["text"] and "bakiye" not in r["text"], r)
code, r = slack_post(slack_id, "commands", {"user_id": "U_AYSE", "text": "help"})
check("Slack: İngilizce komut listesi", code == 200 and "request leave" in r["text"], r)
pref("ayse", "tr")
code, r = slack_post(slack_id, "commands", {"user_id": "U_AYSE", "text": "özet"})
check("Slack: özet komutu", code == 200 and "Özetiniz" in r["text"], r)

# izin formu: düğme -> pencere -> gönder
code, r = slack_post(slack_id, "commands", {"user_id": "U_AYSE", "text": "izin al"})
form_btn = [e for b in (r or {}).get("blocks", []) if b.get("type") == "actions" for e in b["elements"] if e.get("action_id") == "hr360_leave_form"]
check("Slack: 'izin al' form düğmesi verir", code == 200 and bool(form_btn), r)
tf = time.time()
slack_post(slack_id, "interactions", {"payload": json.dumps({"type": "block_actions", "user": {"id": "U_AYSE"}, "trigger_id": "TRIG1",
                                                             "actions": [{"action_id": "hr360_leave_form", "value": "leave"}]})})
opened = wait_for("/api/views.open", lambda c: "TRIG1" in c["body"], tf)
modal = json.loads(urllib.parse.parse_qs(opened[0]["body"])["view"][0]) if opened else {}
check("Slack: izin penceresi açıldı (bakiye ve sağlık uyarısı)", modal.get("callback_id") == "hr360_leave"
      and "Sağlık bilgisi" in json.dumps(modal, ensure_ascii=False), modal.get("callback_id"))


def leave_submit(start, end, reason):
    view = {"callback_id": "hr360_leave", "state": {"values": {
        "type": {"v": {"selected_option": {"value": "Unpaid"}}}, "start": {"v": {"selected_date": start}},
        "end": {"v": {"selected_date": end}}, "reason": {"v": {"value": reason}}}}}
    return slack_post(slack_id, "interactions", {"payload": json.dumps({"type": "view_submission", "user": {"id": "U_AYSE"}, "view": view})})


code, r = leave_submit(free_day(3), free_day(1), "ters-tarih")
check("Slack formu: hatalı tarih pencerede hata olarak döner", code == 200 and r.get("response_action") == "errors", r)
tl = time.time()
for wk in range(0, 30, 3):
    d = free_day(4 + wk * 7)
    code, r = leave_submit(d, d, "slack-formdan-izin")
    if r and r.get("response_action") == "clear":
        break
check("Slack formu: izin talebi oluşturuldu", code == 200 and r.get("response_action") == "clear", r)
conf = wait_for("/api/chat.postMessage", lambda c: "D_U_AYSE" in c["body"] and "oluşturuldu" in urllib.parse.unquote_plus(c["body"]), tl)
check("Slack formu: talep sahibine onay mesajı", bool(conf), mock_calls("chat.postMessage", tl)[-2:])
appr = wait_for("/api/chat.postMessage", lambda c: "D_U_MEHMET" in c["body"] and "hr360_reject" in urllib.parse.unquote_plus(c["body"]), tl)
appr_txt = plain(appr[0]["body"]) if appr else ""
check("Slack formu: onaycıya kart gitti (gün sayısıyla)", bool(appr) and "1 gün" in appr_txt, appr_txt[:300])

# gerekçeli ret: düğme -> pencere -> gönder; gerekçe sohbete yazılmaz
if appr:
    blocks = json.loads(urllib.parse.parse_qs(appr[0]["body"])["blocks"][0])
    val = next(e["value"] for b in blocks if b["type"] == "actions" for e in b["elements"] if e.get("action_id") == "hr360_reject")
    wf_form = val.split("|")[0]
    tg2 = time.time()
    slack_post(slack_id, "interactions", {"payload": json.dumps({"type": "block_actions", "user": {"id": "U_MEHMET"}, "trigger_id": "TRIG2",
                                                                 "actions": [{"action_id": "hr360_reject", "value": val}]})})
    rm = wait_for("/api/views.open", lambda c: "TRIG2" in c["body"], tg2)
    rview = json.loads(urllib.parse.parse_qs(rm[0]["body"])["view"][0]) if rm else {}
    check("Slack: Reddet gerekçe penceresi açar", rview.get("callback_id") == "hr360_reject", rview)
    check("Slack: pencere açılınca henüz karar verilmedi", wf_status(wf_form)["status"] == "Pending")
    code, r = slack_post(slack_id, "interactions", {"payload": json.dumps({"type": "view_submission", "user": {"id": "U_MEHMET"},
        "view": {"callback_id": "hr360_reject", "private_metadata": rview.get("private_metadata", ""),
                 "state": {"values": {"reason": {"v": {"value": "Takvim uygun degil"}}}}}})})
    time.sleep(4)
    st = wf_status(wf_form)
    check("Slack: gerekçeyle reddedildi", st["status"] == "Rejected" and any("Takvim uygun degil" in (x.get("comment") or "") for x in st["steps"]), st.get("steps"))
    rej_dm = wait_for("/api/chat.postMessage", lambda c: "D_U_AYSE" in c["body"] and wf_form in urllib.parse.unquote_plus(c["body"]) or
                      ("D_U_AYSE" in c["body"] and "reddedildi" in urllib.parse.unquote_plus(c["body"])), tg2)
    rej_txt = urllib.parse.unquote_plus(rej_dm[-1]["body"]) if rej_dm else ""
    check("Slack: gerekçe sohbete yazılmadı, HR360'a yönlendirildi", bool(rej_dm) and "Takvim uygun" not in rej_txt and "Karar notu" in rej_txt, rej_txt[:300])

# kişisel ana sayfa
th = time.time()
slack_post(slack_id, "events", raw=json.dumps({"type": "event_callback", "event": {"type": "app_home_opened", "user": "U_AYSE", "tab": "home"}}))
home = wait_for("/api/views.publish", lambda c: "U_AYSE" in c["body"], th)
check("Slack: ana sayfa yayınlandı (özet + izin düğmesi)", bool(home) and "Özetiniz" in plain(home[0]["body"])
      and "hr360_leave_form" in urllib.parse.unquote_plus(home[0]["body"]), home)
code, man = api("admin", "GET", f"{G}/chat-apps/{slack_id}/slack-manifest")
check("Slack manifesti: ana sayfa ve app_home_opened", man["features"]["app_home"]["home_tab_enabled"] and "app_home_opened" in man["settings"]["event_subscriptions"]["bot_events"], man["features"]["app_home"])

# sabah özeti
wf_dig = new_leave(19, "sabah-ozeti")
time.sleep(3)
td = time.time()
code, r = api("admin", "POST", f"{G}/chat-apps/{slack_id}/digest")
dig = wait_for("/api/chat.postMessage", lambda c: "D_U_MEHMET" in c["body"] and "Günaydın" in urllib.parse.unquote_plus(c["body"]), td)
dig_txt = urllib.parse.unquote_plus(dig[0]["body"]) if dig else ""
check("Sabah özeti: onaycıya gitti, ad ve izin türü yok", code == 200 and r["sent"] >= 1 and bool(dig) and "Ayşe" not in dig_txt and "Ücretsiz" not in dig_txt, (r, dig_txt[:200]))

for w in (wf_prompt, wf_dig):
    code, r = slack_post(slack_id, "commands", {"user_id": "U_MEHMET", "text": "onaylarım"})
    v = next((e["value"] for b in (r or {}).get("blocks", []) if b.get("type") == "actions" for e in b["elements"]
              if e.get("action_id") == "hr360_reject" and w in e["value"]), None)
    if v:
        slack_post(slack_id, "interactions", {"payload": json.dumps({"type": "block_actions", "user": {"id": "U_MEHMET"},
                   "actions": [{"action_id": "hr360_reject", "value": v}], "response_url": "http://chatmock:8000/slack-response/9"})})

# gönderilemeyen mesaj kuyruğa alınır ve yeniden denenir (önceki bildirimler bitsin diye beklenir)
time.sleep(6)
mock("/_fail?path=/api/chat.postMessage&count=1", "POST")
tr = time.time()
wf_retry = new_leave(15, "slack-yeniden-deneme")
attempts = []
for _ in range(60):
    attempts = [c for c in mock_calls("/api/chat.postMessage", tr) if wf_retry in urllib.parse.unquote_plus(c["body"])]
    if len(attempts) >= 2:
        break
    time.sleep(1)
retry = attempts[1:]
check("Yeniden deneme: ilk gönderim başarısız, kuyruktan tekrar gönderildi", bool(retry) and len(attempts) >= 2, len(attempts))
if retry:
    v = next(e["value"] for b in json.loads(urllib.parse.parse_qs(retry[-1]["body"])["blocks"][0]) if b["type"] == "actions"
             for e in b["elements"] if e.get("action_id") == "hr360_reject")
    slack_post(slack_id, "interactions", {"payload": json.dumps({"type": "block_actions", "user": {"id": "U_MEHMET"},
               "actions": [{"action_id": "hr360_reject", "value": v}], "response_url": "http://chatmock:8000/slack-response/10"})})
    time.sleep(3)
    check("Yeniden gönderilen karttan karar verildi", wf_status(wf_retry)["status"] == "Rejected")

code, ids = api("admin", "GET", f"{G}/chat-apps/{slack_id}/identities")
check("Slack: eşleşen kullanıcılar listelenir", code == 200 and {i.get("employeeName") for i in ids} >= {"Mehmet Demir", "Ayşe Yılmaz"}, ids)
api("admin", "PUT", f"{G}/chat-apps/{slack_id}", {"platform": "Slack", "name": "HR360 Slack", "isEnabled": False, "notifyApprovals": True,
                                                  "notifyRequesters": True})
code, _ = slack_post(slack_id, "commands", {"user_id": "U_AYSE", "text": "bakiye"})
check("Slack: devre dışı uygulama 401", code == 401, code)

# ====================================================================== TEAMS
APP = str(uuid.uuid4())
MSTENANT = str(uuid.uuid4())
SVC = "http://chatmock:8000/teams/"
code, r = api("admin", "POST", f"{G}/chat-apps", {"platform": "Teams", "name": "HR360 Teams", "isEnabled": True, "notifyApprovals": True,
                                                   "notifyRequesters": True, "teamsAppId": APP, "teamsAppPassword": "wrong", "teamsAzureTenantId": MSTENANT})
check("Teams: yanlış gizli anahtar reddedilir", code == 400 and "Invalid client secret" in json.dumps(r, ensure_ascii=False), r)
code, r = api("admin", "POST", f"{G}/chat-apps", {"platform": "Teams", "name": "HR360 Teams", "isEnabled": True, "notifyApprovals": True,
                                                   "notifyRequesters": True, "teamsAppId": APP, "teamsAppPassword": "s3cret", "teamsAzureTenantId": MSTENANT})
check("Teams botu eklendi", code == 200 and r["endpoints"]["messaging"].endswith(f"/chat/teams/{r['id']}/messages"), r)
teams_id = r["id"]
req = urllib.request.Request(BASE + f"{G}/chat-apps/{teams_id}/teams-package", headers={"Authorization": "Bearer " + tok("admin")})
with urllib.request.urlopen(req) as resp:
    pkg = resp.read()
import io, zipfile  # noqa: E402
z = zipfile.ZipFile(io.BytesIO(pkg))
man = json.loads(z.read("manifest.json"))
check("Teams paketi (manifest + simgeler)", set(z.namelist()) == {"manifest.json", "color.png", "outline.png"} and man["bots"][0]["botId"] == APP
      and z.read("color.png")[:4] == b"\x89PNG", z.namelist())


def teams_post(activity, aud=APP, serviceurl=SVC, **jwt_opts):
    q = urllib.parse.urlencode({"aud": aud, "serviceurl": serviceurl, **jwt_opts})
    token = mock(f"/_jwt?{q}")["token"]
    return http("POST", f"{G}/chat/teams/{teams_id}/messages", activity, {"Authorization": "Bearer " + token})


def act(user, text=None, value=None, typ="message", reply_to=None, tenant=MSTENANT, conv=None):
    a = {"type": typ, "id": "in-" + uuid.uuid4().hex[:6], "serviceUrl": SVC, "channelId": "msteams",
         "from": {"id": f"29:{user}", "name": user}, "recipient": {"id": f"28:{APP}", "name": "HR360"},
         "conversation": {"id": conv or f"a:conv-{user}", "conversationType": "personal", "tenantId": tenant}}
    if text is not None:
        a["text"] = text
    if value is not None:
        a["value"] = value
    if reply_to:
        a["replyToId"] = reply_to
    if typ == "conversationUpdate":
        a["membersAdded"] = [{"id": f"28:{APP}"}]
    return a


code, _ = http("POST", f"{G}/chat/teams/{teams_id}/messages", act("mehmet.demir", "bakiye"), {"Authorization": "Bearer x.y.z"})
check("Teams: geçersiz jeton 401", code == 401, code)
code, _ = teams_post(act("mehmet.demir", "bakiye"), wrongkey="1")
check("Teams: yanlış anahtarla imzalı jeton 401", code == 401, code)
code, _ = teams_post(act("mehmet.demir", "bakiye"), aud=str(uuid.uuid4()))
check("Teams: farklı hedef kitle (aud) 401", code == 401, code)
code, _ = teams_post(act("mehmet.demir", "bakiye"), expired="1")
check("Teams: süresi dolmuş jeton 401", code == 401, code)
code, _ = teams_post(act("mehmet.demir", "bakiye"), serviceurl="http://evil.example/")
check("Teams: serviceUrl uyuşmazlığı 401", code == 401, code)
code, _ = teams_post(act("mehmet.demir", "bakiye", tenant=str(uuid.uuid4())))
check("Teams: başka Microsoft 365 kiracısı 403", code == 403, code)

t5 = time.time()
for u in ("mehmet.demir", "ayse.yilmaz"):
    code, _ = teams_post(act(u, typ="conversationUpdate"))
    check(f"Teams: {u} botu ekledi", code == 200, code)
welcome = wait_for("/teams/v3/conversations/a:conv-mehmet.demir/activities", since=t5)
check("Teams: hoş geldin mesajı + bağlama bağlantısı", bool(welcome) and "Onay talepleriniz" in welcome[0]["body"] and link_code(welcome[0]["body"]), welcome)
check("Teams: bot jetonuyla gönderim", bool(welcome) and welcome[0]["auth"] == "Bearer teams-bot-token", welcome)
for u, who in (("mehmet.demir", "mehmet"), ("ayse.yilmaz", "ayse")):
    w = wait_for(f"/teams/v3/conversations/a:conv-{u}/activities", since=t5)
    c1, prev, c2, res = confirm_link(link_code(w[0]["body"]) if w else None, who)
    check(f"Teams: {u} hesabını bağladı", c2 == 200 and prev["platform"] == "Teams", (c1, prev, c2, res))

t6 = time.time()
teams_post(act("ayse.yilmaz", "bakiye"))
rep = wait_for("/teams/v3/conversations/a:conv-ayse.yilmaz/activities", since=t6)
check("Teams: 'bakiye' komutu yanıtlandı", bool(rep) and "izin bakiyeniz" in rep[0]["body"], rep)

# grup sohbetinde kişisel yanıt kanala yazılmaz, kişiye özel sohbete gider
tg = time.time()
teams_post(act("ayse.yilmaz", "bakiye", conv="19:grup-sohbeti") | {"conversation": {"id": "19:grup-sohbeti", "conversationType": "groupChat", "tenantId": MSTENANT}})
grp = wait_for("/teams/v3/conversations/19:grup-sohbeti/activities", since=tg)
dm = wait_for("/teams/v3/conversations/a:conv-ayse.yilmaz/activities", lambda c: "izin bakiyeniz" in c["body"], tg)
grp_text = json.loads(grp[0]["body"])["summary"] if grp else ""
check("Teams: grup sohbetinde kişisel veri yok, yanıt özelden", "izin bakiyeniz" not in grp_text and "özel mesajla" in grp_text and bool(dm), (grp_text, dm))

t7 = time.time()
wf3 = new_leave(14, "teams-red-testi")
card = wait_for("/teams/v3/conversations/a:conv-mehmet.demir/activities", lambda c: wf3 in c["body"], t7)
check("Teams: onaycıya Adaptive Card gitti", bool(card) and "Action.Submit" in card[0]["body"], card)
if card:
    body = json.loads(card[0]["body"])
    data = next(a["data"] for a in body["attachments"][0]["content"]["actions"] if a.get("title") == "Reddet")
    acts_before = [c for c in mock_calls("/activities", t7)]
    card_id = f"act-{len([c for c in mock('/_log') if c['method'] == 'POST' and c['path'].endswith('/activities')])}"
    t8 = time.time()
    code, _ = teams_post(act("mehmet.demir", value=data, reply_to=card_id))
    check("Teams: karar isteği 200", code == 200, code)
    time.sleep(3)
    st = wf_status(wf3)
    check("Teams: iş akışı reddedildi", st["status"] == "Rejected", st.get("status"))
    put = wait_for("/teams/v3/conversations/a:conv-mehmet.demir/activities/", lambda c: c["method"] == "PUT" and "Reddettiniz" in c["body"], t8)
    check("Teams: kart 'Reddettiniz' olarak güncellendi", bool(put) and "Action.Submit" not in put[0]["body"], put)
    dm = wait_for("/teams/v3/conversations/a:conv-ayse.yilmaz/activities", lambda c: "reddedildi" in c["body"], t8)
    check("Teams: talep sahibine 'reddedildi' bildirimi", bool(dm), dm)


# Teams: izin kartı ve gerekçeli ret
tl2 = time.time()
teams_post(act("ayse.yilmaz", "izin al"))
lc = wait_for("/teams/v3/conversations/a:conv-ayse.yilmaz/activities", lambda c: "Input.Date" in c["body"], tl2)
check("Teams: izin kartı geldi", bool(lc), lc)
created = None
for wk in range(0, 30, 3):
    d = free_day(2 + wk * 7 + 21)
    tsub = time.time()
    teams_post(act("ayse.yilmaz", value={"hr360": "leave", "type": "Unpaid", "start": d, "end": d, "reason": "teams-kartindan"}))
    res = wait_for("/teams/v3/conversations/a:conv-ayse.yilmaz/activities", lambda c: "oluşturuldu" in json.loads(c["body"])["summary"] or "⚠" in json.loads(c["body"])["summary"], tsub)
    if res and "oluşturuldu" in json.loads(res[-1]["body"])["summary"]:
        created = res
        break
check("Teams: karttan izin talebi oluşturuldu", bool(created), res)
tc = wait_for("/teams/v3/conversations/a:conv-mehmet.demir/activities", lambda c: "teams-kartindan" not in c["body"] and "Input.Text" in c["body"] and "decide" in c["body"], tl2)
if tc:
    cbody = json.loads(tc[-1]["body"])
    data = next(a["data"] for a in cbody["attachments"][0]["content"]["actions"] if a.get("title") == "Reddet")
    wf_t = data["wf"]
    teams_post(act("mehmet.demir", value={**data, "reason": "Ekip yogun"}))
    time.sleep(4)
    st = wf_status(wf_t)
    check("Teams: karttaki gerekçeyle reddedildi", st["status"] == "Rejected" and any("Ekip yogun" in (x.get("comment") or "") for x in st["steps"]), st.get("steps"))

code, ids = api("admin", "GET", f"{G}/chat-apps/{teams_id}/identities")
check("Teams: eşleşen kullanıcılar", code == 200 and sum(1 for i in ids if i["employeeId"] and i["canReceive"]) >= 2, ids)

# yetki: çalışan sohbet uygulamalarını yönetemez
code, _ = api("ayse", "GET", f"{G}/chat-apps")
check("Yetki: çalışan sohbet uygulamalarını göremez", code == 403, code)

for a in (slack_id, teams_id):
    api("admin", "DELETE", f"{G}/chat-apps/{a}")
print(f"\nFAILS: {len(FAIL)}")
for f in FAIL:
    print("  -", f)
sys.exit(1 if FAIL else 0)

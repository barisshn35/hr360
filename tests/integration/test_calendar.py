"""Google Takvim / Microsoft 365 takvim bağlantısı, izin senkronizasyonu ve
Zoom / Teams / Google Meet toplantıları — uçtan uca (sahte sağlayıcılarla).

Ön koşullar test_chat.py ile aynı (chatmock + deploy/testing/chat-mock.yml).
"""

import datetime as dt
import json
import random
import sys
import time
import urllib.parse
import urllib.request

sys.path.insert(0, __import__("os").path.dirname(__file__))
from common import ensure_transfers, AYSE, BASE, FAIL, api, check, mock, wait_for  # noqa: E402

G = "/api/governance"
MEHMET = "3ab24e3e-cb06-40ab-934c-9ff7eab91fb6"
FAIL.clear()
ensure_transfers("google", "microsoft", "zoom")
mock("/_reset", "POST")


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *a, **k):
        return None


def callback(provider, **params):
    url = f"{BASE}{G}/calendar/oauth/callback/{provider}?" + urllib.parse.urlencode(params)
    opener = urllib.request.build_opener(NoRedirect)
    try:
        opener.open(url)
        return None
    except urllib.error.HTTPError as e:
        return e.headers.get("Location")


# ---------------------------------------------------------------- yönetici ayarları
for p in ("google", "microsoft", "zoom"):
    api("admin", "DELETE", f"{G}/calendar/providers/{p}")
code, _ = api("ayse", "GET", f"{G}/calendar/providers")
check("Yetki: çalışan sağlayıcı ayarlarını göremez", code == 403, code)
code, r = api("admin", "PUT", f"{G}/calendar/providers/microsoft", {"clientId": "yanlis", "clientSecret": "x", "isEnabled": True})
check("Microsoft: istemci kimliği GUID olmalı", code == 400, r)
code, _ = api("admin", "PUT", f"{G}/calendar/providers/google", {"clientId": "gid.apps.googleusercontent.com", "clientSecret": "gsecret", "isEnabled": True})
check("Google istemcisi kaydedildi", code == 200)
code, _ = api("admin", "PUT", f"{G}/calendar/providers/microsoft", {"clientId": "11111111-2222-3333-4444-555555555555", "clientSecret": "mssecret",
                                                                    "msTenant": "organizations", "isEnabled": True})
check("Microsoft istemcisi kaydedildi", code == 200)
code, r = api("admin", "PUT", f"{G}/calendar/providers/zoom", {"clientId": "zid", "clientSecret": "yanlis", "zoomAccountId": "acc1", "isEnabled": True})
check("Zoom: yanlış gizli anahtar reddedilir (gerçek jeton denemesi)", code == 400 and "Invalid client" in json.dumps(r), r)
code, _ = api("admin", "PUT", f"{G}/calendar/providers/zoom", {"clientId": "zid", "clientSecret": "zsecret", "zoomAccountId": "acc1",
                                                               "zoomDefaultHost": "mehmet.demir@demo.hr360", "isEnabled": True})
check("Zoom kaydedildi", code == 200)
code, prov = api("admin", "GET", f"{G}/calendar/providers")
g = next(p for p in prov if p["provider"] == "Google")
check("Sağlayıcı listesi: yönlendirme adresi, sır yok", g["redirectUri"].endswith("/calendar/oauth/callback/google") and "gsecret" not in json.dumps(prov), g)

# ---------------------------------------------------------------- kişisel bağlantı (OAuth + PKCE)
for who in ("ayse", "mehmet"):
    _, mine = api(who, "GET", f"{G}/calendar/connections")
    for c in mine.get("connections", []):
        api(who, "DELETE", f"{G}/calendar/connections/{c['id']}")
code, mine = api("ayse", "GET", f"{G}/calendar/connections")
check("Ayşe: Google ve Microsoft bağlanabilir", code == 200 and set(mine["available"]) == {"Google", "Microsoft"}, mine)

code, r = api("ayse", "POST", f"{G}/calendar/connect/google")
q = urllib.parse.parse_qs(urllib.parse.urlparse(r["authorizeUrl"]).query)
check("Google yetkilendirme adresi (PKCE S256, offline erişim)", q.get("code_challenge_method") == ["S256"] and q.get("access_type") == ["offline"]
      and "calendar.events" in q["scope"][0], r)
state = q["state"][0]
loc = callback("google", code="google-ayse", state=state)
check("Google geri dönüşü: profil sayfasına 'bağlandı'", loc and "takvim=baglandi" in loc, loc)
loc = callback("google", code="google-ayse", state=state)
check("Aynı state ikinci kez kullanılamaz", loc and "takvim=hata" in loc, loc)
loc = callback("google", code="google-ayse", state="uydurma")
check("Geçersiz state reddedilir", loc and "takvim=hata" in loc, loc)
_, r = api("ayse", "POST", f"{G}/calendar/connect/microsoft")
st2 = urllib.parse.parse_qs(urllib.parse.urlparse(r["authorizeUrl"]).query)["state"][0]
loc = callback("microsoft", error="access_denied", state=st2)
check("Kullanıcı izin vermezse anlaşılır hata", loc and "takvim=hata" in loc and "verilmedi" in urllib.parse.unquote(loc), loc)

_, r = api("mehmet", "POST", f"{G}/calendar/connect/microsoft")
q = urllib.parse.parse_qs(urllib.parse.urlparse(r["authorizeUrl"]).query)
check("Microsoft yetkilendirme adresi (Calendars.ReadWrite)", "Calendars.ReadWrite" in q["scope"][0], q)
loc = callback("microsoft", code="ms-mehmet", state=q["state"][0])
check("Mehmet Outlook'u bağladı", loc and "takvim=baglandi" in loc, loc)
_, mine = api("ayse", "GET", f"{G}/calendar/connections")
gc = next((c for c in mine["connections"] if c["provider"] == "Google"), None)
check("Ayşe'nin Google bağlantısı etkin ve e-posta doğru", gc and gc["status"] == "Active" and gc["accountEmail"] == "ayse.yilmaz@demo.hr360", mine)

# ---------------------------------------------------------------- izin senkronizasyonu
t0 = time.time()
# Önceki çalıştırmaların (ya da diğer testlerin) izinleriyle çakışırsa başka bir gün denenir.
for _try in range(10):
    start = (dt.date(2031, 1, 6) + dt.timedelta(weeks=random.randint(0, 150))).isoformat()
    code, lv = api("ayse", "POST", "/api/leave/leave-requests", {"employeeId": AYSE, "type": "Unpaid", "startDate": start, "endDate": start, "days": 0, "reason": "takvim-testi"})
    if code != 409:
        break
check("İzin talebi", code in (200, 201), lv)
wf = lv["workflowRequestId"]
_, w = api("ayse", "GET", f"/api/workflow/workflows/{wf}")
code, _ = api("mehmet", "POST", f"/api/workflow/workflows/{wf}/steps/{w['steps'][0]['id']}/decide", {"decision": "Approved", "comment": "ok"})
check("İzin web'den onaylandı", code == 200, code)
ev = wait_for("/googleapis/calendar/v3/calendars/primary/events", lambda c: c["method"] == "POST" and start in c["body"], t0)
body = json.loads(ev[0]["body"]) if ev else {}
check("Onaylanan izin Ayşe'nin Google Takvimi'ne tüm gün etkinlik olarak yazıldı",
      bool(ev) and body["start"] == {"date": start} and "İzinli" in body["summary"] and ev[0]["auth"] == "Bearer gtok-ayse", ev)

# ---------------------------------------------------------------- toplantılar
when = (dt.datetime.utcnow() + dt.timedelta(days=3)).replace(hour=8, minute=0, second=0, microsecond=0)
code, oo = api("mehmet", "POST", "/api/engagement/one-on-ones", {"employeeId": AYSE, "scheduledAt": when.isoformat() + "Z", "agenda": ["Takvim testi"]})
check("1:1 planlandı", code in (200, 201), oo)
oo_id = oo["id"]
code, opt = api("mehmet", "GET", f"{G}/meetings/options")
check("Mehmet için seçenekler: Zoom + Teams (Google yok)", opt == {"zoom": True, "teams": True, "google": False, "calendar": True}, opt)

code, _ = api("ayse", "POST", f"{G}/meetings", {"sourceType": "one-on-one", "sourceId": oo_id, "durationMinutes": 30, "provider": "zoom", "addToCalendars": True})
check("Yetki: 1:1'in çalışanı toplantı oluşturamaz", code == 403, code)
code, r = api("mehmet", "POST", f"{G}/meetings", {"sourceType": "one-on-one", "sourceId": oo_id, "durationMinutes": 30, "provider": "google", "addToCalendars": True})
check("Google Meet için Google takvimi gerekir (anlaşılır hata)", code == 400 and "Google" in r["message"], r)

t1 = time.time()
code, teams = api("mehmet", "POST", f"{G}/meetings", {"sourceType": "one-on-one", "sourceId": oo_id, "durationMinutes": 30, "provider": "teams", "addToCalendars": True})
check("Teams toplantısı oluştu (katılım bağlantısı)", code == 200 and (teams.get("joinUrl") or "").startswith("https://teams.microsoft.com/"), teams)
ev = wait_for("/graph/v1.0/me/events", lambda c: c["method"] == "POST", t1)
b = json.loads(ev[0]["body"]) if ev else {}
check("Outlook etkinliği: Teams çevrimiçi toplantı + Ayşe davetli", b.get("isOnlineMeeting") is True
      and any(a["emailAddress"]["address"] == "ayse.yilmaz@demo.hr360" for a in b.get("attendees", [])), b)

t2 = time.time()
code, zoom = api("mehmet", "POST", f"{G}/meetings", {"sourceType": "one-on-one", "sourceId": oo_id, "durationMinutes": 45, "provider": "zoom", "addToCalendars": True})
check("Zoom toplantısı oluştu", code == 200 and zoom["joinUrl"].startswith("https://zoom.us/j/"), zoom)
zc = wait_for("/zoomapi/v2/users/", since=t2)
check("Zoom: düzenleyicinin hesabı adına, Server-to-Server jetonuyla", bool(zc) and "mehmet.demir@demo.hr360" in zc[0]["path"] and zc[0]["auth"] == "Bearer zoom-token", zc)
zb = json.loads(zc[0]["body"]) if zc else {}
check("Zoom: başlangıç UTC, süre 45 dk, bekleme odası", zb.get("duration") == 45 and zb.get("start_time", "").endswith("Z") and zb["settings"]["waiting_room"], zb)
ev = wait_for("/graph/v1.0/me/events", lambda c: c["method"] == "POST" and "zoom.us" in c["body"], t2)
check("Zoom bağlantısı Outlook davetine eklendi", bool(ev), ev)

code, lst = api("ayse", "GET", f"{G}/meetings?sourceType=one-on-one&sourceId={oo_id}")
check("Ayşe (katılımcı) 1:1 toplantılarını görür", code == 200 and {m["provider"] for m in lst} >= {"zoom", "teams"}, lst)
code, lst = api("zeynep" if False else "ayse", "GET", f"{G}/meetings")
check("Yaklaşan toplantılarım", code == 200 and any(m["id"] == zoom["id"] for m in lst), lst)

t3 = time.time()
code, _ = api("ayse", "DELETE", f"{G}/meetings/{zoom['id']}")
check("Yetki: katılımcı toplantıyı iptal edemez", code == 403, code)
code, _ = api("mehmet", "DELETE", f"{G}/meetings/{zoom['id']}")
check("Zoom toplantısı iptal edildi", code == 204, code)
dz = wait_for("/zoomapi/v2/meetings/", lambda c: c["method"] == "DELETE", t3)
dg = wait_for("/graph/v1.0/me/events/", lambda c: c["method"] == "DELETE", t3)
check("İptal: Zoom toplantısı ve Outlook etkinliği silindi", bool(dz) and bool(dg), (dz, dg))

# ---------------------------------------------------------------- boş / dolu
# İzin/1:1 kaydı olmayan uzak bir hafta içi gün (öneriler yalnızca hafta içi iş saatlerinde).
# Rastgele hafta resmî tatile ya da önceki çalıştırmaların izinlerine denk gelirse öneri
# çıkmaz; birkaç hafta denenir (öneri mantığı yine aşağıda doğrulanır).
for _try in range(5):
    frm = dt.datetime(2033, 3, 7) + dt.timedelta(weeks=random.randint(0, 100))
    code, av = api("mehmet", "POST", f"{G}/meetings/availability", {"employeeIds": [MEHMET, AYSE], "from": frm.isoformat() + "Z",
                                                                    "to": (frm + dt.timedelta(days=1)).isoformat() + "Z", "durationMinutes": 60})
    if code != 200 or (av or {}).get("suggestions"):
        break
people = {p["name"]: p for p in (av or {}).get("people", [])}
check("Boş/dolu: iki kişi, takvimleri bağlı", code == 200 and all(p["calendarConnected"] for p in people.values()) and len(people) == 2, av)
busy = [(b["start"], b["end"]) for p in people.values() for b in p["busy"] if b["source"] == "takvim"]
check("Boş/dolu: Google (07–08Z) ve Outlook (08–09Z) dolu aralıkları, 'free' hariç", len(busy) == 2, busy)
sug = [s for s in av.get("suggestions", [])]
check("Saat önerileri iş saatinde ve dolu aralıklarla çakışmaz",
      sug and all(not ("T07:" in s or "T08:" in s) and "T06:" <= s[10:14] + ":" for s in sug) and sug[0].startswith(frm.date().isoformat() + "T06:00"), sug)
code, _ = api("ayse", "POST", f"{G}/meetings/availability", {"employeeIds": [MEHMET], "from": frm.isoformat() + "Z", "to": (frm + dt.timedelta(days=1)).isoformat() + "Z", "durationMinutes": 30})
check("Yetki: çalışan başkalarının boş/dolu bilgisini sorgulayamaz", code == 403, code)

# ---------------------------------------------------------------- temizlik
api("mehmet", "DELETE", f"{G}/meetings/{teams['id']}")
api("mehmet", "DELETE", f"/api/engagement/one-on-ones/{oo_id}")
for who in ("ayse", "mehmet"):
    _, mine = api(who, "GET", f"{G}/calendar/connections")
    for c in mine["connections"]:
        code, _ = api(who, "DELETE", f"{G}/calendar/connections/{c['id']}")
check("Takvim bağlantıları kaldırıldı", code == 204, code)
for p in ("google", "microsoft", "zoom"):
    api("admin", "DELETE", f"{G}/calendar/providers/{p}")

print(f"\nFAILS: {len(FAIL)}")
for f in FAIL:
    print("  -", f)
sys.exit(1 if FAIL else 0)

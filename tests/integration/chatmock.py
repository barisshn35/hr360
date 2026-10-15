"""Slack Web API, Microsoft kimlik (token), Bot Framework OpenID/JWKS, Teams Bot
Connector, Google OAuth + Takvim, Microsoft Graph (takvim) ve Zoom API'sinin sahtesi.
Entegrasyon testleri gerçek hesaplar olmadan uçtan uca çalışsın diye.

Çalıştırma (hr360-net ağında, "chatmock" adıyla):
    docker run -d --name chatmock --network hr360-net -v $PWD/tests/integration:/t \
      python:3.11-slim sh -c "pip install -q cryptography pyjwt && python -u /t/chatmock.py"

Test yardımcı uçları:
    GET  /_log            alınan tüm çağrılar (JSON)
    POST /_reset          kaydı temizler
    POST /_fail?path=..&count=N   yolu bu metni içeren sonraki N isteğe 500 döner (yeniden deneme testi)
    GET  /_jwt?aud=..&serviceurl=..&expired=0&wrongkey=0   Bot Framework jetonu üretir
Slack kullanıcıları e-postaya göre: ad.soyad@... -> U_AD (büyük harf).

Dalga 5e:
    GET  /slackfiles/<ad>   Slack url_private_download (bot jetonu ister) -> fixtures/receipt.png
    GET  /teamsfiles/<ad>   Teams satır içi görüntü (bot jetonu ister) -> fixtures/receipt.png
    /mm/api/v4/...          Mattermost REST API v4 (bot jetonu "mm-bot-token"); kullanıcı kimliği "mm_<e-posta yerel kısmı>"
    /rc/api/v1/...          Rocket.Chat REST API (X-Auth-Token "rc-token", X-User-Id "rcbot"); kullanıcı kimliği "rc_<yerel kısım>"

Dalga 12 (hesap açma/kapatma):
    POST /google/token (grant jwt-bearer)  servis hesabı JWT'si (RS256, iss/sub/scope/aud denetlenir) -> "gadmin-<sub yerel kısmı>";
                                            sub "denied@..." ise 401 unauthorized_client
    /googleadmin/admin/directory/v1/users   Google Admin SDK Directory: GET liste, POST aç (var olan / "exists." önekli -> 409),
                                            PUT /users/<e-posta> askıya al, POST /users/<e-posta>/signOut
    /ms/<kiracı>/oauth2/v2.0/token (client_credentials, scope graph .default) -> "msgraph-app-token" (client_secret "wrong" -> 401)
    /graph/v1.0/users                       Microsoft Graph: GET liste, POST aç (var olan -> 400 "already exists"),
                                            PATCH /users/<upn>, POST /users/<upn>/revokeSignInSessions
    GET  /_directory                        açılan/askıya alınan sahte hesaplar (test doğrulaması)
"""

import base64
import json
import threading
import time
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, unquote, urlparse

import jwt
from cryptography.hazmat.primitives.asymmetric import rsa

KEY = rsa.generate_private_key(public_exponent=65537, key_size=2048)
OTHER_KEY = rsa.generate_private_key(public_exponent=65537, key_size=2048)
KID = "mock-key-1"
LOG = []
SYSLOG = []  # SIEM aktarımı (UDP 5514) ile gelen satırlar
FAILS = {}
LOCK = threading.Lock()
EMAILS = {}  # slack user id -> email
SEQ = [0]
EVENTS = {}  # takvim etkinlikleri: id -> gövde
DIRECTORY = {}  # hesap sağlama: e-posta -> {"provider", "id", "suspended", "signedOut"}
SHORT = {"ayse": "ayse.yilmaz", "mehmet": "mehmet.demir", "zeynep": "zeynep.kaya"}


def user_of_token(auth: str) -> str:
    # "Bearer gtok-ayse" / "Bearer mstok-mehmet" -> ayse.yilmaz@demo.hr360
    short = (auth or "").split("-", 1)[-1]
    return SHORT.get(short, short) + "@demo.hr360"


def next_id(prefix):
    SEQ[0] += 1
    return f"{prefix}-{SEQ[0]}"


def b64u(n: int) -> str:
    b = n.to_bytes((n.bit_length() + 7) // 8, "big")
    return base64.urlsafe_b64encode(b).rstrip(b"=").decode()


def jwks():
    pub = KEY.public_key().public_numbers()
    return {"keys": [{"kty": "RSA", "use": "sig", "kid": KID, "alg": "RS256", "n": b64u(pub.n), "e": b64u(pub.e),
                      "endorsements": ["msteams"]}]}


FIXTURE = __import__("os").path.join(__import__("os").path.dirname(__file__), "fixtures", "receipt.png")


def mm_user(uid):
    local = uid[3:] if uid.startswith("mm_") else uid
    first, _, last = local.partition(".")
    return {"id": uid, "username": local, "email": f"{local}@demo.hr360", "first_name": first.capitalize(), "last_name": last.capitalize()}


def rc_user(uid):
    local = uid[3:] if uid.startswith("rc_") else uid
    return {"_id": uid, "username": local, "name": local, "emails": [{"address": f"{local}@demo.hr360", "verified": True}]}


def slack_user_for(email: str) -> str:
    uid = "U_" + email.split("@")[0].split(".")[0].upper()
    EMAILS[uid] = email
    return uid


class H(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *a):  # sessiz
        pass

    def _send(self, code, obj=None):
        body = b"" if obj is None else json.dumps(obj).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _body(self):
        # .NET JsonContent / PostAsJsonAsync gövdeyi "chunked" gönderir.
        if "chunked" in (self.headers.get("Transfer-Encoding") or "").lower():
            out = b""
            while True:
                size = int(self.rfile.readline().strip().split(b";")[0], 16)
                if size == 0:
                    self.rfile.readline()
                    break
                out += self.rfile.read(size)
                self.rfile.readline()
            return out.decode()
        n = int(self.headers.get("Content-Length") or 0)
        return self.rfile.read(n).decode() if n else ""

    def _record(self, body):
        with LOCK:
            LOG.append({"method": self.command, "path": unquote(self.path), "auth": self.headers.get("Authorization"),
                        "body": body, "at": time.time()})

    # ------------------------------------------------------------------ GET
    def do_GET(self):
        u = urlparse(self.path)
        q = {k: v[0] for k, v in parse_qs(u.query).items()}
        if u.path == "/_log":
            with LOCK:
                return self._send(200, LOG)
        if u.path.startswith("/tcmb/"):
            # Sahte TCMB kur dosyası: hafta içi günler için USD/EUR, hafta sonu 404.
            import datetime as _dt
            name = u.path.rsplit("/", 1)[-1]
            if name == "today.xml":
                day = _dt.date.today()
            else:
                try:
                    day = _dt.datetime.strptime(name[:8], "%d%m%Y").date()
                except ValueError:
                    return self._send(404, {"error": "bad"})
            if day.weekday() >= 5:
                return self._send(404, {"error": "holiday"})
            with LOCK:
                LOG.append({"method": "GET", "path": u.path, "auth": None, "body": None, "at": time.time()})
            xml = ('<?xml version="1.0" encoding="UTF-8"?><Tarih_Date Tarih="%s">'
                   '<Currency Kod="USD" CurrencyCode="USD"><Unit>1</Unit><ForexBuying>41.5000</ForexBuying></Currency>'
                   '<Currency Kod="EUR" CurrencyCode="EUR"><Unit>1</Unit><ForexBuying>48.2500</ForexBuying></Currency>'
                   '</Tarih_Date>') % day.strftime("%d.%m.%Y")
            data = xml.encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/xml")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)
            return
        if u.path.startswith("/slackfiles/") or u.path.startswith("/teamsfiles/"):
            self._record("")
            want = "Bearer xoxb-test-token" if u.path.startswith("/slackfiles/") else "Bearer teams-bot-token"
            if u.path.endswith("/presigned.png"):
                want = None  # Teams dosya eki: ön imzalı adres, jeton gerekmez
            if want and self.headers.get("Authorization") != want:
                return self._send(401, {"error": "unauthorized"})
            if u.path.endswith(".txt"):
                data, ctype = b"not an image", "text/plain"
            else:
                with open(FIXTURE, "rb") as fh:
                    data, ctype = fh.read(), "image/png"
            self.send_response(200)
            self.send_header("Content-Type", ctype)
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)
            return
        if u.path.startswith("/mm/api/v4/"):
            self._record("")
            if self.headers.get("Authorization") != "Bearer mm-bot-token":
                return self._send(401, {"id": "api.context.session_expired.app_error", "message": "Invalid or expired session"})
            rest = u.path[len("/mm/api/v4/"):]
            if rest == "users/me":
                return self._send(200, {"id": "mmbot", "username": "hr360"})
            if rest.startswith("users/email/"):
                email = unquote(rest.split("/", 2)[2])
                if email.endswith("@demo.hr360"):
                    return self._send(200, mm_user("mm_" + email.split("@")[0]))
                return self._send(404, {"message": "Unable to find the user."})
            if rest.startswith("users/"):
                return self._send(200, mm_user(unquote(rest.split("/", 1)[1])))
            return self._send(404, {"message": "not found"})
        if u.path.startswith("/rc/api/v1/"):
            self._record("")
            if self.headers.get("X-Auth-Token") != "rc-token" or self.headers.get("X-User-Id") != "rcbot":
                return self._send(401, {"success": False, "error": "You must be logged in to do this."})
            rest = u.path[len("/rc/api/v1/"):]
            if rest == "me":
                return self._send(200, {"_id": "rcbot", "username": "hr360bot", "success": True})
            if rest == "users.info":
                return self._send(200, {"user": rc_user(q.get("userId", "")), "success": True})
            if rest == "users.list":
                query = json.loads(q.get("query", "{}"))
                email = query.get("emails.address", "")
                users = [rc_user("rc_" + email.split("@")[0])] if email.endswith("@demo.hr360") else []
                return self._send(200, {"users": users, "count": len(users), "success": True})
            return self._send(404, {"success": False, "error": "not found"})
        if u.path == "/_syslog":
            with LOCK:
                return self._send(200, SYSLOG[-int(q.get("n", "500")):])
        if u.path == "/botframework/openid":
            return self._send(200, {"issuer": "https://api.botframework.com",
                                    "jwks_uri": "http://chatmock:8000/botframework/jwks",
                                    "id_token_signing_alg_values_supported": ["RS256"]})
        if u.path == "/botframework/jwks":
            return self._send(200, jwks())
        if u.path == "/_jwt":
            now = int(time.time())
            claims = {"iss": "https://api.botframework.com", "aud": q["aud"], "serviceurl": q.get("serviceurl"),
                      "nbf": now - 10, "iat": now - 10, "exp": now - 60 if q.get("expired") == "1" else now + 600}
            key = OTHER_KEY if q.get("wrongkey") == "1" else KEY
            tok = jwt.encode(claims, key, algorithm="RS256", headers={"kid": KID})
            return self._send(200, {"token": tok})
        if u.path == "/_directory":
            with LOCK:
                return self._send(200, DIRECTORY)
        if u.path.startswith("/googleadmin/") or u.path.startswith("/graph/v1.0/users"):
            self._record("")
            return self._directory("GET", u, "")
        if u.path == "/googleapis/oauth2/v3/userinfo":
            self._record("")
            return self._send(200, {"email": user_of_token(self.headers.get("Authorization"))})
        if u.path == "/graph/v1.0/me":
            self._record("")
            return self._send(200, {"mail": user_of_token(self.headers.get("Authorization")), "userPrincipalName": "x"})
        if u.path == "/google/auth" or u.path.endswith("/oauth2/v2.0/authorize"):
            return self._send(200, {"note": "test: authorize"})
        if "/v3/conversations/" in u.path and "/members/" in u.path:
            self._record("")
            uid = unquote(u.path.rsplit("/", 1)[1])
            # Teams kullanıcı kimliği "29:<e-posta yerel kısmı>" biçiminde verilir (testte).
            local = uid.split(":", 1)[-1]
            return self._send(200, {"id": uid, "name": local, "email": f"{local}@demo.hr360",
                                    "userPrincipalName": f"{local}@demo.hr360", "aadObjectId": str(uuid.uuid5(uuid.NAMESPACE_DNS, local))})
        self._send(404, {"error": "not_found"})

    # ------------------------------------------------------------------ POST
    def do_POST(self):
        if self.path.startswith("/push/"):
            # Sahte Web Push servisi: ikili (aes128gcm) gövde base64 olarak kaydedilir.
            import base64 as _b64
            n = int(self.headers.get("Content-Length") or 0)
            raw = self.rfile.read(n) if n else b""
            with LOCK:
                LOG.append({"method": "POST", "path": self.path, "auth": self.headers.get("Authorization"),
                            "body": _b64.b64encode(raw).decode(), "encoding": self.headers.get("Content-Encoding"),
                            "ttl": self.headers.get("TTL"), "at": time.time()})
            return self._send(410 if "/gone" in self.path else 201)
        body = self._body()
        self._record(body)
        u = urlparse(self.path)
        if u.path == "/_reset":
            with LOCK:
                LOG.clear()
                FAILS.clear()
            return self._send(200, {"ok": True})
        if u.path == "/_fail":
            q = {k: v[0] for k, v in parse_qs(u.query).items()}
            with LOCK:
                FAILS[q["path"]] = int(q.get("count", "1"))
            return self._send(200, {"ok": True})
        with LOCK:
            hit = next((k for k, n in FAILS.items() if n > 0 and k in u.path), None)
            if hit:
                FAILS[hit] -= 1
        if hit:
            return self._send(500, {"ok": False, "error": "injected_failure"})
        if u.path.endswith("/oauth2/v2.0/token") or u.path == "/google/token":
            f = {k: v[0] for k, v in parse_qs(body).items()}
            if f.get("client_secret") == "wrong":
                return self._send(401, {"error": "invalid_client", "error_description": "AADSTS7000215: Invalid client secret provided."})
            google = u.path == "/google/token"
            pre = "g" if google else "ms"
            grant = f.get("grant_type")
            if google and grant == "urn:ietf:params:oauth:grant-type:jwt-bearer":
                return self._google_assertion(f.get("assertion", ""))
            if grant == "client_credentials" and "graph.microsoft.com" in f.get("scope", ""):
                return self._send(200, {"token_type": "Bearer", "expires_in": 3599, "access_token": "msgraph-app-token"})
            if grant == "client_credentials":
                return self._send(200, {"token_type": "Bearer", "expires_in": 3599, "access_token": "teams-bot-token"})
            if grant == "authorization_code":
                if not f.get("code_verifier"):
                    return self._send(400, {"error": "invalid_request", "error_description": "PKCE code_verifier eksik"})
                who = f["code"].split("-", 1)[-1]
                return self._send(200, {"access_token": f"{pre}tok-{who}", "refresh_token": f"{pre}refresh-{who}", "expires_in": 3600})
            if grant == "refresh_token":
                if f["refresh_token"].endswith("revoked"):
                    return self._send(400, {"error": "invalid_grant", "error_description": "Token has been expired or revoked."})
                who = f["refresh_token"].split("-", 1)[-1]
                return self._send(200, {"access_token": f"{pre}tok-{who}", "expires_in": 3600})
            return self._send(400, {"error": "unsupported_grant_type"})
        if u.path.startswith("/googleadmin/") or u.path.startswith("/graph/v1.0/users"):
            return self._directory("POST", u, body)
        if u.path == "/anthropic/v1/messages":
            q = json.loads(body)
            if self.headers.get("x-api-key") != "test" or not self.headers.get("anthropic-version"):
                return self._send(401, {"type": "error", "error": {"type": "authentication_error", "message": "invalid x-api-key"}})
            user = q["messages"][0]["content"]
            text = f"MOCK-LLM[{q['model']}] Çalışan için yanıt. Girdi: {user[:120]}"
            return self._send(200, {"type": "message", "role": "assistant", "model": q["model"],
                                    "content": [{"type": "text", "text": text}], "usage": {"input_tokens": len(user) // 4, "output_tokens": 42}})
        if u.path == "/openai/v1/chat/completions":
            q = json.loads(body)
            user = q["messages"][-1]["content"]
            return self._send(200, {"choices": [{"message": {"role": "assistant", "content": f"MOCK-OPENAI {user[:80]}"}}],
                                    "usage": {"prompt_tokens": 10, "completion_tokens": 5}})
        if u.path == "/zoom/oauth/token":
            auth = base64.b64decode((self.headers.get("Authorization") or "Basic Og==")[6:]).decode()
            if not auth.endswith(":zsecret"):
                return self._send(401, {"reason": "Invalid client_id or client_secret", "error": "invalid_client"})
            return self._send(200, {"access_token": "zoom-token", "expires_in": 3599})
        if u.path.startswith("/zoomapi/v2/users/") and u.path.endswith("/meetings"):
            user = unquote(u.path.split("/")[4])
            if user.startswith("zeynep"):
                return self._send(404, {"code": 1001, "message": "User does not exist."})
            mid = 83100000000 + SEQ[0]
            SEQ[0] += 1
            return self._send(201, {"id": mid, "join_url": f"https://zoom.us/j/{mid}?pwd=test", "host_email": user})
        if u.path == "/googleapis/calendar/v3/calendars/primary/events":
            ev = json.loads(body)
            eid = next_id("gev")
            EVENTS[eid] = ev
            out = {"id": eid, "htmlLink": f"https://calendar.google.com/event?eid={eid}"}
            if ev.get("conferenceData"):
                out["hangoutLink"] = f"https://meet.google.com/abc-{eid}"
            return self._send(200, out)
        if u.path == "/graph/v1.0/me/events":
            ev = json.loads(body)
            eid = next_id("msev")
            EVENTS[eid] = ev
            out = {"id": eid, "webLink": f"https://outlook.office.com/calendar/item/{eid}"}
            if ev.get("isOnlineMeeting"):
                out["onlineMeeting"] = {"joinUrl": f"https://teams.microsoft.com/l/meetup-join/{eid}"}
            return self._send(201, out)
        if u.path == "/googleapis/calendar/v3/freeBusy":
            q = json.loads(body)
            day = q["timeMin"][:10]
            return self._send(200, {"calendars": {"primary": {"busy": [{"start": f"{day}T07:00:00Z", "end": f"{day}T08:00:00Z"}]}}})
        if u.path == "/graph/v1.0/me/calendar/getSchedule":
            q = json.loads(body)
            day = q["startTime"]["dateTime"][:10]
            return self._send(200, {"value": [{"scheduleItems": [{"status": "busy", "start": {"dateTime": f"{day}T08:00:00.0000000"}, "end": {"dateTime": f"{day}T09:00:00.0000000"}},
                                                                 {"status": "free", "start": {"dateTime": f"{day}T12:00:00"}, "end": {"dateTime": f"{day}T13:00:00"}}]}]})
        if u.path.startswith("/mm/api/v4/"):
            if self.headers.get("Authorization") != "Bearer mm-bot-token":
                return self._send(401, {"message": "Invalid or expired session"})
            rest = u.path[len("/mm/api/v4/"):]
            if rest == "channels/direct":
                ids = json.loads(body or "[]")
                user = next((i for i in ids if i != "mmbot"), "x")
                return self._send(201, {"id": "mmdm_" + user, "type": "D"})
            if rest == "posts":
                SEQ[0] += 1
                return self._send(201, {"id": f"mmpost-{SEQ[0]}"})
            return self._send(404, {"message": "not found"})
        if u.path.startswith("/rc/api/v1/"):
            if self.headers.get("X-Auth-Token") != "rc-token" or self.headers.get("X-User-Id") != "rcbot":
                return self._send(401, {"success": False, "error": "You must be logged in to do this."})
            rest = u.path[len("/rc/api/v1/"):]
            if rest == "im.create":
                username = json.loads(body or "{}").get("username", "x")
                return self._send(200, {"room": {"_id": "rcbot" + "rc_" + username, "t": "d"}, "success": True})
            if rest == "chat.postMessage":
                SEQ[0] += 1
                return self._send(200, {"message": {"_id": f"rcmsg-{SEQ[0]}"}, "success": True})
            if rest == "chat.update":
                return self._send(200, {"success": True})
            return self._send(404, {"success": False, "error": "not found"})
        if u.path.startswith("/api/"):
            return self._slack(u.path[5:], {k: v[0] for k, v in parse_qs(body).items()})
        if u.path.startswith("/slack-response/"):
            return self._send(200, {"ok": True})
        if "/v3/conversations/" in u.path and u.path.endswith("/activities"):
            SEQ[0] += 1
            return self._send(201, {"id": f"act-{SEQ[0]}"})
        if "/v3/conversations/" in u.path and "/activities/" in u.path:
            return self._send(200, {"id": u.path.rsplit("/", 1)[1]})
        if u.path.startswith("/hooks/"):
            # n8n / Zapier "Catch Hook" benzeri alıcı (REST hook teslimatı).
            return self._send(200, {"received": True})
        self._send(404, {"error": "not_found"})

    def do_DELETE(self):
        self._record("")
        u = urlparse(self.path)
        if u.path.startswith("/googleapis/calendar/v3/calendars/primary/events/") or u.path.startswith("/graph/v1.0/me/events/"):
            eid = unquote(u.path.rsplit("/", 1)[1])
            return self._send(204 if EVENTS.pop(eid, None) is not None else 404)
        if u.path.startswith("/zoomapi/v2/meetings/"):
            return self._send(204)
        self._send(404, {})

    def do_PUT(self):
        body = self._body()
        self._record(body)
        if "/v3/conversations/" in self.path:
            return self._send(200, {"id": self.path.rsplit("/", 1)[1]})
        if self.path.startswith("/mm/api/v4/posts/") and self.path.endswith("/patch"):
            return self._send(200, {"id": self.path.split("/")[5]})
        if self.path.startswith("/googleadmin/"):
            return self._directory("PUT", urlparse(self.path), body)
        self._send(404, {})

    def do_PATCH(self):
        body = self._body()
        self._record(body)
        if self.path.startswith("/graph/v1.0/users/"):
            return self._directory("PATCH", urlparse(self.path), body)
        self._send(404, {})

    # ------------------------------------------------------------------ hesap sağlama (dalga 12)
    def _google_assertion(self, assertion):
        try:
            header = jwt.get_unverified_header(assertion)
            claims = jwt.decode(assertion, options={"verify_signature": False})
        except jwt.PyJWTError:
            return self._send(400, {"error": "invalid_grant", "error_description": "Invalid JWT"})
        ok = (header.get("alg") == "RS256" and claims.get("iss", "").endswith(".iam.gserviceaccount.com")
              and claims.get("aud") == "https://oauth2.googleapis.com/token"
              and "admin.directory.user" in claims.get("scope", "") and claims.get("exp", 0) > time.time())
        if not ok:
            return self._send(400, {"error": "invalid_grant", "error_description": "Invalid JWT claims"})
        sub = claims.get("sub", "")
        if sub.startswith("denied@"):
            return self._send(401, {"error": "unauthorized_client", "error_description": "Client is unauthorized to retrieve access tokens using this method."})
        return self._send(200, {"access_token": "gadmin-" + sub.split("@")[0], "expires_in": 3600, "token_type": "Bearer"})

    def _directory(self, method, u, body):
        auth = self.headers.get("Authorization", "")
        google = u.path.startswith("/googleadmin/")
        if google and not auth.startswith("Bearer gadmin-"):
            return self._send(401, {"error": {"code": 401, "message": "Login Required."}})
        if not google and auth != "Bearer msgraph-app-token":
            return self._send(401, {"error": {"code": "InvalidAuthenticationToken", "message": "Access token is empty."}})
        base = "/googleadmin/admin/directory/v1/users" if google else "/graph/v1.0/users"
        rest = unquote(u.path[len(base):]).strip("/")
        provider = "Google" if google else "Microsoft"
        if method == "GET" and rest == "":
            return self._send(200, {"users": []} if google else {"value": []})
        if method == "POST" and rest == "":
            q = json.loads(body or "{}")
            email = (q.get("primaryEmail") or q.get("userPrincipalName") or "").lower()
            with LOCK:
                exists = email in DIRECTORY or email.startswith("exists.")
                if not exists:
                    DIRECTORY[email] = {"provider": provider, "id": next_id("gu" if google else "msu"), "suspended": False, "signedOut": False}
            if exists:
                if google:
                    return self._send(409, {"error": {"code": 409, "message": "Entity already exists."}})
                return self._send(400, {"error": {"code": "Request_BadRequest",
                                                  "message": "Another object with the same value for property userPrincipalName already exists."}})
            return self._send(201, {"id": DIRECTORY[email]["id"], "primaryEmail": email} if google else {"id": DIRECTORY[email]["id"], "userPrincipalName": email})
        parts = rest.split("/")
        email = parts[0].lower()
        with LOCK:
            acct = DIRECTORY.get(email)
            if acct is None and not email.startswith("missing"):
                # Ürünün açmadığı (önceden var olan) hesap: askıya alma için kabul edilir.
                acct = DIRECTORY[email] = {"provider": provider, "id": next_id("pre"), "suspended": False, "signedOut": False}
        if acct is None:
            return self._send(404, {"error": {"code": 404, "message": "Resource Not Found: userKey"}})
        if (method == "PUT" and google) or (method == "PATCH" and not google):
            q = json.loads(body or "{}")
            with LOCK:
                if q.get("suspended") is True or q.get("accountEnabled") is False:
                    acct["suspended"] = True
            return self._send(200, {"primaryEmail": email, "suspended": acct["suspended"]}) if google else self._send(204)
        if method == "POST" and len(parts) == 2 and parts[1] in ("signOut", "revokeSignInSessions"):
            with LOCK:
                acct["signedOut"] = True
            return self._send(204) if google else self._send(200, {"value": True})
        return self._send(404, {"error": "not_found"})

    def _slack(self, method, f):
        auth = self.headers.get("Authorization", "")
        if auth != "Bearer xoxb-test-token":
            return self._send(200, {"ok": False, "error": "invalid_auth"})
        if method == "auth.test":
            return self._send(200, {"ok": True, "team_id": "T_DEMO", "team": "Demo Workspace", "user_id": "U_BOT"})
        if method == "users.lookupByEmail":
            email = f.get("email", "")
            if email.endswith("@demo.hr360"):
                return self._send(200, {"ok": True, "user": {"id": slack_user_for(email)}})
            return self._send(200, {"ok": False, "error": "users_not_found"})
        if method == "users.info":
            uid = f["user"]
            email = EMAILS.get(uid) or f"{uid[2:].lower()}.x@demo.hr360"
            return self._send(200, {"ok": True, "user": {"id": uid, "name": uid.lower(), "profile": {"email": email, "real_name": uid}}})
        if method == "conversations.open":
            return self._send(200, {"ok": True, "channel": {"id": "D_" + f["users"]}})
        if method == "chat.postMessage":
            SEQ[0] += 1
            return self._send(200, {"ok": True, "channel": f["channel"], "ts": f"1700000000.{SEQ[0]:06d}"})
        if method == "chat.update":
            return self._send(200, {"ok": True, "channel": f["channel"], "ts": f["ts"]})
        if method == "views.open":
            if not f.get("trigger_id"):
                return self._send(200, {"ok": False, "error": "invalid_trigger_id"})
            return self._send(200, {"ok": True, "view": {"id": "V_" + f["trigger_id"]}})
        if method == "views.publish":
            return self._send(200, {"ok": True, "view": {"id": "V_HOME_" + f.get("user_id", "")}})
        self._send(200, {"ok": False, "error": "unknown_method"})


if __name__ == "__main__":
    # Bilinen test kullanıcıları önceden kaydedilir (users.info e-postası için).
    for e in ("mehmet.demir@demo.hr360", "ayse.yilmaz@demo.hr360", "zeynep.kaya@demo.hr360"):
        slack_user_for(e)
    def syslog_udp():
        import socket
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.bind(("", 5514))
        while True:
            data, _ = sock.recvfrom(16384)
            with LOCK:
                SYSLOG.append(data.decode("utf-8", "replace"))
                del SYSLOG[:-5000]
    threading.Thread(target=syslog_udp, daemon=True).start()
    print("chatmock :8000 (syslog udp :5514)", flush=True)
    ThreadingHTTPServer(("", 8000), H).serve_forever()

"""Entegrasyon testlerinin ortak yardımcıları (HTTP, jetonlar, sahte sunucu kaydı)."""

import json
import subprocess
import time
import urllib.parse
import urllib.request
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "support"))
import hr360_login as _login  # noqa: E402

BASE = _login.BASE_URL
G = "/api/governance"
FAIL = []
AYSE = "0e879b9e-d72b-489f-aa5b-8291e0bcbefb"


def tok(w):
    return _login.token(w)


def http(method, path, body=None, headers=None, form=None, raw_body=None):
    h = dict(headers or {})
    data = None
    if raw_body is not None:
        data = raw_body.encode()
    elif form is not None:
        data = urllib.parse.urlencode(form).encode()
        h["Content-Type"] = "application/x-www-form-urlencoded"
    elif body is not None:
        data = json.dumps(body).encode()
        h["Content-Type"] = "application/json"
    req = urllib.request.Request(BASE + path, data=data, method=method, headers=h)
    try:
        with urllib.request.urlopen(req, timeout=30) as r:
            code, txt = r.status, r.read().decode()
    except urllib.error.HTTPError as e:
        code, txt = e.code, e.read().decode()
    try:
        return code, json.loads(txt) if txt else None
    except ValueError:
        return code, txt


def form_token(path, wait=True):
    """Herkese açık form jetonu (güvenlik dalgası 2B): GET path → {token, minSeconds}. Sunucu jetonu en
    erken minSeconds sonra kabul eder; wait=True ise o kadar beklenir. Jeton tek kullanımlıktır."""
    import time
    code, r = http("GET", path)
    if code != 200:
        return None
    if wait:
        time.sleep(float(r.get("minSeconds", 3)) + 0.3)
    return r["token"]


def check(name, cond, detail=""):
    print(("OK  " if cond else "XX  ") + name + ("" if cond else f"  -> {detail}"))
    if not cond:
        FAIL.append(name)


def api(who, method, path, body=None):
    return http(method, path, body, {"Authorization": "Bearer " + tok(who)})


def mock(path, method="GET"):
    out = subprocess.run(["docker", "exec", "hr360-gateway-1", "wget", "-qO-"] + (["--post-data", ""] if method == "POST" else [])
                         + [f"http://chatmock:8000{path}"], capture_output=True, text=True)
    return json.loads(out.stdout or "null")


def mock_calls(substr, since=0):
    return [c for c in mock("/_log") if substr in c["path"] and c["at"] >= since]


def wait_for(substr, pred=lambda c: True, since=0, timeout=40):
    end = time.time() + timeout
    while time.time() < end:
        hits = [c for c in mock_calls(substr, since) if pred(c)]
        if hits:
            return hits
        time.sleep(1)
    return []




def ensure_transfers(*providers):
    """KVKK m.9: yurt dışı hizmetler dayanak kaydı olmadan açılamaz; testler için standart sözleşme kaydı."""
    import datetime as _dt
    today = _dt.date.today().isoformat()
    for p in providers:
        code, _ = api("admin", "PUT", f"{G}/privacy/transfers/{p}",
                      {"mechanism": "StandardContract", "signedAt": today, "notifiedAt": today, "reference": "test"})
        if code != 200:
            raise SystemExit(f"aktarım dayanağı kaydedilemedi: {p} ({code})")

"""HR360 test kullanıcılarıyla tarayıcı üzerinden giriş ve erişim jetonu.

Kimlik bilgileri (sırayla):
  1. HR360_TEST_USERS ortam değişkeni (JSON: {"admin": ["demo.admin", "parola"], ...})
  2. tests/credentials.json (git'e girmez; örnek: tests/credentials.example.json)
Adres: HR360_BASE_URL (varsayılan http://localhost).

Jetonlar /tmp/hr360-tok-<kullanıcı>.json'da süresi dolana kadar önbelleklenir.
"""

import base64
import json
import os
import time
from pathlib import Path

BASE_URL = os.environ.get("HR360_BASE_URL", "http://localhost").rstrip("/")
_CRED_FILE = Path(__file__).resolve().parent.parent / "credentials.json"


def users() -> dict:
    raw = os.environ.get("HR360_TEST_USERS")
    if raw:
        return json.loads(raw)
    if _CRED_FILE.exists():
        return json.loads(_CRED_FILE.read_text())
    raise RuntimeError("Test kullanıcıları tanımlı değil: HR360_TEST_USERS ya da tests/credentials.json")


def login_page(page, who: str, path: str = "/panel") -> None:
    """Keycloak giriş ekranından (iki adımlı: kullanıcı adı, sonra parola) oturum açar."""
    user, pw = users()[who]
    page.goto(f"{BASE_URL}/giris", wait_until="networkidle")
    page.click("text=Kurumsal hesabımla giriş yap")
    page.wait_for_selector("input[name='username']", timeout=20000)
    page.fill("input[name='username']", user)
    page.click("input[type='submit'], button[type='submit']")
    page.wait_for_selector("input[name='password']", timeout=20000)
    page.fill("input[name='password']", pw)
    page.click("input[type='submit'], button[type='submit']")
    page.wait_for_url("**/panel**", timeout=30000)
    if path != "/panel":
        page.goto(BASE_URL + path, wait_until="networkidle")


def _exp(token: str) -> float:
    try:
        payload = token.split(".")[1]
        payload += "=" * (-len(payload) % 4)
        return float(json.loads(base64.urlsafe_b64decode(payload))["exp"])
    except Exception:  # noqa: BLE001
        return 0


def token(who: str, browser=None, min_valid: int = 60) -> str:
    """Geçerli erişim jetonu; yoksa ya da `min_valid` sn içinde dolacaksa tarayıcıyla yeniden giriş
    yapılır. Zaten açık bir Playwright tarayıcısı varsa (e2e testleri) `browser` ile verilir."""
    cache = Path(f"/tmp/hr360-tok-{who}.json")
    if cache.exists():
        t = json.loads(cache.read_text()).get("token", "")
        if t and _exp(t) - time.time() > min_valid:
            return t

    holder = {}

    def grab(b):
        ctx = b.new_context()
        page = ctx.new_page()

        def on_response(r):
            if "openid-connect/token" in r.url and r.request.method == "POST":
                try:
                    j = r.json()
                    if "access_token" in j:
                        holder["t"] = j["access_token"]
                except Exception:  # noqa: BLE001
                    pass

        page.on("response", on_response)
        login_page(page, who)
        page.wait_for_timeout(1000)
        ctx.close()

    # Servisler yeni başlatıldığında ilk giriş zaman aşımına uğrayabilir; birkaç kez denenir.
    for _attempt in range(3):
        try:
            if browser is not None:
                grab(browser)
            else:
                from playwright.sync_api import sync_playwright

                with sync_playwright() as p:
                    b = p.chromium.launch(headless=True, args=["--no-sandbox"])
                    grab(b)
                    b.close()
        except Exception:  # noqa: BLE001
            pass
        if holder.get("t"):
            break
        time.sleep(3)
    if not holder.get("t"):
        raise RuntimeError(f"{who} için jeton alınamadı")
    cache.write_text(json.dumps({"token": holder["t"]}))
    cache.chmod(0o600)
    return holder["t"]


if __name__ == "__main__":
    # Yük testi gibi araçlar için: python3 hr360_login.py ayse mehmet  -> {"ayse": "...", ...}
    import sys

    print(json.dumps({w: token(w, min_valid=270) for w in sys.argv[1:]}))

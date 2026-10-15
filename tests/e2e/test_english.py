"""İngilizce arayüz: dil seçimi saklanır, tüm ekranlar hatasız açılır ve arayüzde çevrilmemiş
metin kalmaz. Ad, pozisyon, kural adı gibi kullanıcı verileri Türkçe kalabileceği için kontrol,
görünen metinlerin sözlükteki Türkçe kaynak metinlerden biriyle birebir aynı olup olmadığına bakar
(çeviri sözlüğe girmediyse ya da koddaki metin tx() ile sarılmadıysa yakalanır)."""

import json
import os
import re

import pytest

from conftest import BASE_URL
from test_smoke import ROUTES

EN = json.load(open(os.path.join(os.path.dirname(__file__), "..", "..", "apps", "web", "src", "locales", "en.json"), encoding="utf-8"))
TR = re.compile("[çğıöşüÇĞİÖŞÜ]")
# Çevirisi kaynakla aynı olan (bot komutu, örnek veri) ve yer tutuculu anahtarlar hariç.
SOURCES = {k for k, v in EN.items() if not k.startswith("@server:") and "{" not in k and v != k and TR.search(k) and len(k) > 2}


def ui_texts(page):
    return page.evaluate("""() => [...document.querySelectorAll(
        'header nav a, header button, h1, h2, h3, th, label, button, [role=tab], [role=menuitem]')]
        .filter(e => e.offsetParent !== null).map(e => e.innerText.trim()).filter(Boolean)""")


def test_ingilizce_arayuz(session):
    page, watched = session("admin")
    page.goto(BASE_URL + "/panel?lang=en", wait_until="networkidle")
    assert page.evaluate("document.documentElement.lang") == "en"
    # Dil seçimi saklanır: parametresiz adreste de İngilizce kalır.
    page.goto(BASE_URL + "/panel/izin", wait_until="networkidle")
    assert page.evaluate("document.documentElement.lang") == "en"
    page.get_by_role("button", name="New leave request").first.wait_for(state="visible", timeout=10000)

    problems, turkish = {}, {}
    for route in ROUTES:
        page.goto(BASE_URL + route, wait_until="domcontentloaded")
        # networkidle karşılığı; SSE akışı yok sayılır (bkz. conftest.Watched.settle).
        watched.settle()
        errs = [e for e in watched.take() if "events/stream" not in e]
        if errs:
            problems[route] = errs
        left = sorted({t[:60] for t in ui_texts(page) if t in SOURCES or any(line.strip() in SOURCES for line in t.splitlines())})
        if left:
            turkish[route] = left
    assert not problems, "\n".join(f"{r}: {e}" for r, e in problems.items())
    assert not turkish, "Çevrilmemiş arayüz metni:\n" + "\n".join(f"{r}: {t}" for r, t in turkish.items())

    # Türkçeye dönüş
    page.goto(BASE_URL + "/panel?lang=tr", wait_until="networkidle")
    assert page.evaluate("document.documentElement.lang") == "tr"


# Ayşe'nin sunucudaki dil tercihini kısa süreliğine İngilizce yapar: aynı anda açılan başka bir Ayşe
# oturumu (boş localStorage) ya da o arada üretilen bildirim İngilizce olur. Paralel aşamada çalışmaz.
@pytest.mark.serial
def test_dil_tercihi_sunucuda_saklanir(browser):
    """Düğmeyle seçilen dil sunucuya yazılır; başka bir tarayıcıda (boş localStorage) aynı dil açılır.
    Bilerek saklanan oturum durumu (storage_state) KULLANILMAZ: her bağlam giriş ekranından açılır."""
    import time

    import hr360_login

    def wait_lang(page, want, timeout=15):
        # wait_for_function dize değerlendirir; uygulamanın CSP'si (unsafe-eval yok) buna izin vermez.
        end = time.time() + timeout
        while time.time() < end:
            try:
                if page.evaluate("document.documentElement.lang") == want:
                    return
            except Exception:  # noqa: BLE001 — sayfa yeniden yüklenirken
                pass
            time.sleep(0.3)
        raise AssertionError(f"dil {want} olmadı")

    def fresh():
        ctx = browser.new_context(viewport={"width": 1440, "height": 900}, locale="tr-TR")
        page = ctx.new_page()
        hr360_login.login_page(page, "ayse")
        page.wait_for_load_state("networkidle")
        return ctx, page

    try:
        ctx, page = fresh()
        page.evaluate("localStorage.setItem('hr360.lang', 'tr')")
        page.goto(BASE_URL + "/panel", wait_until="networkidle")
        page.get_by_role("button", name="Switch to English").click()
        wait_lang(page, "en")
        ctx.close()

        # Yeni tarayıcı: dil seçimi yok, sunucudaki tercih (en) uygulanır.
        ctx, page = fresh()
        wait_lang(page, "en")
        # Geri al
        page.get_by_role("button", name="Türkçeye geç").click()
        wait_lang(page, "tr")
        ctx.close()
    finally:
        # Test yarıda kalsa da Ayşe'nin sunucudaki tercihi Türkçeye döner (yoksa sonraki sohbet/bildirim
        # testleri İngilizce yanıt alır).
        import json as _json
        import urllib.request as _rq
        req = _rq.Request(hr360_login.BASE_URL + "/api/governance/me/language", method="PUT",
                          data=_json.dumps({"language": "tr"}).encode(),
                          headers={"Authorization": "Bearer " + hr360_login.token("ayse"), "Content-Type": "application/json"})
        try:
            _rq.urlopen(req, timeout=20).read()
        except Exception:  # noqa: BLE001 — geri alma en iyi çaba
            pass

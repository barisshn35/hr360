"""Herkese açık site sayfaları, çerez bilgilendirmesi ve arama motoru / paylaşım etiketleri (tarayıcıda).

- /sss, /gizlilik, /kosullar, /iletisim ve /tesekkurler oturumsuz açılır, hatasızdır.
- Giriş sayfasının alt bilgisindeki bağlantılar bu sayfalara götürür; gizlilikte çerez tablosu var.
- Çerez bandı ilk ziyarette görünür, "Anladım" ile kapanır ve yeniden yüklemede çıkmaz.
- index.html'de mutlak og:image; robots.txt ve sitemap.xml gateway'den döner; favicon.ico var.
"""

import re
import urllib.request

from conftest import BASE_URL, VIEWPORT


def _anon(browser, raw=False):
    make = browser.new_context_raw if raw else browser.new_context
    ctx = make(viewport=VIEWPORT, locale="tr-TR")
    return ctx, ctx.new_page()


def test_site_sayfalari_acilir_ve_hatasizdir(browser):
    ctx, page = _anon(browser)
    try:
        errors = []
        page.on("pageerror", lambda e: errors.append(str(e)))
        for path, test_id, heading in (
            ("/sss", "page-faq", "Sık sorulan sorular"),
            ("/gizlilik", "page-privacy", "Gizlilik politikası"),
            ("/kosullar", "page-terms", "Kullanım koşulları"),
            ("/iletisim", "page-contact", None),
            ("/tesekkurler?neden=kayit", "page-thanks", "Teşekkürler, kaydınız alındı"),
        ):
            page.goto(BASE_URL + path, wait_until="networkidle")
            page.get_by_test_id(test_id).wait_for(timeout=15000)
            if heading:
                page.get_by_role("heading", name=heading, level=1).wait_for(timeout=10000)
        # SSS: arama süzer, akordeon açılır.
        page.goto(BASE_URL + "/sss", wait_until="networkidle")
        page.fill("#faq-search", "parola")
        page.wait_for_timeout(400)
        assert page.get_by_role("button", name=re.compile("Parolamı unuttum")).count() == 1
        # Gizlilik: çerez bölümü ve tablosu her zaman var.
        page.goto(BASE_URL + "/gizlilik#cerezler", wait_until="networkidle")
        section = page.locator("#cerezler")
        section.wait_for(timeout=10000)
        assert section.get_by_text("KEYCLOAK_SESSION", exact=False).count() > 0
        assert not errors, errors
    finally:
        ctx.close()


def test_giris_alt_bilgisi_site_sayfalarina_goturur(browser):
    ctx, page = _anon(browser)
    try:
        page.goto(BASE_URL + "/giris", wait_until="networkidle")
        footer = page.get_by_test_id("site-footer")
        footer.wait_for(timeout=15000)
        for name, path, test_id in (("SSS", "/sss", "page-faq"), ("Gizlilik", "/gizlilik", "page-privacy"),
                                    ("Kullanım koşulları", "/kosullar", "page-terms"), ("İletişim", "/iletisim", "page-contact")):
            page.goto(BASE_URL + "/giris", wait_until="networkidle")
            page.get_by_test_id("site-footer").get_by_role("link", name=name, exact=True).click()
            page.get_by_test_id(test_id).wait_for(timeout=15000)
            assert page.url.split("#")[0].endswith(path), page.url
    finally:
        ctx.close()


def test_cerez_bandi_kapatilinca_bir_daha_cikmaz(browser):
    ctx, page = _anon(browser, raw=True)
    try:
        page.goto(BASE_URL + "/giris", wait_until="networkidle")
        notice = page.get_by_test_id("cookie-notice")
        notice.wait_for(state="visible", timeout=15000)
        assert notice.get_by_role("link", name="Ayrıntılar").get_attribute("href") == "/gizlilik#cerezler"
        page.get_by_test_id("cookie-notice-ok").click()
        notice.wait_for(state="detached", timeout=5000)
        assert page.evaluate("localStorage.getItem('hr360.cookieNotice')") == "1"
        page.reload(wait_until="networkidle")
        page.wait_for_timeout(1500)
        assert page.get_by_test_id("cookie-notice").count() == 0
        # Aynı köken: Keycloak giriş ekranında da çıkmaz.
        page.click("text=Kurumsal hesabımla giriş yap")
        page.wait_for_selector("input[name='username']", timeout=20000)
        page.wait_for_timeout(800)
        assert page.locator("#hr360-cookie").count() == 0
    finally:
        ctx.close()


def test_keycloak_giris_ekraninda_cerez_bandi(browser):
    ctx, page = _anon(browser, raw=True)
    try:
        page.goto(BASE_URL + "/giris", wait_until="networkidle")
        page.click("text=Kurumsal hesabımla giriş yap")
        page.wait_for_selector("input[name='username']", timeout=20000)
        page.locator("#hr360-cookie").wait_for(state="visible", timeout=10000)
        page.click("#hr360-cookie button")
        page.locator("#hr360-cookie").wait_for(state="detached", timeout=5000)
        assert page.evaluate("localStorage.getItem('hr360.cookieNotice')") == "1"
    finally:
        ctx.close()


def test_paylasim_etiketleri_robots_sitemap_favicon():
    html = urllib.request.urlopen(BASE_URL + "/giris", timeout=20).read().decode()
    og = re.search(r'<meta property="og:image" content="([^"]+)"', html)
    assert og and og.group(1) == BASE_URL + "/og-image.png", og and og.group(1)
    assert "__HR360_ORIGIN__" not in html
    assert 'name="twitter:card" content="summary_large_image"' in html
    with urllib.request.urlopen(BASE_URL + "/og-image.png", timeout=20) as r:
        assert r.headers.get("Content-Type") == "image/png"
        assert len(r.read()) < 300_000
    with urllib.request.urlopen(BASE_URL + "/robots.txt", timeout=20) as r:
        body = r.read().decode()
        assert r.headers.get("Content-Type", "").startswith("text/plain")
        assert "User-agent: *" in body
    with urllib.request.urlopen(BASE_URL + "/sitemap.xml", timeout=20) as r:
        assert "xml" in r.headers.get("Content-Type", "")
        assert "<urlset" in r.read().decode()
    with urllib.request.urlopen(BASE_URL + "/favicon.ico", timeout=20) as r:
        assert r.status == 200 and len(r.read()) > 0

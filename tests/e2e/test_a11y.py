"""Erişilebilirlik (G23, WCAG 2.1 AA): axe-core ile temel ekranlarda "serious"/"critical" ihlal olmamalı
(çalışan Ayşe ve İK yöneticisi admin, koyu ve açık tema); klavye ile gezinme (içeriğe atla, kısayollar),
sayfa başlığı, rota değişiminin duyurulması ve odağın ana başlığa taşınması, dil özniteliği.

axe-core npm paketinden okunur (apps/web devDependency: apps/web/node_modules/axe-core/axe.min.js;
yoksa `cd apps/web && npm install`). Uygulamanın CSP'si satır içi betiğe izin vermediği için test
bağlamı bypass_csp ile açılır ve axe page.add_script_tag(content=...) ile enjekte edilir.
Çalıştırma: python3 -m pytest -q tests/e2e/test_a11y.py
"""

import json
import os
import re

import pytest

from conftest import BASE_URL, logged_in_page

AXE_PATH = os.path.join(os.path.dirname(__file__), "..", "..", "apps", "web", "node_modules", "axe-core", "axe.min.js")
AXE = open(AXE_PATH, encoding="utf-8").read() if os.path.exists(AXE_PATH) else None

# Çalışanın (Ayşe) en sık kullandığı ekranlar.
PAGES = [
    "/panel",
    "/panel/onaylar",
    "/panel/izin",
    "/panel/masraf",
    "/panel/bildirimler",
    "/panel/profil",
    "/panel/profil?sekme=bildirimler",
    "/panel/profil?sekme=erisilebilirlik",
    "/panel/bordrolarim",
    "/panel/egitim",
    "/panel/takdir",
    "/panel/performans/benim",
]
LIGHT_PAGES = ["/panel", "/panel/izin", "/panel/profil?sekme=bildirimler", "/panel/bildirimler", "/panel/masraf"]
# İK yöneticisinin (admin) ana ekranları.
ADMIN_PAGES = [
    "/panel",
    "/panel/calisanlar",
    "/panel/onaylar",
    "/panel/izin",
    "/panel/organizasyon",
    "/panel/bordro",
    "/panel/ise-alim",
    "/panel/performans/donemler",
    "/panel/kvkk",
    "/panel/denetim",
    "/panel/duyurular",
    "/panel/analitik",
]
ADMIN_LIGHT_PAGES = ["/panel", "/panel/calisanlar", "/panel/kvkk"]
BLOCKING = {"serious", "critical"}


def settle(page):
    try:
        page.wait_for_load_state("networkidle", timeout=10000)
    except Exception:  # noqa: BLE001
        pass
    # İskelet yükleyiciler gitsin, giriş animasyonları bitsin.
    page.wait_for_timeout(1200)
    # Yük altında (paralel çalışma) 1,2 sn yetmeyebilir: biten (sonsuz olmayan) CSS/WAAPI animasyonu
    # kalmayana kadar en çok 5 sn daha beklenir; yarım kalmış bir solma renk kontrastını bozar.
    for _ in range(25):
        running = page.evaluate("""() => (document.getAnimations ? document.getAnimations() : [])
            .filter(a => a.playState === 'running' && a.effect && a.effect.getTiming().iterations !== Infinity).length""")
        if not running:
            break
        page.wait_for_timeout(200)


def axe_violations(page):
    if not page.evaluate("typeof window.axe !== 'undefined'"):
        page.add_script_tag(content=AXE)
    res = page.evaluate(
        """async () => {
            const r = await axe.run(document, {
              runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'] },
              resultTypes: ['violations'],
            });
            return r.violations.map(v => ({
              id: v.id, impact: v.impact, help: v.help,
              nodes: v.nodes.slice(0, 4).map(n => ({ target: n.target.join(' '), summary: (n.failureSummary || '').slice(0, 220) })),
              count: v.nodes.length,
            }));
        }"""
    )
    return [v for v in res if v["impact"] in BLOCKING]


def scan(page, routes):
    problems = {}
    for route in routes:
        page.goto(BASE_URL + route, wait_until="domcontentloaded")
        settle(page)
        bad = axe_violations(page)
        if bad:
            problems[route] = bad
    return problems


def fmt(problems):
    return "\n".join(f"{r}:\n" + "\n".join(f"  [{v['impact']}] {v['id']} ({v['count']}): {v['help']} :: "
                                           + json.dumps(v["nodes"], ensure_ascii=False) for v in vs)
                     for r, vs in problems.items())


@pytest.fixture
def a11y_session(browser, auth_state):
    """Oturum açmış sayfa; CSP atlanır (axe betiği enjekte edilebilsin), hareket azaltılmış."""
    contexts = []

    def make(who):
        if AXE is None:
            pytest.skip(f"axe-core yok: {AXE_PATH} (cd apps/web && npm install)")
        # Saklanan oturumla açılır (conftest.logged_in_page); giriş ekranı her testte tekrarlanmaz.
        ctx, page, _watched = logged_in_page(browser, auth_state, who, bypass_csp=True, reduced_motion="reduce")
        contexts.append(ctx)
        return page

    yield make
    for c in contexts:
        c.close()


@pytest.fixture
def ayse(a11y_session):
    return a11y_session("ayse")


@pytest.fixture
def admin(a11y_session):
    return a11y_session("admin")


def scan_themes(page, dark_pages, light_pages):
    problems = {f"[koyu] {k}": v for k, v in scan(page, dark_pages).items()}
    page.evaluate("localStorage.setItem('hr360.theme', 'light')")
    try:
        problems.update({f"[açık] {k}": v for k, v in scan(page, light_pages).items()})
    finally:
        page.evaluate("localStorage.removeItem('hr360.theme')")
    return problems


def test_axe_calisan(ayse):
    problems = scan_themes(ayse, PAGES, LIGHT_PAGES)
    assert not problems, fmt(problems)


def test_axe_ik_yoneticisi(admin):
    problems = scan_themes(admin, ADMIN_PAGES, ADMIN_LIGHT_PAGES)
    assert not problems, fmt(problems)


def test_rota_degisimi_duyurulur_ve_odak_basliga_tasinir(ayse):
    ayse.goto(BASE_URL + "/panel", wait_until="domcontentloaded")
    settle(ayse)
    # Menü/komut paleti yerine klavye kısayoluyla gezin (gerçek kullanıcı yolu).
    ayse.locator("body").click(position={"x": 5, "y": 300})
    ayse.keyboard.type("g")
    ayse.keyboard.type("i")
    ayse.wait_for_url(re.compile(r".*/panel/izin$"), timeout=5000)
    live = ayse.get_by_test_id("route-announcer")
    assert live.get_attribute("aria-live") == "polite"
    ayse.wait_for_function("document.querySelector('[data-testid=route-announcer]').textContent.includes('sayfası açıldı')", timeout=5000)
    text = live.text_content()
    assert "İzin" in text, text
    ayse.wait_for_function("document.activeElement && document.activeElement.tagName === 'H1'", timeout=5000)
    assert "İzin" in ayse.evaluate("document.activeElement.textContent")


def test_klavye_icerige_atla_baslik_dil(ayse):
    ayse.goto(BASE_URL + "/panel/izin", wait_until="domcontentloaded")
    settle(ayse)
    assert ayse.evaluate("document.documentElement.lang") == "tr"
    assert re.search(r"İzin.*HR360", ayse.title()), ayse.title()
    assert ayse.locator("header").count() >= 1 and ayse.locator("main#main-content").count() == 1
    assert ayse.locator("nav").count() >= 1
    assert ayse.locator("h1").count() >= 1
    # İlk Tab "içeriğe atla" bağlantısına gelir ve görünür olur; Enter odağı ana içeriğe taşır.
    ayse.evaluate("document.activeElement && document.activeElement.blur()")
    ayse.keyboard.press("Tab")
    focused = ayse.evaluate("({ text: document.activeElement.textContent, href: document.activeElement.getAttribute('href') })")
    assert focused["href"] == "#main-content", focused
    box = ayse.locator("a[href='#main-content']").bounding_box()
    assert box and box["width"] > 20 and box["height"] > 10, box
    ayse.keyboard.press("Enter")
    assert ayse.evaluate("document.activeElement.id") == "main-content"


def test_klavye_kisayollari(ayse):
    ayse.goto(BASE_URL + "/panel", wait_until="domcontentloaded")
    settle(ayse)
    ayse.locator("body").click(position={"x": 5, "y": 300})
    # ? → kısayol yardımı
    ayse.keyboard.type("?")
    dlg = ayse.get_by_role("dialog", name="Klavye kısayolları")
    dlg.wait_for(timeout=5000)
    ayse.keyboard.press("Escape")
    dlg.wait_for(state="detached", timeout=5000)
    # g ardından i → İzin
    ayse.keyboard.type("g")
    ayse.keyboard.type("i")
    ayse.wait_for_url(re.compile(r".*/panel/izin$"), timeout=5000)
    # g ardından p → Profil
    ayse.keyboard.type("g")
    ayse.keyboard.type("p")
    ayse.wait_for_url(re.compile(r".*/panel/profil$"), timeout=5000)
    # / → komut paleti (genel arama)
    ayse.keyboard.type("/")
    ayse.get_by_role("dialog").first.wait_for(timeout=5000)
    ayse.keyboard.press("Escape")
    ayse.wait_for_timeout(400)
    # Yazı alanındayken kısayollar çalışmaz.
    ayse.goto(BASE_URL + "/panel/profil", wait_until="domcontentloaded")
    settle(ayse)
    field = ayse.get_by_label("Hakkımda")
    field.click()
    ayse.keyboard.type("gi?")
    ayse.wait_for_timeout(600)
    assert ayse.url.endswith("/panel/profil"), ayse.url
    assert ayse.get_by_role("dialog").count() == 0
    # Profilim › Erişilebilirlik'ten kapatılınca çalışmaz.
    ayse.goto(BASE_URL + "/panel/profil?sekme=erisilebilirlik", wait_until="domcontentloaded")
    settle(ayse)
    box = ayse.get_by_role("checkbox", name="Klavye kısayollarını kullan")
    assert box.get_attribute("aria-checked") == "true"
    box.click()
    try:
        ayse.locator("body").click(position={"x": 5, "y": 300})
        ayse.keyboard.type("gi")
        ayse.wait_for_timeout(600)
        assert "/panel/izin" not in ayse.url, ayse.url
    finally:
        ayse.evaluate("localStorage.removeItem('hr360.shortcuts')")


def test_bildirim_tercihleri_paneli(ayse):
    ayse.goto(BASE_URL + "/panel/profil?sekme=bildirimler", wait_until="domcontentloaded")
    settle(ayse)
    locked = ayse.get_by_role("checkbox", name=re.compile(r"Bordro ve ücret – Uygulama içi"))
    assert locked.is_disabled() and locked.get_attribute("aria-checked") == "true"
    assert ayse.get_by_role("checkbox", name="Duyurular ve etkileşim – E-posta").is_enabled()
    assert ayse.get_by_text("Sessiz saatler", exact=True).count() >= 1
    assert ayse.get_by_text("Günlük özet", exact=True).count() >= 1

"""Açılış ekranı, hata ekranları ve giriş sayfasındaki entegrasyon logo şeridi (tarayıcıda).

- JS öncesi açılış ekranı (#hr-splash) uygulama yüklenince kaldırılır.
- /giris'te "Bağlanabildiği uygulamalar" şeridi görünür, logolar yerel dosyadan yüklenir.
- Panel dışındaki bilinmeyen adres girişe atmaz, tam ekran 404 gösterir; panel içi 404 kabukta.
- Yetkisiz ekran 403, oturum sona erdi ekranı, çevrimdışı ve bakım sayfaları.
"""

import urllib.error
import urllib.request

from conftest import BASE_URL, VIEWPORT


def _anon(browser):
    ctx = browser.new_context(viewport=VIEWPORT, locale="tr-TR")
    return ctx, ctx.new_page()


def test_acilis_ekrani_kalkar_ve_logo_seridi_gorunur(browser):
    ctx, page = _anon(browser)
    try:
        errors = []
        page.on("pageerror", lambda e: errors.append(str(e)))
        page.goto(BASE_URL + "/giris", wait_until="networkidle")
        page.wait_for_selector("#hr-splash", state="detached", timeout=15000)
        strip = page.get_by_test_id("integrations-marquee")
        strip.wait_for(timeout=15000)
        # Başlık CSS ile büyük harfe çevrilir; erişilebilir ad özgün metindir.
        assert strip.get_by_role("region", name="Bağlanabildiği uygulamalar").count() == 1
        for name in ("Slack", "Microsoft Teams", "Okta", "Zapier", "n8n", "Ollama", "Webhook"):
            assert strip.get_by_text(name, exact=True).count() > 0, name
        # Logolar yerel dosyadan ve hepsi yüklenmiş olmalı (CSP: yalnızca aynı köken).
        page.wait_for_function(
            """() => [...document.querySelectorAll('[data-testid=integrations-marquee] img')]
                 .every(i => i.complete && i.naturalWidth > 0)""",
            timeout=15000,
        )
        srcs = page.eval_on_selector_all("[data-testid=integrations-marquee] img", "els => els.map(e => e.src)")
        assert srcs and all(s.startswith(BASE_URL + "/integrations/") for s in srcs)
        assert not errors, errors
    finally:
        ctx.close()


def test_panel_disi_bilinmeyen_adres_404(browser):
    ctx, page = _anon(browser)
    try:
        page.goto(BASE_URL + "/boyle-bir-sayfa-yok", wait_until="networkidle")
        page.get_by_test_id("not-found").wait_for(timeout=15000)
        assert "/boyle-bir-sayfa-yok" in page.url
        assert page.get_by_role("heading", name="Sayfa bulunamadı").count() == 1
    finally:
        ctx.close()


def test_panel_ici_404_ve_403(session):
    page, _ = session("ayse")
    page.goto(BASE_URL + "/panel/boyle-bir-ekran-yok", wait_until="domcontentloaded")
    page.get_by_test_id("not-found").wait_for(timeout=15000)
    assert page.url.endswith("/panel/404")
    # Çalışan rolü denetim kaydını göremez.
    page.goto(BASE_URL + "/panel/denetim", wait_until="domcontentloaded")
    page.get_by_test_id("forbidden").wait_for(timeout=15000)
    assert page.get_by_role("heading", name="Bu bölüme erişiminiz yok").count() == 1


def test_oturum_sona_erdi_ekrani(browser):
    ctx, page = _anon(browser)
    try:
        page.goto(BASE_URL + "/oturum-sona-erdi?devam=%2Fpanel%2Fizin", wait_until="networkidle")
        page.get_by_test_id("session-expired").wait_for(timeout=15000)
        assert page.get_by_role("button", name="Yeniden giriş yap").count() == 1
    finally:
        ctx.close()


def test_statik_cevrimdisi_sayfasi():
    with urllib.request.urlopen(BASE_URL + "/offline.html", timeout=15) as r:
        body = r.read().decode()
    assert r.status == 200 and "İnternet bağlantısı yok" in body and "<script" not in body


def test_bakim_sayfasi_disaridan_acilmaz():
    # Yalnızca gateway'in 502/503/504 yanıtında iç yönlendirmeyle sunulur.
    try:
        with urllib.request.urlopen(BASE_URL + "/_hr360/bakim.html", timeout=15) as r:
            body = r.read().decode()
        assert "Kısa bir ara veriyoruz" not in body
    except urllib.error.HTTPError as e:
        assert e.code == 404

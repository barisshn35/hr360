"""Tarayıcı uçtan uca testleri (Playwright, pytest).

Çalıştırma:  scripts/test.sh e2e            (paralel: önce paralel testler, sonra "serial" işaretliler)
             python3 -m pytest -q tests/e2e  (sıralı; pytest-xdist gerekmez)
             python3 -m pytest -q -n 4 -m "not serial" tests/e2e && python3 -m pytest -q -m serial tests/e2e
Gereken: çalışan bir HR360 (HR360_BASE_URL), test kullanıcıları (tests/credentials.json
ya da HR360_TEST_USERS), `pip install pytest playwright pytest-xdist` ve `playwright install chromium`.

Hızlandırma (dalga 12):
- Oturum durumu (çerezler + localStorage) rol başına BİR kez gerçek giriş ekranından alınır ve
  `storage_state` olarak saklanır; sonraki bağlamlar giriş yapmadan açılır. Dosya bu çalıştırmanın
  pytest geçici dizinindedir (0700), xdist işçileri arasında kilitle paylaşılır, oturum sonunda silinir.
  Saklanan oturum geçersizse (Keycloak oturumu düştüyse) o bağlamda yeniden giriş yapılır.
- `Watched.settle()`: Playwright'ın "networkidle" beklemesinin uzun ömürlü akışları (SSE: olay radarı
  vb.) yok sayan karşılığı. Eskiden bu ekranlarda 10 sn zaman aşımı + 1,5 sn bekleme oluyordu.
- Paralel çalıştırma güvenliği: testler farklı kullanıcılarla ya da rastgele etiketli kayıtlarla çalışır.
  Kullanıcının SUNUCUDAKİ ortak durumunu değiştiren testler (ör. dil tercihi) `@pytest.mark.serial`
  ile işaretlenir ve paralel aşamadan sonra tek başına çalışır (scripts/test.sh).
"""

import fcntl
import os
import sys
import time
from pathlib import Path

import pytest
from playwright.sync_api import sync_playwright

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "support"))
import hr360_login  # noqa: E402

BASE_URL = hr360_login.BASE_URL
VIEWPORT = {"width": 1440, "height": 900}
# Uzun ömürlü akışlar: ağ hiçbir zaman "boşta" olmaz, beklemede sayılmaz.
STREAM_MARKERS = ("events/stream",)


def pytest_configure(config):
    config.addinivalue_line("markers", "serial: kullanıcının sunucudaki ortak durumunu değiştirir; paralel aşamada çalışmaz")


# Uzun süren testler (tüm ekranları gezenler) önce başlar: paralel çalışmada en uzun iş sona kalıp
# toplam süreyi uzatmasın. Sıralı çalışmada sonuca etkisi yoktur; testler birbirinden bağımsızdır.
LONG_TESTS = ("test_tum_ekranlar_hatasiz", "test_ingilizce_arayuz", "test_axe_")


def pytest_collection_modifyitems(config, items):
    items.sort(key=lambda it: next((i for i, p in enumerate(LONG_TESTS) if it.name.startswith(p)), len(LONG_TESTS)))


def pytest_sessionfinish(session, exitstatus):
    # Saklanan oturum çerezleri yalnızca bu çalıştırma içindir: ana süreç (xdist denetleyicisi ya da
    # tek süreç) bitişte siler.
    if os.environ.get("PYTEST_XDIST_WORKER"):
        return
    factory = getattr(session.config, "_tmp_path_factory", None)
    if factory is None:
        return
    try:
        for f in Path(factory.getbasetemp()).glob("**/hr360-auth-*.json"):
            f.unlink(missing_ok=True)
    except Exception:  # noqa: BLE001
        pass


# Çerez bilgilendirme bandı (giriş/herkese açık sayfalar ve Keycloak teması) sayfanın altındaki
# öğelerin üstüne gelebilir: testlerin bağlamlarında "kapatıldı" olarak işaretlenir. Bandın kendisini
# sınayan test ham bağlamı kullanır (browser.new_context_raw).
COOKIE_ACK_SCRIPT = "try { localStorage.setItem('hr360.cookieNotice', '1') } catch (e) {}"


@pytest.fixture(scope="session")
def browser():
    with sync_playwright() as p:
        b = p.chromium.launch(headless=True, args=["--no-sandbox"])
        raw = b.new_context

        def new_context(*args, **kwargs):
            ctx = raw(*args, **kwargs)
            ctx.add_init_script(COOKIE_ACK_SCRIPT)
            return ctx

        b.new_context_raw = raw
        b.new_context = new_context
        yield b
        b.close()


class Watched:
    """Sayfa hatalarını, konsol hatalarını ve başarısız API yanıtlarını toplar; süren istekleri izler."""

    def __init__(self, page):
        self.page = page
        self.errors: list[str] = []
        self.inflight: dict = {}  # istek -> başlangıç (monotonic)
        page.on("pageerror", lambda e: self.errors.append(f"PAGEERROR {e}"))
        page.on("console", lambda m: self.errors.append(f"CONSOLE {m.text[:200]}") if m.type == "error" else None)
        page.on("response", lambda r: self.errors.append(f"HTTP {r.status} {r.request.method} {r.url.replace(BASE_URL, '')[:120]}")
                if r.status >= 400 and ("/api/" in r.url or "/ml/" in r.url) else None)
        page.on("request", self._started)
        page.on("requestfinished", lambda r: self.inflight.pop(r, None))
        page.on("requestfailed", lambda r: self.inflight.pop(r, None))

    def _started(self, req):
        if req.resource_type == "document" and req.frame == self.page.main_frame:
            # Yeni belge (page.goto): önceki sayfanın istekleri bu sayfayı ilgilendirmez. Paralel
            # çalışmada iptal edilen bir isteğin "requestfailed" olayı bazen hiç gelmiyor; temizlenmezse
            # sonraki her ekranda bekleme zaman aşımına kadar uzuyordu.
            self.inflight.clear()
        if req.resource_type != "eventsource" and not any(m in req.url for m in STREAM_MARKERS):
            self.inflight[req] = time.monotonic()

    def busy(self, stale: float = 8.0) -> bool:
        """Süren (akış dışı) istek var mı? `stale` sn'den uzun süren istek takılmış sayılır, beklenmez."""
        now = time.monotonic()
        return any(now - t < stale for t in self.inflight.values())

    def take(self) -> list[str]:
        out, self.errors = self.errors, []
        return out

    def settle(self, timeout: float = 10.0, quiet: float = 0.5) -> bool:
        """Akışlar dışındaki tüm istekler bitip `quiet` sn boyunca yeni istek başlamayana kadar bekler
        (networkidle ile aynı ölçüt, SSE hariç). Zaman aşımında False döner, hata vermez."""
        end = time.monotonic() + timeout
        idle_since = None
        while time.monotonic() < end:
            if self.busy():
                idle_since = None
            elif idle_since is None:
                idle_since = time.monotonic()
            elif time.monotonic() - idle_since >= quiet:
                return True
            self.page.wait_for_timeout(100)  # olay döngüsünü işletir
        return False


def _state_dir(tmp_path_factory) -> Path:
    # xdist'te her işçinin geçici dizini ortak bir üst dizinin altındadır (popen-gwN).
    base = tmp_path_factory.getbasetemp()
    return base.parent if os.environ.get("PYTEST_XDIST_WORKER") else base


def _logged_in(page, timeout: float = 30.0) -> bool:
    """Panel kabuğu (içeriğe atla bağlantısı) görünürse True; giriş ekranına düşerse False."""
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        if "/giris" in page.url or "/realms/" in page.url:
            return False
        if page.locator("a[href='#main-content']").count() > 0:
            return True
        page.wait_for_timeout(200)
    return False


@pytest.fixture(scope="session")
def auth_state(browser, tmp_path_factory):
    """auth_state('ayse') -> storage_state dosyası. Rol başına bir kez gerçek giriş yapılır; xdist
    işçileri dosyayı kilitle paylaşır. Dosyayı yenilemek için auth_state('ayse', refresh=True)."""
    root = _state_dir(tmp_path_factory)

    def get(who: str, refresh: bool = False) -> str:
        path = root / f"hr360-auth-{who}.json"
        with open(root / f"hr360-auth-{who}.lock", "w") as lock:
            fcntl.flock(lock, fcntl.LOCK_EX)
            if refresh or not path.exists():
                ctx = browser.new_context(viewport=VIEWPORT, locale="tr-TR")
                try:
                    page = ctx.new_page()
                    hr360_login.login_page(page, who)
                    page.wait_for_load_state("networkidle")
                    ctx.storage_state(path=str(path))
                finally:
                    ctx.close()
                path.chmod(0o600)
        return str(path)

    return get


def logged_in_page(browser, auth_state, who: str, path: str = "/panel", **ctx_opts):
    """Saklanan oturumla bağlam + sayfa açar; oturum geçersizse yeniden giriş yapar. (ctx, page, watched)"""
    opts = {"viewport": VIEWPORT, "locale": "tr-TR", **ctx_opts}
    for attempt in range(2):
        ctx = browser.new_context(storage_state=auth_state(who, refresh=attempt > 0), **opts)
        page = ctx.new_page()
        watched = Watched(page)
        page.goto(BASE_URL + path, wait_until="domcontentloaded")
        if _logged_in(page):
            watched.settle()
            watched.take()
            return ctx, page, watched
        ctx.close()
    # Son çare: eski yol (giriş ekranından doğrudan giriş).
    ctx = browser.new_context(**opts)
    page = ctx.new_page()
    watched = Watched(page)
    hr360_login.login_page(page, who, path)
    page.wait_for_load_state("networkidle")
    watched.take()
    return ctx, page, watched


@pytest.fixture
def session(browser, auth_state):
    """Kullanıcı adına oturum açmış sayfa üretir: session('ayse') -> (page, watched)."""
    contexts = []

    def make(who: str):
        ctx, page, watched = logged_in_page(browser, auth_state, who)
        contexts.append(ctx)
        return page, watched

    yield make
    for c in contexts:
        c.close()


def users():
    return hr360_login.users()

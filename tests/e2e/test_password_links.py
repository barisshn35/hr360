"""Süreli parola linki ve Sistem ayarları ekranları (tarayıcıda).

- Yönetici çalışana giriş erişimi verir → link API yanıtında döner; link oturumsuz açılır, parola
  formla belirlenir, aynı link ikinci kez "kullanılmış" ekranı gösterir.
- Geçersiz link ve "parolamı unuttum" (e-posta kapalıyken "İK yöneticinize başvurun") ekranları.
- Platform yöneticisi Sistem ayarlarını, şirket yöneticisi Davetler listesini ve parola yöntemini görür.
Test çalışanı ve Keycloak hesabı sonda silinir.
"""

import json
import os
import random
import string
import subprocess
import sys
import urllib.request

from conftest import BASE_URL

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "support"))
import hr360_login  # noqa: E402

ROOT = os.path.join(os.path.dirname(__file__), "..", "..")


def _api(browser, who, method, path, body=None):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(BASE_URL + path, data=data, method=method, headers={
        "Authorization": "Bearer " + hr360_login.token(who, browser), "Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=30) as r:
        txt = r.read().decode()
        return json.loads(txt) if txt else None


def _cleanup(employee_id, kc_user):
    def psql(sql):
        subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", "hr360_operational", "-Atc", sql],
                       capture_output=True, text=True, cwd=ROOT)
    if kc_user:
        psql(f"""DELETE FROM tenant_password_invites WHERE "KeycloakUserId" = '{kc_user}'""")
        script = ('/opt/keycloak/bin/kcadm.sh config credentials --config /tmp/kc-e2e.cfg --server http://localhost:8080/auth '
                  '--realm master --user "$KEYCLOAK_ADMIN" --password "$KEYCLOAK_ADMIN_PASSWORD" >/dev/null 2>&1 && '
                  f'/opt/keycloak/bin/kcadm.sh delete users/{kc_user} --config /tmp/kc-e2e.cfg -r hr360')
        subprocess.run(["docker", "exec", "hr360-keycloak-1", "sh", "-c", script], capture_output=True, text=True)
    if employee_id:
        psql(f"""DELETE FROM notification_messages WHERE "RecipientEmployeeId" = '{employee_id}';
                 DELETE FROM employee_employees WHERE "Id" = '{employee_id}'""")


def test_davet_linkiyle_parola_belirleme(browser):
    run = "".join(random.choices(string.ascii_lowercase + string.digits, k=6))
    emp = _api(browser, "admin", "POST", "/api/employee/employees", {
        "firstName": "TEST", "lastName": f"E2E Davet {run}", "email": f"test.e2edavet.{run}@demo.hr360", "hireDate": "2024-01-02"})
    kc_user = None
    try:
        inv = _api(browser, "admin", "POST", f"/api/tenant/my-tenant/members/{emp['id']}/invite")
        kc_user = inv["keycloakUserId"]
        link = inv["passwordLink"]["link"]
        assert link and "/parola-belirle/" in link

        ctx = browser.new_context(locale="tr-TR")
        page = ctx.new_page()
        errors = []
        page.on("pageerror", lambda e: errors.append(str(e)))
        page.goto(link, wait_until="networkidle")
        page.get_by_role("heading", name="Parolanızı belirleyin").wait_for(state="visible", timeout=15000)
        pw = "E2e-" + "".join(random.choices(string.ascii_letters + string.digits, k=14)) + "!9"
        page.fill("#pw", "kisa")
        page.fill("#pw2", "kisa")
        page.get_by_role("button", name="Parolayı kaydet").click()
        page.get_by_role("alert").first.wait_for(state="visible", timeout=10000)
        page.fill("#pw", pw)
        page.fill("#pw2", pw)
        page.get_by_role("button", name="Parolayı kaydet").click()
        # Parola belirlenince teşekkür sayfasına yönlenir (sonraki adımlar + giriş düğmesi).
        page.locator("[data-testid='page-thanks'][data-reason='parola']").wait_for(state="visible", timeout=20000)
        assert "/tesekkurler" in page.url
        page.goto(link, wait_until="networkidle")
        page.get_by_test_id("password-state-used").wait_for(state="visible", timeout=15000)
        page.goto(BASE_URL + "/parola-belirle/" + "x" * 43, wait_until="networkidle")
        page.get_by_test_id("password-state-invalid").wait_for(state="visible", timeout=15000)
        assert not errors, errors
        ctx.close()
    finally:
        _cleanup(emp["id"], kc_user)


def test_parolami_unuttum_sayfasi(browser):
    cfg = json.loads(urllib.request.urlopen(BASE_URL + "/api/tenant/public/config", timeout=15).read())
    ctx = browser.new_context(locale="tr-TR")
    page = ctx.new_page()
    page.goto(BASE_URL + "/parola-sifirla", wait_until="networkidle")
    if cfg.get("passwordResetByEmail"):
        page.get_by_role("button", name="Sıfırlama linki gönder").wait_for(state="visible", timeout=15000)
    else:
        page.get_by_test_id("password-state-admin").wait_for(state="visible", timeout=15000)
    ctx.close()


def test_sistem_ayarlari_ve_davetler_ekrani(session):
    page, watched = session("platform")
    page.goto(BASE_URL + "/panel/platform/sistem", wait_until="domcontentloaded")
    watched.settle()
    page.get_by_role("heading", name="E-posta sunucusu (SMTP)").wait_for(state="visible", timeout=15000)
    page.get_by_role("switch", name="Herkese açık şirket kaydı").wait_for(state="visible")
    assert not watched.take()

    apage, awatched = session("admin")
    apage.goto(BASE_URL + "/panel/roller", wait_until="domcontentloaded")
    awatched.settle()
    apage.get_by_role("heading", name="Davetler ve parola linkleri").wait_for(state="visible", timeout=15000)
    apage.goto(BASE_URL + "/panel/ayarlar", wait_until="domcontentloaded")
    awatched.settle()
    apage.get_by_role("heading", name="Parola belirleme yöntemi").wait_for(state="visible", timeout=15000)
    assert not awatched.take()

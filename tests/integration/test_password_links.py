"""Süreli parola linkleri ve Sistem ayarları (E-posta C).

- Davet: giriş erişimi verilince HR360'ın tek kullanımlık, süreli linki yöneticiye döner; link
  durumları (açık / kullanılmış / iptal / süresi dolmuş / geçersiz); parola politikası hatası linki
  kapatmaz; yeniden üretme eskisini iptal eder.
- Yetki: çalışan liste göremez; İK yöneticisi şirket yöneticisinin linkini üretemez; Sistem
  ayarları yalnızca platform yöneticisinde.
- E-posta: SMTP yokken "parolamı unuttum" e-posta göndermez; platform SMTP'si arayüzden
  (Mailpit'e yönlendirilerek) ayarlanınca davet ve sıfırlama linki e-postayla gider, linkle parola
  belirlenir; eski yöntem (Keycloak e-postası) seçilebilir. Sonda ayarlar eski haline döner.

E-postalar yalnızca paketteki Mailpit'e gider (HR360_MAILPIT_URL, varsayılan http://127.0.0.1:8025).
Test çalışanı ve Keycloak hesabı sonda silinir.
"""

import json
import os
import random
import re
import string
import subprocess
import sys
import time
import urllib.request

sys.path.insert(0, os.path.dirname(__file__))
from common import FAIL, api, check, http  # noqa: E402

ROOT = os.path.join(os.path.dirname(__file__), "..", "..")
MAILPIT = os.environ.get("HR360_MAILPIT_URL", "http://127.0.0.1:8025")
RUN = "".join(random.choices(string.ascii_lowercase + string.digits, k=6))
EMAIL = f"test.davet.{RUN}@demo.hr360"
T = "/api/tenant"
FAIL.clear()


def psql(sql, db="hr360_operational"):
    out = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", db, "-Atc", sql],
                         capture_output=True, text=True, cwd=ROOT)
    return (out.stdout + out.stderr).strip()


def kcadm(*args):
    script = ('/opt/keycloak/bin/kcadm.sh config credentials --config /tmp/kc-pwl.cfg --server http://localhost:8080/auth '
              '--realm master --user "$KEYCLOAK_ADMIN" --password "$KEYCLOAK_ADMIN_PASSWORD" >/dev/null 2>&1 && '
              '/opt/keycloak/bin/kcadm.sh ' + " ".join(args) + ' --config /tmp/kc-pwl.cfg -r hr360')
    return subprocess.run(["docker", "exec", "hr360-keycloak-1", "sh", "-c", script], capture_output=True, text=True)


def mails_to(addr):
    q = urllib.request.quote(f"to:{addr}")
    with urllib.request.urlopen(f"{MAILPIT}/api/v1/search?query={q}&limit=20", timeout=10) as r:
        return json.loads(r.read())["messages"]


def mail_text(mid):
    with urllib.request.urlopen(f"{MAILPIT}/api/v1/message/{mid}", timeout=10) as r:
        m = json.loads(r.read())
    return (m.get("Text") or "") + "\n" + (m.get("HTML") or "")


def wait_mail(addr, pred, timeout=90):
    end = time.time() + timeout
    while time.time() < end:
        for m in mails_to(addr):
            if pred(m):
                return m
        time.sleep(3)
    return None


def token_of(link):
    return link.rstrip("/").rsplit("/", 1)[-1]


def strong_pw():
    return "Tst-" + "".join(random.choices(string.ascii_letters + string.digits, k=14)) + "!7"


created = {"employee": None, "kc_user": None}
_, before = api("platform", "GET", f"{T}/platform/settings")
try:
    # ---------------------------------------------------------------- yetkiler
    code, _ = api("ayse", "GET", f"{T}/platform/settings")
    check("Sistem ayarları: çalışan göremez", code == 403, code)
    code, _ = api("admin", "GET", f"{T}/platform/settings")
    check("Sistem ayarları: şirket yöneticisi göremez", code == 403, code)
    check("Sistem ayarları: platform yöneticisi görür", isinstance(before, dict) and "smtp" in before, before)
    code, _ = api("ayse", "GET", f"{T}/my-tenant/password-links")
    check("Parola linkleri: çalışan listeyi göremez", code == 403, code)

    # Başlangıç: platform SMTP'si test kutusu (gerçek gönderim yok).
    code, s = api("platform", "PUT", f"{T}/platform/settings/smtp/mode", {"mode": "mailpit"})
    check("SMTP test kutusu kipine alınır", code == 200 and s["smtp"]["mode"] == "mailpit" and not s["smtp"]["configured"], (code, s))
    time.sleep(16)  # ayar önbelleği (15 sn)
    code, cfg = http("GET", f"{T}/public/config")
    check("Herkese açık ayar: SMTP yokken parolamı unuttum e-postayla değil", code == 200 and cfg.get("passwordResetByEmail") is False, (code, cfg))

    # ---------------------------------------------------------------- davet linki
    code, emp = api("admin", "POST", "/api/employee/employees", {"firstName": "TEST", "lastName": f"Davet {RUN}",
                    "email": EMAIL, "hireDate": "2024-01-02"})
    check("Hazırlık: test çalışanı", code == 201, (code, emp))
    created["employee"] = emp["id"]
    code, inv = api("admin", "POST", f"{T}/my-tenant/members/{emp['id']}/invite")
    link = (inv or {}).get("passwordLink") or {}
    created["kc_user"] = (inv or {}).get("keycloakUserId")
    check("Giriş erişimi: süreli davet linki yöneticiye döner", code == 200 and link.get("method") == "invite"
          and re.match(r"^https?://.+/parola-belirle/[A-Za-z0-9_-]{43}$", link.get("link") or "") is not None, (code, inv))
    check("SMTP yokken e-posta gitmez", link.get("emailStatus") == "none", link)
    tok1 = token_of(link["link"])
    raw = psql(f"""SELECT "TokenHash" FROM tenant_password_invites WHERE "Id" = '{link['inviteId']}'""")
    check("Token veritabanında düz metin tutulmaz (yalnızca SHA-256 özeti)", len(raw) == 64 and tok1 not in raw, raw)

    code, st = http("GET", f"{T}/public/password/link/{tok1}")
    check("Link durumu: açık, maskeli e-posta ve şirket", code == 200 and st["status"] == "active" and st["purpose"] == "setup"
          and st["email"].startswith("te***@") and st.get("company"), (code, st))
    code, st = http("GET", f"{T}/public/password/link/{'x' * 43}")
    check("Uydurma link: geçersiz", code == 200 and st["status"] == "invalid", (code, st))

    code, r = http("POST", f"{T}/public/password/link/{tok1}", {"password": "kisa"})
    check("Parola politikası reddeder, link açık kalır", code == 400 and r.get("status") == "active" and r.get("code"), (code, r))
    pw = strong_pw()
    code, r = http("POST", f"{T}/public/password/link/{tok1}", {"password": pw})
    check("Linkle parola belirlenir", code == 200 and r.get("status") == "used", (code, r))
    creds = kcadm("get", f"users/{created['kc_user']}/credentials").stdout
    user = json.loads(kcadm("get", f"users/{created['kc_user']}").stdout or "{}")
    check("Keycloak: parola kaydı var, UPDATE_PASSWORD gereksinimi kalktı",
          '"password"' in creds and "UPDATE_PASSWORD" not in (user.get("requiredActions") or []), (creds[:200], user.get("requiredActions")))
    check("Ekranda gösterilen link e-postayı doğrulanmış saymaz", user.get("emailVerified") is False, user.get("emailVerified"))
    code, r = http("POST", f"{T}/public/password/link/{tok1}", {"password": strong_pw()})
    check("Kullanılmış link ikinci kez çalışmaz (410 used)", code == 410 and r.get("status") == "used", (code, r))

    # ---------------------------------------------------------------- sıfırlama, yeniden üretme, iptal
    code, v2 = api("admin", "POST", f"{T}/my-tenant/password-links/users/{created['kc_user']}/reset")
    check("Yönetici sıfırlama linki üretir (parolası olan hesap: reset)", code == 200 and v2.get("purpose") == "reset" and v2.get("link"), (code, v2))
    tok2 = token_of(v2["link"])
    code, v3 = api("admin", "POST", f"{T}/my-tenant/password-links/{v2['inviteId']}/regenerate")
    check("Yeniden üret: yeni link döner", code == 200 and v3.get("link") and v3["link"] != v2["link"], (code, v3))
    _, st = http("GET", f"{T}/public/password/link/{tok2}")
    check("Yeniden üretince eski link iptal", st.get("status") == "revoked", st)
    code, _ = api("admin", "POST", f"{T}/my-tenant/password-links/{v3['inviteId']}/revoke")
    _, st = http("GET", f"{T}/public/password/link/{token_of(v3['link'])}")
    check("İptal edilen link kullanılamaz", code == 200 and st.get("status") == "revoked", (code, st))
    code, lst = api("ik", "GET", f"{T}/my-tenant/password-links")
    mine = [x for x in (lst or []) if x["email"] == EMAIL]
    check("İK yöneticisi listeyi görür; durumlar doğru", code == 200 and {x["status"] for x in mine} >= {"used", "revoked"}
          and all("link" not in x and "tokenHash" not in x for x in mine), (code, mine))
    code, v4 = api("ik", "POST", f"{T}/my-tenant/password-links/users/{created['kc_user']}/reset")
    check("İK yöneticisi çalışan için link üretebilir", code == 200 and v4.get("link"), (code, v4))
    psql(f"""UPDATE tenant_password_invites SET "ExpiresAt" = now() - interval '1 minute' WHERE "Id" = '{v4['inviteId']}'""")
    _, st = http("GET", f"{T}/public/password/link/{token_of(v4['link'])}")
    check("Süresi dolan link: expired", st.get("status") == "expired", st)

    admin_id = json.loads(kcadm("get", "users", "-q", "username=demo.admin", "--fields", "id").stdout or "[{}]")[0].get("id")
    code, r = api("ik", "POST", f"{T}/my-tenant/password-links/users/{admin_id}/reset")
    check("İK yöneticisi şirket yöneticisinin linkini üretemez", code == 403, (code, r))

    # Parolamı unuttum: SMTP yokken hiçbir link üretilmez, yanıt hesabı belli etmez.
    n0 = psql(f"""SELECT count(*) FROM tenant_password_invites WHERE "Email" = '{EMAIL}'""")
    code, r = http("POST", f"{T}/public/password/reset-request", {"email": EMAIL})
    code2, r2 = http("POST", f"{T}/public/password/reset-request", {"email": f"yok.{RUN}@example.com"})
    n1 = psql(f"""SELECT count(*) FROM tenant_password_invites WHERE "Email" = '{EMAIL}'""")
    check("Parolamı unuttum (SMTP yok): 202, link üretilmez, yanıtlar aynı", code == 202 and code2 == 202 and r == r2 and n0 == n1, (code, code2, n0, n1))

    # ---------------------------------------------------------------- SMTP arayüzden (Mailpit'e)
    code, s = api("platform", "PUT", f"{T}/platform/settings/smtp", {"host": "mailpit", "port": 1025,
                  "fromAddress": "ik-test@demo.hr360", "fromName": "HR360 Test"})
    check("Platform SMTP'si arayüzden kaydedilir", code == 200 and s["smtp"]["mode"] == "custom" and s["smtp"]["configured"], (code, s))
    code, r = api("platform", "POST", f"{T}/platform/settings/smtp/test", {"to": f"deneme.{RUN}@example.com"})
    check("Deneme e-postası gönderilir", code == 200 and r.get("ok") is True, (code, r))
    check("Deneme e-postası Mailpit'e ulaşır", wait_mail(f"deneme.{RUN}@example.com", lambda m: True, 30) is not None)
    time.sleep(16)
    _, cfg = http("GET", f"{T}/public/config")
    check("SMTP açıkken parolamı unuttum e-postayla", cfg.get("passwordResetByEmail") is True, cfg)

    code, r = http("POST", f"{T}/public/password/reset-request", {"email": EMAIL})
    m = wait_mail(EMAIL, lambda m: "sıfırla" in (m.get("Subject") or "").lower() or "reset" in (m.get("Subject") or "").lower())
    check("Parolamı unuttum: sıfırlama linki e-postayla gelir", code == 202 and m is not None, (code, m))
    body = mail_text(m["ID"]) if m else ""
    found = re.search(r"/parola-belirle/([A-Za-z0-9_-]{43})", body)
    check("E-postada HR360 linki var (Keycloak bağlantısı değil)", found is not None and "execute-actions" not in body, body[:300])
    if found:
        pw2 = strong_pw()
        code, r = http("POST", f"{T}/public/password/link/{found.group(1)}", {"password": pw2})
        user = json.loads(kcadm("get", f"users/{created['kc_user']}").stdout or "{}")
        check("E-postadaki linkle parola sıfırlanır; e-posta doğrulanmış sayılır", code == 200 and user.get("emailVerified") is True, (code, r, user.get("emailVerified")))

    code, v5 = api("admin", "POST", f"{T}/my-tenant/password-links/users/{created['kc_user']}/reset")
    check("SMTP açıkken: link gösterilir ve e-postayla da gider", code == 200 and v5.get("link") and v5.get("emailStatus") == "queued", (code, v5))

    # Eski yöntem: Keycloak e-postası.
    code, mv = api("admin", "PUT", f"{T}/my-tenant/password-method", {"method": "keycloak"})
    check("Şirket eski yöntemi seçebilir (SMTP açık)", code == 200 and mv["selected"] == "keycloak" and mv["effective"] == "keycloak", (code, mv))
    code, v6 = api("admin", "POST", f"{T}/my-tenant/password-links/users/{created['kc_user']}/reset")
    check("Eski yöntem: Keycloak e-postası gider, link gösterilmez", code == 200 and v6.get("method") == "keycloak" and v6.get("link") is None, (code, v6))
    km = wait_mail(EMAIL, lambda m: "/auth/realms/hr360/login-actions/action-token" in mail_text(m["ID"]), 60)
    check("Keycloak'ın parola e-postası Mailpit'e ulaşır", km is not None)
    code, mv = api("admin", "PUT", f"{T}/my-tenant/password-method", {"method": None})
    check("Platform varsayılanına dönülür", code == 200 and mv["choice"] is None and mv["selected"] == "invite", (code, mv))

    # SMTP test kutusuna dönünce eski yöntem seçilemez.
    api("platform", "PUT", f"{T}/platform/settings/smtp/mode", {"mode": "mailpit"})
    code, r = api("admin", "PUT", f"{T}/my-tenant/password-method", {"method": "keycloak"})
    check("SMTP yokken eski yöntem seçilemez", code == 400, (code, r))
    code, r = api("platform", "PUT", f"{T}/platform/settings/general", {"inviteTtlHours": 0})
    check("Link süresi sınırı (1-720)", code == 400, (code, r))
    rows = psql("""SELECT count(*) FROM audit_log WHERE "EntityType" IN ('PasswordInvite','PlatformSettings')
                   AND "OccurredAt" > now() - interval '30 minutes' AND "Changes"::text NOT LIKE '%parola-belirle%'""")
    check("Oluşturma/kullanma/iptal ve ayar değişiklikleri denetim kaydında (link yazılmadan)", int(rows or 0) >= 8, rows)
finally:
    # Ayarları eski haline getir (önceki SMTP kipi; arayüzden girilmiş özel sunucu yoksa kurulum ayarı).
    # Arayüzden girilmiş özel sunucu yoksa kurulum ayarına (.env) dön; "mailpit" açıkça yazılmasın.
    mode = (before or {}).get("smtp", {}).get("mode")
    if mode in ("mailpit", "env"):
        api("platform", "PUT", f"{T}/platform/settings/smtp/mode", {"mode": "install"})
    api("admin", "PUT", f"{T}/my-tenant/password-method", {"method": None})
    if created["kc_user"]:
        psql(f"""DELETE FROM tenant_password_invites WHERE "KeycloakUserId" = '{created['kc_user']}'""")
        kcadm("delete", f"users/{created['kc_user']}")
    if created["employee"]:
        e = created["employee"]
        psql(f"""DELETE FROM notification_messages WHERE "RecipientEmployeeId" = '{e}' OR "RecipientEmail" = '{EMAIL}';
                 DELETE FROM employee_employees WHERE "Id" = '{e}'""")
    psql(f"""DELETE FROM notification_messages WHERE "RecipientEmail" = 'deneme.{RUN}@example.com'""")

print(f"FAILS: {len(FAIL)}")
sys.exit(1 if FAIL else 0)

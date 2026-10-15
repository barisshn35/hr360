"""E-posta dili: alıcının tercihine göre konu, gövde ve e-posta çerçevesi Türkçe ya da İngilizce.
Keycloak dili (giriş ekranı, parola sıfırlama e-postası) da aynı tercihle güncellenir.

Ön koşul: SMTP olarak Mailpit (varsayılan kurulum; HR360_MAILPIT_URL, varsayılan http://127.0.0.1:8025).
"""

import datetime as dt
import json
import os
import random
import subprocess
import sys
import time
import urllib.request

sys.path.insert(0, os.path.dirname(__file__))
from common import AYSE, FAIL, api, check  # noqa: E402
import hr360_login  # noqa: E402

MAILPIT = os.environ.get("HR360_MAILPIT_URL", "http://127.0.0.1:8025")
ROOT = os.path.join(os.path.dirname(__file__), "..", "..")
FAIL.clear()


def mails(query):
    with urllib.request.urlopen(f"{MAILPIT}/api/v1/search?query={urllib.request.quote(query)}&limit=20", timeout=10) as r:
        return json.loads(r.read())["messages"]


def mail_html(mid):
    with urllib.request.urlopen(f"{MAILPIT}/api/v1/message/{mid}", timeout=10) as r:
        return json.loads(r.read())


def leave_for_approval(tag):
    """Ayşe bir izin talebi açar; onaycı Mehmet'e 'onayınızı bekleyen talep' e-postası gider."""
    day = dt.date(2040, 1, 1) + dt.timedelta(days=random.randint(0, 3000))
    while day.weekday() >= 5:
        day += dt.timedelta(days=1)
    code, r = api("ayse", "POST", "/api/leave/leave-requests",
                  {"employeeId": AYSE, "type": "Unpaid", "startDate": day.isoformat(), "endDate": day.isoformat(), "days": 1, "reason": tag})
    return code, r


def wait_mail(since, subject_part, timeout=60):
    end = time.time() + timeout
    while time.time() < end:
        for m in mails(f'subject:"{subject_part}"'):
            created = dt.datetime.fromisoformat(m["Created"].replace("Z", "+00:00")).timestamp()
            if created >= since - 2:
                return m
        time.sleep(2)
    return None


# Tercih uçları
code, r = api("mehmet", "PUT", "/api/notification/notifications/preferences/me", {"language": "en"})
check("Dil tercihi kaydedildi (en)", code == 200 and r["language"] == "en" and r["linked"], (code, r))
code, r = api("mehmet", "GET", "/api/notification/notifications/preferences/me")
check("Dil tercihi okunur", code == 200 and r["language"] == "en", r)
code, r = api("mehmet", "PUT", "/api/notification/notifications/preferences/me", {"language": "de"})
check("Geçersiz dil reddedilir", code == 400, code)

# İngilizce e-posta
t0 = time.time()
code, req = leave_for_approval("email-en-" + str(random.randint(1000, 9999)))
check("İzin talebi oluşturuldu", code in (200, 201), (code, req))
m = wait_mail(t0, "A request is awaiting your approval")
check("Onaycıya İngilizce e-posta gitti", m is not None, mails("subject:approval")[:2])
if m:
    full = mail_html(m["ID"])
    check("E-posta gövdesi ve çerçevesi İngilizce", "is awaiting your approval" in full["Text"] and "This email was sent automatically" in full["HTML"]
          and 'lang="en"' in full["HTML"], full["Text"][:200])

# Türkçeye dönüş
api("mehmet", "PUT", "/api/notification/notifications/preferences/me", {"language": "tr"})
t1 = time.time()
code, req2 = leave_for_approval("email-tr-" + str(random.randint(1000, 9999)))
m = wait_mail(t1, "Onayınızı bekleyen bir talep var")
check("Tercih Türkçe olunca e-posta Türkçe", m is not None and "otomatik olarak gönderilmiştir" in mail_html(m["ID"])["HTML"], m)

# Talepleri geri çek (test verisi kalmasın)
for r in (req, req2):
    if isinstance(r, dict) and r.get("id"):
        api("ayse", "POST", f"/api/leave/leave-requests/{r['id']}/cancel")

# Keycloak dili
code, _ = api("ayse", "PUT", "/api/tenant/my-tenant/me/locale", {"language": "en"})
check("Keycloak dili güncellendi", code == 204, code)
# Keycloak 25, kullanıcı profili tanımında olmayan "locale" özniteliğini yönetim API'sinde
# göstermez ama saklar ve kullanır; bu yüzden doğrudan Keycloak veritabanından okunur.
uname = hr360_login.users()["ayse"][0]
out = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", "keycloak", "-Atc",
                      "SELECT a.value FROM user_attribute a JOIN user_entity u ON u.id = a.user_id "
                      f"WHERE a.name = 'locale' AND u.username = '{uname}'"],
                     cwd=ROOT, capture_output=True, text=True)
check("Keycloak kullanıcısında locale=en", out.stdout.strip() == "en", out.stdout[:200] or out.stderr[:200])
api("ayse", "PUT", "/api/tenant/my-tenant/me/locale", {"language": "tr"})

print(f"\nFAILS: {len(FAIL)}")
for f in FAIL:
    print("  -", f)
sys.exit(1 if FAIL else 0)

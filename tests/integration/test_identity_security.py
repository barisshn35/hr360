#!/usr/bin/env python3
"""Güvenlik dalgası 2A (tenant-service + tüm servislerin kiracı kapısı):
platform yöneticisi süreli erişim izni (break-glass), iki adımlı doğrulama politikası uçları,
şüpheli giriş uyarıları ve art arda hatalı girişin denetim kaydına düşmesi.

Ön koşul: HR360 çalışıyor, scripts/sql/2026-10-16_identity_security.sql uygulanmış,
deploy/testing/chat-mock.yml katmanı açık (LOGIN_WATCH_INTERVAL_SECONDS=5), Keycloak hr360
realm'inde giriş olayları açık (scripts/keycloak-service-account.sh).

Not: iki adımlı doğrulama politikası demo şirketinde AÇILMAZ (açılırsa test kullanıcılarının
tarayıcı girişleri doğrulayıcı kurulumu ister); yalnızca okuma, geçersiz değer ve "off" sınanır.

Çalıştırma: timeout 600 python3 tests/integration/test_identity_security.py
"""
import base64
import json
import os
import subprocess
import sys
import time
import uuid

sys.path.insert(0, os.path.dirname(__file__))
from common import BASE, FAIL, api, check, http, tok  # noqa: E402

ROOT = os.path.join(os.path.dirname(__file__), "..", "..")
FAIL.clear()
PA = "/api/tenant/platform-access"
REASON = "TEST-2A destek talebi #1234 inceleme"


def psql(sql):
    out = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", "hr360_operational", "-Atc", sql],
                         cwd=ROOT, capture_output=True, text=True)
    return (out.stdout + out.stderr).strip()


def as_platform(method, path, body=None, tenant="demo"):
    h = {"Authorization": "Bearer " + tok("platform")}
    if tenant:
        h["X-HR360-Tenant"] = tenant
    return http(method, path, body, h)


# Önceki koşulardan kalan test izinleri kapatılır (kapı önbelleği 15 sn).
psql("""UPDATE platform_access_grants SET "RevokedAt" = now() WHERE "Reason" LIKE 'TEST-2A%' AND "RevokedAt" IS NULL""")
time.sleep(16)

# ============================================================ break-glass
code, r = as_platform("GET", "/api/employee/employees")
check("İzinsiz platform yöneticisi kiracı verisine erişemez", code == 403 and (r or {}).get("code") == "platform_access_grant_required", (code, r))
code, r = as_platform("GET", "/api/leave/leave-requests")
check("İzinsiz: izin servisi de reddeder", code == 403, code)
code, r = as_platform("GET", "/api/employee/employees", tenant=None)
check("Kiracı seçilmeden kiracı verisi istenemez", code == 403, (code, r))
code, _ = api("platform", "GET", "/api/tenant/tenants")
check("Platform düzeyi (kiracı listesi) izinsiz çalışır", code == 200, code)

# Kabuk her sayfada my-tenant ve ui-prefs ister: izinsiz de 404/403 vermemeli.
code, mt = as_platform("GET", "/api/tenant/my-tenant")
check("Platform: seçili şirketin salt okunur özeti (izinsiz: platformAccess boş)",
      code == 200 and mt["slug"] == "demo" and mt["readOnly"] is True and mt.get("platformAccess") is None and "smtpFromAddress" not in mt, (code, mt))
code, _ = as_platform("GET", "/api/tenant/my-tenant", tenant=None)
check("Platform: şirket seçilmemişse my-tenant 404", code == 404, code)
code, _ = as_platform("GET", "/api/notification/notifications/ui-prefs")
check("Platform: kendi arayüz tercihleri izin istemez (403 değil)", code in (200, 204), code)
code, r = api("platform", "POST", f"{PA}/grants", {"tenantSlug": "demo", "reason": "kısa", "hours": 1})
check("Kısa gerekçe reddedilir", code == 400, (code, r))
code, r = api("platform", "POST", f"{PA}/grants", {"tenantSlug": "demo", "reason": REASON, "hours": 5})
check("4 saatten uzun izin reddedilir", code == 400, (code, r))
code, r = api("platform", "POST", f"{PA}/grants", {"tenantSlug": "yok-boyle-sirket", "reason": REASON, "hours": 1})
check("Olmayan şirkete izin açılamaz", code == 404, code)
code, r = api("admin", "POST", f"{PA}/grants", {"tenantSlug": "demo", "reason": REASON, "hours": 1})
check("Şirket yöneticisi izin açamaz", code == 403, code)

code, g = api("platform", "POST", f"{PA}/grants", {"tenantSlug": "demo", "reason": REASON, "hours": 1})
check("Platform yöneticisi gerekçeli 1 saatlik izin açar", code == 200 and g["active"] and g["tenantSlug"] == "demo", (code, g))
gid = (g or {}).get("id")
time.sleep(4)  # kapının olumsuz önbelleği (3 sn)
code, emps = as_platform("GET", "/api/employee/employees")
check("İzinle çalışan listesi okunur", code == 200, (code, str(emps)[:200]))
code, _ = as_platform("GET", "/api/employee/employees", tenant="acme-baska")
check("İzin yalnızca açıldığı şirket için geçerli", code == 403, code)
code, mt = as_platform("GET", "/api/tenant/my-tenant")
check("Platform: my-tenant etkin iznin bitişini döner", code == 200 and (mt.get("platformAccess") or {}).get("grantId") == gid, (code, mt))
code, pend = as_platform("GET", "/api/governance/privacy/consents/pending")
check("Platform: rıza bandı listesi boş", code == 200 and pend == [], (code, pend))
code, r = as_platform("POST", "/api/governance/privacy/consents/me", {"consentType": "GORSEL_KULLANIM", "granted": True})
check("Platform: rıza kaydı reddedilir", code == 403, (code, r))
admin_sub = json.loads(base64.urlsafe_b64decode(tok("admin").split(".")[1] + "==="))["sub"]
if psql(f"""SELECT count(*) FROM employee_employees WHERE "KeycloakUserId" = '{admin_sub}'""") == "0":
    mails = psql("""SELECT count(*) FROM notification_messages WHERE "TenantSlug" = 'demo' AND "TemplateCode" = 'security.platform_access'
                    AND "Channel" = 'Email' AND "RecipientEmployeeId" = '00000000-0000-0000-0000-000000000000' AND "CreatedAt" > now() - interval '2 minutes'""")
    check("Çalışan kaydı olmayan şirket yöneticisine izin e-postası", mails.isdigit() and int(mails) >= 1, mails)
    code, members = api("admin", "GET", "/api/tenant/my-tenant/members")
    me_row = next((m for m in members if m.get("keycloakUserId") == admin_sub), None) if code == 200 else None
    check("Üye listesi çalışan kaydı olmayan yönetici hesabını içerir",
          me_row is not None and me_row["employeeId"] is None and me_row["hasEmployeeRecord"] is False and "tenant-admin" in me_row["roles"], (code, me_row))
code, mine = api("platform", "GET", f"{PA}/grants/mine")
check("Platform yöneticisi etkin iznini görür", code == 200 and any(x["id"] == gid for x in mine), (code, mine))
audit = psql(f"""SELECT count(*) FROM audit_log WHERE "TenantSlug" = 'demo' AND "Action" = 'PlatformAccess' AND "EntityId" = '{gid}'""")
check("Erişim şirketin denetim kaydına yazılır", audit.isdigit() and int(audit) >= 1, audit)
granted = psql(f"""SELECT count(*) FROM audit_log WHERE "TenantSlug" = 'demo' AND "Action" = 'PlatformAccessGranted' AND "EntityId" = '{gid}'""")
check("İzin açılışı denetim kaydında", granted == "1", granted)

code, lst = api("admin", "GET", f"{PA}/grants")
row = next((x for x in lst if x["id"] == gid), None) if code == 200 else None
check("Şirket yöneticisi açılan izni görür", row is not None and row["active"] and row["reason"] == REASON, (code, row))
code, _ = api("ayse", "GET", f"{PA}/grants")
check("Çalışan izin listesini göremez", code == 403, code)
code, _ = api("ayse", "POST", f"{PA}/grants/{gid}/revoke")
check("Çalışan izni kapatamaz", code == 404, code)
code, r = api("admin", "POST", f"{PA}/grants/{gid}/revoke")
check("Şirket yöneticisi izni kapatır", code == 200 and r["active"] is False and r["revokedAt"], (code, r))
time.sleep(16)  # kapının olumlu önbelleği (15 sn)
code, _ = as_platform("GET", "/api/employee/employees")
check("Kapatılan izinle erişim biter", code == 403, code)

# ============================================================ iki adımlı doğrulama politikası
code, m = api("admin", "GET", "/api/tenant/security/mfa")
check("MFA durumu politika ve rol bilgisiyle döner", code == 200 and m.get("policy") in ("off", "privileged", "all")
      and "privileged" in (m.get("users") or [{}])[0] and m.get("privilegedRoles"), (code, str(m)[:300]))
code, _ = api("admin", "PUT", "/api/tenant/security/mfa/policy", {"policy": "bazen"})
check("Geçersiz politika reddedilir", code == 400, code)
code, r = api("admin", "PUT", "/api/tenant/security/mfa/policy", {"policy": "off"})
check("Politika kapatılabilir", code == 200 and r["policy"] == "off", (code, r))
code, _ = api("ayse", "PUT", "/api/tenant/security/mfa/policy", {"policy": "all"})
check("Çalışan politikayı değiştiremez", code == 403, code)

# ============================================================ şüpheli giriş
code, alerts = api("admin", "GET", "/api/tenant/security/login-alerts")
check("Şüpheli giriş uyarıları okunur", code == 200 and isinstance(alerts, list), (code, alerts))
code, _ = api("ayse", "GET", "/api/tenant/security/login-alerts")
check("Çalışan uyarıları göremez", code == 403, code)


def failed_logins(n):
    """Var olmayan bir kullanıcı adıyla n kez hatalı tarayıcı girişi (user_not_found olayı)."""
    from playwright.sync_api import sync_playwright
    user = f"test2a-yok-{uuid.uuid4().hex[:8]}@example.invalid"
    done = 0
    with sync_playwright() as p:
        b = p.chromium.launch(headless=True, args=["--no-sandbox"])
        for _ in range(n):
            ctx = b.new_context()
            page = ctx.new_page()
            try:
                page.goto(f"{BASE}/giris", wait_until="networkidle")
                page.click("text=Kurumsal hesabımla giriş yap")
                page.wait_for_selector("input[name='username']", timeout=20000)
                page.fill("input[name='username']", user)
                page.click("input[type='submit'], button[type='submit']")
                page.wait_for_selector("input[name='password']", timeout=20000)
                page.fill("input[name='password']", "Yanlis-Parola-123")
                page.click("input[type='submit'], button[type='submit']")
                page.wait_for_timeout(1500)
                done += 1
            except Exception as e:  # noqa: BLE001
                print("  (hatalı giriş denemesi tamamlanamadı:", str(e)[:120], ")")
            ctx.close()
        b.close()
    return done


done = failed_logins(5)
hit = ""
for _ in range(24):
    hit = psql("""SELECT count(*) FROM audit_log WHERE "Action" = 'SuspiciousLogin' AND "Changes"->>'kind' = 'failed_network'
                  AND "OccurredAt" > now() - interval '60 minutes'""")
    if hit.isdigit() and int(hit) >= 1:
        break
    time.sleep(5)
check("Aynı ağdan art arda hatalı giriş denetim kaydına düşer (60 dk içinde tek uyarı)", done == 5 and hit.isdigit() and int(hit) >= 1, (done, hit))

print(f"\nFAILS: {len(FAIL)}")
for f in FAIL:
    print("  -", f)
sys.exit(1 if FAIL else 0)

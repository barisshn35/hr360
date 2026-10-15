"""KVKK operasyonları ve güvenlik (Dalga 5a):
K2 aydınlatma metni sürümleri, K3 veri ihlali (72 saat), K8 başvuru kimlik doğrulama,
K9 gizlilik etki değerlendirmesi, G20 alan düzeyinde yetki, G21 değiştirilemez denetim
kaydı + SIEM, G22 IP kısıtı / oturumlar / passkey akışı.

Ön koşul: HR360 çalışıyor, deploy/testing/chat-mock.yml katmanı açık (SIEM sahte alıcısı).
"""

import datetime as dt
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(__file__))
from common import AYSE, FAIL, G, api, check, http, mock  # noqa: E402

ROOT = os.path.join(os.path.dirname(__file__), "..", "..")
P = f"{G}/privacy"
FAIL.clear()
now = dt.datetime.now(dt.timezone.utc)


def psql(sql, db="hr360_operational"):
    out = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", db, "-Atc", sql],
                         cwd=ROOT, capture_output=True, text=True)
    return (out.stdout + out.stderr).strip()


# ============================================================ K2 aydınlatma metinleri
TYPE = "GORSEL_KULLANIM"
psql(f"""DELETE FROM governance_privacy_notices WHERE "Type" = '{TYPE}'""")
code, notices = api("admin", "GET", f"{P}/notices")
n0 = next((n for n in notices if n["type"] == TYPE), None) if code == 200 else None
check("Metinler: liste ve yerleşik sürüm", code == 200 and n0 and n0["version"] == "2026.1" and not n0["custom"], (code, n0))
api("ayse", "POST", f"{P}/consents/me", {"consentType": TYPE, "granted": True})
code, _ = api("ayse", "GET", f"{P}/notices")
check("Metin yönetimi yalnızca İK", code == 403, code)
code, r = api("admin", "POST", f"{P}/notices", {"type": TYPE, "title": "Fotoğraf ve ad kullanımı", "text": "Şirket içi bülten ve intranette fotoğrafımın, adımın ve unvanımın kullanılmasına izin veriyorum.", "version": "test-2", "changeNote": "unvan eklendi"})
check("Yeni sürüm yayımlanır", code == 200 and r["version"] == "test-2", (code, r))
code, r = api("admin", "POST", f"{P}/notices", {"type": TYPE, "title": "x", "text": "kısa"})
check("Kısa metin reddedilir", code == 400, code)
code, _ = api("admin", "POST", f"{P}/notices", {"type": TYPE, "title": "Fotoğraf", "text": "Aynı sürüm adıyla ikinci yayın denemesi metni.", "version": "test-2"})
check("Aynı sürüm tekrar yayımlanamaz", code == 409, code)
code, mine = api("ayse", "GET", f"{P}/consents/me")
m = next((x for x in mine if x["type"] == TYPE), None) if code == 200 else None
check("Çalışan yeni sürümü görür ve onayı 'güncel değil'", m and m["version"] == "test-2" and m["outdated"] is True and "unvan" in m["text"], m)
code, _ = api("ayse", "POST", f"{P}/consents/me", {"consentType": TYPE, "granted": True})
code, mine = api("ayse", "GET", f"{P}/consents/me")
m = next((x for x in mine if x["type"] == TYPE), None)
check("Yeniden onay sonrası güncel", m and m["outdated"] is False, m)
code, v = api("ayse", "GET", f"{P}/notices/{TYPE}/2026.1")
check("Eski sürümün metni kanıt olarak okunabilir", code == 200 and "fotoğrafımın ve adımın" in v["text"], (code, v))
code, summ = api("admin", "GET", f"{P}/consents")
t = next((x for x in summ["types"] if x["type"] == TYPE), {})
check("Rıza özeti güncel sürümü kullanır", t.get("version") == "test-2" and "outdated" in t, t)

# ============================================================ K3 veri ihlali
psql("""DELETE FROM governance_data_breaches WHERE "Title" LIKE 'TEST%'""")
code, _ = api("ayse", "GET", f"{P}/breaches")
check("İhlal kayıtları yalnızca İK", code == 403, code)
detected = (now - dt.timedelta(hours=80)).isoformat()
code, b = api("admin", "POST", f"{P}/breaches", {"title": "TEST bordro e-postası yanlış kişiye", "description": "Bir bordro pusulası yanlış alıcıya gönderildi.",
                                                  "detectedAt": detected, "dataCategories": "ücret, kimlik", "affectedEmployees": [AYSE], "severity": "High"})
check("İhlal kaydı oluşur, 72 saat aşıldı", code == 200 and b["overdue"] is True and b["affectedCount"] == 1, (code, b))
bid = b["id"] if code == 200 else None
code, _ = api("admin", "POST", f"{P}/breaches", {"title": "TEST", "description": "x", "detectedAt": (now + dt.timedelta(days=2)).isoformat()})
check("Gelecekte tespit zamanı reddedilir", code == 400, code)
code, checks = api("admin", "GET", f"{P}/compliance")
c = next((x for x in checks if x["key"] == "breaches"), None)
check("Uyum: bildirilmemiş ihlal hata", c and c["status"] == "error", c)
code, form = api("admin", "GET", f"{P}/breaches/{bid}/board-form")
check("Kurul formu taslağı (geç bildirim gerekçesi)", code == 200 and "TEST bordro" in form["text"] and form["late"] is True and "Geç bildirim" in form["text"], (code, form))
code, r = api("admin", "POST", f"{P}/breaches/{bid}/notify", {})
check("Etkilenen çalışan bilgilendirilir", code == 200 and r["notified"] == 1, (code, r))
cnt = psql(f"""SELECT count(*) FROM notification_messages WHERE "TemplateCode" = 'privacy.breach' AND "RecipientEmployeeId" = '{AYSE}'""")
check("Bilgilendirme bildirimi yazıldı", cnt.isdigit() and int(cnt) >= 1, cnt)
code, _ = api("admin", "POST", f"{P}/breaches/{bid}/close")
check("Önlemler yazılmadan kapatılamaz", code == 400, code)
code, r = api("admin", "POST", f"{P}/breaches/{bid}/report", {"reference": "VERBIS-TEST-1"})
check("Kurul'a bildirildi (geç)", code == 200 and r["status"] == "Reported" and r["lateReport"] is True, (code, r))
code, r = api("admin", "PUT", f"{P}/breaches/{bid}", {"title": "TEST bordro e-postası yanlış kişiye", "description": "Bir bordro pusulası yanlış alıcıya gönderildi.",
                                                       "detectedAt": detected, "affectedEmployees": [AYSE], "severity": "High", "measures": "Alıcıdan silmesi istendi, gönderim kuralı değişti."})
code, r = api("admin", "POST", f"{P}/breaches/{bid}/close")
check("İhlal kaydı kapanır", code == 200 and r["status"] == "Closed", (code, r))
code, _ = api("admin", "PUT", f"{P}/breaches/{bid}", {"title": "TEST değişiklik", "description": "x"})
check("Kapalı kayıt değiştirilemez", code == 409, code)
psql(f"""DELETE FROM notification_messages WHERE "TemplateCode" = 'privacy.breach' AND "RecipientEmployeeId" = '{AYSE}'""")

# ============================================================ K8 başvuru kimlik doğrulama
code, meta = api("admin", "GET", f"{P}/request-meta")
check("Başvuru şablonları ve doğrulama yöntemleri", code == 200 and len(meta["templates"]) >= 5 and any(m["value"] == "Kep" for m in meta["verificationMethods"]), code)
code, _ = api("ayse", "POST", f"{P}/requests/external", {"kind": "Access", "personName": "X", "channel": "Email"})
check("Panel dışı başvuruyu yalnızca İK kaydeder", code == 403, code)
received = (now - dt.timedelta(days=3)).isoformat()
code, rq = api("admin", "POST", f"{P}/requests/external", {"kind": "Access", "personName": "TEST Eski Çalışan", "channel": "Kep", "contact": "eski@kep.tr", "details": "Verilerimin dökümünü istiyorum", "receivedAt": received})
check("E-posta/KEP başvurusu kaydedilir, kimlik doğrulanmamış", code == 200 and rq["identityVerified"] is False and rq["channel"] == "Kep", (code, rq))
rid = rq["id"] if code == 200 else None
code, rows = api("admin", "GET", f"{P}/requests")
row = next((x for x in rows if x["id"] == rid), {})
check("Süre ulaştığı tarihten başlar (≈27 gün)", row.get("daysLeft") in (26, 27, 28), row.get("daysLeft"))
code, r = api("admin", "PATCH", f"{P}/requests/{rid}", {"status": "Completed", "response": "Döküm ektedir."})
check("Kimlik doğrulanmadan sonuçlandırılamaz", code == 400 and (r or {}).get("code") == "identity_unverified", (code, r))
code, r = api("admin", "POST", f"{P}/requests/{rid}/verify", {"method": "Bilinmeyen"})
check("Geçersiz doğrulama yöntemi", code == 400, code)
code, r = api("admin", "POST", f"{P}/requests/{rid}/verify", {"method": "Kep"})
check("Kimlik KEP ile doğrulandı", code == 200 and r["identityVerified"] is True, (code, r))
code, r = api("admin", "PATCH", f"{P}/requests/{rid}", {"status": "Completed", "response": "Döküm ektedir."})
check("Doğrulama sonrası sonuçlandırılır", code == 200, (code, r))
code, own = api("ayse", "POST", f"{P}/requests", {"kind": "Rectification", "details": "TEST adres düzeltme"})
check("Panelden başvuru oturumla doğrulanmış", code == 200 and own["identityVerified"] is True and own["verificationMethod"] == "Session", (code, own))
psql("""DELETE FROM governance_data_requests WHERE "PersonName" LIKE 'TEST%' OR "Details" LIKE 'TEST%'""")

# ============================================================ K9 gizlilik etki değerlendirmesi
psql("""DELETE FROM governance_privacy_assessments WHERE "Subject" LIKE 'TEST%'""")
code, qs = api("admin", "GET", f"{P}/assessments/questions")
codes = [q["code"] for q in qs["questions"]] if code == 200 else []
check("Kontrol listesi soruları", code == 200 and len(codes) >= 10 and "special" in codes, code)
risky = {c: {"answer": "no"} for c in codes}
risky.update({"special": {"answer": "yes"}, "abroad": {"answer": "yes"}})
code, a = api("admin", "POST", f"{P}/assessments", {"subject": "TEST sağlık verisi yurt dışı", "kind": "Process", "providerKey": None, "answers": risky})
check("Özel nitelikli + yurt dışı = yüksek risk", code == 200 and a["risk"] == "High", (code, a))
code, r = api("admin", "POST", f"{P}/assessments/{a['id']}/approve")
check("Yüksek riskli değerlendirmeyi hazırlayan onaylayamaz", code == 403, code)
safe = {c: {"answer": "yes" if q["riskyAnswer"] == "no" else "no"} for c, q in ((q["code"], q) for q in qs["questions"])}
code, b2 = api("admin", "POST", f"{P}/assessments", {"subject": "TEST Slack", "kind": "Integration", "providerKey": "slack", "answers": safe})
check("Güvenli yanıtlar = düşük risk", code == 200 and b2["risk"] == "Low", (code, b2))
code, _ = api("admin", "POST", f"{P}/assessments", {"subject": "TEST eksik", "kind": "Integration", "providerKey": "slack", "answers": {"special": {"answer": "belki"}}})
check("Geçersiz yanıt reddedilir", code == 400, code)
code, r = api("admin", "POST", f"{P}/assessments/{b2['id']}/approve")
check("Değerlendirme onaylanır", code == 200 and r["status"] == "Approved", (code, r))
code, _ = api("admin", "DELETE", f"{P}/assessments/{b2['id']}")
check("Onaylı değerlendirme silinemez", code == 409, code)
code, r = api("admin", "PUT", f"{P}/assessments/{b2['id']}", {"subject": "TEST Slack", "kind": "Integration", "providerKey": "slack", "answers": {**safe, "abroad": {"answer": "yes"}}})
check("Yanıt değişince onay düşer", code == 200 and r["status"] == "Draft" and r["approvedBy"] is None, (code, r))
code, checks = api("admin", "GET", f"{P}/compliance")
check("Uyum: etki değerlendirmesi denetimi", code == 200 and any(x["key"] == "assessments" for x in checks), code)
code, _ = api("ayse", "GET", f"{P}/assessments")
check("Değerlendirmeler yalnızca İK", code == 403, code)
psql("""DELETE FROM governance_privacy_assessments WHERE "Subject" LIKE 'TEST%'""")

# ============================================================ G20 alan düzeyinde yetki
psql("""DELETE FROM governance_field_policies WHERE "TenantSlug" = 'demo'""")
code, fp = api("admin", "GET", f"{P}/field-policies")
check("Alan politikaları (varsayılanlar)", code == 200 and {f["field"] for f in fp} >= {"iban", "nationalId", "birthDate", "skills"}, code)
code, _ = api("admin", "PUT", f"{P}/field-policies/iban", {"level": "everyone"})
check("IBAN herkese açılamaz", code == 400, code)
code, _ = api("ayse", "PUT", f"{P}/field-policies/birthDate", {"level": "everyone"})
check("Çalışan politika değiştiremez", code == 403, code)
api("ayse", "PUT", "/api/engagement/profile/me", {"birthDate": "1992-05-17", "linkedInUrl": "https://linkedin.com/in/test-ayse"})
code, prof = api("mehmet", "GET", f"/api/engagement/profile/{AYSE}")
check("Varsayılan: yönetici doğum tarihini göremez", code == 200 and prof.get("birthDate") is None and "birthDate" in prof.get("hiddenFields", []), (code, prof))
check("Varsayılan: LinkedIn herkese açık", prof.get("linkedInUrl") == "https://linkedin.com/in/test-ayse", prof.get("linkedInUrl"))
for f, lvl in (("birthDate", "manager"), ("linkedInUrl", "hr"), ("nationalId", "self")):
    code, _ = api("admin", "PUT", f"{P}/field-policies/{f}", {"level": lvl})
    check(f"Politika: {f} → {lvl}", code == 200, code)
time.sleep(32)  # engagement-service politika önbelleği 30 sn
code, prof = api("mehmet", "GET", f"/api/engagement/profile/{AYSE}")
check("Bölüm yöneticisi doğum tarihini görür", code == 200 and prof.get("birthDate") == "1992-05-17", (code, prof))
check("LinkedIn artık yalnız İK'ya açık", prof.get("linkedInUrl") is None, prof.get("linkedInUrl"))
code, prof = api("admin", "GET", f"/api/engagement/profile/{AYSE}")
check("İK 'yalnızca kendisi' alanını göremez", code == 200 and prof.get("nationalId") is None and "nationalId" in prof.get("hiddenFields", []), (code, prof))
code, r = api("admin", "GET", f"/api/engagement/profile/{AYSE}/reveal?field=nationalId&reason=bordro%20kontrol")
check("İK 'yalnızca kendisi' alanını açamaz", code == 403 and (r or {}).get("code") == "field_policy", (code, r))
code, own = api("ayse", "GET", "/api/engagement/profile/me")
check("Çalışan kendi alanlarını görür", code == 200 and own.get("birthDate") == "1992-05-17", (code, own))
psql("""DELETE FROM governance_field_policies WHERE "TenantSlug" = 'demo'""")

# ============================================================ G21 değiştirilemez denetim kaydı + SIEM
code, v = api("admin", "GET", f"{G}/audit/verify")
check("Denetim zinciri sağlam", code == 200 and v["ok"] is True and v["rows"] > 0, (code, v))
code, _ = api("ayse", "GET", f"{G}/audit/verify")
check("Zincir doğrulama yalnızca İK", code == 403, code)
out = psql("""UPDATE audit_log SET "Action" = 'X' WHERE "Id" = (SELECT max("Id") FROM audit_log)""")
check("Denetim satırı güncellenemez (tetikleyici)", "değiştirilemez" in out or "ERROR" in out, out)
# Kurcalama simülasyonu: tetikleyici geçici kapatılıp bir satır değiştirilir; doğrulama yakalamalı.
target = psql("""SELECT "Id" || '|' || "Action" FROM audit_log WHERE "TenantSlug" = 'demo' AND "ChainSeq" IS NOT NULL ORDER BY "ChainSeq" DESC OFFSET 5 LIMIT 1""")
tid, tact = target.split("|", 1)
psql(f"""ALTER TABLE audit_log DISABLE TRIGGER trg_audit_log_immutable; UPDATE audit_log SET "Action" = 'Kurcalandi' WHERE "Id" = {tid}; ALTER TABLE audit_log ENABLE TRIGGER trg_audit_log_immutable;""")
code, v = api("admin", "GET", f"{G}/audit/verify")
check("Değiştirilen satır doğrulamada yakalanır", code == 200 and v["ok"] is False and v["tampered"] == 1, (code, v))
psql(f"""ALTER TABLE audit_log DISABLE TRIGGER trg_audit_log_immutable; UPDATE audit_log SET "Action" = '{tact}' WHERE "Id" = {tid}; ALTER TABLE audit_log ENABLE TRIGGER trg_audit_log_immutable;""")
code, v = api("admin", "GET", f"{G}/audit/verify")
check("Geri alınınca zincir yeniden sağlam", code == 200 and v["ok"] is True, (code, v))

code, st = api("admin", "GET", f"{G}/audit/siem")
check("SIEM aktarımı yapılandırılmış", code == 200 and st["configured"] is True, (code, st))
lines = []
for _ in range(20):
    lines = [x for x in mock("/_syslog?n=300") or [] if "hr360@32473" in x]
    if any('action="Verified"' in x for x in lines):
        break
    time.sleep(1)
check("Denetim kaydı syslog'a ulaştı (RFC 5424)", any(x.startswith("<110>1 ") and 'action="Verified"' in x for x in lines), lines[-2:])
check("SIEM'de kullanıcı takma adlı, ad/e-posta yok", lines and all('user="p_' in x or 'user="-"' in x for x in lines)
      and not any(("@demo.hr360" in x) or ("Ayşe" in x) or ("admin" in x.split("]")[0].split("user=")[1][:20]) for x in lines), lines[-1:])
check("SIEM'de IP son okteti maskeli", all(('net="-"' in x) or ('.0/24"' in x) or ('/48"' in x) for x in lines), [x for x in lines if "net=" in x][:1])

# ============================================================ G22 IP kısıtı, oturumlar, passkey
code, ip = api("admin", "GET", "/api/tenant/security/ip-allowlist")
check("IP kısıtı okunur", code == 200 and ip["yourIp"], (code, ip))
code, r = api("admin", "PUT", "/api/tenant/security/ip-allowlist", {"entries": ["10.255.255.0/24"]})
check("Kendini dışarıda bırakan liste reddedilir", code == 400 and (r or {}).get("code") == "self_lockout", (code, r))
code, r = api("admin", "PUT", "/api/tenant/security/ip-allowlist", {"entries": ["bozuk-adres"]})
check("Geçersiz adres reddedilir", code == 400, code)
code, r = api("admin", "PUT", "/api/tenant/security/ip-allowlist", {"entries": [ip["yourIp"]]})
check("Kendi adresi içeren liste kaydedilir", code == 200 and r["entries"] == [ip["yourIp"] + ("/32" if ":" not in ip["yourIp"] else "/128")], (code, r))
code, _ = api("ayse", "GET", "/api/tenant/my-tenant")
check("İzinli adresten erişim sürer", code == 200, code)
psql("""UPDATE platform_tenants SET "IpAllowlist" = '10.255.255.0/24' WHERE "Slug" = 'demo'""")
code, r = api("ayse", "GET", "/api/tenant/my-tenant")
check("İzinsiz adresten erişim engellenir", code == 403 and (r or {}).get("code") == "ip_not_allowed", (code, r))
code, _ = api("platform", "GET", "/api/tenant/tenants")
check("Platform yöneticisi IP kısıtından etkilenmez", code == 200, code)
psql("""UPDATE platform_tenants SET "IpAllowlist" = NULL WHERE "Slug" = 'demo'""")
code, _ = api("ayse", "GET", "/api/tenant/my-tenant")
check("Kısıt kaldırılınca erişim döner", code == 200, code)
code, _ = api("ayse", "PUT", "/api/tenant/security/ip-allowlist", {"entries": []})
check("IP kısıtını yalnızca şirket yöneticisi değiştirir", code == 403, code)

code, ms = api("ayse", "GET", "/api/tenant/my-tenant/me/sessions")
check("Kendi oturumlarım", code == 200 and len(ms["sessions"]) >= 1 and "hasPasskey" in ms, (code, ms))
check("Bu oturum işaretli", any(s["current"] for s in ms.get("sessions", [])), ms.get("sessions"))
code, _ = api("ayse", "DELETE", "/api/tenant/my-tenant/me/sessions/baskasinin-oturumu")
check("Başkasının/olmayan oturum kapatılamaz", code == 404, code)
code, ts = api("admin", "GET", "/api/tenant/security/sessions")
check("Yönetici şirket oturumlarını görür", code == 200 and any(u.get("username") in ("ayse.yilmaz", "ayse") or "ayse" in (u.get("username") or "") for u in ts), (code, [u.get("username") for u in ts] if code == 200 else ts))
code, _ = api("ayse", "GET", "/api/tenant/security/sessions")
check("Çalışan şirket oturumlarını göremez", code == 403, code)
code, pk = api("admin", "GET", "/api/tenant/security/passkeys")
check("Passkey istatistiği", code == 200 and pk["members"] >= 3, (code, pk))
flow = psql("""SELECT f.alias FROM realm r JOIN authentication_flow f ON f.id = r.browser_flow WHERE r.name = 'hr360'""", db="keycloak")
check("Keycloak giriş akışı passkey adımlı", flow == "hr360 browser", flow)
ex = psql("""SELECT e.authenticator || ':' || e.requirement FROM authentication_execution e JOIN authentication_flow f ON f.id = e.flow_id
             WHERE f.alias LIKE 'hr360 browser%Conditional OTP%' AND e.authenticator IN ('webauthn-authenticator','auth-otp-form') ORDER BY 1""", db="keycloak")
check("OTP ve WebAuthn alternatif (requirement 2 = ALTERNATIVE)", ex.splitlines() == ["auth-otp-form:2", "webauthn-authenticator:2"], ex)

print(f"\nFAILS: {len(FAIL)}")
for f in FAIL:
    print("  -", f)
sys.exit(1 if FAIL else 0)

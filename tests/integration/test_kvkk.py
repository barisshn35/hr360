"""KVKK temeli: uyum durumu, işleme envanteri, yurt dışı aktarım kilidi (m.9), imha tutanağı,
hassas veri erişim kaydı ve gerekçe, TCKN/IBAN şifreleme, otomatik analize itiraz (m.11/1-g).

Ön koşul: HR360 çalışıyor (scripts/test.sh integration ile aynı ortam).
"""

import datetime as dt
import os
import subprocess
import sys

sys.path.insert(0, os.path.dirname(__file__))
from common import AYSE, FAIL, G, api, check, ensure_transfers, http  # noqa: E402

ROOT = os.path.join(os.path.dirname(__file__), "..", "..")
P = f"{G}/privacy"
FAIL.clear()
today = dt.date.today()


def psql(sql):
    out = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", "hr360_operational", "-Atc", sql],
                         cwd=ROOT, capture_output=True, text=True)
    return out.stdout.strip()


# --- uyum durumu ve envanter --------------------------------------------------
code, checks = api("admin", "GET", f"{P}/compliance")
keys = {c["key"] for c in checks} if code == 200 else set()
check("Uyum durumu denetimleri", code == 200 and {"notice", "requests", "objections", "retention", "transfers", "inventory"} <= keys, (code, keys))
code, _ = api("ayse", "GET", f"{P}/compliance")
check("Uyum durumu yalnızca İK'ya açık", code == 403, code)

code, inv = api("admin", "GET", f"{P}/inventory")
acts = {a["id"]: a for a in inv["activities"]} if code == 200 else {}
check("Envanter: işleme faaliyetleri", code == 200 and len(acts) >= 15 and "automated-analysis" in acts and "chat" in acts, (code, list(acts)[:5]))
check("Envanter: hukuki sebep ve saklama dolu", all(a["legalBasis"] and a["retention"] and a["purpose"] for a in acts.values()), "")
check("Envanter: izin kaydı özel nitelikli", acts.get("leave", {}).get("special") is True, acts.get("leave"))
check("Envanter: sohbet botu yurt dışı aktarım içeriyor", {t["key"] for t in acts.get("chat", {}).get("transfers", [])} == {"slack", "microsoft", "mattermost", "rocketchat"}, acts.get("chat"))

# --- yurt dışı aktarım --------------------------------------------------------
code, tr = api("admin", "GET", f"{P}/transfers")
check("Aktarım listesi", code == 200 and {p["key"] for p in tr["providers"]} >= {"slack", "microsoft", "google", "zoom", "anthropic", "openai"}, code)
code, r = api("admin", "PUT", f"{P}/transfers/openai", {"mechanism": "StandardContract", "signedAt": today.isoformat()})
check("Standart sözleşme: Kurul bildirimi bekliyor", code == 200 and r["status"] == "NotifyPending", (code, r))
old = (today - dt.timedelta(days=14)).isoformat()
code, r = api("admin", "PUT", f"{P}/transfers/openai", {"mechanism": "StandardContract", "signedAt": old})
check("5 iş günü geçti: bildirim süresi aşıldı", code == 200 and r["status"] == "NotifyOverdue", (code, r))
code, r = api("admin", "PUT", f"{P}/transfers/openai", {"mechanism": "StandardContract", "signedAt": old, "notifiedAt": today.isoformat()})
check("Bildirildi: tamam", code == 200 and r["status"] == "Ok", (code, r))
code, r = api("admin", "PUT", f"{P}/transfers/openai", {"mechanism": "StandardContract", "signedAt": (today + dt.timedelta(days=3)).isoformat()})
check("Gelecek imza tarihi reddedilir", code == 400, code)
code, r = api("admin", "PUT", f"{P}/transfers/bilinmeyen", {"mechanism": "StandardContract", "signedAt": today.isoformat()})
check("Bilinmeyen hizmet 404", code == 404, code)
code, _ = api("ayse", "PUT", f"{P}/transfers/openai", {"mechanism": "Adequacy", "signedAt": today.isoformat()})
check("Çalışan aktarım kaydı giremez", code == 403, code)
code, _ = api("admin", "DELETE", f"{P}/transfers/openai")
check("Kullanılmayan hizmetin kaydı silinir", code == 204, code)

# Kilit: dayanak kaydı yokken Google takvim entegrasyonu açılamaz.
code, providers = api("admin", "GET", f"{G}/calendar/providers")
google = next(p for p in providers if p["provider"] == "Google")
body = {"clientId": google["clientId"] or "test-client.apps.googleusercontent.com", "clientSecret": None if google["hasSecret"] else "secret",
        "msTenant": None, "zoomAccountId": None, "zoomDefaultHost": None}
api("admin", "PUT", f"{G}/calendar/providers/Google", {**body, "isEnabled": False})
code, _ = api("admin", "DELETE", f"{P}/transfers/google")
check("Kullanımdan çıkan hizmetin kaydı silinebilir", code in (204, 404), code)
code, r = api("admin", "PUT", f"{G}/calendar/providers/Google", {**body, "isEnabled": True})
check("KİLİT: dayanak yokken Google açılamaz", code == 400 and r.get("code") == "kvkk_transfer", (code, r))
code, r = api("admin", "GET", f"{P}/compliance")
ensure_transfers("google")
code, r = api("admin", "PUT", f"{G}/calendar/providers/Google", {**body, "isEnabled": True})
check("Dayanak kaydedilince Google açılır", code == 200, (code, r))
code, r = api("admin", "DELETE", f"{P}/transfers/google")
check("Kullanımdaki hizmetin kaydı silinemez", code == 409, code)

# --- imha tutanağı -------------------------------------------------------------
code, pols = api("admin", "GET", f"{P}/retention")
wh = next(p for p in pols if p["category"] == "WebhookDeliveries")
check("Yeni saklama kategorileri", {"AiUsage", "ChatMessages", "WebhookDeliveries"} <= {p["category"] for p in pols}, [p["category"] for p in pols])
code, r = api("admin", "POST", f"{P}/retention/{wh['id']}/run")
code, logs = api("admin", "GET", f"{P}/destruction-logs?from={today.isoformat()}&to={today.isoformat()}")
check("Elle imha tutanağa yazıldı", code == 200 and any(l["category"] == "WebhookDeliveries" and l["trigger"] == "Manual" and l["method"] for l in logs), logs[:2] if code == 200 else code)

# --- hassas veri: şifreleme, gerekçe, erişim kaydı -------------------------------
enc = psql("""SELECT count(*) FILTER (WHERE coalesce("Iban", '') <> '' AND "Iban" NOT LIKE 'enc1:%' AND "Iban" NOT LIKE 'enc2:%') + count(*) FILTER (WHERE coalesce("NationalId", '') <> '' AND "NationalId" NOT LIKE 'enc1:%' AND "NationalId" NOT LIKE 'enc2:%') FROM engagement_profiles""")
check("TCKN/IBAN veritabanında düz metin yok", enc == "0", enc)
# Ayşe'nin mevcut IBAN'ı saklanır, test sonunda geri yazılır.
_, _orig = api("ayse", "GET", f"/api/engagement/profile/{AYSE}/reveal?field=iban")
ORIG_IBAN = (_orig or {}).get("value") or ""
# Ayşe kendi IBAN'ını kaydeder (geçerli TR IBAN örneği), maskeli görür.
code, prof = api("ayse", "PUT", "/api/engagement/profile/me", {"iban": "TR330006100519786457841326"})
check("IBAN kaydı (şifreli saklanır, maskeli döner)", code == 200 and prof["iban"].startswith("•") and prof["iban"].endswith("1326"), (code, prof.get("iban") if isinstance(prof, dict) else prof))
raw = psql(f"""SELECT left("Iban", 5) FROM engagement_profiles WHERE "EmployeeId" = '{AYSE}'""")
check("Veritabanında IBAN şifreli", raw in ("enc1:", "enc2:"), raw)  # enc2: anahtar yenilendiyse
code, r = api("ayse", "GET", f"/api/engagement/profile/{AYSE}/reveal?field=iban")
check("Kişi kendi IBAN'ını gerekçesiz açar", code == 200 and r["value"] == "TR330006100519786457841326", (code, r))
code, r = api("admin", "GET", f"/api/engagement/profile/{AYSE}/reveal?field=iban")
check("İK başkasının IBAN'ını gerekçesiz açamaz", code == 400 and r.get("code") == "reason_required", (code, r))
code, r = api("admin", "GET", f"/api/engagement/profile/{AYSE}/reveal?field=iban&reason=Maa%C5%9F%20%C3%B6demesi%20kontrol%C3%BC")
check("Gerekçeyle açılır", code == 200 and r["value"].endswith("1326"), (code, r))
code, _ = api("admin", "GET", f"/api/compensation/compensation/records?employeeId={AYSE}")
check("Ücret kaydı okundu", code == 200, code)
code, rows = api("admin", "GET", f"{P}/access-log?employeeId={AYSE}&days=1")
check("Erişim kaydı: gerekçeli IBAN açılışı", code == 200 and any(x["action"] == "Revealed" and x["reason"] == "Maaş ödemesi kontrolü" for x in rows), rows[:3] if code == 200 else code)
check("Erişim kaydı: ücret görüntüleme", any(x["action"] == "SensitiveViewed" and x["field"] == "salary" for x in rows), [x["field"] for x in rows][:5])
code, mine = api("ayse", "GET", f"{P}/access-log/me")
check("Çalışan kendi erişim kaydını görür", code == 200 and any(x["reason"] == "Maaş ödemesi kontrolü" for x in mine), code)
code, _ = api("ayse", "GET", f"{P}/access-log?employeeId={AYSE}")
check("Tüm erişim kaydı yalnızca İK", code == 403, code)
code, exp = api("ayse", "GET", f"{P}/export/{AYSE}")
iban_in_export = (exp.get("profil") or [{}])[0].get("Iban") if isinstance(exp, dict) else None
check("Kişisel veri dökümünde IBAN açık metin", iban_in_export == "TR330006100519786457841326", iban_in_export)

# --- otomatik analize itiraz ------------------------------------------------------
psql(f"""DELETE FROM governance_analysis_objections WHERE "EmployeeId" = '{AYSE}'""")
code, ob = api("ayse", "POST", f"{P}/objections", {"analysis": "AttritionRisk", "reason": "Test itirazı"})
check("İtiraz oluşturuldu", code == 200 and ob["status"] == "Open", (code, ob))
code, _ = api("ayse", "POST", f"{P}/objections", {"analysis": "AttritionRisk"})
check("Aynı analize ikinci itiraz reddedilir", code == 409, code)
code, st = api("admin", "GET", f"{P}/objections/status/{AYSE}?analysis=AttritionRisk")
check("Durum: engelli", code == 200 and st["blocked"] is True, (code, st))
code, r = api("admin", "POST", f"{P}/analysis/attrition/{AYSE}", {"features": [2.5, 1.0, 3, 12, 0, 0]})
check("İtiraz açıkken skor üretilmez", code == 409 and r.get("code") == "objection", (code, r))
code, r = api("admin", "PATCH", f"{P}/objections/{ob['id']}", {"status": "Rejected", "response": ""})
check("Gerekçesiz karar reddedilir", code == 400, code)
code, r = api("ayse", "PATCH", f"{P}/objections/{ob['id']}", {"status": "Rejected", "response": "x"})
check("Çalışan karar veremez", code == 403, code)
code, r = api("admin", "PATCH", f"{P}/objections/{ob['id']}", {"status": "Rejected", "response": "Skor yalnızca görüşme önerisi için kullanılıyor."})
check("İtiraz reddedildi", code == 200 and r["status"] == "Rejected", (code, r))
code, r = api("admin", "POST", f"{P}/analysis/attrition/{AYSE}", {"features": [2.5, 1.0, 3, 12, 0, 0]})
check("Ret sonrası analiz yapılabilir (model yüklü değilse 503)", code in (200, 503), (code, r))
if code == 200:
    code, rows = api("admin", "GET", f"{P}/access-log?employeeId={AYSE}&days=1")
    check("Otomatik analiz erişim kaydında", any(x["action"] == "AutomatedAnalysis" for x in rows), [x["action"] for x in rows][:5])
code, _ = api("ayse", "POST", f"{P}/analysis/attrition/{AYSE}", {"features": [1]})
check("Çalışan risk analizi çalıştıramaz", code == 403, code)

# --- gateway: model uçları doğrudan kapalı ------------------------------------------
code, _ = http("POST", "/ml/predict", {"features": [1, 2, 3, 4, 5, 6]})
check("/ml/predict dışarıya kapalı", code == 404, code)

# Test verisini temizle
psql(f"""DELETE FROM governance_analysis_objections WHERE "EmployeeId" = '{AYSE}'""")
psql(f"""DELETE FROM notification_messages WHERE "TemplateCode" = 'KVKK_OBJECTION' AND "RecipientEmployeeId" = '{AYSE}'""")
api("ayse", "PUT", "/api/engagement/profile/me", {"iban": ORIG_IBAN})

print(f"\nFAILS: {len(FAIL)}")
for f in FAIL:
    print("  -", f)
sys.exit(1 if FAIL else 0)

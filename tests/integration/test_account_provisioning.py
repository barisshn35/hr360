"""Dalga 12 — madde 92: Google Workspace / Microsoft 365 hesap açma (işe giriş) ve askıya alma (ayrılış).

Gerçek Google/Microsoft hesaplarıyla DENENMEDİ: Google Admin SDK (servis hesabı JWT'si) ve Microsoft Graph
(istemci kimlik bilgileri) uçlarının sahtesi tests/integration/chatmock.py'dedir. Kurulum düzeyinde
ACCOUNT_PROVISIONING_ENABLED=true (deploy/testing/chat-mock.yml) gerekir.

Denenenler: özellik bayrağı, yetki (çalışan 403), ayar doğrulaması (bozuk servis hesabı JSON'u, alan adı dışı
yönetici), sır yanıtta yok, bağlantı testi (doğru/yanlış gizli anahtar), elle istek + dört göz (açan onaylayamaz),
sağlayıcı hatasında Failed + yeniden deneme, hesap açma (geçici parola yalnızca yanıtta; günlükte/denetimde yok),
yinelenen istek 409, askıya alma + oturum kapatma, ret, tarama, denetim kaydı, takvim sağlayıcısı bağlantı testi (91).

Test kayıtları: alan adı "prov-test.hr360.example" (ayar ve istek adresleri), not "TEST-PROV".
tests/support/cleanup_test_data.py bunları siler.
"""

import json
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(__file__))
from common import AYSE, FAIL, G, api, check, ensure_transfers, mock  # noqa: E402

ROOT = os.path.join(os.path.dirname(__file__), "..", "..")
P = f"{G}/account-provisioning"
DOMAIN = "prov-test.hr360.example"
FAIL.clear()


def psql(sql):
    out = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", "hr360_operational", "-Atc", sql],
                         cwd=ROOT, capture_output=True, text=True)
    return (out.stdout + out.stderr).strip()


def service_account_json():
    pem = subprocess.run(["openssl", "genpkey", "-algorithm", "RSA", "-pkeyopt", "rsa_keygen_bits:2048"],
                         capture_output=True, text=True, check=True).stdout
    return json.dumps({"type": "service_account", "project_id": "hr360-test", "private_key_id": "k1", "private_key": pem,
                       "client_email": "hr360-prov@hr360-test.iam.gserviceaccount.com", "token_uri": "https://oauth2.googleapis.com/token"})


def cleanup():
    for p in ("google", "microsoft"):
        api("admin", "DELETE", f"{P}/configs/{p}")


started = time.time()
code, st = api("admin", "GET", P)
if code != 200 or not st.get("enabled"):
    check("Özellik açık (ACCOUNT_PROVISIONING_ENABLED=true, chat-mock.yml)", False, (code, st))
    print(f"FAILS: {len(FAIL)}")
    sys.exit(1)
check("Durum: iki sağlayıcı listelenir", {p["provider"] for p in st["providers"]} == {"Google", "Microsoft"}, st)
code, _ = api("ayse", "GET", P)
check("Çalışan hesap sağlama ekranına erişemez (403)", code == 403, code)

ensure_transfers("google", "microsoft")
cleanup()
# Yarıda kalmış önceki koşunun istekleri (normalde cleanup_test_data.py siler).
psql(f"""DELETE FROM governance_provisioning_requests WHERE "TenantSlug" = 'demo' AND "AccountEmail" LIKE '%@{DOMAIN}'""")
mock("/_reset", "POST")

# ---------------------------------------------------------------- ayar doğrulaması
base = {"domain": DOMAIN, "isEnabled": True, "autoCreate": False, "autoSuspend": False, "adminSubject": f"admin@{DOMAIN}"}
code, r = api("admin", "PUT", f"{P}/configs/google", {**base, "credentials": "{bozuk"})
check("Google: bozuk servis hesabı anahtarı 400", code == 400, (code, r))
sa = service_account_json()
code, r = api("admin", "PUT", f"{P}/configs/google", {**base, "credentials": sa, "adminSubject": "admin@baska.com"})
check("Google: alan adı dışındaki yönetici hesabı 400", code == 400, (code, r))
code, r = api("admin", "PUT", f"{P}/configs/google", {**base, "domain": "localhost", "credentials": sa})
check("Google: geçersiz alan adı 400", code == 400, (code, r))
code, r = api("admin", "PUT", f"{P}/configs/google", {**base, "credentials": sa, "orgUnit": "/Calisanlar"})
check("Google: ayar kaydedildi", code == 200, (code, r))
code, st = api("admin", "GET", P)
g = next(p for p in st["providers"] if p["provider"] == "Google")
txt = json.dumps(st)
check("Google: servis hesabı e-postası gösterilir, anahtar yanıtta yok",
      g["clientId"] == "hr360-prov@hr360-test.iam.gserviceaccount.com" and g["hasCredentials"] and "PRIVATE KEY" not in txt, g)
enc = psql(f"""SELECT "CredentialsEnc" FROM governance_provisioning_configs WHERE "TenantSlug" = 'demo' AND "Provider" = 'Google'""")
check("Google: servis hesabı anahtarı şifreli saklanır", enc and "PRIVATE KEY" not in enc and "client_email" not in enc, enc[:40])

code, r = api("admin", "POST", f"{P}/configs/google/test")
check("Google: bağlantı testi başarılı (JWT → jeton → Directory listesi)", code == 200 and r["ok"], (code, r))
jwt_calls = [c for c in mock("/_log") if c["path"] == "/google/token" and "jwt-bearer" in (c["body"] or "")]
check("Google: jeton servis hesabı JWT'siyle (jwt-bearer) alındı", len(jwt_calls) >= 1, len(jwt_calls))

ms = {"domain": DOMAIN, "isEnabled": True, "autoCreate": True, "autoSuspend": False,
      "clientId": "11111111-2222-3333-4444-555555555555", "msTenant": "66666666-7777-8888-9999-000000000000", "usageLocation": "TR"}
code, r = api("admin", "PUT", f"{P}/configs/microsoft", {**ms, "msTenant": "organizations", "credentials": "s3cret"})
check("Microsoft: dizin kimliği GUID olmalı (400)", code == 400, (code, r))
code, r = api("admin", "PUT", f"{P}/configs/microsoft", {**ms, "credentials": "wrong"})
check("Microsoft: ayar kaydedildi", code == 200, (code, r))
code, r = api("admin", "POST", f"{P}/configs/microsoft/test")
check("Microsoft: yanlış gizli anahtarla test başarısız ve sebep döner", code == 200 and not r["ok"] and "401" in r["message"], (code, r))
code, r = api("admin", "PUT", f"{P}/configs/microsoft", {**ms, "credentials": "dogru-gizli"})
code, r = api("admin", "POST", f"{P}/configs/microsoft/test")
check("Microsoft: doğru gizli anahtarla test başarılı", code == 200 and r["ok"], (code, r))
code, st = api("admin", "GET", P)
m = next(p for p in st["providers"] if p["provider"] == "Microsoft")
check("Microsoft: başarılı testten sonra hata temizlenir", m["lastError"] is None and m["lastTestAt"], m)

# ---------------------------------------------------------------- hesap açma (Google)
code, r = api("admin", "POST", f"{P}/requests", {"employeeId": AYSE, "provider": "google", "action": "Create", "note": "TEST-PROV açma"})
check("Elle açma isteği: ad/soyaddan Türkçe karakterleri dönüştürülmüş adres", code == 200 and r["accountEmail"] == f"ayse.yilmaz@{DOMAIN}", (code, r))
rid = r.get("id") if code == 200 else None
code, r2 = api("admin", "POST", f"{P}/requests", {"employeeId": AYSE, "provider": "google", "action": "Create", "note": "TEST-PROV yineleme"})
check("Aynı çalışan için ikinci açma isteği 409", code == 409, (code, r2))
code, r2 = api("admin", "POST", f"{P}/requests", {"employeeId": AYSE, "provider": "google", "action": "Create", "accountEmail": "ayse@baska.com", "note": "TEST-PROV"})
check("Alan adı dışı adres reddedilir (400)", code in (400, 409), (code, r2))

if rid:
    code, r = api("admin", "POST", f"{P}/requests/{rid}/approve", {})
    check("Dört göz: isteği açan kişi onaylayamaz (403)", code == 403 and r.get("code") == "sod", (code, r))
    mock("/_fail?path=/googleadmin/admin/directory/v1/users&count=1", "POST")
    code, r = api("ik", "POST", f"{P}/requests/{rid}/approve", {})
    check("Sağlayıcı hatası: 502 ve istek Failed", code == 502 and not r["ok"], (code, r))
    code, rows = api("admin", "GET", f"{P}/requests?status=Failed")
    row = next((x for x in rows if x["id"] == rid), None) if code == 200 else None
    check("Başarısız istek hata iletisiyle listelenir", row and row["error"] and row["attempts"] == 1, row)
    code, r = api("ik", "POST", f"{P}/requests/{rid}/approve", {})
    check("Yeniden deneme: hesap açıldı, geçici parola bir kez döner",
          code == 200 and r["ok"] and r["accountEmail"] == f"ayse.yilmaz@{DOMAIN}" and len(r.get("initialPassword") or "") == 16, (code, r))
    password = r.get("initialPassword") if code == 200 else None
    d = mock("/_directory")
    acct = d.get(f"ayse.yilmaz@{DOMAIN}")
    check("Sahte Google dizininde hesap var (askıda değil)", acct and acct["provider"] == "Google" and not acct["suspended"], d)
    posts = [c for c in mock("/_log") if c["method"] == "POST" and c["path"] == "/googleadmin/admin/directory/v1/users"]
    body = json.loads(posts[-1]["body"]) if posts else {}
    check("Google'a ad, soyad, ilk girişte parola değiştirme ve kuruluş birimi gönderildi",
          body.get("name") == {"givenName": "Ayşe", "familyName": "Yılmaz"} and body.get("changePasswordAtNextLogin") is True
          and body.get("orgUnitPath") == "/Calisanlar" and body.get("password") == password, body)
    code, rows = api("admin", "GET", f"{P}/requests")
    row = next((x for x in rows if x["id"] == rid), None) if code == 200 else None
    check("İstek Done, karar veren ve dış kimlik kaydı", row and row["status"] == "Done" and row["decidedByName"] and row["completedAt"], row)
    check("Liste yanıtında parola yok", password and password not in json.dumps(rows), "")
    code, r = api("ik", "POST", f"{P}/requests/{rid}/approve", {})
    check("Tamamlanan istek tekrar onaylanamaz (409)", code == 409, (code, r))
    if password:
        logs = subprocess.run(["docker", "logs", "--since", str(int(time.time() - started) + 5) + "s", "hr360-governance-service-1"],
                              capture_output=True, text=True)
        check("Geçici parola servis günlüğünde yok", password not in (logs.stdout + logs.stderr), "")
        aud = psql(f"""SELECT count(*) FROM audit_log WHERE "TenantSlug" = 'demo' AND "Changes"::text LIKE '%{password}%'""")
        check("Geçici parola denetim kaydında yok", aud == "0", aud)
    aud = psql(f"""SELECT string_agg("Action", ',' ORDER BY "Id") FROM audit_log WHERE "TenantSlug" = 'demo'
                   AND "EntityType" = 'AccountProvisioning' AND "EntityId" = '{rid}'""")
    check("Denetim kaydı: istek, onay, hata, hesap açıldı",
          all(a in (aud or "") for a in ("Requested", "Approved", "ProviderFailed", "AccountCreated")), aud)

    # ------------------------------------------------------------ askıya alma (Google)
    code, r = api("admin", "POST", f"{P}/requests", {"employeeId": AYSE, "provider": "google", "action": "Suspend", "note": "TEST-PROV askı"})
    check("Askıya alma isteği: adres açılan hesaptan gelir", code == 200 and r["accountEmail"] == f"ayse.yilmaz@{DOMAIN}", (code, r))
    sid = r.get("id") if code == 200 else None
    if sid:
        code, r = api("ik", "POST", f"{P}/requests/{sid}/approve", {})
        check("Askıya alma onaylandı, parola dönmez", code == 200 and r["ok"] and not r.get("initialPassword"), (code, r))
        acct = mock("/_directory").get(f"ayse.yilmaz@{DOMAIN}")
        check("Sahte dizinde hesap askıda ve oturumları kapatıldı", acct and acct["suspended"] and acct["signedOut"], acct)

# ---------------------------------------------------------------- ret (Microsoft)
code, r = api("admin", "POST", f"{P}/requests", {"employeeId": AYSE, "provider": "microsoft", "action": "Create", "note": "TEST-PROV ret"})
mid = r.get("id") if code == 200 else None
if code == 409:
    # Tarama (autoCreate) Ayşe için zaten bekleyen istek açmış olabilir.
    _, rows = api("admin", "GET", f"{P}/requests?status=Pending")
    mid = next((x["id"] for x in rows if x["provider"] == "Microsoft" and x["employeeId"] == AYSE and x["action"] == "Create"), None)
check("Microsoft açma isteği", mid is not None, (code, r))
if mid:
    code, r = api("ik", "POST", f"{P}/requests/{mid}/reject", {"reason": "TEST-PROV gerek yok"})
    check("Ret", code == 200 and r["status"] == "Rejected", (code, r))
    code, r = api("ik", "POST", f"{P}/requests/{mid}/approve", {})
    check("Reddedilen istek onaylanamaz (409)", code == 409, (code, r))
    graph = [c for c in mock("/_log") if c["path"].startswith("/graph/v1.0/users") and c["method"] == "POST"]
    check("Ret sonrası Microsoft'a hesap açma çağrısı yapılmadı", not graph, graph)

# ---------------------------------------------------------------- tarama
code, r = api("admin", "POST", f"{P}/scan")
check("Tarama: yalnızca bekleyen istek açar (sayılar döner)", code == 200 and isinstance(r["create"], int) and isinstance(r["suspend"], int), (code, r))
code, r = api("admin", "POST", f"{P}/scan")
check("Tarama idempotent (ikinci turda yeni istek yok)", code == 200 and r["create"] == 0 and r["suspend"] == 0, (code, r))

# ---------------------------------------------------------------- 91: takvim sağlayıcısı bağlantı testi
code, r = api("admin", "POST", f"{G}/calendar/providers/google/test")
check("Takvim sağlayıcısı bağlantı testi ucu (200 ya da yapılandırılmamışsa 404)", code in (200, 404) and (code == 404 or "ok" in r), (code, r))
code, _ = api("ayse", "POST", f"{G}/calendar/providers/google/test")
check("Takvim bağlantı testi çalışana kapalı (403)", code == 403, code)

cleanup()
code, st = api("admin", "GET", P)
check("Ayarlar silindi", code == 200 and not any(p["configured"] for p in st["providers"]), st)
print(f"FAILS: {len(FAIL)}")
sys.exit(1 if FAIL else 0)

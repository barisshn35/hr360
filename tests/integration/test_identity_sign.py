#!/usr/bin/env python3
"""Dalga 5d kimlik ve imza:
Y26 SCIM 2.0 (jeton, keşif uçları, Users CRUD + filtre + sayfalama, PATCH/PUT, active=false ve
    DELETE = hesap kapatma, yalnızca en az veri saklanır) ve LDAP/AD eşitlemesi (OpenLDAP test
    konteyneri: bağlantı testi, dry-run, eşitleme, güncelleme, dizinden silinenin kapatılması),
G28 kiracıya özel alan adı (kayıt, yetki, DNS TXT doğrulaması, psql ile doğrulanmış alan adı,
    herkese açık marka uç noktası),
Y28 OTP ile basit elektronik imza (talep, kod, süre aşımı, 5 deneme, kanıt özeti, değiştirilemezlik).

Ön koşul: HR360 çalışıyor; deploy/testing/chat-mock.yml katmanı açık (tenant-service TEST modu)
ve OpenLDAP test konteyneri: docker compose -f docker-compose.yml -f deploy/testing/chat-mock.yml up -d openldap
Oluşturulan Keycloak kullanıcıları, çalışan kayıtları ve satırlar test sonunda silinir.
"""
import datetime as dt
import hashlib
import json
import os
import re
import subprocess
import sys
import time
import uuid
from urllib.parse import quote

sys.path.insert(0, os.path.dirname(__file__))
from common import AYSE, FAIL, api, check, http, tok  # noqa: E402

ROOT = os.path.join(os.path.dirname(__file__), "..", "..")
FAIL.clear()
RUN = uuid.uuid4().hex[:6].lower()
from demo_ids import MEHMET  # noqa: E402
T = "/api/tenant"
SCIM = f"{T}/scim/v2"
DIR = f"{T}/my-tenant/directory"
DOM = f"{T}/my-tenant/domains"
EXP = "/api/expense/documents"
START = dt.datetime.now(dt.timezone.utc) - dt.timedelta(seconds=5)
STARTS = START.strftime("%Y-%m-%d %H:%M:%S+00")
LDAP_ADMIN_PW = os.environ.get("LDAP_TEST_ADMIN_PASSWORD", "test-only-ldap-admin")
LDAP_BASE = "ou=people,dc=hr360test,dc=local"
LDAP_BIND = "cn=admin,dc=hr360test,dc=local"
SEED = os.path.join(os.path.dirname(__file__), "ldap", "seed.ldif")
DISCLAIMER = "Basit elektronik imza — 5070 sayılı Kanun kapsamında nitelikli (güvenli) elektronik imza değildir."


def psql(sql, db="hr360_operational"):
    out = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", db, "-Atc", sql],
                         capture_output=True, text=True, cwd=ROOT)
    return (out.stdout + out.stderr).strip()


def kcadm(*args):
    script = ('/opt/keycloak/bin/kcadm.sh config credentials --config /tmp/kc-ids.cfg --server http://localhost:8080/auth '
              '--realm master --user "$KEYCLOAK_ADMIN" --password "$KEYCLOAK_ADMIN_PASSWORD" >/dev/null 2>&1 && '
              '/opt/keycloak/bin/kcadm.sh ' + " ".join(args) + ' --config /tmp/kc-ids.cfg -r hr360')
    return subprocess.run(["docker", "exec", "hr360-keycloak-1", "sh", "-c", script], capture_output=True, text=True)


def ldap(tool, ldif=None, *args):
    cmd = ["docker", "exec", "-i", "hr360-openldap-1", tool, "-x", "-H", "ldap://localhost:1389", "-D", LDAP_BIND, "-w", LDAP_ADMIN_PW, *args]
    return subprocess.run(cmd, input=ldif, capture_output=True, text=True)


def seed_entry(uid):
    """seed.ldif'ten tek bir kişinin LDIF bloğu (yeniden eklemek için)."""
    blocks = open(SEED, encoding="utf-8").read().split("\n\n")
    return next(b for b in blocks if f"dn: uid={uid}," in b) + "\n"


def scim(method, path, body=None, token=None):
    return http(method, SCIM + path, body, {"Authorization": "Bearer " + (token or SCIM_TOKEN)})


def wait(pred, timeout=25):
    end = time.time() + timeout
    while time.time() < end:
        if pred():
            return True
        time.sleep(1)
    return False


def kc_user(kc_id):
    return psql(f"SELECT enabled || '|' || coalesce(email,'') || '|' || coalesce(first_name,'') || '|' || coalesce(last_name,'') FROM user_entity WHERE id = '{kc_id}'", db="keycloak")


created = {"kc": set(), "employees": set(), "docs": set(), "tokens": set(), "domains": set()}
SCIM_TOKEN = ""


def cleanup():
    for kc_id in created["kc"]:
        kcadm("delete", f"users/{kc_id}")
    # Dizin kullanıcılarının Keycloak hesapları (test e-postalarıyla) da silinir.
    for kc_id in psql(f"""SELECT "KeycloakUserId" FROM tenant_directory_users WHERE "TenantSlug" = 'demo'
                          AND ("Email" LIKE 'test.scim%{RUN}@example.com' OR "Email" LIKE 'testldap.%@example.com')
                          AND "KeycloakUserId" IS NOT NULL""").split():
        kcadm("delete", f"users/{kc_id}")
    emps = set(created["employees"]) | set(psql(
        """SELECT "Id" FROM employee_employees WHERE "TenantSlug" = 'demo' AND ("Email" LIKE 'test.scim%@example.com' OR "Email" LIKE 'testldap.%@example.com')""").split())
    if emps:
        ids = ",".join(f"'{e}'" for e in emps)
        # Çalışan olayıyla diğer servislerin açtığı satırlar (bakiye, bildirim...) da yalnızca bu test çalışanları için silinir.
        tables = psql("""SELECT table_name FROM information_schema.columns WHERE column_name = 'EmployeeId' AND table_schema = 'public'
                         AND data_type = 'uuid' AND table_name NOT IN ('audit_log')""").split()
        for t in tables:
            psql(f"""DELETE FROM "{t}" WHERE "EmployeeId" IN ({ids})""")
        psql(f"""DELETE FROM notification_messages WHERE "RecipientEmployeeId" IN ({ids})""")
        psql(f"""DELETE FROM employee_assignments WHERE "EmployeeId" IN ({ids}); DELETE FROM employee_employees WHERE "Id" IN ({ids})""")
    psql(f"""DELETE FROM tenant_directory_users WHERE "TenantSlug" = 'demo' AND ("Email" LIKE 'test.scim%@example.com' OR "Email" LIKE 'testldap.%@example.com'
             OR "UserName" LIKE 'testldap.%')""")
    psql("""DELETE FROM tenant_directory_settings WHERE "TenantSlug" = 'demo'""")
    psql(f"""DELETE FROM tenant_scim_tokens WHERE "Name" LIKE 'TEST-scim-{RUN}%'""")
    psql(f"""DELETE FROM tenant_custom_domains WHERE "Domain" LIKE '%{RUN}.hr360-test.com.tr'""")
    for d in created["docs"]:
        # expense_documents silme tetikleyicisi governance_signatures/otps (HrDocument) satırlarını da siler.
        psql(f"""DELETE FROM expense_signature_evidence WHERE "DocumentId" = '{d}'; DELETE FROM expense_document_signatures WHERE "DocumentId" = '{d}';
                 DELETE FROM expense_documents WHERE "Id" = '{d}'""")
    psql(f"""DELETE FROM notification_messages WHERE "CreatedAt" >= '{STARTS}' AND "TemplateCode" LIKE 'document.sign.%'
             AND "RecipientEmployeeId" IN ('{AYSE}', '{MEHMET}')""")
    # İmza kodları governance'tan 'signature.otp' ile gelir (yalnızca bu testin belgesi).
    psql(f"""DELETE FROM notification_messages WHERE "CreatedAt" >= '{STARTS}' AND "TemplateCode" = 'signature.otp'
             AND "RecipientEmployeeId" = '{AYSE}' AND "Body" LIKE '%TEST-imza-{RUN}.pdf%'""")
    subprocess.run(["docker", "exec", "hr360-tenant-service-1", "sh", "-c", "echo '{}' > /tmp/hr360-dns-override.json"], capture_output=True)
    # LDAP test verisini tohum hâline döndür.
    ldap("ldapadd", seed_entry("testldap.bora"))
    ldap("ldapmodify", f"dn: uid=testldap.ada,{LDAP_BASE}\nchangetype: modify\nreplace: title\ntitle: TEST Uzman\n")


try:
    # ================================================================== Y26 SCIM 2.0
    code, r = api("mehmet", "POST", f"{DIR}/scim-tokens", {"name": f"TEST-scim-{RUN}"})
    check("Y26 yönetici olmayan SCIM jetonu üretemez", code == 403, (code, r))
    code, r = api("admin", "POST", f"{DIR}/scim-tokens", {"name": f"TEST-scim-{RUN}"})
    check("Y26 tenant-admin SCIM jetonu üretir (bir kez gösterilir)", code == 200 and r["token"].startswith("hr360scim_"), (code, r))
    SCIM_TOKEN = r["token"]
    TOKEN_ID = r["id"]
    sha = hashlib.sha256(SCIM_TOKEN.encode()).hexdigest()
    check("Y26 veritabanında yalnızca SHA-256 özeti", psql(f"""SELECT count(*) FROM tenant_scim_tokens WHERE "TokenHash" = '{sha}'""") == "1"
          and psql(f"""SELECT count(*) FROM tenant_scim_tokens t WHERE row_to_json(t)::text LIKE '%{SCIM_TOKEN[12:]}%'""") == "0")
    code, lst = api("admin", "GET", f"{DIR}/scim-tokens")
    check("Y26 jeton listesinde açık jeton yok", code == 200 and any(t["id"] == TOKEN_ID for t in lst) and SCIM_TOKEN not in json.dumps(lst), lst)

    code, r = scim("GET", "/ServiceProviderConfig")
    dm = (r or {}).get("urn:hr360:params:scim:dataMinimisation") or {}
    check("Y26 ServiceProviderConfig (patch, filtre, veri minimizasyonu ilanı)", code == 200 and r["patch"]["supported"] and r["filter"]["supported"]
          and not r["bulk"]["supported"] and "title" in dm.get("storedAttributes", []) and "saklanmaz" in dm.get("notice", ""), (code, r))
    code, r = scim("GET", "/ResourceTypes")
    check("Y26 ResourceTypes yalnızca User", code == 200 and r["totalResults"] == 1 and r["Resources"][0]["id"] == "User", r)
    code, r = scim("GET", "/Schemas")
    user_schema = next((s for s in (r or {}).get("Resources", []) if s["id"].endswith("core:2.0:User")), {})
    names = {a["name"] for a in user_schema.get("attributes", [])}
    check("Y26 Schemas: telefon/adres ilan edilmez", code == 200 and {"userName", "name", "emails", "title", "active"} <= names
          and "phoneNumbers" not in names and "addresses" not in names, names)
    check("Y26 jetonsuz istek 401", scim("GET", "/Users", token="x")[0] == 401 and http("GET", SCIM + "/Users")[0] == 401)
    check("Y26 Keycloak JWT'si SCIM'de geçmez", http("GET", SCIM + "/Users", headers={"Authorization": "Bearer " + tok("admin")})[0] == 401)

    email1 = f"test.scim.{RUN}@example.com"
    body1 = {
        "schemas": ["urn:ietf:params:scim:schemas:core:2.0:User", "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User"],
        "userName": email1, "externalId": f"ext-{RUN}", "active": True, "title": "TEST Uzman",
        "name": {"givenName": "TestScim", "familyName": "Bir", "middleName": "GIZLIORTA"},
        "emails": [{"value": f"ev.{RUN}@example.org", "type": "home"}, {"value": email1, "type": "work", "primary": True}],
        "phoneNumbers": [{"value": "+90 555 999 88 77", "type": "mobile"}],
        "addresses": [{"streetAddress": "GIZLI Sokak 5", "locality": "İstanbul"}],
        "photos": [{"value": "https://example.com/photo.jpg"}], "nickName": "GIZLILAKAP",
        "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User": {"department": "Mühendislik", "employeeNumber": f"EMPNO-{RUN}", "manager": {"value": "m-1"}},
    }
    code, u1 = scim("POST", "/Users", body1)
    check("Y26 POST Users 201", code == 201 and u1["userName"] == email1 and u1["active"] is True, (code, u1))
    U1 = u1["id"] if code == 201 else str(uuid.uuid4())
    check("Y26 yanıtta yalnızca en az nitelikler", code == 201 and "phoneNumbers" not in u1 and "addresses" not in u1 and "photos" not in u1
          and "nickName" not in u1 and u1["title"] == "TEST Uzman" and len(u1["emails"]) == 1 and u1["emails"][0]["value"] == email1
          and u1["urn:ietf:params:scim:schemas:extension:enterprise:2.0:User"] == {"department": "Mühendislik"}, u1)
    row = psql(f"""SELECT row_to_json(u)::text FROM tenant_directory_users u WHERE "Id" = '{U1}'""")
    # Telefon tam parçasıyla aranır: yalın "555" zaman damgasının mikrosaniyesine de uyabilir (…17.354555).
    check("Y26 dizin satırında telefon/adres/foto/yönetici/çalışan no yok",
          row and all(x not in row for x in ["555 999", "GIZLI", "photo", f"EMPNO-{RUN}", "m-1", "example.org"]), row)
    cols = set(psql("""SELECT column_name FROM information_schema.columns WHERE table_name = 'tenant_directory_users'""").split())
    check("Y26 tabloda telefon/adres sütunu yok", cols and not ({"Phone", "Address", "HireDate"} & cols), cols)
    KC1 = psql(f"""SELECT "KeycloakUserId" FROM tenant_directory_users WHERE "Id" = '{U1}'""")
    if KC1:
        created["kc"].add(KC1)
    check("Y26 Keycloak hesabı açıldı (etkin, ad/e-posta)", kc_user(KC1).startswith(f"true|{email1}|TestScim|Bir"), kc_user(KC1))
    check("Y26 Keycloak'a telefon yazılmadı", psql(f"""SELECT count(*) FROM user_attribute WHERE user_id = '{KC1}' AND (value LIKE '%555%' OR value LIKE '%GIZLI%')""", db="keycloak") == "0")
    check("Y26 Keycloak hesabı employee rolünde", psql(f"""SELECT count(*) FROM user_role_mapping m JOIN keycloak_role r ON r.id = m.role_id
                                                         WHERE m.user_id = '{KC1}' AND r.name = 'employee'""", db="keycloak") == "1")
    check("Y26 hesap şirket organizasyonunun üyesi", psql(f"""SELECT count(*) FROM org o JOIN user_group_membership g ON g.group_id = o.group_id
                                                           WHERE o.name = 'demo' AND g.user_id = '{KC1}'""", db="keycloak") == "1",
          psql(f"""SELECT count(*) FROM org o JOIN user_group_membership g ON g.group_id = o.group_id WHERE o.name = 'demo' AND g.user_id = '{KC1}'""", db="keycloak"))
    E1 = psql(f"""SELECT "Id" FROM employee_employees WHERE "TenantSlug" = 'demo' AND "KeycloakUserId" = '{KC1}'""")
    if E1:
        created["employees"].add(E1)
    emp = psql(f"""SELECT "FirstName" || '|' || "LastName" || '|' || "Email" || '|' || coalesce("Phone", '-') FROM employee_employees WHERE "Id" = '{E1}'""") if E1 else ""
    check("Y26 çalışan kaydı oluşturuldu (telefonsuz)", emp == f"TestScim|Bir|{email1}|-", emp)
    asg = psql(f"""SELECT d."Name" || '|' || a."PositionTitle" FROM employee_assignments a JOIN organization_departments d ON d."Id" = a."DepartmentId"
                   WHERE a."EmployeeId" = '{E1}' AND a."EffectiveTo" IS NULL""") if E1 else ""
    check("Y26 departman ve unvan atandı", asg == "Mühendislik|TEST Uzman", asg)
    check("Y26 dizin satırı çalışana bağlı", psql(f"""SELECT "EmployeeState" || '|' || "EmployeeId" FROM tenant_directory_users WHERE "Id" = '{U1}'""") == f"Linked|{E1}")

    code, r = scim("POST", "/Users", body1)
    check("Y26 aynı userName 409 uniqueness", code == 409 and r.get("scimType") == "uniqueness", (code, r))
    email2 = f"test.scim2.{RUN}@example.com"
    code, u2 = scim("POST", "/Users", {"userName": email2, "name": {"givenName": "TestScim", "familyName": "Iki"}, "emails": [{"value": email2}]})
    check("Y26 ikinci kullanıcı (unvansız, departmansız)", code == 201, (code, u2))
    U2 = u2["id"] if code == 201 else str(uuid.uuid4())
    KC2 = psql(f"""SELECT "KeycloakUserId" FROM tenant_directory_users WHERE "Id" = '{U2}'""")
    if KC2:
        created["kc"].add(KC2)
    code, r = http("GET", SCIM + "/Users?filter=" + quote(f'userName eq "{email1}"'), headers={"Authorization": "Bearer " + SCIM_TOKEN})
    check("Y26 filtre userName eq", code == 200 and r["totalResults"] == 1 and r["Resources"][0]["id"] == U1, (code, r))
    code, r = http("GET", SCIM + "/Users?filter=" + quote(f'userName eq "yok.{RUN}@example.com"'), headers={"Authorization": "Bearer " + SCIM_TOKEN})
    check("Y26 eşleşmeyen filtre boş liste", code == 200 and r["totalResults"] == 0 and r["Resources"] == [], r)
    code, r = http("GET", SCIM + "/Users?filter=" + quote('userName co "test"'), headers={"Authorization": "Bearer " + SCIM_TOKEN})
    check("Y26 desteklenmeyen filtre 400 invalidFilter", code == 400 and r.get("scimType") == "invalidFilter", (code, r))
    c1, p1 = scim("GET", "/Users?startIndex=1&count=1")
    c2, p2 = scim("GET", "/Users?startIndex=2&count=1")
    check("Y26 startIndex/count sayfalama", c1 == 200 and c2 == 200 and p1["itemsPerPage"] == 1 and p1["totalResults"] >= 2
          and p2["startIndex"] == 2 and p1["Resources"][0]["id"] != p2["Resources"][0]["id"], (p1, p2))
    code, r = scim("GET", f"/Users/{U1}")
    check("Y26 GET Users/{id}", code == 200 and r["id"] == U1 and r["externalId"] == f"ext-{RUN}", r)
    check("Y26 bilinmeyen id 404", scim("GET", f"/Users/{uuid.uuid4()}")[0] == 404)

    put = dict(body1, title="TEST Kıdemli Uzman", name={"givenName": "TestScimX", "familyName": "Bir"})
    code, r = scim("PUT", f"/Users/{U1}", put)
    check("Y26 PUT ad ve unvan günceller", code == 200 and r["name"]["givenName"] == "TestScimX" and r["title"] == "TEST Kıdemli Uzman", (code, r))
    check("Y26 PUT Keycloak adını günceller", kc_user(KC1).startswith(f"true|{email1}|TestScimX|"), kc_user(KC1))
    check("Y26 PUT çalışan kaydını ve unvanı günceller",
          psql(f"""SELECT "FirstName" FROM employee_employees WHERE "Id" = '{E1}'""") == "TestScimX"
          and psql(f"""SELECT "PositionTitle" FROM employee_assignments WHERE "EmployeeId" = '{E1}' AND "EffectiveTo" IS NULL""") == "TEST Kıdemli Uzman")

    email1b = f"test.scim.yeni.{RUN}@example.com"
    code, r = scim("PATCH", f"/Users/{U1}", {"schemas": ["urn:ietf:params:scim:api:messages:2.0:PatchOp"], "Operations": [
        {"op": "Replace", "path": "name.familyName", "value": "Yeni"},
        {"op": "replace", "path": 'emails[type eq "work"].value', "value": email1b},
        {"op": "add", "path": "phoneNumbers", "value": [{"value": "+90 555 111 22 33"}]}]})
    check("Y26 PATCH ad ve e-posta (Entra biçimi)", code == 200 and r["name"]["familyName"] == "Yeni" and r["emails"][0]["value"] == email1b, (code, r))
    check("Y26 PATCH Keycloak e-postası/soyadı güncellendi", kc_user(KC1) == f"true|{email1b}|TestScimX|Yeni", kc_user(KC1))
    check("Y26 PATCH çalışan e-postası güncellendi", psql(f"""SELECT "Email" || '|' || "LastName" FROM employee_employees WHERE "Id" = '{E1}'""") == f"{email1b}|Yeni")
    check("Y26 PATCH ile gelen telefon saklanmadı", "555 111" not in psql(f"""SELECT row_to_json(u)::text FROM tenant_directory_users u WHERE "Id" = '{U1}'"""))

    code, r = scim("PATCH", f"/Users/{U1}", {"Operations": [{"op": "Replace", "value": {"active": "False"}}]})
    check("Y26 PATCH active=false", code == 200 and r["active"] is False, (code, r))
    check("Y26 active=false Keycloak hesabını kapatır (e-posta/ad korunur)", kc_user(KC1) == f"false|{email1b}|TestScimX|Yeni", kc_user(KC1))
    code, r = scim("PATCH", f"/Users/{U1}", {"Operations": [{"op": "replace", "path": "active", "value": True}]})
    check("Y26 PATCH active=true hesabı açar", code == 200 and r["active"] is True and kc_user(KC1).startswith("true|"), (code, kc_user(KC1)))

    code, _ = scim("DELETE", f"/Users/{U2}")
    check("Y26 DELETE 204 (hesap kapatma)", code == 204, code)
    code, r = scim("GET", f"/Users/{U2}")
    check("Y26 DELETE sonrası kayıt active=false, Keycloak kapalı", code == 200 and r["active"] is False and kc_user(KC2).startswith("false|"), (r, kc_user(KC2)))
    E2 = psql(f"""SELECT "Id" FROM employee_employees WHERE "TenantSlug" = 'demo' AND "KeycloakUserId" = '{KC2}'""")
    if E2:
        created["employees"].add(E2)
    check("Y26 DELETE çalışan kaydını silmez", bool(E2), E2)

    code, r = api("admin", "GET", f"{DIR}/users?source=scim")
    check("Y26 dizin kullanıcıları listesi (İK)", code == 200 and any(i["id"] == U1 and i["employeeState"] == "Linked" for i in r["items"]), (code, r))
    check("Y26 çalışan dizin listesini göremez", api("ayse", "GET", f"{DIR}/users")[0] == 403)

    code, _ = api("admin", "DELETE", f"{DIR}/scim-tokens/{TOKEN_ID}")
    check("Y26 jeton iptal edilir; sonrasında 401", code == 200 and scim("GET", "/ServiceProviderConfig")[0] == 401)

    # ================================================================== Y26 LDAP / AD
    ldap("ldapadd", seed_entry("testldap.bora"))  # önceki koşudan silinmiş olabilir
    up = subprocess.run(["docker", "inspect", "-f", "{{.State.Running}}", "hr360-openldap-1"], capture_output=True, text=True).stdout.strip()
    check("Y26 OpenLDAP test konteyneri çalışıyor", up == "true", up)
    ldap_cfg = {"enabled": True, "autoSync": False, "url": "ldap://openldap:1389", "bindDn": LDAP_BIND, "bindPassword": LDAP_ADMIN_PW,
                "baseDn": LDAP_BASE, "userFilter": "(objectClass=inetOrgPerson)", "usernameAttr": "uid", "emailAttr": "mail",
                "departmentAttr": "departmentNumber", "titleAttr": "title", "disabledAttr": None}
    check("Y26 çalışan LDAP ayarı yapamaz", api("mehmet", "PUT", f"{DIR}/settings", {"ldap": ldap_cfg})[0] == 403)
    code, r = api("admin", "PUT", f"{DIR}/settings", {"ldap": dict(ldap_cfg, url="http://openldap")})
    check("Y26 geçersiz şema reddedilir", code == 400, (code, r))
    code, r = api("admin", "PUT", f"{DIR}/settings", {"ldap": ldap_cfg})
    check("Y26 ldap:// kaydedilir ama uyarı verilir", code == 200 and "ldaps://" in (r.get("warning") or ""), (code, r))
    enc = psql("""SELECT "LdapBindPasswordEncrypted" FROM tenant_directory_settings WHERE "TenantSlug" = 'demo'""")
    check("Y26 bağlama parolası şifreli saklanır", enc and LDAP_ADMIN_PW not in enc and len(enc) > 30, enc[:12])
    code, r = api("admin", "GET", f"{DIR}/settings")
    check("Y26 ayar görünümünde parola yok", code == 200 and r["ldap"]["hasPassword"] and LDAP_ADMIN_PW not in json.dumps(r), r)

    api("admin", "PUT", f"{DIR}/settings", {"ldap": dict(ldap_cfg, url="ldap://keycloak:389", bindPassword=None)})
    code, r = api("admin", "POST", f"{DIR}/ldap/test")
    check("Y26 SSRF: iç ağ sunucusu reddedilir", code == 400 and "iç ağ" in r.get("message", ""), (code, r))
    api("admin", "PUT", f"{DIR}/settings", {"ldap": dict(ldap_cfg, bindPassword="yanlis-parola")})
    code, r = api("admin", "POST", f"{DIR}/ldap/test")
    check("Y26 yanlış bağlama parolası anlaşılır hata", code == 400 and "kimlik bilgileri" in r.get("message", ""), (code, r))
    api("admin", "PUT", f"{DIR}/settings", {"ldap": ldap_cfg})
    code, r = api("admin", "POST", f"{DIR}/ldap/test")
    check("Y26 bağlantı testi: 2 TEST kullanıcı", code == 200 and r["ok"] and r["entries"] == 2 and "testldap.ada" in r["sample"] and "ldaps://" in (r.get("warning") or ""), (code, r))

    code, r = api("admin", "POST", f"{DIR}/ldap/sync", {"dryRun": True, "forceDisable": False})
    check("Y26 dry-run 2 oluşturma raporlar", code == 200 and r["dryRun"] and r["created"] == 2 and {a["userName"] for a in r["actions"] if a["kind"] == "Create"} == {"testldap.ada", "testldap.bora"}, (code, r))
    check("Y26 dry-run hiçbir şey oluşturmaz", psql("""SELECT count(*) FROM tenant_directory_users WHERE "TenantSlug" = 'demo' AND "Source" = 'ldap'""") == "0"
          and psql("SELECT count(*) FROM user_entity WHERE email LIKE 'testldap.%@example.com'", db="keycloak") == "0")
    code, r = api("admin", "POST", f"{DIR}/ldap/sync", {"dryRun": False, "forceDisable": False})
    check("Y26 eşitleme 2 kullanıcı oluşturur", code == 200 and r["created"] == 2 and not r["errors"], (code, r))
    rows = psql("""SELECT "UserName" || '|' || coalesce("Title",'-') || '|' || coalesce("Department",'-') || '|' || "EmployeeState" || '|' || "Active"
                   FROM tenant_directory_users WHERE "TenantSlug" = 'demo' AND "Source" = 'ldap' ORDER BY "UserName" """).splitlines()
    check("Y26 LDAP kullanıcıları (unvan, departman, çalışan bağlı)",
          rows == ["testldap.ada|TEST Uzman|Mühendislik|Linked|true", "testldap.bora|TEST Analist|-|Linked|true"], rows)
    ldap_rows = psql("""SELECT string_agg(row_to_json(u)::text, ' ') FROM tenant_directory_users u WHERE "TenantSlug" = 'demo' AND "Source" = 'ldap'""")
    check("Y26 LDAP'tan telefon/adres alınmadı", "555 000" not in ldap_rows and "Sokak" not in ldap_rows, ldap_rows[:200])
    kc_ada = psql("""SELECT "KeycloakUserId" FROM tenant_directory_users WHERE "TenantSlug" = 'demo' AND "UserName" = 'testldap.ada'""")
    kc_bora = psql("""SELECT "KeycloakUserId" FROM tenant_directory_users WHERE "TenantSlug" = 'demo' AND "UserName" = 'testldap.bora'""")
    created["kc"].update(x for x in (kc_ada, kc_bora) if x)
    check("Y26 LDAP Keycloak hesapları etkin", kc_user(kc_ada).startswith("true|testldap.ada@example.com|TestLdap|Ada") and kc_user(kc_bora).startswith("true|"),
          (kc_user(kc_ada), kc_user(kc_bora)))
    e_ada = psql(f"""SELECT "Id" FROM employee_employees WHERE "KeycloakUserId" = '{kc_ada}'""")
    if e_ada:
        created["employees"].add(e_ada)
    check("Y26 LDAP çalışanı departman + unvanla oluştu (telefonsuz)",
          psql(f"""SELECT coalesce(e."Phone",'-') || '|' || d."Name" || '|' || a."PositionTitle" FROM employee_employees e
                   JOIN employee_assignments a ON a."EmployeeId" = e."Id" AND a."EffectiveTo" IS NULL
                   JOIN organization_departments d ON d."Id" = a."DepartmentId" WHERE e."Id" = '{e_ada}'""") == "-|Mühendislik|TEST Uzman")
    code, r = api("admin", "POST", f"{DIR}/ldap/sync", {"dryRun": False, "forceDisable": False})
    check("Y26 ikinci eşitleme değişiklik yapmaz", code == 200 and r["created"] == 0 and r["updated"] == 0 and r["unchanged"] == 2, r)

    ldap("ldapmodify", f"dn: uid=testldap.ada,{LDAP_BASE}\nchangetype: modify\nreplace: title\ntitle: TEST Takim Lideri\n")
    code, r = api("admin", "POST", f"{DIR}/ldap/sync", {"dryRun": False, "forceDisable": False})
    check("Y26 dizindeki unvan değişikliği yansır", code == 200 and r["updated"] == 1
          and psql(f"""SELECT "PositionTitle" FROM employee_assignments WHERE "EmployeeId" = '{e_ada}' AND "EffectiveTo" IS NULL""") == "TEST Takim Lideri", r)

    out = ldap("ldapdelete", None, f"uid=testldap.bora,{LDAP_BASE}")
    check("Y26 (hazırlık) bora dizinden silindi", out.returncode == 0, out.stderr)
    code, r = api("admin", "POST", f"{DIR}/ldap/sync", {"dryRun": True, "forceDisable": False})
    check("Y26 dry-run silinen kullanıcı için kapatma raporlar", code == 200 and r["disabled"] == 1
          and any(a["kind"] == "Disable" and a["userName"] == "testldap.bora" for a in r["actions"]) and kc_user(kc_bora).startswith("true|"), r)
    code, r = api("admin", "POST", f"{DIR}/ldap/sync", {"dryRun": False, "forceDisable": False})
    check("Y26 dizinden silinen kullanıcı kapatılır", code == 200 and r["disabled"] == 1 and kc_user(kc_bora).startswith("false|testldap.bora@example.com|"), (r, kc_user(kc_bora)))
    check("Y26 kapatılan dizin satırı pasif", psql("""SELECT "Active"::text FROM tenant_directory_users WHERE "TenantSlug" = 'demo' AND "UserName" = 'testldap.bora'""") == "false", psql("""SELECT "UserName", "Active" FROM tenant_directory_users WHERE "TenantSlug" = 'demo' AND "Source" = 'ldap'"""))
    st = psql("""SELECT "LastSyncTrigger" || '|' || "LastSyncStatus" FROM tenant_directory_settings WHERE "TenantSlug" = 'demo'""")
    check("Y26 son eşitleme durumu kaydedilir", st == "manual|Success", st)
    # Arka plan eşitlemesi (DIRECTORY_SYNC_MINUTES=0.2 testte): otomatik eşitleme açılınca kendiliğinden çalışır.
    api("admin", "PUT", f"{DIR}/settings", {"ldap": dict(ldap_cfg, autoSync=True, bindPassword=None)})
    check("Y26 arka plan eşitlemesi çalışır (DIRECTORY_SYNC_MINUTES)", wait(lambda: psql(
        """SELECT "LastSyncTrigger" FROM tenant_directory_settings WHERE "TenantSlug" = 'demo'""") == "background", 45),
        psql("""SELECT "LastSyncTrigger" || '|' || "LastSyncStatus" FROM tenant_directory_settings WHERE "TenantSlug" = 'demo'"""))
    api("admin", "PUT", f"{DIR}/settings", {"ldap": dict(ldap_cfg, autoSync=False, bindPassword=None)})

    # ================================================================== G28 özel alan adı
    D = f"ik-{RUN}.hr360-test.com.tr"
    D2 = f"ik2-{RUN}.hr360-test.com.tr"
    check("G28 yönetici olmayan alan adı ekleyemez", api("mehmet", "POST", DOM, {"domain": D})[0] == 403)
    code, r = api("admin", "POST", DOM, {"domain": "https://" + D + "/giris"})
    check("G28 adres/yol içeren girdi reddedilir", code == 400, (code, r))
    code, r = api("admin", "POST", DOM, {"domain": D.upper() + "."})
    check("G28 alan adı eklenir (Pending, _hr360-verify TXT)", code == 200 and r["domain"] == D and r["status"] == "Pending"
          and r["txtName"] == "_hr360-verify." + D and r["txtValue"].startswith("hr360-verify="), (code, r))
    DID, TXT = (r["id"], r["txtValue"]) if code == 200 else ("", "")
    check("G28 aynı alan adı ikinci kez eklenemez", api("admin", "POST", DOM, {"domain": D})[0] == 409)
    check("G28 doğrulanmamış alan adı için marka yok (204)", http("GET", f"{T}/public/branding?host={D}")[0] == 204)
    code, r = api("admin", "POST", f"{DOM}/{DID}/verify")
    check("G28 TXT kaydı yokken doğrulama başarısız", code == 400 and "TXT" in r.get("message", ""), (code, r))
    over = json.dumps({"_hr360-verify." + D: ["v=spf1 -all", TXT]})
    subprocess.run(["docker", "exec", "-i", "hr360-tenant-service-1", "sh", "-c", "cat > /tmp/hr360-dns-override.json"], input=over, text=True)
    code, r = api("admin", "POST", f"{DOM}/{DID}/verify")
    check("G28 TXT eşleşince Verified", code == 200 and r["domain"]["status"] == "Verified", (code, r))
    kc_client = kcadm("get", "clients", "-q", "clientId=hr360-web", "--fields", "redirectUris")
    check("G28 doğrulanan alan adı giriş yönlendirmelerine eklendi", f"https://{D}/*" in kc_client.stdout, kc_client.stdout[:300])
    code, b = http("GET", f"{T}/public/branding?host={D.upper()}:443")
    check("G28 herkese açık marka: slug/ad", code == 200 and b["slug"] == "demo" and b["name"] and set(b) == {"slug", "name", "logoUrl", "primaryColorHex"}, (code, b))
    check("G28 bilinmeyen host 204 (konsolda hata yok)", http("GET", f"{T}/public/branding?host=yok-{RUN}.example.com")[0] == 204
          and http("GET", f"{T}/public/branding?host=localhost")[0] == 204)

    code, r = api("admin", "POST", DOM, {"domain": D2})
    D2ID = r["id"] if code == 200 else ""
    psql(f"""UPDATE tenant_custom_domains SET "Status" = 'Verified', "VerifiedAt" = now() WHERE "Id" = '{D2ID}'""")
    code, b = http("GET", f"{T}/public/branding?host={D2}")
    check("G28 psql ile doğrulanmış alan adı markayı döndürür", code == 200 and b["slug"] == "demo", (code, b))
    psql(f"""INSERT INTO tenant_custom_domains ("Id","TenantSlug","Domain","VerificationToken","Status") VALUES ('{uuid.uuid4()}','kotu','{D2}','x','Pending')""")
    err = psql(f"""UPDATE tenant_custom_domains SET "Status" = 'Verified' WHERE "TenantSlug" = 'kotu' AND "Domain" = '{D2}'""")
    check("G28 bir alan adı iki şirkette doğrulanamaz", "unique" in err.lower() or "benzersiz" in err.lower(), err)
    code, r = api("admin", "GET", DOM)
    check("G28 liste yalnızca kendi şirketinin alan adları", code == 200 and {d["domain"] for d in r["items"]} >= {D, D2}
          and all(d["domain"] != D2 or d["status"] == "Verified" for d in r["items"]), r)
    check("G28 alan adı silinir", api("admin", "DELETE", f"{DOM}/{DID}")[0] == 200 and api("admin", "DELETE", f"{DOM}/{D2ID}")[0] == 200)
    check("G28 silinen alan adı için marka yok", http("GET", f"{T}/public/branding?host={D}")[0] == 204)
    kc_client = kcadm("get", "clients", "-q", "clientId=hr360-web", "--fields", "redirectUris")
    check("G28 silinen alan adı yönlendirmelerden çıkarıldı", f"https://{D}/*" not in kc_client.stdout and kc_client.returncode == 0, kc_client.stdout[:300])

    # ================================================================== Y28 basit e-imza
    code, doc = api("admin", "POST", EXP, {"employeeId": AYSE, "type": "Contract", "fileName": f"TEST-imza-{RUN}.pdf",
                                          "storageKey": f"test/{RUN}.pdf", "sizeBytes": 1234, "contentType": "application/pdf"})
    check("Y28 (hazırlık) doküman kaydı", code == 201, (code, doc))
    DOC = doc["id"]
    created["docs"].add(DOC)
    check("Y28 çalışan imzaya gönderemez", api("ayse", "POST", f"{EXP}/{DOC}/signature-requests", {})[0] == 403)
    code, req = api("admin", "POST", f"{EXP}/{DOC}/signature-requests", {"message": "TEST lütfen imzalayın"})
    check("Y28 İK imzaya gönderir; yasal açıklama yanıtta", code == 200 and req["status"] == "Pending"
          and req["disclaimer"] == DISCLAIMER, (code, req))
    SID = req["id"]
    check("Y28 aynı doküman için ikinci açık talep 409", api("admin", "POST", f"{EXP}/{DOC}/signature-requests", {})[0] == 409)
    check("Y28 çalışana uygulama içi bildirim", psql(f"""SELECT count(*) FROM notification_messages WHERE "RecipientEmployeeId" = '{AYSE}'
                                                     AND "TemplateCode" = 'document.sign.request' AND "CreatedAt" >= '{STARTS}'""") == "1")
    code, mine = api("ayse", "GET", f"{EXP}/signature-requests/mine")
    item = next((i for i in (mine or {}).get("items", []) if i["id"] == SID), None)
    check("Y28 çalışan belgeyi görür (özet eşleşir)", code == 200 and item and item["document"]["fileName"] == f"TEST-imza-{RUN}.pdf"
          and item["document"]["contentHash"] == item["requestedDocumentHash"] and len(item["requestedDocumentHash"]) == 64, item)
    check("Y28 başkası talebi göremez/kod isteyemez", api("mehmet", "GET", f"{EXP}/signature-requests/{SID}")[0] == 404
          and api("mehmet", "POST", f"{EXP}/signature-requests/{SID}/otp")[0] == 404)
    code, r = api("ayse", "POST", f"{EXP}/signature-requests/{SID}/sign", {"code": "123456", "accept": True})
    check("Y28 kod istemeden imza yok (otp_not_found)", code == 404 and r.get("code") == "otp_not_found", (code, r))

    # Kod, imza ve kanıt governance'taki tek imza motorunda (DocumentType 'HrDocument'); kod 'signature.otp' bildirimiyle gelir.
    def latest_otp():
        body = psql(f"""SELECT "Body" FROM notification_messages WHERE "RecipientEmployeeId" = '{AYSE}' AND "TemplateCode" = 'signature.otp'
                        AND "Channel" = 'InApp' AND "CreatedAt" >= '{STARTS}' AND "Body" LIKE '%TEST-imza-{RUN}.pdf%' ORDER BY "CreatedAt" DESC LIMIT 1""")
        # Gövdede belge adı (TEST-imza-<RUN>.pdf) da geçer; RUN tamamen rakamsa ilk eşleşme o olur. Kod sondadır.
        m = re.findall(r"\b(\d{6})\b", body)
        return m[-1] if m else None

    def allow_resend():
        # 30 sn yeniden gönderme beklemesini atla (saatlik sayım değişmez).
        psql(f"""UPDATE governance_signature_otps SET "CreatedAt" = "CreatedAt" - interval '1 minute'
                 WHERE "DocumentType" = 'HrDocument' AND "DocumentId" = '{DOC}'""")

    code, r = api("ayse", "POST", f"{EXP}/signature-requests/{SID}/otp")
    check("Y28 kod gönderilir (uygulama içi + e-posta)", code == 200 and r["channel"] == "InApp+Email" and r["attemptsLeft"] == 5
          and r.get("otpId") and "5070" in r["disclaimer"], (code, r))
    OTP = latest_otp()
    check("Y28 kod yanıtta yok", OTP and OTP not in json.dumps(r), r)
    check("Y28 e-posta kanalı satırı", psql(f"""SELECT "RecipientEmail" FROM notification_messages WHERE "RecipientEmployeeId" = '{AYSE}'
                                              AND "TemplateCode" = 'signature.otp' AND "Channel" = 'Email' AND "CreatedAt" >= '{STARTS}'
                                              AND "Body" LIKE '%TEST-imza-{RUN}.pdf%' LIMIT 1""") == "ayse.yilmaz@demo.hr360")
    orow = psql(f"""SELECT row_to_json(o)::text FROM governance_signature_otps o WHERE "DocumentType" = 'HrDocument' AND "DocumentId" = '{DOC}'
                    ORDER BY "CreatedAt" DESC LIMIT 1""")
    otp_hash = psql(f"""SELECT "CodeHash" FROM governance_signature_otps WHERE "Id" = '{r.get('otpId')}'""")
    check("Y28 kod yalnızca özetiyle saklanır (imza motorunda)", OTP and OTP not in orow and len(otp_hash) == 64 and r.get("otpId") in orow, orow)
    check("Y28 expense artık kod/özet yazmaz", psql(f"""SELECT coalesce("OtpHash",'NULL') || '|' || "OtpSentCount" FROM expense_document_signatures WHERE "Id" = '{SID}'""") == "NULL|0")
    exp_at = psql(f"""SELECT round(extract(epoch FROM "ExpiresAt" - now())) FROM governance_signature_otps WHERE "Id" = '{r.get('otpId')}'""")
    check("Y28 kod 10 dakika geçerli", exp_at and 560 <= float(exp_at) <= 600, exp_at)
    code, r2 = api("ayse", "POST", f"{EXP}/signature-requests/{SID}/otp")
    check("Y28 hemen yeniden kod istenemez (429 otp_cooldown)", code == 429 and r2.get("code") == "otp_cooldown", (code, r2))
    wrong = "000000" if OTP != "000000" else "111111"
    results = [api("ayse", "POST", f"{EXP}/signature-requests/{SID}/sign", {"code": wrong, "accept": True}) for _ in range(5)]
    check("Y28 hatalı kod: kalan deneme azalır", [(c, x.get("code"), x.get("attemptsLeft")) for c, x in results[:4]]
          == [(400, "otp_invalid", 4), (400, "otp_invalid", 3), (400, "otp_invalid", 2), (400, "otp_invalid", 1)]
          and "Kalan deneme: 1" in results[3][1]["message"], results[:4])
    check("Y28 5. hatalı denemede kilit", results[4][0] == 429 and results[4][1].get("code") == "otp_locked", results[4])
    check("Y28 hatalı denemeler denetim kaydında", psql(f"""SELECT count(*) FROM audit_log WHERE "EntityType" = 'DocumentSignature' AND "EntityId" = '{SID}'
                                                          AND "Action" = 'OtpFailed'""") == "5")
    code, r = api("ayse", "POST", f"{EXP}/signature-requests/{SID}/sign", {"code": OTP, "accept": True})
    check("Y28 kilitten sonra doğru kod da geçmez", code == 429 and r.get("code") == "otp_locked", (code, r))

    allow_resend()
    code, r = api("ayse", "POST", f"{EXP}/signature-requests/{SID}/otp")
    OTP2 = latest_otp()
    psql(f"""UPDATE governance_signature_otps SET "ExpiresAt" = now() - interval '1 second' WHERE "Id" = '{r.get('otpId')}'""")
    code, r = api("ayse", "POST", f"{EXP}/signature-requests/{SID}/sign", {"code": OTP2, "accept": True})
    check("Y28 süresi dolan kod reddedilir (410 otp_expired)", code == 410 and r.get("code") == "otp_expired", (code, r))

    allow_resend()
    code, r = api("ayse", "POST", f"{EXP}/signature-requests/{SID}/otp")
    OTP3, OTP3_ID = latest_otp(), r.get("otpId")
    check("Y28 yeni kod farklı ve deneme hakkı yenilenir (saatte 5 kod)", code == 200 and r["attemptsLeft"] == 5 and OTP3 and r["sendsLeft"] == 2, (code, r))
    code, r = api("ayse", "POST", f"{EXP}/signature-requests/{SID}/sign", {"code": OTP3, "accept": False})
    check("Y28 açıklama onaylanmadan imza yok", code == 400, (code, r))
    code, r = api("ayse", "POST", f"{EXP}/signature-requests/{SID}/sign", {"code": OTP3, "accept": True, "otpId": OTP3_ID})
    ev = (r or {}).get("evidence") or {}
    check("Y28 doğru kodla imzalanır", code == 200 and r["disclaimer"] == DISCLAIMER, (code, r))
    check("Y28 kanıt: belge özeti, imzalayan, kanal, kısaltılmış IP, bütünlük (governance kanıtı)",
          ev.get("documentHash") == item["requestedDocumentHash"] and ev.get("signerEmployeeId") == AYSE and ev.get("otpChannel") == "InApp+Email"
          and ev.get("method") == "OTP-InApp+Email" and ev.get("signatureId") == SID
          and (ev.get("ipMasked") is None or ev["ipMasked"].endswith(".0/24") or ev["ipMasked"].endswith("/48"))
          and len(ev.get("evidenceHash") or "") == 64 and ev.get("integrityOk") is True and ev.get("source") == "governance", ev)
    db_ev = psql(f"""SELECT "SignerEmployeeId" || '|' || "DocumentSha256" || '|' || "Title" || '|' || "Disclaimer" FROM governance_signatures
                     WHERE "DocumentType" = 'HrDocument' AND "DocumentId" = '{DOC}' AND "Id" = '{ev.get('id')}'""")
    check("Y28 kanıt satırı imza motorunda (HrDocument, başlık, tek uyarı metni)",
          db_ev == f"{AYSE}|{item['requestedDocumentHash']}|TEST-imza-{RUN}.pdf|{DISCLAIMER}", db_ev)
    check("Y28 talep governance kanıtını gösterir (EvidenceRef)",
          psql(f"""SELECT "EvidenceRef" FROM expense_document_signatures WHERE "Id" = '{SID}'""") == ev.get("id"))
    check("Y28 eski kanıt tablosuna yazılmaz", psql(f"""SELECT count(*) FROM expense_signature_evidence WHERE "SignatureId" = '{SID}'""") == "0")
    check("Y28 kanıtta kod/OTP özeti yok", psql(f"""SELECT row_to_json(e)::text FROM governance_signatures e WHERE "Id" = '{ev.get('id')}'""").find(OTP3) == -1
          and psql(f"""SELECT coalesce("OtpHash",'NULL') FROM expense_document_signatures WHERE "Id" = '{SID}'""") == "NULL")
    check("Y28 imza denetim kaydı (HrDocument, Signed)", psql(f"""SELECT count(*) FROM audit_log WHERE "EntityType" = 'HrDocument' AND "EntityId" = '{DOC}'
                                                              AND "Action" = 'Signed'""") == "1")
    check("Y28 aynı talep ikinci kez imzalanamaz", api("ayse", "POST", f"{EXP}/signature-requests/{SID}/sign", {"code": OTP3, "accept": True})[0] == 409)
    err = psql(f"""UPDATE governance_signatures SET "Method" = 'X' WHERE "Id" = '{ev.get('id')}'""")
    check("Y28 kanıt değiştirilemez (tetikleyici)", "değiştirilemez" in err, err)
    err = psql(f"""UPDATE expense_documents SET "FileName" = 'degisti.pdf' WHERE "Id" = '{DOC}'""")
    check("Y28 imzalı doküman değiştirilemez", "İmzalanmış doküman değiştirilemez" in err, err)
    check("Y28 dokümanda imza zamanı", psql(f"""SELECT "SignedAt" IS NOT NULL FROM expense_documents WHERE "Id" = '{DOC}'""") == "t")
    code, mine = api("ayse", "GET", f"{EXP}/signature-requests/mine")
    it = next((i for i in (mine or {}).get("items", []) if i["id"] == SID), None)
    check("Y28 çalışanın talep listesinde governance kanıtı", code == 200 and it and it["status"] == "Signed" and it["evidence"]["id"] == ev.get("id")
          and it["evidence"]["integrityOk"], it)
    code, gm = api("ayse", "GET", "/api/governance/documents/signatures/mine")
    row = next((x for x in (gm or {}).get("items", []) if x["id"] == ev.get("id")), None)
    check("Y28 İmzaladığım belgeler (governance): HrDocument satırı", code == 200 and row and row["documentType"] == "HrDocument"
          and row["documentId"] == DOC and row["title"] == f"TEST-imza-{RUN}.pdf" and row["integrityOk"] is True
          and row["evidenceSha256"] == ev.get("evidenceHash") and gm["disclaimer"] == DISCLAIMER, (code, row))
    code, gm2 = api("mehmet", "GET", "/api/governance/documents/signatures/mine")
    check("Y28 başkasının imzaladığı belge listede yok", code == 200 and all(x["id"] != ev.get("id") for x in gm2.get("items", [])), code)
    code, _ = http("POST", "/api/governance/internal/signatures/evidence", {"tenantSlug": "demo", "documentType": "HrDocument", "documentId": DOC})
    check("Y28 iç imza uçları dışarıdan kapalı", code in (403, 404), code)

    code, req2 = api("admin", "POST", f"{EXP}/{DOC}/signature-requests", {})
    check("Y28 yeniden imza yeni talep gerektirir (yeni talep açılır)", code == 200 and req2["id"] != SID and req2["status"] == "Pending", (code, req2))
    # Belge başına saatte 5 kod sınırı talepler arasında da geçerli (bu belge için 3 kod istendi).
    sends = []
    for _ in range(3):
        allow_resend()
        c, x = api("ayse", "POST", f"{EXP}/signature-requests/{req2['id']}/otp")
        sends.append((c, x.get("code") or x.get("sendsLeft")))
    check("Y28 belge başına saatte en fazla 5 kod (otp_rate_limited)", sends == [(200, 1), (200, 0), (429, "otp_rate_limited")], sends)
    check("Y28 bekleyen talep iptal edilir", api("admin", "DELETE", f"{EXP}/signature-requests/{req2['id']}")[0] == 200)
    code, r = api("ayse", "POST", f"{EXP}/signature-requests/{req2['id']}/sign", {"code": "123456", "accept": True})
    check("Y28 iptal edilen talep imzalanamaz", code == 409, (code, r))
    code, hist = api("admin", "GET", f"{EXP}/{DOC}/signatures")
    check("Y28 İK imza geçmişi ve kanıt görünümü", code == 200 and len(hist["items"]) == 2
          and any(i["status"] == "Signed" and i["evidence"]["integrityOk"] and i["evidence"]["source"] == "governance" for i in hist["items"]), hist)
    check("Y28 kanıt görüntüleme denetim kaydında", psql(f"""SELECT count(*) FROM audit_log WHERE "EntityType" = 'SignatureEvidence' AND "EntityId" = '{DOC}'
                                                           AND "Action" = 'SensitiveViewed'""") != "0")
    code, st = api("admin", "GET", f"{EXP}/signature-status")
    check("Y28 durum özeti son talebi gösterir", code == 200 and any(s["documentId"] == DOC and s["signedAt"] for s in st), st)
    check("Y28 çalışan imza geçmişini göremez", api("ayse", "GET", f"{EXP}/{DOC}/signatures")[0] == 403)
    code, r = api("admin", "DELETE", f"{EXP}/{DOC}")
    check("Y28 imzalı doküman onaysız silinemez", code == 409 and r.get("code") == "signed_document", (code, r))
    code, _ = api("admin", "DELETE", f"{EXP}/{DOC}?confirmSigned=true")
    left = psql(f"""SELECT (SELECT count(*) FROM governance_signatures WHERE "DocumentType" = 'HrDocument' AND "DocumentId" = '{DOC}')
                         + (SELECT count(*) FROM governance_signature_otps WHERE "DocumentType" = 'HrDocument' AND "DocumentId" = '{DOC}')
                         + (SELECT count(*) FROM expense_signature_evidence WHERE "DocumentId" = '{DOC}')""")
    check("Y28 saklama sonu imha: doküman, kodlar ve kanıt birlikte silinir", code == 204 and left == "0", (code, left))
finally:
    cleanup()

left = psql(f"""SELECT (SELECT count(*) FROM tenant_directory_users WHERE "Email" LIKE '%{RUN}@example.com' OR "UserName" LIKE 'testldap.%')
               + (SELECT count(*) FROM tenant_custom_domains WHERE "Domain" LIKE '%{RUN}.hr360-test.com.tr')
               + (SELECT count(*) FROM expense_documents WHERE "FileName" = 'TEST-imza-{RUN}.pdf')
               + (SELECT count(*) FROM governance_signatures WHERE "DocumentType" = 'HrDocument' AND "Title" = 'TEST-imza-{RUN}.pdf')
               + (SELECT count(*) FROM employee_employees WHERE "Email" LIKE 'test.scim%{RUN}@example.com' OR "Email" LIKE 'testldap.%@example.com')""")
check("temizlik: test satırı kalmadı", left == "0", left)
check("temizlik: test Keycloak hesabı kalmadı",
      psql(f"SELECT count(*) FROM user_entity WHERE email LIKE '%{RUN}@example.com' OR email LIKE 'testldap.%@example.com'", db="keycloak") == "0")
print(f"FAILS: {len(FAIL)}")
sys.exit(1 if FAIL else 0)

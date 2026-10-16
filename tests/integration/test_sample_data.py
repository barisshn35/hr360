#!/usr/bin/env python3
"""Örnek veri: /api/tenant/my-tenant/sample-data ve /api/tenant/tenants/by-slug/{slug}/sample-data.

Denetlenenler: yalnızca şirket/platform yöneticisi; dolu şirkete (demo) örnek veri eklenmez; boş bir
test şirketi doldurulur (departmanlar, başları, @ornek.invalid çalışanlar, takdir, fikir, etkinlik),
örnek kişiler için olay/e-posta oluşmaz; ikinci doldurma reddedilir; temizlik örnek kayıtları ve
açılan departmanları siler, şirketin kendi departmanına dokunmaz; iç uçlar gateway'den kapalı.
Test şirketi ("test-ornek-veri") ve kayıtları sonda silinir.
Çalıştırma: timeout 180 python3 tests/integration/test_sample_data.py
"""
import os
import subprocess
import sys

sys.path.insert(0, os.path.dirname(__file__))
from common import FAIL, api, check, http  # noqa: E402

ROOT = os.path.join(os.path.dirname(__file__), "..", "..")
FAIL.clear()
SLUG = "test-ornek-veri"
P = f"/api/tenant/tenants/by-slug/{SLUG}/sample-data"


def psql(sql):
    out = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", "hr360_operational", "-Atc", sql],
                         capture_output=True, text=True, cwd=ROOT)
    return (out.stdout + out.stderr).strip()


def count(table, where=""):
    return int(psql(f"""SELECT count(*) FROM {table} WHERE "TenantSlug" = '{SLUG}' {where}""") or 0)


def cleanup():
    for t in ("engagement_event_rsvps", "engagement_events", "engagement_kudos", "engagement_ideas", "engagement_badge_awards",
              "employee_assignments", "employee_employees", "organization_departments", "organization_companies", "platform_sample_data"):
        psql(f"""DELETE FROM {t} WHERE "TenantSlug" = '{SLUG}'""")
    psql(f"""DELETE FROM platform_tenants WHERE "Slug" = '{SLUG}'""")


cleanup()
start = psql("SELECT now()")
psql(f"""INSERT INTO platform_tenants ("Id","Name","Slug","Status","AdminEmail","CreatedAt") VALUES (gen_random_uuid(),'Test Örnek A.Ş.','{SLUG}','Active','admin@ornek.invalid',now())""")
psql(f"""INSERT INTO organization_companies ("Id","TenantSlug","Name","CreatedAt") VALUES (gen_random_uuid(),'{SLUG}','Test Örnek A.Ş.',now())""")
own = psql(f"""INSERT INTO organization_departments ("Id","TenantSlug","Name","CompanyId","CreatedAt")
               SELECT gen_random_uuid(),'{SLUG}','Mühendislik',"Id",now() FROM organization_companies WHERE "TenantSlug" = '{SLUG}' RETURNING "Id" """).splitlines()[0]

try:
    code, _ = api("ik", "GET", "/api/tenant/my-tenant/sample-data")
    check("SD1: İK (şirket yöneticisi değil) erişemez", code == 403, code)
    code, st = api("admin", "GET", "/api/tenant/my-tenant/sample-data")
    check("SD1: şirket yöneticisi kendi şirketinin durumunu görür", code == 200 and "canFill" in st and st["sampleEmployees"] == 0, (code, st))
    code, _ = api("admin", "GET", P)
    check("SD1: başka şirketin uçları şirket yöneticisine kapalı", code == 403, code)

    # Gerçek çalışanı 5'ten fazla olan şirkete eklenmez (geçici 6 kişi).
    psql(f"""INSERT INTO employee_employees ("Id","TenantSlug","FirstName","LastName","Email","HireDate","Status","CreatedAt")
             SELECT gen_random_uuid(),'{SLUG}','Gerçek','Kişi'||g,'gercek'||g||'@test-ornek.example',current_date,'Active',now() FROM generate_series(1,6) g""")
    code, st = api("platform", "GET", P)
    check("SD1: dolu şirkette doldurma kapalı", code == 200 and not st["canFill"] and st["realEmployees"] == 6, (code, st))
    code, _ = api("platform", "POST", P)
    check("SD1: dolu şirkete doldurma reddedilir", code == 400, code)
    psql(f"""DELETE FROM employee_employees WHERE "TenantSlug" = '{SLUG}' AND "Email" LIKE '%@test-ornek.example'""")

    code, st = api("platform", "GET", P)
    check("SD2: boş test şirketi doldurulabilir", code == 200 and st["canFill"] and st["sampleEmployees"] == 0, (code, st))
    code, st = api("platform", "POST", P)
    check("SD2: doldurma başarılı", code == 200 and st["hasSample"] and st["sampleEmployees"] == 16 and not st["canFill"], (code, st))
    check("SD2: örnek çalışanlar @ornek.invalid", count("employee_employees", """AND "Email" LIKE '%@ornek.invalid'""") == 16, count("employee_employees"))
    check("SD2: dört departman; yalnızca örnek verinin açtığı üçüne baş atanır, şirketin kendi departmanına dokunulmaz",
          count("organization_departments") == 4 and count("organization_departments", 'AND "HeadEmployeeId" IS NOT NULL') == 3
          and psql(f"""SELECT "HeadEmployeeId" IS NULL FROM organization_departments WHERE "Id" = '{own}'""") == "t", count("organization_departments"))
    check("SD2: herkesin ataması ve unvanı var", count("employee_assignments", 'AND "PositionTitle" IS NOT NULL') == 16, count("employee_assignments"))
    check("SD2: takdir, fikir, etkinlik", count("engagement_kudos") == 8 and count("engagement_ideas") == 3 and count("engagement_events") == 1, (count("engagement_kudos"), count("engagement_ideas")))
    n = psql(f"""SELECT count(*) FROM employee_outbox_messages WHERE "CreatedAt" >= '{start}' AND "Payload" LIKE '%ornek.invalid%'""")
    check("SD2: örnek kişiler için olay yayımlanmaz", n in ("0", "") or "does not exist" in n, n)
    n = psql(f"""SELECT count(*) FROM notification_messages WHERE "TenantSlug" = '{SLUG}'""")
    check("SD2: bildirim/e-posta oluşmaz", n == "0", n)
    code, _ = api("platform", "POST", P)
    check("SD3: ikinci doldurma reddedilir", code == 400, code)

    code, st = api("platform", "DELETE", P)
    check("SD4: temizlik başarılı", code == 200 and not st["hasSample"] and st["sampleEmployees"] == 0, (code, st))
    check("SD4: örnek çalışan, takdir, fikir, etkinlik kalmaz",
          count("employee_employees") == 0 and count("employee_assignments") == 0 and count("engagement_kudos") == 0 and count("engagement_ideas") == 0 and count("engagement_events") == 0,
          (count("employee_employees"), count("engagement_kudos")))
    left = psql(f"""SELECT string_agg("Name", ',') FROM organization_departments WHERE "TenantSlug" = '{SLUG}'""")
    check("SD4: açılan departmanlar silinir, şirketin kendi departmanı kalır", left == "Mühendislik"
          and psql(f"""SELECT count(*) FROM organization_departments WHERE "Id" = '{own}'""") == "1", left)
    n = psql(f"""SELECT count(*) FROM audit_log WHERE "EntityType" = 'SampleData' AND "EntityId" = '{SLUG}' AND "OccurredAt" >= '{start}'""")
    check("SD5: doldurma ve temizlik denetim kaydında", n == "2", n)

    code, _ = http("POST", "/api/employee/internal/employees/sample/status", {"tenantSlug": SLUG}, {"X-Internal-Token": "x"})
    check("SD6: iç uç gateway'den kapalı", code in (403, 404), code)
finally:
    cleanup()

print("\nSONUÇ:", "BAŞARILI" if not FAIL else f"{len(FAIL)} HATA: {FAIL}")
sys.exit(1 if FAIL else 0)

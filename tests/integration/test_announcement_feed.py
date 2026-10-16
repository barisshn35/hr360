#!/usr/bin/env python3
"""Şirket içi akış: duyurulara tepki ve yorum (/api/governance/announcements).

Denetlenenler: kişi başına tek tepki (aynısı geri alır, farklısı değiştirir), geçersiz tepki reddi;
yorum ekleme/listeleme, başkasının yorumunu çalışan silemez, İK siler; yorumları kapalı duyuruya yorum
yok; hedef kitle dışındaki çalışan tepki veremez ve yorumları göremez; liste sayıları doğru.
Test duyuruları (başlık öneki "TEST-FEED") ve bildirimleri sonda silinir.
Çalıştırma: timeout 120 python3 tests/integration/test_announcement_feed.py
"""
import os
import subprocess
import sys

sys.path.insert(0, os.path.dirname(__file__))
from common import FAIL, api, check  # noqa: E402
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "support"))
from demo_ids import ENG  # noqa: E402

ROOT = os.path.join(os.path.dirname(__file__), "..", "..")
FAIL.clear()
B = "/api/governance/announcements"


def psql(sql):
    out = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", "hr360_operational", "-Atc", sql],
                         capture_output=True, text=True, cwd=ROOT)
    return (out.stdout + out.stderr).strip()


def item(who, aid):
    return next((a for a in api(who, "GET", B)[1] if a["id"] == aid), None)


start = psql("SELECT now()")
other_dept = psql(f"""SELECT "Id" FROM organization_departments WHERE "TenantSlug" = 'demo' AND "Id" <> '{ENG}' ORDER BY "Name" LIMIT 1""")

try:
    code, a = api("ik", "POST", B, {"title": "TEST-FEED Yeni ofis açıldı", "body": "Kadıköy ofisimiz pazartesi açılıyor.", "audience": "All", "requiresAck": False})
    check("AF1: İK duyuru yayımlar", code == 200, (code, a))
    aid = a["id"]
    it = item("ayse", aid)
    check("AF1: yorumlar varsayılan açık, sayılar sıfır", it and it["allowComments"] and it["commentCount"] == 0 and it["reactions"] == {}, it)

    code, _ = api("ayse", "POST", f"{B}/{aid}/react", {"kind": "angry"})
    check("AF2: geçersiz tepki reddedilir", code == 400, code)
    _, r = api("ayse", "POST", f"{B}/{aid}/react", {"kind": "like"})
    _, r = api("mehmet", "POST", f"{B}/{aid}/react", {"kind": "celebrate"})
    check("AF2: tepkiler sayılır", r["reactions"] == {"like": 1, "celebrate": 1} and r["myReaction"] == "celebrate", r)
    _, r = api("ayse", "POST", f"{B}/{aid}/react", {"kind": "clap"})
    check("AF2: farklı tepki değiştirir (kişi başına tek)", r["reactions"] == {"clap": 1, "celebrate": 1} and r["myReaction"] == "clap", r)
    _, r = api("ayse", "POST", f"{B}/{aid}/react", {"kind": "clap"})
    check("AF2: aynı tepki geri alınır", r["reactions"] == {"celebrate": 1} and r["myReaction"] is None, r)

    code, c1 = api("ayse", "POST", f"{B}/{aid}/comments", {"body": "Harika haber, otopark var mı?"})
    check("AF3: yorum eklenir", code == 200, (code, c1))
    code, _ = api("ayse", "POST", f"{B}/{aid}/comments", {"body": "   "})
    check("AF3: boş yorum reddedilir", code == 400, code)
    _, lst = api("mehmet", "GET", f"{B}/{aid}/comments")
    check("AF3: başkası yorumu görür, silme yetkisi yok", len(lst) == 1 and not lst[0]["mine"] and not lst[0]["canDelete"], lst)
    code, _ = api("mehmet", "DELETE", f"{B}/{aid}/comments/{c1['id']}")
    check("AF3: çalışan başkasının yorumunu silemez", code == 403, code)
    check("AF3: listede yorum sayısı", item("mehmet", aid)["commentCount"] == 1, item("mehmet", aid))
    code, _ = api("ik", "DELETE", f"{B}/{aid}/comments/{c1['id']}")
    check("AF3: İK yorumu siler", code == 204, code)

    code, b = api("ik", "POST", B, {"title": "TEST-FEED Yorumsuz duyuru", "body": "Bilgilendirme.", "audience": "All", "requiresAck": False, "allowComments": False})
    code, _ = api("ayse", "POST", f"{B}/{b['id']}/comments", {"body": "deneme"})
    check("AF4: yorumları kapalı duyuruya yorum yapılamaz", code == 400, code)
    code, _ = api("ayse", "POST", f"{B}/{b['id']}/react", {"kind": "like"})
    check("AF4: tepki yine verilebilir", code == 200, code)

    code, d = api("ik", "POST", B, {"title": "TEST-FEED Başka departman", "body": "Yalnızca o departmana.", "audience": "Departments", "departmentIds": [other_dept], "requiresAck": False})
    check("AF5: departmana özel duyuru oluşur", code == 200, (code, d))
    code, _ = api("ayse", "POST", f"{B}/{d['id']}/react", {"kind": "like"})
    check("AF5: hedef kitle dışı tepki veremez", code == 404, code)
    code, _ = api("ayse", "GET", f"{B}/{d['id']}/comments")
    check("AF5: hedef kitle dışı yorumları göremez", code == 404, code)
finally:
    psql("DELETE FROM governance_acknowledgements WHERE \"SubjectId\" IN (SELECT \"Id\" FROM governance_announcements WHERE \"Title\" LIKE 'TEST-FEED%')")
    psql("DELETE FROM governance_announcements WHERE \"Title\" LIKE 'TEST-FEED%'")
    psql(f"DELETE FROM notification_messages WHERE \"TemplateCode\" = 'announcement.published' AND \"TenantSlug\" = 'demo' AND \"Subject\" LIKE 'TEST-FEED%' AND \"CreatedAt\" >= '{start}'")

print("\nSONUÇ:", "BAŞARILI" if not FAIL else f"{len(FAIL)} HATA: {FAIL}")
sys.exit(1 if FAIL else 0)

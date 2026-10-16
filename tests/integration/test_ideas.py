#!/usr/bin/env python3
"""Fikir kutusu: /api/engagement/ideas.

Denetlenenler: doğrulama (başlık/açıklama/kategori); anonim fikirde yazar kimseye dönmez ama yazar kendi
fikrini tanır; veritabanında anonim yazar kimliği yok; destek aç/kapa (kişi başına bir); yorum ve
yazara bildirim; durum yalnızca İK (uygun görülmedi için gerekçe zorunlu); yazar yanıtlanmış fikri
silemez, İK silebilir. Test kayıtları sonda silinir (başlık öneki "TEST-IDEA").
Çalıştırma: timeout 120 python3 tests/integration/test_ideas.py
"""
import os
import subprocess
import sys

sys.path.insert(0, os.path.dirname(__file__))
from common import FAIL, api, check  # noqa: E402
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "support"))
from demo_ids import AYSE  # noqa: E402

ROOT = os.path.join(os.path.dirname(__file__), "..", "..")
FAIL.clear()
B = "/api/engagement/ideas"


def psql(sql):
    out = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", "hr360_operational", "-Atc", sql],
                         capture_output=True, text=True, cwd=ROOT)
    return (out.stdout + out.stderr).strip()


try:
    code, _ = api("ayse", "POST", B, {"title": "kısa", "body": "yeterince uzun bir açıklama", "category": "Diğer", "anonymous": False})
    check("ID1: kısa başlık reddedilir", code == 400, code)
    code, _ = api("ayse", "POST", B, {"title": "TEST-IDEA geçersiz", "body": "yeterince uzun bir açıklama", "category": "Yok", "anonymous": False})
    check("ID1: geçersiz kategori reddedilir", code == 400, code)

    code, anon = api("ayse", "POST", B, {"title": "TEST-IDEA anonim öneri", "body": "Toplantısız cuma öğleden sonraları olsun.", "category": "Çalışma ortamı", "anonymous": True})
    check("ID2: anonim fikir oluşur", code == 200 and anon["mine"], (code, anon))
    _, lst = api("mehmet", "GET", B + "?sort=new")
    seen = next((x for x in lst if x["id"] == anon["id"]), None)
    check("ID2: başkası yazarı görmez", seen and seen["authorName"] is None and seen["authorEmployeeId"] is None and not seen["mine"], seen)
    _, hr = api("ik", "GET", f"{B}/{anon['id']}")
    check("ID2: İK de yazarı görmez", hr["idea"]["authorName"] is None, hr["idea"])
    check("ID2: veritabanında anonim yazar kimliği yok",
          psql(f"SELECT \"AuthorEmployeeId\" IS NULL AND \"AuthorName\" IS NULL FROM engagement_ideas WHERE \"Id\" = '{anon['id']}'") == "t")

    code, named = api("ayse", "POST", B, {"title": "TEST-IDEA adlı öneri", "body": "Yeni gelenlere bir haftalık buddy eşleştirmesi.", "category": "Sosyal", "anonymous": False})
    check("ID3: adlı fikirde yazar görünür", api("mehmet", "GET", f"{B}/{named['id']}")[1]["idea"]["authorName"], named)

    _, v = api("mehmet", "POST", f"{B}/{named['id']}/vote")
    check("ID4: destek verilir", v["votes"] == 1 and v["votedByMe"], v)
    _, v = api("mehmet", "POST", f"{B}/{named['id']}/vote")
    check("ID4: ikinci tıklama desteği geri alır", v["votes"] == 0 and not v["votedByMe"], v)
    api("mehmet", "POST", f"{B}/{named['id']}/vote")

    code, c = api("mehmet", "POST", f"{B}/{named['id']}/comments", {"body": "TEST-IDEA katılıyorum"})
    check("ID5: yorum eklenir", code == 200, (code, c))
    n = psql(f"SELECT count(*) FROM notification_messages WHERE \"TemplateCode\" = 'ideas.comment' AND \"RecipientEmployeeId\" = '{AYSE}' AND \"CreatedAt\" > now() - interval '5 minutes'")
    check("ID5: yazara yorum bildirimi", n not in ("", "0"), n)

    code, _ = api("ayse", "PUT", f"{B}/{named['id']}/status", {"status": "Planned", "response": None})
    check("ID6: çalışan durum değiştiremez", code == 403, code)
    code, _ = api("ik", "PUT", f"{B}/{anon['id']}/status", {"status": "Declined", "response": None})
    check("ID6: gerekçesiz ret reddedilir", code == 400, code)
    code, s = api("ik", "PUT", f"{B}/{named['id']}/status", {"status": "Planned", "response": "Ocak'ta pilot başlıyor."})
    check("ID6: İK planlar", code == 200 and s["status"] == "Planned" and s["response"], s)
    n = psql(f"SELECT count(*) FROM notification_messages WHERE \"TemplateCode\" = 'ideas.status' AND \"RecipientEmployeeId\" = '{AYSE}' AND \"CreatedAt\" > now() - interval '5 minutes'")
    check("ID6: fikir sahibine durum bildirimi", n not in ("", "0"), n)

    code, _ = api("ayse", "DELETE", f"{B}/{named['id']}")
    check("ID7: yazar yanıtlanmış fikri silemez", code == 403, code)
    code, _ = api("ayse", "DELETE", f"{B}/{anon['id']}")
    check("ID7: yazar yanıtlanmamış anonim fikrini silebilir", code == 204, code)
finally:
    psql("DELETE FROM engagement_ideas WHERE \"Title\" LIKE 'TEST-IDEA%'")
    psql("DELETE FROM notification_messages WHERE \"TemplateCode\" LIKE 'ideas.%' AND \"TenantSlug\" = 'demo'")

print("\nSONUÇ:", "BAŞARILI" if not FAIL else f"{len(FAIL)} HATA: {FAIL}")
sys.exit(1 if FAIL else 0)

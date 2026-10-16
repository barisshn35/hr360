#!/usr/bin/env python3
"""Çalışan fotoğrafı: PUT/GET/DELETE /api/employees/<id>/photo.

Denetlenenler: kişinin kendisi yükler; aynı şirketten meslektaş görür ama değiştiremez; oturumsuz okunamaz;
yalnızca PNG/JPEG/WebP (SVG/HTML reddedilir, tür ilk baytlardan); 512 KB sınırı; dizinde ve kayıtta
photoVersion artar; silince 404 ve photoVersion null.

Test kaydı bırakmaz: Ayşe'nin önceki fotoğrafı varsa sonda geri yüklenir, yoksa silinir.
Çalıştırma: timeout 120 python3 tests/integration/test_employee_photo.py
"""
import os
import sys
import urllib.error
import urllib.request
import uuid

sys.path.insert(0, os.path.dirname(__file__))
from common import BASE, FAIL, api, check, tok  # noqa: E402

FAIL.clear()
# 1x1 PNG
PNG = bytes.fromhex("89504e470d0a1a0a0000000d4948445200000001000000010806000000"
                    "1f15c4890000000d49444154789c6360f8cf00000301010018dd8db10000000049454e44ae426082")


def raw(method, path, who=None, data=None, ctype=None):
    h = {"Authorization": "Bearer " + tok(who)} if who else {}
    body = None
    if data is not None:
        b = uuid.uuid4().hex
        body = (f"--{b}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"f\"\r\n"
                f"Content-Type: {ctype}\r\n\r\n").encode() + data + f"\r\n--{b}--\r\n".encode()
        h["Content-Type"] = f"multipart/form-data; boundary={b}"
    req = urllib.request.Request(BASE + path, data=body, method=method, headers=h)
    try:
        with urllib.request.urlopen(req, timeout=30) as r:
            return r.status, r.read(), dict(r.headers)
    except urllib.error.HTTPError as e:
        return e.code, e.read(), dict(e.headers)


code, me = api("ayse", "GET", "/api/employee/employees/me")
eid = me["id"]
P = f"/api/employee/employees/{eid}/photo"
had, before, before_type = None, None, None
c, data, hd = raw("GET", P, "ayse")
if c == 200:
    had, before, before_type = True, data, hd.get("Content-Type", "image/png")

# 1) Kendisi yükler, sürüm artar
c, body, _ = raw("PUT", P, "ayse", PNG, "image/png")
check("EP1: kişi kendi fotoğrafını yükler", c == 200, (c, body[:200]))
_, me2 = api("ayse", "GET", "/api/employee/employees/me")
check("EP1: photoVersion artar", (me2.get("photoVersion") or 0) > (me.get("photoVersion") or 0), me2.get("photoVersion"))

# 2) Meslektaş görür (dizinde sürüm), değiştiremez; oturumsuz okunamaz
c, data, hd = raw("GET", P, "mehmet")
check("EP2: meslektaş fotoğrafı görür", c == 200 and data == PNG and hd.get("Content-Type") == "image/png", (c, hd.get("Content-Type")))
check("EP2: önbellek private", "private" in (hd.get("Cache-Control") or ""), hd.get("Cache-Control"))
_, d = api("mehmet", "GET", "/api/employee/employees/directory")
check("EP2: dizinde photoVersion", any(x["id"] == eid and x.get("photoVersion") for x in d))
c, _, _ = raw("PUT", P, "mehmet", PNG, "image/png")
check("EP2: meslektaş değiştiremez (403)", c == 403, c)
c, _, _ = raw("DELETE", P, "mehmet")
check("EP2: meslektaş silemez (403)", c == 403, c)
c, _, _ = raw("GET", P)
check("EP2: oturumsuz okunamaz", c == 401, c)

# 3) İçerik doğrulaması: SVG, png diye bildirilen HTML, büyük dosya
c, _, _ = raw("PUT", P, "ayse", b"<svg xmlns='http://www.w3.org/2000/svg' onload='alert(1)'/>", "image/svg+xml")
check("EP3: SVG reddedilir", c == 400, c)
c, _, _ = raw("PUT", P, "ayse", b"<html><script>alert(1)</script></html>", "image/png")
check("EP3: png diye gönderilen HTML reddedilir", c == 400, c)
c, _, _ = raw("PUT", P, "ayse", PNG + b"\0" * (600 * 1024), "image/png")
check("EP3: 512 KB üstü reddedilir", c in (400, 413), c)

# 4) İK başkasının fotoğrafını yükleyip kaldırabilir
c, _, _ = raw("PUT", P, "ik", PNG, "image/png")
check("EP4: İK yükleyebilir", c == 200, c)
c, _, _ = raw("DELETE", P, "ik")
check("EP4: İK kaldırabilir", c == 204, c)
c, _, _ = raw("GET", P, "ayse")
check("EP4: silince 404", c == 404, c)
_, me3 = api("ayse", "GET", "/api/employee/employees/me")
check("EP4: silince photoVersion null", me3.get("photoVersion") is None, me3.get("photoVersion"))

# Önceki durumu geri getir
if had:
    raw("PUT", P, "ayse", before, before_type)

print("\nSONUÇ:", "BAŞARILI" if not FAIL else f"{len(FAIL)} HATA: {FAIL}")
sys.exit(1 if FAIL else 0)

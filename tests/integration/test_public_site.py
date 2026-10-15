"""Herkese açık site (SSS, gizlilik, koşullar, iletişim) ve arama motoru ayarları.

- Anonim uç: /api/tenant/public/site; varsayılan olarak arama motorlarına kapalı (robots.txt
  "Disallow: /", sitemap boş, X-Robots-Tag: noindex).
- Yalnızca platform yöneticisi değiştirir; doğrulama (e-posta, telefon, SSS soru/cevap eşleşmesi).
- Açılınca robots.txt kişisel yolları engeller ve sitemap'i gösterir, sitemap mutlak adresli.
- Değişiklik denetim kaydına yazılır. Sonda ayarlar eski haline döner.
"""

import os
import random
import string
import subprocess
import sys
import urllib.request

sys.path.insert(0, os.path.dirname(__file__))
from common import BASE, FAIL, api, check, http  # noqa: E402

ROOT = os.path.join(os.path.dirname(__file__), "..", "..")
RUN = "".join(random.choices(string.ascii_lowercase + string.digits, k=6))
T = "/api/tenant"
FAIL.clear()


def psql(sql):
    out = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", "hr360_operational", "-Atc", sql],
                         capture_output=True, text=True, cwd=ROOT)
    return (out.stdout + out.stderr).strip()


def raw(path):
    req = urllib.request.Request(BASE + path)
    with urllib.request.urlopen(req, timeout=20) as r:
        return r.status, dict(r.headers), r.read().decode()


code, before = api("platform", "GET", f"{T}/platform/settings")
check("Platform yöneticisi Sistem ayarlarını okur (site bölümü dahil)", code == 200 and isinstance(before, dict) and "site" in before, code)
site_before = (before or {}).get("site") or {}

try:
    code, pub = http("GET", f"{T}/public/site")
    check("Herkese açık site bilgisi oturumsuz okunur", code == 200 and isinstance(pub, dict) and "faq" in pub, code)

    for who in ("admin", "ik", "ayse"):
        code, _ = api(who, "PUT", f"{T}/platform/settings/site", {"faq": [], "searchIndexing": True})
        check(f"{who} site ayarını değiştiremez", code == 403, code)

    code, r = api("platform", "PUT", f"{T}/platform/settings/site", {"email": "gecersiz", "faq": [], "searchIndexing": False})
    check("Geçersiz e-posta reddedilir", code == 400 and (r or {}).get("field") == "email", (code, r))
    code, r = api("platform", "PUT", f"{T}/platform/settings/site", {"phone": "abc<script>", "faq": [], "searchIndexing": False})
    check("Geçersiz telefon reddedilir", code == 400 and (r or {}).get("field") == "phone", (code, r))
    code, r = api("platform", "PUT", f"{T}/platform/settings/site", {"faq": [{"q": "Cevapsız soru?", "a": ""}], "searchIndexing": False})
    check("Cevapsız SSS sorusu reddedilir", code == 400 and (r or {}).get("field") == "faq", (code, r))

    # Kapalıyken: robots her şeyi engeller, sitemap boş ve noindex.
    api("platform", "PUT", f"{T}/platform/settings/site", {**{k: site_before.get(k) for k in site_before}, "searchIndexing": False})
    _, h, body = raw("/robots.txt")
    check("Kapalıyken robots.txt tüm siteyi engeller", "Disallow: /\n" in body and "Sitemap:" not in body, body[:200])
    _, h, body = raw("/sitemap.xml")
    check("Kapalıyken sitemap boş ve noindex", "<url>" not in body and h.get("X-Robots-Tag") == "noindex", (h.get("X-Robots-Tag"), body[:200]))

    md = "# Başlık\n\nMetin <script>alert(1)</script> [bağlantı](javascript:alert(1))"
    body = {
        "operatorName": f"Test İşletmeci {RUN}",
        "email": f"kvkk.{RUN}@example.com",
        "phone": "+90 212 000 00 00",
        "kep": f"test{RUN}@hs01.kep.tr",
        "verbisNo": "123456",
        "address": "Test Mah. Deneme Sok. No:1 İstanbul",
        "privacyMd": md,
        "termsMd": "",
        "faq": [{"q": "Soru 1?", "a": "Cevap **1**"}, {"q": "Soru 2?", "a": "Cevap 2"}],
        "searchIndexing": True,
    }
    code, r = api("platform", "PUT", f"{T}/platform/settings/site", body)
    site = (r or {}).get("site") or {}
    check("Platform yöneticisi site bilgilerini kaydeder", code == 200 and site.get("operatorName") == body["operatorName"], (code, r))
    check("Boş koşul metni varsayılan şablona bırakılır (null)", site.get("termsMd") is None, site.get("termsMd"))

    code, pub = http("GET", f"{T}/public/site")
    check("Herkese açık uç yeni bilgileri döner", code == 200 and pub.get("email") == body["email"] and len(pub.get("faq") or []) == 2, pub)
    check("Markdown metni olduğu gibi saklanır (güvenli işleme arayüzde)", (pub or {}).get("privacyMd") == md, (pub or {}).get("privacyMd"))

    _, _, robots = raw("/robots.txt")
    check("Açıkken robots.txt panel, API ve jetonlu yolları engeller",
          all(f"Disallow: {p}\n" in robots for p in ("/panel", "/api/", "/parola-belirle/", "/kariyer/*/teklif/")), robots[:400])
    check("Açıkken robots.txt mutlak sitemap adresi verir", "Sitemap: http" in robots and "/sitemap.xml" in robots, robots[-120:])
    _, h, sitemap = raw("/sitemap.xml")
    check("Açıkken sitemap herkese açık sayfaları mutlak adresle listeler",
          all(f"{p}</loc>" in sitemap for p in ("/giris", "/sss", "/gizlilik", "/kosullar", "/iletisim")) and "<loc>http" in sitemap, sitemap[:400])
    check("Sitemap XML türünde", "xml" in (h.get("Content-Type") or ""), h.get("Content-Type"))

    rows = psql("""SELECT count(*) FROM audit_log WHERE "Action" = 'PlatformSiteChanged' AND "OccurredAt" > now() - interval '10 minutes'""")
    check("Site değişikliği denetim kaydında", int(rows or 0) >= 1, rows)
finally:
    restore = {k: site_before.get(k) for k in ("operatorName", "address", "email", "phone", "kep", "verbisNo", "privacyMd", "termsMd")}
    restore["faq"] = site_before.get("faq") or []
    restore["searchIndexing"] = bool(site_before.get("searchIndexing"))
    code, _ = api("platform", "PUT", f"{T}/platform/settings/site", restore)
    check("Site ayarları eski haline döndü", code == 200, code)

print(f"FAILS: {len(FAIL)}")
sys.exit(1 if FAIL else 0)

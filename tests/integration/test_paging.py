#!/usr/bin/env python3
"""G24: sunucu tarafı sayfalama, X-Total-Count, sort/q ve sayfasız (eski) biçimle uyumluluk.

Uçlar: çalışanlar, izin talepleri, izin bakiyeleri, departmanlar (60 sn önbellekli), ekipler,
performans değerlendirmeleri ve hedefleri, işe alışma planları ve demirbaşlar.

Ön koşul: HR360 çalışıyor, scripts/sql/2026-10-09_paging_indexes.sql uygulanmış.
Test yalnızca TEST-PG önekli departmanlar ve demirbaşlar oluşturur ve sonunda siler.
"""
import json
import os
import subprocess
import sys
import urllib.parse
import urllib.request

sys.path.insert(0, os.path.dirname(__file__))
from common import AYSE, BASE, FAIL, api, check, tok  # noqa: E402

ROOT = os.path.join(os.path.dirname(__file__), "..", "..")
FAIL.clear()
PREFIX = "TEST-PG"


def psql(sql):
    out = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", "hr360_operational", "-Atc", sql],
                         capture_output=True, text=True, cwd=ROOT)
    return (out.stdout + out.stderr).strip()


def get(who, path, **params):
    """GET; (durum, gövde, X-Total-Count) döner."""
    q = {k: v for k, v in params.items() if v is not None}
    url = BASE + path + ("?" + urllib.parse.urlencode(q) if q else "")
    req = urllib.request.Request(url, headers={"Authorization": "Bearer " + tok(who)})
    try:
        with urllib.request.urlopen(req, timeout=30) as r:
            body, code, total = r.read().decode(), r.status, r.headers.get("X-Total-Count")
    except urllib.error.HTTPError as e:
        body, code, total = e.read().decode(), e.code, e.headers.get("X-Total-Count")
    try:
        body = json.loads(body) if body else None
    except ValueError:
        pass
    return code, body, (int(total) if total is not None else None)


def all_pages(who, path, size, **params):
    items, total = [], None
    for page in range(1, 200):
        code, body, hdr = get(who, path, page=page, pageSize=size, **params)
        if code != 200:
            return None, None
        total = body["total"]
        items += body["items"]
        if not body["items"] or len(items) >= total:
            break
    return items, total


def cleanup():
    psql(f"""DELETE FROM organization_departments WHERE "Name" LIKE '{PREFIX}%'""")
    psql(f"""DELETE FROM onboarding_assets WHERE "AssetTag" LIKE '{PREFIX}%'""")


cleanup()

# ---------------------------------------------------------------------- çalışanlar
E = "/api/employee/employees"
code, legacy, hdr = get("admin", E)
check("Çalışanlar: sayfasız istek eski biçimde (düz dizi) döner", code == 200 and isinstance(legacy, list), (code, legacy))
check("Çalışanlar: sayfasız yanıtta X-Total-Count = kayıt sayısı", hdr == len(legacy or []), (hdr, len(legacy or [])))
n = len(legacy or [])
code, p1, hdr = get("admin", E, page=1, pageSize=2)
check("Çalışanlar: sayfalı yanıt {items,total,page,pageSize}", code == 200 and set(p1) >= {"items", "total", "page", "pageSize"}, p1)
check("Çalışanlar: sayfa boyutu ve toplam", code == 200 and len(p1["items"]) == min(2, n) and p1["total"] == n and hdr == n, (p1, hdr, n))
items, total = all_pages("admin", E, 2)
check("Çalışanlar: sayfalar örtüşmez ve tümü sayfasız listeyle aynı",
      items is not None and sorted(e["id"] for e in items) == sorted(e["id"] for e in legacy) and len({e["id"] for e in items}) == len(items),
      (len(items or []), n))
code, big, _ = get("admin", E, page=1, pageSize=500)
check("Çalışanlar: pageSize en fazla 200'e kırpılır", code == 200 and big["pageSize"] == 200, big and big.get("pageSize"))
code, beyond, hdr = get("admin", E, page=999, pageSize=20)
check("Çalışanlar: son sayfanın ötesi boş, toplam yine doğru", code == 200 and beyond["items"] == [] and beyond["total"] == n and hdr == n, beyond)
code, qa, _ = get("admin", E, page=1, pageSize=20, q="ayse")
check("Çalışanlar: q Türkçe harf katlamalı arar ('ayse' → Ayşe)", code == 200 and any(e["id"] == AYSE for e in qa["items"]), qa)
code, qn, _ = get("admin", E, page=1, pageSize=20, q="zzz-yok-boyle-biri")
check("Çalışanlar: eşleşmeyen q boş sayfa ve total 0", code == 200 and qn["items"] == [] and qn["total"] == 0, qn)
code, pct, _ = get("admin", E, page=1, pageSize=20, q="%")
check("Çalışanlar: q'daki % joker olarak yorumlanmaz", code == 200 and pct["total"] == 0, pct)
code, s_asc, _ = get("admin", E, page=1, pageSize=200, sort="hireDate", dir="asc")
code2, s_desc, _ = get("admin", E, page=1, pageSize=200, sort="hireDate", dir="desc")
dates = [e["hireDate"] for e in (s_asc or {}).get("items", [])]
check("Çalışanlar: sort=hireDate asc sıralı", code == 200 and dates == sorted(dates), dates)
check("Çalışanlar: dir=desc ters sıra",
      code2 == 200 and [e["hireDate"] for e in s_desc["items"]] == sorted(dates, reverse=True), s_desc and [e["hireDate"] for e in s_desc["items"]])
code, st, _ = get("admin", E, page=1, pageSize=50, status=0)
check("Çalışanlar: status filtresi", code == 200 and all(e["status"] in (0, "Active") for e in st["items"]), st)
code_any, _, _ = get("ayse", E, page=1, pageSize=5)
check("Çalışan rolü: e-postasız (sayfalı) tam liste yine yasak", code_any == 403, code_any)

# ---------------------------------------------------------------------- izin talepleri
L = "/api/leave/leave-requests"
code, legacy, hdr = get("admin", L)
check("İzin talepleri: sayfasız eski biçim + X-Total-Count", code == 200 and isinstance(legacy, list) and hdr == len(legacy), (code, hdr))
created = [r["createdAt"] for r in legacy or []]
check("İzin talepleri: sayfasız sıra değişmedi (en yeni önce)", created == sorted(created, reverse=True))
n = len(legacy or [])
code, p1, hdr = get("admin", L, page=1, pageSize=10)
check("İzin talepleri: 10'luk sayfa", code == 200 and len(p1["items"]) == min(10, n) and p1["total"] == n and hdr == n, (p1 and p1["total"], hdr, n))
check("İzin talepleri: 1. sayfa sayfasız listenin ilk 10'u",
      code == 200 and [r["id"] for r in p1["items"]] == [r["id"] for r in legacy[:10]])
items, total = all_pages("admin", L, 50)
check("İzin talepleri: tüm sayfalar = sayfasız liste (örtüşmesiz)",
      items is not None and len(items) == n and {r["id"] for r in items} == {r["id"] for r in legacy}, (len(items or []), n))
code, sub, hdr = get("admin", L, page=1, pageSize=200, status="Submitted")
check("İzin talepleri: status filtresi + toplam", code == 200 and all(r["status"] == "Submitted" for r in sub["items"])
      and sub["total"] == sum(1 for r in legacy if r["status"] == "Submitted") and hdr == sub["total"], sub and sub["total"])
code, sd, _ = get("admin", L, page=1, pageSize=200, sort="startDate", dir="asc")
starts = [r["startDate"] for r in (sd or {}).get("items", [])]
check("İzin talepleri: sort=startDate asc", code == 200 and starts == sorted(starts), starts[:5])
code, dd, _ = get("admin", L, page=1, pageSize=200, sort="days", dir="desc")
days = [r["days"] for r in (dd or {}).get("items", [])]
check("İzin talepleri: sort=days desc", code == 200 and days == sorted(days, reverse=True), days[:5])
code, qt, _ = get("admin", L, page=1, pageSize=200, q="zzz-yok", qTypes="Annual")
check("İzin talepleri: qTypes ile tür adı araması", code == 200 and qt["total"] > 0 and all(r["type"] == "Annual" for r in qt["items"]), qt and qt["total"])
src = next((r for r in legacy if r.get("reason") and len(r["reason"].strip()) >= 4 and r["reason"].isascii()), None)
if src:
    code, qr, _ = get("admin", L, page=1, pageSize=200, q=src["reason"].strip()[:4].upper())
    check("İzin talepleri: q gerekçede büyük/küçük harf duyarsız arar", code == 200 and src["id"] in [r["id"] for r in qr["items"]],
          qr and qr["total"])
code, fr, _ = get("admin", L, page=1, pageSize=200, **{"from": "2026-01-01", "to": "2026-12-31"})
check("İzin talepleri: from/to tarih çakışması", code == 200
      and all(r["endDate"] >= "2026-01-01" and r["startDate"] <= "2026-12-31" for r in fr["items"]), fr and fr["total"])
code, own, hdr = get("ayse", L, page=1, pageSize=5)
check("İzin talepleri: çalışan sayfalı istekte yalnızca kendi taleplerini görür",
      code == 200 and all(r["employeeId"] == AYSE for r in own["items"]) and own["total"] == sum(1 for r in legacy if r["employeeId"] == AYSE),
      own and own["total"])
code, other, _ = get("ayse", L, page=1, pageSize=5, employeeId="00000000-0000-0000-0000-000000000001")
check("İzin talepleri: çalışan başkasının taleplerini sayfalı da göremez", code == 403, code)
code, own_legacy, hdr = get("ayse", L)
check("İzin talepleri: çalışanın sayfasız isteği eski biçimde", code == 200 and isinstance(own_legacy, list) and hdr == len(own_legacy))

code, bal, hdr = get("admin", "/api/leave/leave-balances", page=1, pageSize=3)
code2, bal_legacy, hdr2 = get("admin", "/api/leave/leave-balances")
check("İzin bakiyeleri: sayfalı + sayfasız uyumlu", code == 200 and code2 == 200 and isinstance(bal_legacy, list)
      and bal["total"] == len(bal_legacy) == hdr == hdr2 and len(bal["items"]) == min(3, len(bal_legacy)), (bal and bal.get("total"), hdr2))

# ---------------------------------------------------------------------- departmanlar (önbellekli)
D = "/api/organization/departments"
code, companies = api("admin", "GET", "/api/organization/companies")
company = companies[0]["id"] if code == 200 and companies else None
check("Şirket var (departman testi için)", company is not None, companies)
names = [f"{PREFIX} Dept {c}" for c in "EDCBA"]
ids = []
for nm in names:
    code, d = api("admin", "POST", D, {"name": nm, "companyId": company})
    if code in (200, 201):
        ids.append(d["id"])
check("TEST departmanları oluşturuldu", len(ids) == 5, ids)
code, legacy, hdr = get("admin", D)
check("Departmanlar: sayfasız eski biçim + X-Total-Count", code == 200 and isinstance(legacy, list) and hdr == len(legacy), (code, hdr))
code, p, hdr = get("admin", D, page=1, pageSize=2, q=PREFIX.lower())
check("Departmanlar: q + sayfa (ada göre artan)", code == 200 and p["total"] == 5 and hdr == 5
      and [d["name"] for d in p["items"]] == [f"{PREFIX} Dept A", f"{PREFIX} Dept B"], p)
code, p3, _ = get("admin", D, page=3, pageSize=2, q=PREFIX)
check("Departmanlar: son sayfa", code == 200 and [d["name"] for d in p3["items"]] == [f"{PREFIX} Dept E"], p3)
code, pd, _ = get("admin", D, page=1, pageSize=2, q=PREFIX, dir="desc")
check("Departmanlar: dir=desc", code == 200 and [d["name"] for d in pd["items"]] == [f"{PREFIX} Dept E", f"{PREFIX} Dept D"], pd)
# Önbellek: yazma sonrası liste hemen güncel (sürüm eskitme)
code, extra = api("admin", "POST", D, {"name": f"{PREFIX} Dept AA", "companyId": company})
code, p, hdr = get("admin", D, page=1, pageSize=2, q=PREFIX)
check("Departmanlar: oluşturma önbelleği hemen eskitir", code == 200 and p["total"] == 6
      and [d["name"] for d in p["items"]] == [f"{PREFIX} Dept A", f"{PREFIX} Dept AA"], p)
if extra and extra.get("id"):
    api("admin", "PUT", f"{D}/{extra['id']}", {"name": f"{PREFIX} Dept ZZ"})
    code, p, _ = get("admin", D, page=1, pageSize=2, q=PREFIX)
    check("Departmanlar: güncelleme önbelleği hemen eskitir", code == 200 and f"{PREFIX} Dept AA" not in [d["name"] for d in p["items"]], p)
    api("admin", "DELETE", f"{D}/{extra['id']}")
    code, p, _ = get("admin", D, page=1, pageSize=2, q=PREFIX)
    check("Departmanlar: silme önbelleği hemen eskitir", code == 200 and p["total"] == 5, p and p["total"])
code, kotu, _ = get("ayse", D, page=1, pageSize=50, q=PREFIX)
check("Departmanlar: aynı kiracının çalışanı da görür (referans verisi)", code == 200 and kotu["total"] == 5, kotu)
mi = psql("""SELECT count(*) FROM organization_departments WHERE "TenantSlug" <> 'demo' AND "Name" LIKE 'TEST-PG%'""")
check("Departmanlar: TEST kayıtları yalnızca demo kiracısında", mi.strip() == "0", mi)

# ---------------------------------------------------------------------- ekipler
T = "/api/organization/teams"
code, legacy, hdr = get("admin", T)
code2, p, hdr2 = get("admin", T, page=1, pageSize=1)
check("Ekipler: sayfasız + sayfalı uyumlu", code == 200 and code2 == 200 and isinstance(legacy, list) and hdr == len(legacy)
      and p["total"] == len(legacy) == hdr2 and len(p["items"]) == min(1, len(legacy)), (hdr, p))

# ---------------------------------------------------------------------- performans
for path, label in (("/api/performance/reviews", "Değerlendirmeler"), ("/api/performance/goals", "Hedefler")):
    code, legacy, hdr = get("admin", path)
    check(f"{label}: sayfasız eski biçim + X-Total-Count", code == 200 and isinstance(legacy, list) and hdr == len(legacy), (code, hdr))
    items, total = all_pages("admin", path, 3)
    check(f"{label}: tüm sayfalar = sayfasız liste", items is not None and [x["id"] for x in items] == [x["id"] for x in legacy],
          (len(items or []), len(legacy or [])))
    code, p, hdr = get("admin", path, page=1, pageSize=2, sort="createdAt", dir="asc")
    asc = sorted(legacy or [], key=lambda x: (x["createdAt"]))
    check(f"{label}: sort=createdAt asc", code == 200 and [x["createdAt"] for x in p["items"]] == [x["createdAt"] for x in asc[:2]], p and p["items"][:2])
code, gown, _ = get("ayse", "/api/performance/goals", page=1, pageSize=5)
check("Hedefler: çalışan sayfalı istekte yalnızca kendi hedeflerini görür", code == 200 and all(g["employeeId"] == AYSE for g in gown["items"]), gown)
code, rown, _ = get("ayse", "/api/performance/reviews", page=1, pageSize=5)
check("Değerlendirmeler: çalışan sayfalı istekte yalnızca yetkili kayıtları görür", code == 200
      and all(r["employeeId"] == AYSE or r["reviewerEmployeeId"] == AYSE for r in rown["items"]), rown)
code, rs, _ = get("admin", "/api/performance/reviews", page=1, pageSize=50, submitted="true")
check("Değerlendirmeler: submitted=true filtresi", code == 200 and all(r["submittedAt"] for r in rs["items"]), rs and rs["total"])

# ---------------------------------------------------------------------- işe alışma planları
OP = "/api/onboarding/onboarding-plans"
code, legacy, hdr = get("admin", OP)
check("Planlar: sayfasız eski biçim + X-Total-Count", code == 200 and isinstance(legacy, list) and hdr == len(legacy), (code, hdr))
created = [x["createdAt"] for x in legacy or []]
check("Planlar: sayfasız sıra değişmedi (en yeni önce)", created == sorted(created, reverse=True))
check("Planlar: sayfasız yanıtta görevler gömülü", all("tasks" in x for x in legacy or []))
n = len(legacy or [])
code, p1, hdr = get("admin", OP, page=1, pageSize=2)
check("Planlar: sayfalı yanıt {items,total,page,pageSize}", code == 200 and set(p1) >= {"items", "total", "page", "pageSize"}
      and len(p1["items"]) == min(2, n) and p1["total"] == n and hdr == n, (p1 and p1.get("total"), hdr, n))
items, total = all_pages("admin", OP, 2)
check("Planlar: tüm sayfalar = sayfasız liste (örtüşmesiz, aynı sıra)",
      items is not None and [x["id"] for x in items] == [x["id"] for x in legacy], (len(items or []), n))
code, big, _ = get("admin", OP, page=1, pageSize=500)
check("Planlar: pageSize en fazla 200'e kırpılır", code == 200 and big["pageSize"] == 200, big and big.get("pageSize"))
code, ip, hdr = get("admin", OP, page=1, pageSize=200, status="InProgress")
check("Planlar: status filtresi + toplam", code == 200 and all(x["status"] == "InProgress" for x in ip["items"])
      and ip["total"] == sum(1 for x in legacy if x["status"] == "InProgress") and hdr == ip["total"], ip and ip["total"])
code, sd, _ = get("admin", OP, page=1, pageSize=200, sort="startDate", dir="asc")
starts = [x["startDate"] for x in (sd or {}).get("items", [])]
check("Planlar: sort=startDate asc", code == 200 and starts == sorted(starts), starts[:5])
code, qe, _ = get("admin", OP, page=1, pageSize=200, q="zzz-yok", qEmployeeIds=AYSE)
check("Planlar: qEmployeeIds ile çalışan adı araması", code == 200
      and qe["total"] == sum(1 for x in legacy if x["employeeId"] == AYSE) and all(x["employeeId"] == AYSE for x in qe["items"]), qe and qe["total"])
code, pct, _ = get("admin", OP, page=1, pageSize=20, q="%")
check("Planlar: q'daki % joker olarak yorumlanmaz", code == 200 and pct["total"] == 0, pct and pct["total"])
code, own, _ = get("ayse", OP, page=1, pageSize=50)
code2, own_legacy, hdr = get("ayse", OP)
check("Planlar: çalışan sayfalı/sayfasız yalnızca görebildiği planları alır (aynı küme)",
      code == 200 and code2 == 200 and isinstance(own_legacy, list) and own["total"] == len(own_legacy) == hdr
      and {x["id"] for x in own["items"]} <= {x["id"] for x in legacy}, (own and own.get("total"), hdr))

# ---------------------------------------------------------------------- demirbaşlar
A = "/api/onboarding/assets"
asset_ids = []
for i, (typ, model) in enumerate((("Laptop", "Çalışma İstasyonu"), ("Phone", "Telefon"), ("Laptop", "Dizüstü"),
                                  ("Monitor", "Ekran"), ("Laptop", "Dizüstü"))):
    code, a = api("admin", "POST", A, {"assetTag": f"{PREFIX}-{i + 1}", "type": typ, "model": model, "serialNumber": f"SN_{i}%"})
    if code in (200, 201):
        asset_ids.append(a["id"])
check("TEST demirbaşları oluşturuldu", len(asset_ids) == 5, asset_ids)
code, legacy, hdr = get("admin", A)
check("Demirbaşlar: sayfasız eski biçim + X-Total-Count", code == 200 and isinstance(legacy, list) and hdr == len(legacy), (code, hdr))
tags = [x["assetTag"] for x in legacy or [] if x["assetTag"].startswith(PREFIX)]
check("Demirbaşlar: sayfasız sıra değişmedi (etikete göre artan)", tags == [f"{PREFIX}-{i}" for i in range(1, 6)], tags)
check("Demirbaşlar: sayfasız yanıtta zimmet alanları var",
      all({"assignedEmployeeId", "assignedOn", "expectedReturnOn"} <= set(x) for x in legacy or []))
n = len(legacy or [])
items, total = all_pages("admin", A, 2)
check("Demirbaşlar: tüm sayfalar = sayfasız liste (örtüşmesiz, aynı sıra)",
      items is not None and total == n and [x["id"] for x in items] == [x["id"] for x in legacy], (len(items or []), n))
by_id = {x["id"]: x for x in legacy or []}
check("Demirbaşlar: sayfadaki zimmet alanları sayfasız listeyle aynı",
      items is not None and all(x["assignedEmployeeId"] == by_id[x["id"]]["assignedEmployeeId"] for x in items))
code, big, _ = get("admin", A, page=1, pageSize=500)
check("Demirbaşlar: pageSize en fazla 200'e kırpılır", code == 200 and big["pageSize"] == 200, big and big.get("pageSize"))
code, p, hdr = get("admin", A, page=1, pageSize=2, q=PREFIX.lower())
check("Demirbaşlar: q + sayfa (etikete göre artan)", code == 200 and p["total"] == 5 and hdr == 5
      and [x["assetTag"] for x in p["items"]] == [f"{PREFIX}-1", f"{PREFIX}-2"], p)
code, pd, _ = get("admin", A, page=1, pageSize=2, q=PREFIX, dir="desc")
check("Demirbaşlar: dir=desc", code == 200 and [x["assetTag"] for x in pd["items"]] == [f"{PREFIX}-5", f"{PREFIX}-4"], pd)
code, qf, _ = get("admin", A, page=1, pageSize=50, q="calisma istasyonu")
check("Demirbaşlar: q Türkçe harf katlamalı arar (model)", code == 200 and f"{PREFIX}-1" in [x["assetTag"] for x in qf["items"]], qf)
code, qs_, _ = get("admin", A, page=1, pageSize=50, q="SN_3%")
check("Demirbaşlar: q'daki % ve _ joker değil (seri no)", code == 200 and [x["assetTag"] for x in qs_["items"]] == [f"{PREFIX}-4"], qs_)
code, ql, _ = get("admin", A, page=1, pageSize=200, q=PREFIX, type="Laptop")
check("Demirbaşlar: type filtresi + q", code == 200 and ql["total"] == 3 and all(x["type"] == "Laptop" for x in ql["items"]), ql and ql["total"])
code, qt, _ = get("admin", A, page=1, pageSize=200, q="zzz-yok", qTypes="Monitor")
check("Demirbaşlar: qTypes ile tür adı araması", code == 200 and qt["total"] == sum(1 for x in legacy if x["type"] == "Monitor")
      and all(x["type"] == "Monitor" for x in qt["items"]), qt and qt["total"])
held = next((x for x in legacy or [] if x.get("assignedEmployeeId")), None)
if held:
    code, qe, _ = get("admin", A, page=1, pageSize=200, q="zzz-yok", qEmployeeIds=held["assignedEmployeeId"])
    check("Demirbaşlar: qEmployeeIds ile zimmetli kişi araması", code == 200 and held["id"] in [x["id"] for x in qe["items"]]
          and all(x["assignedEmployeeId"] == held["assignedEmployeeId"] for x in qe["items"]), qe and qe["total"])
code, av, hdr = get("admin", A, page=1, pageSize=200, status="Available")
check("Demirbaşlar: status filtresi + toplam", code == 200 and all(x["status"] == "Available" for x in av["items"])
      and av["total"] == sum(1 for x in legacy if x["status"] == "Available") and hdr == av["total"], av and av["total"])
for i in asset_ids:
    api("admin", "DELETE", f"{A}/{i}")

# ---------------------------------------------------------------------- temizlik
for i in ids:
    api("admin", "DELETE", f"{D}/{i}")
cleanup()
left = psql(f"""SELECT count(*) FROM organization_departments WHERE "Name" LIKE '{PREFIX}%'""")
check("Temizlik: TEST departmanları silindi", left.strip() == "0", left)
left = psql(f"""SELECT count(*) FROM onboarding_assets WHERE "AssetTag" LIKE '{PREFIX}%'""")
check("Temizlik: TEST demirbaşları silindi", left.strip() == "0", left)

print(f"\nFAILS: {len(FAIL)}")
for f in FAIL:
    print("  -", f)
sys.exit(1 if FAIL else 0)

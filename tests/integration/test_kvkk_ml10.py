"""Dalga 10 — KVKK ve ML:
53 VERBİS envanteri (tohumlama, düzenleme, CSV/HTML/JSON dışa aktarım, denetim kaydı),
54 ilgili kişi başvurusu (gün sayacı, erişim başvurusunda otomatik veri paketi, kimlik ve sonuçlandırma kuralı),
55 yeniden onay kampanyası (yeni sürümde otomatik, hedef/ilerleme, giriş bandı, hatırlatma sınırı),
56/57 imha önizlemesi (tablo bazında kuru çalıştırma), kapsam denetimi, nesne deposu silme doğrulaması
     (governance → tenant-service iç ucu),
36 kiracı verisiyle eğitim izni ve asgari veri eşiği, 49 "Sana uygun" önerileri, 51 aday–ilan uygunluğu.

Test kayıtları: başvuru açıklaması "TEST-W10", metin sürümü "test-w10", VERBİS faaliyeti "TEST W10",
dosya anahtarı "test-w10/". tests/support/cleanup_test_data.py bunları siler.
"""

import hashlib
import io
import json
import os
import subprocess
import sys
import urllib.request
import zipfile

sys.path.insert(0, os.path.dirname(__file__))
from common import AYSE, BASE, FAIL, G, api, check, tok  # noqa: E402

ROOT = os.path.join(os.path.dirname(__file__), "..", "..")
P = f"{G}/privacy"
FAIL.clear()


def psql(sql):
    out = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", "hr360_operational", "-Atc", sql],
                         cwd=ROOT, capture_output=True, text=True)
    return (out.stdout + out.stderr).strip()


def download(who, path):
    req = urllib.request.Request(BASE + path, headers={"Authorization": "Bearer " + tok(who)})
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            return r.status, r.headers.get("Content-Type", ""), r.read()
    except urllib.error.HTTPError as e:
        return e.code, e.headers.get("Content-Type", ""), e.read()


def cleanup():
    # Başvurular ve bağlı bildirimleri (yanıt bildirimi) tests/support/cleanup_test_data.py birlikte siler.
    psql("""DELETE FROM governance_consents WHERE "TenantSlug" = 'demo' AND "Version" = 'test-w10'""")
    psql("""DELETE FROM governance_privacy_notices WHERE "TenantSlug" = 'demo' AND "Version" = 'test-w10'""")
    psql("""DELETE FROM governance_consent_campaigns WHERE "TenantSlug" = 'demo' AND "Version" = 'test-w10'""")
    psql("""DELETE FROM governance_privacy_inventory WHERE "TenantSlug" = 'demo' AND "Activity" LIKE 'TEST W10%'""")
    psql(f"""DELETE FROM governance_storage_deletions WHERE "TenantSlug" = 'demo' AND "KeyHash" = '{hashlib.sha256(b"test-w10/yok.pdf").hexdigest()}'""")


cleanup()

# ============================================================ 53) VERBİS envanteri
code, v = api("ayse", "GET", f"{P}/verbis")
check("VERBİS: çalışan erişemez", code == 403, code)
code, v = api("admin", "GET", f"{P}/verbis")
if code == 503:
    print("UYARI: 2026-10-23_kvkk_ml.sql uygulanmamış; dalga 10 testleri atlanıyor.")
    print(f"FAILS: {len(FAIL) + 1}")
    sys.exit(1)
keys = {i["key"] for i in v["items"]} if code == 200 else set()
check("VERBİS: üründen tohumlanır (izin, bordro, işe alım, yeni ML faaliyetleri)", code == 200 and {"leave", "payroll", "recruitment", "candidate-fit", "attrition-training"} <= keys, (code, sorted(keys)[:10]))
check("VERBİS: son güncelleme bilgisi", code == 200 and v["lastUpdatedAt"], v.get("lastUpdatedAt") if code == 200 else code)
body = {"module": "Test", "activity": "TEST W10 ziyaretçi kaydı", "subjects": ["Ziyaretçiler"], "dataCategories": ["Kimlik (ad, soyad)", "Görsel kayıt (kamera)"],
        "purpose": "Bina güvenliği", "legalBasis": "m.5/2-f meşru menfaat", "special": False, "retention": "30 gün", "retentionCategory": None,
        "recipients": ["Güvenlik firması"], "transferProviders": [], "measures": "Erişim yalnızca güvenlik birimi", "isActive": True}
code, bad = api("admin", "POST", f"{P}/verbis/items", {**body, "legalBasis": ""})
check("VERBİS: hukuki sebep zorunlu", code == 400, code)
code, bad = api("admin", "POST", f"{P}/verbis/items", {**body, "transferProviders": ["yok-boyle"]})
check("VERBİS: bilinmeyen aktarım sağlayıcısı reddedilir", code == 400, code)
code, created = api("admin", "POST", f"{P}/verbis/items", body)
check("VERBİS: şirket faaliyet ekler", code == 200 and created.get("key", "").startswith("custom-"), (code, created))
cid = created.get("id") if code == 200 else None
code, upd = api("admin", "PUT", f"{P}/verbis/items/{cid}", {**body, "purpose": "Bina ve çalışan güvenliği", "transferProviders": ["slack"]})
check("VERBİS: düzenleme değişen alanları döner", code == 200 and set(upd.get("changed", [])) == {"purpose", "transferProviders"}, (code, upd))
aud = psql(f"""SELECT count(*) FROM audit_log WHERE "TenantSlug" = 'demo' AND "EntityType" = 'VerbisInventory' AND "Action" = 'Updated' AND "Changes"::text LIKE '%Bina ve çalışan%'""")
check("VERBİS: düzenleme denetim kaydında önceki/sonraki değerle", aud.isdigit() and int(aud) >= 1, aud)
st, ctype, raw = download("admin", f"{P}/verbis/export?format=csv")
csv = raw.decode("utf-8-sig") if st == 200 else ""
check("VERBİS: CSV (noktalı virgül, Türkçe başlık, kategori başına satır)", st == 200 and "text/csv" in ctype and csv.startswith("Veri kategorisi;")
      and "Görsel kayıt;kamera" in csv and "Slack" in csv, (st, ctype, csv[:120]))
st, ctype, raw = download("admin", f"{P}/verbis/export?format=html")
check("VERBİS: yazdırılabilir HTML", st == 200 and "text/html" in ctype and b"<table>" in raw and "TEST W10".encode() in raw, (st, ctype))
code, js = api("admin", "GET", f"{P}/verbis/export?format=json")
check("VERBİS: JSON (arayüz XLSX)", code == 200 and len(js["columns"]) == 12 and all(len(r) == 12 for r in js["rows"]), code)
exp = psql("""SELECT count(*) FROM audit_log WHERE "TenantSlug" = 'demo' AND "EntityType" = 'VerbisInventory' AND "Action" = 'Exported' AND "OccurredAt" > now() - interval '5 minutes'""")
check("VERBİS: her dışa aktarım denetim kaydında", exp.isdigit() and int(exp) >= 3, exp)
leave = next((i for i in v["items"] if i["key"] == "leave"), None)
code, _ = api("admin", "DELETE", f"{P}/verbis/items/{leave['id']}") if leave else (0, None)
check("VERBİS: katalog faaliyeti silinemez (yalnızca pasifleştirilir)", code == 409, code)
code, _ = api("admin", "DELETE", f"{P}/verbis/items/{cid}")
check("VERBİS: şirketin eklediği faaliyet silinir", code == 204, code)

# ============================================================ 54) başvuru: gün sayacı ve veri paketi
code, r = api("ayse", "POST", f"{P}/requests", {"kind": "Access", "details": "TEST-W10 verilerimin dökümünü istiyorum"})
check("Başvuru: erişim talebi alınır", code == 200, (code, r))
rid = r.get("id") if code == 200 else None
code, mine = api("ayse", "GET", f"{P}/requests")
m = next((x for x in mine if x["id"] == rid), None) if code == 200 else None
check("Başvuru: gün sayacı (1/30) ve otomatik veri paketi", m and m["day"] == 1 and m["legalDays"] == 30 and m["package"] and m["package"]["downloadable"] is False, m)
st, _, _ = download("ayse", f"{P}/requests/{rid}/package")
check("Paket: başvurucu sonuçlanmadan indiremez", st == 404, st)
st, ctype, raw = download("admin", f"{P}/requests/{rid}/package")
ok_zip = False
if st == 200:
    z = zipfile.ZipFile(io.BytesIO(raw))
    names = set(z.namelist())
    manifest = json.loads(z.read("manifest.json"))
    data = json.loads(z.read("kisisel-veri.json"))
    ok_zip = {"kisisel-veri.json", "erisim-kayitlari.json", "basvuru.txt", "BENIOKU.txt", "manifest.json"} <= names \
        and all(hashlib.sha256(z.read(f["file"])).hexdigest() == f["sha256"] for f in manifest["files"]) \
        and data["calisan"] and data["calisan"][0]["Id"] == AYSE
check("Paket: İK indirir; ZIP içeriği, SHA-256 manifesti ve çalışanın verisi", ok_zip, (st, ctype))
psql(f"""UPDATE governance_data_requests SET "CreatedAt" = now() - interval '21 days', "DueAt" = now() + interval '9 days' WHERE "Id" = '{rid}'""")
code, mine = api("admin", "GET", f"{P}/requests")
m = next((x for x in mine if x["id"] == rid), None) if code == 200 else None
check("Başvuru: 21 gün önce alınan başvuru 22. günde görünür", m and m["day"] == 22, m and m.get("day"))
code, _ = api("admin", "PATCH", f"{P}/requests/{rid}", {"status": "Completed", "response": "TEST-W10 veri paketiniz Profilim › Gizlilik ekranında."})
check("Başvuru: sonuçlandırılır", code == 200, code)
st, ctype, raw = download("ayse", f"{P}/requests/{rid}/package")
check("Paket: başvurucu sonuçlanınca indirir (ZIP)", st == 200 and raw[:2] == b"PK", (st, ctype))
st, _, _ = download("mehmet", f"{P}/requests/{rid}/package")
check("Paket: başkası indiremez", st == 404, st)
aud = psql(f"""SELECT string_agg(DISTINCT "Action", ',') FROM audit_log WHERE "TenantSlug" = 'demo' AND (("EntityType" = 'DataRequest' AND "EntityId" = '{rid}') OR ("EntityType" = 'PersonalDataExport' AND "Changes"::text LIKE '%{rid}%'))""")
check("Paket: hazırlama ve indirmeler denetim kaydında", "PackagePrepared" in aud and "Exported" in aud, aud)
code, r2 = api("admin", "POST", f"{P}/requests/external", {"kind": "Access", "personName": "TEST-W10 Dış Başvurucu", "employeeId": AYSE, "channel": "Email",
                                                          "details": "TEST-W10 e-postayla gelen erişim talebi"})
eid = r2.get("id") if code == 200 else None
code, _ = api("admin", "POST", f"{P}/requests/{eid}/package")
check("Paket: kimliği doğrulanmamış dış başvuruda hazırlanamaz", code == 400, code)
code, _ = api("admin", "POST", f"{P}/requests/{eid}/verify", {"method": "RegisteredEmail"})
code, lst = api("admin", "GET", f"{P}/requests")
e = next((x for x in lst if x["id"] == eid), None) if code == 200 else None
check("Paket: kimlik doğrulanınca otomatik hazırlanır", e and e["package"] is not None, e and e.get("package"))

# ============================================================ 55) yeniden onay kampanyası
TYPE = "GORSEL_KULLANIM"
api("ayse", "POST", f"{P}/consents/me", {"consentType": TYPE, "granted": True})  # eski (güncel) sürüme onay
code, n = api("admin", "POST", f"{P}/notices", {"type": TYPE, "title": "Fotoğraf ve ad kullanımı", "version": "test-w10", "changeNote": "TEST W10",
                                                "text": "Şirket içi bülten, intranet ve sosyal medya hesaplarında fotoğrafımın ve adımın kullanılmasına izin veriyorum."})
check("Kampanya: yeni sürüm yayımlanır", code == 200, (code, n))
code, cs = api("admin", "GET", f"{P}/consent-campaigns")
c = next((x for x in cs["campaigns"] if x["type"] == TYPE and x["version"] == "test-w10"), None) if code == 200 else None
pend = {p["employeeId"] for p in (c or {}).get("pending") or []}
check("Kampanya: otomatik açılır, eski sürüme onay veren Ayşe bekleyenlerde", c and c["status"] == "Open" and c["current"] and AYSE in pend and c["target"] >= 1, c)
code, mine = api("ayse", "GET", f"{P}/consents/pending")
check("Kampanya: giriş bandı güncellenen metni gösterir", code == 200 and any(x["type"] == TYPE and x["reason"] == "updated" for x in mine), (code, mine))
code, rem = api("admin", "POST", f"{P}/consent-campaigns/{c['id']}/remind") if c else (0, None)
check("Kampanya: bekleyenlere hatırlatma", code == 200 and rem["sent"] >= 1, (code, rem))
code, _ = api("admin", "POST", f"{P}/consent-campaigns/{c['id']}/remind") if c else (0, None)
check("Kampanya: 24 saatte bir hatırlatma", code == 429, code)
api("ayse", "POST", f"{P}/consents/me", {"consentType": TYPE, "granted": False})
code, mine = api("ayse", "GET", f"{P}/consents/pending")
check("Kampanya: yeni sürüme yanıt (ret de yanıttır) sonrası bant kalkar", code == 200 and not any(x["type"] == TYPE for x in mine), mine)
code, cs = api("admin", "GET", f"{P}/consent-campaigns")
c2 = next((x for x in cs["campaigns"] if x["id"] == c["id"]), None) if code == 200 and c else None
check("Kampanya: ilerleme güncellenir", c2 and c2["done"] >= 1 and AYSE not in {p["employeeId"] for p in c2["pending"] or []}, c2)
code, _ = api("admin", "POST", f"{P}/consent-campaigns", {"type": TYPE})
check("Kampanya: aynı sürüme ikinci kampanya açılmaz", code == 409, code)
code, _ = api("ayse", "GET", f"{P}/consent-campaigns")
check("Kampanya: çalışan yönetemez", code == 403, code)
code, _ = api("admin", "POST", f"{P}/consent-campaigns/{c['id']}/close") if c else (0, None)
check("Kampanya: kapatılır", code == 200, code)

# ============================================================ 56/57) imha önizleme ve doğrulama
code, pols = api("admin", "GET", f"{P}/retention")
pol = {p["category"]: p for p in pols} if code == 200 else {}
code, pv = api("admin", "GET", f"{P}/retention/{pol['TerminatedEmployees']['id']}/preview")
tables = {t["table"] for t in pv.get("tables", [])} if code == 200 else set()
# Ayrılmış çalışan yoksa plan boştur (hedef 0); kapsam ise doğrulama ekranındaki adım listesinden okunur.
check("Önizleme: ayrılmış çalışan kuru çalıştırma", code == 200 and pv["subjects"] is not None and pv["retained"], (code, pv))
code, pv = api("admin", "GET", f"{P}/retention/{pol['RejectedCandidates']['id']}/preview")
check("Önizleme: aday kategorisi dosya anahtarlarını sayar", code == 200 and any(s["column"] == "ResumeStorageKey" for s in pv["storage"]), pv)
code, pv = api("admin", "GET", f"{P}/retention/{pol['Notifications']['id']}/preview")
check("Önizleme: tek tablolu kategori satır sayısı", code == 200 and pv["tables"][0]["table"] == "notification_messages" and pv["subjects"] is None, pv)
code, ver = api("admin", "GET", f"{P}/destruction-verification")
review = [c["table"] for c in ver.get("coverage", []) if c["status"] == "review"] if code == 200 else ["?"]
steps = {s["table"] for s in ver.get("steps", [])} if code == 200 else set()
check("Kapsam: kişi kimliği taşıyan her tablo planda ya da gerekçeli istisnada", code == 200 and not review, review)
check("Kapsam: tüm servislerin tabloları planda", {"engagement_profiles", "leave_requests", "timeshift_time_entries", "expense_travel_requests",
      "notification_messages", "governance_chat_context", "recruitment_candidates"} - steps <= {"recruitment_candidates"} and len(steps) >= 40, sorted(steps)[:5])
key = "test-w10/yok.pdf"
psql(f"""INSERT INTO governance_storage_deletions ("Id","TenantSlug","Category","SourceTable","SourceColumn","StorageKey","KeyHash","Status","CreatedAt")
         VALUES (gen_random_uuid(),'demo','RejectedCandidates','recruitment_candidates','ResumeStorageKey','{key}','{hashlib.sha256(key.encode()).hexdigest()}','Pending',now())""")
code, pr = api("admin", "POST", f"{P}/destruction-verification/process")
row = psql(f"""SELECT "Status" || '|' || coalesce("StorageKey", 'NULL') FROM governance_storage_deletions WHERE "KeyHash" = '{hashlib.sha256(key.encode()).hexdigest()}'""")
check("Nesne deposu: tenant-service doğrular, anahtar silinir (özet kalır)", code == 200 and pr["processed"] >= 1 and row.split("|")[0] in ("NotStored", "Absent", "Deleted")
      and row.endswith("|NULL"), (code, pr, row))
code, ver = api("admin", "GET", f"{P}/destruction-verification")
check("Doğrulama raporu: özet ve dosya konumları", code == 200 and any(r["keyHash"] == hashlib.sha256(key.encode()).hexdigest()[:12] for r in ver["recent"])
      and len(ver["storageMap"]) >= 5, code)
code, _ = api("ayse", "GET", f"{P}/destruction-verification")
check("Doğrulama raporu yalnızca İK", code == 403, code)

# ============================================================ ML 36) kiracı verisiyle eğitim
M = f"{G}/model/tenant-training"
code, _ = api("ayse", "GET", M)
check("Eğitim izni: çalışan göremez", code == 403, code)
code, st0 = api("admin", "GET", M)
check("Eğitim izni: varsayılan durum ve eşikler", code == 200 and st0["thresholds"]["minTrainRows"] == 200 and st0["thresholds"]["minTrainLeavers"] == 20, (code, st0))
code, pv = api("admin", "GET", f"{M}/preview")
check("Eğitim önizleme: demo şirketi eşiği sağlamaz (gerekçeli)", code == 200 and pv["ok"] is False and pv["refusals"], (code, pv))
was = st0.get("enabled") if code == 200 else False
code, _ = api("admin", "PUT", f"{M}/consent", {"enabled": False})
code, run = api("admin", "POST", f"{M}/run")
check("Eğitim: izin yokken reddedilir", code == 409 and run.get("code") == "no_consent", (code, run))
code, s1 = api("admin", "PUT", f"{M}/consent", {"enabled": True})
check("Eğitim izni: şirket yöneticisi verir", code == 200 and s1["enabled"] is True and s1["consentBy"], (code, s1))
code, run = api("admin", "POST", f"{M}/run")
check("Eğitim: veri yetersizse reddedilir (ML'e satır gitmez)", code == 422 and run.get("code") == "insufficient_data", (code, run))
api("admin", "PUT", f"{M}/consent", {"enabled": bool(was)})
aud = psql("""SELECT count(*) FROM audit_log WHERE "TenantSlug" = 'demo' AND "Action" LIKE 'TenantTrainingConsent%' AND "OccurredAt" > now() - interval '5 minutes'""")
check("Eğitim izni değişiklikleri denetim kaydında", aud.isdigit() and int(aud) >= 2, aud)

# ============================================================ ML 49) "Sana uygun"
code, rec = api("ayse", "GET", f"{G}/growth/recommendations")
check("Sana uygun: çalışan kendi önerilerini alır", code == 200 and rec["linked"] and {"postings", "mentors", "courses", "gaps"} <= rec.keys(), (code, rec))
check("Sana uygun: yalnızca öneri (otomatik işlem yok notu)", code == 200 and "otomatik" in (rec.get("note") or ""), rec.get("note") if code == 200 else code)
check("Sana uygun: mentor önerisinde kişinin kendisi yok", code == 200 and all(m["info"]["name"] != "Ayşe Yılmaz" for m in rec["mentors"]), code)

# ============================================================ ML 51) aday–ilan uygunluğu
pid = psql("""SELECT a."JobPostingId" FROM recruitment_applications a JOIN recruitment_candidates c ON c."Id" = a."CandidateId"
             WHERE a."TenantSlug" = 'demo' AND c."AnonymizedAt" IS NULL AND a."Status" <> 'Withdrawn' LIMIT 1""")
if pid and len(pid) == 36:
    code, fit = api("ayse", "GET", f"{G}/ai/recruit-fit/{pid}")
    check("Uygunluk: çalışan göremez", code == 403, code)
    code, fit = api("admin", "GET", f"{G}/ai/recruit-fit/{pid}")
    txt = json.dumps(fit, ensure_ascii=False) if code == 200 else ""
    check("Uygunluk: açıklamalı puan, otomatik ret yok", code == 200 and fit["results"] and all("reasons" in r and "components" in r for r in fit["results"])
          and "otomatik" in fit["note"], (code, txt[:300]))
    check("Uygunluk: yanıtta aday adı/e-postası yok", code == 200 and "firstName" not in txt and "@" not in txt.replace("@anonim", ""), txt[:200])
    aud = psql(f"""SELECT count(*) FROM audit_log WHERE "TenantSlug" = 'demo' AND "EntityType" = 'CandidateFit' AND "EntityId" = '{pid}' AND "Action" = 'AutomatedAnalysis'""")
    check("Uygunluk: hesaplama denetim kaydında", aud.isdigit() and int(aud) >= 1, aud)
else:
    print("UYARI: başvurusu olan ilan yok; uygunluk testleri atlandı")
code, card = api("admin", "GET", f"{G}/ai/recruit-fit/card")
check("Uygunluk: model kartı dışlanan nitelikleri listeler", code == 200 and {"name", "gender", "age", "photo", "address"} <= {e["attribute"] for e in card["excluded_attributes"]}, (code, card))

cleanup()
print(f"FAILS: {len(FAIL)}")
sys.exit(1 if FAIL else 0)

#!/usr/bin/env python3
"""Eğitim ve performans (Dalga 5c): yetkinlik matrisi + eğitim önerisi (Y19), eğitim içeriği /
sınav / SCORM 1.2 / sertifika (Y20), sertifika bitiş hatırlatmaları (G17), 9-kutu + kalibrasyon
ve dönem şablonları (G12).

Ön koşul: HR360 çalışıyor, deploy/testing/chat-mock.yml katmanı açık (CERT_REMINDER_SECONDS=3).
"""
import datetime as dt
import io
import json
import os
import subprocess
import sys
import time
import urllib.request
import uuid
import zipfile

sys.path.insert(0, os.path.dirname(__file__))
from common import AYSE, BASE, FAIL, api, check, tok  # noqa: E402

L = "/api/learning"
P = "/api/performance"
ROOT = os.path.join(os.path.dirname(__file__), "..", "..")
FAIL.clear()
TODAY = dt.date.today()


def psql(sql):
    out = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", "hr360_operational", "-Atc", sql],
                         capture_output=True, text=True, cwd=ROOT)
    return (out.stdout + out.stderr).strip()


def raw(method, path, headers=None, data=None):
    req = urllib.request.Request(BASE + path, data=data, method=method, headers=headers or {})
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            return r.status, dict(r.headers), r.read()
    except urllib.error.HTTPError as e:
        return e.code, dict(e.headers), e.read()


def upload(who, path, filename, content, fields=None):
    boundary = "----hr360" + uuid.uuid4().hex
    parts = []
    for k, v in (fields or {}).items():
        parts.append(f'--{boundary}\r\nContent-Disposition: form-data; name="{k}"\r\n\r\n{v}\r\n'.encode())
    parts.append(f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="{filename}"\r\nContent-Type: application/zip\r\n\r\n'.encode()
                 + content + b"\r\n")
    parts.append(f"--{boundary}--\r\n".encode())
    code, _, body = raw("POST", path, {"Authorization": "Bearer " + tok(who), "Content-Type": f"multipart/form-data; boundary={boundary}"}, b"".join(parts))
    try:
        return code, json.loads(body) if body else None
    except ValueError:
        return code, body[:200]


def cleanup():
    psql("""DELETE FROM learning_courses WHERE "Title" LIKE 'TEST5c%';
            DELETE FROM learning_certifications WHERE "Name" LIKE 'TEST5c%';
            DELETE FROM learning_competencies WHERE "Name" LIKE 'TEST5c%';
            DELETE FROM learning_scorm_packages WHERE "Title" LIKE 'TEST5c%';
            DELETE FROM notification_messages WHERE "Body" LIKE '%TEST5c%';
            DELETE FROM performance_review_scores WHERE "ReviewId" IN (SELECT r."Id" FROM performance_reviews r JOIN performance_cycles c ON c."Id" = r."CycleId" WHERE c."Name" LIKE 'TEST5c%');
            DELETE FROM performance_reviews WHERE "CycleId" IN (SELECT "Id" FROM performance_cycles WHERE "Name" LIKE 'TEST5c%');
            DELETE FROM performance_snapshots WHERE "CycleId" IN (SELECT "Id" FROM performance_cycles WHERE "Name" LIKE 'TEST5c%');
            DELETE FROM performance_cycles WHERE "Name" LIKE 'TEST5c%';
            DELETE FROM performance_cycle_templates WHERE "Name" LIKE 'TEST5c%';""")


cleanup()
DEPT = psql("""SELECT "Id" FROM organization_departments WHERE "TenantSlug" = 'demo' AND "Name" = 'Mühendislik'""")
MEHMET = psql("""SELECT "HeadEmployeeId" FROM organization_departments WHERE "Id" = '%s'""" % DEPT)
AYSE_TITLE = psql(f"""SELECT "PositionTitle" FROM employee_assignments WHERE "EmployeeId" = '{AYSE}' AND "EffectiveTo" IS NULL LIMIT 1""")
ZEYNEP = psql(f"""SELECT "Id" FROM employee_employees WHERE "TenantSlug" = 'demo' AND "Id" NOT IN ('{AYSE}', '{MEHMET}') AND "Status" <> 'Terminated' LIMIT 1""")
check("Hazırlık: departman, yönetici, unvan", len(DEPT) == 36 and len(MEHMET) == 36 and AYSE_TITLE, (DEPT, MEHMET, AYSE_TITLE))

try:
    # ================================================================== Y19 yetkinlik matrisi
    code, c1 = api("admin", "POST", f"{L}/competencies", {"name": "TEST5c Kubernetes", "category": "Teknik", "description": "Konteyner orkestrasyonu"})
    check("İK yetkinlik tanımlar", code == 200 and c1["name"] == "TEST5c Kubernetes", (code, c1))
    code, c2 = api("admin", "POST", f"{L}/competencies", {"name": "TEST5c Sunum", "category": "Davranışsal"})
    code, _ = api("admin", "POST", f"{L}/competencies", {"name": "test5c kubernetes"})
    check("Aynı ad (büyük/küçük harf) ikinci kez eklenemez", code == 409, code)
    code, _ = api("ayse", "POST", f"{L}/competencies", {"name": "TEST5c Yetkisiz"})
    check("Çalışan yetkinlik tanımlayamaz", code == 403, code)

    code, rp1 = api("admin", "PUT", f"{L}/competencies/role-profiles", {"competencyId": c1["id"], "positionTitle": AYSE_TITLE.upper(), "requiredLevel": 4})
    check("Rol profili: pozisyon için seviye 4", code == 200 and rp1["requiredLevel"] == 4, (code, rp1))
    code, rp2 = api("admin", "PUT", f"{L}/competencies/role-profiles", {"competencyId": c2["id"], "departmentId": DEPT, "requiredLevel": 3})
    check("Rol profili: departman için seviye 3", code == 200, (code, rp2))
    code, _ = api("admin", "PUT", f"{L}/competencies/role-profiles", {"competencyId": c2["id"], "departmentId": DEPT, "positionTitle": "X", "requiredLevel": 3})
    check("Pozisyon ve departman birlikte verilemez", code == 400, code)
    code, rp2b = api("admin", "PUT", f"{L}/competencies/role-profiles", {"competencyId": c2["id"], "departmentId": DEPT, "requiredLevel": 3})
    check("Aynı hedef güncellenir, yinelenmez", code == 200 and rp2b["id"] == rp2["id"], rp2b)

    code, a1 = api("ayse", "POST", f"{L}/competencies/assessments", {"employeeId": AYSE, "competencyId": c1["id"], "level": 2})
    check("Öz değerlendirme (kaynak Self)", code == 200 and a1["source"] == "Self", (code, a1))
    time.sleep(1)
    code, a2 = api("mehmet", "POST", f"{L}/competencies/assessments", {"employeeId": AYSE, "competencyId": c1["id"], "level": 3, "note": "Projede gözlemlendi"})
    check("Yönetici değerlendirmesi (kaynak Manager, değerlendiren adı)", code == 200 and a2["source"] == "Manager" and a2["assessedByName"], (code, a2))
    code, _ = api("ayse", "POST", f"{L}/competencies/assessments", {"employeeId": MEHMET, "competencyId": c1["id"], "level": 5})
    check("Çalışan yöneticisini değerlendiremez", code == 403, code)
    code, _ = api("ayse", "POST", f"{L}/competencies/assessments", {"employeeId": AYSE, "competencyId": c1["id"], "level": 7})
    check("Seviye 1-5 dışında reddedilir", code == 400, code)

    code, g = api("ayse", "GET", f"{L}/competencies/gaps/me")
    items = {i["competencyId"]: i for i in g.get("items", [])} if code == 200 else {}
    check("Açık analizi: son değerlendirme geçerli (4-3=1)", items.get(c1["id"], {}).get("current") == 3 and items[c1["id"]]["gap"] == 1 and items[c1["id"]]["source"] == "Manager", g)
    check("Değerlendirilmemiş yetkinlik: güncel yok, açık = beklenen", items.get(c2["id"], {}).get("current") is None and items[c2["id"]]["gap"] == 3, items.get(c2["id"]))
    check("Açıklar büyükten küçüğe sıralı", [i["competencyId"] for i in g["items"]][:2] == [c2["id"], c1["id"]], [i["competency"] for i in g["items"]])
    code, _ = api("mehmet", "GET", f"{L}/competencies/gaps/{AYSE}")
    check("Departman başkanı çalışanın açığını görür", code == 200, code)
    code, _ = api("ayse", "GET", f"{L}/competencies/gaps/{MEHMET}")
    check("Çalışan başkasının açığını göremez", code == 404, code)
    code, _ = api("admin", "GET", f"{L}/competencies/gaps/{AYSE}")
    check("İK herkesin açığını görür", code == 200, code)
    viewed = psql(f"""SELECT count(*) FROM audit_log WHERE "EntityType" = 'CompetencyGap' AND "EntityId" = '{AYSE}' AND "Action" = 'SensitiveViewed' AND "OccurredAt" > now() - interval '5 minutes'""")
    check("Başkasının açık analizine erişim kaydı", viewed.isdigit() and int(viewed) >= 2, viewed)

    code, team = api("mehmet", "GET", f"{L}/competencies/team")
    rows = {r["employeeId"]: r for r in team.get("rows", [])} if code == 200 else {}
    check("Isı haritası: yönetici kendi departmanını görür (kendisi hariç)", AYSE in rows and MEHMET not in rows, (code, list(rows)))
    cells = {c["competencyId"]: c for c in rows.get(AYSE, {}).get("cells", [])}
    check("Isı haritası hücreleri (beklenen/güncel/açık)", cells.get(c1["id"], {}).get("gap") == 1 and cells.get(c2["id"], {}).get("required") == 3, cells)
    code, _ = api("ayse", "GET", f"{L}/competencies/team")
    check("Çalışan ekip ısı haritasını göremez", code == 403, code)
    code, team = api("admin", "GET", f"{L}/competencies/team?departmentId={DEPT}")
    check("İK departman seçerek ısı haritasını görür", code == 200 and any(r["employeeId"] == MEHMET for r in team["rows"]), code)

    def course(title, **kw):
        body = {"title": title, "durationHours": 2, "category": "Technical", "isMandatory": False}
        body.update(kw)
        code, c = api("admin", "POST", f"{L}/courses", body)
        assert code in (200, 201), (code, c)
        return c

    ca = course("TEST5c K8s Temelleri")
    cb = course("TEST5c Sunum 101")
    cc = course("TEST5c İleri Sunum")
    api("admin", "PUT", f"{L}/courses/{ca['id']}/competencies", [{"competencyId": c1["id"], "targetLevel": 4}])
    api("admin", "PUT", f"{L}/courses/{cb['id']}/competencies", [{"competencyId": c2["id"], "targetLevel": 2}])
    code, tags = api("admin", "PUT", f"{L}/courses/{cc['id']}/competencies", [{"competencyId": c2["id"], "targetLevel": 5}])
    check("Eğitim yetkinlik + hedef seviyeyle etiketlenir", code == 200 and tags[0]["targetLevel"] == 5, (code, tags))
    code, _ = api("ayse", "PUT", f"{L}/courses/{cc['id']}/competencies", [])
    check("Çalışan etiket değiştiremez", code == 403, code)
    code, rec = api("ayse", "GET", f"{L}/competencies/recommendations/me")
    order = [i["courseId"] for i in rec.get("items", []) if i["title"].startswith("TEST5c")] if code == 200 else []
    check("Öneri: açığı kapatanlar açık büyüklüğüne göre sıralı", order == [cc["id"], cb["id"], ca["id"]], [i["title"] for i in rec.get("items", [])])
    check("Öneri yanıtı 'yalnızca öneri' notu taşır", "yalnızca bir öneridir" in rec.get("notice", ""), rec.get("notice"))
    first = rec["items"][0]["closes"][0]
    check("Öneri kapatılan aralığı gösterir (0→3)", first["from"] == 0 and first["to"] == 3, first)
    enrolled = psql(f"""SELECT count(*) FROM learning_enrollments WHERE "EmployeeId" = '{AYSE}' AND "CourseId" IN ('{ca['id']}','{cb['id']}','{cc['id']}')""")
    check("Öneri otomatik kayıt açmaz", enrolled == "0", enrolled)
    code, _ = api("ayse", "GET", f"{L}/competencies/recommendations/{MEHMET}")
    check("Başkasının önerisini çalışan göremez", code == 404, code)

    # ================================================================== Y20 içerik + sınav
    cq = course("TEST5c Sınavlı Eğitim", isMandatory=True, certificateValidityMonths=12)
    code, mt = api("admin", "POST", f"{L}/courses/{cq['id']}/modules", {"title": "Giriş metni", "kind": "Text", "textBody": "Hoş geldiniz."})
    check("Metin modülü eklenir", code == 200 and mt["kind"] == "Text", (code, mt))
    code, mv = api("admin", "POST", f"{L}/courses/{cq['id']}/modules", {"title": "Video", "kind": "Video", "videoUrl": "javascript:alert(1)"})
    check("Video modülü yalnızca http(s) bağlantısı kabul eder", code == 400, code)
    code, mq = api("admin", "POST", f"{L}/courses/{cq['id']}/modules", {"title": "Sınav", "kind": "Quiz", "passMarkPercent": 60, "maxAttempts": 3})
    qbody = {"questions": [
        {"text": "Pod nedir?", "kind": "Single", "options": [{"id": "a", "text": "Sanal makine"}, {"id": "b", "text": "En küçük dağıtım birimi"}, {"id": "c", "text": "Disk"}], "correct": ["b"]},
        {"text": "Hangileri denetleyicidir?", "kind": "Multiple", "options": [{"id": "a", "text": "Deployment"}, {"id": "b", "text": "ConfigMap"}, {"id": "c", "text": "StatefulSet"}, {"id": "d", "text": "Secret"}], "correct": ["a", "c"]},
    ]}
    code, _ = api("admin", "PUT", f"{L}/courses/{cq['id']}/modules/{mq['id']}/quiz", {"questions": [dict(qbody["questions"][0], correct=["a", "b"])]})
    check("Tek seçimli soruda iki doğru reddedilir", code == 400, code)
    code, r = api("admin", "PUT", f"{L}/courses/{cq['id']}/modules/{mq['id']}/quiz", qbody)
    check("Sınav soruları kaydedilir", code == 200 and r["questionCount"] == 2, (code, r))
    code, _ = api("ayse", "GET", f"{L}/courses/{cq['id']}/modules/{mq['id']}/quiz")
    check("Kayıtsız çalışan sınavı açamaz", code == 403, code)
    code, _ = api("ayse", "POST", f"{L}/courses/{cq['id']}/enroll", {"employeeId": AYSE})
    check("Çalışan kendini kaydeder", code in (200, 201), code)
    code, quiz = api("ayse", "GET", f"{L}/courses/{cq['id']}/modules/{mq['id']}/quiz")
    dump = json.dumps(quiz)
    check("Öğrenen sınavı alır (2 soru, seçenekler)", code == 200 and len(quiz["questions"]) == 2 and len(quiz["questions"][1]["options"]) == 4, (code, quiz))
    check("Sınav yanıtında doğru cevap YOK", "correct" not in dump.lower() and "answer" not in dump.lower(), dump[:400])
    code, mods = api("ayse", "GET", f"{L}/courses/{cq['id']}/modules")
    check("Modül listesinde de cevap anahtarı yok", code == 200 and "correct" not in json.dumps(mods).lower(), mods)
    code, _ = api("ayse", "GET", f"{L}/courses/{cq['id']}/modules/{mq['id']}/quiz/answer-key")
    check("Çalışan cevap anahtarını alamaz", code == 403, code)
    code, key = api("admin", "GET", f"{L}/courses/{cq['id']}/modules/{mq['id']}/quiz/answer-key")
    check("İK cevap anahtarını görür", code == 200 and key[0]["correct"] == ["b"], (code, key))
    q1, q2 = quiz["questions"][0]["id"], quiz["questions"][1]["id"]
    code, at1 = api("ayse", "POST", f"{L}/courses/{cq['id']}/modules/{mq['id']}/quiz/attempts", {"answers": {q1: ["b"], q2: ["a"]}})
    check("1. deneme sunucuda puanlanır: %50, kaldı", code == 200 and at1["scorePercent"] == 50 and at1["passed"] is False and at1["attemptsLeft"] == 2, (code, at1))
    check("Gönderim sonrası da doğru seçenek sızmaz", set(at1["perQuestion"][0].keys()) == {"questionId", "correct"} and '"b"' not in json.dumps(at1["perQuestion"]), at1["perQuestion"])
    code, mt_done = api("ayse", "POST", f"{L}/courses/{cq['id']}/modules/{mt['id']}/complete")
    check("Metin modülü tamamlandı, eğitim sürüyor", code == 200 and mt_done["enrollmentStatus"] == "InProgress", (code, mt_done))
    code, at2 = api("ayse", "POST", f"{L}/courses/{cq['id']}/modules/{mq['id']}/quiz/attempts", {"answers": {q1: ["b"], q2: ["c", "a"]}})
    check("2. deneme %100 geçti → eğitim tamamlandı + sertifika", code == 200 and at2["passed"] is True and at2["enrollmentStatus"] == "Completed" and at2["certificateId"], (code, at2))
    code, _ = api("ayse", "POST", f"{L}/courses/{cq['id']}/modules/{mq['id']}/quiz/attempts", {"answers": {q1: ["b"]}})
    check("Geçilen / sonuçlanan sınav tekrar gönderilemez", code == 409, code)
    cert_id = at2.get("certificateId")
    row = psql(f"""SELECT "VerificationCode" || '|' || "ExpiresOn" || '|' || "IsMandatory" FROM learning_certifications WHERE "Id" = '{cert_id}'""").split("|")
    check("Sertifika: doğrulama kodu, 12 ay geçerlilik, zorunlu", len(row) == 3 and len(row[0]) == 14 and row[1] in {(d.replace(year=d.year + 1)).isoformat() for d in (TODAY, TODAY - dt.timedelta(days=1), TODAY + dt.timedelta(days=1))} and row[2] in ("t", "true"), row)
    code, cv = api("ayse", "GET", f"{L}/certifications/{cert_id}/certificate")
    check("Çalışan yazdırılabilir sertifikayı alır", code == 200 and cv["courseTitle"] == "TEST5c Sınavlı Eğitim" and cv["verificationCode"] == row[0] and cv["score"] == 100, (code, cv))
    code, _ = api("mehmet", "GET", f"{L}/certifications/{cert_id}/certificate")
    check("Departman başkanı sertifikayı görür", code == 200, code)
    code, vf = api("mehmet", "GET", f"{L}/certifications/verify/{row[0].lower()}")
    check("Doğrulama kodu sorgusu (baş harflerle)", code == 200 and vf["valid"] is True and vf["holder"].endswith(".") and "Ayşe" not in json.dumps(vf, ensure_ascii=False), (code, vf))
    code, res = api("mehmet", "GET", f"{L}/courses/{cq['id']}/results")
    check("Yönetici departmanının sonuçlarını görür", code == 200 and any(r["progress"]["employeeId"] == AYSE for r in res), (code, res))
    code, _ = api("ayse", "GET", f"{L}/courses/{cq['id']}/results")
    check("Çalışan sonuç listesini göremez", code == 403, code)
    code, pr = api("admin", "GET", f"{L}/courses/{cq['id']}/progress?employeeId={AYSE}")
    check("İK ilerlemeyi görür (2/2 modül, sertifika)", code == 200 and pr["progress"]["completedModules"] == 2 and pr["progress"]["certificate"]["id"] == cert_id, (code, pr))
    notified = psql(f"""SELECT count(*) FROM notification_messages WHERE "TemplateCode" = 'learning.certificate.issued' AND "RecipientEmployeeId" = '{AYSE}' AND "Body" LIKE '%TEST5c Sınavlı%'""")
    check("Sertifika bildirimi gitti", notified == "1", notified)

    # Deneme hakkı
    cm = course("TEST5c Tek Hak")
    code, mq1 = api("admin", "POST", f"{L}/courses/{cm['id']}/modules", {"title": "Tek hak", "kind": "Quiz", "passMarkPercent": 100, "maxAttempts": 1})
    api("admin", "PUT", f"{L}/courses/{cm['id']}/modules/{mq1['id']}/quiz", {"questions": [qbody["questions"][0]]})
    api("ayse", "POST", f"{L}/courses/{cm['id']}/enroll", {"employeeId": AYSE})
    code, quiz1 = api("ayse", "GET", f"{L}/courses/{cm['id']}/modules/{mq1['id']}/quiz")
    code, f1 = api("ayse", "POST", f"{L}/courses/{cm['id']}/modules/{mq1['id']}/quiz/attempts", {"answers": {quiz1["questions"][0]["id"]: ["a"]}})
    check("Tek hakta kaldı → hak bitti, kayıt Failed", code == 200 and f1["passed"] is False and f1["attemptsLeft"] == 0 and f1["enrollmentStatus"] == "Failed", (code, f1))
    code, f2 = api("ayse", "POST", f"{L}/courses/{cm['id']}/modules/{mq1['id']}/quiz/attempts", {"answers": {quiz1["questions"][0]["id"]: ["b"]}})
    check("Hakkı biten sınava yeni deneme reddedilir", code == 409, (code, f2))
    attempts = psql(f"""SELECT count(*) FROM learning_quiz_attempts WHERE "ModuleId" = '{mq1['id']}'""")
    check("Fazladan deneme kaydedilmedi", attempts == "1", attempts)

    # ================================================================== Y20 SCORM 1.2
    manifest = """<?xml version="1.0" encoding="UTF-8"?>
<manifest identifier="TEST5C-PKG" version="1.0" xmlns="http://www.imsproject.org/xsd/imscp_rootv1p1p2"
  xmlns:adlcp="http://www.adlnet.org/xsd/adlcp_rootv1p2">
  <metadata><schema>ADL SCORM</schema><schemaversion>1.2</schemaversion></metadata>
  <organizations default="ORG1"><organization identifier="ORG1"><title>TEST5c Mini SCORM</title>
    <item identifier="I1" identifierref="RES1"><title>Ders</title></item></organization></organizations>
  <resources><resource identifier="RES1" type="webcontent" adlcp:scormtype="sco" href="index.html">
    <file href="index.html"/><file href="js/app.js"/></resource></resources>
</manifest>"""

    def make_zip(files):
        buf = io.BytesIO()
        with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as z:
            for name, content in files.items():
                z.writestr(name, content)
        return buf.getvalue()

    good = make_zip({"imsmanifest.xml": manifest, "index.html": "<!doctype html><html><body><script src='js/app.js'></script>TEST5c</body></html>",
                     "js/app.js": "var api = window.parent.API; api && api.LMSInitialize('');"})
    code, _ = upload("ayse", f"{L}/scorm/packages", "p.zip", good, {"title": "TEST5c yetkisiz"})
    check("Çalışan SCORM paketi yükleyemez", code == 403, code)
    code, r = upload("admin", f"{L}/scorm/packages", "p.zip", make_zip({"index.html": "x"}), {"title": "TEST5c bozuk"})
    check("imsmanifest.xml olmayan paket reddedilir", code == 400 and "imsmanifest" in str(r), (code, r))
    code, r = upload("admin", f"{L}/scorm/packages", "p.zip", make_zip({"imsmanifest.xml": manifest, "index.html": "x", "js/app.js": "x", "../evil.html": "x"}), {"title": "TEST5c kaçış"})
    check("Kök dışına çıkan yol (../) reddedilir", code == 400, (code, r))
    code, r = upload("admin", f"{L}/scorm/packages", "p.zip", make_zip({"imsmanifest.xml": manifest.replace("<schemaversion>1.2", "<schemaversion>2004 3rd Edition"), "index.html": "x", "js/app.js": "x"}), {"title": "TEST5c 2004"})
    check("SCORM 2004 paketi reddedilir (yalnızca 1.2)", code == 400 and "1.2" in str(r), (code, r))
    code, r = upload("admin", f"{L}/scorm/packages", "p.zip", make_zip({"imsmanifest.xml": manifest.replace('href="index.html">', 'href="yok.html">'), "index.html": "x"}), {"title": "TEST5c eksik"})
    check("Başlangıç dosyası pakette yoksa reddedilir", code == 400, (code, r))
    code, pkg = upload("admin", f"{L}/scorm/packages", "mini.zip", good, {"title": "TEST5c Mini SCORM"})
    check("Geçerli SCORM 1.2 paketi yüklenir", code == 200 and pkg["entryPoint"] == "index.html" and pkg["fileCount"] == 3, (code, pkg))
    stored = psql(f"""SELECT count(*) FROM learning_scorm_files WHERE "PackageId" = '{pkg['id']}'""")
    check("Dosyalar veritabanına açıldı", stored == "3", stored)

    cs = course("TEST5c SCORM Eğitimi")
    code, ms = api("admin", "POST", f"{L}/courses/{cs['id']}/modules", {"title": "Etkileşimli ders", "kind": "Scorm", "scormPackageId": pkg["id"]})
    check("SCORM modülü eklenir", code == 200 and ms["scormPackageId"] == pkg["id"], (code, ms))
    code, _, _ = raw("GET", f"{L}/scorm/{pkg['id']}/files/index.html", {"Authorization": "Bearer " + tok("ayse")})
    check("Kayıtsız çalışan paket dosyasını alamaz", code == 404, code)
    api("ayse", "POST", f"{L}/courses/{cs['id']}/enroll", {"employeeId": AYSE})
    code, hdrs, body = raw("POST", f"{L}/scorm/launch", {"Authorization": "Bearer " + tok("ayse"), "Content-Type": "application/json"},
                           json.dumps({"courseId": cs["id"], "moduleId": ms["id"]}).encode())
    launch = json.loads(body) if code == 200 else {}
    set_cookie = hdrs.get("Set-Cookie", "")
    check("Başlatma: giriş adresi + paket yoluna kapsamlı HttpOnly çerez", code == 200 and launch["launchUrl"] == f"{L}/scorm/{pkg['id']}/files/index.html"
          and f"path={L}/scorm/{pkg['id']}/files/" in set_cookie.lower() and "httponly" in set_cookie.lower() and "samesite=strict" in set_cookie.lower(), (code, set_cookie, launch))
    cookie = set_cookie.split(";")[0]
    code, hdrs, body = raw("GET", launch.get("launchUrl", "/x"), {"Cookie": cookie})
    check("iframe isteği (yalnızca çerez) dosyayı alır, text/html", code == 200 and b"TEST5c" in body and hdrs.get("Content-Type", "").startswith("text/html"), (code, hdrs.get("Content-Type")))
    check("Aynı köken çerçeveleme başlıkları (SAMEORIGIN / frame-ancestors 'self')",
          hdrs.get("X-Frame-Options", "").upper() == "SAMEORIGIN" or "frame-ancestors 'self'" in hdrs.get("Content-Security-Policy", ""), hdrs)
    code, hdrs, _ = raw("GET", f"{L}/scorm/{pkg['id']}/files/js/app.js", {"Cookie": cookie})
    check("Alt klasör dosyası doğru içerik türüyle", code == 200 and "javascript" in hdrs.get("Content-Type", ""), (code, hdrs.get("Content-Type")))
    code, _, _ = raw("GET", f"{L}/scorm/{pkg['id']}/files/index.html")
    check("Çerez/jeton olmadan dosya verilmez (401)", code == 401, code)
    code, _, _ = raw("GET", f"{L}/scorm/{uuid.uuid4()}/files/index.html", {"Cookie": cookie})
    check("Çerez başka paket için geçmez", code == 401, code)
    code, _, _ = raw("GET", f"{L}/scorm/{pkg['id']}/files/index.html", {"Cookie": cookie[:-3] + "AAA"})
    check("Bozuk imzalı çerez reddedilir", code == 401, code)
    code, _, _ = raw("GET", f"{L}/scorm/{pkg['id']}/files/%2e%2e/%2e%2e/etc/passwd", {"Cookie": cookie})
    check("Yol geçişi denemesi 404", code in (400, 404), code)
    code, _, _ = raw("GET", f"{L}/scorm/{pkg['id']}/files/index.html", {"Authorization": "Bearer " + tok("ayse")})
    check("Kayıtlı öğrenen Bearer ile de dosyayı alır", code == 200, code)

    rt_path = f"{L}/scorm/runtime"
    code, r = api("ayse", "PUT", rt_path, {"courseId": cs["id"], "moduleId": ms["id"], "finish": False, "values": {
        "cmi.core.lesson_status": "incomplete", "cmi.core.lesson_location": "sayfa-2", "cmi.suspend_data": "TEST5c-durum"}})
    check("LMSCommit: değerler kaydedildi", code == 200 and r["runtime"]["lessonLocation"] == "sayfa-2" and r["enrollmentStatus"] == "InProgress", (code, r))
    code, r = api("ayse", "GET", f"{rt_path}?courseId={cs['id']}&moduleId={ms['id']}")
    check("LMSGetValue için kayıtlı değerler geri okunur", code == 200 and r["suspendData"] == "TEST5c-durum" and r["lessonStatus"] == "incomplete" and r["entry"] == "ab-initio", (code, r))
    code, _ = api("ayse", "PUT", rt_path, {"courseId": cs["id"], "moduleId": ms["id"], "finish": False, "values": {"cmi.core.score.raw": "abc"}})
    check("Geçersiz score.raw reddedilir", code == 400, code)
    code, _ = api("mehmet", "PUT", rt_path, {"courseId": cs["id"], "moduleId": ms["id"], "finish": False, "values": {"cmi.core.lesson_status": "passed"}})
    check("Kayıtsız kişi başkasının çalışma zamanını yazamaz", code == 403, code)
    code, r = api("ayse", "PUT", rt_path, {"courseId": cs["id"], "moduleId": ms["id"], "finish": True, "values": {
        "cmi.core.lesson_status": "passed", "cmi.core.score.raw": "85"}})
    check("LMSFinish passed → eğitim tamamlandı + sertifika", code == 200 and r["enrollmentStatus"] == "Completed" and r["certificateId"], (code, r))
    db = psql(f"""SELECT "LessonStatus" || '|' || "ScoreRaw" || '|' || "SuspendData" || '|' || "LessonLocation" || '|' || "SessionCount" FROM learning_scorm_runtime WHERE "ModuleId" = '{ms['id']}'""")
    check("Çalışma zamanı veritabanında kalıcı", db == "passed|85.00|TEST5c-durum|sayfa-2|1", db)
    code, r = api("ayse", "GET", f"{rt_path}?courseId={cs['id']}&moduleId={ms['id']}")
    check("Yeniden açılışta entry=resume", r.get("entry") == "resume" and r.get("scoreRaw") == 85, r)
    code, _ = api("admin", "DELETE", f"{L}/scorm/packages/{pkg['id']}")
    check("Modülde kullanılan paket silinemez", code == 409, code)

    # ================================================================== G17 hatırlatmalar
    def cert(name, days, mandatory):
        code, c = api("admin", "POST", f"{L}/certifications", {"employeeId": AYSE, "name": name, "issuedOn": (TODAY - dt.timedelta(days=300)).isoformat(),
                                                              "expiresOn": (TODAY + dt.timedelta(days=days)).isoformat(), "isMandatory": mandatory})
        assert code in (200, 201), (code, c)
        return c["id"]

    k30 = cert("TEST5c Cert30", 20, True)
    k7 = cert("TEST5c Cert7", 5, False)
    kx = cert("TEST5c CertExp", -1, True)
    kf = cert("TEST5c CertFar", 60, True)

    def count(recipient, like):
        return psql(f"""SELECT count(*) FROM notification_messages WHERE "RecipientEmployeeId" = '{recipient}' AND "Body" LIKE '%{like}%'""")

    deadline = time.time() + 40
    while time.time() < deadline and psql(f"""SELECT count(*) FROM learning_cert_reminders WHERE "CertificationId" IN ('{k30}','{k7}','{kx}')""") != "5":
        time.sleep(1)
    check("30 gün kala çalışana + (zorunlu) yöneticiye", count(AYSE, "TEST5c Cert30") == "1" and count(MEHMET, "TEST5c Cert30") == "1", (count(AYSE, "TEST5c Cert30"), count(MEHMET, "TEST5c Cert30")))
    check("7 gün kala yalnızca çalışana (zorunlu değil)", count(AYSE, "TEST5c Cert7") == "1" and count(MEHMET, "TEST5c Cert7") == "0", (count(AYSE, "TEST5c Cert7"), count(MEHMET, "TEST5c Cert7")))
    check("Süresi dolunca çalışana + yöneticiye", count(AYSE, "TEST5c CertExp") == "1" and count(MEHMET, "TEST5c CertExp") == "1", (count(AYSE, "TEST5c CertExp"), count(MEHMET, "TEST5c CertExp")))
    check("60 gün kalan sertifikaya hatırlatma yok", count(AYSE, "TEST5c CertFar") == "0", count(AYSE, "TEST5c CertFar"))
    kinds = psql(f"""SELECT string_agg(DISTINCT "Kind", ',' ORDER BY "Kind") FROM learning_cert_reminders WHERE "CertificationId" IN ('{k30}','{k7}','{kx}')""")
    check("Hatırlatma türleri kaydedildi (D30, D7, Expired)", kinds == "D30,D7,Expired", kinds)
    time.sleep(8)
    total = psql("""SELECT count(*) FROM notification_messages WHERE "Body" LIKE '%TEST5c Cert%'""")
    check("Sonraki turlarda tekrar bildirim yok", total == "5", total)
    psql(f"""UPDATE learning_certifications SET "ExpiresOn" = current_date + 6 WHERE "Id" = '{kx}'""")
    deadline = time.time() + 20
    while time.time() < deadline and count(AYSE, "TEST5c CertExp") != "2":
        time.sleep(1)
    check("Yenilenen (bitişi değişen) sertifika için yeni döngü başlar", count(AYSE, "TEST5c CertExp") == "2", count(AYSE, "TEST5c CertExp"))

    # ================================================================== G12 şablon → dönem
    tpl_cfg = {"scale": {"min": 1, "max": 5, "labels": ["Yetersiz", "Gelişmeli", "Karşılıyor", "Aşıyor", "Örnek"]},
               "sections": [{"title": "Teknik", "weight": 60, "questions": [{"text": "Kod kalitesi"}, {"text": "Problem çözme"}]},
                            {"title": "Davranışsal", "weight": 40, "questions": [{"text": "İşbirliği"}]}], "goalWeightPercent": 40}
    bad = json.loads(json.dumps(tpl_cfg))
    bad["sections"][1]["weight"] = 30
    code, r = api("admin", "POST", f"{P}/review-cycles/templates", {"name": "TEST5c Bozuk", "period": "H1", "durationDays": 181, "config": bad})
    check("Ağırlık toplamı 100 değilse şablon reddedilir", code == 400 and "100" in r.get("message", ""), (code, r))
    code, _ = api("mehmet", "POST", f"{P}/review-cycles/templates", {"name": "TEST5c Yönetici", "period": "H1", "durationDays": 181, "config": tpl_cfg})
    check("Yönetici şablon oluşturamaz", code == 403, code)
    code, tpl = api("admin", "POST", f"{P}/review-cycles/templates", {"name": "TEST5c Yarıyıl", "description": "Standart", "period": "H1", "durationDays": 181, "config": tpl_cfg})
    check("İK dönem şablonu oluşturur", code == 200 and tpl["config"]["sections"][0]["weight"] == 60, (code, tpl))
    code, lst = api("mehmet", "GET", f"{P}/review-cycles/templates")
    check("Yönetici şablonları listeler", code == 200 and any(t["id"] == tpl["id"] for t in lst), code)
    code, cyc = api("admin", "POST", f"{P}/review-cycles/from-template", {"templateId": tpl["id"], "name": "TEST5c Dönem 2031", "year": 2031, "startDate": "2031-01-01"})
    check("Şablondan dönem: süre ve yapılandırma kopyalandı", code == 200 and cyc["endDate"] == "2031-06-30" and cyc["templateId"] == tpl["id"]
          and json.loads(cyc["configJson"])["sections"][1]["title"] == "Davranışsal" and cyc["period"] == "H1" and cyc["status"] == "Planned", (code, cyc))
    code, tpl2 = api("admin", "POST", f"{P}/review-cycles/{cyc['id']}/save-as-template", {"name": "TEST5c Yarıyıl kopya"})
    check("Dönem yeniden şablon olarak kaydedilir", code == 200 and tpl2["durationDays"] == 181 and tpl2["config"]["scale"]["labels"][4] == "Örnek", (code, tpl2))
    cid = cyc["id"]

    # ================================================================== G12 9-kutu
    psql(f"""UPDATE performance_cycles SET "Status" = 'Open' WHERE "Id" = '{cid}';
             INSERT INTO performance_goals ("Id","TenantSlug","CycleId","EmployeeId","Title","Weight","Status","CreatedAt")
             VALUES (gen_random_uuid(),'demo','{cid}','{AYSE}','TEST5c hedef',100,'Achieved',now());""")
    code, rv = api("mehmet", "POST", f"{P}/reviews", {"cycleId": cid, "employeeId": AYSE, "reviewerEmployeeId": MEHMET, "type": "Manager"})
    code2, _ = api("mehmet", "POST", f"{P}/reviews/{rv.get('id')}/submit", {"scores": [], "strengths": "TEST5c"})
    check("Hazırlık: yönetici değerlendirmesi gönderildi", code in (200, 201) and code2 == 200, (code, code2, rv))

    code, r = api("mehmet", "PUT", f"{P}/nine-box/potential", {"cycleId": cid, "employeeId": AYSE, "rating": 3, "note": "Liderlik eğilimi"})
    check("Yönetici departmanındaki çalışana potansiyel girer", code == 200 and r["rating"] == 3 and r["publishedToEmployee"] is False, (code, r))
    code, _ = api("ayse", "PUT", f"{P}/nine-box/potential", {"cycleId": cid, "employeeId": AYSE, "rating": 3})
    check("Çalışan kendi potansiyelini giremez", code == 403, code)
    if ZEYNEP:
        code, _ = api("mehmet", "PUT", f"{P}/nine-box/potential", {"cycleId": cid, "employeeId": ZEYNEP, "rating": 2})
        check("Yönetici başka departmandaki çalışana potansiyel giremez", code == 403, code)
        code, _ = api("admin", "PUT", f"{P}/nine-box/potential", {"cycleId": cid, "employeeId": ZEYNEP, "rating": 2})
        check("İK herkese potansiyel girer", code == 200, code)
    code, _ = api("ayse", "GET", f"{P}/nine-box?cycleId={cid}")
    check("Çalışan 9-kutu tablosunu göremez", code == 403, code)
    code, grid = api("mehmet", "GET", f"{P}/nine-box?cycleId={cid}")
    people = {e["employeeId"]: e for c in grid.get("cells", []) for e in c["employees"]} if code == 200 else {}
    unplaced = {e["employeeId"] for e in grid.get("unplaced", [])} if code == 200 else set()
    check("Yönetici: yalnızca kendi departmanı", AYSE in people and (not ZEYNEP or (ZEYNEP not in people and ZEYNEP not in unplaced)) and MEHMET not in people, (code, list(people), unplaced))
    ay = people.get(AYSE, {})
    check("Hücre eşlemesi: puan 100 → performans 3, potansiyel 3 → hücre 9", ay.get("score") == 100 and ay.get("performanceBand") == 3 and ay.get("cell") == 9, ay)
    check("Tartışma aracı notu", "tartışma aracı" in grid.get("notice", ""), grid.get("notice"))
    code, grid = api("admin", "GET", f"{P}/nine-box?cycleId={cid}")
    unplaced = {e["employeeId"]: e for e in grid.get("unplaced", [])}
    check("İK: herkes; puanı olmayan yerleşmemiş listede (potansiyeli görünür)", code == 200 and (not ZEYNEP or unplaced.get(ZEYNEP, {}).get("potential") == 2), (code, list(unplaced)))
    code, mp = api("ayse", "GET", f"{P}/nine-box/my-potential?cycleId={cid}")
    check("Potansiyel çalışana varsayılan olarak gizli", code == 200 and mp["published"] is False and "rating" not in mp, mp)
    code, _ = api("mehmet", "POST", f"{P}/nine-box/potential/publish", {"cycleId": cid, "employeeId": AYSE, "published": True})
    check("Yönetici yayımlayamaz (yalnızca İK)", code == 403, code)
    code, _ = api("admin", "POST", f"{P}/nine-box/potential/publish", {"cycleId": cid, "employeeId": AYSE, "published": True})
    code, mp = api("ayse", "GET", f"{P}/nine-box/my-potential?cycleId={cid}")
    check("İK yayımlayınca çalışan potansiyelini görür", code == 200 and mp["published"] is True and mp["rating"] == 3, mp)

    code, _ = api("mehmet", "POST", f"{P}/nine-box/override", {"cycleId": cid, "employeeId": AYSE, "performanceBand": 2, "potentialBand": 3, "reason": "Kalibrasyon toplantısı kararı"})
    check("Yönetici kalibrasyon düzeltmesi yapamaz", code == 403, code)
    code, _ = api("admin", "POST", f"{P}/nine-box/override", {"cycleId": cid, "employeeId": AYSE, "performanceBand": 2, "potentialBand": 3, "reason": "kısa"})
    check("Gerekçesiz/kısa gerekçeli düzeltme reddedilir", code == 400, code)
    code, ov = api("admin", "POST", f"{P}/nine-box/override", {"cycleId": cid, "employeeId": AYSE, "performanceBand": 2, "potentialBand": 3, "reason": "TEST5c kalibrasyon: hedef tek ve kolaydı"})
    check("İK hücreyi gerekçeyle düzeltir (2,3 → hücre 8)", code == 200 and ov["cell"] == 8, (code, ov))
    code, grid = api("mehmet", "GET", f"{P}/nine-box?cycleId={cid}")
    ay = next((e for c in grid["cells"] for e in c["employees"] if e["employeeId"] == AYSE), {})
    check("Tabloda düzeltilmiş hücre ve hesaplanan hücre birlikte", ay.get("cell") == 8 and ay.get("computedCell") == 9 and ay["override"]["reason"].startswith("TEST5c"), ay)
    aud = psql(f"""SELECT "Changes"->>'reason' || '|' || ("Changes"->>'to') FROM audit_log WHERE "EntityType" = 'NineBox' AND "Action" = 'Calibrated' AND "EntityId" = '{cid}:{AYSE}' ORDER BY "Id" DESC LIMIT 1""")
    check("Kalibrasyon denetim kaydında (gerekçe + yeni hücre)", aud == "TEST5c kalibrasyon: hedef tek ve kolaydı|8", aud)
    viewed = psql(f"""SELECT count(*) FROM audit_log WHERE "EntityType" = 'NineBox' AND "Action" = 'SensitiveViewed' AND "EntityId" = '{cid}'""")
    check("9-kutu görüntülemeleri erişim kaydında", viewed.isdigit() and int(viewed) >= 3, viewed)
finally:
    cleanup()

print(f"FAILS: {len(FAIL)}")
for f in FAIL:
    print("  -", f)
sys.exit(1 if FAIL else 0)

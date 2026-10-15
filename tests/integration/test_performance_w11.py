#!/usr/bin/env python3
"""Performans ve gelişim (Dalga 11): kalibrasyon oturumu (79), OKR hizalama ağacı (80),
anonim 360 derece geri bildirim (81).

Test kayıtları "TEST11P" önekiyle işaretlenir; sonda (ve cleanup_test_data.py ile) silinir.
"""
import datetime as dt
import os
import subprocess
import sys

sys.path.insert(0, os.path.dirname(__file__))
from common import AYSE, FAIL, api, check  # noqa: E402

P = "/api/performance"
ROOT = os.path.join(os.path.dirname(__file__), "..", "..")
FAIL.clear()
TODAY = dt.date.today()


def psql(sql):
    out = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", "hr360_operational", "-Atc", sql],
                         capture_output=True, text=True, cwd=ROOT)
    return (out.stdout + out.stderr).strip()


def cleanup():
    psql("""DELETE FROM performance_f360_requests WHERE "Title" LIKE 'TEST11P%';
            DELETE FROM performance_goals WHERE "Title" LIKE 'TEST11P%';
            DELETE FROM performance_objectives WHERE "Title" LIKE 'TEST11P%';
            DELETE FROM performance_calibration_sessions WHERE "Name" LIKE 'TEST11P%';
            DELETE FROM performance_review_scores WHERE "ReviewId" IN (SELECT r."Id" FROM performance_reviews r JOIN performance_cycles c ON c."Id" = r."CycleId" WHERE c."Name" LIKE 'TEST11P%');
            DELETE FROM performance_reviews WHERE "CycleId" IN (SELECT "Id" FROM performance_cycles WHERE "Name" LIKE 'TEST11P%');
            DELETE FROM performance_snapshots WHERE "CycleId" IN (SELECT "Id" FROM performance_cycles WHERE "Name" LIKE 'TEST11P%');
            DELETE FROM performance_cycles WHERE "Name" LIKE 'TEST11P%';
            DELETE FROM employee_assignments WHERE "EmployeeId" IN (SELECT "Id" FROM employee_employees WHERE "TenantSlug" = 'demo' AND "LastName" = 'TEST11P');
            DELETE FROM employee_employees WHERE "TenantSlug" = 'demo' AND "LastName" = 'TEST11P' AND "Email" LIKE 't11p%.x@demo.hr360';""")


cleanup()
DEPT = psql("""SELECT "Id" FROM organization_departments WHERE "TenantSlug" = 'demo' AND "Name" = 'Mühendislik'""")
MEHMET = psql(f"""SELECT "HeadEmployeeId" FROM organization_departments WHERE "Id" = '{DEPT}'""")
ELIF = psql("""SELECT "Id" FROM employee_employees WHERE "TenantSlug" = 'demo' AND "FirstName" = 'Elif' AND "LastName" = 'Şahin' LIMIT 1""")
# Demo şirketinde yeterli çalışan yok (360 için ≥5 değerlendiren gerekir): test kendi geçici çalışanlarını oluşturur
# (soyadı "TEST11P", e-posta "t11p<n>.x@demo.hr360"; sonda ve cleanup_test_data.py ile silinir). Demo verisine dokunulmaz.
for i in range(1, 5):
    psql(f"""INSERT INTO employee_employees ("Id","TenantSlug","FirstName","LastName","Email","HireDate","Status","CreatedAt")
             VALUES (gen_random_uuid(),'demo','T11p{i}','TEST11P','t11p{i}.x@demo.hr360','2024-01-01','Active',now())""")
OTHERS = [x for x in psql(f"""SELECT "Id" FROM employee_employees WHERE "TenantSlug" = 'demo' AND "Status" <> 'Terminated'
                              AND "Id" NOT IN ('{AYSE}', '{MEHMET}', '{ELIF or AYSE}') ORDER BY ("LastName" = 'TEST11P') DESC, "Id" LIMIT 5""").splitlines() if len(x) == 36]
OTHER_DEPT = psql(f"""SELECT "Id" FROM organization_departments WHERE "TenantSlug" = 'demo' AND "Id" <> '{DEPT}' LIMIT 1""")
check("Hazırlık: departman, yönetici, İK çalışanı ve en az 4 diğer çalışan", len(DEPT) == 36 and len(MEHMET) == 36 and len(ELIF) == 36 and len(OTHERS) >= 4,
      (DEPT, MEHMET, ELIF, OTHERS))
CID = psql("""INSERT INTO performance_cycles ("Id","TenantSlug","Name","Year","Period","StartDate","EndDate","Status","CreatedAt")
              VALUES (gen_random_uuid(),'demo','TEST11P Dönem',2032,'H1','2032-01-01','2032-06-30','Open',now()) RETURNING "Id" """).splitlines()[0]

try:
    # ================================================================== 79 kalibrasyon oturumu
    psql(f"""INSERT INTO performance_goals ("Id","TenantSlug","CycleId","EmployeeId","Title","Weight","Status","CreatedAt")
             VALUES (gen_random_uuid(),'demo','{CID}','{AYSE}','TEST11P 9-kutu hedefi',100,'Achieved',now());""")
    code, rv = api("mehmet", "POST", f"{P}/reviews", {"cycleId": CID, "employeeId": AYSE, "reviewerEmployeeId": MEHMET, "type": "Manager"})
    code2, _ = api("mehmet", "POST", f"{P}/reviews/{rv.get('id') if isinstance(rv, dict) else ''}/submit", {"scores": [], "strengths": "TEST11P"})
    code3, _ = api("mehmet", "PUT", f"{P}/nine-box/potential", {"cycleId": CID, "employeeId": AYSE, "rating": 3})
    check("Hazırlık: değerlendirme + potansiyel (Ayşe hücre 9)", code in (200, 201) and code2 == 200 and code3 == 200, (code, code2, code3))

    code, _ = api("mehmet", "GET", f"{P}/calibration/sessions?cycleId={CID}")
    check("Yönetici kalibrasyon oturumlarını göremez (yalnızca İK)", code == 403, code)
    code, _ = api("mehmet", "POST", f"{P}/calibration/sessions", {"cycleId": CID, "name": "TEST11P Yönetici"})
    check("Yönetici oturum açamaz", code == 403, code)
    code, s = api("admin", "POST", f"{P}/calibration/sessions", {"cycleId": CID, "name": "TEST11P Kalibrasyon"})
    check("İK oturum açar; yerleşim kopyalanır", code == 200 and s["placed"] >= 1, (code, s))
    SID = s.get("id") if isinstance(s, dict) else None
    code, _ = api("admin", "POST", f"{P}/calibration/sessions", {"cycleId": CID, "name": "TEST11P İkinci"})
    check("Dönemde tek açık oturum", code == 409, code)
    code, d = api("admin", "GET", f"{P}/calibration/sessions/{SID}")
    ay = next((i for i in d.get("items", []) if i["employeeId"] == AYSE), {}) if code == 200 else {}
    check("Oturumda Ayşe başlangıç hücresi 9, onaysız", ay.get("cell") == 9 and ay.get("originalCell") == 9 and ay.get("confirmed") is False, ay)
    code, _ = api("admin", "POST", f"{P}/calibration/sessions/{SID}/move", {"employeeId": AYSE, "performanceBand": 2, "potentialBand": 3, "note": "kısa"})
    check("Karar notu olmadan taşınamaz", code == 400, code)
    code, mv = api("admin", "POST", f"{P}/calibration/sessions/{SID}/move",
                   {"employeeId": AYSE, "performanceBand": 2, "potentialBand": 3, "note": "TEST11P hedef tek ve kolaydı, örneklerle tartışıldı"})
    check("Sürükle-bırak taşıma (9 → 8) kaydedilir", code == 200 and mv["cell"] == 8 and mv["confirmed"] is False, (code, mv))
    code, _ = api("admin", "POST", f"{P}/calibration/sessions/{SID}/finalize")
    check("Onaysız satır varken sonuçlanmaz", code == 409, code)
    code, d = api("admin", "GET", f"{P}/calibration/sessions/{SID}")
    ids = [i["employeeId"] for i in d.get("items", []) if not i.get("isSelf")]
    code, c = api("admin", "POST", f"{P}/calibration/sessions/{SID}/confirm", {"employeeIds": ids, "confirmed": True})
    check("İK satırları onaylar", code == 200 and c["changed"] == len(ids), (code, c))
    code, f = api("admin", "POST", f"{P}/calibration/sessions/{SID}/finalize")
    check("Tüm satırlar onaylıyken oturum sonuçlanır", code == 200 and f["changed"] >= 1, (code, f))
    code, grid = api("admin", "GET", f"{P}/nine-box?cycleId={CID}")
    gay = next((e for cc in grid.get("cells", []) for e in cc["employees"] if e["employeeId"] == AYSE), {}) if code == 200 else {}
    check("9-kutu kalibrasyon sonucunu gösterir (hücre 8, gerekçeli)", gay.get("cell") == 8 and (gay.get("override") or {}).get("reason", "").startswith("Kalibrasyon oturumu «TEST11P"), gay)
    code, _ = api("admin", "POST", f"{P}/calibration/sessions/{SID}/move", {"employeeId": AYSE, "performanceBand": 3, "potentialBand": 3, "note": "TEST11P sonuçlandıktan sonra"})
    check("Sonuçlanan oturum değiştirilemez", code == 409, code)
    code, d = api("admin", "GET", f"{P}/calibration/sessions/{SID}")
    acts = {x["action"] for x in d.get("changes", [])}
    check("Değişiklik günlüğü: taşıma, onay, sonuçlandırma", {"Moved", "Confirmed", "Finalized"} <= acts, acts)
    aud = psql(f"""SELECT string_agg(DISTINCT "Action", ',') FROM audit_log WHERE "EntityType" = 'CalibrationSession' AND "EntityId" = '{SID}'""")
    check("Denetim kaydı (oluşturma, taşıma, onay, sonuç, görüntüleme)",
          all(a in aud for a in ("Created", "CalibrationMoved", "CalibrationConfirmed", "CalibrationFinalized", "SensitiveViewed")), aud)

    # ================================================================== 80 OKR
    code, _ = api("ayse", "POST", f"{P}/okr/objectives", {"cycleId": CID, "level": "Company", "title": "TEST11P Şirket"})
    check("Çalışan amaç tanımlayamaz", code == 403, code)
    code, _ = api("mehmet", "POST", f"{P}/okr/objectives", {"cycleId": CID, "level": "Company", "title": "TEST11P Şirket"})
    check("Yönetici şirket amacı tanımlayamaz", code == 403, code)
    code, co = api("admin", "POST", f"{P}/okr/objectives", {"cycleId": CID, "level": "Company", "title": "TEST11P Şirket büyümesi", "weight": 100})
    check("İK şirket amacı tanımlar", code == 200, (code, co))
    code, de = api("mehmet", "POST", f"{P}/okr/objectives", {"cycleId": CID, "level": "Department", "departmentId": DEPT, "parentId": co.get("id"), "title": "TEST11P Mühendislik kalitesi"})
    check("Departman başı kendi departman amacını şirket amacına bağlar", code == 200 and de.get("parentId") == co.get("id"), (code, de))
    if OTHER_DEPT:
        code, _ = api("mehmet", "POST", f"{P}/okr/objectives", {"cycleId": CID, "level": "Department", "departmentId": OTHER_DEPT, "title": "TEST11P Başka"})
        check("Başka departmana amaç tanımlanamaz", code == 403, code)
    code, _ = api("admin", "POST", f"{P}/okr/objectives", {"cycleId": CID, "level": "Department", "departmentId": DEPT, "parentId": de.get("id"), "title": "TEST11P Döngü"})
    check("Departman amacı departman amacına bağlanamaz", code == 400, code)
    g1 = psql(f"""INSERT INTO performance_goals ("Id","TenantSlug","CycleId","EmployeeId","Title","Weight","TargetValue","CurrentValue","Status","CreatedAt")
                  VALUES (gen_random_uuid(),'demo','{CID}','{AYSE}','TEST11P Ayşe hedefi',100,10,8,'Active',now()) RETURNING "Id" """).splitlines()[0]
    g2 = psql(f"""INSERT INTO performance_goals ("Id","TenantSlug","CycleId","EmployeeId","Title","Weight","Status","CreatedAt")
                  VALUES (gen_random_uuid(),'demo','{CID}','{MEHMET}','TEST11P Mehmet hedefi',100,'Achieved',now()) RETURNING "Id" """).splitlines()[0]
    code, _ = api("ayse", "PUT", f"{P}/okr/goals/{g1}/parent", {"parentObjectiveId": de.get("id")})
    check("Çalışan hedef bağlayamaz", code == 403, code)
    c1, _ = api("mehmet", "PUT", f"{P}/okr/goals/{g1}/parent", {"parentObjectiveId": de.get("id")})
    c2, _ = api("mehmet", "PUT", f"{P}/okr/goals/{g2}/parent", {"parentObjectiveId": de.get("id")})
    check("Yönetici kişisel hedefleri departman amacına bağlar", c1 == 200 and c2 == 200, (c1, c2))
    code, t = api("mehmet", "GET", f"{P}/okr/tree?cycleId={CID}")
    root = next((r for r in t.get("roots", []) if r["id"] == co.get("id")), {}) if code == 200 else {}
    dnode = next((c for c in root.get("children", []) if c["id"] == de.get("id")), {})
    check("İlerleme yukarı toplanır: (80 + 100) / 2 = 90", dnode.get("progress") == 90 and root.get("progress") == 90 and len(dnode.get("children", [])) == 2, (root, dnode))
    code, t = api("ayse", "GET", f"{P}/okr/tree?cycleId={CID}")
    root = next((r for r in t.get("roots", []) if r["id"] == co.get("id")), {}) if code == 200 else {}
    dnode = next((c for c in root.get("children", []) if c["id"] == de.get("id")), {})
    kids = dnode.get("children", [])
    check("Çalışan yalnızca kendi hedefini görür, diğerleri sayı olarak", len(kids) == 1 and kids[0]["id"] == g1 and dnode.get("hiddenGoals") == 1, dnode)
    check("Küçük grup ilerlemesi çalışana gizli (ve üst amaca yayılır)",
          dnode.get("progressHidden") is True and dnode.get("progress") is None and root.get("progressHidden") is True, (root.get("progress"), dnode.get("progress")))

    # ================================================================== 81 anonim 360
    subject = OTHERS[0]
    reviewers = [AYSE, MEHMET, ELIF] + OTHERS[1:4]
    body = {"subjectEmployeeId": subject, "title": "TEST11P 360", "reviewers": [{"employeeId": r, "relationship": "Peer"} for r in reviewers]}
    code, _ = api("ayse", "POST", f"{P}/feedback360", body)
    check("Çalışan 360 talebi açamaz", code == 403, code)
    code, _ = api("admin", "POST", f"{P}/feedback360", {**body, "reviewers": body["reviewers"][:4]})
    check("5'ten az değerlendirenle talep açılamaz", code == 400, code)
    code, _ = api("admin", "POST", f"{P}/feedback360", {**body, "reviewers": body["reviewers"] + [{"employeeId": subject, "relationship": "Peer"}]})
    check("Kişi kendini değerlendiremez", code == 400, code)
    code, rq = api("admin", "POST", f"{P}/feedback360", body)
    check("İK 360 talebi açar", code == 200 and rq["reviewers"] == len(reviewers), (code, rq))
    RID = rq.get("id") if isinstance(rq, dict) else None
    code, mine = api("ayse", "GET", f"{P}/feedback360/mine")
    check("Davet değerlendirenin listesinde", code == 200 and any(x["id"] == RID and x["submitted"] is False for x in mine), (code, mine))
    full = {"communication": 4, "collaboration": 5, "ownership": 3}
    c1, _ = api("ayse", "POST", f"{P}/feedback360/{RID}/respond", {"ratings": full, "comment": "TEST11P açık iletişim"})
    c2, _ = api("mehmet", "POST", f"{P}/feedback360/{RID}/respond", {"ratings": {"communication": 5}})
    c3, _ = api("ik", "POST", f"{P}/feedback360/{RID}/respond", {"ratings": {"communication": 3, "ownership": 4}})
    check("Davetliler yanıtlar", (c1, c2, c3) == (200, 200, 200), (c1, c2, c3))
    code, _ = api("ayse", "POST", f"{P}/feedback360/{RID}/respond", {"ratings": full})
    check("İkinci yanıt reddedilir", code == 409, code)
    code, _ = api("admin", "POST", f"{P}/feedback360/{RID}/respond", {"ratings": full})
    check("Davetli olmayan yanıtlayamaz", code in (403, 409) and code != 200, code)
    code, _ = api("ayse", "POST", f"{P}/feedback360/{RID}/respond", {"ratings": {"communication": 9}})
    check("Geçersiz puan reddedilir", code in (400, 409), code)
    cols = psql("""SELECT string_agg(column_name, ',') FROM information_schema.columns WHERE table_name = 'performance_f360_responses'""")
    check("Yanıt tablosunda değerlendiren kimliği/zamanı yok", not any(w in cols for w in ("Reviewer", "Employee", "At", "Relationship")), cols)
    leak = psql(f"""SELECT count(*) FROM audit_log WHERE "EntityType" IN ('F360Response') OR ("Changes"::text LIKE '%TEST11P açık iletişim%')""")
    check("Yanıt içeriği denetim kaydına düşmez", leak == "0", leak)
    code, r = api("admin", "GET", f"{P}/feedback360/{RID}/results")
    check("Açık talepte sonuç gizli", code == 200 and r["hidden"] is True and r["competencies"] == [], (code, r))
    code, _ = api("ayse", "GET", f"{P}/feedback360/{RID}/results")
    check("Değerlendiren sonuçları göremez", code == 403, code)
    code, _ = api("admin", "POST", f"{P}/feedback360/{RID}/close")
    check("Talep kapanır", code == 200, code)
    code, r = api("admin", "GET", f"{P}/feedback360/{RID}/results")
    check("3 yanıtla (<5) sonuç gizli", code == 200 and r["hidden"] is True and r["responses"] == 3, (code, r))
    # Gerçek hesabı olmayan iki değerlendirenin yanıtı (sahte, doğrudan veritabanına).
    psql(f"""INSERT INTO performance_f360_responses ("Id","TenantSlug","RequestId","RatingsJson","Comment") VALUES
             (gen_random_uuid(),'demo','{RID}','{{"communication":4,"collaboration":4}}','TEST11P birlikte çalışması kolay'),
             (gen_random_uuid(),'demo','{RID}','{{"communication":2}}',NULL);""")
    code, r = api("admin", "GET", f"{P}/feedback360/{RID}/results")
    comps = {c["key"]: c for c in r.get("competencies", [])} if code == 200 else {}
    check("5 yanıtla sonuç görünür (iletişim ort. 3,6)", code == 200 and r["hidden"] is False and comps.get("communication", {}).get("average") == 3.6, (code, r))
    check("Yetkinlik başına 5'ten az puan gizli", comps.get("ownership", {}).get("hidden") is True and comps["ownership"]["average"] is None, comps.get("ownership"))
    check("Yorumlar metin sırasında", r.get("comments") == sorted(r.get("comments", [])) and len(r.get("comments", [])) == 2, r.get("comments"))
    code, _ = api("mehmet", "POST", f"{P}/feedback360/{RID}/release", {"released": True})
    check("Sonuç paylaşımı yalnızca İK", code == 403, code)
    code, rel = api("admin", "POST", f"{P}/feedback360/{RID}/release", {"released": True})
    check("İK sonucu kişiyle paylaşır", code == 200 and rel["releasedToSubject"] is True, (code, rel))
    viewed = psql(f"""SELECT count(*) FROM audit_log WHERE "EntityType" = 'Feedback360' AND "EntityId" = '{RID}' AND "Action" = 'SensitiveViewed'""")
    check("Sonuç görüntüleme denetim kaydında", viewed.isdigit() and int(viewed) >= 1, viewed)
    code, det = api("admin", "GET", f"{P}/feedback360/{RID}")
    sub = sum(1 for p in det.get("participants", []) if p["submitted"]) if code == 200 else -1
    check("Katılım listesi yalnızca yanıtladı/yanıtlamadı", code == 200 and sub == 3 and all(set(p) == {"reviewerEmployeeId", "name", "relationship", "submitted"} for p in det["participants"]), (code, det))
finally:
    cleanup()

print(f"FAILS: {len(FAIL)}")
for f in FAIL:
    print("  -", f)
sys.exit(1 if FAIL else 0)

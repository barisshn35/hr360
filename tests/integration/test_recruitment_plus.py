#!/usr/bin/env python3
"""İşe alım+ (Dalga 5c): kariyer sayfası ve aday öz-hizmeti (Y16), tekrar aday tespiti ve kanban (G13),
mülakat puan kartı ve planlama (Y17), teklif mektubu ve onayı (Y18).

Ön koşul: HR360 çalışıyor, recruitment-service deploy/testing/chat-mock.yml katmanıyla açık
(RECRUITMENT_PUBLIC_RATE_LIMIT=25 / 30 sn), workflow- ve notification-service güncel.
"""
import datetime as dt
import hashlib
import os
import random
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(__file__))
from common import AYSE, FAIL, api, check, form_token, http  # noqa: E402

R = "/api/recruitment"
P = f"{R}/public/demo"
W = "/api/workflow/workflows"
ROOT = os.path.join(os.path.dirname(__file__), "..", "..")
FAIL.clear()
RND = random.randint(10000, 99999)
SALARY = 87654.32
START = dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%d %H:%M:%S+00")


def psql(sql):
    out = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", "hr360_operational", "-Atc", sql],
                         capture_output=True, text=True, cwd=ROOT)
    return (out.stdout + out.stderr).strip()


def pub(method, path, body=None):
    return http(method, P + path, body)


def apply_pub(path, body):
    """Başvuru: önce form jetonu alınır (bot koruması, en erken 3 sn sonra geçerli), sonra gönderilir."""
    return pub("POST", path, {**body, "formToken": form_token(P + "/form-token")})


def email(tag):
    return f"test5c-{tag}-{RND}@example.com"


def cleanup():
    psql("""DELETE FROM recruitment_candidates c WHERE EXISTS (
                SELECT 1 FROM recruitment_applications a JOIN recruitment_job_postings p ON p."Id" = a."JobPostingId"
                WHERE a."CandidateId" = c."Id" AND p."Title" LIKE 'TEST5c%');
            DELETE FROM recruitment_candidates WHERE "Email" LIKE 'test5c-%@example.com';
            DELETE FROM recruitment_job_postings WHERE "Title" LIKE 'TEST5c%';
            DELETE FROM workflow_approval_steps WHERE "WorkflowRequestId" IN (SELECT "Id" FROM workflow_requests WHERE "Subject" LIKE 'İş teklifi onayı: TEST5c%');
            DELETE FROM workflow_requests WHERE "Subject" LIKE 'İş teklifi onayı: TEST5c%';
            DELETE FROM notification_messages WHERE "Body" LIKE '%TEST5c%' OR "Subject" LIKE '%TEST5c%' OR "RecipientEmail" LIKE 'test5c-%';""")
    psql(f"""DELETE FROM notification_messages WHERE "CreatedAt" >= '{START}' AND "TemplateCode" IN ('workflow.submitted','workflow.approved')
                AND "Body" LIKE '%teklif%';
             DELETE FROM governance_destruction_logs WHERE "Category" = 'RecruitmentCandidates' AND "RanAt" >= '{START}';""")


cleanup()
MEHMET, DEPT = psql("""SELECT "HeadEmployeeId" || '|' || "Id" FROM organization_departments
                       WHERE "TenantSlug" = 'demo' AND "HeadEmployeeId" IS NOT NULL ORDER BY "CreatedAt" LIMIT 1""").split("|")

# ---------------------------------------------------------------- hazırlık: ilanlar
postings = {}
for key, title, dept in [("main", "TEST5c Backend Geliştirici", DEPT), ("nohead", "TEST5c Stajyer", "00000000-0000-4000-8000-00000000c5c5"),
                         ("draft", "TEST5c Taslak", DEPT)]:
    code, p = api("admin", "POST", f"{R}/job-postings", {"title": title, "departmentId": dept, "description": "Açıklama", "employmentType": "FullTime", "headcount": 2})
    postings[key] = p["id"] if code == 201 else None
    if key != "draft":
        api("admin", "POST", f"{R}/job-postings/{p['id']}/publish")
check("Hazırlık: ilanlar", all(postings.values()), postings)
MAIN, NOHEAD, DRAFT = postings["main"], postings["nohead"], postings["draft"]

# ====================================================================== Y16 kariyer sayfası
code, jobs = pub("GET", "/jobs")
ids = [j["id"] for j in (jobs or {}).get("jobs", [])] if code == 200 else []
check("Kariyer sayfası oturumsuz açılır, yalnızca yayındaki ilanlar", code == 200 and MAIN in ids and NOHEAD in ids and DRAFT not in ids, (code, ids[:5]))
job = next((j for j in jobs["jobs"] if j["id"] == MAIN), {}) if code == 200 else {}
check("İlanda iç alanlar yok", job and not ({"headcount", "departmentId", "applications", "status"} & set(job.keys())) and job.get("department"), job)
check("Aydınlatma metni bilgi olarak döner (saklama süresi + havuz rızası)",
      code == 200 and "AÇIK RIZA" in jobs["privacyNotice"]["text"] and str(jobs["retentionDays"]) in jobs["privacyNotice"]["text"], jobs.get("privacyNotice") if code == 200 else code)
code, _ = http("GET", f"{R}/public/yok-boyle-sirket/jobs")
check("Bilinmeyen kiracı 404", code == 404, code)
code, _ = pub("POST", f"/jobs/{DRAFT}/apply", {"firstName": "Taslak", "lastName": "Aday", "email": email("draft")})
check("Taslak ilana başvuru yapılamaz", code == 404, code)
code, _ = pub("POST", f"/jobs/{MAIN}/apply", {"firstName": "A", "lastName": "B", "email": "gecersiz"})
check("Geçersiz başvuru reddedilir", code == 400, code)

# Güvenlik dalgası 2B: herkese açık başvuru formu imzalı zaman jetonu ister (tek kullanımlık, en erken 3 sn).
code, _ = pub("POST", f"/jobs/{MAIN}/apply", {"firstName": "Jeton", "lastName": "Yok", "email": email("notoken")})
check("Bot koruması: jetonsuz başvuru reddedilir", code == 400, code)
code, r = pub("POST", f"/jobs/{MAIN}/apply", {"firstName": "Hızlı", "lastName": "Bot", "email": email("fast"), "formToken": form_token(P + "/form-token", wait=False)})
check("Bot koruması: 3 sn dolmadan gönderilen form reddedilir", code == 400 and r.get("code") == "form_too_fast", (code, r))
code, r = pub("POST", f"/jobs/{MAIN}/apply", {"firstName": "Sahte", "lastName": "Jeton", "email": email("forged"), "formToken": "1.000000000000000000000000.x"})
check("Bot koruması: sahte jeton reddedilir", code == 400 and r.get("code") == "form_token", (code, r))
n_bot = psql(f"""SELECT count(*) FROM recruitment_candidates WHERE "Email" IN ('{email("notoken")}','{email("fast")}','{email("forged")}')""")
check("Reddedilen bot başvuruları kaydedilmez", n_bot == "0", n_bot)

A_EMAIL = email("a")
code, ra = apply_pub(f"/jobs/{MAIN}/apply", {"firstName": "Gülşen", "lastName": "Testçi", "email": A_EMAIL, "phone": "0532 555 12 34",
                                              "coverNote": "TEST5c ön yazı", "resumeText": "Python, SQL", "privacyNoticeVersion": jobs["privacyNotice"]["version"]})
check("Oturumsuz başvuru (rızasız) alınır ve öz-hizmet jetonu döner", code == 200 and ra.get("token") and ra["linkedToExisting"] is False and ra["talentPoolConsent"] is False, (code, ra))
TOK_A, APP_A = ra["token"], ra["applicationId"]
row = psql(f"""SELECT c."TalentPoolConsent", c."TalentPoolConsentAt" IS NULL, a."SelfServiceTokenHash", a."OwnsCandidate", a."Channel", c."Id"
               FROM recruitment_applications a JOIN recruitment_candidates c ON c."Id" = a."CandidateId" WHERE a."Id" = '{APP_A}'""").split("|")
CAND_A = row[5]
check("Rıza verilmedi: havuz rızası yok (aydınlatma ≠ rıza)", row[0] == "f" and row[1] == "t", row)
check("Jetonun yalnızca SHA-256 özeti saklanır", row[2] == hashlib.sha256(TOK_A.encode()).hexdigest() and row[3] == "t" and row[4] == "Career", row)
leak = psql(f"""SELECT count(*) FROM notification_messages WHERE "Body" LIKE '%{TOK_A}%' OR "ActionUrl" LIKE '%{TOK_A}%'""")
mail = psql(f"""SELECT count(*) FROM notification_messages WHERE "RecipientEmail" = '{A_EMAIL}' AND "Channel" = 'Email' AND "TemplateCode" = 'recruitment.applied'""")
check("Onay e-postası adaya gider, jeton bildirimlerde saklanmaz", leak == "0" and mail == "1", (leak, mail))

B_EMAIL = email("b")
code, rb = apply_pub(f"/jobs/{MAIN}/apply", {"firstName": "Bora", "lastName": "Havuzcu", "email": B_EMAIL, "talentPoolConsent": True})
TOK_B = rb.get("token") if code == 200 else None
row = psql(f"""SELECT c."TalentPoolConsent", c."TalentPoolConsentAt" IS NOT NULL, c."Id" FROM recruitment_applications a
               JOIN recruitment_candidates c ON c."Id" = a."CandidateId" WHERE a."Id" = '{rb.get('applicationId')}'""").split("|")
CAND_B = row[-1]
check("Ayrı açık rıza kutusu işaretlenirse havuz rızası kaydedilir", code == 200 and row[0] == "t" and row[1] == "t", (code, row))

code, ss = pub("GET", f"/self-service/{TOK_A}")
check("Öz-hizmet: durum ve tutulan veriler görülür", code == 200 and ss["status"] == "Received" and ss["data"]["email"] == A_EMAIL
      and ss["data"]["coverNote"] == "TEST5c ön yazı" and "180" in ss["retention"], (code, ss))
code, _ = pub("GET", "/self-service/" + "x" * 43)
check("Geçersiz jeton 404", code == 404, code)

# ====================================================================== G13 tekrar aday
code, rd = apply_pub(f"/jobs/{MAIN}/apply", {"firstName": "Gülşen", "lastName": "Testçi", "email": A_EMAIL.upper()})
check("Aynı ilana ikinci başvuru 409", code == 409, (code, rd))
code, rl = apply_pub(f"/jobs/{NOHEAD}/apply", {"firstName": "Gulsen", "lastName": "Testci", "email": A_EMAIL.replace("@", "+cv@").upper()})
check("Normalize e-posta eşleşmesi mevcut adaya bağlanır", code == 200 and rl["linkedToExisting"] is True, (code, rl))
TOK_L = rl.get("token")
n = psql(f"""SELECT count(*) FROM recruitment_candidates WHERE "NormalizedEmail" = '{A_EMAIL}'""")
cid = psql(f"""SELECT "CandidateId" FROM recruitment_applications WHERE "Id" = '{rl.get('applicationId')}'""")
check("Yeni aday açılmaz; başvuru mevcut adaya bağlanır", n == "1" and cid == CAND_A, (n, cid, CAND_A))
code, ssl = pub("GET", f"/self-service/{TOK_L}")
check("Bağlanan başvurunun bağlantısı mevcut kaydı maskeler", code == 200 and ssl["ownsCandidate"] is False and ssl["data"]["email"] != A_EMAIL
      and "*" in ssl["data"]["email"] and ssl["data"]["resumeText"] is None, (code, ssl.get("data") if code == 200 else ssl))

# Farklı e-posta, aynı telefon + bulanık ad → aynı aday sayılır: A zaten bu ilana başvurduğu için 409.
code, rp = apply_pub(f"/jobs/{MAIN}/apply", {"firstName": "Gulşen", "lastName": "TESTÇİ", "email": email("a2"), "phone": "+90 (532) 555-1234"})
n2 = psql(f"""SELECT count(*) FROM recruitment_candidates WHERE "NormalizedEmail" = '{email("a2")}'""")
check("Telefon + benzer ad (bulanık) eşleşmesi mevcut adaya bağlanır", code == 409 and "daha önce" in rp.get("message", "") and n2 == "0", (code, rp, n2))

code, dup = api("admin", "POST", f"{R}/candidates", {"firstName": "Başka", "lastName": "Kişi", "email": A_EMAIL})
check("Elle ekleme: aynı e-posta 409 + mevcut aday kimliği (zorlanamaz)", code == 409 and dup.get("existingCandidateId") == CAND_A and dup.get("canForce") is False, (code, dup))
code, dup = api("admin", "POST", f"{R}/candidates", {"firstName": "Farklı", "lastName": "Kişi", "email": email("m1"), "phone": "05325551234"})
check("Elle ekleme: aynı telefon farklı ad uyarı (409, zorlanabilir)", code == 409 and dup.get("existingCandidateId") == CAND_A and dup.get("canForce") is True, (code, dup))
code, forced = api("admin", "POST", f"{R}/candidates", {"firstName": "Farklı", "lastName": "Kişi", "email": email("m1"), "phone": "05325551234", "force": True, "skills": ["SQL", "Go"]})
check("İK 'yine de kaydet' ile oluşturabilir", code == 201 and forced["skills"] == ["SQL", "Go"], (code, forced))
code, chk = api("admin", "GET", f"{R}/candidates/duplicates?email={A_EMAIL}")
check("Canlı tekrar kontrolü", code == 200 and chk["duplicate"] and chk["candidate"]["id"] == CAND_A, (code, chk))

# ---------------------------------------------------------------- öz-hizmet rıza
code, _ = pub("POST", f"/self-service/{TOK_L}/consent", {"talentPool": True})
check("Bağlanan başvuru bağlantısıyla havuz rızası verilemez", code == 403, code)
code, c1 = pub("POST", f"/self-service/{TOK_A}/consent", {"talentPool": True})
code2, _ = pub("POST", f"/self-service/{TOK_L}/consent", {"talentPool": False})
cons = psql(f"""SELECT "TalentPoolConsent" FROM recruitment_candidates WHERE "Id" = '{CAND_A}'""")
check("Rıza kaydın sahibi bağlantıyla verilir, her bağlantıyla geri alınabilir", code == 200 and code2 == 200 and cons == "f", (code, code2, cons))

# ====================================================================== G13 kanban
code, pipe = api("admin", "GET", f"{R}/applications/pipeline?jobPostingId={MAIN}")
stages = [s["key"] for s in pipe.get("stages", [])] if code == 200 else []
card = next((c for c in pipe.get("cards", []) if c["id"] == APP_A), None) if code == 200 else None
check("Kanban: sıralı aşamalar ve kartlar", stages[:5] == ["Applied", "Screening", "Interview", "Offer", "Hired"] and card and card["status"] == "Applied"
      and card["channel"] == "Career", (code, stages, card))
code, mv = api("admin", "POST", f"{R}/applications/{APP_A}/move", {"status": "Screening"})
check("Kanban: kart taşınır", code == 200 and mv["status"] == "Screening", (code, mv))
code, mv = api("admin", "POST", f"{R}/applications/{APP_A}/move", {"status": "Hired"})
check("Kanban: teklifsiz işe alım engellenir", code == 400 and "teklif" in mv.get("message", ""), (code, mv))
code, _ = api("ayse", "GET", f"{R}/applications/pipeline?jobPostingId={MAIN}")
check("Çalışan panoyu göremez", code == 403, code)

# ====================================================================== Y17 puan kartı + planlama
code, tpl = api("admin", "PUT", f"{R}/job-postings/{MAIN}/scorecard-template",
                {"criteria": [{"key": "tech", "label": "Teknik", "weight": 3}, {"key": "comm", "label": "İletişim", "weight": 1}]})
check("İlan puan kartı şablonu", code == 200 and len(tpl["criteria"]) == 2, (code, tpl))
code, bad = api("admin", "PUT", f"{R}/job-postings/{MAIN}/scorecard-template", {"criteria": [{"key": "x", "label": "X", "weight": 9}]})
check("Ağırlık 1-5 dışı reddedilir", code == 400, code)

day = dt.date(2031, 1, 1) + dt.timedelta(days=RND % 300)
T = dt.datetime(day.year, day.month, day.day, 7, 0, tzinfo=dt.timezone.utc)
iso = lambda d: d.isoformat().replace("+00:00", "Z")  # noqa: E731
code, iv = api("admin", "POST", f"{R}/applications/{APP_A}/interviews",
               {"type": "Technical", "scheduledAt": iso(T), "interviewerEmployeeIds": [AYSE], "durationMinutes": 60,
                "meetingUrl": "https://meet.example.com/test5c", "notifyCandidate": True})
IV = iv.get("id") if code == 201 else None
check("Mülakat planlanır, kopyalanabilir davet metni döner", code == 201 and "meet.example.com" in iv["invitationText"] and iv["candidateNotified"], (code, iv))
inapp = psql(f"""SELECT count(*) FROM notification_messages WHERE "RecipientEmployeeId" = '{AYSE}' AND "TemplateCode" = 'recruitment.interview.scheduled'
                 AND "Channel" = 'InApp' AND "Body" LIKE '%TEST5c%' AND "Body" NOT LIKE '%Gülşen%'""")
cmail = psql(f"""SELECT count(*) FROM notification_messages WHERE "RecipientEmail" = '{A_EMAIL}' AND "Channel" = 'Email' AND "TemplateCode" = 'recruitment.interview.invite'""")
check("Görüşmeciye uygulama içi (aday adı yok), adaya e-posta davet", inapp == "1" and cmail == "1", (inapp, cmail))
code, cf = api("admin", "POST", f"{R}/applications/{rb['applicationId']}/interviews",
               {"type": "HR", "scheduledAt": iso(T + dt.timedelta(minutes=30)), "interviewerEmployeeIds": [AYSE], "durationMinutes": 45})
check("Görüşmeci çakışması 409", code == 409 and cf.get("code") == "interviewer_busy", (code, cf))
code, ok2 = api("admin", "POST", f"{R}/applications/{rb['applicationId']}/interviews",
                {"type": "HR", "scheduledAt": iso(T + dt.timedelta(minutes=60)), "interviewerEmployeeIds": [AYSE], "durationMinutes": 30})
check("Uç uca mülakat çakışma sayılmaz", code == 201, (code, ok2))

code, mine = api("ayse", "GET", f"{R}/interviews/mine")
check("Görüşmeci kendi mülakatlarını görür", code == 200 and any(m["id"] == IV for m in mine), (code, mine if code != 200 else len(mine)))
code, form = api("ayse", "GET", f"{R}/interviews/{IV}/scorecard-form")
check("Puan kartı formu şablonu ve uyarı ipucunu getirir", code == 200 and form["canSubmit"] and len(form["criteria"]) == 2 and "özel nitelikli" in form["hint"], (code, form))
code, sc = api("ayse", "POST", f"{R}/interviews/{IV}/scorecard",
               {"scores": [{"key": "tech", "score": 5}, {"key": "comm", "score": 2}], "recommendation": "Yes",
                "notes": "Teknik olarak güçlü; görüşmede hamile olduğunu söyledi."})
cats = [w["category"] for w in sc.get("warnings", [])] if code == 200 else []
check("Ağırlıklı puan (3×5+1×2)/4 = 4.25", code == 200 and float(sc["overallScore"]) == 4.25, (code, sc))
check("Özel nitelikli veri uyarısı (kaydı engellemez)", code == 200 and "Hamilelik" in cats, (code, cats))
code, sc2 = api("ayse", "POST", f"{R}/interviews/{IV}/scorecard", {"scores": [{"key": "tech", "score": 7}]})
check("1-5 dışı puan reddedilir", code == 400, code)
code, _ = api("mehmet", "POST", f"{R}/interviews/{IV}/scorecard", {"scores": [{"key": "tech", "score": 3}]})
check("Görüşmeci olmayan puan kartı dolduramaz", code == 403, code)
code, nc = api("ayse", "POST", f"{R}/interviews/notes-check", {"text": "Evli ve 2 çocuğu var, 45 yaşında"})
code2, nc2 = api("ayse", "POST", f"{R}/interviews/notes-check", {"text": "Dinamik, dinleme becerisi yüksek; İstanbul'da yaşıyor."})
check("Canlı not denetimi", code == 200 and {"Medeni hal", "Çocuk / aile planı", "Yaş"} <= {w["category"] for w in nc["warnings"]}
      and code2 == 200 and nc2["warnings"] == [], (nc, nc2))
code, all_sc = api("admin", "GET", f"{R}/interviews/{IV}/scorecards")
check("İK tüm puan kartlarını ve ortalamayı görür", code == 200 and float(all_sc["average"]) == 4.25 and all_sc["scorecards"][0]["interviewer"], (code, all_sc))
code, _ = api("ayse", "GET", f"{R}/interviews/{IV}/scorecards")
check("Görüşmeci diğer puan kartlarını göremez", code == 403, code)
code, pipe = api("admin", "GET", f"{R}/applications/pipeline?jobPostingId={MAIN}")
card = next((c for c in pipe["cards"] if c["id"] == APP_A), {}) if code == 200 else {}
check("Kanban kartında ortalama puan", card.get("averageScore") == 4.25 and card.get("status") == "Interview", card)

# ====================================================================== Y18 teklif
code, tp = api("admin", "GET", f"{R}/offers/template")
check("Varsayılan teklif şablonu", code == 200 and "{brutMaas}" in tp["body"], code)
code, badt = api("admin", "PUT", f"{R}/offers/template", {"body": "Merhaba {bilinmeyen}"})
check("Bilinmeyen yer tutucu reddedilir", code == 400, code)
start = (dt.date.today() + dt.timedelta(days=30)).isoformat()
exp = (dt.date.today() + dt.timedelta(days=10)).isoformat()
offer_body = {"applicationId": APP_A, "positionTitle": "TEST5c Backend Geliştirici", "grossSalary": SALARY, "currency": "TRY",
              "startDate": start, "benefits": "Yemek kartı, özel sağlık sigortası", "expiresAt": exp}
code, pv = api("admin", "POST", f"{R}/offers/preview", offer_body)
check("Mektup önizlemesi şablondan üretilir", code == 200 and "87.654,32 TL" in pv["letterText"] and "Gülşen Testçi" in pv["letterText"], (code, pv))
code, cr = api("admin", "POST", f"{R}/offers", offer_body)
OFFER = cr["offer"]["id"] if code == 200 else None
WF = cr["offer"]["workflowRequestId"] if code == 200 else None
check("Teklif onay akışına (işe alım yöneticisi) gider", code == 200 and WF and cr["offer"]["approverEmployeeId"] == MEHMET
      and cr["offer"]["status"] == "PendingApproval" and not cr["hrDecides"], (code, cr))
code, _ = api("admin", "POST", f"{R}/offers", offer_body)
check("Açık teklif varken ikincisi 409", code == 409, code)
st = psql(f"""SELECT "Status" FROM recruitment_applications WHERE "Id" = '{APP_A}'""")
check("Başvuru Teklif aşamasına geçer", st == "Offer", st)
code, sent = api("admin", "POST", f"{R}/offers/{OFFER}/send")
check("Onaylanmamış teklif gönderilemez", code == 409, (code, sent))
code, _ = api("ayse", "GET", f"{R}/offers/{OFFER}")
check("Çalışan teklifi (ücreti) göremez", code == 403, code)
code, mo = api("mehmet", "GET", f"{R}/offers/{OFFER}")
check("Onaycı ücreti görür (denetlenir)", code == 200 and float(mo["grossSalary"]) == SALARY, (code, mo))
aud = psql(f"""SELECT count(*) FROM audit_log WHERE "EntityType" = 'Offer' AND "EntityId" = '{OFFER}' AND "Action" = 'SensitiveViewed'""")
check("Ücret görüntülemesi denetim kaydına yazılır", int(aud or 0) >= 1, aud)
code, wf = api("mehmet", "GET", f"{W}/{WF}")
check("Akış türü OfferApproval, konu ücretsiz", code == 200 and wf["type"] == "OfferApproval" and "87" not in (wf["subject"] or ""), (code, wf))
step = wf["steps"][0]["id"] if code == 200 else None
code, dec = api("mehmet", "POST", f"{W}/{WF}/steps/{step}/decide", {"decision": "Approved", "comment": "Uygun"})
check("Mehmet (departman başı) onaylar", code == 200, (code, dec))
status = None
for _ in range(40):
    code, o = api("admin", "GET", f"{R}/offers/{OFFER}")
    status = o.get("status") if code == 200 else None
    if status == "Approved":
        break
    time.sleep(1)
check("Workflow kararı (Kafka) teklife işlenir", status == "Approved" and o.get("decisionNote") == "Uygun", (status, o))
leaks = psql(f"""SELECT count(*) FROM notification_messages WHERE "CreatedAt" >= '{START}'
                 AND ("Body" LIKE '%87654%' OR "Body" LIKE '%87.654%' OR "Subject" LIKE '%87654%' OR "Subject" LIKE '%87.654%')""")
wfn = psql(f"""SELECT count(*) FROM notification_messages WHERE "CreatedAt" >= '{START}' AND "TemplateCode" = 'workflow.submitted' AND "Body" LIKE '%teklif%'""")
check("Ücret hiçbir bildirim metninde yok (onaycı bildirimi 'teklif' etiketli)", leaks == "0" and int(wfn or 0) >= 1, (leaks, wfn))

code, sent = api("admin", "POST", f"{R}/offers/{OFFER}/send")
check("Onaylı teklif adaya gönderilir", code == 200 and sent["status"] == "Sent", (code, sent))
code, ss = pub("GET", f"/self-service/{TOK_A}")
check("Aday teklif mektubunu öz-hizmette görür", code == 200 and ss["offer"] and ss["offer"]["status"] == "Sent" and "87.654,32" in ss["offer"]["letterText"], (code, ss.get("offer")))
code, rs = pub("POST", f"/self-service/{TOK_A}/offer/respond", {"accept": True})
st = psql(f"""SELECT "Status" FROM recruitment_applications WHERE "Id" = '{APP_A}'""")
check("Aday kabul eder → başvuru İşe alındı", code == 200 and rs["status"] == "Accepted" and st == "Hired", (code, rs, st))
code, saga = api("admin", "GET", "/api/governance/sagas/offer-to-hire")
row = next((r for r in saga if r["applicationId"] == APP_A), None) if code == 200 else None
check("Teklif→işe alım sagası başvuruyu izler", row is not None and row["stage"] in ("AwaitingEmployee", "AwaitingOnboarding", "Completed"), (code, row))
code, _ = pub("DELETE", f"/self-service/{TOK_A}")
check("İşe alınan aday öz-hizmetten silinemez (409)", code == 409, code)

# İK yedek karar: onaycısı olmayan ilan
APP_L = rl["applicationId"]
code, cr2 = api("admin", "POST", f"{R}/offers", {**offer_body, "applicationId": APP_L, "positionTitle": "TEST5c Stajyer"})
OFFER2 = cr2["offer"]["id"] if code == 200 else None
check("Departman başı yoksa İK karar verir", code == 200 and cr2["hrDecides"] and cr2["offer"]["workflowRequestId"] is None, (code, cr2))
code, _ = api("admin", "POST", f"{R}/offers/{OFFER}/decide", {"approve": True})
check("Akıştaki teklife İK doğrudan karar veremez", code == 409, code)
code, d2 = api("admin", "POST", f"{R}/offers/{OFFER2}/decide", {"approve": True, "note": "TEST5c"})
check("İK doğrudan onaylar", code == 200 and d2["status"] == "Approved", (code, d2))
code, w2 = api("admin", "POST", f"{R}/offers/{OFFER2}/withdraw")
check("Teklif geri çekilir", code == 200 and w2["status"] == "Withdrawn", (code, w2))
code, lst = api("mehmet", "GET", f"{R}/offers?jobPostingId={NOHEAD}")
check("Onaycısı olmadığı teklifin ücretini yönetici göremez", code == 200 and lst and lst[0]["grossSalary"] is None and lst[0]["letterText"] is None, (code, lst))

# ====================================================================== Y16 silme (KVKK m.11)
code, d = pub("DELETE", f"/self-service/{TOK_B}")
gone = psql(f"""SELECT count(*) FROM recruitment_candidates WHERE "Id" = '{CAND_B}'""")
code2, _ = pub("GET", f"/self-service/{TOK_B}")
log = psql(f"""SELECT count(*) FROM governance_destruction_logs WHERE "Category" = 'RecruitmentCandidates' AND "Trigger" = 'DataSubjectRequest' AND "RanAt" >= '{START}'""")
check("Aday öz-hizmetten verisini siler (aday + başvurular)", code == 200 and d["scope"] == "candidate" and gone == "0" and code2 == 404 and int(log) >= 1, (code, d, gone, code2, log))
aud = psql(f"""SELECT count(*) FROM audit_log WHERE "EntityId" = '{CAND_B}' AND "Action" = 'Deleted' AND "Changes"::text LIKE '%{B_EMAIL}%'""")
check("Silme denetim kaydı kişisel veri içermez", aud == "0", aud)
code, d = pub("DELETE", f"/self-service/{TOK_L}")
still = psql(f"""SELECT count(*) FROM recruitment_candidates WHERE "Id" = '{CAND_A}'""")
check("Bağlanan başvurunun bağlantısı yalnızca o başvuruyu siler", code == 200 and d["scope"] == "application" and still == "1", (code, d, still))

# ====================================================================== Y16 saklama süresi
C_EMAIL, D_EMAIL = email("c"), email("d")
code, rc = apply_pub(f"/jobs/{NOHEAD}/apply", {"firstName": "Cemil", "lastName": "Eskiaday", "email": C_EMAIL, "coverNote": "TEST5c eski"})
code2, rdd = apply_pub(f"/jobs/{NOHEAD}/apply", {"firstName": "Deniz", "lastName": "Havuzda", "email": D_EMAIL, "talentPoolConsent": True})
psql(f"""UPDATE recruitment_applications SET "Status" = 'Rejected', "StatusChangedAt" = now() - interval '200 days'
         WHERE "Id" IN ('{rc.get('applicationId')}', '{rdd.get('applicationId')}')""")
code3, run = api("admin", "POST", f"{R}/retention/run")
c_row = psql(f"""SELECT c."FirstName", c."Email" LIKE 'anon-%', c."Phone" IS NULL, a."CoverNote" IS NULL, a."SelfServiceTokenHash" IS NULL, c."AnonymizedAt" IS NOT NULL
                 FROM recruitment_candidates c JOIN recruitment_applications a ON a."CandidateId" = c."Id" WHERE a."Id" = '{rc.get('applicationId')}'""")
d_row = psql(f"""SELECT c."FirstName" FROM recruitment_candidates c JOIN recruitment_applications a ON a."CandidateId" = c."Id" WHERE a."Id" = '{rdd.get('applicationId')}'""")
check("Rızasız aday kapanıştan 180 gün sonra anonimleştirilir", code == 200 and code3 == 200 and run["anonymized"] >= 1 and c_row == "Anonim|t|t|t|t|t", (code3, run, c_row))
check("Havuz rızalı aday 24 ay saklanır", code2 == 200 and d_row == "Deniz", d_row)
aud = psql(f"""SELECT count(*) FROM audit_log WHERE "Changes"::text LIKE '%{C_EMAIL}%' AND "Action" = 'Updated'""")
check("Anonimleştirme eski değerleri denetim kaydına yazmaz", aud == "0", aud)
code, _ = pub("GET", f"/self-service/{rc.get('token')}")
check("Anonimleştirilen adayın bağlantısı geçersizleşir", code == 404, code)

# ====================================================================== Y16 istek sınırı
time.sleep(31)
codes = [pub("POST", f"/jobs/{MAIN}/apply", {"firstName": "x"})[0] for _ in range(27)]
check("Herkese açık POST IP başına sınırlanır (429)", 429 in codes and codes[0] == 400, codes)
ipcol = psql("""SELECT count(*) FROM information_schema.columns WHERE table_name LIKE 'recruitment_%' AND lower(column_name) IN ('ip', 'ipaddress', 'clientip', 'remoteip', 'ipaddr')""")
check("İşe alım tablolarında IP sütunu yok", ipcol == "0", ipcol)

cleanup()
time.sleep(1)
print(f"FAILS: {len(FAIL)}")
for f in FAIL:
    print("  -", f)
sys.exit(1 if FAIL else 0)

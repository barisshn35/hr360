#!/usr/bin/env python3
"""İşe alım dalga 11: çalışan önerisi (73), aday durum bağlantısı (74), Google for Jobs JSON-LD (75),
kör puan kartı ve değerlendiriciler arası tutarlılık (77), huni analizi (78).

Ön koşul: HR360 çalışıyor, recruitment-service güncel ve deploy/testing/chat-mock.yml katmanıyla açık
(RECRUITMENT_STATUS_RATE_LIMIT=40 / 30 sn), scripts/sql/2026-10-24_recruitment_w11.sql uygulanmış.
Test verisi: ilan başlığı "TEST11r", aday e-postası "test11r-...@example.com" (cleanup_test_data.py).
"""
import datetime as dt
import os
import random
import re
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(__file__))
from common import AYSE, FAIL, api, check, form_token, http  # noqa: E402

R = "/api/recruitment"
P = f"{R}/public/demo"
ROOT = os.path.join(os.path.dirname(__file__), "..", "..")
FAIL.clear()
RND = random.randint(10000, 99999)
START = dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%d %H:%M:%S+00")


def psql(sql):
    out = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", "hr360_operational", "-Atc", sql],
                         capture_output=True, text=True, cwd=ROOT)
    return (out.stdout + out.stderr).strip()


def email(tag):
    return f"test11r-{tag}-{RND}@example.com"


def cleanup():
    psql("""DELETE FROM recruitment_candidates WHERE "Email" LIKE 'test11r-%@example.com';
            DELETE FROM recruitment_job_postings WHERE "Title" LIKE 'TEST11r%';
            DELETE FROM notification_messages WHERE "RecipientEmail" LIKE 'test11r-%' OR "Body" LIKE '%TEST11r%' OR "Subject" LIKE '%TEST11r%';""")
    psql(f"""DELETE FROM notification_messages WHERE "CreatedAt" >= '{START}' AND "TemplateCode" IN ('recruitment.referral.reward','recruitment.interview.invite');""")


cleanup()
HAD_SETTINGS = psql("""SELECT count(*) FROM recruitment_program_settings WHERE "TenantSlug" = 'demo'""") == "1"
code, ORIG = api("admin", "GET", f"{R}/referrals/settings")
check("Program ayarları okunur (varsayılan ya da kayıtlı)", code == 200 and "referralEnabled" in ORIG, (code, ORIG))
MEHMET, DEPT = psql("""SELECT "HeadEmployeeId" || '|' || "Id" FROM organization_departments
                       WHERE "TenantSlug" = 'demo' AND "HeadEmployeeId" IS NOT NULL ORDER BY "CreatedAt" LIMIT 1""").split("|")

code, post = api("admin", "POST", f"{R}/job-postings", {"title": "TEST11r Veri Mühendisi", "departmentId": DEPT,
                                                       "description": "Veri boru hatları.\n\nPython ve SQL.", "employmentType": "FullTime", "headcount": 1})
PID = post["id"] if code == 201 else None
api("admin", "POST", f"{R}/job-postings/{PID}/publish")
check("Hazırlık: ilan", PID is not None, (code, post))

# ====================================================================== 75 Google for Jobs
valid = (dt.date.today() + dt.timedelta(days=40)).isoformat() + "T23:59:59Z"
code, cd = api("admin", "PUT", f"{R}/job-postings/{PID}/career-details",
               {"location": "İzmir", "country": "TR", "remoteAllowed": True, "validThrough": valid,
                "salaryMin": 90000, "salaryMax": 130000, "salaryCurrency": "TRY", "salaryPeriod": "MONTH"})
check("İK ilan kariyer alanlarını kaydeder", code == 200 and cd["location"] == "İzmir", (code, cd))
code, bad = api("admin", "PUT", f"{R}/job-postings/{PID}/career-details", {"remoteAllowed": False, "salaryMin": 5, "salaryMax": 1})
check("Ücret üst < alt reddedilir", code == 400, code)
code, _ = api("ayse", "PUT", f"{R}/job-postings/{PID}/career-details", {"remoteAllowed": False})
check("Çalışan ilan alanlarını değiştiremez", code == 403, code)

settings = {**ORIG, "publishSalaryInJobPostings": False, "referralEnabled": True, "referralProbationDays": 60, "referralRewardAmount": 5000, "referralRewardCurrency": "TRY"}
code, _ = api("admin", "PUT", f"{R}/referrals/settings", settings)
check("Program ayarları kaydedilir", code == 200, code)
code, job = http("GET", f"{P}/jobs/{PID}")
ld = (job or {}).get("jsonLd") or {}
check("Herkese açık ilan + JobPosting JSON-LD (zorunlu alanlar)", code == 200 and ld.get("@type") == "JobPosting" and ld.get("title") == "TEST11r Veri Mühendisi"
      and ld.get("datePosted") and ld.get("validThrough", "").startswith(valid[:10]) and ld.get("employmentType") == "FULL_TIME"
      and ld["hiringOrganization"]["name"] and ld["jobLocation"]["address"]["addressLocality"] == "İzmir"
      and ld.get("jobLocationType") == "TELECOMMUTE" and "<p>" in ld.get("description", ""), (code, job))
check("Kiracı açmadıkça ücret yayımlanmaz", "baseSalary" not in ld and job["job"]["salary"] is None, ld.get("baseSalary"))
api("admin", "PUT", f"{R}/referrals/settings", {**settings, "publishSalaryInJobPostings": True})
code, job2 = http("GET", f"{P}/jobs/{PID}")
bs = ((job2 or {}).get("jsonLd") or {}).get("baseSalary") or {}
check("Kiracı açınca baseSalary (aralık)", code == 200 and bs.get("currency") == "TRY" and bs["value"]["minValue"] == 90000 and job2["job"]["salary"]["max"] == 130000, bs)
api("admin", "PUT", f"{R}/referrals/settings", settings)
code, _ = http("GET", f"{P}/jobs/00000000-0000-4000-8000-000000000011")
check("Yayında olmayan ilan 404", code == 404, code)

# ====================================================================== 73 çalışan önerisi
ref = {"jobPostingId": PID, "firstName": "Deniz", "lastName": "Önerilen", "email": email("ref"), "relationship": "FormerColleague",
       "note": "Eski ekip arkadaşım, veri modellemede güçlü.", "candidateConsent": False}
code, r0 = api("ayse", "POST", f"{R}/referrals", ref)
check("Aday onayı işaretlenmeden öneri reddedilir", code == 400, (code, r0))
CV_TEXT = "Deniz Önerilen\nVeri mühendisi, 6 yıl deneyim.\nPython, SQL, Spark"
code, r1 = api("ayse", "POST", f"{R}/referrals", {**ref, "candidateConsent": True, "resumeText": CV_TEXT})
check("Çalışan aday önerir", code == 200 and r1["status"] == "InReview", (code, r1))
stored = psql(f"""SELECT "ResumeText" FROM recruitment_candidates WHERE "Email" = '{ref["email"]}' AND "AnonymizedAt" IS NULL""")
check("Öneriye eklenen CV metni adayın özgeçmişi olur", "Spark" in stored and "6 yıl" in stored, stored[:120])
code, big = api("ayse", "POST", f"{R}/referrals", {**ref, "email": email("ref-big"), "candidateConsent": True, "resumeText": "x" * 20001})
check("CV metni 20.000 karakteri aşamaz", code == 400, code)
code, dup = api("ayse", "POST", f"{R}/referrals", {**ref, "candidateConsent": True})
check("Aynı aday aynı ilana ikinci kez önerilemez (ayrıntı verilmeden)", code == 409, (code, dup))
my_email = psql(f"""SELECT "Email" FROM employee_employees WHERE "Id" = '{AYSE}'""")
code, self_ref = api("ayse", "POST", f"{R}/referrals", {**ref, "email": my_email, "candidateConsent": True})
check("Kendini önerme reddedilir", code == 400, (code, self_ref))

notice = psql(f"""SELECT "Channel" || '|' || "TemplateCode" || '|' || "Body" FROM notification_messages
                  WHERE "RecipientEmail" = '{email("ref")}' AND "TemplateCode" = 'recruitment.referral.notice'""")
ayse_name = psql(f"""SELECT "FirstName" FROM employee_employees WHERE "Id" = '{AYSE}'""")
check("Adaya KVKK aydınlatma e-postası (öneren adı yok)", notice.startswith("Email|recruitment.referral.notice|") and "KVKK m.10" in notice
      and ayse_name not in notice.split("|", 2)[2], notice[:200])
m = re.search(r"/kariyer/demo/durum/([A-Za-z0-9_-]{20,})", notice)
TOKEN = m.group(1) if m else None
check("E-postada durum bağlantısı var", TOKEN is not None, notice[-200:])

code, mine = api("ayse", "GET", f"{R}/referrals/mine")
item = next((i for i in (mine or {}).get("items", []) if i["id"] == r1.get("id")), None) if code == 200 else None
check("Öneren kaba durumu görür", item is not None and item["status"] == "InReview" and item["candidateName"] == "Deniz Önerilen"
      and set(item) >= {"status", "statusLabel", "rewardStatus"} and "applicationStatus" not in item, item)
code, mine_m = api("mehmet", "GET", f"{R}/referrals/mine")
check("Başkasının önerisi görünmez", code == 200 and all(i["id"] != r1.get("id") for i in mine_m["items"]), code)
code, _ = api("ayse", "GET", f"{R}/referrals")
check("Çalışan İK öneri panosunu göremez", code == 403, code)
code, adm = api("admin", "GET", f"{R}/referrals")
arow = next((i for i in (adm or {}).get("items", []) if i["id"] == r1.get("id")), None) if code == 200 else None
check("İK panosu: öneren, onay beyanı, KVKK bildirimi", arow is not None and arow["referrer"] and arow["candidateConsentConfirmed"] and arow["noticeSentAt"]
      and adm["summary"]["total"] >= 1, arow)
APP = arow["applicationId"] if arow else None
src = psql(f"""SELECT a."Channel" || '|' || c."Source" FROM recruitment_applications a JOIN recruitment_candidates c ON c."Id" = a."CandidateId" WHERE a."Id" = '{APP}'""")
check("Başvuru kanalı Referral, aday kaynağı öneri", src == "Referral|Çalışan önerisi", src)

# ====================================================================== 74 durum bağlantısı
code, st = http("GET", f"{P}/status/{TOKEN}")
check("Durum bağlantısı oturumsuz açılır: kaba durum + sonraki adım", code == 200 and st["status"] == "Received" and st["nextStep"] and st["posting"] == "TEST11r Veri Mühendisi", (code, st))
check("Durum sayfasında kişisel veri yok", code == 200 and "Deniz" not in str(st) and "test11r" not in str(st), st)
code, issued = api("admin", "POST", f"{R}/applications/{APP}/status-links", {"days": 30, "sendEmail": False})
NEW = issued["path"].rsplit("/", 1)[1] if code == 200 else None
check("İK yeni durum bağlantısı üretir (jeton bir kez döner)", code == 200 and issued["link"]["active"] and NEW, (code, issued))
code, _ = http("GET", f"{P}/status/{TOKEN}")
check("Yeni bağlantı eskisini geçersiz kılar", code == 404, code)
code, links = api("admin", "GET", f"{R}/applications/{APP}/status-links")
check("Bağlantı listesi (jeton/özet dönmez)", code == 200 and any(l["revokedBy"] == "Reissued" for l in links) and "tokenHash" not in str(links), links)
code, _ = api("ayse", "POST", f"{R}/applications/{APP}/status-links", {"sendEmail": False})
check("Çalışan durum bağlantısı üretemez", code == 403, code)
code, rv = http("POST", f"{P}/status/{NEW}/revoke")
code2, _ = http("GET", f"{P}/status/{NEW}")
check("Aday bağlantıyı kendisi iptal eder", code == 200 and code2 == 404, (code, code2))
code, issued2 = api("admin", "POST", f"{R}/applications/{APP}/status-links", {"days": 30, "sendEmail": True})
TOK2 = issued2["path"].rsplit("/", 1)[1] if code == 200 else None
mails = psql(f"""SELECT count(*) FROM notification_messages WHERE "RecipientEmail" = '{email("ref")}' AND "TemplateCode" = 'recruitment.status.link'""")
check("İsteğe bağlı e-posta ile gönderim", code == 200 and mails == "1", (code, mails))
code, rva = api("admin", "POST", f"{R}/applications/{APP}/status-links/revoke")
code2, _ = http("GET", f"{P}/status/{TOK2}")
check("İK tüm bağlantıları iptal eder", code == 200 and rva["revoked"] == 1 and code2 == 404, (code, rva, code2))
code, issued3 = api("admin", "POST", f"{R}/applications/{APP}/status-links", {"days": 30, "sendEmail": False})
TOK3 = issued3["path"].rsplit("/", 1)[1] if code == 200 else None

# ====================================================================== 77 kör puan kartı + tutarlılık
api("admin", "PUT", f"{R}/job-postings/{PID}/scorecard-template",
    {"criteria": [{"key": "tech", "label": "Teknik", "weight": 3}, {"key": "comm", "label": "İletişim", "weight": 1}]})
day = dt.date(2032, 1, 1) + dt.timedelta(days=RND % 300)
T = dt.datetime(day.year, day.month, day.day, 6, 0, tzinfo=dt.timezone.utc)
code, iv = api("admin", "POST", f"{R}/applications/{APP}/interviews",
               {"type": "Technical", "scheduledAt": T.isoformat().replace("+00:00", "Z"), "interviewerEmployeeIds": [AYSE, MEHMET], "durationMinutes": 45})
IV = iv.get("id") if code == 201 else None
check("İki görüşmecili mülakat", IV is not None, (code, iv))
code, sa = api("ayse", "POST", f"{R}/interviews/{IV}/scorecard",
               {"scores": [{"key": "tech", "score": 5, "evidence": "Akış işleme tasarımını ayrıntılı anlattı"}, {"key": "comm", "score": 4}], "recommendation": "StrongYes"})
check("Kanıt notlu puan kartı", code == 200 and sa["scores"][0]["evidence"], (code, sa))
code, form = api("mehmet", "GET", f"{R}/interviews/{IV}/scorecard-form")
check("Form: panel ve gönderen sayısı", code == 200 and form["panelSize"] == 2 and form["submittedCount"] == 1 and not form["locked"], (code, form))
code, blind = api("mehmet", "GET", f"{R}/interviews/{IV}/scorecards")
check("Kör değerlendirme: gönderene kadar diğer kart gizli", code == 200 and blind["blind"] and blind["scorecards"] == [] and blind["average"] is None
      and blind["consistency"] is None and blind["submittedCount"] == 1, (code, blind))
code, hr_view = api("admin", "GET", f"{R}/interviews/{IV}/scorecards")
check("İK (görüşmeci değil) kartları görür", code == 200 and not hr_view["blind"] and len(hr_view["scorecards"]) == 1, (code, hr_view))


def pipeline_card(who):
    c, pl = api(who, "GET", f"{R}/applications/pipeline?jobPostingId={PID}")
    return c, next((x for x in (pl or {}).get("cards", []) if x["id"] == APP), None)


code, card = pipeline_card("mehmet")
check("Kanban: panel tamamlanmadan görüşmeciye ortalama gizli (kör kural)", code == 200 and card and card["averageScore"] is None and card["averageHidden"], (code, card))
code, card = pipeline_card("admin")
check("Kanban: panel dışındaki İK ortalamayı görür", code == 200 and card and card["averageScore"] is not None and not card["averageHidden"], (code, card))
code, sm = api("mehmet", "POST", f"{R}/interviews/{IV}/scorecard",
               {"scores": [{"key": "tech", "score": 1}, {"key": "comm", "score": 4}], "recommendation": "No"})
check("İkinci görüşmeci gönderir", code == 200, (code, sm))
code, opened = api("mehmet", "GET", f"{R}/interviews/{IV}/scorecards")
cons = (opened or {}).get("consistency") or {}
tech = next((c for c in cons.get("criteria", []) if c["key"] == "tech"), {})
check("Tümü gönderince kartlar açılır + yüksek görüş ayrılığı işaretlenir", code == 200 and not opened["blind"] and len(opened["scorecards"]) == 2
      and cons.get("highDisagreement") and cons.get("recommendationSplit") and tech.get("spread") == 4 and tech.get("highDisagreement"), (code, cons))
code, card = pipeline_card("mehmet")
check("Kanban: panel tamamlanınca görüşmeciye ortalama açılır", code == 200 and card and card["averageScore"] is not None and not card["averageHidden"], (code, card))
code, locked = api("ayse", "POST", f"{R}/interviews/{IV}/scorecard", {"scores": [{"key": "tech", "score": 3}]})
check("Panel tamamlanınca puan kartı kilitlenir", code == 409, (code, locked))

# ====================================================================== 74 sonraki adım + işe alım
code, st = http("GET", f"{P}/status/{TOK3}")
check("Mülakat aşamasında 'Değerlendiriliyor' + mülakat adımı", code == 200 and st["status"] == "InReview" and "mülakat" in st["nextStep"].lower(), (code, st))
for s in ("Offer", "Hired"):
    code, mv = api("admin", "POST", f"{R}/applications/{APP}/move", {"status": s})
check("Başvuru işe alındı", code == 200 and mv["status"] == "Hired", (code, mv))
events = psql(f"""SELECT string_agg("ToStatus", ',' ORDER BY "ChangedAt") FROM recruitment_application_stage_events WHERE "ApplicationId" = '{APP}'""")
check("Aşama geçmişi tetikleyiciyle tutulur", events == "Applied,Interview,Offer,Hired", events)
code, st = http("GET", f"{P}/status/{TOK3}")
check("Durum bağlantısı: olumlu sonuç", code == 200 and st["status"] == "Positive", (code, st))
code, mine = api("ayse", "GET", f"{R}/referrals/mine")
item = next((i for i in mine["items"] if i["id"] == r1["id"]), {}) if code == 200 else {}
check("Öneren 'İşe alındı' görür", item.get("status") == "Hired", item)

# ====================================================================== 73 ödül
code, ev = api("admin", "POST", f"{R}/referrals/evaluate")
code2, adm = api("admin", "GET", f"{R}/referrals")
arow = next((i for i in adm["items"] if i["id"] == r1["id"]), {}) if code2 == 200 else {}
check("İşe alımdan sonra deneme süresi bekleniyor", code == 200 and arow.get("rewardStatus") == "Waiting" and arow.get("rewardEligibleAt"), arow)
code, early = api("admin", "POST", f"{R}/referrals/{r1['id']}/reward", {"action": "approve"})
check("Hak edilmeden ödül onaylanamaz", code == 409, (code, early))
api("admin", "PUT", f"{R}/referrals/settings", {**settings, "referralProbationDays": 0})
api("admin", "POST", f"{R}/referrals/evaluate")
code, adm = api("admin", "GET", f"{R}/referrals?rewardStatus=Eligible")
arow = next((i for i in adm["items"] if i["id"] == r1["id"]), {}) if code == 200 else {}
check("Deneme süresi dolunca hak edilir (İK onayı bekler) + tutar ayarlardan", arow.get("rewardStatus") == "Eligible" and arow.get("rewardAmount") == 5000, arow)
code, _ = api("ayse", "POST", f"{R}/referrals/{r1['id']}/reward", {"action": "approve"})
check("Çalışan ödül kararı veremez", code == 403, code)
code, ap = api("admin", "POST", f"{R}/referrals/{r1['id']}/reward", {"action": "approve", "amount": 6000, "note": "TEST11r onay"})
code2, pd = api("admin", "POST", f"{R}/referrals/{r1['id']}/reward", {"action": "pay"})
check("İK onaylar ve ödendi işaretler", code == 200 and ap["rewardStatus"] == "Approved" and code2 == 200 and pd["rewardStatus"] == "Paid", (ap, pd))
code, again = api("admin", "POST", f"{R}/referrals/{r1['id']}/reward", {"action": "not-eligible"})
check("Ödenmiş ödül değiştirilemez", code == 409, code)
audit = psql(f"""SELECT count(*) FROM audit_log WHERE "EntityType" = 'Referral' AND "EntityId" = '{r1['id']}' AND "Action" = 'RewardDecided'""")
check("Ödül kararları denetim kaydında", audit == "2", audit)
inapp = psql(f"""SELECT count(*) FROM notification_messages WHERE "RecipientEmployeeId" = '{AYSE}' AND "TemplateCode" = 'recruitment.referral.reward' AND "CreatedAt" >= '{START}'""")
check("Önerene uygulama içi ödül bildirimi", inapp == "2", inapp)

# ====================================================================== 78 huni analizi
code, fn = api("admin", "GET", f"{R}/recruitment-analytics/funnel?jobPostingId={PID}")
check("Huni analizi (ilan filtresi)", code == 200 and fn["applications"] == 1 and fn["stages"][0]["stage"] == "Applied"
      and next(s for s in fn["stages"] if s["stage"] == "Hired")["reached"] == 1, (code, fn))
check("Küçük gruplar gizlenir (<5)", code == 200 and fn["timeToHire"]["suppressed"] and fn["timeToHire"]["medianDays"] is None
      and all(s["suppressed"] for s in fn["sources"]), fn.get("timeToHire") if code == 200 else code)
code, fa = api("admin", "GET", f"{R}/recruitment-analytics/funnel")
check("Tüm kiracı hunisi", code == 200 and fa["applications"] >= 1 and fa["minGroup"] == 5, code)
code, _ = api("ayse", "GET", f"{R}/recruitment-analytics/funnel")
code2, _ = api("mehmet", "GET", f"{R}/recruitment-analytics/funnel")
check("Huni analizi yalnızca İK", code == 403 and code2 == 403, (code, code2))

# ====================================================================== 74 öz-hizmette sonraki adım
code, ap2 = http("POST", f"{P}/jobs/{PID}/apply", {"firstName": "Kariyer", "lastName": "Adayı", "email": email("career"), "talentPoolConsent": False,
                                                  "privacyNoticeVersion": "2026-10-v1", "formToken": form_token(P + "/form-token")})
code2, ss = http("GET", f"{P}/self-service/{ap2.get('token')}") if code == 200 else (code, None)
check("Öz-hizmet bağlantısında sonraki adım", code2 == 200 and ss.get("nextStep"), (code, code2, ss))

# ====================================================================== 74 hız sınırı (en sonda)
codes = [http("GET", f"{P}/status/{'x' * 30}")[0] for _ in range(60)]
check("Durum bağlantısı denemeleri hız sınırına takılır (429)", 429 in codes and 404 in codes, sorted(set(codes)))
time.sleep(31)

# ---------------------------------------------------------------------- geri al
if HAD_SETTINGS:
    api("admin", "PUT", f"{R}/referrals/settings", ORIG)
else:
    psql("""DELETE FROM recruitment_program_settings WHERE "TenantSlug" = 'demo'""")
cleanup()
left = psql("""SELECT count(*) FROM recruitment_referrals r JOIN recruitment_job_postings p ON p."Id" = r."JobPostingId" WHERE p."Title" LIKE 'TEST11r%'""")
check("Test verisi temizlendi", left == "0", left)
print(f"FAILS: {len(FAIL)}")
sys.exit(1 if FAIL else 0)

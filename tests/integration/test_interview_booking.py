#!/usr/bin/env python3
"""Adayın mülakat saatini kendisi seçmesi: /api/recruitment/applications/<id>/interview-proposals ve
/api/recruitment/public/<şirket>/interview-booking/<jeton>.

Denetlenenler: öneri doğrulaması (2-10 saat, gelecekte); adaya e-posta; aday bağlantıyla saatleri görür,
birini seçer, mülakat o saate kurulur, görüşmeciye bildirim gider; bağlantı ikinci kez kullanılamaz;
önerilmemiş saat reddedilir; dolu saat listelenmez; geçersiz jeton 404. Geçici ilan/aday sonda silinir.
Çalıştırma: timeout 150 python3 tests/integration/test_interview_booking.py
"""
import datetime as dt
import os
import random
import subprocess
import sys

sys.path.insert(0, os.path.dirname(__file__))
from common import FAIL, api, check, form_token, http  # noqa: E402
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "support"))
from demo_ids import ENG, MEHMET  # noqa: E402

ROOT = os.path.join(os.path.dirname(__file__), "..", "..")
FAIL.clear()
R = "/api/recruitment"
P = f"{R}/public/demo"
RND = random.randint(10000, 99999)


def psql(sql):
    out = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", "hr360_operational", "-Atc", sql],
                         capture_output=True, text=True, cwd=ROOT)
    return (out.stdout + out.stderr).strip()


def cleanup():
    psql("""DELETE FROM recruitment_candidates c WHERE EXISTS (SELECT 1 FROM recruitment_applications a JOIN recruitment_job_postings p ON p."Id" = a."JobPostingId"
                WHERE a."CandidateId" = c."Id" AND p."Title" LIKE 'TEST-IB%');
            DELETE FROM recruitment_job_postings WHERE "Title" LIKE 'TEST-IB%';
            DELETE FROM notification_messages WHERE "RecipientEmail" LIKE 'test-ib-%' OR "TemplateCode" = 'recruitment.interview.booking' OR ("TemplateCode" = 'recruitment.interview.scheduled' AND "Body" LIKE '%TEST-IB%');""")


def at(days, hour):
    d = dt.datetime.now(dt.timezone.utc).replace(minute=0, second=0, microsecond=0) + dt.timedelta(days=days)
    return d.replace(hour=hour).isoformat().replace("+00:00", "Z")


cleanup()
try:
    code, p = api("admin", "POST", f"{R}/job-postings", {"title": f"TEST-IB Geliştirici {RND}", "departmentId": ENG, "description": "x", "employmentType": "FullTime", "headcount": 1})
    api("admin", "POST", f"{R}/job-postings/{p['id']}/publish")
    code, ap = http("POST", f"{P}/jobs/{p['id']}/apply", {"firstName": "Deniz", "lastName": "Aday", "email": f"test-ib-{RND}@example.com",
                                                         "formToken": form_token(P + "/form-token")})
    _, apps = api("admin", "GET", f"{R}/applications")
    app = next(a for a in apps if a.get("jobPostingId") == p["id"])
    A = f"{R}/applications/{app['id']}/interview-proposals"

    code, _ = api("admin", "POST", A, {"type": "Technical", "durationMinutes": 60, "interviewerIds": [MEHMET], "slots": [at(3, 10)]})
    check("IB1: tek saat reddedilir (2-10)", code == 400, code)
    code, _ = api("admin", "POST", A, {"type": "Technical", "durationMinutes": 60, "interviewerIds": [MEHMET], "slots": [at(-1, 10), at(3, 10)]})
    check("IB1: geçmiş saat reddedilir", code == 400, code)
    s1, s2, s3 = at(3, 7), at(3, 9), at(4, 7)
    code, prop = api("admin", "POST", A, {"type": "Technical", "durationMinutes": 60, "interviewerIds": [MEHMET], "slots": [s1, s2, s3], "meetingUrl": "https://meet.example/test-ib"})
    check("IB1: öneri oluşur, bağlantı döner", code == 200 and "/mulakat/" in prop["link"], (code, prop))
    token = prop["link"].rsplit("/", 1)[1]
    n = psql(f"SELECT count(*) FROM notification_messages WHERE \"TemplateCode\" = 'recruitment.interview.booking' AND \"RecipientEmail\" = 'test-ib-{RND}@example.com'")
    check("IB1: adaya e-posta", n == "1", n)

    code, _ = http("GET", f"{P}/interview-booking/{'x' * 43}")
    check("IB2: geçersiz jeton 404", code == 404, code)
    code, view = http("GET", f"{P}/interview-booking/{token}")
    check("IB2: aday saatleri görür", code == 200 and view["status"] == "Open" and len(view["slots"]) == 3 and view["firstName"] == "Deniz", (code, view))
    code, _ = http("POST", f"{P}/interview-booking/{token}", {"slot": at(5, 10)})
    check("IB3: önerilmemiş saat reddedilir", code == 400, code)
    code, b = http("POST", f"{P}/interview-booking/{token}", {"slot": view["slots"][1]})
    check("IB3: aday saati seçer", code == 200, (code, b))
    iv = psql(f"SELECT \"ScheduledAt\" AT TIME ZONE 'UTC' FROM recruitment_interviews WHERE \"ApplicationId\" = '{app['id']}' AND \"Result\" = 'Pending'")
    check("IB3: mülakat seçilen saate kurulur", iv.startswith(s2[:13].replace("T", " ")), (iv, s2))
    n = psql(f"SELECT count(*) FROM notification_messages WHERE \"TemplateCode\" = 'recruitment.interview.scheduled' AND \"RecipientEmployeeId\" = '{MEHMET}' AND \"Body\" LIKE '%TEST-IB%'")
    check("IB3: görüşmeciye bildirim", n not in ("", "0"), n)
    code, _ = http("POST", f"{P}/interview-booking/{token}", {"slot": view["slots"][0]})
    check("IB4: bağlantı ikinci kez kullanılamaz", code == 409, code)
    _, view2 = http("GET", f"{P}/interview-booking/{token}")
    check("IB4: durum 'Booked' ve seçilen saat görünür", view2["status"] == "Booked" and view2["chosen"], view2)

    # Dolu saat: yeni öneride Mehmet'in artık dolu olduğu s2 listelenmez
    code, prop2 = api("admin", "POST", A, {"type": "Final", "durationMinutes": 60, "interviewerIds": [MEHMET], "slots": [s2, s3]})
    _, view3 = http("GET", f"{P}/interview-booking/{prop2['link'].rsplit('/', 1)[1]}")
    check("IB5: görüşmecinin dolu saati listelenmez", len(view3["slots"]) == 1, view3["slots"])
finally:
    cleanup()

print("\nSONUÇ:", "BAŞARILI" if not FAIL else f"{len(FAIL)} HATA: {FAIL}")
sys.exit(1 if FAIL else 0)

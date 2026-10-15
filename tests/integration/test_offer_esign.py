#!/usr/bin/env python3
"""Dalga 11 / madde 76: iş teklifi mektubunun aday tarafından basit elektronik imzayla (e-postaya
tek kullanımlık kod) kabulü. Kod, imza ve kanıt governance'taki TEK imza motorundadır
(DocumentType 'OfferLetter', SignerKind 'Candidate'); recruitment iç uçlarla çağırır.

Ön koşul: HR360 çalışıyor; recruitment-service ve governance-service güncel ve INTERNAL_SERVICE_TOKEN
ikisinde de tanımlı; 2026-10-24_offer_esign.sql uygulanmış. E-posta gerçekte gönderilmez: kod,
notification_messages kaydından okunur (sahte ortam).
"""
import datetime as dt
import hashlib
import os
import random
import re
import subprocess
import time
import sys

sys.path.insert(0, os.path.dirname(__file__))
from common import FAIL, api, check, form_token, http  # noqa: E402

R = "/api/recruitment"
P = f"{R}/public/demo"
ROOT = os.path.join(os.path.dirname(__file__), "..", "..")
FAIL.clear()
RND = random.randint(10000, 99999)
SALARY = 76543.21
START = dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%d %H:%M:%S+00")
NOHEAD_DEPT = "00000000-0000-4000-8000-0000000e5e11"


def psql(sql):
    out = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", "hr360_operational", "-Atc", sql],
                         capture_output=True, text=True, cwd=ROOT)
    return (out.stdout + out.stderr).strip()


def pub(method, path, body=None):
    return http(method, P + path, body)


def email(tag):
    return f"test11e-{tag}-{RND}@example.com"


def cleanup():
    # Aday silinince teklifler (FK) ve tetikleyiciyle governance kodları/kanıtları da silinir.
    psql("""DELETE FROM recruitment_candidates WHERE "Email" LIKE 'test11e-%@example.com';
            DELETE FROM recruitment_job_postings WHERE "Title" LIKE 'TEST11e%';
            DELETE FROM notification_messages WHERE "RecipientEmail" LIKE 'test11e-%' OR "Subject" LIKE '%TEST11e%' OR "Body" LIKE '%TEST11e%';""")


cleanup()

# ---------------------------------------------------------------- hazırlık: ilan + iki kariyer başvurusu
code, p = api("admin", "POST", f"{R}/job-postings", {"title": "TEST11e Teklif İmza", "departmentId": NOHEAD_DEPT, "description": "Açıklama",
                                                     "employmentType": "FullTime", "headcount": 2})
POSTING = p.get("id") if code == 201 else None
api("admin", "POST", f"{R}/job-postings/{POSTING}/publish")
check("Hazırlık: ilan", POSTING is not None, (code, p))

apps = {}
for tag, first in [("a", "İmzacı"), ("b", "Retçi")]:
    code, r = pub("POST", f"/jobs/{POSTING}/apply", {"firstName": first, "lastName": "Testçi", "email": email(tag), "formToken": form_token(P + "/form-token")})
    apps[tag] = r if code == 200 else {}
check("Hazırlık: iki başvuru", all(a.get("token") for a in apps.values()), apps)
TOK_SELF = apps["a"].get("token")
start = (dt.date.today() + dt.timedelta(days=30)).isoformat()
exp = (dt.date.today() + dt.timedelta(days=10)).isoformat()


def make_offer(tag):
    body = {"applicationId": apps[tag].get("applicationId"), "positionTitle": "TEST11e Geliştirici", "grossSalary": SALARY, "currency": "TRY",
            "startDate": start, "expiresAt": exp}
    code, cr = api("admin", "POST", f"{R}/offers", body)
    oid = cr["offer"]["id"] if code == 200 else None
    api("admin", "POST", f"{R}/offers/{oid}/decide", {"approve": True})
    return oid


OFFER = make_offer("a")
check("Hazırlık: teklif (İK karar verir) onaylandı", OFFER is not None, OFFER)

# ---------------------------------------------------------------- gönderim: imza bağlantısı
code, o = api("admin", "GET", f"{R}/offers/{OFFER}")
check("Gönderilmemiş teklifte imza bağlantısı yok", code == 200 and not o.get("signingLinkActive") and not o.get("signed"), (code, o))
code, sent = api("admin", "POST", f"{R}/offers/{OFFER}/send")
path = (sent or {}).get("signingPath") or ""
TOK = path.rsplit("/", 1)[-1] if path else ""
check("Gönderimde imza bağlantısı bir kez döner", code == 200 and sent["status"] == "Sent" and path.startswith("/kariyer/demo/teklif/") and sent["signingLinkActive"],
      (code, sent))
h = psql(f"""SELECT "SignTokenHash" FROM recruitment_offers WHERE "Id" = '{OFFER}'""")
check("İmza jetonunun yalnızca SHA-256 özeti saklanır", TOK and h == hashlib.sha256(TOK.encode()).hexdigest(), h)
code, o = api("admin", "GET", f"{R}/offers/{OFFER}")
check("Sonraki okumalarda bağlantı dönmez", code == 200 and not o.get("signingPath") and o.get("signingLinkActive"), o)
mail = psql(f"""SELECT "Body" FROM notification_messages WHERE "RecipientEmail" = '{email("a")}' AND "TemplateCode" = 'recruitment.offer.sent'
                ORDER BY "CreatedAt" DESC LIMIT 1""")
check("Teklif e-postası imza bağlantısını içerir, ücreti içermez", TOK and TOK in mail and "76543" not in mail and "76.543" not in mail, mail[:200])

# ---------------------------------------------------------------- aday sayfası
code, pg = pub("GET", f"/offer-sign/{TOK}")
check("Aday oturumsuz mektubu görür (imzalanabilir)", code == 200 and pg["canSign"] and "76.543,21" in pg["letterText"] and pg["status"] == "Sent"
      and "*" in pg["emailMasked"] and pg["letterSha256"] == hashlib.sha256(pg["letterText"].replace("\r\n", "\n").encode()).hexdigest(), (code, pg))
code, _ = pub("GET", "/offer-sign/" + "x" * 43)
check("Geçersiz imza jetonu 404", code == 404, code)
code, pg2 = pub("GET", f"/offer-sign/{TOK_SELF}")
check("Öz-hizmet jetonu da aynı teklife çözülür", code == 200 and pg2.get("offerId") == OFFER, (code, pg2))
code, r = pub("POST", f"/offer-sign/{TOK}/sign", {"code": "123456", "confirm": False})
check("Onay kutusu olmadan imza yok (400)", code == 400 and r.get("code") == "confirm_required", (code, r))
code, r = pub("POST", f"/offer-sign/{TOK}/sign", {"code": "123456", "confirm": True})
check("Kod istemeden imza yok (otp_not_found)", code == 404 and r.get("code") == "otp_not_found", (code, r))

# ---------------------------------------------------------------- kod (governance motoru, e-posta)
code, otp = pub("POST", f"/offer-sign/{TOK}/otp")
check("Kod adayın e-postasına gönderilir", code == 200 and otp.get("otpId") and otp.get("channel") == "Email", (code, otp))
body = psql(f"""SELECT "Body" FROM notification_messages WHERE "RecipientEmail" = '{email("a")}' AND "TemplateCode" = 'signature.otp'
                AND "Channel" = 'Email' ORDER BY "CreatedAt" DESC LIMIT 1""")
m = re.search(r"\b(\d{6})\b", body or "")
CODE = m.group(1) if m else None
check("Kod e-postası çalışan kaydına bağlanmaz, ücret/ad içermez",
      CODE and "76543" not in body and "76.543" not in body and "İmzacı" not in body
      and psql(f"""SELECT "RecipientEmployeeId" FROM notification_messages WHERE "RecipientEmail" = '{email("a")}' AND "TemplateCode" = 'signature.otp' LIMIT 1""")
      == "00000000-0000-0000-0000-000000000000", (body or "")[:80].replace(CODE or "§", "******"))
orow = psql(f"""SELECT row_to_json(o)::text FROM governance_signature_otps o WHERE "DocumentType" = 'OfferLetter' AND "DocumentId" = '{OFFER}'""")
check("Kod yalnızca özetiyle saklanır", CODE and CODE not in orow and otp.get("otpId", "?") in orow, "otp row")
wrong = "000000" if CODE != "000000" else "111111"
code, r = pub("POST", f"/offer-sign/{TOK}/sign", {"otpId": otp.get("otpId"), "code": wrong, "confirm": True})
check("Hatalı kod: kalan deneme döner", code == 400 and r.get("code") == "otp_invalid" and r.get("attemptsLeft") == 4, (code, r))

# ---------------------------------------------------------------- imza → kabul
code, s = pub("POST", f"/offer-sign/{TOK}/sign", {"otpId": otp.get("otpId"), "code": CODE, "confirm": True})
check("Doğru kodla imzalanır ve teklif kabul edilir", code == 200 and s.get("status") == "Accepted" and s.get("integrityOk") and s.get("method") == "OTP-Email", (code, s))
st = psql(f"""SELECT "Status" FROM recruitment_applications WHERE "Id" = '{apps["a"].get("applicationId")}'""")
check("Başvuru İşe alındı olur", st == "Hired", st)
ev = psql(f"""SELECT "SignerKind" || '|' || "DocumentSha256" || '|' || coalesce("IpPrefix", '-') || '|' || "Title" FROM governance_signatures
              WHERE "DocumentType" = 'OfferLetter' AND "DocumentId" = '{OFFER}'""").split("|")
check("Kanıt governance'ta: aday imzası, mektup özeti, IP yok, başlıkta ücret/ad yok",
      len(ev) == 4 and ev[0] == "Candidate" and ev[1] == pg.get("letterSha256") and ev[2] == "-" and "76" not in ev[3] and "İmzacı" not in ev[3], ev)
row = psql(f"""SELECT "SignatureEvidenceId" IS NOT NULL, "SignedLetterHtml" IS NOT NULL, encode(sha256(convert_to("SignedLetterHtml", 'UTF8')), 'hex') = "SignedDocumentSha256"
               FROM recruitment_offers WHERE "Id" = '{OFFER}'""")
check("İmzalı belge özetiyle saklanır", row == "t|t|t", row)
aud = psql(f"""SELECT count(*) FROM audit_log WHERE "EntityId" = '{OFFER}' AND "Action" IN ('Signed', 'SignedByCandidate')""")
check("İmza denetim kaydına yazılır (governance + recruitment)", int(aud or 0) >= 2, aud)
code, r = pub("POST", f"/offer-sign/{TOK}/sign", {"otpId": otp.get("otpId"), "code": CODE, "confirm": True})
check("İkinci imza reddedilir (409)", code == 409, (code, r))
code, r = pub("POST", f"/offer-sign/{TOK}/otp")
check("İmzalı teklif için yeni kod istenemez", code == 409, (code, r))
code, d = pub("GET", f"/offer-sign/{TOK}/document")
check("Aday imzalı belgeyi indirir", code == 200 and OFFER and s.get("evidenceId", "?") in d.get("html", "") and d.get("sha256") == s.get("signedDocumentSha256"), code)

# ---------------------------------------------------------------- İK görünümü
code, sig = api("admin", "GET", f"{R}/offers/{OFFER}/signature")
check("İK kanıtı ve bütünlüğü görür", code == 200 and sig["signed"] and sig["documentIntegrityOk"] and sig["letterUnchanged"]
      and sig["evidence"] and sig["evidence"]["integrityOk"] and sig["evidence"]["matchesLetter"], (code, sig))
code, _ = api("ayse", "GET", f"{R}/offers/{OFFER}/signature")
check("Çalışan kanıtı göremez (403)", code == 403, code)
code, sl = api("admin", "GET", f"{R}/offers/{OFFER}/signed-letter")
aud = psql(f"""SELECT count(*) FROM audit_log WHERE "EntityId" = '{OFFER}' AND "Action" = 'SensitiveViewed' AND "Changes"::text LIKE '%signedLetter%'""")
check("İK imzalı belgeyi indirir (denetlenir)", code == 200 and "76.543,21" in sl.get("html", "") and int(aud or 0) >= 1, (code, aud))
code, r = api("admin", "POST", f"{R}/offers/{OFFER}/signing-link")
check("İmzalı teklifte bağlantı yenilenemez (409)", code == 409, (code, r))
mine = psql(f"""SELECT count(*) FROM governance_signatures WHERE "DocumentType" = 'OfferLetter' AND "DocumentId" = '{OFFER}' AND "SignerKind" = 'Employee'""")
check("Aday imzası çalışan imzaları ('İmzalarım') arasına girmez", mine == "0", mine)

# ---------------------------------------------------------------- iptal / yenileme / ret
OFFER_B = make_offer("b")
code, sb = api("admin", "POST", f"{R}/offers/{OFFER_B}/send")
TOK_B1 = ((sb or {}).get("signingPath") or "").rsplit("/", 1)[-1]
code, rn = api("admin", "POST", f"{R}/offers/{OFFER_B}/signing-link")
TOK_B2 = ((rn or {}).get("signingPath") or "").rsplit("/", 1)[-1]
c1, _ = pub("GET", f"/offer-sign/{TOK_B1}")
c2, _ = pub("GET", f"/offer-sign/{TOK_B2}")
check("Yenileme önceki bağlantıyı geçersiz kılar", code == 200 and TOK_B2 and TOK_B1 != TOK_B2 and c1 == 404 and c2 == 200, (code, c1, c2))
code, rv = api("admin", "DELETE", f"{R}/offers/{OFFER_B}/signing-link")
c3, _ = pub("GET", f"/offer-sign/{TOK_B2}")
c4, sp = pub("GET", f"/offer-sign/{apps['b'].get('token')}")
check("İptal edilen bağlantı 404; öz-hizmet bağlantısı çalışır", code == 200 and not rv.get("signingLinkActive") and c3 == 404 and c4 == 200 and sp.get("canDecline"),
      (code, c3, c4))
code, dc = pub("POST", f"/offer-sign/{apps['b'].get('token')}/decline")
check("Aday teklifi reddeder", code == 200 and dc.get("status") == "Declined", (code, dc))
code, _ = api("mehmet", "POST", f"{R}/offers/{OFFER_B}/signing-link")
check("İK dışı bağlantı üretemez (403)", code == 403, code)

# ---------------------------------------------------------------- saklama bağı
cleanup()
# Yük altında (tam koşu) önceki adımların kayıtları birkaç saniye gecikebilir; 20 sn'ye kadar beklenir.
# Gerçek bir yetim kayıt (teklif silindiği hâlde duran kanıt/kod) bekleme sonunda yine yakalanır.
for _ in range(20):
    left = psql(f"""SELECT count(*) FROM governance_signatures WHERE "DocumentType" = 'OfferLetter' AND "DocumentId" IN ('{OFFER}', '{OFFER_B}')""")
    left_otp = psql(f"""SELECT count(*) FROM governance_signature_otps WHERE "DocumentType" = 'OfferLetter' AND "DocumentId" IN ('{OFFER}', '{OFFER_B}')""")
    if left == "0" and left_otp == "0":
        break
    time.sleep(1)
check("Aday/teklif silinince imza kanıtı ve kodları da silinir", left == "0" and left_otp == "0", (left, left_otp))

print(f"FAILS: {len(FAIL)}")
for f in FAIL:
    print("  -", f)
sys.exit(1 if FAIL else 0)

#!/usr/bin/env python3
"""G11 bildirim tercihleri: kategori x kanal, zorunlu kategoriler, sessiz saatler (e-posta/push ertelenir,
düşürülmez), günlük özet (yalnızca konular), uygulama içi gizleme, güvenlik açısından kritik bildirimlerin
tercihleri atlaması ve servisler arası 'effective' ucu. API: GET/PUT /api/notifications/preferences/me.

Ön koşul: notification-service test katmanıyla çalışıyor (EMAIL_POLL_SECONDS / DIGEST_POLL_SECONDS /
PUSH_POLL_SECONDS / DIGEST_CHECK_SECONDS kısa; push uç noktası chatmock):
  docker compose -f docker-compose.yml -f deploy/testing/chat-mock.yml up -d notification-service
SMTP: Mailpit (HR360_MAILPIT_URL, varsayılan http://127.0.0.1:8025).
"""
import base64
import datetime as dt
import json
import os
import subprocess
import sys
import time
import urllib.parse
import urllib.request
import uuid

from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric import ec

sys.path.insert(0, os.path.dirname(__file__))
from common import AYSE, FAIL, api, check, mock_calls  # noqa: E402

FAIL.clear()
ROOT = os.path.join(os.path.dirname(__file__), "..", "..")
MAILPIT = os.environ.get("HR360_MAILPIT_URL", "http://127.0.0.1:8025")
P = "/api/notification/notifications/preferences"
TENANT = "demo"
TAG = "TEST-np-" + uuid.uuid4().hex[:8]
FAKE = str(uuid.uuid4())  # gerçek olmayan çalışan: işçi testleri başka testleri etkilemesin
FAKE_MAIL = f"{TAG.lower()}@example.test"
IST = dt.timezone(dt.timedelta(hours=3))


def psql(sql):
    out = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", "hr360_operational", "-Atc", sql],
                         capture_output=True, text=True, cwd=ROOT)
    return (out.stdout + out.stderr).strip()


def q(s):
    return "'" + str(s).replace("'", "''") + "'"


def insert_msg(emp, channel, code, subject, body="Test gövdesi", email=None, action=None, deferred=None):
    nid = str(uuid.uuid4())
    psql(f"""INSERT INTO notification_messages ("Id","TenantSlug","RecipientEmployeeId","RecipientEmail","Channel","TemplateCode","Subject","Body","Status","AttemptCount","CreatedAt","ActionUrl","DeferredUntil")
             VALUES ('{nid}','{TENANT}','{emp}',{q(email) if email else 'NULL'},'{channel}',{q(code)},{q(subject)},{q(body)},'Pending',0,now(),
                     {q(action) if action else 'NULL'},{deferred or 'NULL'})""")
    return nid


def row(nid):
    r = psql(f"""SELECT "Status", coalesce("DeferredUntil"::text,''), coalesce("PushedAt"::text,'') FROM notification_messages WHERE "Id" = '{nid}'""")
    return r.split("|") if r else ["", "", ""]


def wait(pred, timeout=30, step=1):
    end = time.time() + timeout
    while time.time() < end:
        v = pred()
        if v:
            return v
        time.sleep(step)
    return pred()


def mails_to(addr):
    url = f"{MAILPIT}/api/v1/search?query={urllib.parse.quote(f'to:{addr}')}&limit=50"
    with urllib.request.urlopen(url, timeout=10) as r:
        return json.loads(r.read())["messages"]


def mail(mid):
    with urllib.request.urlopen(f"{MAILPIT}/api/v1/message/{mid}", timeout=10) as r:
        return json.loads(r.read())


def internal(path, token):
    out = subprocess.run(["docker", "exec", "hr360-gateway-1", "wget", "-qO-", "--header", f"X-Internal-Token: {token}",
                          f"http://notification-service:8080{path}"], capture_output=True, text=True)
    try:
        return json.loads(out.stdout) if out.stdout else None, out.stderr
    except ValueError:
        return None, out.stderr


def hhmm(d):
    return d.strftime("%H:%M")


now_local = dt.datetime.now(IST)
cats = None
try:
    # ------------------------------------------------------------ CRUD (Ayşe)
    code, r = api("ayse", "GET", f"{P}/me")
    cats = {c["key"]: c for c in (r or {}).get("categories", [])} if code == 200 else {}
    check("Tercihler okunur (9 kategori, saat dilimi İstanbul, dil alanı)", code == 200 and r["linked"] and len(cats) == 9
          and r["timeZone"] == "Europe/Istanbul" and "language" in r, r)
    check("Bordro ve KVKK/yasal kategorileri zorunlu", cats.get("payroll", {}).get("mandatory") and cats.get("legal", {}).get("mandatory")
          and not cats.get("announcements", {}).get("mandatory"), cats)
    check("Güvenlik kategorisi kritik (kapatılamaz)", cats.get("security", {}).get("critical") is True
          and not cats.get("legal", {}).get("critical"), cats.get("security"))
    lang_before = r.get("language") if code == 200 else None
    check("Varsayılan: tüm kanallar açık", all(c["inApp"] and c["email"] and c["push"] and c["chat"] for c in cats.values()) or
          psql(f"""SELECT count(*) FROM notification_category_prefs WHERE "EmployeeId" = '{AYSE}'""") != "0", cats)

    # Sessiz saat şu anı KAPSAMAYAN bir pencere (eşzamanlı başka testleri etkilemesin).
    qs, qe = now_local + dt.timedelta(hours=3), now_local + dt.timedelta(hours=4)
    body = {
        "categories": [
            {"key": "announcements", "inApp": False, "email": False, "push": True, "chat": False},
            {"key": "payroll", "inApp": False, "email": False, "push": False, "chat": True},
            {"key": "legal", "inApp": False, "email": True, "push": True, "chat": True},
            {"key": "security", "inApp": False, "email": False, "push": False, "chat": False},
        ],
        "quietHours": {"enabled": True, "start": hhmm(qs), "end": hhmm(qe), "days": [1, 2, 3, 4, 5]},
        "digest": {"enabled": True, "hour": 7},
    }
    code, r = api("ayse", "PUT", f"{P}/me", body)
    rc = {c["key"]: c for c in (r or {}).get("categories", [])} if code == 200 else {}
    check("Tercihler kaydedildi", code == 200 and rc["announcements"]["inApp"] is False and rc["announcements"]["email"] is False
          and rc["announcements"]["chat"] is False and rc["payroll"]["email"] is False, (code, r))
    check("Zorunlu kategoride uygulama içi kapatılamaz (bordro, KVKK)", rc.get("payroll", {}).get("inApp") is True and rc.get("legal", {}).get("inApp") is True, rc)
    sec = rc.get("security", {})
    check("Güvenlik kategorisinde hiçbir kanal kapatılamaz", all(sec.get(k) is True for k in ("inApp", "email", "push", "chat")), sec)
    check("Kritik kategori için satır saklanmaz",
          psql(f"""SELECT count(*) FROM notification_category_prefs WHERE "EmployeeId" = '{AYSE}' AND "Category" = 'security'""") == "0")
    code, r = api("ayse", "GET", f"{P}/me")
    check("Kaydedilen tercih geri okunur (sessiz saat, günler, özet)",
          code == 200 and r["quietHours"] == {"enabled": True, "start": hhmm(qs), "end": hhmm(qe), "days": [1, 2, 3, 4, 5]}
          and r["digest"] == {"enabled": True, "hour": 7}
          and {c["key"]: c for c in r["categories"]}["announcements"]["inApp"] is False, r)
    check("Veritabanında zorunlu kategori uygulama içi açık",
          psql(f"""SELECT "InApp" FROM notification_category_prefs WHERE "EmployeeId" = '{AYSE}' AND "Category" = 'payroll'""") == "t")

    for name, bad in [
        ("geçersiz saat", {"quietHours": {"enabled": True, "start": "25:00", "end": "08:00", "days": [1]}}),
        ("aynı başlangıç/bitiş", {"quietHours": {"enabled": True, "start": "08:00", "end": "08:00", "days": [1]}}),
        ("gün seçilmemiş", {"quietHours": {"enabled": True, "start": "22:00", "end": "08:00", "days": []}}),
        ("bilinmeyen kategori", {"categories": [{"key": "xyz", "inApp": True, "email": True, "push": True, "chat": True}]}),
        ("özet saati", {"digest": {"enabled": True, "hour": 30}}),
    ]:
        code, r = api("ayse", "PUT", f"{P}/me", bad)
        check(f"Doğrulama: {name} reddedilir (400)", code == 400, (code, r))

    code, r = api("ayse", "PUT", f"{P}/me", {})
    check("Doğrulama: boş gövde reddedilir (400)", code == 400, (code, r))
    code, r = api("admin", "PUT", f"{P}/me", {"digest": {"enabled": True, "hour": 9}})
    check("Çalışan kaydı olmayan hesap tercih kaydedemez (403)", code == 403, (code, r))
    code, r = api("admin", "GET", f"{P}/me")
    check("Çalışan kaydı olmayan hesap: varsayılanlar, linked=false", code == 200 and r["linked"] is False and len(r["categories"]) == 9, (code, r))

    # Geriye uyum: yalnızca dil gönderen istemci (arayüz dil eşitlemesi) ve eski /channels adı.
    code, r = api("ayse", "PUT", f"{P}/me", {"language": "en"})
    code2, r2 = api("ayse", "GET", f"{P}/me")
    check("Yalnızca dil güncellemesi çalışır, kanal tercihleri korunur", code == 200 and r.get("language") == "en" and code2 == 200
          and r2["language"] == "en" and r2["digest"] == {"enabled": True, "hour": 7}, (code, r, r2 and r2.get("digest")))
    api("ayse", "PUT", f"{P}/me", {"language": lang_before or "tr"})
    code, r = api("ayse", "GET", f"{P}/channels")
    check("Eski /channels adı da çalışır", code == 200 and len(r.get("categories", [])) == 9, code)

    # Uygulama içi gizleme: kapatılan (zorunlu olmayan) kategori listede görünmez; zorunlu olan görünür.
    hid = insert_msg(AYSE, "InApp", "engagement.kudos", f"{TAG} gizli takdir")
    vis = insert_msg(AYSE, "InApp", "compensation.raise", f"{TAG} ücret")
    psql(f"""UPDATE notification_messages SET "PushedAt" = now() WHERE "Id" IN ('{hid}','{vis}')""")
    code, lst = api("ayse", "GET", f"/api/notification/notifications?recipientId={AYSE}&limit=200")
    ids = {n["id"] for n in lst} if code == 200 else set()
    check("Uygulama içi kapatılan kategori listede yok", code == 200 and hid not in ids, (code, len(ids)))
    check("Zorunlu kategori (bordro) listede görünür", vis in ids, len(ids))

    # Tercih değişince ertelenmiş gönderimler yeniden değerlendirilir (sessiz saat şu an değil → gider).
    rid = insert_msg(AYSE, "Email", "learning.reset", f"{TAG} yeniden degerlendirme", email=FAKE_MAIL.replace("@", "+reset@"),
                     deferred="now() + interval '1 hour'")
    code, _ = api("ayse", "PUT", f"{P}/me", {"digest": {"enabled": False, "hour": 7}})
    check("Özet kapatıldı", code == 200)
    st = wait(lambda: row(rid)[0] == "Sent" and row(rid), 30)
    check("Tercih güncellemesi ertelenen e-postayı serbest bırakır (gönderildi)", st and st[0] == "Sent", row(rid))

    # Servisler arası uç (governance sohbet iletimi için).
    token = subprocess.run(["docker", "exec", "hr360-notification-service-1", "printenv", "INTERNAL_SERVICE_TOKEN"],
                           capture_output=True, text=True).stdout.strip()
    res, err = internal(f"/api/notifications/preferences/effective?employeeId={AYSE}&templateCode=engagement.kudos", "yanlis")
    check("İç uç yanlış jetonu reddeder (404)", res is None and "404" in err, err[:200])
    code, r = api("ayse", "GET", f"{P}/effective?employeeId={AYSE}")
    check("İç uç kullanıcı jetonuyla da kapalı (404)", code == 404, code)
    if token:
        res, err = internal(f"/api/notifications/preferences/effective?employeeId={AYSE}&tenant={TENANT}&templateCode=engagement.kudos", token)
        d = (res or {}).get("decision") or {}
        check("İç uç: kategori, sohbet kanalı ve e-posta kararı", res is not None and res["stored"] and d.get("category") == "announcements"
              and d.get("chat") is False and d.get("email") == "Suppress" and d.get("inApp") is False and res["quietNow"] is False, (res, err[:200]))
        res, _ = internal(f"/api/notifications/preferences/effective?employeeId={AYSE}&templateCode=disciplinary.decision", token)
        d = (res or {}).get("decision") or {}
        check("İç uç: yasal bildirim uygulama içi her zaman açık", d.get("category") == "legal" and d.get("inApp") is True, res)
        res, _ = internal(f"/api/notifications/preferences/effective?employeeId={AYSE}&templateCode=privacy.breach", token)
        d = (res or {}).get("decision") or {}
        check("İç uç: veri ihlali bildirimi güvenlik açısından kritik (tüm kanallar, ertelenmez)",
              res and res.get("securityCritical") is True and d.get("category") == "security" and d.get("email") == "Send"
              and d.get("push") == "Send" and d.get("chat") is True and d.get("chatDeferUntil") is None, res)
    else:
        check("INTERNAL_SERVICE_TOKEN tanımlı", False, "boş")

    # ------------------------------------------------------------ İşçiler (sahte çalışan)
    start, end = now_local - dt.timedelta(minutes=60), now_local + dt.timedelta(minutes=60)
    psql(f"""INSERT INTO notification_preferences ("TenantSlug","EmployeeId","Language","UpdatedAt","QuietHoursEnabled","QuietStart","QuietEnd","QuietDays")
             VALUES ('{TENANT}','{FAKE}','tr',now(),true,'{hhmm(start)}','{hhmm(end)}',127)""")
    psql(f"""INSERT INTO notification_category_prefs ("TenantSlug","EmployeeId","Category","InApp","Email","Push","Chat")
             VALUES ('{TENANT}','{FAKE}','recruitment',true,false,true,true), ('{TENANT}','{FAKE}','leave',true,true,false,true),
                    ('{TENANT}','{FAKE}','security',false,false,false,false), ('{TENANT}','{FAKE}','legal',true,false,false,false),
                    ('{TENANT}','{FAKE}','system',false,false,false,false)""")

    # Web Push aboneliği (chatmock uç noktası).
    priv = ec.generate_private_key(ec.SECP256R1())
    pub = priv.public_key().public_bytes(serialization.Encoding.X962, serialization.PublicFormat.UncompressedPoint)
    b64 = lambda b: base64.urlsafe_b64encode(b).rstrip(b"=").decode()  # noqa: E731
    sid = uuid.uuid4().hex
    psql(f"""INSERT INTO notification_push_subscriptions ("Id","TenantSlug","EmployeeId","Endpoint","P256dh","Auth","Device","FailureCount","CreatedAt")
             VALUES ('{uuid.uuid4()}','{TENANT}','{FAKE}','http://chatmock:8000/push/{sid}','{b64(pub)}','{b64(os.urandom(16))}','{TAG}',0,now())""")

    t0 = time.time()
    em = insert_msg(FAKE, "Email", "learning.quiet", f"{TAG} sessiz e-posta", email=FAKE_MAIL)
    pu = insert_msg(FAKE, "InApp", "engagement.kudos", f"{TAG} sessiz push")
    st = wait(lambda: row(em)[1] and row(pu)[1] and (row(em), row(pu)), 20)
    exp_end = end.strftime("%H:%M")
    check("Sessiz saat: e-posta ertelendi (Pending + DeferredUntil = pencere sonu)",
          st and st[0][0] == "Pending" and st[0][1] != "" and
          dt.datetime.fromisoformat(st[0][1].replace(" ", "T")).astimezone(IST).strftime("%H:%M") == exp_end, (st, exp_end))
    check("Sessiz saat: push ertelendi (PushedAt boş)", st and st[1][2] == "" and st[1][1] != "", st)
    time.sleep(4)
    check("Sessiz saatte e-posta gönderilmedi", not [m for m in mails_to(FAKE_MAIL) if TAG in m["Subject"]], mails_to(FAKE_MAIL)[:2])
    check("Sessiz saatte push gönderilmedi", not mock_calls(f"/push/{sid}", t0))

    # Güvenlik açısından kritik: kişi kategoriyi (doğrudan veritabanında) kapatmış ve sessiz saatte olsa da hemen gider.
    tc = time.time()
    cb = insert_msg(FAKE, "Email", "privacy.breach", f"{TAG} veri ihlali bildirimi", email=FAKE_MAIL)
    co = insert_msg(FAKE, "Email", "signature.otp", f"{TAG} dogrulama kodu", email=FAKE_MAIL)
    cp = insert_msg(FAKE, "InApp", "privacy.breach", f"{TAG} veri ihlali push")
    st = wait(lambda: row(cb)[0] == "Sent" and row(co)[0] == "Sent" and row(cp)[2] and (row(cb), row(co), row(cp)), 30)
    check("Kritik e-postalar (veri ihlali, OTP) opt-out ve sessiz saate rağmen gönderildi",
          st and st[0][0] == "Sent" and st[1][0] == "Sent" and st[0][1] == "" and st[1][1] == "", (row(cb), row(co)))
    m = wait(lambda: [x for x in mails_to(FAKE_MAIL) if "veri ihlali bildirimi" in x["Subject"]], 20)
    check("Kritik e-posta Mailpit'e ulaştı", bool(m), [x["Subject"] for x in mails_to(FAKE_MAIL)][:3])
    hits = wait(lambda: mock_calls(f"/push/{sid}", tc), 20)
    check("Kritik push sessiz saatte ve push kapalıyken iletildi", bool(hits) and st and st[2][1] == "", row(cp))
    t2 = time.time()

    # Sessiz saatler biter (penceresi kapanmış gibi): ertelenenler düşürülmeden iletilir.
    psql(f"""UPDATE notification_preferences SET "QuietHoursEnabled" = false WHERE "EmployeeId" = '{FAKE}'""")
    psql(f"""UPDATE notification_messages SET "DeferredUntil" = now() - interval '1 second' WHERE "Id" IN ('{em}','{pu}')""")
    st = wait(lambda: row(em)[0] == "Sent" and row(em), 30)
    check("Sessiz saat bitince e-posta gönderildi", st and st[0] == "Sent", row(em))
    m = wait(lambda: [x for x in mails_to(FAKE_MAIL) if "sessiz e-posta" in x["Subject"]], 20)
    check("Ertelenen e-posta Mailpit'e ulaştı", bool(m), mails_to(FAKE_MAIL)[:2])
    hits = wait(lambda: mock_calls(f"/push/{sid}", t2), 30)
    check("Sessiz saat bitince push iletildi", bool(hits) and row(pu)[2] != "", row(pu))

    # Kategori kapalı: e-posta gönderilmez (kayıt kalır), push iletilmez.
    t1 = time.time()
    sup = insert_msg(FAKE, "Email", "recruitment.applied", f"{TAG} kapali kategori", email=FAKE_MAIL)
    nop = insert_msg(FAKE, "InApp", "leave.approved", f"{TAG} push kapali")
    st = wait(lambda: row(sup)[0] == "Suppressed" and row(nop)[2] and (row(sup), row(nop)), 20)
    check("E-postası kapatılan kategori gönderilmedi (Suppressed)", st and st[0][0] == "Suppressed", (row(sup)))
    time.sleep(3)
    check("Push'u kapatılan kategori iletilmedi", st and st[1][2] != "" and not mock_calls(f"/push/{sid}", t1), row(nop))

    # Günlük özet: acil olmayanlar özete, onay ve eylem bağlantılı e-postalar tek tek gider.
    hour_now = dt.datetime.now(IST).hour
    psql(f"""UPDATE notification_preferences SET "DigestEnabled" = true, "DigestHour" = {hour_now}, "DigestLastSentAt" = now() WHERE "EmployeeId" = '{FAKE}'""")
    d1 = insert_msg(FAKE, "Email", "learning.course.assigned", f"{TAG} ozet egitim", body="GIZLI-GOVDE-1", email=FAKE_MAIL)
    d2 = insert_msg(FAKE, "Email", "engagement.kudos", f"{TAG} ozet takdir", body="GIZLI-GOVDE-2", email=FAKE_MAIL)
    ap = insert_msg(FAKE, "Email", "workflow.submitted", f"{TAG} onay bekliyor", email=FAKE_MAIL)
    ac = insert_msg(FAKE, "Email", "learning.link", f"{TAG} eylem baglantili", email=FAKE_MAIL, action="https://example.test/x")
    st = wait(lambda: all(row(i)[0] == "DigestQueued" for i in (d1, d2)) and all(row(i)[0] == "Sent" for i in (ap, ac)), 30)
    check("Acil olmayan e-postalar özete alındı (DigestQueued)", row(d1)[0] == "DigestQueued" and row(d2)[0] == "DigestQueued", (row(d1), row(d2)))
    check("Onay ve eylem bağlantılı e-postalar hemen gönderildi", row(ap)[0] == "Sent" and row(ac)[0] == "Sent", (row(ap), row(ac)))
    time.sleep(4)
    check("Özet saati gelmeden özet gönderilmez", row(d1)[0] == "DigestQueued", row(d1))

    # Özet saati geldi (son gönderim dün).
    psql(f"""UPDATE notification_preferences SET "DigestLastSentAt" = now() - interval '25 hours' WHERE "EmployeeId" = '{FAKE}'""")
    st = wait(lambda: row(d1)[0] == "Digested" and row(d2)[0] == "Digested", 30)
    check("Özetlenen bildirimler Digested", bool(st), (row(d1), row(d2)))
    dg = psql(f"""SELECT "Id" FROM notification_messages WHERE "RecipientEmployeeId" = '{FAKE}' AND "TemplateCode" = 'digest.daily'""").splitlines()
    check("Tek özet e-postası oluşturuldu", len(dg) == 1, dg)
    m = wait(lambda: [x for x in mails_to(FAKE_MAIL) if "özeti" in x["Subject"]], 30)
    check("Özet e-postası ulaştı (Günlük bildirim özeti (2))", bool(m) and m[0]["Subject"] == "Günlük bildirim özeti (2)", [x["Subject"] for x in mails_to(FAKE_MAIL)])
    if m:
        full = mail(m[0]["ID"])
        txt = full.get("Text", "")
        check("Özet yalnızca konuları içerir, gövdeleri içermez (KVKK)",
              f"{TAG} ozet egitim" in txt and f"{TAG} ozet takdir" in txt and "GIZLI-GOVDE" not in txt and "GIZLI-GOVDE" not in full.get("HTML", ""), txt[:400])
        check("Özet kategoriye göre gruplu", "Eğitim ve oryantasyon (1)" in txt and "Duyurular ve etkileşim (1)" in txt, txt[:400])
    time.sleep(4)
    check("Özet aynı gün tekrar gönderilmez", len(psql(f"""SELECT "Id" FROM notification_messages WHERE "RecipientEmployeeId" = '{FAKE}' AND "TemplateCode" = 'digest.daily'""").splitlines()) == 1)
finally:
    psql(f"""DELETE FROM notification_messages WHERE "RecipientEmployeeId" = '{FAKE}' OR "Subject" LIKE '{TAG}%'""")
    psql(f"""DELETE FROM notification_push_subscriptions WHERE "EmployeeId" = '{FAKE}'""")
    psql(f"""DELETE FROM notification_category_prefs WHERE "EmployeeId" IN ('{FAKE}','{AYSE}')""")
    psql(f"""DELETE FROM notification_preferences WHERE "EmployeeId" = '{FAKE}'""")
    psql(f"""UPDATE notification_preferences SET "QuietHoursEnabled" = false, "QuietStart" = '22:00', "QuietEnd" = '08:00', "QuietDays" = 127,
             "DigestEnabled" = false, "DigestHour" = 18, "DigestLastSentAt" = NULL WHERE "EmployeeId" = '{AYSE}'""")

print(f"\nFAILS: {len(FAIL)}")
for f in FAIL:
    print("  -", f)
sys.exit(1 if FAIL else 0)

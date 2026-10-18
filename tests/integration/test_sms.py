#!/usr/bin/env python3
"""SMS kanalı: kiracı ayarı (Netgsm / İleti Merkezi), deneme SMS'i, kuyruktan gönderim, kişi tercihi, KVKK.

Önkoşul: notification-service test katmanıyla (deploy/testing/chat-mock.yml) çalışıyor olmalı.
Kiracının önceki SMS ayarı varsa test dokunmadan çıkar; Ayşe'nin telefonu ve SMS tercihi geri yüklenir.
"""
import json
import sys
import time
import uuid

from common import AYSE, FAIL, api, check, mock_calls, wait_for  # noqa: E402

S = "/api/notification/notifications/sms-settings"
N = "/api/notification/notifications"
P = "/api/notification/notifications/preferences/me"
TAG = f"TEST-SMS-{uuid.uuid4().hex[:6]}"

code, _ = api("ayse", "GET", S)
check("Çalışan SMS ayarını göremez", code == 403, code)
code, cur = api("admin", "GET", S)
check("Şirket yöneticisi SMS ayarını okur", code == 200 and "providers" in cur, cur)
if code == 200 and cur.get("configured"):
    print("Kiracıda kayıtlı SMS ayarı var; test atlandı (gerçek ayara dokunulmaz).")
    sys.exit(0)

code, _ = api("admin", "POST", N, {"recipientEmployeeId": AYSE, "channel": "Sms", "subject": TAG, "body": "x"})
check("Sağlayıcı yokken SMS bildirimi reddedilir", code == 400, code)

base = {"enabled": True, "provider": "netgsm", "username": "smsuser", "secret": "smspass", "sender": "HR360", "dailyLimit": 50}
for bad, why in (({"provider": "twilio"}, "yurt dışı/bilinmeyen sağlayıcı"), ({"sender": "BU-BASLIK-COK-UZUN"}, "uzun başlık"),
                 ({"sender": "<x>"}, "geçersiz başlık"), ({"secret": None}, "gizli anahtarsız ilk kayıt"), ({"dailyLimit": 0}, "sınır 0")):
    code, _ = api("admin", "PUT", S, {**base, **bad})
    check(f"Geçersiz ayar reddedilir ({why})", code == 400, code)

code, r = api("admin", "PUT", S, base)
check("Netgsm ayarı kaydedildi; gizli anahtar yanıtta yok", code == 200 and r.get("hasSecret") is True and "smspass" not in json.dumps(r), r)
try:
    code, _ = api("admin", "POST", f"{S}/test", {"phone": "123"})
    check("Deneme: geçersiz numara reddedilir", code == 400, code)
    t0 = time.time()
    code, r = api("admin", "POST", f"{S}/test", {"phone": "0532 000 00 01"})
    hits = mock_calls("/netgsm/sms/rest/v2/send", t0)
    sent = [json.loads(h["body"]) for h in hits]
    check("Deneme SMS'i Netgsm'e gitti (10 haneli numara, başlık)", code == 200 and any(
        b["messages"][0]["no"] == "5320000001" and b["msgheader"] == "HR360" for b in sent), (code, r, sent))

    code, _ = api("admin", "PUT", S, {**base, "secret": "yanlis"})
    code, r = api("admin", "POST", f"{S}/test", {"phone": "05320000001"})
    check("Yanlış parola: anlaşılır hata (502), parola sızmaz", code == 502 and "parola" in r.get("message", "") and "yanlis" not in json.dumps(r), (code, r))
    code, _ = api("admin", "PUT", S, {**base, "secret": None})
    check("Gizli anahtar boş bırakılırsa mevcut korunur", code == 200, code)

    code, r = api("admin", "PUT", S, {**base, "provider": "iletimerkezi", "username": "imkey", "secret": None})
    check("Sağlayıcı değişince yeni gizli anahtar istenir", code == 400, (code, r))
    code, _ = api("admin", "PUT", S, {**base, "provider": "iletimerkezi", "username": "imkey", "secret": "imsecret"})
    t0 = time.time()
    code, r = api("admin", "POST", f"{S}/test", {"phone": "+90 532 000 00 02"})
    hits = mock_calls("/iletimerkezi/v1/send-sms/json", t0)
    check("Deneme SMS'i İleti Merkezi'ne gitti (HMAC imzalı, gizli anahtar gövdede yok)",
          code == 200 and hits and "imsecret" not in (hits[-1]["body"] or ""), (code, r))
    api("admin", "PUT", S, base)

    # Kuyruktan gönderim: Ayşe'nin telefonu yoksa geçici olarak verilir, sonra geri alınır.
    code, emp = api("admin", "GET", f"/api/employee/employees/{AYSE}")
    old_phone = emp.get("phone") if code == 200 else None
    if not old_phone:
        api("ayse", "PUT", "/api/employee/employees/me/contact", {"phone": "0532 000 00 03"})
    code, prefs = api("ayse", "GET", P)
    old_sms = prefs.get("sms", True) if code == 200 else True
    try:
        api("ayse", "PUT", P, {"sms": True})
        t0 = time.time()
        code, n = api("admin", "POST", N, {"recipientEmployeeId": AYSE, "channel": "Sms", "subject": TAG, "body": "Kuyruk denemesi"})
        check("SMS bildirimi kuyruğa alındı", code == 201 and n.get("status") == "Pending", (code, n))
        hits = wait_for("/netgsm/sms/rest/v2/send", lambda c: TAG in (c["body"] or ""), since=t0, timeout=40)
        check("Kuyruktaki SMS Netgsm'e iletildi (konu + gövde)", bool(hits) and "Kuyruk denemesi" in hits[0]["body"], hits)
        time.sleep(3)
        code, lst = api("admin", "GET", f"{N}?recipientId={AYSE}&limit=50")
        mine = [x for x in lst if x.get("subject") == TAG] if code == 200 else []
        check("Gönderilen SMS: durum Sent, numara silindi (KVKK)",
              len(mine) == 1 and mine[0]["status"] == "Sent" and mine[0].get("recipientPhone") is None, mine)

        code, _ = api("ayse", "PUT", P, {"sms": False})
        code, p2 = api("ayse", "GET", P)
        check("Kişi SMS almayı kapatabilir", code == 200 and p2.get("sms") is False, p2)
        code, n = api("admin", "POST", N, {"recipientEmployeeId": AYSE, "channel": "Sms", "subject": TAG + " kapali", "body": "Gitmemeli"})
        deadline = time.time() + 30
        st = None
        while time.time() < deadline:
            code, lst = api("admin", "GET", f"{N}?recipientId={AYSE}&limit=50")
            st = next((x["status"] for x in lst if x.get("id") == n.get("id")), None)
            if st and st != "Pending":
                break
            time.sleep(2)
        check("SMS'i kapatan kişiye gönderilmez (Suppressed)", st == "Suppressed", st)
        check("Kapatılan SMS sağlayıcıya gitmedi", not mock_calls("Gitmemeli", 0) and not [
            c for c in mock_calls("/netgsm/", t0) if "Gitmemeli" in (c["body"] or "")], "gitti")
    finally:
        api("ayse", "PUT", P, {"sms": old_sms})
        if not old_phone:
            api("ayse", "PUT", "/api/employee/employees/me/contact", {"phone": None})

    code, _ = api("admin", "PUT", S, {**base, "enabled": False, "secret": None})
    code, _ = api("admin", "POST", N, {"recipientEmployeeId": AYSE, "channel": "Sms", "subject": TAG, "body": "x"})
    check("SMS kapalıyken bildirim reddedilir", code == 400, code)
finally:
    code, _ = api("admin", "DELETE", S)
    code2, r = api("admin", "GET", S)
    check("SMS ayarı silindi (test kalıntısı yok)", code == 204 and r.get("configured") is False, (code, r))

if FAIL:
    print(f"\n{len(FAIL)} kontrol başarısız: {FAIL}")
    sys.exit(1)
print("\nSMS kanalı: tüm kontroller geçti.")

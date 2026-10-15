#!/usr/bin/env python3
"""PWA anlık bildirim (Web Push) uçtan uca testi: abonelik, VAPID, RFC 8291 şifreleme, KVKK.

Önkoşul: notification-service test katmanıyla çalışıyor olmalı:
  docker compose -f docker-compose.yml -f deploy/testing/chat-mock.yml up -d notification-service
"""
import base64
import json
import os
import sys
import time
import uuid

from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives.asymmetric.utils import encode_dss_signature
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from cryptography.hazmat.primitives.kdf.hkdf import HKDF

from common import FAIL, api, check, mock_calls  # noqa: E402

N = "/api/notification/notifications/push"


def b64u(b):
    return base64.urlsafe_b64encode(b).rstrip(b"=").decode()


def ub64u(s):
    return base64.urlsafe_b64decode(s + "=" * (-len(s) % 4))


def hkdf(ikm, salt, info, n):
    return HKDF(algorithm=hashes.SHA256(), length=n, salt=salt, info=info).derive(ikm)


def new_device():
    priv = ec.generate_private_key(ec.SECP256R1())
    pub = priv.public_key().public_bytes(serialization.Encoding.X962, serialization.PublicFormat.UncompressedPoint)
    return priv, pub, os.urandom(16)


def decrypt(body, ua_priv, ua_pub, auth):
    salt, idlen = body[:16], body[20]
    as_pub = body[21:21 + idlen]
    ct = body[21 + idlen:]
    shared = ua_priv.exchange(ec.ECDH(), ec.EllipticCurvePublicKey.from_encoded_point(ec.SECP256R1(), as_pub))
    ikm = hkdf(shared, auth, b"WebPush: info\x00" + ua_pub + as_pub, 32)
    cek = hkdf(ikm, salt, b"Content-Encoding: aes128gcm\x00", 16)
    nonce = hkdf(ikm, salt, b"Content-Encoding: nonce\x00", 12)
    pt = AESGCM(cek).decrypt(nonce, ct, None)
    return pt.rstrip(b"\x00")[:-1]  # son kayıt sınırlayıcısı (0x02)


code, pk = api("ayse", "GET", f"{N}/public-key")
vapid_pub = ub64u(pk["publicKey"]) if code == 200 else b""
check("VAPID açık anahtarı (65 bayt P-256)", code == 200 and len(vapid_pub) == 65 and vapid_pub[0] == 4, pk)

priv, pub, auth = new_device()
for bad in ("https://evil.example.com/push/x", "http://postgres:5432/x", "https://fcm.googleapis.com.evil.com/x"):
    code, r = api("ayse", "POST", f"{N}/subscriptions", {"endpoint": bad, "keys": {"p256dh": b64u(pub), "auth": b64u(auth)}})
    check(f"SSRF: izinsiz uç nokta reddedilir ({bad.split('/')[2]})", code == 400, (code, r))
code, r = api("ayse", "POST", f"{N}/subscriptions", {"endpoint": "http://chatmock:8000/push/x", "keys": {"p256dh": "AAAA", "auth": b64u(auth)}})
check("Geçersiz abonelik anahtarı reddedilir", code == 400, r)

sid = uuid.uuid4().hex
endpoint = f"http://chatmock:8000/push/{sid}"
code, r = api("ayse", "POST", f"{N}/subscriptions", {"endpoint": endpoint, "keys": {"p256dh": b64u(pub), "auth": b64u(auth)}, "device": "test"})
check("Cihaz aboneliği kaydedildi", code == 200, r)
gone = f"http://chatmock:8000/push/gone-{uuid.uuid4().hex}"
p2, pub2, auth2 = new_device()
api("ayse", "POST", f"{N}/subscriptions", {"endpoint": gone, "keys": {"p256dh": b64u(pub2), "auth": b64u(auth2)}})
code, mine = api("ayse", "GET", f"{N}/subscriptions/me")
check("Kişinin cihaz listesi", code == 200 and len(mine) >= 2, mine)

t0 = time.time()
code, _ = api("ayse", "POST", f"{N}/test")
check("Deneme bildirimi kuyruğa alındı", code == 200, code)
deadline = time.time() + 40
hits = []
while time.time() < deadline and not hits:
    hits = [c for c in mock_calls(f"/push/{sid}", t0)]
    time.sleep(1)
check("Push servisine gönderildi", bool(hits), "yok")
if hits:
    h = hits[0]
    check("İçerik kodlaması aes128gcm, TTL var", h.get("encoding") == "aes128gcm" and h.get("ttl"), h)
    authz = h.get("auth") or ""
    ok_hdr = authz.startswith("vapid t=") and f"k={pk['publicKey']}" in authz
    check("VAPID yetki başlığı (t=JWT, k=açık anahtar)", ok_hdr, authz[:80])
    tok = authz.split("t=", 1)[1].split(",", 1)[0]
    head, claims, sig = tok.split(".")
    cl = json.loads(ub64u(claims))
    raw = ub64u(sig)
    der = encode_dss_signature(int.from_bytes(raw[:32], "big"), int.from_bytes(raw[32:], "big"))
    vk = ec.EllipticCurvePublicKey.from_encoded_point(ec.SECP256R1(), vapid_pub)
    try:
        vk.verify(der, f"{head}.{claims}".encode(), ec.ECDSA(hashes.SHA256()))
        verified = True
    except Exception:  # noqa: BLE001
        verified = False
    check("VAPID JWT imzası doğrulandı (ES256), aud = push servisi kökeni", verified and cl["aud"] == "http://chatmock:8000" and cl["exp"] > time.time(), cl)
    try:
        msg = json.loads(decrypt(base64.b64decode(h["body"]), priv, pub, auth))
    except Exception as e:  # noqa: BLE001
        msg = {"error": str(e)}
    check("RFC 8291: cihaz anahtarıyla çözüldü", msg.get("title") == "HR360" and msg.get("url") == "/panel/bildirimler", msg)
    check("KVKK: push içeriğinde kişisel veri yok", "Ayşe" not in json.dumps(msg, ensure_ascii=False) and "Yılmaz" not in json.dumps(msg, ensure_ascii=False)
          and msg.get("body") in ("Yeni bir bildiriminiz var", "You have a new notification"), msg)

time.sleep(3)
code, mine = api("ayse", "GET", f"{N}/subscriptions/me")
check("410 dönen (geçersiz) abonelik silindi", code == 200 and len(mine) == 1 and mine[0].get("device") == "test", mine)
code, _ = api("ayse", "POST", f"{N}/subscriptions/delete", {"endpoint": endpoint})
check("Abonelik kaldırıldı", code == 204, code)
code, mine = api("ayse", "GET", f"{N}/subscriptions/me")
check("Kaldırılınca listede yok", code == 200 and len(mine) == 0, mine)

# Başkasının aboneliğini silemez (uç nokta bilinse bile).
priv3, pub3, auth3 = new_device()
ep3 = f"http://chatmock:8000/push/{uuid.uuid4().hex}"
api("mehmet", "POST", f"{N}/subscriptions", {"endpoint": ep3, "keys": {"p256dh": b64u(pub3), "auth": b64u(auth3)}})
api("ayse", "POST", f"{N}/subscriptions/delete", {"endpoint": ep3})
code, mm = api("mehmet", "GET", f"{N}/subscriptions/me")
check("Yetki: başkasının aboneliği silinemez", code == 200 and len(mm) >= 1, mm)
api("mehmet", "POST", f"{N}/subscriptions/delete", {"endpoint": ep3})

print(f"\nFAILS: {len(FAIL)}")
for f in FAIL:
    print("  -", f)
sys.exit(1 if FAIL else 0)

#!/usr/bin/env python3
"""Dalga 12 (madde 93-94): webhook teslimat hataları (otomatik yeniden deneme, elle yeniden gönderim,
deneme durdurma) ve kapsamlı API anahtarları (read-only üst yetkisi, takma adlar, son kullanma,
döndürme, kullanım istatistiği).

Teslimat hedefi sahte sunucudaki (tests/integration/chatmock.py) REST hook alıcısıdır; hata
`/_fail` ile enjekte edilir. Gerçek Zapier/n8n ile denenmedi.

Ön koşul: HR360 çalışıyor, deploy/testing/chat-mock.yml katmanı açık (WEBHOOK_RETRY_BASE_SECONDS=2;
yoksa varsayılan 60 sn ile yeniden deneme beklemesi uzar ve ilgili kontroller zaman aşımına düşer).

Test kayıtları: API anahtarı adı "TEST-w12 …", hedef adresi "…/hooks/test-w12…", olay konusu "test.w12"
(tests/support/cleanup_test_data.py da siler).

Çalıştırma: timeout 300 python3 tests/integration/test_webhooks_apikeys.py
"""
import json
import os
import subprocess
import sys
import time
import uuid

sys.path.insert(0, os.path.dirname(__file__))
from common import FAIL, api, check, http, mock  # noqa: E402

G = "/api/governance"
ROOT = os.path.join(os.path.dirname(__file__), "..", "..")
FAIL.clear()


def psql(sql):
    out = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", "hr360_operational", "-Atc", sql],
                         capture_output=True, text=True, cwd=ROOT)
    return (out.stdout + out.stderr).strip()


def key_call(key, method, path, body=None):
    return http(method, path, body, {"X-Api-Key": key})


def cleanup():
    psql("""DELETE FROM governance_webhook_deliveries WHERE "WebhookId" IN (SELECT "Id" FROM governance_webhooks WHERE "Url" LIKE '%test-w12%');
            DELETE FROM governance_webhooks WHERE "Url" LIKE '%test-w12%';
            DELETE FROM governance_api_key_usage WHERE "KeyId" IN (SELECT "Id" FROM governance_api_keys WHERE "Name" LIKE 'TEST-w12%');
            DELETE FROM governance_api_keys WHERE "Name" LIKE 'TEST-w12%';
            DELETE FROM governance_events WHERE "Topic" = 'test.w12';""")


def wait(pred, timeout=40):
    end = time.time() + timeout
    while time.time() < end:
        v = pred()
        if v:
            return v
        time.sleep(1)
    return None


cleanup()
try:
    # ------------------------------------------------------------------ 94: kapsamlı API anahtarları
    code, scopes = api("admin", "GET", f"{G}/api-keys/scopes")
    check("Yetki listesinde read-only var", code == 200 and "read-only" in scopes, (code, scopes))

    code, ro = api("admin", "POST", f"{G}/api-keys", {"name": "TEST-w12 salt okunur", "scopes": ["read-only"], "expiresInDays": 30})
    check("read-only anahtar oluşturuldu (son kullanmalı)", code == 200 and ro.get("expiresAt") and ro["scopes"] == ["read-only"], (code, ro))
    code, al = api("admin", "POST", f"{G}/api-keys", {"name": "TEST-w12 takma ad", "scopes": ["leave:read", "bilinmeyen"]})
    check("Takma ad kanonik yetkiye çevrilir (leave:read → leaves:read)", code == 200 and al["scopes"] == ["leaves:read"], (code, al))

    code, _ = key_call(ro["key"], "GET", f"{G}/public/v1/employees")
    check("read-only: employees:read karşılanır", code == 200, code)
    code, _ = key_call(ro["key"], "GET", f"{G}/public/v1/leaves")
    check("read-only: leaves:read karşılanır", code == 200, code)
    code, r = key_call(ro["key"], "GET", f"{G}/public/v1/hooks")
    check("read-only: yazma yetkisi (hooks:write) yok → 403", code == 403 and r.get("code") == "scope_denied", (code, r))
    code, _ = key_call(al["key"], "GET", f"{G}/public/v1/employees")
    check("Yalnız leaves:read anahtarı çalışan listesini alamaz", code == 403, code)

    code, u = api("admin", "GET", f"{G}/api-keys/{ro['id']}/usage")
    check("Kullanım istatistiği: 2 istek, 1 ret", code == 200 and u["total"] >= 2 and u["denied"] >= 1
          and {s["scope"] for s in u["byScope"]} >= {"employees:read", "leaves:read", "hooks:write"}, (code, u))
    code, keys = api("admin", "GET", f"{G}/api-keys")
    row = next((k for k in keys if k["id"] == ro["id"]), {}) if code == 200 else {}
    check("Liste: son kullanım, 30 gün sayacı, durum", row.get("lastUsedAt") and row.get("last30", 0) >= 2 and row.get("status") == "active"
          and row.get("lastUsedScope") in ("employees:read", "leaves:read"), row)
    check("Liste anahtar özeti/hash döndürmez", "keyHash" not in json.dumps(keys), "keyHash")

    # Döndürme: geçiş süresiyle eski anahtar çalışmaya devam eder; 0 ile hemen iptal.
    code, rot = api("admin", "POST", f"{G}/api-keys/{ro['id']}/rotate", {"graceHours": 1})
    check("Döndürme yeni anahtar üretir", code == 200 and rot.get("key") and rot["key"] != ro["key"] and rot.get("oldKeyValidUntil"), (code, rot))
    code, _ = key_call(rot["key"], "GET", f"{G}/public/v1/departments")
    check("Yeni anahtar çalışır", code == 200, code)
    code, _ = key_call(ro["key"], "GET", f"{G}/public/v1/departments")
    check("Eski anahtar geçiş süresince çalışır", code == 200, code)
    code, rot2 = api("admin", "POST", f"{G}/api-keys/{rot['id']}/rotate", {"graceHours": 0})
    code2, _ = key_call(rot["key"], "GET", f"{G}/public/v1/departments")
    check("Geçiş süresi 0: önceki anahtar hemen geçersiz", code == 200 and code2 == 401, (code, code2))
    code, _ = api("admin", "POST", f"{G}/api-keys/{rot['id']}/rotate", {"graceHours": 0})
    check("İptal edilmiş anahtar döndürülemez", code == 409, code)

    # Süre dolumu: son kullanma geçmişe çekilir → 401 key_expired, listede "expired".
    psql(f"""UPDATE governance_api_keys SET "ExpiresAt" = now() - interval '1 minute' WHERE "Id" = '{rot2['id']}'""")
    code, r = key_call(rot2["key"], "GET", f"{G}/public/v1/departments")
    check("Süresi dolmuş anahtar → 401 key_expired", code == 401 and r.get("code") == "key_expired", (code, r))
    code, keys = api("admin", "GET", f"{G}/api-keys")
    check("Listede süresi dolmuş durum", any(k["id"] == rot2["id"] and k["status"] == "expired" and not k["active"] for k in keys), rot2["id"])

    # ------------------------------------------------------------------ 93: webhook teslimat hataları
    code, hk = api("admin", "POST", f"{G}/api-keys", {"name": "TEST-w12 kanca", "scopes": ["hooks:write"]})
    code, sub = key_call(hk["key"], "POST", f"{G}/public/v1/hooks", {"target_url": "http://chatmock:8000/hooks/test-w12-retry", "event": "leave.approved"})
    check("REST hook aboneliği (sahte alıcı)", code == 201, (code, sub))
    hook_id = sub.get("id")

    # Olay kaydı + başarısız teslimat (yeniden deneme sırada, zamanı geldi). Önce alıcıya 1 hata enjekte edilir:
    # 1. otomatik deneme 500 alır (deneme 2, sonraki deneme ~10 sn), 2. otomatik deneme başarılı (deneme 3).
    # Hata, teslimat satırı eklenmeden ÖNCE enjekte edilir: yeniden deneme işçisi satırı hemen alabilir.
    mock("/_fail?path=/hooks/test-w12-retry&count=1", "POST")
    ev = str(uuid.uuid4())
    d0 = str(uuid.uuid4())
    psql(f"""INSERT INTO governance_events ("Id","TenantSlug","Topic","EventType","Payload","OccurredAt")
             VALUES ('{ev}','demo','test.w12','leave.approved','{{"TenantSlug":"demo","LeaveRequestId":"00000000-0000-4000-8000-000000000012"}}'::jsonb, now());
             INSERT INTO governance_webhook_deliveries ("Id","TenantSlug","WebhookId","EventType","StatusCode","Error","DurationMs","OccurredAt","EventId","Attempt","NextRetryAt","RetryState")
             VALUES ('{d0}','demo','{hook_id}','leave.approved',503,'HTTP 503',5,now(),'{ev}',1,now(),'pending');""")

    def chain():
        rows = psql(f"""SELECT "Attempt", coalesce("StatusCode",0), coalesce("RetryState",'-') FROM governance_webhook_deliveries
                        WHERE "EventId" = '{ev}' ORDER BY "Attempt" """).splitlines()
        return [tuple(r.split("|")) for r in rows if "|" in r]

    got = wait(lambda: (lambda c: c if len(c) >= 3 else None)(chain()), timeout=60)
    c = chain()
    check("Otomatik yeniden deneme: 503 → 500 (geri çekilme) → 200", got is not None
          and c[0] == ("1", "503", "retried") and c[1] == ("2", "500", "retried") and c[2] == ("3", "200", "-"), c)

    code, lst = api("admin", "GET", f"{G}/webhooks/deliveries?state=failed&webhookId={hook_id}")
    items = lst.get("items", []) if code == 200 else []
    check("Hata ekranı: başarısız teslimatlar listelenir, yük dönmez", code == 200 and len(items) >= 2
          and all(i["webhookId"] == hook_id for i in items) and "Payload" not in json.dumps(lst) and "LeaveRequestId" not in json.dumps(lst), (code, lst))
    check("Hata ekranı: özet ve geri çekilme planı", code == 200 and lst["retry"]["maxAttempts"] == 6 and len(lst["retry"]["scheduleSeconds"]) == 5
          and lst["summary"]["failed24h"] >= 1, lst.get("retry") if code == 200 else code)

    # Elle yeniden gönderim (aynı olay kimliği, deneme sırası artar, denetim kaydı).
    first = next((i for i in items if i["id"] == d0), None)
    check("İlk teslimat yeniden gönderilebilir", first is not None and first["canResend"], first)
    code, rs = api("admin", "POST", f"{G}/webhooks/deliveries/{d0}/resend")
    check("Elle yeniden gönderim başarılı", code == 200 and rs.get("ok") and rs.get("attempt") == 2, (code, rs))
    audit = psql(f"""SELECT count(*) FROM audit_log WHERE "EntityType" = 'WebhookDelivery' AND "EntityId" = '{d0}' AND "Action" = 'Resent'""")
    check("Elle yeniden gönderim denetim kaydına yazıldı", audit.strip() == "1", audit)
    manual = psql(f"""SELECT "Manual", "TriggeredByName" IS NOT NULL FROM governance_webhook_deliveries WHERE "ParentDeliveryId" = '{d0}' AND "Manual" """)
    check("Elle gönderim işaretli (kim)", manual.strip() == "t|t", manual)

    # Durdurma: sırada bekleyen deneme vazgeçilir.
    d1 = str(uuid.uuid4())
    psql(f"""INSERT INTO governance_webhook_deliveries ("Id","TenantSlug","WebhookId","EventType","StatusCode","Error","DurationMs","OccurredAt","EventId","Attempt","NextRetryAt","RetryState")
             VALUES ('{d1}','demo','{hook_id}','leave.approved',502,'HTTP 502',5,now(),'{ev}',1,now() + interval '1 hour','pending');""")
    code, _ = api("admin", "POST", f"{G}/webhooks/deliveries/{d1}/cancel-retry")
    st = psql(f"""SELECT "RetryState" FROM governance_webhook_deliveries WHERE "Id" = '{d1}'""")
    check("Sıradaki deneme durduruldu (gave_up)", code == 200 and st.strip() == "gave_up", (code, st))
    code, _ = api("admin", "POST", f"{G}/webhooks/deliveries/{d1}/cancel-retry")
    check("Bekleyen deneme yoksa 409", code == 409, code)

    # Süresi dolmuş olay: yeniden gönderilemez.
    psql(f"""DELETE FROM governance_events WHERE "Id" = '{ev}'""")
    code, r = api("admin", "POST", f"{G}/webhooks/deliveries/{d1}/resend")
    check("Olay kaydı silinmişse 409 event_expired", code == 409 and r.get("code") == "event_expired", (code, r))

    # Yetki: çalışan bu uçları göremez.
    code, _ = api("ayse", "GET", f"{G}/webhooks/deliveries")
    check("Çalışan teslimat ekranına erişemez", code in (401, 403), code)
    code, _ = api("ayse", "POST", f"{G}/api-keys/{ro['id']}/rotate", {})
    check("Çalışan anahtar döndüremez", code in (401, 403), code)
finally:
    cleanup()

print(f"\nFAILS: {len(FAIL)}")
for f in FAIL:
    print("  -", f)
sys.exit(1 if FAIL else 0)

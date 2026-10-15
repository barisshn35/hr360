"""Yapay zekâ (LLM) entegrasyonu — kiracı izni, kişisel veri izni, takma ad, kota,
kullanım kaydı ve İK asistanı (sahte Anthropic API ile).

Ön koşul: governance-service deploy/testing/chat-mock.yml ile (LLM_PROVIDER=anthropic,
LLM_BASE_URL=http://chatmock:8000/anthropic, kota 1 dakikada 20 çağrı) çalışıyor.
"""

import json
import sys
import time

sys.path.insert(0, __import__("os").path.dirname(__file__))
from common import ensure_transfers, FAIL, api, check, mock, wait_for  # noqa: E402

A = "/api/governance/ai"
FAIL.clear()
ensure_transfers("anthropic")
mock("/_reset", "POST")

# Önceki çalıştırmanın kota penceresi boşalsın (testte pencere 1 dakika).
for _ in range(70):
    code, s = api("admin", "GET", f"{A}/settings")
    if s.get("usedThisWindow", 0) == 0:
        break
    time.sleep(1)
code, s = api("ayse", "GET", f"{A}/settings")
check("Ayarlar: sağlayıcı yapılandırılmış, kullanım bilgisi çalışana gösterilmez", code == 200 and s["configured"] and s["provider"] == "anthropic" and s["usage"] is None, s)
code, _ = api("ayse", "PUT", f"{A}/settings", {"enabled": True, "allowPersonalData": True})
check("Yetki: çalışan yapay zekâ ayarını değiştiremez", code == 403, code)

api("admin", "PUT", f"{A}/settings", {"enabled": False, "allowPersonalData": False})
code, r = api("mehmet", "POST", f"{A}/job-draft", {"title": "Backend Geliştirici"})
check("Kiracı kapalıyken model çağrılmaz (403 llm_disabled)", code == 403 and r["code"] == "llm_disabled", r)
check("Kapalıyken sağlayıcıya istek gitmedi", not wait_for("/anthropic/v1/messages", timeout=2))

code, r = api("admin", "PUT", f"{A}/settings", {"enabled": True, "allowPersonalData": False})
check("İK yöneticisi yapay zekâyı açtı", code == 200 and r["enabled"] and not r["allowPersonalData"], r)

t0 = time.time()
code, r = api("mehmet", "POST", f"{A}/job-draft", {"title": "Backend Geliştirici", "skills": ["C#", "PostgreSQL"], "level": "senior", "workModel": "hybrid"})
check("İlan taslağı modelden geldi", code == 200 and r["source"] == "llm" and r["text"].startswith("MOCK-LLM[mock-model]"), r)
check("Taslak ayrımcı ifade denetiminden geçti (ml-inference)", isinstance(r.get("bias"), dict) and "score" in r["bias"], r.get("bias"))
call = wait_for("/anthropic/v1/messages", since=t0)
req = json.loads(call[0]["body"]) if call else {}
check("Sistem talimatı ayrımcı ölçütleri yasaklıyor", "ayrımcı" in req.get("system", ""), req.get("system"))
check("İstek gövdesi beceri ve seviyeyi içeriyor", "C#" in req["messages"][0]["content"] and "senior" in req["messages"][0]["content"], req)

code, r = api("ayse", "POST", f"{A}/job-draft", {"title": "X"})
check("Yetki: çalışan ilan taslağı üretemez", code == 403, code)

code, r = api("mehmet", "POST", f"{A}/inclusive-rewrite", {"text": "25-35 yaş arası erkek adaylar", "phrases": ["25-35 yaş", "erkek"]})
check("Kapsayıcı dille yeniden yazma", code == 200 and r["text"].startswith("MOCK-LLM"), r)

perf = {"name": "Ayşe Yılmaz", "score": 4.1, "previousScore": 3.7,
        "goals": [{"title": "Ayşe'nin API gecikmesi hedefi", "progress": 90}],
        "reviews": [{"strengths": "Ayşe ekibe çok destek oluyor", "improvements": "Yılmaz dokümantasyonu geliştirmeli"}]}
code, r = api("mehmet", "POST", f"{A}/perf-summary", perf)
check("Kişisel veri izni yokken performans özeti reddedilir", code == 403 and r["code"] == "llm_personal_data", r)
api("admin", "PUT", f"{A}/settings", {"enabled": True, "allowPersonalData": True})
t1 = time.time()
code, r = api("mehmet", "POST", f"{A}/perf-summary", perf)
check("Performans özeti üretildi, ad yanıtta yerine kondu", code == 200 and r["pseudonymized"] and "Ayşe için" in r["text"], r)
call = wait_for("/anthropic/v1/messages", since=t1)
sent = call[0]["body"] if call else ""
check("Modele ad GÖNDERİLMEDİ (takma ad 'Çalışan')", sent and "Ayşe" not in json.loads(sent)["messages"][0]["content"] and "Yılmaz" not in sent
      and "Çalışan" in json.loads(sent)["messages"][0]["content"], sent[:400])

t2 = time.time()
code, r = api("ayse", "POST", f"{A}/assistant", {"question": "Uzaktan çalışma politikası nedir?"})
check("İK asistanı modelden yanıtladı", code == 200 and r["source"] == "llm" and "Uzaktan çalışma" in r["related"], r)
call = wait_for("/anthropic/v1/messages", since=t2)
check("Asistan: bilgi bankası makalesi bağlam olarak gitti, 'yalnızca bilgi bankası' talimatı var",
      bool(call) and "BİLGİ BANKASI" in json.loads(call[0]["body"])["system"] and "Uzaktan" in json.loads(call[0]["body"])["system"], call[:1])
code, r = api("ayse", "POST", "/api/governance/insights/assistant", {"question": "Uzaktan çalışmada kaç gün ofise gelmeliyim?"})
check("Asistan penceresi (insights) yapay zekâyı kullanır", code == 200 and r["source"] == "llm", r)
code, r = api("ayse", "POST", "/api/governance/insights/assistant", {"question": "izin bakiyem ne kadar"})
check("Kişisel veri soruları yine deterministik (modele gitmez)", code == 200 and r["source"] == "data", r)

code, s = api("admin", "GET", f"{A}/settings")
tasks = {u["task"]: u for u in s["usage"] or []}
check("Kullanım kaydı: görev bazında çağrı ve jeton (içerik yok)", {"job-draft", "perf-summary", "assistant", "inclusive-rewrite"} <= set(tasks)
      and tasks["job-draft"]["outputTokens"] > 0, s["usage"])

# kota (testte 1 dakikada 20 çağrı)
hit = None
for _ in range(s["hourlyLimit"] + 2):
    code, r = api("mehmet", "POST", f"{A}/inclusive-rewrite", {"text": "metin"})
    if code == 429:
        hit = r
        break
check("Saatlik kota aşılınca 429", hit is not None and hit["code"] == "llm_rate_limited", hit)

api("admin", "PUT", f"{A}/settings", {"enabled": False, "allowPersonalData": False})
print(f"\nFAILS: {len(FAIL)}")
for f in FAIL:
    print("  -", f)
sys.exit(1 if FAIL else 0)

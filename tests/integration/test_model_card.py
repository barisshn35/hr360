#!/usr/bin/env python3
"""G5: devir riski modeli - model kartı, küresel önem (ortalama |SHAP|), veri kayması (PSI,
Prometheus hr360_model_feature_psi), yeniden eğitim yetkisi ve governance uyumluluğu.

Ön koşul: HR360 çalışıyor (ml-inference + MLflow). Yeniden eğitim yalnızca dry_run ile
çağrılır: aday MLflow'a "promoted=false" olarak yazılır, yayındaki sürüm değişmez.
"""
import json
import os
import random
import subprocess
import sys

sys.path.insert(0, os.path.dirname(__file__))
from common import AYSE, FAIL, api, check, http  # noqa: E402

ROOT = os.path.join(os.path.dirname(__file__), "..", "..")
M = "/ml/model"
FAIL.clear()
FEATURES = ["tenure_years", "compa_ratio", "last_rating", "months_since_promotion", "overtime_hours_month", "training_hours_year"]


def metrics():
    code = ("import urllib.request;print(urllib.request.urlopen('http://localhost:8000/metrics').read().decode())")
    out = subprocess.run(["docker", "compose", "exec", "-T", "ml-inference", "python", "-c", code],
                         capture_output=True, text=True, cwd=ROOT)
    return out.stdout


# ---------------------------------------------------------------------- model kartı
code, card = api("admin", "GET", f"{M}/card")
check("Model kartı İK'ya açık", code == 200 and card.get("loaded") and card.get("serving"), (code, card))
card = card if code == 200 else {}
check("Kart: sürüm, eğitim tarihi, veri kaynağı ve satır sayısı", all(card.get(k) for k in ("version", "trained_at", "training_source", "training_rows")), card)
check("Kart: metrikler (AUC, doğruluk)", (card.get("metrics") or {}).get("auc", 0) > 0.6, card.get("metrics"))
check("Kart: özellik listesi 6 iş özelliği, giriş sırasıyla", [f["name"] for f in card.get("features", [])] == FEATURES, card.get("features"))
excluded = {a["key"] for a in card.get("excluded_attributes", [])}
check("Kart: cinsiyet, yaş, medeni durum, sağlık bilerek dışlanmış", {"gender", "age", "marital_status", "health"} <= excluded, excluded)
check("Kart: dışlanan nitelik denetimi geçti", (card.get("excluded_check") or {}).get("passed") is True, card.get("excluded_check"))
check("Kart: adalet notu, sınırlamalar ve veri dönemi bilgisi",
      bool((card.get("fairness") or {}).get("note")) and len(card.get("limitations", [])) >= 2
      and (card.get("data_window") or card.get("data_window_note")), card.get("fairness"))
check("Kart: referans histogramları kartta yok", "reference" not in card)
check("Kart: çalışana kapalı", api("ayse", "GET", f"{M}/card")[0] == 403)
check("Kart: oturumsuz kapalı", http("GET", f"{M}/card")[0] == 401)

# ---------------------------------------------------------------------- küresel önem
code, imp = api("admin", "GET", f"{M}/importance")
check("Küresel önem: ortalama |SHAP|", code == 200 and imp.get("method") == "mean_abs_shap", (code, imp))
if code == 200:
    names = [f["feature"] for f in imp["features"]]
    values = [f["value"] for f in imp["features"]]
    check("Küresel önem: 6 özellik, azalan sıralı, paylar toplamı 1", sorted(names) == sorted(FEATURES)
          and values == sorted(values, reverse=True) and abs(sum(f["share"] for f in imp["features"]) - 1) < 0.01, imp)
check("Küresel önem: çalışana kapalı", api("ayse", "GET", f"{M}/importance")[0] == 403)

# ---------------------------------------------------------------------- kayma (toplu satırlar)
random.seed(7)
rows = [{"tenure_years": round(random.gammavariate(2, 2.5), 2), "compa_ratio": round(min(2, max(0.3, random.gauss(0.8, 0.1))), 3),
         "last_rating": random.choice([1, 2, 3, 4, 5]), "months_since_promotion": round(random.gammavariate(2, 12), 1),
         "overtime_hours_month": round(min(200, random.gammavariate(1.5, 8) + 40), 1), "training_hours_year": round(random.gammavariate(2, 10), 1)}
        for _ in range(200)]
code, rep = api("admin", "POST", f"{M}/drift", {"rows": rows})
check("Kayma: toplu satırlarla PSI hesaplanır", code == 200 and rep.get("rows") == 200 and len(rep.get("features", [])) == 6, (code, rep))
sig = set((rep or {}).get("significant_features") or [])
check("Kayma: kaydırılmış özellikler PSI > 0,2 ile işaretlenir", {"overtime_hours_month", "compa_ratio"} <= sig, rep and rep.get("features"))
check("Kayma: kimlik sütunu reddedilir", api("admin", "POST", f"{M}/drift", {"rows": [dict(r, employee_id="x") for r in rows[:40]]})[0] == 422)
check("Kayma: çalışana kapalı", api("ayse", "POST", f"{M}/drift", {"rows": rows})[0] == 403)
code, last = api("admin", "GET", f"{M}/drift/last")
check("Kayma: son ölçüm kiracı için saklanır", code == 200 and last.get("available") and last.get("max_psi") == rep.get("max_psi"), last)
m = metrics()
psi_lines = [l for l in m.splitlines() if l.startswith("hr360_model_feature_psi{")]
over = [l for l in psi_lines if 'feature="overtime_hours_month"' in l]
check("Prometheus: hr360_model_feature_psi{feature} yayımlanır", len(psi_lines) == 6 and over and float(over[0].split()[-1]) > 0.2, psi_lines)

# ---------------------------------------------------------------------- governance uyumu + son tahminler
code, before = api("admin", "GET", f"{M}/drift/recent")
check("Son tahminlerden kayma ucu", code == 200 and "available" in before and "rows" in before, before)
code, res = api("admin", "POST", f"/api/governance/privacy/analysis/attrition/{AYSE}", {"features": [3, 1.0, 3, 12, 8, 20]})
check("Governance devir riski ucu değişmeden çalışır (tahmin + açıklama)",
      (code == 200 and res["prediction"]["prediction"] in (0, 1) and len(res["prediction"]["probability"]) == 2
       and res["explanation"] and len(res["explanation"]["feature_contributions"]) == 6) or (code == 409 and res.get("code") == "objection"),
      (code, res))
if code == 200:
    check("Açıklama özellik adlarını da döner", res["explanation"].get("feature_names") == FEATURES, res["explanation"])
    code, after = api("admin", "GET", f"{M}/drift/recent")
    check("Tahmin yalnızca toplu histograma sayılır (kayıt +1)", code == 200 and after["rows"] == before["rows"] + 1
          and after.get("model_version") == before.get("model_version"), (before, after))
check("Gateway: /ml/predict kapalı kalır", api("admin", "POST", "/ml/predict", {"features": [1] * 6})[0] == 404)
check("Gateway: /ml/explain kapalı kalır", api("admin", "POST", "/ml/explain", {"features": [1] * 6})[0] == 404)

# ---------------------------------------------------------------------- yeniden eğitim
body = {"source": "synthetic", "synthetic": {"n": 1500}, "dry_run": True,
        "data_window": {"start": "2026-01-01", "end": "2026-06-30"}}
check("Yeniden eğitim: çalışana kapalı", api("ayse", "POST", f"{M}/retrain", body)[0] == 403)
X = [dict(r, label=random.randint(0, 1)) for r in rows]
check("Yeniden eğitim: kiracı İK'sı satır verisiyle eğitemez (paylaşılan model)",
      api("admin", "POST", f"{M}/retrain", {"source": "rows", "rows": X, "dry_run": True})[0] == 403)
code, rt = api("admin", "POST", f"{M}/retrain", body)
check("Yeniden eğitim (İK, sentetik, dry_run): aday kaydedildi, yayımlanmadı",
      code == 200 and rt["promoted"] is False and rt["serving_version"] == card.get("version") and rt["candidate"]["auc"] > 0.6, (code, rt))
if code == 200:
    a = rt.get("audit") or {}
    check("Yeniden eğitim: denetim yükü döner (governance yazar)", a.get("entityType") == "AttritionModel" and a.get("tenant") == "demo"
          and a.get("action") == "ModelRetrainRejected" and a["changes"]["data_window"] == {"start": "2026-01-01", "end": "2026-06-30"}, a)
code, card2 = api("admin", "GET", f"{M}/card")
check("Yeniden eğitim sonrası yayındaki sürüm değişmedi", code == 200 and card2.get("version") == card.get("version"), card2.get("version"))


# Governance üzerinden yeniden eğitim: özet denetim kaydına yazılır.
import subprocess as _sp
code, gr = api("admin", "POST", "/api/governance/model/retrain", {"dryRun": True})
check("Governance üzerinden yeniden eğitim (dry_run)", code == 200 and gr.get("promoted") is False, (code, str(gr)[:200]))
_out = _sp.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", "hr360_operational", "-Atc",
                "SELECT count(*) FROM audit_log WHERE \"EntityType\" = 'AttritionModel' AND \"OccurredAt\" > now() - interval '10 minutes'"],
               capture_output=True, text=True, cwd=__import__("os").path.join(__import__("os").path.dirname(__file__), "..", "..")).stdout.strip()
check("Yeniden eğitim denetim kaydında", _out.isdigit() and int(_out) >= 1, _out)
code, _ = api("ayse", "POST", "/api/governance/model/retrain", {"dryRun": True})
check("Çalışan modeli yeniden eğitemez", code == 403, code)

# ---------------------------------------------------------------------- ML dalgası 2
# Servisler arası ML uçları gateway'den kapalı; erişim yetki ve denetim kaydı olan servis uçlarından.
for path in ("/ml/payroll/anomaly", "/ml/compensation/pay-equity", "/ml/semantic/search", "/ml/text/topics", "/ml/skills/graph"):
    check(f"Gateway: {path} kapalı", api("admin", "POST", path, {})[0] == 404)
code, r = api("ayse", "GET", "/api/governance/search/semantic?q=y%C4%B1ll%C4%B1k%20izin%20hakk%C4%B1m")
check("Anlamsal arama: çalışan bilgi bankasında arar", code == 200 and isinstance(r.get("hits"), list), (code, str(r)[:200]))
code, sk = api("admin", "POST", "/ml/skills/extract", {"text": "k8s ve csharp deneyimi", "catalog": []})
check("Beceri çıkarımı (eş anlamlılar)", code == 200 and {"kubernetes", "c#"} <= {s["key"] for s in sk.get("skills", [])}, (code, sk))
code, _ = api("ayse", "GET", "/api/compensation/compensation/analytics/pay-equity")
check("Yetki: çalışan ücret adaleti analizini açamaz", code == 403, code)
code, _ = api("ayse", "GET", "/api/governance/skills/graph")
check("Yetki: çalışan beceri haritasını açamaz", code == 403, code)
code, _ = api("ayse", "GET", "/api/governance/analytics/leave-forecast")
check("Yetki: çalışan izin tahminini açamaz", code == 403, code)
code, lf = api("mehmet", "GET", "/api/governance/analytics/leave-forecast?weeks=8")
check("İzin tahmini (yönetici): yanıt ya da 'veri yok' bildirimi", code == 200 and "available" in lf, (code, str(lf)[:200]))

print(f"\nFAILS: {len(FAIL)}")
for f in FAIL:
    print("  -", f)
sys.exit(1 if FAIL else 0)

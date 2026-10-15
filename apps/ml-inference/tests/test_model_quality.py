"""Devir riski modeli kalite katmani: kalibrasyon, esik tablosu, champion/challenger,
adillik denetimi, tahmin dagilimi kaymasi (PSI) ve sade dilde aciklamalar.

MLflow ve SHAP gerektirmez (dosya tabanli sahte depo).
"""

import asyncio
from datetime import date

import numpy as np
import pytest
from fastapi import FastAPI
from fastapi.testclient import TestClient

import attrition_ml as aml
import model_quality as mq
from model_routes import ModelService, build_router
from model_store import LocalModelStore
from test_attrition_model import fake_verify, h


@pytest.fixture()
def env(monkeypatch):
    monkeypatch.delenv("INTERNAL_SERVICE_TOKEN", raising=False)
    monkeypatch.delenv("MODEL_PROMOTION_REQUIRES_APPROVAL", raising=False)
    store = LocalModelStore()
    service = ModelService(store, "hr360-attrition-risk", explainer_factory=None, bootstrap=True, bootstrap_rows=2000)
    asyncio.run(service.load(attempts=1, delay=0))
    app = FastAPI()
    app.include_router(build_router(fake_verify, service))
    return TestClient(app), service, store


# ------------------------------------------------------------------ kalibrasyon

def test_calibration_improves_overconfident_scores():
    rng = np.random.default_rng(0)
    true_p = rng.uniform(0.05, 0.6, 4000)
    y = (rng.random(4000) < true_p).astype(int)
    raw = np.clip(true_p ** 0.4, 0, 1)  # asiri emin, monoton bozulmus skor
    cal = mq.fit_calibration(raw[:2000], y[:2000], seed=1)
    assert cal["method"] in ("sigmoid", "isotonic")
    assert set(cal["selection"]) == {"none", "sigmoid", "isotonic"}
    after = mq.apply_calibration(raw[2000:], cal)
    assert mq.brier(after, y[2000:]) < mq.brier(raw[2000:], y[2000:])
    assert ((after >= 0) & (after <= 1)).all()


def test_calibration_falls_back_on_tiny_or_single_class():
    assert mq.fit_calibration([0.2] * 10, [0] * 10)["method"] == "none"
    assert mq.fit_calibration(np.linspace(0, 1, 100), [1] * 100)["method"] == "none"
    raw = np.array([0.1, 0.7])
    assert np.allclose(mq.apply_calibration(raw, None), raw)
    assert np.allclose(mq.apply_calibration(raw, {"method": "none"}), raw)


def test_reliability_suppresses_small_bins_and_ece():
    p = np.array([0.05] * 50 + [0.95] * 3)
    y = np.array([0] * 50 + [1] * 3)
    bins, ece = mq.reliability(p, y)
    assert len(bins) == 10
    assert bins[0]["count"] == 50 and bins[0]["observed_rate"] == 0.0
    last = bins[-1]
    assert last["count"] == 3 and last["observed_rate"] is None and last["suppressed"] is True
    assert ece == pytest.approx((50 * 0.05 + 3 * 0.05) / 53, abs=1e-4)


def test_threshold_table_monotonic():
    rng = np.random.default_rng(3)
    p = rng.random(1000)
    y = (rng.random(1000) < p).astype(int)
    table = mq.threshold_table(p, y)
    assert [r["threshold"] for r in table][:3] == [0.05, 0.1, 0.15]
    flag_rates = [r["flag_rate"] for r in table]
    recalls = [r["recall"] for r in table]
    assert flag_rates == sorted(flag_rates, reverse=True) and recalls == sorted(recalls, reverse=True)
    assert 0.05 <= mq.recommended_threshold(table) <= 0.95


def test_retrain_stores_calibration_and_card_summary(env):
    client, service, _ = env
    q = service.state.meta["quality"]
    assert q["method"] in ("none", "sigmoid", "isotonic")
    assert len(q["reliability"]) == 10 and len(q["thresholds"]) == len(mq.THRESHOLDS)
    assert 0 <= q["brier"] <= 0.25 and 0 <= q["ece"] <= 1
    r = client.get("/model/calibration", headers=h("hr"))
    assert r.status_code == 200 and r.json()["available"] is True
    assert client.get("/model/calibration", headers=h("employee")).status_code == 403
    card = client.get("/model/card", headers=h("hr")).json()
    assert card["calibration"]["method"] == q["method"] and "thresholds" not in card["calibration"]
    assert card["approval_required"] is True
    f = card["freshness"]
    assert f["validity_days"] == 180 and f["stale"] is False and f["age_days"] == 0


# ------------------------------------------------------------------ champion / challenger

def test_challenger_needs_approval_then_rollback(env):
    client, service, store = env
    body = {"source": "synthetic", "synthetic": {"n": 2500, "seed": 17}}
    r = client.post("/model/retrain", json=body, headers=h("platform")).json()
    assert r["awaiting_approval"] is True and service.state.version == "1"
    assert r["audit"]["action"] == "ModelChallengerRegistered"
    assert r["candidate"]["brier"] is not None and r["current"]["brier"] is not None  # ayni kumede
    v = client.get("/model/versions", headers=h("hr")).json()
    rows = {x["version"]: x for x in v["versions"]}
    assert v["champion"] == "1" and rows["2"]["status"] == "challenger" and rows["2"]["comparison_current"] is True
    # Kiraci IK'si ve calisan yayina alamaz.
    assert client.post("/model/promote", json={"version": "2"}, headers=h("hr")).status_code == 403
    assert client.post("/model/promote", json={"version": "2"}, headers=h("employee")).status_code == 403
    assert client.post("/model/promote", json={"version": "1"}, headers=h("platform")).status_code == 409
    assert client.post("/model/promote", json={"version": "9"}, headers=h("platform")).status_code == 404
    r = client.post("/model/promote", json={"version": "2"}, headers=h("platform"))
    assert r.status_code == 200 and service.state.version == "2" and r.json()["previous_version"] == "1"
    assert store.tags["1"]["hr360.status"] == "retired"
    # Geri alma: yalnizca daha once yayinda olmus surume.
    r = client.post("/model/retrain", json={**body, "synthetic": {"n": 2500, "seed": 18}}, headers=h("platform")).json()
    cand = r["candidate_version"]
    assert client.post("/model/rollback", json={"version": cand}, headers=h("platform")).status_code == 409
    r = client.post("/model/rollback", json={"version": "1"}, headers=h("platform"))
    assert r.status_code == 200 and service.state.version == "1"
    assert r.json()["audit"]["action"] == "ModelRolledBack"
    # Aday, artik yayindaki surumle karsilastirilmadigi icin yayina alinamaz.
    assert client.post("/model/promote", json={"version": cand}, headers=h("platform")).status_code == 409


def test_approval_can_be_disabled(env, monkeypatch):
    client, service, _ = env
    monkeypatch.setenv("MODEL_PROMOTION_REQUIRES_APPROVAL", "false")
    r = client.post("/model/retrain", json={"source": "synthetic", "synthetic": {"n": 2500, "seed": 19}},
                    headers=h("platform")).json()
    assert r["promoted"] is True and r["awaiting_approval"] is False and service.state.version == "2"


def test_version_request_validation(env):
    client, _, _ = env
    assert client.post("/model/promote", json={"version": "1; drop"}, headers=h("platform")).status_code == 422


# ------------------------------------------------------------------ adillik

def test_fairness_report_suppresses_small_groups_and_flags_four_fifths():
    scores = [0.9] * 10 + [0.1] * 10 + [0.9] * 4 + [0.1] * 6 + [0.9] * 3
    groups = {"department": ["A"] * 10 + ["B"] * 10 + ["C"] * 10 + ["Gizli-Ekip"] * 3}
    rep = mq.fairness_report(scores, 0.5, groups)
    dep = rep["attributes"][0]
    names = [g["group"] for g in dep["groups"]]
    assert names == ["A", "B", "C"] and dep["hidden_groups"] == 1  # n=3 grup gizli, adi yok
    by = {g["group"]: g for g in dep["groups"]}
    assert by["A"]["flag_rate"] == 1.0 and by["A"]["disparity_ratio"] == 1.0
    assert by["C"]["disparity_ratio"] == 0.4 and by["C"]["four_fifths_warning"] is True
    assert {w["group"] for w in rep["warnings"]} == {"B", "C"}
    assert "Gizli-Ekip" not in str(rep)


def test_fairness_tpr_fpr_only_with_enough_labels():
    scores = [0.8] * 6 + [0.2] * 6
    labels = [1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 1]
    rep = mq.fairness_report(scores, 0.5, {"tenure_band": ["1-3"] * 12}, labels)
    g = rep["attributes"][0]["groups"][0]
    assert g["tpr"] == round(5 / 6, 4) and g["fpr"] == round(1 / 6, 4)
    rep = mq.fairness_report(scores, 0.5, {"tenure_band": ["1-3"] * 12}, [1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0])
    assert rep["attributes"][0]["groups"][0]["tpr"] is None  # 2 ayrilan < 5


def test_fairness_endpoint(env):
    client, _, _ = env
    X, y = aml.synthetic_dataset(200, 4)
    rows = []
    for i, (x, label) in enumerate(zip(X, y)):
        r = dict(zip(aml.FEATURE_NAMES, map(float, x)))
        r["label"] = int(label)
        r["groups"] = {"department": "Mühendislik" if i % 2 else "Satış", "tenure_band": mq.tenure_band(float(x[0]))}
        rows.append(r)
    r = client.post("/model/fairness", json={"rows": rows, "threshold": 0.4}, headers=h("hr"))
    assert r.status_code == 200, r.text
    rep = r.json()
    assert rep["rows"] == 200 and rep["threshold"] == 0.4 and rep["labels_used"] is True
    assert {a["attribute"] for a in rep["attributes"]} == {"department", "tenure_band"}
    assert {a["attribute"] for a in rep["absent_attributes"]} == {"age_band", "gender"}
    # Kimlik ya da bilinmeyen grup niteligi reddedilir.
    bad = [dict(rows[0], employee_id="x")] + rows[1:]
    assert client.post("/model/fairness", json={"rows": bad}, headers=h("hr")).status_code == 422
    bad = [dict(rows[0], groups={"religion": "x"})] + rows[1:]
    assert client.post("/model/fairness", json={"rows": bad}, headers=h("hr")).status_code == 422
    assert client.post("/model/fairness", json={"rows": rows}, headers=h("employee")).status_code == 403


def test_group_attributes_are_not_model_features():
    # Denetim grup nitelikleri (cinsiyet, yas bandi) ozellik listesine girerse egitim reddedilir.
    assert not aml.check_features(list(aml.FEATURE_NAMES) + ["age_band"])["passed"]
    assert not aml.check_features(list(aml.FEATURE_NAMES) + ["gender"])["passed"]


def test_tenure_band():
    assert [mq.tenure_band(v) for v in (0.5, 1, 4.9, 7, 12)] == ["0-1", "1-3", "3-5", "5-10", "10+"]
    assert mq.tenure_band(None) is None and mq.tenure_band(-1) is None


# ------------------------------------------------------------------ tahmin dagilimi kaymasi

def test_prediction_psi_and_daily_gauges(env):
    from prometheus_client import REGISTRY
    client, service, _ = env
    assert service.state.meta["prediction_reference"]["edges"] == list(mq.SCORE_EDGES)
    X, _ = aml.synthetic_dataset(80, 51, shift={"overtime_hours_month": 60, "compa_ratio": -0.3})
    _, cal = service.scores(X)
    for x, p in zip(X, cal):
        service.record_prediction("demo", x, float(p))
    r = client.get("/model/drift/recent", headers=h("hr")).json()
    assert r["available"] and r["prediction_psi"] > 0.2 and r["prediction_level"] == "significant"
    result = service.daily_drift()
    assert result["measured"] == 1 and result["prediction_psi"] == r["prediction_psi"]
    assert REGISTRY.get_sample_value("hr360_ml_prediction_psi") == r["prediction_psi"]
    assert REGISTRY.get_sample_value("hr360_ml_feature_psi", {"feature": "overtime_hours_month"}) > 0.2
    # Skor sayaci yalnizca kova sayisi tutar.
    version, counts = service.recent_scores["demo"]
    assert version == service.state.version and sum(counts) == 80 and all(isinstance(c, int) for c in counts)


def test_batch_drift_includes_prediction_psi(env):
    client, _, _ = env
    X, _ = aml.synthetic_dataset(300, 61)
    rows = [dict(zip(aml.FEATURE_NAMES, map(float, x))) for x in X]
    rep = client.post("/model/drift", json={"rows": rows}, headers=h("hr")).json()
    assert rep["prediction_psi"] < 0.1 and rep["status"] == "stable"


# ------------------------------------------------------------------ sade dilde aciklama + guncellik

def test_explain_reasons_plain_language():
    names = list(aml.FEATURE_NAMES)
    values = [1.0, 0.82, 3, 26, 30, 4]
    contributions = [0.05, 0.12, 0.0, 0.09, -0.03, 0.01]
    reasons = mq.explain_reasons(names, values, contributions)
    assert [r["code"] for r in reasons] == ["pay_below", "no_promotion", "tenure_short"]
    assert reasons[0]["text"] == "Ücreti bant ortasının altında (%82) (+)" and reasons[0]["params"] == [82]
    assert reasons[1]["text"] == "Son 26 aydır terfi ya da unvan değişikliği yok (+)"
    assert all(r["direction"] == "up" for r in reasons)
    down = mq.explain_reasons(names, values, [0, 0, 0, 0, -0.2, 0], top=3)
    assert len(down) == 1 and down[0]["direction"] == "down" and down[0]["text"].endswith("(-)")


def test_freshness():
    f = mq.freshness("2026-01-01T10:00:00+00:00", None, 180, today=date(2026, 8, 1))
    assert f["stale"] is True and f["expires_on"] == "2026-06-30" and f["age_days"] == 212
    f = mq.freshness("2026-07-01T10:00:00+00:00", {"start": "2026-01-01", "end": "2026-06-30"}, 180,
                     today=date(2026, 8, 1))
    assert f["stale"] is False and f["days_left"] == 149 and f["data_range"]["end"] == "2026-06-30"
    assert mq.freshness(None, None, 180)["stale"] is None

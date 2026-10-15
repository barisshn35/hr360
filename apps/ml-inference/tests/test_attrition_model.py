"""Devir riski modeli: kart, yeniden egitim/terfi karari ve PSI kayma olcumu.

MLflow ve SHAP gerektirmez (dosya tabanli sahte depo). Calistirma:
    cd apps/ml-inference && python -m pytest -q tests
"""

import asyncio

import numpy as np
import pytest
from fastapi import FastAPI, Header, HTTPException
from fastapi.testclient import TestClient

import attrition_ml as aml
from model_routes import ModelService, build_router
from model_store import LocalModelStore

ROLES = {
    "hr": {"active": True, "realm_access": {"roles": ["hr-admin"]}, "organization": {"demo": {}}},
    "other-hr": {"active": True, "realm_access": {"roles": ["tenant-admin"]}, "organization": {"acme": {}}},
    "employee": {"active": True, "realm_access": {"roles": ["employee"]}, "organization": {"demo": {}}},
    "platform": {"active": True, "realm_access": {"roles": ["platform-admin"]}},
}


async def fake_verify(authorization: str = Header(None)):
    if not authorization or not authorization.startswith("Bearer "):
        raise HTTPException(status_code=401, detail="Missing bearer token")
    who = authorization.split(" ", 1)[1]
    if who not in ROLES:
        raise HTTPException(status_code=401, detail="Invalid")
    return ROLES[who]


def h(who, **extra):
    return {"Authorization": f"Bearer {who}", **extra}


@pytest.fixture()
def env(monkeypatch):
    monkeypatch.delenv("INTERNAL_SERVICE_TOKEN", raising=False)
    monkeypatch.delenv("INTERNAL_SERVICE_TOKEN_PREVIOUS", raising=False)
    store = LocalModelStore()
    service = ModelService(store, "hr360-attrition-risk", explainer_factory=None, bootstrap=True, bootstrap_rows=2000)
    asyncio.run(service.load(attempts=1, delay=0))
    app = FastAPI()
    app.include_router(build_router(fake_verify, service))
    return TestClient(app), service, store


# ------------------------------------------------------------------ saf islevler

def test_feature_list_contains_no_excluded_attribute():
    check = aml.check_features(aml.FEATURE_NAMES)
    assert check["passed"], check
    keys = {k for k, _ in aml.EXCLUDED_ATTRIBUTES}
    assert keys == {"gender", "age", "marital_status", "health", "religion", "ethnicity", "union_membership"}


@pytest.mark.parametrize("bad,attr", [
    ("gender_code", "gender"), ("age_band", "age"), ("Yaş", "age"), ("birth_year", "age"),
    ("marital_status", "marital_status"), ("medeni_durum", "marital_status"), ("sick_days", "health"),
    ("sağlık_raporu", "health"), ("religion", "religion"), ("ethnic_group", "ethnicity"),
    ("sendika_uyesi", "union_membership"), ("union_member", "union_membership"), ("Cinsiyet", "gender"),
])
def test_check_detects_excluded_attributes(bad, attr):
    check = aml.check_features(list(aml.FEATURE_NAMES) + [bad])
    assert not check["passed"]
    assert check["violations"][0]["attribute"] == attr


def test_check_has_no_false_positive_on_similar_words():
    assert aml.check_features(["average_overtime", "manager_rating", "page_views", "usage_hours"])["passed"]


def test_psi_low_for_same_distribution_high_for_shift():
    X_ref, _ = aml.synthetic_dataset(4000, 1)
    ref = aml.reference_histograms(X_ref)
    same, _ = aml.synthetic_dataset(1500, 2)
    shifted, _ = aml.synthetic_dataset(1500, 3, shift={"overtime_hours_month": 25, "compa_ratio": -0.2})
    r1 = aml.drift_report(ref, same)
    r2 = aml.drift_report(ref, shifted)
    assert r1["status"] == "stable" and r1["max_psi"] < 0.1, r1
    by = {f["feature"]: f for f in r2["features"]}
    assert by["overtime_hours_month"]["psi"] > 0.2 and by["overtime_hours_month"]["level"] == "significant"
    assert by["compa_ratio"]["level"] == "significant"
    assert by["tenure_years"]["level"] == "stable"
    assert set(r2["significant_features"]) == {"overtime_hours_month", "compa_ratio"}


def test_psi_identical_is_zero():
    assert aml.psi([0.2, 0.3, 0.5], [0.2, 0.3, 0.5]) == pytest.approx(0.0, abs=1e-9)


def test_promotion_rule():
    assert aml.promotion_decision({"auc": 0.70}, None)["promote"]
    assert aml.promotion_decision({"auc": 0.79}, {"auc": 0.80})["promote"]
    assert aml.promotion_decision({"auc": 0.781}, {"auc": 0.80})["promote"]
    assert not aml.promotion_decision({"auc": 0.77}, {"auc": 0.80})["promote"]


def test_reference_histograms_hold_no_rows():
    X, _ = aml.synthetic_dataset(1000, 5)
    ref = aml.reference_histograms(X)
    for spec in ref.values():
        assert set(spec) == {"edges", "proportions", "mean", "std"}
        assert len(spec["proportions"]) == len(spec["edges"]) + 1
        assert abs(sum(spec["proportions"]) - 1) < 1e-3


# ------------------------------------------------------------------ uclar

def test_bootstrap_and_card(env):
    client, service, _ = env
    assert service.state.version == "1"
    r = client.get("/model/card", headers=h("hr"))
    assert r.status_code == 200
    card = r.json()
    assert card["loaded"] and card["version"] == "1"
    assert card["training_source"] == "synthetic" and card["training_rows"] == 2000
    assert card["metrics"]["auc"] > 0.7
    assert card["excluded_check"]["passed"] is True
    assert [f["name"] for f in card["features"]] == list(aml.FEATURE_NAMES)
    assert {a["key"] for a in card["excluded_attributes"]} >= {"gender", "age", "religion", "union_membership"}
    assert "reference" not in card  # histogramlar kartta degil


def test_unloadable_champion_keeps_service_up_and_retrain_replaces_it(monkeypatch):
    """Kutuphane yukseltmesinden sonra yayindaki surumun dosyasi acilamazsa servis ayakta kalir
    (tahmin 503, kartta/health'te neden), sentetik ilk egitime DUSMEZ; yeniden egitim yeni surumu yayimlar."""
    from model_store import ModelLoadError

    monkeypatch.delenv("INTERNAL_SERVICE_TOKEN", raising=False)
    store = LocalModelStore()
    service = ModelService(store, "hr360-attrition-risk", explainer_factory=None, bootstrap=True, bootstrap_rows=2000)
    asyncio.run(service.load(attempts=1, delay=0))
    assert service.state.version == "1"

    def broken():
        raise ModelLoadError("v1: ValueError: uyumsuz dosya")

    monkeypatch.setattr(store, "load_current", broken)
    fresh = ModelService(store, "hr360-attrition-risk", explainer_factory=None, bootstrap=True, bootstrap_rows=2000)
    asyncio.run(fresh.load(attempts=2, delay=0))
    assert fresh.state.model is None and "uyumsuz" in fresh.state.load_error
    assert len(store.models) == 1  # sessizce yeni surum egitilmedi
    app = FastAPI()
    app.include_router(build_router(fake_verify, fresh))
    client = TestClient(app)
    card = client.get("/model/card", headers=h("hr")).json()
    assert card["loaded"] is False and "uyumsuz" in card["load_error"]
    r = client.post("/model/retrain", json={"source": "synthetic", "synthetic": {"n": 1000, "seed": 3}},
                    headers=h("platform"))
    assert r.status_code == 200, r.text
    assert r.json()["promoted"] is True and fresh.state.version == "2" and fresh.state.load_error is None


def test_card_requires_hr(env):
    client, _, _ = env
    assert client.get("/model/card").status_code == 401
    assert client.get("/model/card", headers=h("employee")).status_code == 403


def test_retrain_promotes_good_and_rejects_worse(env):
    client, service, store = env
    r = client.post("/model/retrain", json={"source": "synthetic", "synthetic": {"n": 3000, "seed": 7}},
                    headers=h("platform"))
    assert r.status_code == 200, r.text
    good = r.json()
    # Champion/challenger: aday karsilastirmayi gecti ama insan onayi olmadan yayina girmez.
    assert good["promoted"] is False and good["awaiting_approval"] is True and good["candidate_version"] == "2"
    assert good["decision"]["recommended"] is True
    assert good["current"]["auc"] is not None and good["candidate"]["auc"] > 0.7
    assert service.state.version == "1" and store.tags["2"]["hr360.status"] == "challenger"
    r = client.post("/model/promote", json={"version": "2"}, headers=h("platform"))
    assert r.status_code == 200, r.text
    assert service.state.version == "2" and r.json()["audit"]["action"] == "ModelPromotionApproved"

    r = client.post("/model/retrain", json={"source": "synthetic", "synthetic": {"n": 3000, "seed": 8, "label_noise": 1.0}},
                    headers=h("platform"))
    bad = r.json()
    assert r.status_code == 200
    assert bad["promoted"] is False
    assert bad["decision"]["auc_delta"] < -0.02
    assert bad["candidate_version"] == "3" and store.promoted["3"] is False
    assert service.state.version == "2"  # yayindaki surum degismedi


def test_retrain_from_rows_and_dry_run(env):
    client, service, _ = env
    X, y = aml.synthetic_dataset(800, 11)
    rows = [dict(zip(aml.FEATURE_NAMES, map(float, x)), label=int(l)) for x, l in zip(X, y)]
    r = client.post("/model/retrain", json={"source": "rows", "rows": rows, "dry_run": True}, headers=h("platform"))
    assert r.status_code == 200, r.text
    body = r.json()
    assert body["promoted"] is False and body["evaluation"]["source"] == "provided-holdout"
    assert body["evaluation"]["rows"] == 200 and body["training"]["rows"] == 600
    assert service.state.version == "1"


def test_retrain_rejects_identifiers_and_sensitive_columns(env):
    client, _, _ = env
    X, y = aml.synthetic_dataset(300, 12)
    rows = [dict(zip(aml.FEATURE_NAMES, map(float, x)), label=int(l)) for x, l in zip(X, y)]
    rows[0]["employee_id"] = "8c7dd608"
    assert client.post("/model/retrain", json={"source": "rows", "rows": rows}, headers=h("platform")).status_code == 422
    rows[0].pop("employee_id")
    rows[0]["gender"] = 1
    assert client.post("/model/retrain", json={"source": "rows", "rows": rows}, headers=h("platform")).status_code == 422


def test_retrain_permissions(env, monkeypatch):
    client, _, _ = env
    body = {"source": "synthetic", "synthetic": {"n": 500, "seed": 1}, "dry_run": True}
    # IK rolleri sentetik veriyle yeniden egitebilir; calisan ve jetonsuz istek reddedilir.
    assert client.post("/model/retrain", json=body, headers=h("hr")).status_code == 200
    assert client.post("/model/retrain", json=body, headers=h("employee")).status_code == 403
    assert client.post("/model/retrain", json=body).status_code == 401
    # Kiraci verisiyle (satir/CSV) egitim paylasilan modeli degistirir: yalnizca platform ya da servis.
    X, y = aml.synthetic_dataset(400, 3)
    rows = [dict(zip(aml.FEATURE_NAMES, map(float, x)), label=int(l)) for x, l in zip(X, y)]
    rbody = {"source": "rows", "rows": rows, "dry_run": True}
    assert client.post("/model/retrain", json=rbody, headers=h("hr")).status_code == 403
    assert client.post("/model/retrain", json=rbody, headers=h("platform")).status_code == 200
    monkeypatch.setenv("INTERNAL_SERVICE_TOKEN", "s3cret")
    assert client.post("/model/retrain", json=rbody, headers=h("hr", **{"X-Internal-Token": "wrong"})).status_code == 403
    assert client.post("/model/retrain", json=rbody, headers=h("hr", **{"X-Internal-Token": "s3cret"})).status_code == 200
    assert client.post("/model/retrain", json=body, headers={"X-Internal-Token": "s3cret"}).status_code == 401
    # Anahtar degistirme penceresi: onceki anahtar (INTERNAL_SERVICE_TOKEN_PREVIOUS) da kabul edilir.
    monkeypatch.setenv("INTERNAL_SERVICE_TOKEN_PREVIOUS", "eski")
    assert client.post("/model/retrain", json=rbody, headers=h("hr", **{"X-Internal-Token": "eski"})).status_code == 200
    monkeypatch.setenv("INTERNAL_SERVICE_TOKEN_PREVIOUS", "")
    assert client.post("/model/retrain", json=rbody, headers=h("hr", **{"X-Internal-Token": "eski"})).status_code == 403
    assert client.post("/model/retrain", json=rbody, headers=h("hr", **{"X-Internal-Token": ""})).status_code == 403


def test_retrain_returns_audit_payload(env):
    client, _, _ = env
    r = client.post("/model/retrain", json={"source": "synthetic", "synthetic": {"n": 1500, "seed": 5},
                                            "data_window": {"start": "2025-01-01", "end": "2025-12-31"}},
                    headers=h("hr"))
    assert r.status_code == 200, r.text
    audit = r.json()["audit"]
    assert audit["action"] in ("ModelPromoted", "ModelRetrainRejected", "ModelChallengerRegistered")
    assert audit["entityType"] == "AttritionModel" and audit["entityId"].endswith("/v2")
    assert audit["tenant"] == "demo" and audit["actor"]["roles"] == ["hr-admin"]
    assert audit["changes"]["previous_version"] == "1" and audit["changes"]["data_window"]["end"] == "2025-12-31"
    card = client.get("/model/card", headers=h("hr")).json()
    if r.json()["promoted"]:
        assert card["data_window"] == {"start": "2025-01-01", "end": "2025-12-31"}


def test_tenant_hr_retrain_is_forced_to_dry_run(env, monkeypatch):
    client, service, store = env
    body = {"source": "synthetic", "synthetic": {"n": 3000, "seed": 7}, "dry_run": False}
    # Kiraci IK'si yayina alamaz: istek 403 degil, deneme egitimi olarak calisir ve yanitta bildirilir.
    r = client.post("/model/retrain", json=body, headers=h("hr"))
    assert r.status_code == 200, r.text
    res = r.json()
    assert res["promoted"] is False and res["dry_run"] is True and res["dry_run_only"] is True
    assert "deneme" in res["notice"].lower() and res["audit"]["changes"]["dry_run_forced"] is True
    assert service.state.version == "1" and store.promoted[res["candidate_version"]] is False
    # Platform yoneticisi ve servisler arasi cagri gercek egitim yapar; aday onay bekler (challenger).
    r = client.post("/model/retrain", json=body, headers=h("platform"))
    assert r.status_code == 200 and r.json()["awaiting_approval"] is True and r.json()["dry_run_only"] is False
    assert "notice" not in r.json()
    monkeypatch.setenv("INTERNAL_SERVICE_TOKEN", "s3cret")
    r = client.post("/model/retrain", json={**body, "synthetic": {"n": 3000, "seed": 9}},
                    headers=h("hr", **{"X-Internal-Token": "s3cret"}))
    assert r.status_code == 200 and r.json()["dry_run"] is False


# ------------------------------------------------------------------ dislanan nitelik korumasi

def test_train_model_refuses_forbidden_feature_names():
    X, y = aml.synthetic_dataset(600, 2)
    X7 = np.column_stack([X, np.zeros(len(X))])
    for bad in ("gender", "age", "marital_status", "health_score", "birth_date", "dogum_tarihi"):
        with pytest.raises(aml.ForbiddenFeatureError) as e:
            aml.train_model(X7, y, 1, list(aml.FEATURE_NAMES) + [bad])
        assert e.value.violations[0]["feature"] == bad
    assert aml.train_model(X, y, 1).n_features_in_ == 6  # izinli liste egitilir


def test_retrain_refuses_when_schema_gains_forbidden_feature(env, monkeypatch):
    client, service, _ = env
    monkeypatch.setattr(aml, "FEATURE_NAMES", aml.FEATURE_NAMES + ("employee_age",))
    r = client.post("/model/retrain", json={"source": "synthetic", "synthetic": {"n": 500, "seed": 1}}, headers=h("hr"))
    assert r.status_code == 422 and r.json()["detail"]["violations"][0]["attribute"] == "age"
    assert service.state.version == "1"


def test_prediction_guard_blocks_model_with_forbidden_feature(env):
    from fastapi import HTTPException as HE

    from model_store import LoadedModel
    _, service, _ = env
    model = service.state.model
    assert service.guard() is model
    service._activate(LoadedModel(model=model, version="9", meta={"features": list(aml.FEATURE_NAMES[:5]) + ["marital_status"]}))
    with pytest.raises(HE) as e:
        service.guard()
    assert e.value.status_code == 503
    card = service.card()
    assert card["serving"] is False and "marital_status" in card["blocked_reason"]


def test_retrain_from_csv(env):
    client, _, _ = env
    X, y = aml.synthetic_dataset(600, 31)
    header = ",".join(list(aml.FEATURE_NAMES) + ["label"])
    lines = [header] + [",".join(f"{v:.4f}" for v in x) + f",{int(l)}" for x, l in zip(X, y)]
    r = client.post("/model/retrain", json={"source": "csv", "csv": "\n".join(lines), "dry_run": True}, headers=h("platform"))
    assert r.status_code == 200, r.text
    assert r.json()["training"]["rows"] == 450 and r.json()["audit"]["changes"]["source"] == "csv"
    bad = "\n".join([header.replace("training_hours_year", "cinsiyet")] + lines[1:])
    r = client.post("/model/retrain", json={"source": "csv", "csv": bad}, headers=h("platform"))
    assert r.status_code == 422 and r.json()["detail"]["violations"][0]["attribute"] == "gender"
    extra = "\n".join([header + ",employee_id"] + [ln + ",x" for ln in lines[1:]])
    r = client.post("/model/retrain", json={"source": "csv", "csv": extra}, headers=h("platform"))
    assert r.status_code == 422 and r.json()["detail"]["unexpected"] == ["employee_id"]


# ------------------------------------------------------------------ kuresel onem

def test_importance_impurity_fallback(env):
    client, _, _ = env
    assert client.get("/model/importance", headers=h("employee")).status_code == 403
    r = client.get("/model/importance", headers=h("hr"))
    assert r.status_code == 200, r.text
    body = r.json()
    assert body["method"] == "impurity" and body["version"] == "1"
    assert {f["feature"] for f in body["features"]} == set(aml.FEATURE_NAMES)
    values = [f["value"] for f in body["features"]]
    assert values == sorted(values, reverse=True) and abs(sum(f["share"] for f in body["features"]) - 1) < 0.01


def test_importance_mean_abs_shap():
    shap = pytest.importorskip("shap")
    X, y = aml.synthetic_dataset(1500, 4)
    model = aml.train_model(X, y, 4)
    ref = aml.reference_histograms(X)
    sample = aml.sample_from_reference(ref, 200, seed=0)
    assert sample.shape == (200, 6)
    imp = aml.global_importance(model, sample, shap.TreeExplainer(model))
    assert imp["method"] == "mean_abs_shap" and imp["sample_rows"] == 200
    assert all(f["value"] >= 0 for f in imp["features"])
    # Etiket kurali ucret oranini ve kidemi agir kullanir; ikisi de ilk uce girmeli.
    top3 = [f["feature"] for f in imp["features"][:3]]
    assert "compa_ratio" in top3, top3


def test_sample_from_reference_matches_marginals():
    X, _ = aml.synthetic_dataset(4000, 8)
    ref = aml.reference_histograms(X)
    S = aml.sample_from_reference(ref, 4000, seed=1)
    assert aml.drift_report(ref, S)["max_psi"] < 0.1


# ------------------------------------------------------------------ son tahminlerden kayma

def test_recent_drift_from_aggregated_predictions(env):
    from prometheus_client import REGISTRY
    client, service, _ = env
    assert client.get("/model/drift/recent", headers=h("hr")).json()["available"] is False
    X, _ = aml.synthetic_dataset(60, 41, shift={"overtime_hours_month": 40})
    for x in X[:20]:
        service.record_prediction("demo", x)
    r = client.get("/model/drift/recent", headers=h("hr")).json()
    assert r == {"available": False, "rows": 20, "min_rows": 30, "model_version": "1"}
    for x in X[20:]:
        service.record_prediction("demo", x)
    r = client.get("/model/drift/recent", headers=h("hr")).json()
    assert r["available"] and r["rows"] == 60 and r["source"] == "recent-predictions"
    assert "overtime_hours_month" in r["significant_features"]
    assert REGISTRY.get_sample_value("hr360_model_feature_psi", {"feature": "overtime_hours_month"}) > 0.2
    # Baska kiracinin sayaci ayri; histogram ham deger/kimlik tutmaz.
    assert client.get("/model/drift/recent", headers=h("other-hr")).json()["available"] is False
    hist = service.recent["demo"]
    assert set(vars(hist)) == {"version", "edges", "counts", "sums", "rows"}
    assert all(isinstance(c, int) for cs in hist.counts.values() for c in cs)


def test_drift_endpoint_and_last_is_per_tenant(env):
    client, _, store = env
    assert client.get("/model/drift/last", headers=h("hr")).json() == {"available": False}
    X, _ = aml.synthetic_dataset(400, 21, shift={"overtime_hours_month": 30})
    rows = [dict(zip(aml.FEATURE_NAMES, map(float, x))) for x in X]
    r = client.post("/model/drift", json={"rows": rows}, headers=h("hr"))
    assert r.status_code == 200, r.text
    rep = r.json()
    assert rep["status"] == "significant" and "overtime_hours_month" in rep["significant_features"]
    assert rep["model_version"] == "1" and rep["rows"] == 400
    last = client.get("/model/drift/last", headers=h("hr")).json()
    assert last["available"] and last["max_psi"] == rep["max_psi"]
    assert client.get("/model/drift/last", headers=h("other-hr")).json() == {"available": False}
    assert "demo" in store.drifts


def test_drift_validation(env):
    client, _, _ = env
    X, _ = aml.synthetic_dataset(10, 1)
    rows = [dict(zip(aml.FEATURE_NAMES, map(float, x))) for x in X]
    assert client.post("/model/drift", json={"rows": rows}, headers=h("hr")).status_code == 422  # < 30 satir
    X, _ = aml.synthetic_dataset(40, 1)
    rows = [dict(zip(aml.FEATURE_NAMES, map(float, x)), employee_id="x") for x in X]
    assert client.post("/model/drift", json={"rows": rows}, headers=h("hr")).status_code == 422
    assert client.post("/model/drift", json={"rows": rows}, headers=h("employee")).status_code in (403, 422)


def test_tenant_consented_training_records_provenance_and_waits_for_approval(env, monkeypatch):
    """Dalga 10 (madde 36): kiracı izniyle toplanan satırlar + zaman bazlı ayrı değerlendirme kümesi;
    aday yayına otomatik girmez (champion/challenger), kaynak model kartına ve denetim yüküne yazılır."""
    client, service, _ = env
    monkeypatch.setenv("INTERNAL_SERVICE_TOKEN", "s3cret")
    X, y = aml.synthetic_dataset(600, 21)
    Xe, ye = aml.synthetic_dataset(300, 22)
    rows = [dict(zip(aml.FEATURE_NAMES, map(float, x)), label=int(l)) for x, l in zip(X, y)]
    ev = [dict(zip(aml.FEATURE_NAMES, map(float, x)), label=int(l)) for x, l in zip(Xe, ye)]
    prov = {"kind": "tenant-consented", "tenant_ref": "a1b2c3d4e5f60718", "consent_at": "2026-10-01T09:00:00Z",
            "validation": "time-based", "train_snapshot": "2024-10-01", "eval_snapshot": "2025-10-01"}
    body = {"source": "rows", "rows": rows, "evaluation_rows": ev, "provenance": prov,
            "data_window": {"start": "2024-10-01", "end": "2026-10-01"}}
    r = client.post("/model/retrain", json=body, headers=h("hr", **{"X-Internal-Token": "s3cret"}))
    assert r.status_code == 200, r.text
    res = r.json()
    assert res["evaluation"]["source"] == "provided-evaluation" and res["evaluation"]["rows"] == 300
    assert res["promoted"] is False and service.state.version == "1"
    assert res["audit"]["changes"]["provenance"]["kind"] == "tenant-consented"
    # Kiracı adı değil yalnızca özet kabul edilir; bilinmeyen alan reddedilir.
    bad = {**body, "provenance": {**prov, "tenant_ref": "demo"}}
    assert client.post("/model/retrain", json=bad, headers=h("hr", **{"X-Internal-Token": "s3cret"})).status_code == 422
    bad = {**body, "provenance": {**prov, "tenant": "demo"}}
    assert client.post("/model/retrain", json=bad, headers=h("hr", **{"X-Internal-Token": "s3cret"})).status_code == 422

# Sirlar dosyadan da okunabilir (X_FILE, Docker secrets); diger modullerden once.
import secret_env
secret_env.load()

from fastapi import FastAPI, Depends, HTTPException, Header
from pydantic import BaseModel, Field
from prometheus_fastapi_instrumentator import Instrumentator
import asyncio
from contextlib import asynccontextmanager
import httpx
import math
import os
import mlflow
import mlflow.sklearn
import numpy as np
import shap

# MLflow'un varsayilan HTTP zaman asimi (120sn) ve tekrar deneme sayisi (5,
# ustel geri cekilmeyle) cok yuksek - MLflow ayaga kalkmadan once
# ml-inference baslarsa (docker compose'da mlflow icin bir healthcheck
# olmadigindan bu her zaman mumkun), model yukleme denemesi DAKIKALARCA
# askida kalabilir ve FastAPI'nin startup event'i bunu bekledigi icin
# /health de dahil HICBIR istek bu sure boyunca yanit vermez. Servisin kendi
# tasarimi zaten "model yuklenemezse calismaya devam et" (asagidaki
# try/except) oldugundan, burada sadece bu basarisizligin HIZLI gerceklesmesini
# sagliyoruz - operator farkli bir deger set etmisse onu eziyoruz (setdefault).
os.environ.setdefault("MLFLOW_HTTP_REQUEST_TIMEOUT", "10")
os.environ.setdefault("MLFLOW_HTTP_REQUEST_MAX_RETRIES", "2")

_background: set[asyncio.Task] = set()


@asynccontextmanager
async def lifespan(_app: FastAPI):
    # Bilerek AWAIT edilmiyor: MLflow henuz hazir degilse (docker compose'da
    # mlflow icin healthcheck yok, ml-inference sadece container'in
    # BASLAMIS olmasini bekliyor) bu cagri dakikalarca surebilir. Arka
    # planda calistirarak /health ve diger uclar bu sure boyunca da yanit
    # vermeye devam eder; model hazir olunca 'model_loaded' otomatik true olur.
    # Gorev referansi tutulur (aksi halde cop toplayici yarida kesebilir).
    task = asyncio.create_task(_load_model_and_explainer())
    _background.add(task)
    task.add_done_callback(_background.discard)
    # Gunluk veri kaymasi olcumu (son tahminlerin toplu dagilimi -> Prometheus hr360_ml_*_psi).
    interval = float(os.getenv("DRIFT_INTERVAL_SECONDS", "86400"))
    if interval > 0:
        drift = asyncio.create_task(model_service.drift_loop(interval))
        _background.add(drift)
        drift.add_done_callback(_background.discard)
    yield


app = FastAPI(title="HR360 ML Inference Service", lifespan=lifespan)

# Performans ML katmani: anomali tespiti + yorunge tahmini.
# Terfi karari VERMEZ - karar performance-service icindeki kural
# motorunda, denetlenebilir sekilde aliniyor.
from performance_ml import router as performance_router
app.include_router(performance_router)
Instrumentator().instrument(app).expose(app)

KEYCLOAK_URL = os.getenv("KEYCLOAK_URL", "http://keycloak:8080/auth")
REALM = os.getenv("KEYCLOAK_REALM", "hr360")
CLIENT_ID = os.getenv("KEYCLOAK_CLIENT_ID", "hr360-ml-inference")
CLIENT_SECRET = os.getenv("KEYCLOAK_CLIENT_SECRET", "")
MLFLOW_TRACKING_URI = os.getenv("MLFLOW_TRACKING_URI", "http://mlflow:5000")
MODEL_NAME = os.getenv("MODEL_NAME", "hr360-attrition-risk")
MODEL_STAGE = os.getenv("MODEL_STAGE", "1")  # version 1

mlflow.set_tracking_uri(MLFLOW_TRACKING_URI)

class PredictRequest(BaseModel):
    # Sira attrition_ml.FEATURES'tir; deger sinirlari _features icinde FeatureSpec'ten uygulanir.
    features: list[float] = Field(min_length=1, max_length=50)
    # Kiracinin IK'ca secilen inceleme esigi (governance gonderir); yoksa 0,5. Esik ustu bir
    # KARAR degildir: "insan incelemesi onerilir" isaretidir.
    threshold: float | None = Field(default=None, ge=0.01, le=0.99)

async def verify_token(authorization: str = Header(None)):
    if not authorization or not authorization.startswith("Bearer "):
        raise HTTPException(status_code=401, detail="Missing bearer token")
    token = authorization.split(" ")[1]
    introspect_url = f"{KEYCLOAK_URL}/realms/{REALM}/protocol/openid-connect/token/introspect"
    async with httpx.AsyncClient() as client:
        resp = await client.post(
            introspect_url,
            data={"token": token, "client_id": CLIENT_ID, "client_secret": CLIENT_SECRET},
        )
    if resp.status_code != 200 or not resp.json().get("active"):
        raise HTTPException(status_code=401, detail="Invalid or expired token")
    return resp.json()

# Yapay zeka destekli IK araclari (CV ayristirma, ilan yazici + ayrimci ifade,
# performans ozeti, aday-ilan eslesmesi, izin tahmini, egitim onerisi).
# Hepsi yerel, aciklanabilir yontemler; tum uclar Keycloak jetonu ister.
from ai_tools import router as ai_router
app.include_router(ai_router, dependencies=[Depends(verify_token)])

# Fiş okuma (yerel Tesseract): çağıranın Keycloak jetonu gerekir (expense-service iletir).
from ocr import router as ocr_router
app.include_router(ocr_router, dependencies=[Depends(verify_token)])

# Serbest metinde kişisel / özel nitelikli veri uyarısı (TCKN, IBAN, telefon, e-posta, sağlık,
# sabıka...): yalnızca konum döner, metin saklanmaz. Arayüz aynı kuralları istemcide de uygular.
from pii import router as pii_router
app.include_router(pii_router, dependencies=[Depends(verify_token)])

# Masraf denetimi: olagan disi tutar / mukerrer fis isareti ve e-Fatura karekodu ayristirma
# (expense-service cagiranin jetonunu iletir). Yalnizca isaret uretir; beyan reddedilmez.
from expense_ml import router as expense_ml_router
app.include_router(expense_ml_router, dependencies=[Depends(verify_token)])

# ML dalgası 2 (hepsi yerel, açıklanabilir; yalnızca işaret/öneri üretir, kayıt yazmaz):
#  - bordro / puantaj denetimi (compensation- ve timeshift-service çağırır; takma adlı girdi)
#  - mevsimsellikli izin tahmini ve ekip kapasitesi (governance-service; yalnızca gün başına sayı)
#  - anket açık uçlu yanıtlarında konu + duygu (engagement-service; 5'ten küçük grup yok)
#  - beceri çıkarımı ve beceri haritası (arayüz / governance-service)
#  - ücret adaleti analizi (compensation-service; İK, denetim kaydıyla)
#  - anlamsal arama (governance-service; kiracı başına bellek içi dizin)
from payroll_ml import router as payroll_ml_router
from leave_forecast import router as leave_forecast_router
from text_insights import router as text_insights_router
from skills_ml import router as skills_router
from pay_equity import router as pay_equity_router
for _r in (payroll_ml_router, leave_forecast_router, text_insights_router, skills_router, pay_equity_router):
    app.include_router(_r, dependencies=[Depends(verify_token)])

# Dalga 9 (madde 44): vardiya planı optimizasyonu (OR-Tools CP-SAT, yoksa açgözlü sezgisel). Yalnızca öneri;
# timeshift-service takma adlı girdiyle çağırır, uygulamayı İK/yönetici yapar.
from shift_optimizer import router as shift_optimizer_router
app.include_router(shift_optimizer_router, dependencies=[Depends(verify_token)])

# Dalga 10 (madde 51): aday–ilan uygunluk puanı — işe alım uzmanına yardımcı; otomatik ret yok,
# ad/cinsiyet/yaş/fotoğraf/adres modele girmez (recruit_fit.py model kartı). governance-service çağırır.
from recruit_fit import router as recruit_fit_router
app.include_router(recruit_fit_router, dependencies=[Depends(verify_token)])


# Devir riski modeli: yuklenme, model karti, yeniden egitim ve veri kaymasi
# (model_routes.py). Yayindaki surum MLflow'da "champion" takma adiyla isaretlenir;
# takma ad yoksa MODEL_STAGE surumu yuklenir. Kayitli model hic yoksa (yeni kurulum)
# sentetik ureteciyle ilk surum egitilir (ATTRITION_BOOTSTRAP=false ile kapatilir).
from model_routes import ModelService, build_router, tenant_of
from attrition_ml import FEATURES, FEATURE_NAMES
from model_quality import DEFAULT_THRESHOLD, explain_reasons
from model_store import MlflowModelStore

model_service = ModelService(
    MlflowModelStore(MODEL_NAME, MODEL_STAGE),
    MODEL_NAME,
    explainer_factory=shap.TreeExplainer,
    bootstrap=os.getenv("ATTRITION_BOOTSTRAP", "true").lower() != "false",
)
app.include_router(build_router(verify_token, model_service))

import semantic_search
app.include_router(semantic_search.build_router(verify_token, tenant_of))


async def _load_model_and_explainer():
    await model_service.load()

@app.get("/health")
async def health():
    st = model_service.state
    return {"status": "ok", "service": "hr360-ml-inference", "model_loaded": st.model is not None,
            "model_version": st.version, "model_error": st.load_error}

def _features(req: PredictRequest, model) -> np.ndarray:
    expected = getattr(model, "n_features_in_", None)
    if expected is not None and len(req.features) != expected:
        raise HTTPException(status_code=422, detail=f"Model {expected} özellik bekliyor, {len(req.features)} geldi.")
    # Deger sinirlari (model_routes._Features ile ayni kaynak: attrition_ml.FEATURES). Onceden
    # kidem -5, performans 9 (1-5 olcek), fazla mesai -100 gibi degerler tahmine giriyordu.
    names = list(model_service.state.meta.get("features") or []) if model_service.state.meta else []
    if len(names) != len(req.features):
        names = list(FEATURE_NAMES) if len(req.features) == len(FEATURE_NAMES) else []
    specs = {f.name: f for f in FEATURES}
    errors = []
    for i, v in enumerate(req.features):
        spec = specs.get(names[i]) if i < len(names) else None
        if not math.isfinite(v):
            errors.append(f"{spec.label if spec else i + 1}: geçersiz sayı")
        elif spec is not None and not (spec.lo <= v <= spec.hi):
            unit = f" {spec.unit}" if spec.unit in ("yıl", "ay", "saat") else ""
            errors.append(f"{spec.label} {_num(spec.lo)} ile {_num(spec.hi)}{unit} arasında olmalı (gelen: {_num(v)})")
    if errors:
        raise HTTPException(status_code=422, detail="Geçersiz girdi: " + "; ".join(errors))
    return np.array(req.features, dtype=float).reshape(1, -1)


def _num(x: float) -> str:
    return f"{x:g}".replace(".", ",")


@app.post("/predict")
async def predict(req: PredictRequest, token_info: dict = Depends(verify_token)):
    st = model_service.state
    # Dislanan nitelik denetimi (cinsiyet, yas, medeni durum, saglik ve vekilleri) gecmeyen
    # bir model surumuyle tahmin yapilmaz.
    model = model_service.guard()
    X = _features(req, model)
    raw, cal = model_service.scores(X)
    p = float(cal[0])
    threshold = req.threshold if req.threshold is not None else DEFAULT_THRESHOLD
    # Kayma izleme: girdi ve skor yalnizca kiracinin TOPLU histogramina sayilir (kisi/kimlik saklanmaz).
    model_service.record_prediction(tenant_of(token_info), X[0], p)
    calibration = (st.meta or {}).get("calibration") or {}
    return {
        # Kalibre olasilik (kalibrasyonu olmayan eski surumde ham olasilik).
        "prediction": int(p >= DEFAULT_THRESHOLD),
        "probability": [1.0 - p, p],
        "raw_probability": float(raw[0]),
        "calibration": calibration.get("method", "none"),
        "threshold": threshold,
        "flagged": p >= threshold,
        "model": f"{MODEL_NAME}/v{st.version}",
        "authenticated_client": token_info.get("client_id", token_info.get("azp")),
    }

@app.post("/explain")
async def explain(req: PredictRequest, token_info: dict = Depends(verify_token)):
    st = model_service.state
    model_service.guard()
    explainer = st.explainer
    if st.model is None or explainer is None:
        raise HTTPException(status_code=503, detail="Model or explainer not loaded")
    X = _features(req, st.model)
    raw = explainer.shap_values(X)
    arr = np.array(raw)
    # arr shape genelde (n_samples, n_features, n_classes) ya da (n_classes, n_samples, n_features)
    # pozitif sinifin (class 1) katkilarini duz bir listeye indirgeyelim
    if arr.ndim == 3 and arr.shape[-1] == 2:
        contributions = arr[0, :, 1].tolist()
    elif arr.ndim == 3 and arr.shape[0] == 2:
        contributions = arr[1, 0, :].tolist()
    else:
        contributions = np.array(raw).reshape(-1).tolist()

    base = explainer.expected_value
    if isinstance(base, (list, np.ndarray)):
        base_value = float(np.array(base).reshape(-1)[-1])
    else:
        base_value = float(base)

    names = list(st.meta.get("features") or []) or list(FEATURE_NAMES)
    return {
        "feature_contributions": contributions,
        # Katkilar giris sirasindadir; adlar model kartindaki ozellik sirasidir.
        "feature_names": list(st.meta.get("features") or []),
        # Sade dilde "neden?": en etkili 3 ozellik (+ riski artirir, - azaltir). Ham SHAP katkisina dayanir.
        "reasons": explain_reasons(names, X[0].tolist(), contributions) if len(names) == len(contributions) else [],
        "base_value": base_value,
        "model": f"{MODEL_NAME}/v{st.version}",
    }
# CI deploy retry - variable adi duzeltildi
# CI/CD deploy dogrulama - secret isim duzeltmesi sonrasi

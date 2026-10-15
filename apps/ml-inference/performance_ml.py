"""
Performans ML katmani.

NE YAPAR / NE YAPMAZ
--------------------
Bu servis terfi KARARI VERMEZ. Karar, performance-service icindeki kural
motorunda; sirketin kendi belirledigi esiklerle, denetlenebilir sekilde
uretiliyor. Birinin terfisi reddedildiginde gerekce gosterilebilmesi
gerekir; "model boyle hesapladi" savunulabilir bir gerekce degildir.

Bu servis, kurallarin IFADE EDEMEDIGI iki seyi yapiyor:

  1. Anomali tespiti (IsolationForest) - denetimsiz, etiket gerektirmez.
     "Bu kisinin oruntusu ekibin geri kalanina gore sira disi" bilgisi
     esik kurallariyla yazilamaz; cok boyutlu bir dagilim sorusudur.

  2. Yorunge tahmini (agirlikli dogrusal regresyon) - "bu hizla giderse
     N donem sonra hangi puanda olur". Tek bir esik karsilastirmasi
     gelecegi soylemez.

NEDEN DENETIMLI MODEL YOK
-------------------------
Elimizde etiketli veri yok: "bu kisi terfi aldi/almadi" gecmisi mevcut
degil. Uydurma etiketle siniflandirici egitmek gosteristen ibaret olur
ve gercek kullanimda yanlis yonlendirir. Etiketli veri biriktiginde
(sistem bir sure calistiktan sonra) denetimli bir katman eklenebilir.
"""

from __future__ import annotations

import logging
from datetime import datetime, timezone
from typing import Literal

import numpy as np
from fastapi import APIRouter, HTTPException
from pydantic import BaseModel, ConfigDict, Field
from sklearn.ensemble import IsolationForest
from sklearn.preprocessing import StandardScaler

logger = logging.getLogger(__name__)
router = APIRouter(prefix="/performance", tags=["performance-ml"])


# ----------------------------------------------------------------- girdi

class ScorePoint(BaseModel):
    """Bir calisanin tek bir zamandaki puani."""
    captured_at: datetime
    score: float = Field(ge=0, le=100)
    is_provisional: bool = False


class EmployeeSeries(BaseModel):
    employee_id: str
    points: list[ScorePoint] = Field(min_length=1)
    goal_score: float | None = Field(default=None, ge=0, le=100)
    review_count: int = 0
    months_since_hire: int = 0


class AnalyzeRequest(BaseModel):
    """
    Bir ekip ya da sirket icin toplu analiz.
    Anomali tespiti, kisinin kendi gecmisine degil GRUBA gore yapilir -
    "sira disi" kavrami ancak bir referans kume varsa anlamlidir.
    """
    employees: list[EmployeeSeries] = Field(min_length=1)
    forecast_periods: int = Field(default=2, ge=1, le=6)
    contamination: float = Field(
        default=0.1, ge=0.01, le=0.4,
        description="Beklenen anomali orani. Kucuk ekiplerde yuksek tutmak yanlis alarm uretir.",
    )


# ----------------------------------------------------------------- cikti

class Forecast(BaseModel):
    periods_ahead: int
    predicted_score: float
    confidence: Literal["low", "medium", "high"]
    basis: str


class EmployeeAnalysis(BaseModel):
    employee_id: str
    anomaly: bool
    anomaly_score: float = Field(description="Negatife yaklastikca daha sira disi.")
    anomaly_reason: str | None
    trend_slope: float
    volatility: float
    forecast: Forecast | None
    data_quality: Literal["insufficient", "limited", "adequate"]
    notes: list[str]


class AnalyzeResponse(BaseModel):
    # model_config: "model_used"/"model_skip_reason" alan adlari pydantic'in
    # kendi korumali "model_" on ekiyle cakisiyor (model_dump vb. ile
    # karisabilir uyarisi verir) - anlamli isimler oldugu icin degistirmek
    # yerine bu sinifta korumali ad namespace kontrolunu devre disi birakiyoruz.
    model_config = ConfigDict(protected_namespaces=())

    analyzed_at: datetime
    employee_count: int
    anomaly_count: int
    model_used: bool
    model_skip_reason: str | None
    group_stats: dict
    results: list[EmployeeAnalysis]


# ------------------------------------------------------------ yardimcilar

def _weighted_trend(scores: list[float]) -> float:
    """
    Agirlikli dogrusal egim. Yeni noktalar daha agir sayilir:
    6 ay onceki dusus, gecen haftaki dususten daha az anlamli.
    """
    n = len(scores)
    if n < 2:
        return 0.0

    x = np.arange(n, dtype=float)
    y = np.array(scores, dtype=float)
    # Dogrusal artan agirlik: en eski 1, en yeni n
    w = np.linspace(1.0, float(n), n)

    w_sum = w.sum()
    x_mean = (w * x).sum() / w_sum
    y_mean = (w * y).sum() / w_sum

    denom = (w * (x - x_mean) ** 2).sum()
    if denom == 0:
        return 0.0

    return float((w * (x - x_mean) * (y - y_mean)).sum() / denom)


def _features(emp: EmployeeSeries) -> tuple[list[float], float, float]:
    """Anomali modeline verilecek oznitelikler + egim ve oynaklik."""
    scores = [p.score for p in emp.points]
    slope = _weighted_trend(scores)
    volatility = float(np.std(scores, ddof=1)) if len(scores) > 1 else 0.0

    latest = scores[-1]
    mean = float(np.mean(scores))
    # Son puanin kendi ortalamasindan sapmasi: kisinin kendi
    # normaline gore nerede oldugu
    self_delta = latest - mean

    return (
        [latest, mean, slope, volatility, self_delta,
         emp.goal_score if emp.goal_score is not None else mean,
         float(emp.review_count)],
        slope,
        volatility,
    )


def _quality(emp: EmployeeSeries) -> tuple[str, list[str]]:
    notes: list[str] = []
    n = len(emp.points)
    provisional = sum(1 for p in emp.points if p.is_provisional)

    if n < 2:
        notes.append("Tek veri noktası var; eğilim hesaplanamıyor.")
        return "insufficient", notes

    if n < 4:
        notes.append(f"Yalnızca {n} veri noktası var; eğilim zayıf.")
        quality = "limited"
    else:
        quality = "adequate"

    if provisional > n / 2:
        notes.append(f"{provisional}/{n} puan geçici; sonuçlar değişebilir.")
        quality = "limited"

    if emp.months_since_hire and emp.months_since_hire < 6:
        notes.append(f"İşe girişten bu yana {emp.months_since_hire} ay geçmiş.")

    return quality, notes


def _forecast(scores: list[float], slope: float, periods: int, quality: str) -> Forecast | None:
    if len(scores) < 2:
        return None

    predicted = float(np.clip(scores[-1] + slope * periods, 0.0, 100.0))

    # Guven, hem veri miktarina hem oynakliga bagli. Cok dalgali bir
    # seride egim tahmini guvenilmezdir.
    volatility = float(np.std(scores, ddof=1))
    if quality == "adequate" and volatility < 8:
        conf = "high"
    elif quality == "insufficient" or volatility > 18:
        conf = "low"
    else:
        conf = "medium"

    direction = "yükseliş" if slope > 0.3 else ("düşüş" if slope < -0.3 else "yatay")
    return Forecast(
        periods_ahead=periods,
        predicted_score=round(predicted, 1),
        confidence=conf,
        basis=f"{len(scores)} veri noktası, ağırlıklı eğim {slope:+.2f}/dönem, {direction} eğilimi",
    )


# ------------------------------------------------------------------- uc

@router.post("/analyze", response_model=AnalyzeResponse)
def analyze(req: AnalyzeRequest) -> AnalyzeResponse:
    """
    Ekip capinda anomali tespiti ve yorunge tahmini.

    Kural motorunun urettigi aksiyon onerisine EK sinyal saglar;
    kararin kendisini vermez.
    """
    employees = req.employees
    n = len(employees)

    feats: list[list[float]] = []
    slopes: list[float] = []
    vols: list[float] = []

    for emp in employees:
        f, slope, vol = _features(emp)
        feats.append(f)
        slopes.append(slope)
        vols.append(vol)

    X = np.array(feats, dtype=float)

    # IsolationForest anlamli calismak icin yeterli ornek ister.
    # Kucuk ekiplerde herkesi "anomali" ilan etmek yanlis alarm uretir;
    # modeli calistirmiyor ve bunu acikca soyluyoruz.
    model_used = False
    skip_reason: str | None = None
    labels = np.ones(n, dtype=int)
    raw_scores = np.zeros(n, dtype=float)

    MIN_SAMPLES = 8
    if n < MIN_SAMPLES:
        skip_reason = (
            f"Anomali tespiti için en az {MIN_SAMPLES} çalışan gerekiyor "
            f"(mevcut: {n}). Küçük gruplarda model yanlış alarm üretir."
        )
        logger.info("Anomali tespiti atlandi: %s calisan", n)
    else:
        try:
            Xs = StandardScaler().fit_transform(X)
            clf = IsolationForest(
                contamination=req.contamination,
                random_state=42,          # tekrarlanabilirlik
                n_estimators=200,
            )
            labels = clf.fit_predict(Xs)          # -1 anomali, 1 normal
            raw_scores = clf.score_samples(Xs)    # dusuk = daha sira disi
            model_used = True
        except Exception as exc:                  # noqa: BLE001
            skip_reason = f"Model çalıştırılamadı: {exc}"
            logger.exception("IsolationForest hatasi")

    all_latest = [e.points[-1].score for e in employees]
    group_mean = float(np.mean(all_latest))
    group_std = float(np.std(all_latest, ddof=1)) if n > 1 else 0.0

    results: list[EmployeeAnalysis] = []
    for i, emp in enumerate(employees):
        scores = [p.score for p in emp.points]
        quality, notes = _quality(emp)

        is_anom = bool(labels[i] == -1)
        reason = None
        if is_anom:
            latest = scores[-1]
            parts = []
            if group_std > 0 and abs(latest - group_mean) > 1.5 * group_std:
                yon = "üzerinde" if latest > group_mean else "altında"
                parts.append(f"puanı grup ortalamasının belirgin {yon}")
            if abs(slopes[i]) > 1.5:
                parts.append("eğilimi grubun geri kalanından farklı")
            if vols[i] > 15:
                parts.append("puanları alışılmadık ölçüde dalgalı")
            reason = ("Sıra dışı örüntü: " + ", ".join(parts)) if parts else \
                     "Çok boyutlu örüntüsü grubun geri kalanından ayrışıyor."

        results.append(EmployeeAnalysis(
            employee_id=emp.employee_id,
            anomaly=is_anom,
            anomaly_score=round(float(raw_scores[i]), 4),
            anomaly_reason=reason,
            trend_slope=round(slopes[i], 3),
            volatility=round(vols[i], 2),
            forecast=_forecast(scores, slopes[i], req.forecast_periods, quality),
            data_quality=quality,  # type: ignore[arg-type]
            notes=notes,
        ))

    return AnalyzeResponse(
        analyzed_at=datetime.now(timezone.utc),
        employee_count=n,
        anomaly_count=sum(1 for r in results if r.anomaly),
        model_used=model_used,
        model_skip_reason=skip_reason,
        group_stats={
            "mean": round(group_mean, 2),
            "std_dev": round(group_std, 2),
            "min": round(min(all_latest), 2),
            "max": round(max(all_latest), 2),
        },
        results=results,
    )


@router.get("/health")
def health() -> dict:
    return {
        "status": "ok",
        "module": "performance-ml",
        "capabilities": ["anomaly-detection", "trajectory-forecast"],
        "note": "Terfi kararı vermez; kural motoruna ek sinyal sağlar.",
    }

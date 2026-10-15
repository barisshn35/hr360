"""
Devir (isten ayrilma) riski modeli: ozellik semasi, sentetik veri ureteci,
egitim, degerlendirme, terfi karari ve veri kaymasi (PSI).

Bu modul yalnizca numpy + scikit-learn kullanir (MLflow/SHAP gerektirmez);
boylece birim testleri hafif ortamda (CI) calisir. MLflow kaydi ve sunum
model_routes.py / model_store.py icindedir.

KVKK / ayrimcilik ilkesi
------------------------
Model yalnizca asagidaki IS ILE ILGILI, sayisal ve kimliksiz ozellikleri
kullanir. Cinsiyet, yas, medeni durum, saglik, din, etnik koken ve sendika
uyeligi gibi ozel nitelikli ya da ayrimciliga yol acabilecek nitelikler
BILEREK disarida birakilmistir; `check_features` bunu her model kartinda ve
her egitimde dogrular. Egitim verisi kimlik icermez (satir semasi ekstra
alan kabul etmez) ve modelde saklanmaz - yalnizca ozellik basina histogram
(toplulastirilmis) referans olarak tutulur.
"""

from __future__ import annotations

import math
import re
import unicodedata
from dataclasses import dataclass

import numpy as np
from sklearn.ensemble import RandomForestClassifier
from sklearn.metrics import accuracy_score, roc_auc_score
from sklearn.model_selection import train_test_split
from tr_text import tr_num


@dataclass(frozen=True)
class FeatureSpec:
    name: str
    label: str
    unit: str
    description: str
    lo: float
    hi: float


# /predict ve /explain girdisindeki SIRA budur (governance 6 elemanli liste gonderir).
FEATURES: tuple[FeatureSpec, ...] = (
    FeatureSpec("tenure_years", "Kıdem", "yıl", "Şirketteki çalışma süresi", 0, 45),
    FeatureSpec("compa_ratio", "Ücret / bant ortası", "oran", "Brüt ücretin pozisyon bandı ortasına oranı", 0.3, 2.0),
    FeatureSpec("last_rating", "Son performans puanı", "1-5", "Son tamamlanan değerlendirme puanı", 1, 5),
    FeatureSpec("months_since_promotion", "Son terfiden bu yana", "ay", "Son terfi ya da unvan değişikliğinden bu yana geçen süre", 0, 360),
    FeatureSpec("overtime_hours_month", "Aylık fazla mesai", "saat", "Son 3 ayın aylık ortalama fazla mesaisi", 0, 200),
    FeatureSpec("training_hours_year", "Yıllık eğitim", "saat", "Son 12 ayda tamamlanan eğitim süresi", 0, 500),
)
FEATURE_NAMES: tuple[str, ...] = tuple(f.name for f in FEATURES)

# Modelde BILEREK kullanilmayan nitelikler (KVKK m.6 ozel nitelikli veri ve ayrimcilik yasagi).
EXCLUDED_ATTRIBUTES: tuple[tuple[str, str], ...] = (
    ("gender", "Cinsiyet"),
    ("age", "Yaş / doğum tarihi"),
    ("marital_status", "Medeni durum"),
    ("health", "Sağlık verisi (rapor, engellilik, hastalık izni)"),
    ("religion", "Din / inanç"),
    ("ethnicity", "Irk / etnik köken"),
    ("union_membership", "Sendika üyeliği"),
)

# Ozellik adlarinda gecmemesi gereken kokler (TR + EN). Adlar ASCII'ye indirgenip aranir.
_BANNED_ROOTS: dict[str, tuple[str, ...]] = {
    "gender": ("gender", "sex", "cinsiyet", "kadin", "erkek"),
    "age": ("age", "birth", "dob", "yas", "dogum"),
    "marital_status": ("marital", "married", "spouse", "medeni", "evli", "bekar", "es_"),
    "health": ("health", "sick", "medical", "disab", "illness", "saglik", "hastal", "rapor", "engel"),
    "religion": ("religio", "faith", "din_", "inanc", "mezhep"),
    "ethnicity": ("ethnic", "race", "etnik", "irk", "nation", "uyruk"),
    "union_membership": ("union", "sendika"),
}

_SEGMENT = re.compile(r"[a-z0-9]+")


def _ascii(s: str) -> str:
    s = s.replace("ı", "i").replace("İ", "i")
    return unicodedata.normalize("NFKD", s).encode("ascii", "ignore").decode().lower()


def check_features(names: tuple[str, ...] | list[str]) -> dict:
    """Ozellik listesinde dislanan bir niteligin (ya da esanlamlisinin) olmadigini dogrular.

    Kokler ad parcalarinin BASINDA aranir ("age" -> "age_band" yakalanir; "average"
    yanlis alarm vermez). "es_" / "din_" gibi kisa kokler yalnizca tam parca olarak eslesir.
    """
    violations = []
    for name in names:
        n = _ascii(name)
        parts = _SEGMENT.findall(n)
        for attr, roots in _BANNED_ROOTS.items():
            for root in roots:
                r = root.rstrip("_")
                hit = (r in parts) if root.endswith("_") or len(r) <= 3 else any(p.startswith(r) for p in parts)
                if hit:
                    violations.append({"feature": name, "attribute": attr, "matched": r})
                    break
    return {"passed": not violations, "violations": violations}


class ForbiddenFeatureError(ValueError):
    """Ozellik listesinde dislanan bir nitelik (ya da vekili) var: egitim/tahmin REDDEDILIR."""

    def __init__(self, violations: list[dict]):
        self.violations = violations
        names = ", ".join(sorted({v["feature"] for v in violations}))
        super().__init__(f"Dışlanan nitelik içeren özellik(ler): {names}")


def assert_allowed(names) -> None:
    """Egitim ve tahminden once cagrilir; ihlal varsa ForbiddenFeatureError firlatir."""
    check = check_features(list(names))
    if not check["passed"]:
        raise ForbiddenFeatureError(check["violations"])


# ------------------------------------------------------------------ sentetik veri

def _sigmoid(z: np.ndarray) -> np.ndarray:
    return 1.0 / (1.0 + np.exp(-z))


SYNTHETIC_DESCRIPTION = (
    "Sentetik veri: kıdem ~ Gamma(2, 2.5) yıl; ücret oranı ~ N(1.0, 0.12); puan 1-5 "
    "(%5/%15/%45/%25/%10); son terfiden bu yana ~ Gamma(2, 12) ay; fazla mesai ~ Gamma(1.5, 8) saat; "
    "eğitim ~ Gamma(2, 10) saat. Etiket lojistik bir kuraldan örneklenir; gerçek kişi verisi içermez."
)


def synthetic_features(n: int, rng: np.random.Generator, shift: dict[str, float] | None = None) -> np.ndarray:
    tenure = np.clip(rng.gamma(2.0, 2.5, n), 0, 45)
    compa = np.clip(rng.normal(1.0, 0.12, n), 0.3, 2.0)
    rating = rng.choice([1, 2, 3, 4, 5], size=n, p=[0.05, 0.15, 0.45, 0.25, 0.10]).astype(float)
    promo = np.clip(rng.gamma(2.0, 12.0, n), 0, 360)
    overtime = np.clip(rng.gamma(1.5, 8.0, n), 0, 200)
    training = np.clip(rng.gamma(2.0, 10.0, n), 0, 500)
    X = np.column_stack([tenure, compa, rating, promo, overtime, training])
    for name, delta in (shift or {}).items():
        i = FEATURE_NAMES.index(name)
        X[:, i] = np.clip(X[:, i] + delta, FEATURES[i].lo, FEATURES[i].hi)
    return X


def synthetic_labels(X: np.ndarray, rng: np.random.Generator, label_noise: float = 0.0) -> np.ndarray:
    tenure, compa, rating, promo, overtime, training = X.T
    z = (0.2 - 0.22 * np.minimum(tenure, 12) - 6.0 * (compa - 1.0) - 0.55 * (rating - 3)
         + 0.03 * np.minimum(promo, 72) + 0.035 * overtime - 0.02 * training)
    y = (rng.random(len(X)) < _sigmoid(z)).astype(int)
    if label_noise > 0:
        flip = rng.random(len(X)) < label_noise
        y[flip] = rng.integers(0, 2, int(flip.sum()))
    return y


def synthetic_dataset(n: int, seed: int, label_noise: float = 0.0, shift: dict[str, float] | None = None):
    rng = np.random.default_rng(seed)
    X = synthetic_features(n, rng, shift)
    return X, synthetic_labels(X, rng, label_noise)


# ------------------------------------------------------------------ egitim / degerlendirme

ALGORITHM = {"name": "RandomForestClassifier", "n_estimators": 150, "max_depth": 8, "min_samples_leaf": 5,
             "class_weight": "balanced"}


def train_model(X: np.ndarray, y: np.ndarray, seed: int = 42,
                feature_names=FEATURE_NAMES) -> RandomForestClassifier:
    # Koruma: dislanan bir nitelik (cinsiyet, yas, medeni durum, saglik ya da vekilleri)
    # ozellik listesine girmisse model HIC egitilmez.
    assert_allowed(feature_names)
    if X.shape[1] != len(feature_names):
        raise ValueError(f"Özellik sayısı uyuşmuyor: {X.shape[1]} sütun, {len(feature_names)} ad.")
    params = {k: v for k, v in ALGORITHM.items() if k != "name"}
    model = RandomForestClassifier(random_state=seed, n_jobs=1, **params)
    model.fit(X, y)
    return model


def evaluate(model, X: np.ndarray, y: np.ndarray) -> dict:
    proba = model.predict_proba(X)[:, 1]
    pred = (proba >= 0.5).astype(int)
    auc = float(roc_auc_score(y, proba)) if len(set(y.tolist())) > 1 else float("nan")
    return {"auc": round(auc, 4), "accuracy": round(float(accuracy_score(y, pred)), 4),
            "n": int(len(y)), "positive_rate": round(float(np.mean(y)), 4)}


def split(X: np.ndarray, y: np.ndarray, seed: int, test_size: float = 0.25):
    return train_test_split(X, y, test_size=test_size, random_state=seed, stratify=y)


PROMOTION_TOLERANCE = 0.02


def promotion_decision(candidate: dict, current: dict | None, tolerance: float = PROMOTION_TOLERANCE) -> dict:
    """Aday, mevcut modelden AUC'de `tolerance`'tan fazla kotu DEGILSE terfi eder.

    Ikisi de AYNI degerlendirme kumesinde olculur. Mevcut model yoksa aday terfi eder.
    """
    if current is None or current.get("auc") is None or math.isnan(current["auc"]):
        return {"promote": True, "reason": "Mevcut model yok; aday ilk sürüm olarak yayımlanır.", "auc_delta": None}
    if candidate.get("auc") is None or math.isnan(candidate["auc"]):
        return {"promote": False, "reason": "Adayın AUC değeri hesaplanamadı (tek sınıf).", "auc_delta": None}
    delta = round(candidate["auc"] - current["auc"], 4)
    if delta < -tolerance:
        return {"promote": False, "auc_delta": delta,
                "reason": f"Aday mevcut modelden {tr_num(abs(delta), 3)} AUC daha kötü (eşik {tr_num(tolerance)}); yayımlanmadı."}
    return {"promote": True, "auc_delta": delta,
            "reason": f"Aday mevcut modelden kötü değil (AUC farkı {'+' if delta >= 0 else '-'}{tr_num(abs(delta), 3)}, eşik -{tr_num(tolerance)})."}


# ------------------------------------------------------------------ veri kaymasi (PSI)

PSI_MODERATE = 0.1
PSI_SIGNIFICANT = 0.2
_EPS = 1e-4


def reference_histograms(X: np.ndarray, bins: int = 10) -> dict:
    """Egitim verisinin ozellik basina dagilimi (yalnizca kova sinirlari ve oranlari).

    Kova sinirlari egitim verisinin yuzdelik dilimleridir (kesikli ozelliklerde
    tekrar eden sinirlar birlesir). Ham satirlar saklanmaz.
    """
    ref = {}
    for i, name in enumerate(FEATURE_NAMES):
        col = X[:, i]
        inner = np.unique(np.quantile(col, np.linspace(0, 1, bins + 1)[1:-1]))
        counts = _bin_counts(col, inner)
        ref[name] = {"edges": [round(float(e), 6) for e in inner],
                     "proportions": [round(c / len(col), 6) for c in counts],
                     "mean": round(float(np.mean(col)), 4), "std": round(float(np.std(col)), 4)}
    return ref


def _bin_counts(values: np.ndarray, inner_edges) -> list[int]:
    # Kova i: (edge[i-1], edge[i]] ; ilk kova (-inf, edge[0]], son kova (edge[-1], +inf)
    idx = np.searchsorted(np.asarray(inner_edges, dtype=float), values, side="left")
    return np.bincount(idx, minlength=len(inner_edges) + 1).tolist()


def psi(expected: list[float], actual: list[float]) -> float:
    e = np.clip(np.asarray(expected, dtype=float), _EPS, None)
    a = np.clip(np.asarray(actual, dtype=float), _EPS, None)
    e, a = e / e.sum(), a / a.sum()
    return float(np.sum((a - e) * np.log(a / e)))


def psi_level(value: float) -> str:
    if value > PSI_SIGNIFICANT:
        return "significant"
    if value > PSI_MODERATE:
        return "moderate"
    return "stable"


def drift_report(reference: dict, X: np.ndarray) -> dict:
    counts = {name: _bin_counts(X[:, i], reference[name]["edges"])
              for i, name in enumerate(FEATURE_NAMES) if reference.get(name)}
    means = {name: round(float(np.mean(X[:, i])), 4) for i, name in enumerate(FEATURE_NAMES)}
    return drift_report_from_counts(reference, counts, int(len(X)), means)


def drift_report_from_counts(reference: dict, counts: dict[str, list[int]], rows: int,
                             means: dict[str, float] | None = None) -> dict:
    """PSI raporu yalnizca kova sayilarindan uretilir (kisi bazli satir gerekmez)."""
    features = []
    for i, name in enumerate(FEATURE_NAMES):
        ref = reference.get(name)
        c = counts.get(name)
        if not ref or not c or sum(c) == 0:
            continue
        actual = [x / sum(c) for x in c]
        value = round(psi(ref["proportions"], actual), 4)
        features.append({"feature": name, "label": FEATURES[i].label, "psi": value, "level": psi_level(value),
                         "reference_mean": ref.get("mean"), "batch_mean": (means or {}).get(name)})
    worst = max((f["psi"] for f in features), default=0.0)
    significant = [f["feature"] for f in features if f["level"] == "significant"]
    return {"rows": int(rows), "features": features, "max_psi": worst, "status": psi_level(worst),
            "significant_features": significant,
            "thresholds": {"moderate": PSI_MODERATE, "significant": PSI_SIGNIFICANT},
            "recommendation": ("Belirgin kayma var: modeli güncel toplu veriyle yeniden eğitmeyi ve kararları "
                               "bu süre içinde insan incelemesine bağlamayı değerlendirin.") if significant else
                              ("Orta düzey kayma: izlemeye devam edin.") if worst > PSI_MODERATE else
                              "Dağılım eğitim verisine yakın."}


def rows_to_matrix(rows: list[dict]) -> np.ndarray:
    return np.array([[float(r[n]) for n in FEATURE_NAMES] for r in rows], dtype=float)


class PredictionHistogram:
    """Tahmin girdilerinin TOPLU dagilimi: ozellik basina yalnizca kova sayilari tutulur.

    Kisi, kimlik, zaman damgasi ya da ham deger saklanmaz; kova sinirlari egitim
    referansindan gelir. Sayac `version` degisince (yeni model) sifirlanir.
    """

    def __init__(self, reference: dict, version: str | None):
        self.version = version
        self.edges = {n: reference[n]["edges"] for n in FEATURE_NAMES if reference.get(n)}
        self.counts = {n: [0] * (len(e) + 1) for n, e in self.edges.items()}
        self.sums = {n: 0.0 for n in self.edges}
        self.rows = 0

    def add(self, values) -> None:
        for i, name in enumerate(FEATURE_NAMES):
            if name not in self.edges:
                continue
            v = float(values[i])
            idx = int(np.searchsorted(np.asarray(self.edges[name], dtype=float), v, side="left"))
            self.counts[name][idx] += 1
            self.sums[name] += v
        self.rows += 1

    def means(self) -> dict[str, float]:
        return {n: round(s / self.rows, 4) for n, s in self.sums.items()} if self.rows else {}


def sample_from_reference(reference: dict, n: int, seed: int = 0) -> np.ndarray:
    """Referans histogramlarindan (kova oranlari) yapay satir ornekler - kuresel onem
    hesabi icin. Ham egitim satiri gerekmez; ozellikler bagimsiz orneklenir."""
    rng = np.random.default_rng(seed)
    cols = []
    for i, spec in enumerate(FEATURES):
        ref = reference.get(spec.name)
        if not ref:
            cols.append(rng.uniform(spec.lo, spec.hi, n))
            continue
        bounds = [spec.lo, *ref["edges"], spec.hi]
        p = np.clip(np.asarray(ref["proportions"], dtype=float), 0, None)
        p = p / p.sum()
        b = rng.choice(len(p), size=n, p=p)
        lo = np.asarray(bounds[:-1], dtype=float)[b]
        hi = np.maximum(np.asarray(bounds[1:], dtype=float)[b], lo)
        cols.append(rng.uniform(lo, hi))
    return np.column_stack(cols)


def _positive_class_shap(raw, n_features: int) -> np.ndarray:
    """shap ciktisini (n_samples, n_features) bicimine indirger (pozitif sinif = ayrilma)."""
    if isinstance(raw, list):  # eski shap: sinif basina liste
        raw = raw[1] if len(raw) > 1 else raw[0]
    arr = np.asarray(raw, dtype=float)
    if arr.ndim == 3:
        arr = arr[:, :, 1] if arr.shape[2] == 2 and arr.shape[1] == n_features else arr[1]
    return arr


def global_importance(model, X: np.ndarray, explainer=None) -> dict:
    """Kuresel ozellik onemi: ortalama |SHAP| (pozitif sinif). Aciklayici yoksa modelin
    safsizlik tabanli onemine duser (yontem alaninda belirtilir)."""
    method = "mean_abs_shap"
    try:
        if explainer is None:
            raise RuntimeError("explainer yok")
        values = np.abs(_positive_class_shap(explainer.shap_values(X), X.shape[1])).mean(axis=0)
    except Exception:  # noqa: BLE001 - shap yoksa / hata verirse
        method = "impurity"
        values = np.asarray(getattr(model, "feature_importances_", np.zeros(X.shape[1])), dtype=float)
    total = float(values.sum()) or 1.0
    items = [{"feature": f.name, "label": f.label, "value": round(float(v), 5), "share": round(float(v) / total, 4)}
             for f, v in zip(FEATURES, values)]
    items.sort(key=lambda x: x["value"], reverse=True)
    return {"method": method, "sample_rows": int(len(X)), "features": items}

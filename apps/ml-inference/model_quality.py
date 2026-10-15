"""
Devir riski modelinin kalite katmani: olasilik kalibrasyonu, guvenilirlik egrisi,
esik tablosu, tahmin dagilimi kaymasi, adillik denetimi ve sade dilde aciklamalar.

Yalnizca numpy + scikit-learn (MLflow/SHAP gerektirmez; birim testleri hafif ortamda calisir).

Kalibrasyon parametreleri model nesnesine degil model kartinin meta verisine (JSON) yazilir:
sigmoid icin iki katsayi, izotonik icin kirilma noktalari. Boylece MLflow'daki model dosyasi
(skops) degismez, eski surumler kalibrasyonsuz (ham olasilikla) calismaya devam eder.

KVKK
----
- Adillik raporunda 5'ten kucuk gruplar GOSTERILMEZ (ad dahil); yalnizca gizlenen grup sayisi verilir.
- Grup nitelikleri (departman, kidem bandi; veri modelinde varsa cinsiyet/yas bandi) yalnizca
  denetim icin kullanilir, modele OZELLIK olarak girmez.
- Skor tek basina karar degildir: esik ustu "insan incelemesi onerilir" anlamina gelir.
"""

from __future__ import annotations

import math
from datetime import date, datetime, timedelta, timezone

import numpy as np
from sklearn.isotonic import IsotonicRegression
from sklearn.linear_model import LogisticRegression
from sklearn.metrics import roc_auc_score

MIN_GROUP = 5
FOUR_FIFTHS = 0.8
DEFAULT_THRESHOLD = 0.5
THRESHOLDS = tuple(round(x, 2) for x in np.arange(0.05, 0.951, 0.05))
SCORE_EDGES = tuple(round(x, 1) for x in np.arange(0.1, 0.91, 0.1))  # 10 esit genislikte skor kovasi
_EPS = 1e-6


# ------------------------------------------------------------------ kalibrasyon

def _logit(p: np.ndarray) -> np.ndarray:
    p = np.clip(np.asarray(p, dtype=float), _EPS, 1 - _EPS)
    return np.log(p / (1 - p))


def brier(p, y) -> float:
    p = np.asarray(p, dtype=float)
    y = np.asarray(y, dtype=float)
    return float(np.mean((p - y) ** 2)) if len(y) else float("nan")


def _fit_sigmoid(raw: np.ndarray, y: np.ndarray) -> dict:
    lr = LogisticRegression(C=1e4, solver="lbfgs")
    lr.fit(_logit(raw).reshape(-1, 1), y)
    return {"a": round(float(lr.coef_[0][0]), 6), "b": round(float(lr.intercept_[0]), 6)}


def _fit_isotonic(raw: np.ndarray, y: np.ndarray) -> dict:
    iso = IsotonicRegression(out_of_bounds="clip", y_min=0.0, y_max=1.0)
    iso.fit(raw, y)
    return {"x": [round(float(v), 6) for v in iso.X_thresholds_],
            "y": [round(float(v), 6) for v in iso.y_thresholds_]}


def apply_calibration(raw, calibration: dict | None) -> np.ndarray:
    """Ham pozitif sinif olasiligini kalibre eder. Kalibrasyon yoksa ham deger doner."""
    raw = np.asarray(raw, dtype=float)
    if not calibration or calibration.get("method") in (None, "none"):
        return raw
    p = calibration.get("params") or {}
    if calibration["method"] == "sigmoid":
        return 1.0 / (1.0 + np.exp(-(p["a"] * _logit(raw) + p["b"])))
    if calibration["method"] == "isotonic":
        return np.clip(np.interp(raw, p["x"], p["y"]), 0.0, 1.0)
    return raw


def fit_calibration(raw, y, seed: int = 0) -> dict:
    """Sigmoid (Platt) ve izotonik kalibrasyonu karsilastirir, Brier'e gore secer.

    Kalibrasyon kumesi ikiye bolunur: yontemler A'da egitilir, B'de Brier olculur; en dusuk
    Brier'li yontem (kalibrasyonsuz dahil) secilip tum kumede yeniden egitilir. Bu,
    scikit-learn CalibratedClassifierCV(cv="prefit") ile ayni hesaptir; parametreler JSON tutulur.
    """
    raw = np.asarray(raw, dtype=float)
    y = np.asarray(y, dtype=int)
    if len(y) < 40 or y.min() == y.max():
        return {"method": "none", "params": {}, "selection": {}, "rows": int(len(y)),
                "note": "Kalibrasyon kümesi küçük ya da tek sınıflı; ham olasılık kullanılır."}
    rng = np.random.default_rng(seed)
    idx = rng.permutation(len(y))
    a, b = idx[: len(idx) // 2], idx[len(idx) // 2:]
    scores = {"none": brier(raw[b], y[b])}
    if y[a].min() != y[a].max():
        sig = {"method": "sigmoid", "params": _fit_sigmoid(raw[a], y[a])}
        iso = {"method": "isotonic", "params": _fit_isotonic(raw[a], y[a])}
        scores["sigmoid"] = brier(apply_calibration(raw[b], sig), y[b])
        scores["isotonic"] = brier(apply_calibration(raw[b], iso), y[b])
    method = min(scores, key=lambda k: (scores[k], k != "none"))
    params = {} if method == "none" else (_fit_sigmoid(raw, y) if method == "sigmoid" else _fit_isotonic(raw, y))
    return {"method": method, "params": params, "rows": int(len(y)),
            "selection": {k: round(v, 5) for k, v in scores.items()},
            "note": "Yöntem, kalibrasyon kümesinin ayrı yarısında Brier skoruna göre seçildi."}


def reliability(p, y, bins: int = 10) -> tuple[list[dict], float]:
    """Esit genislikte kovalarla guvenilirlik egrisi ve ECE. 5'ten az kayitli kovanin
    gozlenen orani gosterilmez (ECE hesabina yine katilir: toplu bir sayidir)."""
    p = np.asarray(p, dtype=float)
    y = np.asarray(y, dtype=float)
    n = len(y)
    out, ece = [], 0.0
    edges = np.linspace(0, 1, bins + 1)
    idx = np.clip(np.digitize(p, edges[1:-1], right=False), 0, bins - 1)
    for i in range(bins):
        m = idx == i
        c = int(m.sum())
        mean_p = float(p[m].mean()) if c else None
        obs = float(y[m].mean()) if c else None
        if c:
            ece += c / n * abs(obs - mean_p)
        shown = c >= MIN_GROUP
        out.append({"lo": round(float(edges[i]), 2), "hi": round(float(edges[i + 1]), 2), "count": c,
                    "mean_predicted": round(mean_p, 4) if shown else None,
                    "observed_rate": round(obs, 4) if shown else None, "suppressed": bool(c and not shown)})
    return out, round(float(ece), 4)


def threshold_table(p, y, thresholds=THRESHOLDS) -> list[dict]:
    """Her esikte: isaretlenme orani, kesinlik (precision), duyarlilik (recall), yanlis alarm orani."""
    p = np.asarray(p, dtype=float)
    y = np.asarray(y, dtype=int)
    pos = int(y.sum())
    neg = int(len(y) - pos)
    rows = []
    for t in thresholds:
        f = p >= t
        tp = int((f & (y == 1)).sum())
        fp = int((f & (y == 0)).sum())
        flagged = int(f.sum())
        prec = tp / flagged if flagged else None
        rec = tp / pos if pos else None
        f1 = (2 * prec * rec / (prec + rec)) if prec and rec else None
        rows.append({"threshold": float(t), "flag_rate": round(flagged / len(y), 4) if len(y) else 0.0,
                     "precision": round(prec, 4) if prec is not None else None,
                     "recall": round(rec, 4) if rec is not None else None,
                     "false_positive_rate": round(fp / neg, 4) if neg else None,
                     "f1": round(f1, 4) if f1 is not None else None})
    return rows


def recommended_threshold(table: list[dict]) -> float:
    """Bilgi amacli oneri: F1'i en yuksek esik. Esigi IK secer; oneri otomatik uygulanmaz."""
    best = max((r for r in table if r["f1"] is not None), key=lambda r: r["f1"], default=None)
    return best["threshold"] if best else DEFAULT_THRESHOLD


def calibration_report(raw_eval, y_eval, calibration: dict) -> dict:
    """Degerlendirme kumesinde ham ve kalibre olasiliklarin Brier/ECE degerleri,
    guvenilirlik egrisi (kalibre) ve esik tablosu. Model kartina yazilir."""
    raw = np.asarray(raw_eval, dtype=float)
    y = np.asarray(y_eval, dtype=int)
    cal = apply_calibration(raw, calibration)
    bins, ece = reliability(cal, y)
    raw_bins, raw_ece = reliability(raw, y)
    table = threshold_table(cal, y)
    return {
        "method": calibration.get("method", "none"),
        "selection": calibration.get("selection", {}),
        "calibration_rows": calibration.get("rows"),
        "evaluation_rows": int(len(y)),
        "brier": round(brier(cal, y), 5),
        "brier_raw": round(brier(raw, y), 5),
        "ece": ece,
        "ece_raw": raw_ece,
        "reliability": bins,
        "reliability_raw": raw_bins,
        "thresholds": table,
        "recommended_threshold": recommended_threshold(table),
        "default_threshold": DEFAULT_THRESHOLD,
    }


def metrics_with_calibration(model, calibration: dict | None, X, y) -> dict:
    """Ayni degerlendirme kumesinde AUC + Brier (champion/aday karsilastirmasi icin)."""
    raw = model.predict_proba(X)[:, 1]
    cal = apply_calibration(raw, calibration)
    auc = float(roc_auc_score(y, raw)) if len(set(np.asarray(y).tolist())) > 1 else float("nan")
    return {"auc": round(auc, 4), "brier": round(brier(cal, y), 5),
            "accuracy": round(float(np.mean((cal >= DEFAULT_THRESHOLD).astype(int) == np.asarray(y))), 4),
            "n": int(len(y)), "positive_rate": round(float(np.mean(y)), 4)}


# ------------------------------------------------------------------ tahmin dagilimi (kayma)

def score_bin(p: float) -> int:
    return int(np.searchsorted(np.asarray(SCORE_EDGES), float(p), side="right"))


def score_histogram(scores) -> dict:
    s = np.asarray(scores, dtype=float)
    counts = np.bincount([score_bin(v) for v in s], minlength=len(SCORE_EDGES) + 1).tolist()
    total = sum(counts) or 1
    return {"edges": list(SCORE_EDGES), "proportions": [round(c / total, 6) for c in counts],
            "mean": round(float(s.mean()), 4) if len(s) else None}


# ------------------------------------------------------------------ adillik denetimi

GROUP_ATTRIBUTES = {
    "department": "Departman",
    "tenure_band": "Kıdem bandı",
    "age_band": "Yaş bandı",
    "gender": "Cinsiyet",
}


def _rate(num: int, den: int) -> float | None:
    return round(num / den, 4) if den else None


def fairness_report(scores, threshold: float, groups: dict[str, list], labels=None,
                    min_group: int = MIN_GROUP) -> dict:
    """Grup basina isaretlenme orani, (etiket varsa) TPR/FPR ve en yuksek gruba gore oranlar.

    - n < min_group olan grup hic gosterilmez (adi da); TPR icin ayrilan, FPR icin kalan
      sayisi min_group'tan azsa o oran gosterilmez.
    - Oran (disparity) = grubun isaretlenme orani / en yuksek isaretlenme orani; 0,8'in altinda
      "dort-beste bir" kurali uyarisi verilir. Uyari karar degildir, insan incelemesi icindir.
    """
    s = np.asarray(scores, dtype=float)
    flagged = s >= threshold
    y = None if labels is None else np.asarray([(-1 if v is None else int(v)) for v in labels], dtype=int)
    attributes = []
    warnings = []
    for attr, values in groups.items():
        vals = np.asarray([("" if v is None else str(v)) for v in values], dtype=object)
        shown, hidden = [], 0
        for g in sorted({v for v in vals.tolist() if v}):
            m = vals == g
            n = int(m.sum())
            if n < min_group:
                hidden += 1
                continue
            row = {"group": g, "n": n, "flag_rate": _rate(int(flagged[m].sum()), n)}
            if y is not None:
                known = m & (y >= 0)
                pos = known & (y == 1)
                neg = known & (y == 0)
                row["tpr"] = _rate(int((flagged & pos).sum()), int(pos.sum())) if pos.sum() >= min_group else None
                row["fpr"] = _rate(int((flagged & neg).sum()), int(neg.sum())) if neg.sum() >= min_group else None
            shown.append(row)
        top = max((r["flag_rate"] for r in shown if r["flag_rate"] is not None), default=0.0)
        top_tpr = max((r.get("tpr") or 0.0 for r in shown), default=0.0)
        for r in shown:
            r["disparity_ratio"] = round(r["flag_rate"] / top, 4) if top else None
            r["tpr_ratio"] = round(r["tpr"] / top_tpr, 4) if r.get("tpr") is not None and top_tpr else None
            r["four_fifths_warning"] = r["disparity_ratio"] is not None and r["disparity_ratio"] < FOUR_FIFTHS
            if r["four_fifths_warning"]:
                warnings.append({"attribute": attr, "group": r["group"], "disparity_ratio": r["disparity_ratio"]})
        ratios = [r["disparity_ratio"] for r in shown if r["disparity_ratio"] is not None]
        attributes.append({"attribute": attr, "label": GROUP_ATTRIBUTES.get(attr, attr), "groups": shown,
                           "hidden_groups": hidden, "min_ratio": min(ratios) if ratios else None,
                           "comparable": len(shown) >= 2})
    return {"rows": int(len(s)), "threshold": float(threshold), "flag_rate": _rate(int(flagged.sum()), len(s)),
            "labels_used": y is not None and bool((y >= 0).any()), "min_group": min_group,
            "four_fifths": FOUR_FIFTHS, "attributes": attributes, "warnings": warnings,
            "note": ("Oran, grubun işaretlenme oranının en yüksek gruba bölümüdür; 0,8'in altı dört-beşte bir "
                     "kuralına göre incelenmelidir. 5'ten küçük gruplar gösterilmez. Bu rapor karar vermez; "
                     "farkın nedeni insan incelemesiyle değerlendirilir.")}


def tenure_band(years: float | None) -> str | None:
    if years is None or not math.isfinite(years) or years < 0:
        return None
    if years < 1:
        return "0-1"
    if years < 3:
        return "1-3"
    if years < 5:
        return "3-5"
    if years < 10:
        return "5-10"
    return "10+"


# ------------------------------------------------------------------ sade dilde aciklama

def _n(x: float, digits: int = 1) -> str:
    v = round(float(x), digits)
    return (f"{v:g}" if digits else str(int(round(v)))).replace(".", ",")


def _reason(name: str, value: float) -> tuple[str, list, str]:
    """(kod, parametreler, Turkce metin). Kodlar arayuzde ceviri anahtarina eslenir."""
    v = float(value)
    if name == "tenure_years":
        return ("tenure_short" if v < 2 else "tenure", [round(v, 1)],
                f"Kıdemi kısa ({_n(v)} yıl)" if v < 2 else f"Kıdem {_n(v)} yıl")
    if name == "compa_ratio":
        pct = int(round(v * 100))
        if v < 0.95:
            return "pay_below", [pct], f"Ücreti bant ortasının altında (%{pct})"
        if v > 1.05:
            return "pay_above", [pct], f"Ücreti bant ortasının üstünde (%{pct})"
        return "pay_mid", [pct], f"Ücreti bant ortasına yakın (%{pct})"
    if name == "last_rating":
        return "rating", [round(v, 1)], f"Son performans puanı {_n(v)}/5"
    if name == "months_since_promotion":
        m = int(round(v))
        return (("no_promotion", [m], f"Son {m} aydır terfi ya da unvan değişikliği yok") if m >= 12
                else ("recent_promotion", [m], f"Son terfi {m} ay önce"))
    if name == "overtime_hours_month":
        return "overtime", [int(round(v))], f"Aylık ortalama {int(round(v))} saat fazla mesai"
    if name == "training_hours_year":
        return "training", [int(round(v))], f"Son 12 ayda {int(round(v))} saat eğitim"
    return "feature", [name, round(v, 2)], f"{name}: {_n(v, 2)}"


def explain_reasons(names, values, contributions, top: int = 3) -> list[dict]:
    """En buyuk |SHAP| katkili ozellikleri sade cumleye cevirir: (+) riski artirir, (-) azaltir."""
    items = sorted(zip(names, values, contributions), key=lambda t: abs(t[2]), reverse=True)
    out = []
    for name, value, c in items[:top]:
        if abs(c) < 1e-6:
            continue
        code, params, text = _reason(name, value)
        sign = "+" if c > 0 else "-"
        out.append({"feature": name, "code": code, "params": params, "direction": "up" if c > 0 else "down",
                    "contribution": round(float(c), 5), "text": f"{text} ({sign})"})
    return out


# ------------------------------------------------------------------ guncellik

def freshness(trained_at: str | None, data_window: dict | None, validity_days: int,
              today: date | None = None) -> dict:
    """Model karti guncelligi: egitim tarihinden itibaren `validity_days` gun gecerli kabul edilir."""
    today = today or datetime.now(timezone.utc).date()
    if not trained_at:
        return {"validity_days": validity_days, "expires_on": None, "stale": None, "age_days": None,
                "data_range": data_window}
    try:
        trained = datetime.fromisoformat(trained_at.replace("Z", "+00:00")).date()
    except ValueError:
        return {"validity_days": validity_days, "expires_on": None, "stale": None, "age_days": None,
                "data_range": data_window}
    expires = trained + timedelta(days=validity_days)
    return {"validity_days": validity_days, "trained_on": trained.isoformat(), "expires_on": expires.isoformat(),
            "age_days": (today - trained).days, "stale": today > expires, "data_range": data_window,
            "days_left": (expires - today).days}

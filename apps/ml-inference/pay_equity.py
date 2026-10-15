"""
Ücret adaleti analizi (yalnızca İK / şirket yöneticisi; compensation-service çağırır ve denetim kaydı yazar):

  POST /compensation/pay-equity - log(aylık brüt taban ücret) meşru etkenlere göre doğrusal regresyonla
                                  açıklanır: ücret kademesi (yoksa unvan), kıdem (yıl, yıl²), departman
                                  ve (varsa) çalışma yeri. Her grup niteliği için o niteliğin KENDİSİ
                                  modelden çıkarılarak kalan (açıklanamayan) fark grup bazında ortalanır:
                                    fark% = exp(ortalama artık) - 1, %95 güven aralığıyla.

Grup nitelikleri: veri modelinde cinsiyet, yaş, uyruk gibi korunan nitelikler TUTULMAZ (KVKK veri en aza
indirme); bu nedenle rapor yalnızca departman ve kıdem bandı kırılımındadır. Bu bir ayrımcılık tespiti
DEĞİLDİR; incelenecek alanları gösterir. KVKK: 5 kişiden küçük gruplar "Diğer"de birleşir, o da 5'ten
küçükse gösterilmez. Kişi bazında sonuç (kimin ücreti sapıyor) döndürülmez; yalnızca sayı.
"""

from __future__ import annotations

import math

import numpy as np
from fastapi import APIRouter, HTTPException
from pydantic import BaseModel, ConfigDict, Field

router = APIRouter(prefix="/compensation", tags=["pay-equity"])

MIN_GROUP = 5
MIN_ROWS = 20
OTHER = "Diğer"


class PayRow(BaseModel):
    model_config = ConfigDict(extra="forbid")
    pay: float = Field(gt=0, le=1e9)
    grade: str | None = Field(default=None, max_length=120)
    title: str | None = Field(default=None, max_length=200)
    department: str | None = Field(default=None, max_length=200)
    location: str | None = Field(default=None, max_length=200)
    tenure_years: float = Field(ge=0, le=60)


class PayEquityRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")
    rows: list[PayRow] = Field(max_length=200_000)


def tenure_band(y: float) -> str:
    return "0-1" if y < 1 else "1-3" if y < 3 else "3-5" if y < 5 else "5-10" if y < 10 else "10+"


def _band(r: PayRow) -> str:
    return (r.grade or "").strip() or (r.title or "").strip() or "—"


def _merge_small(values: list[str]) -> list[str]:
    counts: dict[str, int] = {}
    for v in values:
        counts[v] = counts.get(v, 0) + 1
    return [v if counts[v] >= MIN_GROUP else OTHER for v in values]


def _design(rows: list[PayRow], factors: list[str]) -> tuple[np.ndarray, list[str]]:
    cols = [np.ones(len(rows))]
    names = ["sabit"]
    for f in factors:
        if f == "tenure":
            t = np.asarray([r.tenure_years for r in rows], dtype=float)
            cols += [t, t ** 2]
            names += ["kıdem", "kıdem²"]
            continue
        vals = {"band": [_band(r) for r in rows],
                "department": [(r.department or "—") for r in rows],
                "location": [(r.location or "—") for r in rows],
                "tenure_band": [tenure_band(r.tenure_years) for r in rows]}[f]
        if f != "band":
            # Kategorik etken; az gözlemli düzeyler birleşir (aşırı uyumu önler).
            vals = _merge_small(vals)
        levels = sorted(set(vals))
        for lv in levels[1:]:
            cols.append(np.asarray([1.0 if v == lv else 0.0 for v in vals]))
            names.append(f"{f}={lv}")
    return np.column_stack(cols), names


def _fit(X: np.ndarray, y: np.ndarray) -> tuple[np.ndarray, np.ndarray, float]:
    # Küçük sırt (ridge) cezası: tekil tasarım matrisinde (ör. her kademede 1 kişi) kararlılık için.
    lam = 1e-6
    XtX = X.T @ X + lam * np.eye(X.shape[1])
    beta = np.linalg.solve(XtX, X.T @ y)
    resid = y - X @ beta
    ss_tot = float(((y - y.mean()) ** 2).sum())
    r2 = 1 - float((resid ** 2).sum()) / ss_tot if ss_tot > 0 else 0.0
    return beta, resid, r2


def _group_gaps(groups: list[str], resid: np.ndarray, sigma: float) -> tuple[list[dict], int]:
    by: dict[str, list[float]] = {}
    for g, r in zip(groups, resid):
        by.setdefault(g, []).append(float(r))
    small = [g for g, v in by.items() if len(v) < MIN_GROUP]
    if small:
        merged = [x for g in small for x in by.pop(g)]
        by.setdefault(OTHER, []).extend(merged)
    hidden = 0
    if OTHER in by and len(by[OTHER]) < MIN_GROUP:
        hidden = len(by.pop(OTHER))
    out = []
    for g, v in by.items():
        m = float(np.mean(v))
        se = sigma / math.sqrt(len(v))
        lo, hi = m - 1.96 * se, m + 1.96 * se
        out.append({"group": g, "people": len(v), "gap_pct": round(100 * (math.exp(m) - 1), 1),
                    "ci_low_pct": round(100 * (math.exp(lo) - 1), 1), "ci_high_pct": round(100 * (math.exp(hi) - 1), 1),
                    "significant": lo > 0 or hi < 0})
    out.sort(key=lambda r: r["gap_pct"])
    return out, hidden


def analyze(req: PayEquityRequest) -> dict:
    rows = req.rows
    if len(rows) < MIN_ROWS:
        raise HTTPException(422, f"Analiz için en az {MIN_ROWS} çalışanın ücret kaydı gerekir (gelen: {len(rows)}).")
    y = np.log(np.asarray([r.pay for r in rows], dtype=float))
    has_location = any(r.location for r in rows)
    legit = ["band", "tenure", "department"] + (["location"] if has_location else [])

    X, names = _design(rows, legit)
    beta, resid, r2 = _fit(X, y)
    sigma = float(np.sqrt((resid ** 2).sum() / max(1, len(rows) - X.shape[1])))
    tenure_idx = names.index("kıdem")
    t_mean = float(np.mean([r.tenure_years for r in rows]))
    tenure_effect = beta[tenure_idx] + 2 * beta[tenure_idx + 1] * t_mean

    reports = []
    for attr, label, factors, groups in (
        ("department", "Departman", [f for f in legit if f != "department"], [(r.department or "—") for r in rows]),
        ("tenure_band", "Kıdem bandı", [f for f in legit if f != "tenure"], [tenure_band(r.tenure_years) for r in rows]),
    ):
        Xg, _ = _design(rows, factors)
        _, rg, r2g = _fit(Xg, y)
        sg = float(np.sqrt((rg ** 2).sum() / max(1, len(rows) - Xg.shape[1])))
        gaps, hidden = _group_gaps(groups, rg, sg)
        reports.append({"attribute": attr, "label": label, "controls": factors, "r2": round(r2g, 3),
                        "groups": gaps, "hidden_people": hidden})

    outliers = int(np.sum(np.abs(resid) > 2 * sigma)) if sigma > 0 else 0
    return {
        "employees": len(rows),
        "model": {
            "r2": round(r2, 3),
            "controls": legit,
            "residual_sd_pct": round(100 * (math.exp(sigma) - 1), 1),
            "tenure_effect_pct_per_year": round(100 * (math.exp(float(tenure_effect)) - 1), 2),
            "bands": len({_band(r) for r in rows}),
        },
        "reports": reports,
        "outside_2sd": outliers,
        "min_group": MIN_GROUP,
        "unavailable_attributes": ["gender", "age", "nationality"],
        "notes": [
            "Cinsiyet, yaş ve uyruk veri modelinde tutulmadığından bu kırılımlarda analiz yapılamaz (KVKK veri en aza indirme).",
            "Açıklanamayan fark: meşru etkenler (kademe/unvan, kıdem, departman" + (", çalışma yeri" if has_location else "") +
            ") sabitken grubun ortalama ücret farkıdır; nedenini insan incelemesi belirler.",
            "Çalışma yeri bilgisi olmadığından modele girmedi." if not has_location else "Çalışma yeri modele dahil edildi.",
        ],
        "method": "Sıradan en küçük kareler (log ücret), kategorik etkenlerde 5'ten küçük düzeyler birleştirilir; %95 güven aralığı.",
    }


@router.post("/pay-equity")
async def pay_equity(req: PayEquityRequest) -> dict:
    return analyze(req)

"""
Bordro ve puantaj denetimi (insan incelemesi için işaret; otomatik karar YOK):

  POST /payroll/anomaly            - hesaplanan dönemin pusula kalemlerini (fazla mesai saati, ek ödeme,
                                     kesinti, brüt) çalışanın KENDİ geçmiş dönemleriyle ve aynı dönemdeki
                                     eş grubuyla (ücret kademesi; en az 5 kişi) karşılaştırır: sağlam
                                     z-skoru (medyan/MAD, Iglewicz-Hoaglin 3,5), yıllık 270 saat fazla
                                     mesai sınırı (İş K. m.41), olağan dışı kesinti oranı; yeterli geçmiş
                                     varsa (>= 200 satır) IsolationForest.
  POST /payroll/timesheet-anomaly  - haftalık puantajda çalışanın kendi geçmişine göre ani saat artışı,
                                     45 saatlik haftalık çalışma süresinin aşılması (İş K. m.63) ve
                                     tekrarlayan eksik giriş/çıkış örüntüsü.

KVKK: istekte ad/soyad ya da çalışan kimliği yoktur; çalışan kiracıya özgü tuzla özetlenmiş takma
addır (compensation/timeshift-service üretir). Girdi saklanmaz; model her istekte bellekte kurulur.
Yanıt yalnızca işaret + gerekçedir; bordro hesaplaması ve kapatma bu yanıta bağlı değildir.
"""

from __future__ import annotations

import math
from typing import Literal

import numpy as np
from fastapi import APIRouter
from pydantic import BaseModel, ConfigDict, Field
from tr_text import tr_num

router = APIRouter(prefix="/payroll", tags=["payroll-ml"])

ROBUST_Z = 3.5
MIN_OWN = 3            # çalışanın kendi geçmişinde en az dönem
MIN_PEER = 5           # eş grubu en az kişi (KVKK: 5'ten küçük grup kıyaslanmaz)
MIN_IFOREST = 200
ANNUAL_OVERTIME_LIMIT = 270.0   # İş K. m.41: yılda en fazla 270 saat
WEEKLY_LIMIT = 45.0             # İş K. m.63: haftalık en fazla 45 saat
# Kendi geçmişi sabitse (MAD = 0) "anlamlı sıçrama" için mutlak alt sınırlar.
ABS_MIN = {"overtime_hours": 8.0, "additions": 2000.0, "deductions": 2000.0, "gross": 5000.0}

METRICS = ("overtime_hours", "additions", "deductions", "gross")
CODES = {
    "overtime_hours": "OVERTIME",
    "additions": "ADDITIONS",
    "deductions": "DEDUCTIONS",
    "gross": "GROSS",
}
LABELS = {
    "overtime_hours": "Fazla mesai saati",
    "additions": "Ek ödeme (prim/ikramiye)",
    "deductions": "Kesinti",
    "gross": "Brüt ücret",
}


def _flag(code: str, severity: Literal["low", "medium", "high"], reason: str, **details) -> dict:
    return {"code": code, "severity": severity, "reason": reason, "details": details}


def robust_z(x: float, values: np.ndarray, min_n: int) -> float | None:
    """Değiştirilmiş z-skoru: 0,6745 * (x - medyan) / MAD; MAD 0 ise ortalama mutlak sapma (x1,2533)."""
    if len(values) < min_n:
        return None
    med = float(np.median(values))
    mad = float(np.median(np.abs(values - med)))
    if mad > 0:
        return 0.6745 * (x - med) / mad
    mean_ad = float(np.mean(np.abs(values - np.mean(values))))
    if mean_ad > 0:
        return (x - med) / (1.253314 * mean_ad)
    return None


def _jump(x: float, values: np.ndarray, metric: str) -> bool:
    """Geçmiş tamamen sabitken (z hesaplanamaz) belirgin artış: medyanın 1,5 katı ve mutlak eşik üstü."""
    med = float(np.median(values))
    return x - med >= ABS_MIN[metric] and x >= 1.5 * med


# ------------------------------------------------------------------ bordro

class PayslipIn(BaseModel):
    model_config = ConfigDict(extra="forbid")
    id: str = Field(max_length=64)
    employee: str = Field(max_length=64, description="Çalışanın takma adı (hash)")
    group: str | None = Field(default=None, max_length=64, description="Eş grubu (ücret kademesi / unvan özeti)")
    overtime_hours: float = Field(ge=0, le=1000)
    additions: float = Field(ge=0, le=1e9)
    deductions: float = Field(ge=0, le=1e9)
    gross: float = Field(ge=0, le=1e9)
    unpaid_days: int = Field(default=0, ge=0, le=30)


class PayslipHistoryIn(BaseModel):
    model_config = ConfigDict(extra="forbid")
    employee: str = Field(max_length=64)
    period: str = Field(pattern=r"^\d{4}-\d{2}$")
    overtime_hours: float = Field(ge=0, le=1000)
    additions: float = Field(ge=0, le=1e9)
    deductions: float = Field(ge=0, le=1e9)
    gross: float = Field(ge=0, le=1e9)
    unpaid_days: int = Field(default=0, ge=0, le=30)


class PayrollAnomalyRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")
    period: str = Field(pattern=r"^\d{4}-(0[1-9]|1[0-2])$")
    items: list[PayslipIn] = Field(min_length=1, max_length=20000)
    history: list[PayslipHistoryIn] = Field(default_factory=list, max_length=300000)


def _features(r) -> list[float]:
    return [math.log1p(r.overtime_hours), math.log1p(r.additions), math.log1p(r.deductions), math.log1p(r.gross)]


def detect_payroll(req: PayrollAnomalyRequest) -> dict:
    year = req.period[:4]
    own: dict[str, list] = {}
    for h in req.history:
        if h.period < req.period:  # yalnızca önceki dönemler
            own.setdefault(h.employee, []).append(h)

    peers: dict[str, list[PayslipIn]] = {}
    for it in req.items:
        if it.group:
            peers.setdefault(it.group, []).append(it)

    iforest = None
    past = [h for hs in own.values() for h in hs]
    if len(past) >= MIN_IFOREST:
        from sklearn.ensemble import IsolationForest  # noqa: PLC0415

        iforest = IsolationForest(n_estimators=100, contamination=0.02, random_state=0)
        iforest.fit(np.asarray([_features(h) for h in past], dtype=float))

    results = []
    for it in req.items:
        flags: list[dict] = []
        hist = own.get(it.employee, [])
        # Ücretsiz izinli (eksik günlü) ay brütü olağan biçimde düşer; brüt kıyası yalnızca tam aylarda.
        full_hist = [h for h in hist if h.unpaid_days == 0]
        for m in METRICS:
            x = float(getattr(it, m))
            series = full_hist if m == "gross" else hist
            if m == "gross" and it.unpaid_days > 0:
                continue
            vals = np.asarray([float(getattr(h, m)) for h in series], dtype=float)
            if len(vals) >= MIN_OWN:
                z = robust_z(x, vals, MIN_OWN)
                median = round(float(np.median(vals)), 2)
                if (z is not None and z > ROBUST_Z and x - median >= ABS_MIN[m] / 4) or (z is None and _jump(x, vals, m)):
                    flags.append(_flag(f"{CODES[m]}_SPIKE_OWN", "medium" if z is None or z > 2 * ROBUST_Z else "low",
                                       f"{LABELS[m]}, çalışanın önceki dönemlerine göre olağan dışı yüksek (medyan {tr_num(median)}).",
                                       value=round(x, 2), median=median, z=None if z is None else round(z, 2), n=int(len(vals))))
            group = [p for p in peers.get(it.group or "", []) if p is not it]
            if it.group and len(group) >= MIN_PEER:
                pv = np.asarray([float(getattr(p, m)) for p in group], dtype=float)
                z = robust_z(x, pv, MIN_PEER)
                median = round(float(np.median(pv)), 2)
                if z is not None and z > ROBUST_Z and x - median >= ABS_MIN[m] / 4:
                    flags.append(_flag(f"{CODES[m]}_OUTLIER_PEER", "low",
                                       f"{LABELS[m]}, aynı kademedeki çalışanlara göre olağan dışı yüksek (medyan {tr_num(median)}).",
                                       value=round(x, 2), median=median, z=round(z, 2), n=int(len(pv))))

        ytd = sum(h.overtime_hours for h in hist if h.period.startswith(year)) + it.overtime_hours
        if ytd > ANNUAL_OVERTIME_LIMIT:
            flags.append(_flag("OVERTIME_ANNUAL_LIMIT", "high",
                               "Yıl içindeki toplam fazla mesai 270 saati aşıyor (İş K. m.41).",
                               year_to_date=round(ytd, 1), limit=ANNUAL_OVERTIME_LIMIT))
        if it.gross > 0 and it.deductions > 0.5 * it.gross:
            flags.append(_flag("DEDUCTION_RATIO_HIGH", "medium",
                               "Kesintiler brüt ücretin yarısından fazla; tutarı ve dayanağını kontrol edin.",
                               ratio=round(it.deductions / it.gross, 3)))
        if iforest is not None and not flags:
            score = float(iforest.decision_function(np.asarray([_features(it)], dtype=float))[0])
            if score < 0:
                flags.append(_flag("UNUSUAL_PATTERN", "low",
                                   "Pusula, şirketin geçmiş bordro örüntüsüne göre olağan dışı (mesai/ek ödeme/kesinti/brüt).",
                                   score=round(score, 4)))
        results.append({"id": it.id, "flags": flags})

    return {
        "period": req.period,
        "items": results,
        "flagged": sum(1 for r in results if r["flags"]),
        "history_rows": len(req.history),
        "methods": {"robust_z": ROBUST_Z, "min_own_periods": MIN_OWN, "min_peer_group": MIN_PEER,
                    "isolation_forest": iforest is not None, "annual_overtime_limit": ANNUAL_OVERTIME_LIMIT},
        "note": "İşaretler bordroyu hazırlayan ve onaylayan için bilgi amaçlıdır; dönem hesaplaması ve kapatma engellenmez.",
    }


@router.post("/anomaly")
async def payroll_anomaly(req: PayrollAnomalyRequest) -> dict:
    return detect_payroll(req)


# ------------------------------------------------------------------ puantaj (haftalık)

class WeekIn(BaseModel):
    model_config = ConfigDict(extra="forbid")
    employee: str = Field(max_length=64)
    week: str = Field(pattern=r"^\d{4}-W\d{2}$")
    worked_hours: float = Field(ge=0, le=168)
    days_worked: int = Field(ge=0, le=7)
    missing_punches: int = Field(default=0, ge=0, le=14)


class TimesheetAnomalyRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")
    weeks: list[WeekIn] = Field(min_length=1, max_length=200000)
    # Değerlendirilecek son haftalar (öncekiler yalnızca geçmiş olarak kullanılır).
    recent: int = Field(default=2, ge=1, le=12)


MIN_OWN_WEEKS = 4


def detect_timesheet(req: TimesheetAnomalyRequest) -> dict:
    all_weeks = sorted({w.week for w in req.weeks})
    recent = set(all_weeks[-req.recent:])
    by_emp: dict[str, list[WeekIn]] = {}
    for w in req.weeks:
        by_emp.setdefault(w.employee, []).append(w)

    out = []
    for emp, rows in by_emp.items():
        rows.sort(key=lambda r: r.week)
        past = [r for r in rows if r.week not in recent]
        for r in rows:
            if r.week not in recent:
                continue
            flags: list[dict] = []
            vals = np.asarray([p.worked_hours for p in past if p.days_worked > 0], dtype=float)
            if len(vals) >= MIN_OWN_WEEKS:
                z = robust_z(r.worked_hours, vals, MIN_OWN_WEEKS)
                med = round(float(np.median(vals)), 1)
                if z is not None and z > ROBUST_Z and r.worked_hours - med >= 6:
                    flags.append(_flag("WEEKLY_HOURS_SPIKE", "medium" if z > 2 * ROBUST_Z else "low",
                                       "Haftalık çalışma süresi çalışanın olağan haftalarına göre belirgin yüksek (medyan {} saat).".format(tr_num(med, 1)),
                                       hours=round(r.worked_hours, 1), median=med, z=round(z, 2), n=int(len(vals))))
            if r.worked_hours > WEEKLY_LIMIT:
                flags.append(_flag("WEEKLY_HOURS_OVER_LIMIT", "medium",
                                   "Haftalık çalışma 45 saati aşıyor; fazlası fazla mesai onayı gerektirir (İş K. m.41, m.63).",
                                   hours=round(r.worked_hours, 1), limit=WEEKLY_LIMIT))
            if r.missing_punches >= 2:
                flags.append(_flag("MISSING_PUNCHES", "low",
                                   "Bu hafta birden çok gün giriş ya da çıkış kaydı eksik.", count=r.missing_punches))
            else:
                window = [p for p in rows if p.week <= r.week][-4:]
                weeks_with_missing = sum(1 for p in window if p.missing_punches > 0)
                if r.missing_punches > 0 and weeks_with_missing >= 3:
                    flags.append(_flag("MISSING_PUNCH_PATTERN", "low",
                                       "Son 4 haftanın en az 3'ünde eksik giriş/çıkış kaydı var (tekrarlayan örüntü).",
                                       weeks=weeks_with_missing))
            if flags:
                out.append({"employee": emp, "week": r.week, "flags": flags})
    return {
        "items": out,
        "weeks_evaluated": sorted(recent),
        "methods": {"robust_z": ROBUST_Z, "min_own_weeks": MIN_OWN_WEEKS, "weekly_limit": WEEKLY_LIMIT},
        "note": "Puantaj işaretleri bilgilendirme amaçlıdır; otomatik kesinti ya da yaptırım uygulanmaz.",
    }


@router.post("/timesheet-anomaly")
async def timesheet_anomaly(req: TimesheetAnomalyRequest) -> dict:
    return detect_timesheet(req)

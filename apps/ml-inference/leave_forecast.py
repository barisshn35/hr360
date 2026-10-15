"""
Mevsimsellikli izin (devamsızlık) tahmini ve ekip kapasite görünümü.

  POST /forecast/leave-daily - günlük onaylı izinli kişi sayılarından (şirket ve ekip bazında) iş günü
                               başına izinli oranını modeller ve önümüzdeki haftalar için tahmin eder.

Model (açıklanabilir, çarpımsal):
    oran(gün) = taban oran x haftanın günü etkisi x ay etkisi x köprü/tatil komşuluğu etkisi
  * Etkiler, geçmişteki ilgili günlerin ortalama oranının taban orana bölümüdür; az gözlemli etkiler
    1'e doğru büzülür (haftanın günü/ay için k = 20, köprü/tatil komşusu için k = 3 gözlem ağırlığı).
    Ay etkisi en az 12 aylık geçmişte kullanılır.
  * Resmî tatiller (leave_public_holidays) iş günü sayılmaz. "Köprü günü": iki tatil/hafta sonu
    arasında kalan tek iş günü (ör. Perşembe tatilse Cuma); "tatil komşusu": tatile bitişik iş günü.
  * Geri test: son 8 hafta dışarıda bırakılıp kalan veriyle kurulan model o haftaları tahmin eder;
    haftalık izinli kişi-gün üzerinden MAPE (gerçekleşenin 0 olduğu haftalar hariç) ve WAPE verilir,
    düz ortalamaya (mevsimsellik yok) göre karşılaştırılır.
  * Ekip kapasitesi: her ekip için haftalık beklenen izinli oranı. KVKK: 5 kişiden küçük ekipler
    "Diğer" altında birleştirilir; o da 5'ten küçükse gösterilmez.

Kişi bazında tahmin YOKTUR; girdi yalnızca gün başına izinli SAYISIDIR (kimlik içermez).
"""

from __future__ import annotations

from datetime import date, timedelta

import numpy as np
from fastapi import APIRouter, HTTPException
from pydantic import BaseModel, ConfigDict, Field

router = APIRouter(prefix="/forecast", tags=["leave-forecast"])

MIN_GROUP = 5
SHRINK = 20.0       # haftanın günü / ay etkisi büzülme ağırlığı (gözlem)
SHRINK_KIND = 3.0   # köprü / tatil komşusu günleri yılda birkaç kez görülür: daha az büzülür
BACKTEST_WEEKS = 8
OTHER = "Diğer"


class DayCount(BaseModel):
    model_config = ConfigDict(extra="forbid")
    date: date
    absent: float = Field(ge=0, le=100000)


class TeamIn(BaseModel):
    model_config = ConfigDict(extra="forbid")
    team: str = Field(min_length=1, max_length=120)
    headcount: int = Field(ge=0, le=1_000_000)
    days: list[DayCount] = Field(default_factory=list, max_length=4000)


class DailyForecastRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")
    history_start: date
    history_end: date
    headcount: int = Field(ge=1, le=1_000_000)
    days: list[DayCount] = Field(default_factory=list, max_length=4000, description="Şirket geneli; yalnızca izinli olan günler")
    teams: list[TeamIn] = Field(default_factory=list, max_length=500)
    holidays: list[date] = Field(default_factory=list, max_length=2000)
    weeks: int = Field(default=12, ge=1, le=26)
    start: date | None = Field(default=None, description="Tahminin başladığı hafta (Pazartesi); yoksa geçmişin bitişini izleyen Pazartesi")


# ------------------------------------------------------------------ takvim

def is_workday(d: date, holidays: set[date]) -> bool:
    return d.weekday() < 5 and d not in holidays


def day_kind(d: date, holidays: set[date]) -> str:
    """'bridge' (iki tatil/hafta sonu arası tek iş günü), 'adjacent' (tatile bitişik iş günü) ya da 'normal'."""
    prev_off = not is_workday(d - timedelta(days=1), holidays)
    next_off = not is_workday(d + timedelta(days=1), holidays)
    if prev_off and next_off:
        return "bridge"
    near_holiday = any((d + timedelta(days=k)) in holidays for k in (-1, 1))
    # Hafta sonuna bitişik sıradan Pazartesi/Cuma tatil komşusu sayılmaz; yalnızca resmî tatile bitişik.
    if near_holiday:
        return "adjacent"
    return "normal"


def monday(d: date) -> date:
    return d - timedelta(days=d.weekday())


# ------------------------------------------------------------------ model

class SeasonalModel:
    def __init__(self, rates: dict[date, float], holidays: set[date], months_of_history: float):
        days = sorted(rates)
        vals = np.asarray([rates[d] for d in days], dtype=float)
        self.base = float(vals.mean()) if len(vals) else 0.0
        self.use_month = months_of_history >= 12

        def effects(keys: list, shrink: float = SHRINK) -> dict:
            out: dict = {}
            if self.base <= 0:
                return out
            groups: dict = {}
            for k, v in zip(keys, vals):
                groups.setdefault(k, []).append(v)
            for k, g in groups.items():
                n = len(g)
                raw = float(np.mean(g)) / self.base
                out[k] = (n * raw + shrink * 1.0) / (n + shrink)
            return out

        self.dow = effects([d.weekday() for d in days])
        self.month = effects([d.month for d in days]) if self.use_month else {}
        kinds = [day_kind(d, holidays) for d in days]
        self.kind = effects(kinds, SHRINK_KIND)
        self.kind["normal"] = 1.0 if "normal" not in self.kind else self.kind["normal"]
        self.holidays = holidays

    def rate(self, d: date) -> float:
        if not is_workday(d, self.holidays):
            return 0.0
        r = self.base * self.dow.get(d.weekday(), 1.0)
        if self.use_month:
            r *= self.month.get(d.month, 1.0)
        k = day_kind(d, self.holidays)
        if k != "normal":
            # Köprü/komşu etkisi normal günlere göre görelidir.
            r *= self.kind.get(k, 1.0) / max(self.kind.get("normal", 1.0), 1e-9)
        return max(0.0, min(1.0, r))


def _rates(counts: dict[date, float], headcount: int, start: date, end: date, holidays: set[date]) -> dict[date, float]:
    out = {}
    d = start
    while d <= end:
        if is_workday(d, holidays):
            out[d] = min(1.0, counts.get(d, 0.0) / max(headcount, 1))
        d += timedelta(days=1)
    return out


def weekly(model: SeasonalModel, headcount: int, start: date, weeks: int) -> list[dict]:
    rows = []
    for w in range(weeks):
        ws = start + timedelta(days=7 * w)
        days = [ws + timedelta(days=i) for i in range(5)]
        work = [d for d in days if is_workday(d, model.holidays)]
        person_days = sum(model.rate(d) * headcount for d in work)
        pct = (sum(model.rate(d) for d in work) / len(work)) if work else 0.0
        peak = max(work, key=model.rate) if work else None
        rows.append({
            "week_start": ws.isoformat(),
            "working_days": len(work),
            "holidays": [d.isoformat() for d in days if d in model.holidays],
            "expected_absent_pct": round(100 * pct, 1),
            "expected_person_days": round(person_days, 1),
            "expected_absent_avg": round(pct * headcount, 1),
            "peak_day": peak.isoformat() if peak else None,
            "bridge_days": [d.isoformat() for d in work if day_kind(d, model.holidays) == "bridge"],
        })
    return rows


def backtest(counts: dict[date, float], headcount: int, start: date, end: date, holidays: set[date]) -> dict | None:
    """Son BACKTEST_WEEKS tam haftayı dışarıda bırakarak haftalık izinli kişi-gün tahmini."""
    last_monday = monday(end + timedelta(days=1)) - timedelta(days=7)  # son tam hafta
    test_start = last_monday - timedelta(days=7 * (BACKTEST_WEEKS - 1))
    if (test_start - start).days < 7 * 16:
        return None  # en az 16 haftalık eğitim verisi
    train_end = test_start - timedelta(days=1)
    train = _rates(counts, headcount, start, train_end, holidays)
    months = (train_end - start).days / 30.44
    model = SeasonalModel(train, holidays, months)
    flat_rate = float(np.mean(list(train.values()))) if train else 0.0
    actual, pred, naive = [], [], []
    for w in range(BACKTEST_WEEKS):
        ws = test_start + timedelta(days=7 * w)
        work = [ws + timedelta(days=i) for i in range(5) if is_workday(ws + timedelta(days=i), holidays)]
        actual.append(sum(min(counts.get(d, 0.0), headcount) for d in work))
        pred.append(sum(model.rate(d) * headcount for d in work))
        naive.append(flat_rate * headcount * len(work))
    a, p, n = (np.asarray(x, dtype=float) for x in (actual, pred, naive))

    def mape(f):
        mask = a > 0
        return None if not mask.any() else round(float(np.mean(np.abs(a[mask] - f[mask]) / a[mask])) * 100, 1)

    def wape(f):
        return None if a.sum() <= 0 else round(float(np.abs(a - f).sum() / a.sum()) * 100, 1)

    return {
        "weeks": BACKTEST_WEEKS, "from": test_start.isoformat(), "to": (test_start + timedelta(days=7 * BACKTEST_WEEKS - 1)).isoformat(),
        "mape": mape(p), "wape": wape(p), "baseline_mape": mape(n), "baseline_wape": wape(n),
        "series": [{"week_start": (test_start + timedelta(days=7 * w)).isoformat(), "actual": round(float(a[w]), 1),
                    "forecast": round(float(p[w]), 1)} for w in range(BACKTEST_WEEKS)],
    }


def merge_small_teams(teams: list[TeamIn]) -> tuple[list[TeamIn], int]:
    """5'ten küçük ekipler "Diğer"de birleşir; birleşik grup da küçükse atılır. (ekipler, gizlenen kişi)."""
    big = [t for t in teams if t.headcount >= MIN_GROUP and t.team != OTHER]
    small = [t for t in teams if t.headcount < MIN_GROUP or t.team == OTHER]
    hidden = 0
    if small:
        hc = sum(t.headcount for t in small)
        if hc >= MIN_GROUP:
            merged: dict[date, float] = {}
            for t in small:
                for dc in t.days:
                    merged[dc.date] = merged.get(dc.date, 0.0) + dc.absent
            big.append(TeamIn(team=OTHER, headcount=hc, days=[DayCount(date=d, absent=v) for d, v in merged.items()]))
        else:
            hidden = hc
    return big, hidden


def forecast_daily(req: DailyForecastRequest) -> dict:
    if req.history_end < req.history_start:
        raise HTTPException(422, "Geçmiş aralığı geçersiz.")
    if (req.history_end - req.history_start).days < 28:
        raise HTTPException(422, "Tahmin için en az 4 haftalık geçmiş gerekir.")
    holidays = set(req.holidays)
    months = (req.history_end - req.history_start).days / 30.44
    nxt = req.history_end + timedelta(days=1)
    start = monday(req.start) if req.start else (nxt if nxt.weekday() == 0 else monday(nxt) + timedelta(days=7))

    counts = {}
    for dc in req.days:
        counts[dc.date] = counts.get(dc.date, 0.0) + dc.absent
    rates = _rates(counts, req.headcount, req.history_start, req.history_end, holidays)
    model = SeasonalModel(rates, holidays, months)
    company = weekly(model, req.headcount, start, req.weeks)

    teams, hidden = merge_small_teams(req.teams)
    team_rows = []
    for t in teams:
        tc = {}
        for dc in t.days:
            tc[dc.date] = tc.get(dc.date, 0.0) + dc.absent
        tm = SeasonalModel(_rates(tc, t.headcount, req.history_start, req.history_end, holidays), holidays, months)
        wk = weekly(tm, t.headcount, start, req.weeks)
        team_rows.append({
            "team": t.team, "headcount": t.headcount,
            "weeks": [{"week_start": w["week_start"], "expected_absent_pct": w["expected_absent_pct"],
                       "expected_absent_avg": w["expected_absent_avg"]} for w in wk],
            "max_pct": max((w["expected_absent_pct"] for w in wk), default=0.0),
        })
    team_rows.sort(key=lambda r: -r["max_pct"])

    dow_names = ["Pazartesi", "Salı", "Çarşamba", "Perşembe", "Cuma"]
    return {
        "start": start.isoformat(),
        "weeks": company,
        "teams": team_rows,
        "hidden_people": hidden,
        "min_group": MIN_GROUP,
        "effects": {
            "base_rate_pct": round(100 * model.base, 2),
            "day_of_week": [{"day": i, "label": dow_names[i], "factor": round(model.dow.get(i, 1.0), 3)} for i in range(5)],
            "month": [{"month": m, "factor": round(model.month.get(m, 1.0), 3)} for m in range(1, 13)] if model.use_month else [],
            "bridge_factor": round(model.kind.get("bridge", 1.0) / max(model.kind.get("normal", 1.0), 1e-9), 3),
            "holiday_adjacent_factor": round(model.kind.get("adjacent", 1.0) / max(model.kind.get("normal", 1.0), 1e-9), 3),
            "monthly_seasonality": model.use_month,
        },
        "backtest": backtest(counts, req.headcount, req.history_start, req.history_end, holidays),
        "method": "Çarpımsal mevsimsellik: haftanın günü + ay (≥ 12 ay geçmişte) + köprü/tatil komşuluğu; resmî tatiller iş günü sayılmaz.",
        "note": "Tahmin, onaylı izinlerin geçmiş örüntüsüne dayanır; henüz girilmemiş ya da onay bekleyen izinleri içermez. Kişi bazında tahmin yapılmaz.",
    }


@router.post("/leave-daily")
async def leave_daily(req: DailyForecastRequest) -> dict:
    return forecast_daily(req)

"""Mevsimsellikli izin tahmini: haftanın günü / köprü etkisi, geri test, küçük ekip birleştirme."""

import os
import sys
from datetime import date, timedelta

import pytest
from fastapi import HTTPException

sys.path.insert(0, os.path.dirname(os.path.dirname(__file__)))

import leave_forecast as lf  # noqa: E402

START = date(2024, 1, 1)
END = date(2026, 9, 30)
HOLIDAYS = [date(2024, 4, 23), date(2025, 4, 23), date(2026, 4, 23), date(2025, 10, 29), date(2026, 10, 29),
            date(2024, 5, 1), date(2025, 5, 1), date(2026, 5, 1), date(2026, 10, 28)]


def synth(headcount=100, friday=8.0, other=3.0, august=2.0, bridge=20.0):
    hol = set(HOLIDAYS)
    days = []
    d = START
    while d <= END:
        if lf.is_workday(d, hol):
            v = friday if d.weekday() == 4 else other
            if d.month == 8:
                v *= august
            if lf.day_kind(d, hol) == "bridge":
                v = bridge
            days.append(lf.DayCount(date=d, absent=v))
        d += timedelta(days=1)
    return days


def req(**kw):
    base = dict(history_start=START, history_end=END, headcount=100, days=synth(), holidays=HOLIDAYS, weeks=12)
    base.update(kw)
    return lf.DailyForecastRequest(**base)


def test_kopru_gunu_tanimi():
    hol = {date(2026, 10, 29)}  # Perşembe
    assert lf.day_kind(date(2026, 10, 30), hol) == "bridge"   # Cuma: tatil ile hafta sonu arası
    assert lf.day_kind(date(2026, 10, 28), hol) == "adjacent"
    assert lf.day_kind(date(2026, 10, 27), hol) == "normal"
    assert not lf.is_workday(date(2026, 10, 29), hol)


def test_haftanin_gunu_ve_ay_etkisi_ogrenilir():
    res = lf.forecast_daily(req())
    dow = {d["day"]: d["factor"] for d in res["effects"]["day_of_week"]}
    assert dow[4] > 1.5 * dow[1]
    months = {m["month"]: m["factor"] for m in res["effects"]["month"]}
    assert res["effects"]["monthly_seasonality"] is True
    assert months[8] > 1.4
    assert res["effects"]["bridge_factor"] > 2


def test_tahmin_haftalari_ve_tatil():
    res = lf.forecast_daily(req())
    assert res["start"] == "2026-10-05"
    assert len(res["weeks"]) == 12
    wk = {w["week_start"]: w for w in res["weeks"]}
    tatil_haftasi = wk["2026-10-26"]
    assert tatil_haftasi["working_days"] == 3  # 28 ve 29 Ekim tatil
    assert "2026-10-29" in tatil_haftasi["holidays"]


def test_geri_test_mape_temel_modelden_iyi():
    res = lf.forecast_daily(req())
    bt = res["backtest"]
    assert bt is not None and bt["weeks"] == 8
    assert bt["mape"] is not None and bt["mape"] < 15
    assert bt["baseline_mape"] is None or bt["mape"] <= bt["baseline_mape"]


def test_kisa_gecmiste_geri_test_yok_ve_ay_etkisi_kapali():
    res = lf.forecast_daily(req(history_start=date(2026, 7, 1), days=[d for d in synth() if d.date >= date(2026, 7, 1)]))
    assert res["backtest"] is None
    assert res["effects"]["monthly_seasonality"] is False


def test_cok_kisa_gecmis_reddedilir():
    with pytest.raises(HTTPException):
        lf.forecast_daily(req(history_start=date(2026, 9, 20)))


def test_kucuk_ekipler_digerde_birlesir_ve_gizlenir():
    teams = [lf.TeamIn(team="Büyük", headcount=20, days=[lf.DayCount(date=date(2026, 9, 1), absent=2)]),
             lf.TeamIn(team="A", headcount=3, days=[lf.DayCount(date=date(2026, 9, 1), absent=1)]),
             lf.TeamIn(team="B", headcount=3, days=[])]
    res = lf.forecast_daily(req(teams=teams))
    names = {t["team"] for t in res["teams"]}
    assert names == {"Büyük", "Diğer"}
    assert next(t for t in res["teams"] if t["team"] == "Diğer")["headcount"] == 6
    res2 = lf.forecast_daily(req(teams=teams[:2]))
    assert {t["team"] for t in res2["teams"]} == {"Büyük"}
    assert res2["hidden_people"] == 3

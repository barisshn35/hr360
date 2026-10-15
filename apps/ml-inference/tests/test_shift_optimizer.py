"""Vardiya optimizasyonu: küçük örneklerde kesin kurallar, talep karşılama, tercih, adalet ve sezgisel yedek."""

import os
import sys
from datetime import date, timedelta

import pytest
from fastapi import HTTPException

sys.path.insert(0, os.path.dirname(os.path.dirname(__file__)))

import shift_optimizer as so  # noqa: E402

MON = date(2026, 10, 12)  # Pazartesi
DAY = {"id": "D", "start_min": 8 * 60, "end_min": 16 * 60, "break_min": 30}
EVE = {"id": "E", "start_min": 16 * 60, "end_min": 24 * 60 % 1440, "break_min": 30}
NIGHT = {"id": "N", "start_min": 22 * 60, "end_min": 6 * 60, "break_min": 30}


def req(days=7, emps=4, demand_per_day=None, solver="auto", **kw):
    ds = [MON + timedelta(days=i) for i in range(days)]
    demand_per_day = demand_per_day or {"D": 1, "N": 1}
    employees = kw.pop("employees", None) or [{"id": f"e{i}"} for i in range(emps)]
    return so.OptimizeRequest(
        days=ds, shifts=kw.pop("shifts", [DAY, EVE, NIGHT]), employees=employees,
        demand=[{"date": d, "shift": s, "required": n} for d in ds for s, n in demand_per_day.items()],
        solver=solver, time_limit_seconds=5, **kw)


@pytest.mark.parametrize("solver", ["auto", "greedy"])
def test_talep_karsilanir_kurallar_cignenmez(solver):
    r = so.optimize(req(solver=solver))
    assert r["violations"] == []
    assert r["uncovered"] == []
    assert len(r["assignments"]) == 14
    if solver == "greedy":
        assert r["solver"] == "greedy"


def test_cp_sat_kullanilir_kuruluysa():
    r = so.optimize(req())
    assert r["solver"] == ("cp-sat" if so.HAS_ORTOOLS else "greedy")


@pytest.mark.parametrize("solver", ["auto", "greedy"])
def test_gece_sonrasi_gunduz_yok_11_saat(solver):
    # İki kişi: gece + ertesi gün gündüz aynı kişiye verilemez (06:00 -> 08:00 = 2 saat dinlenme).
    r = so.optimize(req(days=3, emps=2, solver=solver))
    assert r["violations"] == []
    by = {(a["employee"], a["date"]): a["shift"] for a in r["assignments"]}
    for (e, d), s in by.items():
        if s == "N":
            nxt = (date.fromisoformat(d) + timedelta(days=1)).isoformat()
            assert by.get((e, nxt)) != "D"


@pytest.mark.parametrize("solver", ["auto", "greedy"])
def test_haftalik_45_ve_ardisik_gun(solver):
    # Tek kişi, 7 gün gündüz talebi: 7,5 saat x 6 = 45 saat; 7. gün hem 45 saati hem 6 gün sınırını aşar.
    r = so.optimize(req(days=7, emps=1, demand_per_day={"D": 1}, solver=solver))
    assert r["violations"] == []
    assert len(r["assignments"]) == 6
    assert sum(u["missing"] for u in r["uncovered"]) == 1


def test_gecmis_vardiya_dinlenmeyi_etkiler():
    # Pazar gecesi (önceki hafta) çalışan kişi pazartesi gündüz alamaz.
    emps = [{"id": "a", "history": [{"date": MON - timedelta(days=1), "shift": "N"}]}, {"id": "b"}]
    r = so.optimize(req(days=1, demand_per_day={"D": 1}, employees=emps))
    assert r["assignments"] == [{"employee": "b", "date": MON.isoformat(), "shift": "D"}]


def test_izinli_gun_atanmaz_ve_eksik_raporlanir():
    emps = [{"id": "a", "unavailable": [MON]}]
    r = so.optimize(req(days=1, demand_per_day={"D": 1}, employees=emps))
    assert r["assignments"] == []
    assert r["uncovered"] == [{"date": MON.isoformat(), "shift": "D", "skill": None, "missing": 1}]


def test_tercih_dikkate_alinir():
    emps = [{"id": "a", "avoid_types": ["Night"]}, {"id": "b", "preferred_types": ["Night"]}]
    r = so.optimize(req(days=1, demand_per_day={"D": 1, "N": 1}, employees=emps))
    by = {a["employee"]: a["shift"] for a in r["assignments"]}
    assert by == {"a": "D", "b": "N"}
    assert r["fairness"]["preference_conflicts"] == 0


def test_beceri_talebi():
    emps = [{"id": "a"}, {"id": "b", "skills": ["Ekip Lideri"]}, {"id": "c"}]
    ds = [MON]
    rq = so.OptimizeRequest(days=ds, shifts=[DAY], employees=emps,
                            demand=[{"date": MON, "shift": "D", "required": 2}, {"date": MON, "shift": "D", "required": 1, "skill": "Ekip Lideri"}])
    for solver in ("auto", "greedy"):
        rq.solver = solver
        r = so.optimize(rq)
        names = {a["employee"] for a in r["assignments"]}
        assert "b" in names and len(names) == 2, (solver, r)
        assert r["uncovered"] == []


def test_gece_adaleti():
    r = so.optimize(req(days=6, emps=3, demand_per_day={"N": 1}))
    assert r["violations"] == []
    assert r["fairness"]["night_spread"] <= 1


def test_kurala_aykiri_vardiya_kullanilmaz():
    long_night = {"id": "L", "start_min": 20 * 60, "end_min": 8 * 60, "break_min": 60}  # 11 saat net gece
    r = so.optimize(req(days=1, shifts=[DAY, long_night], demand_per_day={"L": 1, "D": 1}))
    assert {"shift": "L", "reason": "night"} in r["unusable_shifts"]
    assert all(a["shift"] != "L" for a in r["assignments"])


def test_gece_hesabi():
    n = so.ShiftDef(**NIGHT)
    d = so.ShiftDef(**DAY)
    assert so.is_night(n) and not so.is_night(d)
    assert so.net_minutes(n) == 450
    assert so.night_minutes(n) == 480


def test_ortools_yoksa_sezgisel(monkeypatch):
    monkeypatch.setattr(so, "HAS_ORTOOLS", False)
    r = so.optimize(req())
    assert r["solver"] == "greedy" and r["status"].startswith("GREEDY")
    assert r["violations"] == []


def test_dogrulama_hatalari():
    rq = req(days=1)
    rq.employees = [so.Employee(id="x"), so.Employee(id="x")]
    with pytest.raises(HTTPException):
        so.optimize_endpoint(rq)

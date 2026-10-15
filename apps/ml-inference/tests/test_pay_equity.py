"""Ücret adaleti: meşru etkenler sabitken açıklanamayan fark, 5'ten küçük grup gizleme."""

import math
import os
import random
import sys

import pytest
from fastapi import HTTPException

sys.path.insert(0, os.path.dirname(os.path.dirname(__file__)))

import pay_equity as pe  # noqa: E402


def rows(gap_dept="Satış", gap=-0.10, n_per=12, seed=1):
    rnd = random.Random(seed)
    out = []
    grades = {"G1": 40000, "G2": 60000, "G3": 90000}
    for dept in ("Mühendislik", "Satış", "Finans"):
        for i in range(n_per):
            g = ["G1", "G2", "G3"][i % 3]
            t = rnd.uniform(0, 12)
            pay = grades[g] * math.exp(0.02 * t) * math.exp(rnd.gauss(0, 0.03))
            if dept == gap_dept:
                pay *= math.exp(gap)
            out.append(pe.PayRow(pay=pay, grade=g, department=dept, tenure_years=t))
    return out


def test_departman_farki_bulunur():
    res = pe.analyze(pe.PayEquityRequest(rows=rows()))
    dept = next(r for r in res["reports"] if r["attribute"] == "department")
    assert "department" not in dept["controls"]
    satis = next(g for g in dept["groups"] if g["group"] == "Satış")
    assert -14 < satis["gap_pct"] < -4
    assert satis["significant"] is True
    assert res["model"]["tenure_effect_pct_per_year"] == pytest.approx(2.0, abs=0.8)
    assert "gender" in res["unavailable_attributes"]


def test_kucuk_grup_digerde_ya_da_gizli():
    data = rows() + [pe.PayRow(pay=50000, grade="G2", department="Hukuk", tenure_years=3) for _ in range(3)]
    res = pe.analyze(pe.PayEquityRequest(rows=data))
    dept = next(r for r in res["reports"] if r["attribute"] == "department")
    groups = {g["group"] for g in dept["groups"]}
    assert "Hukuk" not in groups
    assert dept["hidden_people"] == 3
    for r in res["reports"]:
        assert all(g["people"] >= 5 for g in r["groups"])


def test_az_veriyle_analiz_yapilmaz():
    with pytest.raises(HTTPException):
        pe.analyze(pe.PayEquityRequest(rows=rows()[:10]))

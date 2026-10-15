"""Bordro ve puantaj denetimi: kendi geçmişi / eş grubu karşılaştırması, yasal sınırlar."""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(__file__)))

import payroll_ml as pm  # noqa: E402


def hist(emp, n=6, ot=5.0, add=0.0, ded=0.0, gross=50000.0, year=2026, start_month=1):
    return [pm.PayslipHistoryIn(employee=emp, period=f"{year}-{m:02d}", overtime_hours=ot + (m % 3), additions=add,
                                deductions=ded, gross=gross + 100 * (m % 2)) for m in range(start_month, start_month + n)]


def item(emp="e1", group=None, **kw):
    base = dict(id="0", employee=emp, group=group, overtime_hours=6.0, additions=0.0, deductions=0.0, gross=50000.0)
    base.update(kw)
    return pm.PayslipIn(**base)


def codes(res, idx=0):
    return {f["code"] for f in res["items"][idx]["flags"]}


def test_olagan_pusula_isaretlenmez():
    res = pm.detect_payroll(pm.PayrollAnomalyRequest(period="2026-08", items=[item()], history=hist("e1", 7)))
    assert res["items"][0]["flags"] == []
    assert res["flagged"] == 0


def test_kendi_gecmisine_gore_fazla_mesai_sicramasi():
    res = pm.detect_payroll(pm.PayrollAnomalyRequest(period="2026-08", items=[item(overtime_hours=60)], history=hist("e1", 7)))
    assert "OVERTIME_SPIKE_OWN" in codes(res)
    f = next(f for f in res["items"][0]["flags"] if f["code"] == "OVERTIME_SPIKE_OWN")
    assert f["details"]["n"] == 7 and f["details"]["median"] > 0


def test_sabit_gecmiste_ek_odeme_sicramasi():
    # Geçmişte ek ödeme hep 0 (MAD = 0): mutlak eşik üstü sıçrama işaretlenir.
    res = pm.detect_payroll(pm.PayrollAnomalyRequest(period="2026-08", items=[item(additions=25000)], history=hist("e1", 7)))
    assert "ADDITIONS_SPIKE_OWN" in codes(res)
    small = pm.detect_payroll(pm.PayrollAnomalyRequest(period="2026-08", items=[item(additions=500)], history=hist("e1", 7)))
    assert "ADDITIONS_SPIKE_OWN" not in codes(small)


def test_gelecek_donem_gecmise_sayilmaz():
    future = hist("e1", 3, ot=80, start_month=9)
    res = pm.detect_payroll(pm.PayrollAnomalyRequest(period="2026-08", items=[item(overtime_hours=6)], history=future))
    assert res["items"][0]["flags"] == []


def test_es_grubu_en_az_bes_kisi():
    peers = [item(emp=f"p{i}", group="G1", additions=1000.0 + i * 10) for i in range(5)]
    for i, p in enumerate(peers):
        p.id = str(i)
    odd = item(emp="x", group="G1", additions=40000.0)
    odd.id = "odd"
    res = pm.detect_payroll(pm.PayrollAnomalyRequest(period="2026-08", items=[*peers, odd]))
    flagged = {r["id"]: {f["code"] for f in r["flags"]} for r in res["items"]}
    assert "ADDITIONS_OUTLIER_PEER" in flagged["odd"]
    # 4 eşi olan grupta kıyas yapılmaz (KVKK: 5'ten küçük grup).
    res2 = pm.detect_payroll(pm.PayrollAnomalyRequest(period="2026-08", items=[*peers[:4], odd]))
    assert "ADDITIONS_OUTLIER_PEER" not in {f["code"] for r in res2["items"] if r["id"] == "odd" for f in r["flags"]}


def test_yillik_270_saat_siniri():
    h = [pm.PayslipHistoryIn(employee="e1", period=f"2026-{m:02d}", overtime_hours=40, additions=0, deductions=0, gross=50000)
         for m in range(1, 7)]  # 240 saat
    res = pm.detect_payroll(pm.PayrollAnomalyRequest(period="2026-07", items=[item(overtime_hours=40)], history=h))
    assert "OVERTIME_ANNUAL_LIMIT" in codes(res)
    assert next(f for f in res["items"][0]["flags"] if f["code"] == "OVERTIME_ANNUAL_LIMIT")["severity"] == "high"
    # Önceki yılın saatleri sayılmaz.
    h2 = [pm.PayslipHistoryIn(employee="e1", period=f"2025-{m:02d}", overtime_hours=40, additions=0, deductions=0, gross=50000)
          for m in range(1, 13)]
    res2 = pm.detect_payroll(pm.PayrollAnomalyRequest(period="2026-01", items=[item(overtime_hours=40)], history=h2))
    assert "OVERTIME_ANNUAL_LIMIT" not in codes(res2)


def test_yuksek_kesinti_orani():
    res = pm.detect_payroll(pm.PayrollAnomalyRequest(period="2026-08", items=[item(deductions=30000, gross=50000)]))
    assert "DEDUCTION_RATIO_HIGH" in codes(res)


def test_eksik_gunlu_ayda_brut_kiyaslanmaz():
    res = pm.detect_payroll(pm.PayrollAnomalyRequest(period="2026-08", items=[item(gross=200000, unpaid_days=5)], history=hist("e1", 7)))
    assert "GROSS_SPIKE_OWN" not in codes(res)


def test_isolation_forest_yeterli_gecmiste():
    h = []
    for e in range(40):
        h += hist(f"e{e}", 6)
    res = pm.detect_payroll(pm.PayrollAnomalyRequest(period="2026-08", items=[item()], history=h))
    assert res["methods"]["isolation_forest"] is True


# ------------------------------------------------------------------ puantaj

def weeks(emp, hours, missing=None):
    missing = missing or [0] * len(hours)
    return [pm.WeekIn(employee=emp, week=f"2026-W{i + 10:02d}", worked_hours=h, days_worked=5, missing_punches=m)
            for i, (h, m) in enumerate(zip(hours, missing))]


def test_haftalik_sicrama_ve_45_saat():
    rows = weeks("a", [40, 41, 39, 40, 42, 40, 62])
    res = pm.detect_timesheet(pm.TimesheetAnomalyRequest(weeks=rows, recent=1))
    codes_ = {f["code"] for r in res["items"] for f in r["flags"]}
    assert {"WEEKLY_HOURS_SPIKE", "WEEKLY_HOURS_OVER_LIMIT"} <= codes_
    assert res["weeks_evaluated"] == ["2026-W16"]


def test_eksik_giris_cikis_oruntusu():
    rows = weeks("b", [40] * 8, missing=[0, 0, 0, 0, 1, 1, 0, 1])
    res = pm.detect_timesheet(pm.TimesheetAnomalyRequest(weeks=rows, recent=1))
    assert {f["code"] for r in res["items"] for f in r["flags"]} == {"MISSING_PUNCH_PATTERN"}
    rows2 = weeks("c", [40] * 6, missing=[0, 0, 0, 0, 0, 3])
    res2 = pm.detect_timesheet(pm.TimesheetAnomalyRequest(weeks=rows2, recent=1))
    assert {f["code"] for r in res2["items"] for f in r["flags"]} == {"MISSING_PUNCHES"}


def test_olagan_hafta_isaretsiz():
    res = pm.detect_timesheet(pm.TimesheetAnomalyRequest(weeks=weeks("d", [40, 41, 39, 40, 42, 40]), recent=2))
    assert res["items"] == []

"""Masraf denetimi: e-Fatura karekodu ayristirma ve olagan disi / mukerrer kalem isaretleri."""

import json
import os
import sys
from datetime import date, timedelta

import pytest

sys.path.insert(0, os.path.dirname(os.path.dirname(__file__)))

import expense_ml as em  # noqa: E402


def _vkn(prefix9: str) -> str:
    for c in "0123456789":
        if em.vkn_valid(prefix9 + c):
            return prefix9 + c
    raise AssertionError


SUPPLIER = _vkn("123456789")
ETTN = "f47ac10b-58cc-4372-a567-0e02b2c3d479"


def payload(**over):
    p = {"vkntckn": SUPPLIER, "avkntckn": "10000000146", "senaryo": "EARSIVFATURA", "tip": "SATIS",
         "tarih": "2026-10-03", "no": "ABC2026000000123", "ettn": ETTN, "parabirimi": "TRY",
         "malhizmettoplam": "100,00", "kdvmatrah(20)": "100,00", "hesaplanankdv(20)": "20,00",
         "vergidahil": "120,00", "odenecek": "120,00"}
    p.update(over)
    return json.dumps(p)


# ------------------------------------------------------------------ kimlik denetimi

def test_vkn_ve_tckn_denetim_hanesi():
    assert em.vkn_valid(SUPPLIER)
    bad = SUPPLIER[:-1] + str((int(SUPPLIER[-1]) + 1) % 10)
    assert not em.vkn_valid(bad)
    assert not em.vkn_valid("12345")
    assert em.tckn_valid("10000000146")
    assert not em.tckn_valid("10000000147")
    assert not em.tckn_valid("01234567890")
    assert em.tax_id_kind("10000000146") == "tckn"


# ------------------------------------------------------------------ karekod

def test_karekod_ayristirilir_ve_dogrulanir():
    r = em.parse_einvoice_qr(payload(), today=date(2026, 10, 6))
    assert r["supplierTaxId"] == SUPPLIER and r["supplierTaxIdKind"] == "vkn"
    assert r["ettn"] == ETTN and r["invoiceNo"] == "ABC2026000000123"
    assert r["date"] == "2026-10-03" and r["currency"] == "TRY"
    assert (r["net"], r["vat"], r["total"], r["payable"]) == (100.0, 20.0, 120.0, 120.0)
    assert r["vatBreakdown"] == [{"rate": 20.0, "base": 100.0, "vat": 20.0}]
    assert r["warnings"] == []


def test_karekod_anahtar_cesitleri_ve_birden_fazla_oran():
    text = json.dumps({"VKNTCKN": SUPPLIER, "Tarih": "03.10.2026", "ETTN": ETTN.upper(),
                       "MalHizmetToplam": "1.100,00", "KDVMatrah(10)": "100,00", "HesaplananKDV(10)": "10,00",
                       "KDVMatrah(20)": "1.000,00", "HesaplananKDV(20)": "200,00", "VergiDahil": "1.310,00",
                       "Odenecek": "1.310,00", "ParaBirimi": "TL", "No": "ABC2026000000124"})
    r = em.parse_einvoice_qr(text, today=date(2026, 10, 6))
    assert r["vat"] == 210.0 and r["total"] == 1310.0 and r["currency"] == "TRY" and r["ettn"] == ETTN
    assert [b["rate"] for b in r["vatBreakdown"]] == [10.0, 20.0]
    assert r["warnings"] == []


def test_karekod_tutarsizliklari_uyari_olur():
    r = em.parse_einvoice_qr(payload(vkntckn="1234567891" if not em.vkn_valid("1234567891") else "1234567892",
                                     ettn="xyz", **{"hesaplanankdv(20)": "25,00"}, tarih="2099-01-01"),
                             today=date(2026, 10, 6))
    text = " ".join(r["warnings"])
    assert "denetim hanesi" in text and "ETTN" in text and "%20 KDV" in text and "gelecekte" in text
    assert r["ettn"] is None


def test_karekod_json_degilse_ya_da_tutar_yoksa_hata():
    with pytest.raises(em.EInvoiceError):
        em.parse_einvoice_qr("https://example.com/not-an-invoice")
    with pytest.raises(em.EInvoiceError):
        em.parse_einvoice_qr(json.dumps({"vkntckn": SUPPLIER}))


# ------------------------------------------------------------------ anomali

D0 = date(2026, 9, 1)


def hist(n, category="Meal", amount=lambda i: 100 + (i % 7) * 5, employee="e1", merchant=None):
    return [em.HistoryIn(amount=amount(i), category=category, date=D0 + timedelta(days=i % 28),
                         employee=employee, merchant=merchant) for i in range(n)]


def item(**kw):
    base = {"id": "x", "amount": 120, "category": "Meal", "date": date(2026, 10, 2), "employee": "e1"}
    base.update(kw)
    return em.ItemIn(**base)


def codes(res, i=0):
    return {f["code"] for f in res["items"][i]["flags"]}


def test_olagan_tutar_isaretlenmez():
    assert codes(em.detect_anomalies([item()], hist(30))) == set()


def test_kategori_ve_calisan_bazinda_yuksek_tutar():
    res = em.detect_anomalies([item(amount=5000)], hist(30))
    c = codes(res)
    assert {"AMOUNT_OUTLIER_CATEGORY", "AMOUNT_OUTLIER_EMPLOYEE"} <= c
    f = next(f for f in res["items"][0]["flags"] if f["code"] == "AMOUNT_OUTLIER_CATEGORY")
    assert f["details"]["n"] == 30 and f["details"]["z"] > em.ROBUST_Z and f["reason"]


def test_az_gecmiste_z_skoru_yok_ve_dusuk_tutar_isaretlenmez():
    assert codes(em.detect_anomalies([item(amount=5000)], hist(em.MIN_GROUP - 1))) == set()
    assert codes(em.detect_anomalies([item(amount=1)], hist(30))) == set()


def test_ayni_ettn_mukerrer():
    h = [em.HistoryIn(amount=50, category="Meal", date=D0, ettn=ETTN.upper())]
    res = em.detect_anomalies([item(ettn=ETTN)], h)
    assert "DUPLICATE_ETTN" in codes(res)
    assert res["items"][0]["flags"][0]["severity"] == "high"


def test_ayni_beyanda_iki_kez_ayni_ettn():
    res = em.detect_anomalies([item(id="a", ettn=ETTN), item(id="b", ettn=ETTN, amount=10)], [])
    assert "DUPLICATE_ETTN" in codes(res, 0) and "DUPLICATE_ETTN" in codes(res, 1)


def test_ayni_tutar_tarih_tedarikci_olasi_mukerrer():
    h = [em.HistoryIn(amount=120.00, category="Travel", date=date(2026, 10, 1), merchant=SUPPLIER, employee="e2")]
    assert "POSSIBLE_DUPLICATE" in codes(em.detect_anomalies([item(merchant=SUPPLIER)], h))
    # Tarih 2 gun farkli ya da tutar kurus farkli: isaret yok.
    assert codes(em.detect_anomalies([item(merchant=SUPPLIER, date=date(2026, 10, 3))], h)) == set()
    assert codes(em.detect_anomalies([item(merchant=SUPPLIER, amount=120.01)], h)) == set()


def test_ayni_fatura_numarasi():
    h = [em.HistoryIn(amount=80, category="Meal", date=D0, merchant=SUPPLIER, invoice_no="abc2026000000123")]
    assert "DUPLICATE_INVOICE_NO" in codes(em.detect_anomalies([item(merchant=SUPPLIER, invoice_no="ABC2026000000123")], h))


def test_ayni_aciklama_ozeti():
    h = [em.HistoryIn(amount=120, category="Meal", date=D0, employee="e1", text_hash="abc")]
    assert "DUPLICATE_TEXT" in codes(em.detect_anomalies([item(text_hash="abc")], h))


def test_isolation_forest_yeterli_gecmiste_calisir():
    h = hist(60)
    res = em.detect_anomalies([item()], h)
    assert res["methods"]["isolation_forest"] is True
    assert em.detect_anomalies([item()], hist(10))["methods"]["isolation_forest"] is False


def test_uc_noktalari():
    from fastapi import FastAPI
    from fastapi.testclient import TestClient

    app = FastAPI()
    app.include_router(em.router)
    c = TestClient(app)
    r = c.post("/expense/einvoice/parse", json={"text": payload()})
    assert r.status_code == 200 and r.json()["ettn"] == ETTN
    assert c.post("/expense/einvoice/parse", json={"text": "selam"}).status_code == 422
    body = {"items": [{"id": "1", "amount": 120, "category": "Meal", "date": "2026-10-02", "ettn": ETTN}],
            "history": [{"amount": 50, "category": "Meal", "date": "2026-09-01", "ettn": ETTN}]}
    r = c.post("/expense/anomaly", json=body)
    assert r.status_code == 200 and r.json()["items"][0]["flags"][0]["code"] == "DUPLICATE_ETTN"
    # Ek alan (ad, soyad vb.) kabul edilmez.
    body["items"][0]["employee_name"] = "Ayşe"
    assert c.post("/expense/anomaly", json=body).status_code == 422

"""Kişisel / özel nitelikli veri dedektörü (pii.py) birim testleri."""

import os
import sys

from fastapi import FastAPI
from fastapi.testclient import TestClient

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import pii  # noqa: E402

VALID_TCKN = "10000000146"
VALID_IBAN = "TR33 0006 1005 1978 6457 8413 26"


def cats(text):
    return [f["category"] for f in pii.scan(text)]


def test_tckn_checksum():
    assert pii.tckn_valid(VALID_TCKN)
    assert not pii.tckn_valid("10000000147")  # 11. hane yanlış
    assert not pii.tckn_valid("01234567890")  # 0 ile başlayamaz
    assert not pii.tckn_valid("1234567890")


def test_tckn_in_text_only_when_valid():
    assert cats(f"Çalışanın kimlik no: {VALID_TCKN}.") == ["tckn"]
    assert cats("Sipariş no 12345678901 teslim edildi") == []
    # Daha uzun sayı dizisinin parçası işaretlenmez
    assert cats(f"9{VALID_TCKN}") == []


def test_iban_mod97():
    assert pii.iban_valid(VALID_IBAN)
    assert pii.iban_valid("DE89370400440532013000")
    assert not pii.iban_valid("TR33 0006 1005 1978 6457 8413 27")
    assert not pii.iban_valid("TR33 0006 1005 1978 6457 8413")  # TR 26 karakter olmalı
    assert cats(f"Ödeme {VALID_IBAN} hesabına") == ["iban"]
    assert cats("Ödeme TR33 0006 1005 1978 6457 8413 27 hesabına") == []


def test_phone_and_email():
    assert cats("Bana 0532 123 45 67 numarasından ulaşın") == ["phone"]
    assert cats("+90 (532) 123-45-67") == ["phone"]
    assert cats("Sabit: 0212 555 12 34") == ["phone"]
    assert cats("Tutar 2125551234 TL") == []  # ön eksiz sabit hat sayılmaz
    assert cats("e-posta: ayse.yilmaz@ornek.com.tr") == ["email"]


def test_health_and_criminal_keywords_with_suffixes():
    assert cats("Kanser tedavisi gördüğü için") == ["health", "health"]
    assert "health" in cats("Sağlık raporunu getirdi")
    assert "health" in cats("SAGLIK RAPORU")  # Türkçe karakter olmadan / büyük harf
    assert "criminal" in cats("Adli sicil kaydında sabıkası var")
    assert "criminal" in cats("Daha önce hapis yattı")
    assert "religion" in cats("Cuma namazına gidiyor")
    assert "union" in cats("Sendikaya üye oldu")


def test_no_false_positives_on_ordinary_text():
    for t in ["Proje raporu hazırlandı", "Toplantı salı günü yapılacak", "Raporlama süreci", "Hastalık izni politikası değil maaş",
              "Kurt Mühendislik ekibi", "Ortalama skor 4,5", "Haftalık hedefler"]:
        found = cats(t)
        assert found in ([], ["health"]), (t, found)
    assert cats("Proje raporu hazırlandı") == []
    assert cats("Ortalama skor 4,5") == []
    assert cats("Haftalık hedefler") == []


def test_exclusions_not_flagged():
    assert cats("Dini bayramda memleketime gideceğim") == []
    assert cats("dini bayram tatili + yıllık izin") == []
    assert "religion" in cats("Dini inancı gereği oruç tutuyor")
    assert "religion" in cats("Bayramda camiye gidecek")


def test_spans_and_no_text_echo():
    text = f"Kimlik {VALID_TCKN} ve diyabet"
    res = pii.summarize(pii.scan(text))
    spans = {f["category"]: text[f["start"]:f["end"]] for f in res["findings"]}
    assert spans["tckn"] == VALID_TCKN
    assert spans["health"] == "diyabet"
    assert res["special_category"] is True
    assert res["categories"] == ["tckn", "health"]
    assert VALID_TCKN not in str(res)  # ham değer yanıtta yok, yalnızca maskeli ipucu
    assert res["findings"][0]["hint"] == "10*********"


def test_offsets_preserved_with_turkish_uppercase():
    text = "İstanbul: İLAÇ KULLANIYOR; ŞİZOFRENİ"
    f = pii.scan(text)
    assert [text[x["start"]:x["end"]] for x in f] == ["İLAÇ KULLANIYOR", "ŞİZOFRENİ"]


def test_rules_shape():
    rules = pii.load_rules()
    keys = [c["key"] for c in rules["categories"]]
    assert {"tckn", "iban", "phone", "email", "health", "criminal"} <= set(keys)
    for c in rules["categories"]:
        if c["kind"] == "keyword":
            assert c["special"] is True
            for s in c["stems"]:
                # Tek sözcüklü kökler en az 4 harf (kısa kökler yalnızca "words" listesinde, tam eşleşme)
                assert len(s.split()) > 1 or len(pii.fold(s)) >= 4, s


def test_endpoints():
    app = FastAPI()
    app.include_router(pii.router)
    c = TestClient(app)
    r = c.post("/pii/scan", json={"text": f"TCKN {VALID_TCKN}"})
    assert r.status_code == 200 and r.json()["categories"] == ["tckn"]
    assert "text" not in r.json()
    assert c.post("/pii/scan", json={"text": "x" * 20001}).status_code == 422
    assert c.post("/pii/scan", json={"text": "a", "extra": 1}).status_code == 422
    r = c.get("/pii/rules")
    assert r.status_code == 200 and r.json()["version"] == pii.load_rules()["version"]

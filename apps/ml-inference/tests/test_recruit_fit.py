"""Aday–ilan uygunluk puanı (Dalga 10, madde 51): açıklamalar, otomatik ret yok, önyargı koruması."""

import os
import sys

import pytest
from fastapi import FastAPI
from fastapi.testclient import TestClient

sys.path.insert(0, os.path.dirname(os.path.dirname(__file__)))

import recruit_fit as rf  # noqa: E402

POSTING = rf.PostingIn(title="Kıdemli Backend Geliştirici", text="""
Ekibimize backend geliştirici arıyoruz.
Aranan nitelikler:
- En az 5 yıl yazılım geliştirme deneyimi
- C#, PostgreSQL ve Docker bilgisi
- Bilgisayar mühendisliği bölümü mezunu
Tercih sebebi: Kubernetes
""")


def run(*cands, year=2026):
    return rf.fit(rf.FitRequest(posting=POSTING, candidates=list(cands), this_year=year))


def test_ilan_ayristirma():
    p = rf.parse_posting(POSTING.title, POSTING.text)
    assert p["min_years"] == 5
    assert {"c#", "postgresql", "docker"} <= set(p["required"])
    assert "kubernetes" in p["preferred"] and "kubernetes" not in p["required"]
    assert any("mühendisliği" in q for q in p["qualifications"])


def test_puan_aciklamali_ve_karar_yok():
    good = rf.CandidateIn(ref="a1", text="2016 - günümüz Backend geliştirici. C#, PostgreSQL, Docker, Kubernetes. Bilgisayar mühendisliği bölümü mezunu.")
    weak = rf.CandidateIn(ref="a2", text="Grafik tasarım, Photoshop ve illüstrasyon alanında 2 yıl deneyim. Güzel sanatlar mezunu.")
    res = run(good, weak)
    by = {r["ref"]: r for r in res["results"]}
    assert res["decision"] == "none" and all(r["decision"] == "none" for r in res["results"])
    assert by["a1"]["score"] > by["a2"]["score"]
    assert res["results"][0]["ref"] == "a1"
    c = by["a1"]["components"]
    assert c["required_skills"]["value"] == 1.0 and c["experience_years"]["years"] == 10
    assert any("Zorunlu becerilerden 3/3" in x for x in by["a1"]["reasons"])
    assert by["a2"]["components"]["required_skills"]["missing"]
    assert "otomatik eleme" in res["note"].lower()


def test_demografik_ve_kisisel_ifadeler_puani_degistirmez():
    base = "C#, PostgreSQL ve Docker ile 6 yıl deneyim. Bilgisayar mühendisliği bölümü mezunu."
    noisy = ("Cinsiyet: Kadın\nMedeni hal: Evli\nDoğum tarihi: 01.01.1990\n34 yaşında\nAdres: Atatürk Cad. No:5 Kadıköy\n"
             "Fotoğraf: ekte\nayse@example.com 0532 123 45 67\n" + base)
    r1 = run(rf.CandidateIn(ref="x", text=base))["results"][0]
    r2 = run(rf.CandidateIn(ref="y", text=noisy))["results"][0]
    assert r1["score"] == r2["score"]
    red = r2["redactions"]
    assert red.get("demographic", 0) >= 2 and red.get("age", 0) >= 1 and red.get("address", 0) >= 1 and red.get("contact", 0) >= 2
    assert red.get("photo", 0) >= 1 or red.get("demographic", 0) >= 3
    clean, _ = rf.sanitize(noisy)
    for word in ("Kadın", "Evli", "1990", "Kadıköy", "ayse@example.com", "0532"):
        assert word not in clean


def test_sema_kimlik_alani_kabul_etmez():
    with pytest.raises(Exception):
        rf.CandidateIn(ref="z", text="x", name="Ayşe Yılmaz")
    with pytest.raises(Exception):
        rf.CandidateIn(ref="z", text="x", gender="F")
    app = FastAPI()
    app.include_router(rf.router)
    c = TestClient(app)
    body = {"posting": {"title": "Geliştirici", "text": "C# bilgisi"}, "candidates": [{"ref": "1", "text": "C#", "age": 30}]}
    assert c.post("/recruit/fit", json=body).status_code == 422


def test_kisa_metin_isaretlenir_ve_model_karti():
    r = run(rf.CandidateIn(ref="k", text="kısa"))["results"][0]
    assert "insufficient_text" in r["flags"]
    card = rf.card()
    keys = {e["attribute"] for e in card["excluded_attributes"]}
    assert {"name", "gender", "age", "photo", "address"} <= keys
    assert "Otomatik eleme/ret" in card["not_for"]


def test_deneyim_tarih_araliklari_birlesir():
    yrs, ev = rf.experience_years("2010-2014 A şirketi; 2013 - 2016 B şirketi; 2018 – halen C", 2026)
    assert yrs == 14 and "tarih aralığı" in ev

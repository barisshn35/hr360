"""Beceri çıkarımı (eş anlamlılar, katalog eşleşmesi) ve beceri haritası (5'ten küçük grup gizli)."""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(__file__)))

import skills_ml as sm  # noqa: E402
from ai_tools import parse_cv_text  # noqa: E402


def keys(found):
    return {f["key"] for f in found}


def test_es_anlamlilar_ve_kisa_beceriler():
    k = keys(sm.find_skills("5 yıl k8s ve csharp deneyimi; payroll süreçleri; Project Management"))
    assert {"kubernetes", "c#", "bordro", "proje yonetimi"} <= k
    assert "go" not in keys(sm.find_skills("projeye go live sürecinde katıldım"))
    assert "go" in keys(sm.find_skills("Backend: Go, PostgreSQL"))


def test_katalog_eslesmesi_ve_guven():
    catalog = [sm.CatalogItem(id="1", name="C# ile geliştirme"), sm.CatalogItem(id="2", name="Sunum becerisi"),
               sm.CatalogItem(id="3", name="Bütçe planlama ve kontrol"), sm.CatalogItem(id="4", name="Muhasebe")]
    res = sm.extract(sm.ExtractRequest(text="csharp ile mikroservis geliştirdim. Yıllık bütçe planlaması yaptım.", catalog=catalog))
    by = {m["id"]: m for m in res["catalog_matches"]}
    assert by["1"]["confidence"] == "high"
    assert "3" in by and by["3"]["confidence"] in ("high", "medium")
    assert "2" not in by and "4" not in by


def test_profilde_olan_beceri_onerilmez():
    res = sm.extract(sm.ExtractRequest(text="Python ve Kubernetes", known=["python", "K8s"]))
    assert keys(res["skills"]) == set()
    assert {f["key"] for f in res["already_known"]} == {"python", "kubernetes"}


def test_cv_ayristirma_es_anlamlilari_kullanir():
    cv = parse_cv_text("Ayşe Yılmaz\nayse@example.com\nDeneyim: k8s, csharp, payroll\n" + "x " * 120)
    assert {"kubernetes", "c#", "bordro"} <= set(cv.skills)


def test_beceri_haritasi_kucuk_grup_gizler():
    people = [sm.PersonSkills(department="Mühendislik", skills=["Python", "k8s"]) for _ in range(6)]
    people += [sm.PersonSkills(department="Mühendislik", skills=["Rust"]) for _ in range(2)]
    people += [sm.PersonSkills(department="Finans", skills=["Muhasebe", "Excel"]) for _ in range(5)]
    people += [sm.PersonSkills(department="Hukuk", skills=["İş hukuku"]) for _ in range(3)]
    res = sm.skill_graph(sm.GraphRequest(people=people))
    names = {n["skill"] for n in res["nodes"]}
    assert "Kubernetes" in names and "Python" in names and "Muhasebe" in names
    assert "Rust" not in names  # 2 kişi
    assert res["hidden_skills"] >= 2
    depts = {d["department"] for d in res["departments"]}
    assert "Hukuk" not in depts and "Diğer" not in depts  # 3 kişilik departman birleşip yine küçük → gizli
    assert res["hidden_people"] == 3
    edge = next(e for e in res["edges"] if {e["a"], e["b"]} == {"Python", "Kubernetes"})
    assert edge["people"] == 6 and edge["jaccard"] == 1.0


def test_sana_uygun_oneriler_aciklamali_ve_yalnizca_oneri():
    req = sm.RecommendRequest(
        me=sm.MeIn(skills=["csharp", "PostgreSQL", "Excel"], position="Yazılım Geliştirici",
                   gaps=[sm.Gap(id="g1", name="Kubernetes yönetimi", required=4, current=1), sm.Gap(id="g2", name="Sunum", required=3, current=3)],
                   completed_course_ids=["c-done"]),
        postings=[sm.PostingRef(id="p1", title="Backend Geliştirici", text="C#, PostgreSQL ve Kubernetes bilgisi"),
                  sm.PostingRef(id="p2", title="Muhasebe Uzmanı", text="Muhasebe ve bordro deneyimi")],
        mentors=[sm.MentorRef(id="m1", offers=["Kubernetes", "Docker"], free_slots=2),
                 sm.MentorRef(id="m2", offers=["Kubernetes"], free_slots=0),
                 sm.MentorRef(id="m3", offers=["Satış"], free_slots=3)],
        courses=[sm.CourseRef(id="c1", title="Kubernetes ile konteyner yönetimi", competencies=[sm.CourseCompetency(id="g1", target_level=3)]),
                 sm.CourseRef(id="c-done", title="Kubernetes giriş", competencies=[sm.CourseCompetency(id="g1", target_level=2)]),
                 sm.CourseRef(id="c3", title="Pasta yapımı")],
    )
    res = sm.recommend(req)
    assert [p["id"] for p in res["postings"]] == ["p1"]
    assert "Kubernetes" in res["postings"][0]["missing"] and res["postings"][0]["reasons"]
    assert [m["id"] for m in res["mentors"]] == ["m1"]  # dolu mentor ve ilgisiz mentor önerilmez
    assert [c["id"] for c in res["courses"]] == ["c1"]  # tamamlanan ve ilgisiz eğitim önerilmez
    assert "Yetkinlik açığını kapatır" in res["courses"][0]["reasons"][0]
    assert [g["id"] for g in res["gaps"]] == ["g1"]
    assert "otomatik" in res["note"]


def test_oneri_semasi_kimlik_kabul_etmez():
    import pytest
    with pytest.raises(Exception):
        sm.MentorRef(id="m", offers=[], free_slots=1, name="Mehmet")

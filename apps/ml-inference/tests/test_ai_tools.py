"""ai_tools router'inin birim testleri (kimlik dogrulamasiz, yalnizca router).

Calistirma:  cd apps/ml-inference && python -m pytest -q tests
"""

from fastapi import FastAPI
from fastapi.testclient import TestClient

from ai_tools import router

app = FastAPI()
app.include_router(router)  # router zaten /ai onekli
client = TestClient(app)

CV = """Ayşe Kaya
ayse.kaya@example.com | +90 532 111 22 33 | İstanbul
linkedin.com/in/aysekaya

Deneyim
2019 - 2024 Kıdemli Yazılım Geliştirici, Örnek A.Ş. — C#, .NET, PostgreSQL, Docker
2016 - 2019 Yazılım Geliştirici, Deneme Ltd. — Python, React

Eğitim
Orta Doğu Teknik Üniversitesi, Bilgisayar Mühendisliği Lisans

Diller: İngilizce (ileri), Almanca (başlangıç)
"""


def test_cv_parse_text_extracts_contact_and_skills():
    r = client.post("/ai/cv/parse-text", json={"text": CV})
    assert r.status_code == 200, r.text
    d = r.json()
    assert d["email"] == "ayse.kaya@example.com"
    assert d["phone"]
    skills = {s.lower() for s in d["skills"]}
    assert {"c#", "postgresql", "docker"} <= skills
    assert d["experience_years"] and d["experience_years"] >= 7


def test_cv_parse_rejects_unknown_file_type():
    r = client.post("/ai/cv/parse", files={"file": ("cv.exe", b"MZ\x00\x00", "application/octet-stream")})
    assert r.status_code in (400, 415, 422)


def test_bias_check_flags_discriminatory_phrases():
    r = client.post("/ai/jobs/bias-check", json={"text": "25-35 yaş arası, askerliğini yapmış erkek adaylar aranıyor."})
    assert r.status_code == 200
    d = r.json()
    assert d["findings"], d
    assert any(f["severity"] == "high" for f in d["findings"])
    assert d["score"] < 100


def test_bias_check_clean_text():
    r = client.post("/ai/jobs/bias-check", json={"text": "Takım çalışmasına yatkın, iletişimi güçlü bir backend geliştirici arıyoruz."})
    assert r.status_code == 200
    assert not [f for f in r.json()["findings"] if f["severity"] == "high"]


def test_job_draft_contains_skills_and_passes_bias():
    r = client.post("/ai/jobs/draft", json={"title": "Backend Geliştirici", "skills": ["C#", "PostgreSQL"], "level": "senior"})
    assert r.status_code == 200
    d = r.json()
    assert "C#" in d["text"] and "PostgreSQL" in d["text"]
    assert not [f for f in d["bias"]["findings"] if f["severity"] == "high"]


def test_performance_summary():
    r = client.post("/ai/performance/summary", json={
        "name": "Mehmet",
        "score": 4.2, "previous_score": 3.6,
        "goals": [{"title": "API gecikmesini düşür", "progress": 100}, {"title": "Dokümantasyon", "progress": 40}],
        "reviews": [{"strengths": "Teknik derinliği yüksek, ekibe destek oluyor.", "improvements": "Zaman yönetimini geliştirmeli."}],
    })
    assert r.status_code == 200
    d = r.json()
    assert "Mehmet" in d["headline"] or "Mehmet" in d["paragraph"]
    assert d["goal_stats"]


def test_match_candidates_ranks_relevant_first():
    r = client.post("/ai/match/candidates", json={
        "job_title": "Veri Mühendisi", "job_text": "Python, Spark ve SQL ile veri hatları", "job_skills": ["Python", "SQL", "Spark"],
        "candidates": [
            {"id": "a", "name": "Grafik Tasarımcı", "text": "Photoshop, Illustrator, marka kimliği", "skills": ["Photoshop"]},
            {"id": "b", "name": "Veri Uzmanı", "text": "Python ve Spark ile ETL, SQL optimizasyonu", "skills": ["Python", "SQL", "Spark"]},
        ],
    })
    assert r.status_code == 200
    rows = r.json()
    assert rows[0]["id"] == "b"
    assert rows[0]["score"] > rows[-1]["score"]


def test_forecast_leave_shapes_and_bounds():
    hist = [{"month": f"2025-{m:02d}", "days": 10 + (8 if m in (7, 8) else 0)} for m in range(1, 13)]
    r = client.post("/ai/forecast/leave", json={"history": hist, "horizon": 3})
    assert r.status_code == 200
    d = r.json()
    assert [p["month"] for p in d["points"]] == ["2026-01", "2026-02", "2026-03"]
    for p in d["points"]:
        assert p["low"] <= p["forecast"] <= p["high"]
        assert p["low"] >= 0


def test_recommend_training_puts_mandatory_first_and_skips_completed():
    r = client.post("/ai/recommend/training", json={
        "position": "Backend Geliştirici", "skills": ["C#"], "development_areas": ["bulut", "kubernetes"],
        "completed_course_ids": ["done"],
        "courses": [
            {"id": "k8s", "title": "Kubernetes ile bulut dağıtımı", "category": "Teknik"},
            {"id": "kvkk", "title": "KVKK farkındalık eğitimi", "is_mandatory": True},
            {"id": "done", "title": "Kubernetes temelleri"},
            {"id": "excel", "title": "İleri Excel"},
        ],
    })
    assert r.status_code == 200
    rows = r.json()
    ids = [x["id"] for x in rows]
    assert "done" not in ids
    assert ids[0] == "kvkk"
    assert ids.index("k8s") < ids.index("excel") if "excel" in ids else True


def test_health():
    assert client.get("/ai/health").json()


def test_cv_parse_corrupt_pdf_is_client_error():
    r = client.post("/ai/cv/parse", files={"file": ("cv.pdf", b"%PDF-1.4 bozuk", "application/pdf")})
    assert r.status_code == 400


def test_cv_parse_txt_upload():
    r = client.post("/ai/cv/parse", files={"file": ("cv.txt", CV.encode(), "text/plain")})
    assert r.status_code == 200
    assert r.json()["email"] == "ayse.kaya@example.com"


def test_cv_parse_rejects_content_extension_mismatch():
    # .pdf adlı HTML, .docx adlı PDF, .txt adlı ikili/UTF-8 olmayan içerik reddedilir
    assert client.post("/ai/cv/parse", files={"file": ("cv.pdf", b"<html><script>x</script>", "application/pdf")}).status_code == 415
    assert client.post("/ai/cv/parse", files={"file": ("cv.docx", b"%PDF-1.4 x", "application/octet-stream")}).status_code == 415
    assert client.post("/ai/cv/parse", files={"file": ("cv.txt", b"PK\x03\x04abc", "text/plain")}).status_code == 415
    assert client.post("/ai/cv/parse", files={"file": ("cv.txt", "Ayşe".encode("cp1254"), "text/plain")}).status_code == 415


def test_ocr_sniff_image():
    from ocr import sniff_image
    assert sniff_image(b"\x89PNG\r\n\x1a\n....") == "image/png"
    assert sniff_image(b"\xff\xd8\xff\xe0....") == "image/jpeg"
    assert sniff_image(b"RIFF\x00\x00\x00\x00WEBPVP8 ") == "image/webp"
    assert sniff_image(b"<svg onload=alert(1)>") is None
    assert sniff_image(b"%PDF-1.7") is None


def _high(text: str) -> list[dict]:
    r = client.post("/ai/jobs/bias-check", json={"text": text})
    assert r.status_code == 200, r.text
    return [f for f in r.json()["findings"] if f["severity"] == "high"]


def test_bias_check_catches_inflected_forms():
    cases = {
        "Erkek adaylar tercih edilir.": "Cinsiyet",
        "Bay/Bayan satış danışmanı aranıyor.": "Cinsiyet",
        "Kadın çalışanlarımız için ilan.": "Cinsiyet",
        "Herhangi bir sağlık sorunu olmayan adaylar başvurabilir.": "Engellilik / sağlık",
        "Engelli olmayanlar başvursun.": "Engellilik / sağlık",
        "Bekâr olması tercih sebebidir.": "Medeni hâl / aile",
        "Evli adaylar başvurmasın.": "Medeni hâl / aile",
        "Hamile olmayan adaylar.": "Medeni hâl / aile",
        "Askerlik hizmetini tamamlamış olmak.": "Cinsiyet",
    }
    for text, cat in cases.items():
        hits = _high(text)
        assert any(f["category"] == cat for f in hits), (text, hits)
        # bulunan ifade özgün metinden kesilir (konumlar kaymamalı)
        for f in hits:
            assert text[f["start"]:f["end"]] == f["phrase"]


def test_bias_check_no_false_positive_on_neutral_words():
    assert not _high("Bayram dönemlerinde esnek çalışma; kadro genişliyor, evrak süreçleri dijital.")


def test_cv_parse_short_skills_languages_and_university():
    text = CV + "\nAraçlar: Figma, Jira. Guide hazırlama, quick wins.\n"
    d = client.post("/ai/cv/parse-text", json={"text": text}).json()
    skills = {s.lower() for s in d["skills"]}
    assert "ui" not in skills and "go" not in skills and "qa" not in skills
    assert not {"ingilizce", "almanca"} & skills  # diller beceri değil
    assert {"İngilizce", "Almanca"} <= set(d["languages"])
    assert d["universities"] == ["Orta Doğu Teknik Üniversitesi"]
    d2 = client.post("/ai/cv/parse-text", json={"text": CV + "\nUI/UX tasarımı, Go ile servis geliştirme.\n"}).json()
    assert {"ui", "ux", "go"} <= {s.lower() for s in d2["skills"]}


def test_forecast_leave_backtest_mape():
    # 30 aylık düzenli mevsimsel seri: son 3 ay dışarıda bırakılarak ölçülen hata küçük olmalı.
    hist = [{"month": f"{2024 + (i // 12)}-{i % 12 + 1:02d}", "days": 20 + (15 if i % 12 == 7 else 0) + i * 0.2} for i in range(30)]
    r = client.post("/ai/forecast/leave", json={"history": hist, "horizon": 3}).json()
    assert r["backtest_months"] == 3
    assert r["backtest_mape"] is not None and r["backtest_mape"] < 10
    short = client.post("/ai/forecast/leave", json={"history": hist[:5], "horizon": 2}).json()
    assert short["backtest_mape"] is None and short["backtest_months"] == 0

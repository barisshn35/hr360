"""Anket açık uçlu yanıtları: konu çıkarma, duygu sözlüğü, kişisel veri temizleme, 5'ten küçük grup."""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(__file__)))

import text_insights as ti  # noqa: E402
from tr_text import fold, stem  # noqa: E402

YEMEK = ["Yemekhanedeki yemekler çok kötü, yemek kalitesi düşük.", "Yemek kalitesi berbat, yemekhane menüsü değişmeli.",
         "Yemekhanede yemek çeşitleri yetersiz.", "Yemek kalitesi artırılmalı, yemekler soğuk geliyor.",
         "Yemekhane çok kalabalık ve yemek kalitesi kötü.", "Yemekler lezzetsiz, yemek kalitesi düşük."]
YONETICI = ["Yöneticim çok destekleyici, ekip ruhu harika.", "Ekip arkadaşlarım ve yöneticim çok destekleyici.",
            "Yöneticimle iletişimimiz çok iyi, ekip ruhu güzel.", "Ekip ruhu mükemmel, yöneticim adil.",
            "Yöneticim destekleyici ve ekip içi iletişim iyi.", "Ekip ruhu güçlü, yöneticim takdir ediyor."]


def test_kok_ve_indirgeme():
    assert fold("İŞYERİ Işık") == "isyeri isik"
    assert stem("ekiplerimizde") == "ekip"
    assert stem("yöneticilerimizden".replace("ö", "o")) == "yonetici"


def test_duygu_olumsuzluk_ve_bilesik():
    assert ti.sentiment("Çalışma ortamı çok güzel")[0] == "positive"
    assert ti.sentiment("Ortam iyi değil")[0] == "negative"
    assert ti.sentiment("Hiç sorun yok, her şey sorunsuz")[0] == "positive"
    assert ti.sentiment("Yöneticimi sevmiyorum")[0] == "negative"
    assert ti.sentiment("Zorunlu eğitimler var")[0] == "neutral"  # "zorunlu" ≠ "zor"
    _, strong = ti.sentiment("çok kötü")
    _, plain = ti.sentiment("kötü")
    assert strong < plain


def test_bes_yanittan_az_cikti_yok():
    res = ti.extract_topics(YEMEK[:4])
    assert res["available"] is False and res["topics"] == []


def test_konular_ayrisir_ve_duygu_dogru():
    res = ti.extract_topics(YEMEK + YONETICI, max_topics=4)
    assert res["available"] is True
    assert res["responses"] == 12
    labels = " ".join(t["label"] for t in res["topics"])
    assert "yemek" in fold(labels)
    for t in res["topics"]:
        assert t["responses"] >= 5
        assert len(t["snippets"]) <= 3
    yemek = next(t for t in res["topics"] if "yemek" in fold(t["label"]))
    assert yemek["sentiment"]["negative"] > yemek["sentiment"]["positive"]
    assert res["sentiment"]["positive"] >= 5


def test_kucuk_konu_gizlenir():
    res = ti.extract_topics(YEMEK + ["Otopark yetersiz.", "Otopark sorunu var."], max_topics=3)
    assert all(t["responses"] >= 5 for t in res["topics"])
    assert res["hidden_responses"] + sum(t["responses"] for t in res["topics"]) == 8


def test_alintida_kisisel_veri_gizlenir():
    texts = [f"Yemek kalitesi kötü, bana 0532 123 45 67 numarasından ulaşın {i}" for i in range(5)] + \
            ["Yemek kalitesi kötü, IBAN TR33 0006 1005 1978 6457 8413 26 olan hesabıma ödeme gelmedi"]
    res = ti.extract_topics(texts, max_topics=1)
    joined = " ".join(s for t in res["topics"] for s in t["snippets"])
    assert "0532" not in joined and "TR33" not in joined
    assert "[gizlendi]" in joined

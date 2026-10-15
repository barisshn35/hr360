"""Anlamsal arama: Türkçe ek toleransı, külliyat anahtarı, kiracı ayrımı, 409 ile yeniden gönderim."""

import os
import sys

import pytest
from fastapi import HTTPException

sys.path.insert(0, os.path.dirname(os.path.dirname(__file__)))

import semantic_search as ss  # noqa: E402

DOCS = [
    ss.SemanticDoc(id="kb:1", title="Yıllık izin politikası", text="1-5 yıl kıdemde 14 iş günü yıllık izin hakkı vardır. İzin talebi en az bir hafta önceden yöneticiye iletilir."),
    ss.SemanticDoc(id="kb:2", title="Masraf politikası", text="Masraflar harcamadan sonraki 30 gün içinde fişiyle beyan edilir. Yemek limiti günlük 750 TL."),
    ss.SemanticDoc(id="kb:3", title="Uzaktan çalışma", text="Haftada en fazla 2 gün evden çalışılabilir. Çekirdek saatler 10:00-16:00."),
    ss.SemanticDoc(id="lib:4", title="Hastalık raporu", text="Rapor alındığında aynı gün yöneticinize ve İK'ya bildirin; e-Rapor SGK sisteminden düşer."),
]


def req(q, docs=DOCS, key=None):
    return ss.SemanticSearchRequest(query=q, docs=docs, corpus_key=key)


def test_ek_toleransli_arama():
    cache = ss.IndexCache()
    res = ss.run_search(cache, "demo", req("izinlerimi ne zaman talep etmeliyim"))
    assert res["hits"][0]["id"] == "kb:1"
    assert res["built"] is True
    res2 = ss.run_search(cache, "demo", req("evden calismak"))
    assert res2["hits"][0]["id"] == "kb:3"
    assert res2["built"] is False  # aynı külliyat: dizin yeniden kurulmaz


def test_anahtarla_arama_ve_409():
    cache = ss.IndexCache()
    key = ss.run_search(cache, "demo", req("rapor"))["corpus_key"]
    assert key == ss.corpus_key(DOCS)
    res = ss.run_search(cache, "demo", ss.SemanticSearchRequest(query="fiş beyanı", corpus_key=key))
    assert res["hits"][0]["id"] == "kb:2"
    with pytest.raises(HTTPException) as e:
        ss.run_search(cache, "baska-kiraci", ss.SemanticSearchRequest(query="fiş", corpus_key=key))
    assert e.value.status_code == 409


def test_icerik_degisince_anahtar_degisir():
    changed = [*DOCS[:3], ss.SemanticDoc(id="lib:4", title="Hastalık raporu", text="Değişti")]
    assert ss.corpus_key(changed) != ss.corpus_key(DOCS)
    assert ss.corpus_key(list(reversed(DOCS))) == ss.corpus_key(DOCS)


def test_bos_ve_alakasiz_sorgu():
    cache = ss.IndexCache()
    assert ss.run_search(cache, "t", req("zzzz qqqq"))["hits"] == []
    assert ss.run_search(cache, "t", req("izin", docs=[]))["hits"] == []


def test_lru_sinir():
    cache = ss.IndexCache(max_tenants=2)
    for t in ("a", "b", "c"):
        ss.run_search(cache, t, req("izin"))
    assert cache.get("a", ss.corpus_key(DOCS)) is None
    assert cache.get("c", ss.corpus_key(DOCS)) is not None


def test_uzun_belge_pasajlara_bolunur():
    long = ss.SemanticDoc(id="x", title="Uzun", text=" ".join(f"kelime{i}" for i in range(200)) + " fazla mesai onayı")
    ps = ss.passages(long)
    assert len(ps) >= 3
    res = ss.run_search(ss.IndexCache(), "t", req("fazla mesai", docs=[long, *DOCS]))
    assert res["hits"][0]["id"] == "x" and "mesai" in res["hits"][0]["snippet"]

"""
Politika / bilgi bankası / duyuru metinlerinde anlamsal arama (büyük model YOK):

  POST /semantic/search - governance-service kiracının belgelerini (bilgi bankası maddeleri, doküman
                          kütüphanesinin güncel sürümleri, duyurular) gönderir; servis belgeleri ~60
                          sözcüklük pasajlara böler ve üç temsili birleştirir:
                            * sözcük TF-IDF (Türkçe hafif ek budama, 1-2 gram)
                            * karakter 3-5 gram TF-IDF (yazım farkı / ek toleransı)
                            * LSA (TF-IDF + TruncatedSVD; 10+ pasajda) — eş/yakın anlamlı terimleri
                              aynı gizil boyutlarda toplar
                          Skor = ağırlıklı kosinüs benzerliği; belge skoru en iyi pasajınki, parçacık o
                          pasajdır.

Dizin: pgvector kurulu olmadığından (pg_available_extensions'ta 'vector' yok) vektörler veritabanında
TUTULMAZ; LSA uzayı külliyata göre kurulduğundan belge eklenince/değişince zaten tümüyle yeniden
hesaplanması gerekir. Dizin kiracı başına bellekte tutulur (en fazla MAX_TENANTS kiracı, LRU) ve
külliyat anahtarı (her belgenin SHA-256 içerik özetinden) değişince yeniden kurulur. Anahtar istekteki
metinden SERVİSTE hesaplanır; böylece istemci başka içerikle aynı anahtarı "zehirleyemez". Önbellek
kiracı (jetondaki organizasyon) ile ayrılır: bir kiracının jetonu başka kiracının dizinini kullanamaz.

Görünürlük süzgeci (kim hangi belgeyi görebilir) governance-service'tedir; bu servis yalnızca sıralar.
"""

from __future__ import annotations

import hashlib
import threading
from collections import OrderedDict
from collections.abc import Callable

import numpy as np
from fastapi import APIRouter, Depends, HTTPException
from pydantic import BaseModel, ConfigDict, Field

from tr_text import fold, stem, tokens

MAX_TENANTS = 64
PASSAGE_WORDS = 60
PASSAGE_OVERLAP = 15
MIN_SCORE = 0.04
SNIPPET_LEN = 260


class SemanticDoc(BaseModel):
    model_config = ConfigDict(extra="forbid")
    id: str = Field(min_length=1, max_length=80)
    title: str = Field(default="", max_length=300)
    text: str = Field(default="", max_length=200_000)


class SemanticSearchRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")
    query: str = Field(min_length=2, max_length=300)
    k: int = Field(default=10, ge=1, le=50)
    corpus_key: str | None = Field(default=None, max_length=64)
    docs: list[SemanticDoc] | None = Field(default=None, max_length=5000)


def doc_hash(title: str, text: str) -> str:
    return hashlib.sha256(f"{title}\n{text}".encode()).hexdigest()


def corpus_key(docs: list[SemanticDoc]) -> str:
    """Belge kimliği + içerik özetlerinin sıralı birleşiminin SHA-256'sı (governance aynısını hesaplar)."""
    lines = sorted(f"{d.id}:{doc_hash(d.title, d.text)}" for d in docs)
    return hashlib.sha256("\n".join(lines).encode()).hexdigest()


def passages(doc: SemanticDoc) -> list[str]:
    words = doc.text.split()
    if not words:
        return [doc.title]
    out = []
    step = PASSAGE_WORDS - PASSAGE_OVERLAP
    for i in range(0, max(1, len(words) - PASSAGE_OVERLAP), step):
        out.append(" ".join(words[i:i + PASSAGE_WORDS]))
        if i + PASSAGE_WORDS >= len(words):
            break
    return out


def _word_analyzer(s: str) -> list[str]:
    st = [stem(t) for t in tokens(s)]
    return st + [f"{a} {b}" for a, b in zip(st, st[1:])]


class SemanticIndex:
    def __init__(self, key: str, docs: list[SemanticDoc]):
        from sklearn.decomposition import TruncatedSVD  # noqa: PLC0415
        from sklearn.feature_extraction.text import TfidfVectorizer  # noqa: PLC0415

        self.key = key
        self.doc_ids: list[str] = []
        self.texts: list[str] = []
        indexed: list[str] = []
        for d in docs:
            for p in passages(d):
                self.doc_ids.append(d.id)
                self.texts.append(p)
                # Başlık her pasaja eklenir (başlık eşleşmesi tüm belgeyi öne çıkarır).
                indexed.append(f"{d.title} {d.title} {p}")
        self.n_docs = len(docs)
        self.word = TfidfVectorizer(analyzer=_word_analyzer, sublinear_tf=True, min_df=1)
        self.char = TfidfVectorizer(analyzer="char_wb", ngram_range=(3, 5), sublinear_tf=True, min_df=1,
                                    preprocessor=fold, max_features=200_000)
        try:
            self.Xw = self.word.fit_transform(indexed)
        except ValueError:
            self.Xw = None
        self.Xc = self.char.fit_transform(indexed) if indexed else None
        self.svd = None
        self.Z = None
        if self.Xw is not None and self.Xw.shape[0] >= 10 and self.Xw.shape[1] >= 10:
            n = int(min(100, self.Xw.shape[0] - 1, self.Xw.shape[1] - 1))
            self.svd = TruncatedSVD(n_components=n, random_state=0)
            self.Z = _normalize(self.svd.fit_transform(self.Xw))

    @property
    def method(self) -> str:
        return ("Sözcük TF-IDF + karakter 3-5 gram TF-IDF" + (" + LSA (TruncatedSVD)" if self.svd is not None else "")
                + "; Türkçe hafif ek budama; kosinüs benzerliği.")

    def search(self, query: str, k: int) -> list[dict]:
        if not self.texts:
            return []
        score = np.zeros(len(self.texts))
        if self.Xw is not None:
            qw = self.word.transform([query])
            sw = (self.Xw @ qw.T).toarray().ravel()
            if self.svd is not None:
                qz = _normalize(self.svd.transform(qw))
                sz = (self.Z @ qz.T).ravel() if np.any(qz) else np.zeros(len(self.texts))
                score += 0.45 * sw + 0.30 * np.clip(sz, 0, None)
            else:
                score += 0.6 * sw
        if self.Xc is not None:
            qc = self.char.transform([query])
            score += (0.25 if self.svd is not None else 0.4) * (self.Xc @ qc.T).toarray().ravel()
        best: dict[str, tuple[float, int]] = {}
        for i, s in enumerate(score):
            d = self.doc_ids[i]
            if d not in best or s > best[d][0]:
                best[d] = (float(s), i)
        ranked = sorted(best.items(), key=lambda kv: -kv[1][0])
        out = []
        for d, (s, i) in ranked:
            if s < MIN_SCORE:
                break
            text = self.texts[i]
            out.append({"id": d, "score": round(s, 4),
                        "snippet": text if len(text) <= SNIPPET_LEN else text[: SNIPPET_LEN - 1].rstrip() + "…"})
            if len(out) == k:
                break
        return out


def _normalize(m: np.ndarray) -> np.ndarray:
    norms = np.linalg.norm(m, axis=1, keepdims=True)
    norms[norms == 0] = 1.0
    return m / norms


class IndexCache:
    """Kiracı -> dizin (LRU). Her kiracı için yalnızca güncel külliyatın dizini tutulur."""

    def __init__(self, max_tenants: int = MAX_TENANTS):
        self._data: OrderedDict[str, SemanticIndex] = OrderedDict()
        self._lock = threading.Lock()
        self.max = max_tenants

    def get(self, tenant: str, key: str) -> SemanticIndex | None:
        with self._lock:
            idx = self._data.get(tenant)
            if idx is None or idx.key != key:
                return None
            self._data.move_to_end(tenant)
            return idx

    def put(self, tenant: str, idx: SemanticIndex) -> None:
        with self._lock:
            self._data[tenant] = idx
            self._data.move_to_end(tenant)
            while len(self._data) > self.max:
                self._data.popitem(last=False)


def run_search(cache: IndexCache, tenant: str, req: SemanticSearchRequest) -> dict:
    built = False
    if req.docs is not None:
        key = corpus_key(req.docs)
        idx = cache.get(tenant, key)
        if idx is None:
            idx = SemanticIndex(key, req.docs)
            cache.put(tenant, idx)
            built = True
    else:
        key = req.corpus_key or ""
        idx = cache.get(tenant, key) if key else None
        if idx is None:
            # governance belgelerle yeniden gönderir (servis yeniden başlamış ya da külliyat değişmiş).
            raise HTTPException(409, {"code": "index_missing", "message": "Dizin yok ya da güncel değil; belgelerle yeniden gönderin."})
    return {"corpus_key": key, "built": built, "documents": idx.n_docs, "hits": idx.search(req.query, req.k), "method": idx.method}


def build_router(verify_token: Callable, tenant_of: Callable[[dict], str]) -> APIRouter:
    router = APIRouter(prefix="/semantic", tags=["semantic-search"])
    cache = IndexCache()

    @router.post("/search")
    def search(req: SemanticSearchRequest, token_info: dict = Depends(verify_token)) -> dict:
        return run_search(cache, tenant_of(token_info), req)

    return router

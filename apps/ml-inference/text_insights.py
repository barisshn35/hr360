"""
Anket açık uçlu yanıtlarında konu ve duygu çıkarımı (Türkçe, yerel):

  POST /text/topics  - yanıt metinlerinden TF-IDF + NMF ile konular (Türkçe durak sözcükler ve hafif ek
                       budama, tr_text.py), sözlük tabanlı duygu (tr_sentiment.json; olumsuzluk ve
                       pekiştirme kuralları) ve her konu için temsilî alıntılar.

KVKK / anonimlik:
  * En az 5 yanıt yoksa hiçbir çıktı üretilmez (available = false).
  * Bir konu ancak en az 5 yanıtta geçiyorsa gösterilir; alıntılar ve duygu dağılımı yalnızca bu
    konularda verilir. Daha küçük konuların yanıtları "gizlenen yanıt" olarak sayılır.
  * Alıntılar pii.py ile taranır: TCKN, IBAN, telefon, e-posta ve özel nitelikli veri sözcükleri
    "[gizlendi]" ile değiştirilir; alıntı 220 karakterle kısaltılır. Kişi adlarını güvenilir biçimde
    tanıyan bir model kullanılmaz (sınırlılık) - alıntılar İK ekranında, denetim kaydıyla gösterilir.
  * Metin saklanmaz, dışarı gönderilmez; LLM yoktur.
"""

from __future__ import annotations

import json
import math
import os
import re
from collections import Counter
from functools import lru_cache

import numpy as np
from fastapi import APIRouter
from pydantic import BaseModel, ConfigDict, Field

import pii
from tr_text import STOPWORDS, fold, stem

router = APIRouter(prefix="/text", tags=["text-insights"])

MIN_GROUP = 5
SNIPPET_LEN = 220
LEXICON_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "tr_sentiment.json")
_WORD = re.compile(r"[^\W_]+(?:[+#][^\W_]*)?", re.UNICODE)


# ------------------------------------------------------------------ PII temizleme

def scrub(text: str) -> str:
    """pii.scan bulgularını "[gizlendi]" ile değiştirir (konumlar özgün metindedir)."""
    found = pii.scan(text or "")
    out, last = [], 0
    for f in sorted(found, key=lambda x: x["start"]):
        if f["start"] < last:
            continue
        out.append(text[last:f["start"]])
        out.append("[gizlendi]")
        last = f["end"]
    out.append(text[last:])
    return "".join(out)


def snippet(text: str) -> str:
    s = re.sub(r"\s+", " ", scrub(text)).strip()
    return s if len(s) <= SNIPPET_LEN else s[: SNIPPET_LEN - 1].rstrip() + "…"


# ------------------------------------------------------------------ duygu

@lru_cache(maxsize=1)
def lexicon() -> dict:
    with open(LEXICON_PATH, encoding="utf-8") as f:
        return json.load(f)


_VERB_NEG = re.compile(r"^(m[aeiu](y|d|z|m)|m[ae]z)")


def sentiment(text: str) -> tuple[str, float]:
    """('positive'|'negative'|'neutral', puan). Kural: kök önek eşleşmesi, ardından 2 sözcük içinde
    olumsuzlayıcı (değil, yok...) kutbu çevirir; önündeki pekiştirici (çok, aşırı...) 1,5 kat."""
    lx = lexicon()
    toks = re.findall(r"[a-z0-9]+", fold(text))
    phrases = [(tuple(p.split()), pol) for key, pol in (("positive", 1), ("negative", -1)) for p in lx[key] if " " in p]
    singles = [(p, pol) for key, pol in (("positive", 1), ("negative", -1)) for p in lx[key] if " " not in p]
    verbs = [(tuple(p.split()), pol) for key, pol in (("verbs_positive", 1), ("verbs_negative", -1)) for p in lx[key]]
    excl = tuple(lx["exclusions"])
    pos_comp = tuple(lx["positive_compounds"])
    negators = set(lx["negators"])
    intens = {i for i in lx["intensifiers"] if " " not in i}
    score = 0.0
    i = 0
    while i < len(toks):
        t = toks[i]
        pol, used = 0, 1
        if t.startswith(pos_comp):
            pol = 1
        else:
            for words, p in phrases:
                n = len(words)
                if tuple(toks[i:i + n - 1]) == words[:-1] and i + n - 1 < len(toks) and toks[i + n - 1].startswith(words[-1]):
                    pol, used = p, n
                    break
            if not pol and not t.startswith(excl):
                for words, p in verbs:
                    n = len(words)
                    if tuple(toks[i:i + n - 1]) == words[:-1] and i + n - 1 < len(toks) and toks[i + n - 1].startswith(words[-1]):
                        rest = toks[i + n - 1][len(words[-1]):]
                        pol, used = (-p if _VERB_NEG.match(rest) else p), n
                        break
                if not pol:
                    for w, p in singles:
                        if t.startswith(w):
                            pol = p
                            break
        if pol:
            weight = 1.5 if i > 0 and toks[i - 1] in intens else 1.0
            after = toks[i + used:i + used + 2]
            if any(a in negators for a in after):
                pol = -pol
            score += pol * weight
        i += used
    label = "positive" if score > 0 else "negative" if score < 0 else "neutral"
    return label, score


# ------------------------------------------------------------------ konular

def _tr_lower(s: str) -> str:
    return (s or "").replace("İ", "i").replace("I", "ı").lower()


def analyze_doc(text: str, surface: dict[str, Counter]) -> list[str]:
    """Belgeyi kök listesine çevirir; her kökün en sık görünen yazımını `surface`e sayar."""
    out = []
    for m in _WORD.finditer(_tr_lower(text)):
        word = m.group()
        f = fold(word)
        if len(f) < 3 or f in STOPWORDS or f.isdigit():
            continue
        st = stem(f)
        surface.setdefault(st, Counter())[word] += 1
        out.append(st)
    return out


class TopicsRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")
    texts: list[str] = Field(default_factory=list, max_length=5000)
    max_topics: int = Field(default=6, ge=1, le=10)


def extract_topics(texts: list[str], max_topics: int = 6) -> dict:
    texts = [t.strip()[:2000] for t in texts if t and t.strip()]
    n = len(texts)
    method = ("TF-IDF + NMF konu modeli (Türkçe durak sözcükler, hafif ek budama); sözlük tabanlı duygu "
              "(olumsuzluk/pekiştirme kuralları). Yerel; metin dışarı gönderilmez.")
    if n < MIN_GROUP:
        return {"available": False, "responses": n, "min_group": MIN_GROUP, "topics": [], "hidden_responses": n,
                "reason": "En az 5 metin yanıt gerekir.", "method": method}

    clean = [scrub(t) for t in texts]
    surface: dict[str, Counter] = {}
    docs = [analyze_doc(t, surface) for t in clean]
    labels = [sentiment(t)[0] for t in clean]
    overall = Counter(labels)

    from sklearn.decomposition import NMF  # noqa: PLC0415
    from sklearn.feature_extraction.text import TfidfVectorizer  # noqa: PLC0415

    def display(term: str) -> str:
        return " ".join(surface[w].most_common(1)[0][0] if w in surface else w for w in term.split())

    W = None
    terms: np.ndarray | None = None
    try:
        vec = TfidfVectorizer(analyzer=lambda d: d + [f"{a} {b}" for a, b in zip(d, d[1:])],
                              min_df=2 if n >= 10 else 1, max_df=0.95, max_features=3000, sublinear_tf=True)
        X = vec.fit_transform(docs)
        terms = np.asarray(vec.get_feature_names_out())
        k = int(min(max_topics, max(1, round(math.sqrt(n / 2))), max(1, X.shape[1] // 3)))
        nmf = NMF(n_components=k, init="nndsvda", random_state=0, max_iter=500)
        W = nmf.fit_transform(X)
        H = nmf.components_
    except ValueError:
        W = None

    topics = []
    hidden = 0
    if W is None or terms is None or W.shape[1] == 0:
        hidden = n
    else:
        assign = [int(np.argmax(W[i])) if W[i].max() > 1e-9 else -1 for i in range(n)]
        hidden += sum(1 for a in assign if a < 0)
        for j in range(W.shape[1]):
            members = [i for i in range(n) if assign[i] == j]
            if len(members) < MIN_GROUP:
                hidden += len(members)
                continue
            top_idx = np.argsort(H[j])[::-1][:6]
            keywords, seen = [], set()
            for ti in top_idx:
                d = display(str(terms[ti]))
                if d not in seen and H[j][ti] > 0:
                    seen.add(d)
                    keywords.append(d)
            sent = Counter(labels[i] for i in members)
            ranked = sorted(members, key=lambda i: -W[i, j])
            snippets, seen_s = [], set()
            for i in ranked:
                s = snippet(texts[i])
                if len(s) < 12 or s in seen_s:
                    continue
                seen_s.add(s)
                snippets.append(s)
                if len(snippets) == 3:
                    break
            topics.append({
                "label": ", ".join(keywords[:3]),
                "keywords": keywords[:5],
                "responses": len(members),
                "share_pct": round(100 * len(members) / n, 1),
                "sentiment": {"positive": sent["positive"], "negative": sent["negative"], "neutral": sent["neutral"]},
                "net_sentiment": round(100 * (sent["positive"] - sent["negative"]) / len(members)),
                "snippets": snippets,
            })
        topics.sort(key=lambda t: -t["responses"])
    return {
        "available": True,
        "responses": n,
        "min_group": MIN_GROUP,
        "topics": topics,
        "hidden_responses": hidden,
        "sentiment": {"positive": overall["positive"], "negative": overall["negative"], "neutral": overall["neutral"]},
        "lexicon_version": lexicon()["version"],
        "method": method,
        "note": "Alıntılar kişisel veri taramasından geçirilmiştir (TCKN, IBAN, telefon, e-posta, özel nitelikli veri). 5'ten az yanıtlı konular gösterilmez.",
    }


@router.post("/topics")
async def topics(req: TopicsRequest) -> dict:
    return extract_topics(req.texts, req.max_topics)

"""
Aday–ilan uygunluk puanı (Dalga 10, madde 51) — işe alım uzmanına YARDIMCI bir sıralama işareti.

  POST /recruit/fit       - ilan metni + (takma adlı) aday özgeçmiş metinleri -> aday başına 0-100 puan,
                            bileşenler (zorunlu beceriler, deneyim yılı, nitelikler, tercih edilen beceriler)
                            ve her bileşenin kanıtı. Otomatik ret/eleme YOKTUR; yanıt "decision": "none" taşır.
  GET  /recruit/fit/card  - model kartı: amaç, girdiler, ağırlıklar, BİLEREK dışlanan nitelikler,
                            sınırlamalar ve kullanım kuralları.

Önyargı koruması (bias guard)
-----------------------------
Puan yalnızca işle ilgili kanıta dayanır. Ad, cinsiyet, yaş/doğum tarihi, fotoğraf, adres, medeni durum,
uyruk ve KVKK m.6 özel nitelikli veriler modele GİRMEZ:
  * Girdi şeması kimlik alanı kabul etmez (ek alan = 422); aday yalnızca opak "ref" ile gelir.
  * Çağıran (governance) adayın adını metinden siler; burada ayrıca e-posta, telefon, TCKN, IBAN,
    URL, adres satırları, yaş/doğum tarihi, cinsiyet/medeni durum/uyruk ifadeleri, fotoğraf
    anmaları ve özel nitelikli sözcükler (pii.py kuralları) puanlamadan ÖNCE metinden çıkarılır.
  * Bileşen adları attrition_ml.check_features ile denetlenir (dışlanan kök geçerse 500).
Metin saklanmaz, günlüğe yazılmaz.
"""

from __future__ import annotations

import re

from fastapi import APIRouter, HTTPException
from pydantic import BaseModel, ConfigDict, Field

import attrition_ml as aml
import pii
from skills_ml import find_skills, normalize_skill
from tr_text import fold, stem, tokens

router = APIRouter(prefix="/recruit", tags=["recruit-fit"])

COMPONENTS = ("required_skills", "experience_years", "qualifications", "preferred_skills")
WEIGHTS = {"required_skills": 0.5, "experience_years": 0.2, "qualifications": 0.2, "preferred_skills": 0.1}
EXCLUDED = (
    ("name", "Ad, soyad"), ("gender", "Cinsiyet"), ("age", "Yaş / doğum tarihi"), ("photo", "Fotoğraf"),
    ("address", "Adres / ikamet yeri"), ("marital_status", "Medeni durum"), ("nationality", "Uyruk"),
    ("special_category", "KVKK m.6 özel nitelikli veri (sağlık, din, sendika, ceza mahkûmiyeti, biyometrik vb.)"),
    ("contact", "E-posta, telefon, TCKN, IBAN, bağlantılar"),
)
MIN_TEXT = 40

# ------------------------------------------------------------------ metin temizleme (bias guard)

_URL = re.compile(r"https?://\S+|www\.\S+", re.I)
_ADDRESS_LINE = re.compile(r"(?im)^\s*(adres|address|ikamet|yaşadığı yer|location|konum)\s*[:：].*$")
_ADDRESS_PARTS = re.compile(r"(?i)\b[\wçğıöşüÇĞİÖŞÜ.]+\s+(mah(alle(si)?)?|cad(de(si)?)?|sok(ak|ağı)?|bulvar(ı)?|apt|apartmanı|sitesi|blok)\.?\b[^\n,]*")
_AGE = re.compile(r"(?i)\b(\d{2})\s*(yaşında|yaşındayım|yaş|years? old|y/o)\b")
_BIRTH = re.compile(r"(?im)(doğum\s*(tarihi|yeri)?|d\.\s*t\.|date of birth|birth\s*date|dob|born)\s*[:：]?\s*[^\n]*")
_DEMO_LINE = re.compile(r"(?im)^\s*(cinsiyet|gender|medeni\s*(hal|durum)|marital\s*status|uyruk|nationality|askerlik|military\s*service|"
                        r"ehliyet|sürücü belgesi|driving licen[cs]e|fotoğraf|photo)\s*[:：].*$")
_GENDER_WORDS = re.compile(r"(?i)\b(kadın|erkek|bayan|bay|female|male|woman|man|evli|bekar|bekâr|married|single|"
                           r"hanım|beyefendi|anne(siyim)?|baba(sıyım)?|hamile|pregnant)\b")
_PHOTO = re.compile(r"(?i)\b(fotoğraf(ım)?|vesikalık|photo(graph)?|profile picture)\b")


def sanitize(text: str) -> tuple[str, dict[str, int]]:
    """Kişisel/demografik ifadeleri "▒" ile değiştirir; kategori başına kaç bulgu silindiğini döner (değer değil)."""
    counts: dict[str, int] = {}
    t = text or ""

    def sub(rx: re.Pattern, key: str, s: str) -> str:
        n = 0

        def repl(_m):
            nonlocal n
            n += 1
            return " ▒ "
        out = rx.sub(repl, s)
        if n:
            counts[key] = counts.get(key, 0) + n
        return out

    # Önce pii.py (TCKN, IBAN, e-posta, telefon ve m.6 özel nitelikli sözcükler) — konumlar özgün metinde.
    findings = pii.scan(t)
    if findings:
        parts, last = [], 0
        for f in findings:
            parts.append(t[last:f["start"]])
            parts.append(" ▒ ")
            last = f["end"]
            key = "special_category" if f.get("special") else "contact"
            counts[key] = counts.get(key, 0) + 1
        parts.append(t[last:])
        t = "".join(parts)
    t = sub(_URL, "contact", t)
    t = sub(_ADDRESS_LINE, "address", t)
    t = sub(_ADDRESS_PARTS, "address", t)
    t = sub(_BIRTH, "age", t)
    t = sub(_AGE, "age", t)
    t = sub(_DEMO_LINE, "demographic", t)
    t = sub(_GENDER_WORDS, "demographic", t)
    t = sub(_PHOTO, "photo", t)
    return t, counts


# ------------------------------------------------------------------ ilan ayrıştırma

_REQ_HEAD = re.compile(r"(?i)^(aranan nitelikler|gereksinimler|beklentiler|nitelikler|requirements|qualifications|must have|olmazsa olmaz)\b")
_PREF_HEAD = re.compile(r"(?i)^(tercih sebebi|tercihen|artı|arti|nice to have|preferred|bonus)\b")
_ANY_HEAD = re.compile(r"(?i)^[\wçğıöşü ]{3,40}:?\s*$")
_YEARS = re.compile(r"(?i)(?:en az|minimum|min\.?|at least)\s*(\d{1,2})\s*\+?\s*(?:yıl|yil|sene|years?)")
_YEARS_PLUS = re.compile(r"(?i)\b(\d{1,2})\s*\+\s*(?:yıl|yil|sene|years?)")


def parse_posting(title: str, text: str) -> dict:
    """İlan metninden zorunlu/tercih edilen beceriler, en az deneyim yılı ve nitelik satırları."""
    req_lines, pref_lines, mode = [], [], None
    for raw in (text or "").splitlines():
        line = raw.strip(" \t-•*·")
        if not line:
            continue
        if _REQ_HEAD.match(line):
            mode = "req"
            continue
        if _PREF_HEAD.match(line):
            mode = "pref"
            rest = line.split(":", 1)[1].strip() if ":" in line else ""
            if rest:
                pref_lines.append(rest)
            continue
        if _ANY_HEAD.match(line) and line.endswith(":"):
            mode = None
            continue
        if mode == "req":
            req_lines.append(line)
        elif mode == "pref":
            pref_lines.append(line)
    req_text = "\n".join(req_lines) or f"{title}\n{text}"
    required = {f["key"]: f["label"] for f in find_skills(req_text)}
    preferred = {f["key"]: f["label"] for f in find_skills("\n".join(pref_lines)) if f["key"] not in required}
    m = _YEARS.search(text or "") or _YEARS_PLUS.search(text or "")
    min_years = float(m.group(1)) if m else None
    # Nitelik satırları: beceri adı dışında kalan anlamlı gereksinim cümleleri (ör. "Üniversitelerin bilgisayar mühendisliği bölümü mezunu").
    quals = [l for l in req_lines if len(_content(l)) >= 2 and not _YEARS.search(l) and not _YEARS_PLUS.search(l)][:12]
    return {"required": required, "preferred": preferred, "min_years": min_years, "qualifications": quals}


def _content(s: str) -> set[str]:
    return {stem(t) for t in tokens(s) if len(t) >= 3 and not t.isdigit()}


# ------------------------------------------------------------------ deneyim

_EXP_YEARS = re.compile(r"(?i)(\d{1,2})\s*\+?\s*(?:yıl|yil|sene|years?)\s*(?:lık|lik|luk|lük)?\s*(?:deneyim|tecrübe|experience|iş deneyimi)")
_RANGE = re.compile(r"(?i)\b((?:19|20)\d{2})\s*(?:-|–|—|/|to|ile)\s*((?:19|20)\d{2}|günümüz|halen|present|current|now|devam)")


def experience_years(text: str, this_year: int) -> tuple[float | None, str | None]:
    """Açıkça yazılan "5 yıl deneyim" ya da tarih aralıklarının (çakışmalar birleştirilerek) toplamı."""
    explicit = [int(m.group(1)) for m in _EXP_YEARS.finditer(text or "")]
    spans = []
    for m in _RANGE.finditer(text or ""):
        a = int(m.group(1))
        b = this_year if not m.group(2)[0].isdigit() else int(m.group(2))
        if 1970 <= a <= b <= this_year:
            spans.append((a, b))
    merged = 0.0
    if spans:
        spans.sort()
        cur_a, cur_b = spans[0]
        for a, b in spans[1:]:
            if a <= cur_b:
                cur_b = max(cur_b, b)
            else:
                merged += cur_b - cur_a
                cur_a, cur_b = a, b
        merged += cur_b - cur_a
    if explicit and max(explicit) >= merged:
        return float(max(explicit)), f"\"{max(explicit)} yıl deneyim\" ifadesi"
    if merged > 0:
        return float(merged), f"{len(spans)} tarih aralığından ~{merged:.0f} yıl"
    return None, None


# ------------------------------------------------------------------ puan

class PostingIn(BaseModel):
    model_config = ConfigDict(extra="forbid")
    title: str = Field(min_length=1, max_length=300)
    text: str = Field(default="", max_length=50_000)


class CandidateIn(BaseModel):
    """Kimlik alanı YOK: ad, e-posta, cinsiyet, yaş, fotoğraf gönderilirse 422."""
    model_config = ConfigDict(extra="forbid")
    ref: str = Field(min_length=1, max_length=64)
    text: str = Field(default="", max_length=200_000)
    skills: list[str] = Field(default_factory=list, max_length=200)


class FitRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")
    posting: PostingIn
    candidates: list[CandidateIn] = Field(default_factory=list, max_length=500)
    this_year: int | None = Field(default=None, ge=2000, le=2100)


def _qual_hit(q: str, cand_stems: set[str], cand_fold: str) -> bool:
    qs = _content(q)
    if not qs:
        return False
    if fold(q).strip() and fold(q).strip() in cand_fold:
        return True
    return len(qs & cand_stems) / len(qs) >= 0.6


def score_candidate(posting: dict, cand: CandidateIn, this_year: int) -> dict:
    clean, redactions = sanitize(cand.text)
    cand_keys = {f["key"] for f in find_skills(clean)}
    for s in cand.skills:
        n = normalize_skill(s)
        if n:
            cand_keys.add(n[0])
    comps: dict[str, dict] = {}
    req, pref = posting["required"], posting["preferred"]
    if req:
        have = sorted(k for k in req if k in cand_keys)
        comps["required_skills"] = {"value": round(len(have) / len(req), 3), "matched": [req[k] for k in have],
                                    "missing": [req[k] for k in req if k not in cand_keys]}
    if pref:
        have = sorted(k for k in pref if k in cand_keys)
        comps["preferred_skills"] = {"value": round(len(have) / len(pref), 3), "matched": [pref[k] for k in have],
                                     "missing": [pref[k] for k in pref if k not in cand_keys]}
    if posting["min_years"]:
        yrs, evidence = experience_years(clean, this_year)
        comps["experience_years"] = {"value": 0.0 if yrs is None else round(min(1.0, yrs / posting["min_years"]), 3),
                                     "years": yrs, "required": posting["min_years"], "evidence": evidence}
    if posting["qualifications"]:
        stems_c, fold_c = _content(clean), fold(clean)
        met = [q for q in posting["qualifications"] if _qual_hit(q, stems_c, fold_c)]
        comps["qualifications"] = {"value": round(len(met) / len(posting["qualifications"]), 3), "met": met,
                                   "unmet": [q for q in posting["qualifications"] if q not in met]}
    total_w = sum(WEIGHTS[k] for k in comps)
    score = int(round(100 * sum(WEIGHTS[k] * c["value"] for k, c in comps.items()) / total_w)) if total_w else None
    reasons = []
    if "required_skills" in comps:
        c = comps["required_skills"]
        reasons.append(f"Zorunlu becerilerden {len(c['matched'])}/{len(req)} eşleşti"
                       + (f": {', '.join(c['matched'][:6])}" if c["matched"] else "")
                       + (f". Eksik: {', '.join(c['missing'][:6])}" if c["missing"] else ""))
    if "experience_years" in comps:
        c = comps["experience_years"]
        reasons.append(f"Deneyim: {c['years']:.0f} yıl (beklenen en az {c['required']:.0f}; {c['evidence']})" if c["years"] is not None
                       else f"Deneyim yılı metinde bulunamadı (beklenen en az {c['required']:.0f}) — elle kontrol edin")
    if "qualifications" in comps:
        c = comps["qualifications"]
        reasons.append(f"Nitelikler: {len(c['met'])}/{len(posting['qualifications'])} karşılanıyor")
    if "preferred_skills" in comps and comps["preferred_skills"]["matched"]:
        reasons.append("Tercih sebebi: " + ", ".join(comps["preferred_skills"]["matched"][:5]))
    flags = []
    if len(clean.strip()) < MIN_TEXT and not cand.skills:
        flags.append("insufficient_text")
        reasons.append("Özgeçmiş metni çok kısa ya da yok: puan güvenilir değil, elle inceleyin")
    return {"ref": cand.ref, "score": score, "components": comps, "reasons": reasons, "flags": flags,
            "redactions": redactions, "decision": "none"}


def fit(req: FitRequest) -> dict:
    # Bileşen adlarında dışlanan bir nitelik (cinsiyet, yaş...) olmadığının denetimi.
    check = aml.check_features(list(COMPONENTS))
    if not check["passed"]:
        raise HTTPException(status_code=500, detail="Uygunluk bileşenleri dışlanan nitelik içeriyor.")
    from datetime import date
    year = req.this_year or date.today().year
    posting = parse_posting(req.posting.title, req.posting.text)
    rows = [score_candidate(posting, c, year) for c in req.candidates]
    rows.sort(key=lambda r: (r["score"] is None, -(r["score"] or 0)))
    return {
        "posting": {"required_skills": list(posting["required"].values()), "preferred_skills": list(posting["preferred"].values()),
                    "min_years": posting["min_years"], "qualifications": posting["qualifications"]},
        "results": rows,
        "weights": WEIGHTS,
        "decision": "none",
        "note": "Yardımcı sıralamadır; otomatik eleme ya da ret yapılmaz. Karar işe alım uzmanınındır. "
                "Ad, cinsiyet, yaş, fotoğraf, adres ve özel nitelikli veriler puanlamaya girmez.",
        "method": "Sözlük + eş anlamlı beceri eşleşmesi, deneyim yılı (açık ifade ya da tarih aralıkları), nitelik satırı örtüşmesi (kök eşleşmesi).",
    }


def card() -> dict:
    return {
        "name": "Aday–ilan uygunluk puanı",
        "version": "1",
        "intended_use": "İşe alım uzmanının başvuruları incelerken öncelik vermesine yardım. Karar desteği; karar değildir.",
        "not_for": ["Otomatik eleme/ret", "Tek başına işe alım kararı", "Çalışan değerlendirmesi"],
        "inputs": ["İlan başlığı ve metni", "Özgeçmiş metni (kişisel ifadeler çıkarılmış)", "Adayın beceri listesi"],
        "components": [{"name": k, "weight": WEIGHTS[k]} for k in COMPONENTS],
        "excluded_attributes": [{"attribute": k, "label": v} for k, v in EXCLUDED],
        "bias_guard": [
            "Girdi şeması kimlik alanı kabul etmez; aday opak bir referansla gelir.",
            "Adayın adı çağıran serviste, e-posta/telefon/TCKN/IBAN/URL/adres/yaş/doğum tarihi/cinsiyet/medeni durum/uyruk/fotoğraf "
            "ifadeleri ve KVKK m.6 sözcükleri bu serviste puanlamadan önce metinden çıkarılır.",
            "Bileşen adları dışlanan nitelik köklerine karşı otomatik denetlenir.",
            "Okul adı, şehir ve tarih gibi dolaylı göstergeler puana girmez (yalnızca beceri, deneyim süresi, nitelik örtüşmesi).",
        ],
        "limitations": [
            "Sözlükte olmayan beceriler eşleşmez; eş anlamlılar sınırlıdır.",
            "Deneyim yılı metinden çıkarılır; kariyer arası, yarı zamanlı çalışma ayırt edilmez.",
            "Metni kısa ya da taranmış (görüntü) özgeçmişler düşük puan alabilir: 'insufficient_text' işaretiyle gösterilir.",
            "Türkçe ve İngilizce dışındaki dillerde zayıftır.",
        ],
        "human_oversight": "Puan sıralama önerisidir; eleme ya da ret işe alım uzmanının bilinçli işlemidir. Her hesaplama denetim kaydına yazılır.",
        "data_retention": "Metin ve puan saklanmaz; istek anında hesaplanır.",
    }


@router.post("/fit")
async def fit_route(req: FitRequest) -> dict:
    return fit(req)


@router.get("/fit/card")
async def card_route() -> dict:
    return card()

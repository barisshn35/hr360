"""
Beceri çıkarımı ve şirket beceri haritası (öneri üretir; profile/aday kaydına OTOMATİK yazmaz):

  POST /skills/extract - CV / profil metninden sözlük + n-gram eşleşmesiyle beceriler (ai_tools.SKILLS +
                         skill_synonyms.json Türkçe/İngilizce eş anlamlıları) ve şirketin yetkinlik
                         kataloğuyla (learning-service yetkinlikleri) eşleşmeler; her eşleşmenin
                         kanıtı (metinde geçen ifade) ve güveni ('high' | 'medium') döner.
  POST /skills/graph   - çalışan başına (kimliksiz) departman + beceri listesinden beceri ↔ departman
                         sayıları ve birlikte görülme (co-occurrence) bağları. KVKK: 5'ten az kişide
                         görülen beceri, hücre ve bağlar gösterilmez; 5'ten küçük departmanlar "Diğer".

Kayıt yazma kararı her zaman kullanıcıdadır (çalışan profilinde onaylar, İK aday kaydında seçer).
"""

from __future__ import annotations

import json
import os
import re
from collections import Counter
from functools import lru_cache
from itertools import combinations

from fastapi import APIRouter
from pydantic import BaseModel, ConfigDict, Field

from ai_tools import LANGUAGES, SHORT_SKILLS, SKILLS
from tr_text import STOPWORDS, fold, stem

router = APIRouter(prefix="/skills", tags=["skills"])

MIN_GROUP = 5
OTHER = "Diğer"
SYN_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "skill_synonyms.json")


@lru_cache(maxsize=1)
def catalog_index() -> dict[str, dict]:
    """Kanonik anahtar -> {label, phrases}. Etiketi olmayan sözlük becerisi kendi adıyla gösterilir."""
    with open(SYN_PATH, encoding="utf-8") as f:
        data = json.load(f)
    idx: dict[str, dict] = {}
    for s in sorted(SKILLS):
        label = LANGUAGES.get(s) or (s if any(c in s for c in "#./+") or s.isupper() else s[:1].upper() + s[1:])
        idx[s] = {"label": label, "phrases": [s]}
    for e in data["skills"]:
        key = e["key"]
        entry = idx.setdefault(key, {"label": e["label"], "phrases": [key]})
        entry["label"] = e["label"]
        entry["phrases"] = list(dict.fromkeys([key, *(fold(a) for a in e.get("aliases", []))]))
    return idx


@lru_cache(maxsize=1)
def alias_lookup() -> dict[str, str]:
    """İndirgenmiş yazım -> kanonik anahtar (profil becerilerini normalize etmek için)."""
    out: dict[str, str] = {}
    for key, e in catalog_index().items():
        for p in e["phrases"]:
            out.setdefault(p, key)
        out.setdefault(fold(e["label"]), key)
    return out


def _phrase_rx(p: str) -> re.Pattern:
    return re.compile(r"(?<![a-z0-9])" + re.escape(p) + r"(?![a-z0-9])")


@lru_cache(maxsize=4096)
def _rx(p: str) -> re.Pattern:
    return _phrase_rx(p)


def find_skills(text: str) -> list[dict]:
    """Metindeki kanonik beceriler: [{key, label, evidence}] (kanıt = metinde geçen yazım)."""
    n = " " + fold(text) + " "
    out = []
    for key, e in catalog_index().items():
        if key in SHORT_SKILLS:
            m = re.search(SHORT_SKILLS[key], text or "")
            if m:
                out.append({"key": key, "label": e["label"], "evidence": m.group()})
            continue
        for p in e["phrases"]:
            if len(p) < 2:
                continue
            m = _rx(p).search(n)
            if m:
                out.append({"key": key, "label": e["label"], "evidence": p})
                break
    return out


def _content_stems(s: str) -> set[str]:
    return {stem(t) for t in re.findall(r"[a-z0-9#+.]+", fold(s)) if len(t) >= 3 and t not in STOPWORDS}


class CatalogItem(BaseModel):
    model_config = ConfigDict(extra="forbid")
    id: str = Field(max_length=64)
    name: str = Field(min_length=1, max_length=200)
    category: str | None = Field(default=None, max_length=120)


def match_catalog(text: str, catalog: list[CatalogItem], found: list[dict]) -> list[dict]:
    n = " " + fold(text) + " "
    found_keys = {f["key"]: f for f in found}
    text_stems = _content_stems(text)
    idx = catalog_index()
    out = []
    for item in catalog:
        name = fold(item.name).strip()
        hit = None
        if len(name) >= 3 and _rx(name).search(n):
            hit = ("high", item.name)
        if hit is None:
            # Katalog adı bir sözlük becerisini içeriyor ve o beceri metinde var: "C# ile geliştirme" ↔ "csharp".
            for key, f in found_keys.items():
                if any(len(p) >= 2 and _rx(p).search(" " + name + " ") for p in idx[key]["phrases"]):
                    hit = ("high", f["evidence"])
                    break
        if hit is None:
            st = _content_stems(item.name)
            common = st & text_stems
            if len(st) >= 2 and len(common) / len(st) >= 0.67:
                hit = ("medium", ", ".join(sorted(common)))
            elif len(st) == 1 and common and len(next(iter(common))) >= 5:
                hit = ("medium", next(iter(common)))
        if hit:
            out.append({"id": item.id, "name": item.name, "category": item.category, "confidence": hit[0], "evidence": hit[1]})
    out.sort(key=lambda r: (r["confidence"] != "high", r["name"]))
    return out


class ExtractRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")
    text: str = Field(min_length=1, max_length=200_000)
    catalog: list[CatalogItem] = Field(default_factory=list, max_length=5000)
    known: list[str] = Field(default_factory=list, max_length=500, description="Profilde zaten olan beceriler (öneriden çıkarılır)")


def extract(req: ExtractRequest) -> dict:
    found = find_skills(req.text)
    known = {alias_lookup().get(fold(k).strip(), fold(k).strip()) for k in req.known}
    suggestions = [f for f in found if f["key"] not in known and fold(f["label"]) not in known]
    return {
        "skills": suggestions,
        "already_known": [f for f in found if f not in suggestions],
        "catalog_matches": match_catalog(req.text, req.catalog, found),
        "method": "Sözlük + n-gram eşleşmesi (Türkçe/İngilizce eş anlamlılar, hafif ek budama). Öneridir; kaydı kullanıcı onaylar.",
    }


@router.post("/extract")
async def extract_route(req: ExtractRequest) -> dict:
    return extract(req)


# ------------------------------------------------------------------ beceri haritası

class PersonSkills(BaseModel):
    model_config = ConfigDict(extra="forbid")
    department: str | None = Field(default=None, max_length=200)
    skills: list[str] = Field(default_factory=list, max_length=200)


class GraphRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")
    people: list[PersonSkills] = Field(default_factory=list, max_length=100_000)
    max_edges: int = Field(default=60, ge=1, le=500)


def normalize_skill(raw: str) -> tuple[str, str] | None:
    """(gruplama anahtarı, görünen ad). Sözlükte varsa kanonik ad; yoksa kullanıcının yazımı."""
    s = re.sub(r"\s+", " ", (raw or "").strip())
    if not s or len(s) > 80:
        return None
    f = fold(s)
    key = alias_lookup().get(f)
    if key:
        return key, catalog_index()[key]["label"]
    return f, s[:1].upper() + s[1:]


def skill_graph(req: GraphRequest) -> dict:
    dept_size = Counter((p.department or OTHER) for p in req.people)
    small = {d for d, c in dept_size.items() if c < MIN_GROUP}
    people = []
    labels: dict[str, Counter] = {}
    for p in req.people:
        d = p.department or OTHER
        d = OTHER if d in small else d
        keys = set()
        for raw in p.skills:
            n = normalize_skill(raw)
            if n:
                keys.add(n[0])
                labels.setdefault(n[0], Counter())[n[1]] += 1
        people.append((d, keys))
    other_size = sum(1 for d, _ in people if d == OTHER)
    hidden_people = other_size if 0 < other_size < MIN_GROUP else 0

    skill_people = Counter(k for _, ks in people for k in ks)
    visible = {k for k, c in skill_people.items() if c >= MIN_GROUP}
    label = {k: labels[k].most_common(1)[0][0] for k in visible}

    cells = Counter((d, k) for d, ks in people for k in ks if k in visible and not (d == OTHER and hidden_people))
    hidden_cells = sum(1 for c in cells.values() if c < MIN_GROUP)
    nodes = []
    for k in sorted(visible, key=lambda k: (-skill_people[k], label[k])):
        depts = sorted(((d, c) for (d, kk), c in cells.items() if kk == k and c >= MIN_GROUP), key=lambda x: -x[1])
        nodes.append({"skill": label[k], "people": skill_people[k],
                      "departments": [{"department": d, "people": c} for d, c in depts]})

    pairs = Counter()
    for _, ks in people:
        for a, b in combinations(sorted(ks & visible), 2):
            pairs[(a, b)] += 1
    edges = []
    for (a, b), c in pairs.items():
        if c < MIN_GROUP:
            continue
        union = skill_people[a] + skill_people[b] - c
        edges.append({"a": label[a], "b": label[b], "people": c, "jaccard": round(c / union, 3) if union else 0.0})
    edges.sort(key=lambda e: (-e["jaccard"], -e["people"]))

    departments = []
    for d in sorted({d for d, _ in people}):
        if d == OTHER and hidden_people:
            continue
        size = sum(1 for dd, _ in people if dd == d)
        top = sorted(((label[k], c) for (dd, k), c in cells.items() if dd == d and c >= MIN_GROUP), key=lambda x: -x[1])[:8]
        departments.append({"department": d, "people": size, "top_skills": [{"skill": s, "people": c} for s, c in top]})

    return {
        "people": len(req.people),
        "people_with_skills": sum(1 for _, ks in people if ks),
        "nodes": nodes,
        "edges": edges[: req.max_edges],
        "departments": departments,
        "hidden_skills": sum(1 for c in skill_people.values() if c < MIN_GROUP),
        "hidden_cells": hidden_cells,
        "hidden_people": hidden_people,
        "min_group": MIN_GROUP,
        "note": "5'ten az kişide görülen beceriler, departman hücreleri ve bağlar gösterilmez; 5'ten küçük departmanlar \"Diğer\" altında birleşir.",
    }


@router.post("/graph")
async def graph_route(req: GraphRequest) -> dict:
    return skill_graph(req)


# ------------------------------------------------------------------ "Sana uygun" önerileri (Dalga 10, madde 49)
#
# Çalışanın kendi beceri vektörü (profil becerileri, kanonik anahtarlara indirgenmiş) + yetkinlik
# açıkları (rol beklentisi − son değerlendirme) ile iç ilanlar, mentorlar ve eğitimler eşleştirilir.
# Girdi takma adlıdır: mentor ve ilan opak kimlikle gelir, ad/ e-posta yoktur. Yalnızca öneri ve
# gerekçe üretir; başvuru, mentorluk talebi ya da eğitim kaydı OTOMATİK yapılmaz.

class Gap(BaseModel):
    model_config = ConfigDict(extra="forbid")
    id: str = Field(max_length=64)
    name: str = Field(min_length=1, max_length=200)
    required: int = Field(ge=1, le=5)
    current: int = Field(ge=0, le=5)


class MeIn(BaseModel):
    model_config = ConfigDict(extra="forbid")
    skills: list[str] = Field(default_factory=list, max_length=200)
    position: str | None = Field(default=None, max_length=200)
    gaps: list[Gap] = Field(default_factory=list, max_length=200)
    completed_course_ids: list[str] = Field(default_factory=list, max_length=2000)


class PostingRef(BaseModel):
    model_config = ConfigDict(extra="forbid")
    id: str = Field(max_length=64)
    title: str = Field(min_length=1, max_length=300)
    text: str = Field(default="", max_length=50_000)


class MentorRef(BaseModel):
    model_config = ConfigDict(extra="forbid")
    id: str = Field(max_length=64)
    offers: list[str] = Field(default_factory=list, max_length=50)
    free_slots: int = Field(default=0, ge=0, le=20)


class CourseCompetency(BaseModel):
    model_config = ConfigDict(extra="forbid")
    id: str = Field(max_length=64)
    target_level: int = Field(ge=1, le=5)


class CourseRef(BaseModel):
    model_config = ConfigDict(extra="forbid")
    id: str = Field(max_length=64)
    title: str = Field(min_length=1, max_length=300)
    description: str | None = Field(default=None, max_length=10_000)
    category: str | None = Field(default=None, max_length=200)
    competencies: list[CourseCompetency] = Field(default_factory=list, max_length=50)


class RecommendRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")
    me: MeIn
    postings: list[PostingRef] = Field(default_factory=list, max_length=500)
    mentors: list[MentorRef] = Field(default_factory=list, max_length=2000)
    courses: list[CourseRef] = Field(default_factory=list, max_length=2000)
    limit: int = Field(default=5, ge=1, le=20)


def skill_keys(items: list[str]) -> dict[str, str]:
    out: dict[str, str] = {}
    for raw in items:
        n = normalize_skill(raw)
        if n:
            out.setdefault(n[0], n[1])
    return out


def _gap_targets(gaps: list[Gap]) -> list[Gap]:
    return sorted([g for g in gaps if g.required > g.current], key=lambda g: -(g.required - g.current))


def recommend(req: RecommendRequest) -> dict:
    mine = skill_keys(req.me.skills)
    gaps = _gap_targets(req.me.gaps)
    gap_names = {g.id: g.name for g in gaps}

    # İç ilanlar: ilan metnindeki beceriler (sözlük) ile benimkilerin örtüşmesi.
    postings = []
    for p in req.postings:
        need = {f["key"]: f["label"] for f in find_skills(p.title + "\n" + p.text)}
        if not need:
            continue
        have = [need[k] for k in need if k in mine]
        missing = [need[k] for k in need if k not in mine]
        coverage = len(have) / len(need)
        if not have:
            continue
        score = int(round(100 * (0.8 * coverage + 0.2 * min(1.0, len(have) / 3))))
        reasons = [f"Becerilerinizden {len(have)}/{len(need)} ilanda aranıyor: {', '.join(have[:6])}"]
        if missing:
            reasons.append("Gelişmeniz gereken: " + ", ".join(missing[:5]))
        postings.append({"id": p.id, "score": score, "matched": have, "missing": missing, "reasons": reasons})
    postings.sort(key=lambda x: -x["score"])
    postings = postings[: req.limit]

    # Hedef beceriler: yetkinlik açıkları + en uygun ilanların eksik becerileri (mentor ve eğitim aramak için).
    target_terms: dict[str, str] = {}
    for g in gaps:
        for t in _content_stems(g.name):
            target_terms.setdefault(t, g.name)
    for p in postings[:3]:
        for m in p["missing"]:
            n = normalize_skill(m)
            if n:
                target_terms.setdefault(n[0], m)

    # Mentorlar: mentorun sunduğu konular ↔ hedeflerim (beceri anahtarı ya da yetkinlik adı kökleri).
    mentors = []
    for m in req.mentors:
        if m.free_slots <= 0:
            continue
        matched = []
        for o in m.offers:
            n = normalize_skill(o)
            if (n and n[0] in target_terms) or (_content_stems(o) & set(target_terms)):
                matched.append(o)
        if not matched:
            continue
        cover = min(1.0, len(matched) / max(1, min(3, len(target_terms))))
        score = int(round(100 * (0.8 * cover + 0.2 * min(1.0, m.free_slots / 2))))
        mentors.append({"id": m.id, "score": score, "matched": matched,
                        "reasons": [f"Hedeflediğiniz konularda deneyim sunuyor: {', '.join(matched[:5])}",
                                    f"{m.free_slots} boş mentorluk yeri"]})
    mentors.sort(key=lambda x: -x["score"])
    mentors = mentors[: req.limit]

    # Eğitimler: yetkinlik açığını kapatanlar önce (açık büyüklüğü), sonra hedef becerilerle metin örtüşmesi.
    done = set(req.me.completed_course_ids)
    courses = []
    for c in req.courses:
        if c.id in done:
            continue
        closes = []
        gap_score = 0.0
        for cc in c.competencies:
            g = next((x for x in gaps if x.id == cc.id), None)
            if g and cc.target_level > g.current:
                closes.append(f"{g.name} ({g.current} → {min(cc.target_level, g.required)}, beklenen {g.required})")
                gap_score += (min(cc.target_level, g.required) - g.current) / 4
        text_hits = sorted({target_terms[t] for t in (_content_stems(f"{c.title} {c.description or ''} {c.category or ''}") & set(target_terms))})
        skill_hits = [lbl for k, lbl in skill_keys([c.title]).items() if k in target_terms]
        hits = list(dict.fromkeys(text_hits + skill_hits))
        if not closes and not hits:
            continue
        score = int(round(100 * min(1.0, 0.7 * min(1.0, gap_score) + 0.3 * min(1.0, len(hits) / 2) + (0.25 if closes else 0))))
        reasons = []
        if closes:
            reasons.append("Yetkinlik açığını kapatır: " + "; ".join(closes[:3]))
        if hits:
            reasons.append("Hedef konularla örtüşüyor: " + ", ".join(hits[:5]))
        courses.append({"id": c.id, "score": score, "closes_gaps": closes, "matched": hits, "reasons": reasons})
    courses.sort(key=lambda x: -x["score"])
    courses = courses[: req.limit]

    return {
        "postings": postings, "mentors": mentors, "courses": courses,
        "gaps": [{"id": g.id, "name": g.name, "required": g.required, "current": g.current} for g in gaps],
        "skills_used": list(mine.values()),
        "method": "Beceri anahtarı örtüşmesi (sözlük + eş anlamlılar), yetkinlik açığı (rol beklentisi − son değerlendirme) ve hafif kök eşleşmesi.",
        "note": "Öneridir; başvuru, mentorluk talebi ya da eğitim kaydı otomatik yapılmaz.",
    }


@router.post("/recommend")
async def recommend_route(req: RecommendRequest) -> dict:
    return recommend(req)

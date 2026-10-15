"""
Serbest metinde kişisel / özel nitelikli veri dedektörü (KVKK veri en aza indirme).

  POST /pii/scan   - metindeki TCKN (sağlama haneleriyle), IBAN (mod-97), telefon, e-posta ve
                     KVKK m.6 özel nitelikli kategori (sağlık, ceza mahkûmiyeti, din, etnik köken,
                     siyasi düşünce, sendika, cinsel hayat, biyometrik) sözcüklerinin YERLERİNİ döner.
                     Metnin kendisi yanıtta yinelenmez; yalnızca konum (start/end) ve TCKN/IBAN için
                     maskeli ipucu. Metin saklanmaz ve günlüğe yazılmaz.
  GET  /pii/rules  - anahtar sözcük kuralları (pii_rules.json; arayüzdeki istemci tarafı kopyası
                     apps/web/src/lib/piiRules.json bununla aynıdır, Vitest eşitliği denetler).

Uyarı yalnızca bilgilendirir: kayıt engellenmez, otomatik karar verilmez.
"""

from __future__ import annotations

import json
import os
import re
from functools import lru_cache

from fastapi import APIRouter
from pydantic import BaseModel, ConfigDict, Field

router = APIRouter(prefix="/pii", tags=["pii"])

RULES_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "pii_rules.json")
MAX_FINDINGS = 200

# Harf harf (uzunluk korunarak) küçük harfe + ASCII'ye indirgeme: konumlar özgün metinle aynı kalır.
_FOLD = {"İ": "i", "I": "i", "ı": "i", "Ş": "s", "ş": "s", "Ğ": "g", "ğ": "g", "Ü": "u", "ü": "u",
         "Ö": "o", "ö": "o", "Ç": "c", "ç": "c", "Â": "a", "â": "a", "Î": "i", "î": "i", "Û": "u", "û": "u"}


def fold(text: str) -> str:
    out = []
    for ch in text:
        m = _FOLD.get(ch)
        if m is None:
            low = ch.lower()
            m = low if len(low) == 1 else ch
        out.append(m)
    return "".join(out)


@lru_cache(maxsize=1)
def load_rules() -> dict:
    with open(RULES_PATH, encoding="utf-8") as f:
        return json.load(f)


@lru_cache(maxsize=1)
def _compiled() -> list[tuple[str, list[tuple[tuple[str, ...], bool]]]]:
    """Kategori -> [(sözcük kökleri, tam_eşleşme)] (indirgenmiş biçimde)."""
    out = []
    for c in load_rules()["categories"]:
        if c.get("kind") != "keyword":
            continue
        entries = [(tuple(fold(s).split()), False) for s in c.get("stems", [])]
        entries += [(tuple(fold(w).split()), True) for w in c.get("words", [])]
        out.append((c["key"], entries))
    return out


@lru_cache(maxsize=1)
def _exclusions() -> list[tuple[str, ...]]:
    """Kişisel veri sayılmayan yaygın kalıplar (indirgenmiş sözcük kökleri)."""
    return [tuple(fold(e).split()) for e in load_rules().get("exclusions", [])]


def _labels() -> dict[str, dict]:
    return {c["key"]: c for c in load_rules()["categories"]}


# ------------------------------------------------------------------ yapısal kalıplar

_TCKN = re.compile(r"(?<!\d)[1-9]\d{10}(?!\d)")
_IBAN_TR = re.compile(r"(?<![0-9A-Za-z])TR\d{2}(?: ?[0-9A-Z]){22}(?![0-9A-Za-z])", re.IGNORECASE)
_IBAN_ANY = re.compile(r"(?<![0-9A-Za-z])[A-Z]{2}\d{2}[A-Z0-9]{11,30}(?![0-9A-Za-z])")
_EMAIL = re.compile(r"(?<![\w.+-])[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}")
# Cep: (+90 / 0) 5xx xxx xx xx; sabit hat yalnızca ön ekle (+90 ya da 0) - yanlış alarmı azaltır.
_PHONE_MOBILE = re.compile(r"(?<![\d+])(?:\+?90[\s.-]?|0[\s.-]?)?\(?5\d{2}\)?[\s.-]?\d{3}[\s.-]?\d{2}[\s.-]?\d{2}(?!\d)")
_PHONE_LAND = re.compile(r"(?<![\d+])(?:\+?90[\s.-]?|0[\s.-]?)\(?[2-4]\d{2}\)?[\s.-]?\d{3}[\s.-]?\d{2}[\s.-]?\d{2}(?!\d)")
_WORD = re.compile(r"[^\W_]+")


def tckn_valid(s: str) -> bool:
    """T.C. kimlik no: 11 hane, ilki 0 değil; 10. hane = (tekler*7 - çiftler) mod 10, 11. hane = ilk 10 toplamı mod 10."""
    if not re.fullmatch(r"[1-9]\d{10}", s):
        return False
    d = [int(c) for c in s]
    odd = d[0] + d[2] + d[4] + d[6] + d[8]
    even = d[1] + d[3] + d[5] + d[7]
    return (odd * 7 - even) % 10 == d[9] and sum(d[:10]) % 10 == d[10]


def iban_valid(s: str) -> bool:
    """ISO 13616 mod-97 (boşluklar yok sayılır). TR IBAN'ı 26 karakter olmalıdır."""
    s = re.sub(r"\s+", "", s).upper()
    if not re.fullmatch(r"[A-Z]{2}\d{2}[A-Z0-9]{11,30}", s):
        return False
    if s.startswith("TR") and len(s) != 26:
        return False
    rem = 0
    for ch in s[4:] + s[:4]:
        for digit in (str(ord(ch) - 55) if ch.isalpha() else ch):
            rem = (rem * 10 + int(digit)) % 97
    return rem == 1


def _mask(category: str, raw: str) -> str | None:
    compact = re.sub(r"\s+", "", raw)
    if category == "tckn":
        return compact[:2] + "*" * 9
    if category == "iban":
        return compact[:4].upper() + "*" * (len(compact) - 6) + compact[-2:]
    return None


def scan(text: str) -> list[dict]:
    """Bulguları (kategori, konum) döner; çakışan bulgularda önce gelen kategori kazanır."""
    labels = _labels()
    found: list[dict] = []
    taken: list[tuple[int, int]] = []

    def add(category: str, start: int, end: int, raw: str | None = None) -> None:
        if any(start < e and s < end for s, e in taken):
            return
        taken.append((start, end))
        c = labels[category]
        item = {"category": category, "label": c["label"], "special": bool(c.get("special")), "start": start, "end": end}
        hint = _mask(category, raw) if raw is not None else None
        if hint:
            item["hint"] = hint
        found.append(item)

    for m in _TCKN.finditer(text):
        if tckn_valid(m.group()):
            add("tckn", m.start(), m.end(), m.group())
    for m in _IBAN_TR.finditer(text):
        if iban_valid(m.group()):
            add("iban", m.start(), m.end(), m.group())
    for m in _IBAN_ANY.finditer(text):
        if iban_valid(m.group()):
            add("iban", m.start(), m.end(), m.group())
    for m in _EMAIL.finditer(text):
        add("email", m.start(), m.end())
    for rx in (_PHONE_MOBILE, _PHONE_LAND):
        for m in rx.finditer(text):
            add("phone", m.start(), m.end())

    folded = fold(text)
    words = [(m.group(), m.start(), m.end()) for m in _WORD.finditer(folded)]
    rules = _compiled()
    # İstisna kalıpları (ör. "dini bayram"): kapsadıkları sözcükler anahtar sözcük denetimine girmez.
    excluded: set[int] = set()
    for stems in _exclusions():
        n = len(stems)
        for i in range(len(words) - n + 1):
            if all(words[i + k][0].startswith(stems[k]) for k in range(n)):
                excluded.update(range(i, i + n))
    for i in range(len(words)):
        if i in excluded:
            continue
        for key, entries in rules:
            hit = None
            for stems, exact in entries:
                n = len(stems)
                if i + n > len(words):
                    continue
                ok = all((words[i + k][0] == stems[k]) if exact else words[i + k][0].startswith(stems[k]) for k in range(n))
                if ok and (hit is None or n > hit):
                    hit = n
            if hit:
                add(key, words[i][1], words[i + hit - 1][2])
                break
    found.sort(key=lambda f: f["start"])
    return found[:MAX_FINDINGS]


def summarize(findings: list[dict]) -> dict:
    cats: list[str] = []
    for f in findings:
        if f["category"] not in cats:
            cats.append(f["category"])
    labels = _labels()
    return {"findings": findings, "categories": cats, "labels": [labels[c]["label"] for c in cats],
            "special_category": any(labels[c].get("special") for c in cats), "rules_version": load_rules()["version"]}


class ScanRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")
    text: str = Field(max_length=20000)


@router.post("/scan")
async def pii_scan(req: ScanRequest) -> dict:
    # Metin yalnızca bellekte taranır; yanıt metni yinelemez, günlüğe yazılmaz.
    return summarize(scan(req.text))


@router.get("/rules")
async def pii_rules() -> dict:
    return load_rules()

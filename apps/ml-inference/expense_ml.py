"""
Masraf denetimi yardimcilari (insan incelemesi icin isaret; otomatik karar YOK):

  POST /expense/anomaly         - gonderilen kalemleri ayni kiracinin gecmis kalemleriyle
                                  karsilastirir: olagan disi tutar (saglam z-skoru: medyan/MAD;
                                  kategori, calisan+kategori, tedarikci bazinda), yeterli gecmis
                                  varsa IsolationForest, mukerrer fis (ayni ETTN, ayni tedarikci +
                                  fatura no, ayni tutar + tarih (+-1 gun) + tedarikci, ayni aciklama).
                                  Yanit yalnizca isaret ve gerekcedir; beyan reddedilmez.
  POST /expense/einvoice/parse  - e-Fatura / e-Arsiv karekodundaki (QR) JSON metnini ayristirir ve
                                  dogrular (VKN/TCKN denetim hanesi, ETTN, tutar tutarliligi, tarih).

KVKK: istekte ad/soyad yoktur; calisan ve aciklama expense-service'te ozetlenmis (hash) gelir.
Hicbir girdi saklanmaz; model her istekte bellekte kurulur.
"""

from __future__ import annotations

import json
import math
import re
import uuid
from datetime import date, datetime
from decimal import Decimal, InvalidOperation
from typing import Literal

import numpy as np
from fastapi import APIRouter, HTTPException
from pydantic import BaseModel, ConfigDict, Field
from tr_text import tr_num

router = APIRouter(prefix="/expense", tags=["expense-ml"])

# ------------------------------------------------------------------ vergi kimlik denetimi


def vkn_valid(value: str | None) -> bool:
    """10 haneli Vergi Kimlik Numarasi denetim hanesi (GIB algoritmasi)."""
    if not value or not re.fullmatch(r"\d{10}", value):
        return False
    d = [int(c) for c in value]
    total = 0
    for i in range(9):
        tmp = (d[i] + 9 - i) % 10
        v = (tmp * 2 ** (9 - i)) % 9
        if tmp != 0 and v == 0:
            v = 9
        total += v
    return (10 - total % 10) % 10 == d[9]


def tckn_valid(value: str | None) -> bool:
    """11 haneli T.C. Kimlik No denetim haneleri (10. ve 11. hane)."""
    if not value or not re.fullmatch(r"[1-9]\d{10}", value):
        return False
    d = [int(c) for c in value]
    d10 = ((d[0] + d[2] + d[4] + d[6] + d[8]) * 7 - (d[1] + d[3] + d[5] + d[7])) % 10
    return d10 == d[9] and sum(d[:10]) % 10 == d[10]


def tax_id_kind(value: str | None) -> str | None:
    """'vkn', 'tckn' ya da None (gecersiz)."""
    if vkn_valid(value):
        return "vkn"
    if tckn_valid(value):
        return "tckn"
    return None


# ------------------------------------------------------------------ e-Fatura karekodu

_KDV_KEY = re.compile(r"^(kdvmatrah|hesaplanankdv)\s*[\(\[]?\s*(\d{1,2}(?:[.,]\d+)?)\s*[\)\]]?$")


def _norm_key(k: str) -> str:
    k = k.strip().lower()
    for a, b in (("ı", "i"), ("ş", "s"), ("ğ", "g"), ("ü", "u"), ("ö", "o"), ("ç", "c")):
        k = k.replace(a, b)
    return re.sub(r"[\s_\-]", "", k)


def _money(v) -> Decimal | None:
    """'1.234,56', '1234.56', '1234,5', 1234.56 -> Decimal. Gecersizse None."""
    if v is None or isinstance(v, bool):
        return None
    if isinstance(v, (int, float)):
        if not math.isfinite(float(v)):
            return None
        return Decimal(str(v)).quantize(Decimal("0.01"))
    s = str(v).strip().replace(" ", "").replace("TL", "").replace("₺", "")
    if not s:
        return None
    if "," in s and "." in s:
        # Son gelen ayirici ondaliktir.
        s = s.replace(".", "").replace(",", ".") if s.rfind(",") > s.rfind(".") else s.replace(",", "")
    elif "," in s:
        s = s.replace(",", ".")
    try:
        d = Decimal(s)
    except InvalidOperation:
        return None
    if not d.is_finite() or d < 0:
        return None
    return d.quantize(Decimal("0.01"))


def _date(v) -> date | None:
    s = str(v or "").strip()
    for fmt in ("%Y-%m-%d", "%d-%m-%Y", "%d.%m.%Y", "%d/%m/%Y", "%Y%m%d"):
        try:
            return datetime.strptime(s[:10] if fmt != "%Y%m%d" else s[:8], fmt).date()
        except ValueError:
            continue
    return None


class EInvoiceError(ValueError):
    pass


def parse_einvoice_qr(text: str, today: date | None = None) -> dict:
    """e-Fatura / e-Arsiv karekod JSON'unu normalize eder ve dogrular.

    Hatalar (JSON degil, tutar yok) EInvoiceError firlatir; tutarsizliklar 'warnings' listesine
    yazilir (kullanici duzeltebilir). Donen alanlar masraf kalemini doldurmak icindir.
    """
    if not text or len(text) > 8000:
        raise EInvoiceError("Karekod metni boş ya da çok uzun.")
    raw = text.strip()
    # Bazi okuyucular JSON'u tirnak icinde ya da kacisli dondurur.
    try:
        data = json.loads(raw)
        if isinstance(data, str):
            data = json.loads(data)
    except (json.JSONDecodeError, TypeError):
        raise EInvoiceError("Karekod e-Fatura/e-Arşiv biçiminde değil (JSON okunamadı).") from None
    if not isinstance(data, dict):
        raise EInvoiceError("Karekod e-Fatura/e-Arşiv biçiminde değil.")

    fields: dict[str, object] = {}
    vat_base: dict[str, Decimal] = {}
    vat_amt: dict[str, Decimal] = {}
    for k, v in data.items():
        nk = _norm_key(str(k))
        m = _KDV_KEY.match(nk)
        if m:
            rate = m.group(2).replace(",", ".")
            rate = str(int(float(rate))) if float(rate).is_integer() else rate
            amount = _money(v)
            if amount is not None:
                (vat_base if m.group(1) == "kdvmatrah" else vat_amt)[rate] = amount
            continue
        fields[nk] = v

    warnings: list[str] = []

    def s(*keys: str) -> str | None:
        for key in keys:
            v = fields.get(key)
            if v is not None and str(v).strip():
                return str(v).strip()
        return None

    supplier = s("vkntckn", "vkn", "tckn", "saticivkn", "saticivkntckn")
    buyer = s("avkntckn", "alicivkntckn", "alicivkn")
    for label, val in (("Satıcı", supplier), ("Alıcı", buyer)):
        if val is None:
            continue
        if not re.fullmatch(r"\d{10,11}", val):
            warnings.append(f"{label} vergi/kimlik numarası 10 ya da 11 haneli olmalı.")
        elif tax_id_kind(val) is None:
            warnings.append(f"{label} vergi/kimlik numarasının denetim hanesi tutmuyor.")
    if supplier is None:
        warnings.append("Satıcı VKN/TCKN karekodda yok.")

    ettn = s("ettn", "uuid")
    if ettn:
        try:
            ettn = str(uuid.UUID(ettn))
        except ValueError:
            warnings.append("ETTN geçerli bir UUID değil.")
            ettn = None
    else:
        warnings.append("ETTN karekodda yok; mükerrer fiş denetimi tutar/tarih/tedarikçi ile yapılır.")

    inv_date = _date(fields.get("tarih"))
    today = today or date.today()
    if fields.get("tarih") is not None and inv_date is None:
        warnings.append("Fatura tarihi okunamadı.")
    elif inv_date is not None and inv_date > today:
        warnings.append("Fatura tarihi gelecekte.")

    invoice_no = s("no", "faturano", "belgeno")
    if invoice_no and not re.fullmatch(r"[A-Za-z0-9]{3}\d{13}", invoice_no):
        # GIB numarasi: 3 karakter seri + 4 hane yil + 9 hane sira (16 karakter).
        warnings.append("Fatura numarası GİB biçiminde değil (3 karakter + 13 hane).")

    currency = (s("parabirimi", "currency") or "TRY").upper()
    if currency == "TL":
        currency = "TRY"
    if not re.fullmatch(r"[A-Z]{3}", currency):
        warnings.append("Para birimi geçersiz; TRY varsayıldı.")
        currency = "TRY"

    net = _money(fields.get("malhizmettoplam"))
    tax_incl = _money(fields.get("vergidahil") or fields.get("vergidahiltutar"))
    payable = _money(fields.get("odenecek") or fields.get("odenecektutar"))
    vat_total = sum(vat_amt.values(), Decimal("0.00")) if vat_amt else None
    if vat_total is None and fields.get("hesaplanankdv") is not None:
        vat_total = _money(fields.get("hesaplanankdv"))
    total = tax_incl or payable or (net + vat_total if net is not None and vat_total is not None else None)
    if total is None:
        raise EInvoiceError("Karekodda ödenecek tutar bulunamadı.")

    tol = Decimal("0.05")
    if net is not None and vat_total is not None and tax_incl is not None and abs(net + vat_total - tax_incl) > tol:
        warnings.append("Mal/hizmet toplamı + KDV, vergiler dahil tutarla uyuşmuyor.")
    for rate, base in vat_base.items():
        amt = vat_amt.get(rate)
        try:
            expected = (base * Decimal(rate) / 100).quantize(Decimal("0.01"))
        except InvalidOperation:
            continue
        if amt is not None and abs(expected - amt) > tol:
            warnings.append(f"%{tr_num(rate, 4)} KDV tutarı matrahla uyuşmuyor (beklenen {tr_num(expected)}).")
    if payable is not None and tax_incl is not None and payable > tax_incl + tol:
        warnings.append("Ödenecek tutar vergiler dahil tutardan büyük.")

    def f(d: Decimal | None) -> float | None:
        return None if d is None else float(d)

    return {
        "supplierTaxId": supplier,
        "supplierTaxIdKind": tax_id_kind(supplier),
        "buyerTaxId": buyer,
        "invoiceNo": invoice_no,
        "ettn": ettn,
        "date": inv_date.isoformat() if inv_date else None,
        "scenario": s("senaryo"),
        "type": s("tip", "faturatipi"),
        "currency": currency,
        "net": f(net),
        "vat": f(vat_total),
        "vatBreakdown": [{"rate": float(r), "base": f(vat_base.get(r)), "vat": f(vat_amt.get(r))}
                         for r in sorted(set(vat_base) | set(vat_amt), key=float)],
        "total": f(tax_incl or total),
        "payable": f(payable or total),
        "warnings": warnings,
    }


class EInvoiceRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")
    text: str = Field(min_length=2, max_length=8000)


@router.post("/einvoice/parse")
async def einvoice_parse(req: EInvoiceRequest) -> dict:
    try:
        return parse_einvoice_qr(req.text)
    except EInvoiceError as e:
        raise HTTPException(status_code=422, detail=str(e)) from None


# ------------------------------------------------------------------ anomali / mukerrer

ROBUST_Z = 3.5          # Iglewicz-Hoaglin esigi (degistirilmis z-skoru)
MIN_GROUP = 8           # bir grupta z-skoru icin en az gecmis kalem
MIN_IFOREST = 50        # IsolationForest icin en az gecmis kalem
IFOREST_CONTAMINATION = 0.02


class ItemIn(BaseModel):
    model_config = ConfigDict(extra="forbid")
    id: str = Field(max_length=64)
    amount: float = Field(gt=0, le=10_000_000)
    category: str = Field(max_length=32)
    date: date
    employee: str | None = Field(default=None, max_length=64, description="Çalışanın takma adı (hash)")
    merchant: str | None = Field(default=None, max_length=32, description="Tedarikçi VKN/TCKN")
    invoice_no: str | None = Field(default=None, max_length=32)
    ettn: str | None = Field(default=None, max_length=40)
    text_hash: str | None = Field(default=None, max_length=64, description="Normalize açıklamanın özeti")


class HistoryIn(ItemIn):
    id: str | None = Field(default=None, max_length=64)  # type: ignore[assignment]


class AnomalyRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")
    items: list[ItemIn] = Field(min_length=1, max_length=50)
    history: list[HistoryIn] = Field(default_factory=list, max_length=20000)


def _flag(code: str, severity: Literal["low", "medium", "high"], reason: str, **details) -> dict:
    return {"code": code, "severity": severity, "reason": reason, "details": details}


def _robust_z(x: float, values: np.ndarray) -> float | None:
    """Degistirilmis z-skoru (log tutar uzerinde): 0,6745 * (x - medyan) / MAD.
    MAD 0 ise ortalama mutlak sapmaya (x1,2533) duser; o da 0 ise hesaplanmaz."""
    if len(values) < MIN_GROUP:
        return None
    lv = np.log1p(values)
    lx = math.log1p(x)
    med = float(np.median(lv))
    mad = float(np.median(np.abs(lv - med)))
    if mad > 0:
        return 0.6745 * (lx - med) / mad
    mean_ad = float(np.mean(np.abs(lv - np.mean(lv))))
    if mean_ad > 0:
        return (lx - med) / (1.253314 * mean_ad)
    return None


def _money_eq(a: float, b: float) -> bool:
    return round(a * 100) == round(b * 100)


def _norm_ettn(v: str | None) -> str | None:
    return v.strip().lower() if v and v.strip() else None


def _features(rows, categories: list[str]) -> np.ndarray:
    cat_index = {c: i for i, c in enumerate(categories)}
    out = []
    for r in rows:
        onehot = [0.0] * len(categories)
        if r.category in cat_index:
            onehot[cat_index[r.category]] = 1.0
        wd = r.date.weekday()
        out.append([math.log1p(r.amount), 1.0 if wd >= 5 else 0.0,
                    1.0 if _money_eq(r.amount % 100, 0) else 0.0, *onehot])
    return np.asarray(out, dtype=float)


def detect_anomalies(items: list[ItemIn], history: list[HistoryIn]) -> dict:
    """Saf fonksiyon: her kalem icin isaret listesi. Karar vermez."""
    by_cat: dict[str, list[float]] = {}
    by_emp_cat: dict[tuple, list[float]] = {}
    by_merchant: dict[str, list[float]] = {}
    for h in history:
        by_cat.setdefault(h.category, []).append(h.amount)
        if h.employee:
            by_emp_cat.setdefault((h.employee, h.category), []).append(h.amount)
        if h.merchant:
            by_merchant.setdefault(h.merchant, []).append(h.amount)

    iforest = None
    categories = sorted({h.category for h in history} | {i.category for i in items})
    if len(history) >= MIN_IFOREST:
        from sklearn.ensemble import IsolationForest  # noqa: PLC0415

        iforest = IsolationForest(n_estimators=100, contamination=IFOREST_CONTAMINATION, random_state=0)
        iforest.fit(_features(history, categories))

    results = []
    for idx, it in enumerate(items):
        flags: list[dict] = []
        others = [o for j, o in enumerate(items) if j != idx]
        pool = [*history, *others]
        ettn = _norm_ettn(it.ettn)

        # --- mukerrer
        if ettn and any(_norm_ettn(o.ettn) == ettn for o in pool):
            flags.append(_flag("DUPLICATE_ETTN", "high",
                               "Aynı ETTN'li fatura daha önce (ya da bu beyanda) masraf olarak girilmiş.", ettn=ettn))
        if it.merchant and it.invoice_no and any(
                o.merchant == it.merchant and (o.invoice_no or "").upper() == it.invoice_no.upper() for o in pool):
            flags.append(_flag("DUPLICATE_INVOICE_NO", "high",
                               "Aynı tedarikçinin aynı numaralı faturası daha önce girilmiş.", invoice_no=it.invoice_no))
        if not any(f["code"].startswith("DUPLICATE_") for f in flags):
            near = [o for o in pool if _money_eq(o.amount, it.amount) and abs((o.date - it.date).days) <= 1 and (
                (it.merchant and o.merchant == it.merchant)
                or (not it.merchant and it.employee and o.employee == it.employee and o.category == it.category
                    and o.date == it.date))]
            if near:
                flags.append(_flag("POSSIBLE_DUPLICATE", "medium",
                                   "Aynı tutar ve tarihte (±1 gün) aynı tedarikçi/çalışan için başka bir kalem var.",
                                   matches=len(near)))
            elif it.text_hash and any(o.text_hash == it.text_hash and _money_eq(o.amount, it.amount)
                                      and o.employee == it.employee for o in pool):
                flags.append(_flag("DUPLICATE_TEXT", "low",
                                   "Aynı açıklama ve tutarla daha önce bir kalem girilmiş."))

        # --- olagan disi tutar (yalnizca yukari yonde)
        for code, group, label in (
            ("AMOUNT_OUTLIER_CATEGORY", by_cat.get(it.category), "kategorideki"),
            ("AMOUNT_OUTLIER_EMPLOYEE", by_emp_cat.get((it.employee, it.category)) if it.employee else None,
             "çalışanın bu kategorideki"),
            ("AMOUNT_OUTLIER_MERCHANT", by_merchant.get(it.merchant) if it.merchant else None, "tedarikçideki"),
        ):
            if not group:
                continue
            vals = np.asarray(group, dtype=float)
            z = _robust_z(it.amount, vals)
            if z is not None and z > ROBUST_Z:
                median = round(float(np.median(vals)), 2)
                flags.append(_flag(code, "medium" if z > 2 * ROBUST_Z else "low",
                                   f"Tutar, {label} olağan tutarların çok üzerinde (medyan {tr_num(median)}).",
                                   z=round(z, 2), median=median, n=int(len(vals))))

        if iforest is not None:
            score = float(iforest.decision_function(_features([it], categories))[0])
            if score < 0 and not any(f["code"].startswith("AMOUNT_OUTLIER") for f in flags):
                flags.append(_flag("UNUSUAL_PATTERN", "low",
                                   "Kalem, şirketin geçmiş masraf örüntüsüne göre olağan dışı (tutar/gün/kategori).",
                                   score=round(score, 4)))
        results.append({"id": it.id, "flags": flags})

    return {
        "items": results,
        "history_rows": len(history),
        "methods": {"robust_z": ROBUST_Z, "min_group": MIN_GROUP,
                    "isolation_forest": iforest is not None, "min_isolation_forest": MIN_IFOREST},
        "note": "İşaretler insan incelemesi içindir; beyan otomatik reddedilmez.",
    }


@router.post("/anomaly")
async def anomaly(req: AnomalyRequest) -> dict:
    return detect_anomalies(req.items, req.history)

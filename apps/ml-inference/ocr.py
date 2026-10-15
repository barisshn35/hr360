"""Fiş okuma (Y27): yerel Tesseract OCR. Görüntü yalnızca bellekte işlenir, saklanmaz ve
sunucu dışına gönderilmez. Dönen metni expense-service ayrıştırır (tutar, tarih, VKN)."""

import io

from fastapi import APIRouter, File, HTTPException, UploadFile

router = APIRouter(prefix="/ocr", tags=["ocr"])

MAX_BYTES = 5_000_000


def sniff_image(data: bytes) -> str | None:
    """Görüntü türünü ilk baytlardan (magic bytes) belirler: JPEG, PNG, WEBP; diğerleri None.
    Pillow onlarca biçimi (PSD, TIFF, EPS...) açabildiğinden yalnızca bu üçü kabul edilir."""
    if data[:8] == b"\x89PNG\r\n\x1a\n":
        return "image/png"
    if data[:3] == b"\xff\xd8\xff":
        return "image/jpeg"
    if len(data) >= 12 and data[:4] == b"RIFF" and data[8:12] == b"WEBP":
        return "image/webp"
    return None


_GENERIC = {"", "application/octet-stream", "binary/octet-stream"}


def _engine():
    try:
        import pytesseract  # noqa: PLC0415
        from PIL import Image, ImageOps  # noqa: PLC0415
        pytesseract.get_tesseract_version()
        return pytesseract, Image, ImageOps
    except Exception:  # noqa: BLE001
        return None


@router.post("/receipt")
async def receipt(file: UploadFile = File(...)):
    data = await file.read(MAX_BYTES + 1)
    if not data or len(data) > MAX_BYTES:
        raise HTTPException(400, "Görüntü boş ya da 5 MB'tan büyük")
    detected = sniff_image(data)
    if detected is None:
        raise HTTPException(400, "JPEG, PNG ya da WEBP yükleyin")
    declared = (file.content_type or "").split(";")[0].strip().lower().replace("image/jpg", "image/jpeg")
    if declared not in _GENERIC and declared != detected:
        raise HTTPException(400, "Dosyanın içeriği, bildirilen dosya türü ya da uzantısıyla uyuşmuyor")
    eng = _engine()
    if eng is None:
        raise HTTPException(503, "OCR motoru (tesseract) kurulu değil")
    pytesseract, Image, ImageOps = eng
    try:
        img = Image.open(io.BytesIO(data))
        img = ImageOps.exif_transpose(img).convert("L")
        if max(img.size) < 1200:  # küçük fotoğraflarda doğruluk için büyüt
            scale = 1200 / max(img.size)
            img = img.resize((int(img.width * scale), int(img.height * scale)))
    except Exception:  # noqa: BLE001
        raise HTTPException(400, "Görüntü okunamadı") from None
    langs = "tur+eng"
    try:
        d = pytesseract.image_to_data(img, lang=langs, output_type=pytesseract.Output.DICT, config="--psm 6")
    except pytesseract.TesseractError:
        d = pytesseract.image_to_data(img, lang="eng", output_type=pytesseract.Output.DICT, config="--psm 6")
    lines, confs = {}, []
    for i, word in enumerate(d["text"]):
        if not word.strip():
            continue
        key = (d["block_num"][i], d["par_num"][i], d["line_num"][i])
        lines.setdefault(key, []).append(word)
        try:
            c = float(d["conf"][i])
            if c >= 0:
                confs.append(c)
        except (TypeError, ValueError):
            pass
    text = "\n".join(" ".join(ws) for _, ws in sorted(lines.items()))
    return {"text": text, "confidence": round(sum(confs) / len(confs) / 100, 3) if confs else None}

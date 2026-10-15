"""
Türkçe metin yardımcıları (konu çıkarma, anlamsal arama, beceri çıkarımı ortak kullanır).

  fold(s)      : Türkçe kurallı küçük harf + ASCII'ye indirgeme ("İŞ" -> "is", "Işık" -> "isik")
  tokens(s)    : sözcüklere bölme (harf/rakam, + # . içeren teknik terimler korunur)
  stem(w)      : hafif ek budama — yaygın çekim eklerini (çoğul, hâl, iyelik, bazı fiil ekleri)
                 sondan, kök en az 3-4 harf kalacak biçimde atar. Tam bir morfolojik çözümleyici
                 DEĞİLDİR (Zemberek vb. kullanılmaz; ağır bağımlılık ve JVM gerekmez); amaç
                 "ekiplerimizde" / "ekip" gibi biçimleri aynı terime toplamaktır.
  STOPWORDS    : Türkçe + İngilizce sık sözcükler (bu proje için derlenmiştir).

Dış servis çağrısı yoktur; metin saklanmaz.
"""

from __future__ import annotations

import re

_FOLD = str.maketrans({"ı": "i", "ş": "s", "ğ": "g", "ü": "u", "ö": "o", "ç": "c", "â": "a", "î": "i", "û": "u"})


def fold(s: str | None) -> str:
    s = (s or "").replace("İ", "i").replace("I", "ı").lower()
    return s.translate(_FOLD)


STOPWORDS = frozenset(
    """
    acaba ama ancak artik aslinda az bana bazen bazi bazilari belki ben beni benim beri bile bir biraz bircok biri birkac
    birsey biz bize bizi bizim bu buna bunda bundan bunlar bunlari bunlarin bunu bunun burada cok cunku da daha dahi de
    defa degil diger diye dolayi dolayisiyla en gibi gore hala hangi hatta hem hep hepsi her herhangi herkes hic hicbir
    icin ile ilgili ise iste kadar karsin kendi kendine ki kim kimse mi mu nasil ne neden nerede nereye nicin niye
    o olan olarak oldu oldugu olmak olmasi olsa olsun olur on ona onda ondan onlar onlari onlarin onu onun orada oysa
    once ozellikle sadece sanki sen senin siz sizin son sonra su suna sunda sundan sunlar sunu sunun tabi tamam tum uzere
    var ve veya ya yani yine yok zaten zira olabilir olmali gerek gerekiyor bence bizce herseyi sey seyler
    baska boyle boylece soyle oyle ayni simdi bugun yarin dun
    the and or for with of to in on a an is are be as at by from it this that we our you your they their not no
    """.split()
)

# Ekler uzundan kısaya denenir; tek seferde en fazla 3 ek atılır. Kök en az MIN_STEM harf kalır.
_SUFFIXES = sorted(
    {
        # çoğul + iyelik + hâl birleşimleri
        "larimizdan", "lerimizden", "larimizda", "lerimizde", "larimiza", "lerimize", "larimizi", "lerimizi",
        "larimiz", "lerimiz", "lariniz", "leriniz", "larindan", "lerinden", "larinda", "lerinde", "larina", "lerine",
        "larini", "lerini", "larin", "lerin", "lari", "leri", "lar", "ler",
        "imizden", "imizdan", "umuzdan", "umuzda", "imizde", "imizda", "imiz", "umuz", "iniz", "unuz",
        "indan", "inden", "undan", "unden", "inda", "inde", "unda", "unde",
        "nden", "ndan", "nde", "nda", "dan", "den", "tan", "ten", "da", "de", "ta", "te",
        "yla", "yle", "la", "le", "ya", "ye", "na", "ne", "nin", "nun", "in", "un",
        "si", "su", "yi", "yu",
        # fiil / sıfat-fiil ekleri (yaygın olanlar)
        "iyoruz", "uyoruz", "iyorum", "uyorum", "iyor", "uyor", "acak", "ecek", "mak", "mek", "mamiz", "memiz",
        "masi", "mesi", "ma", "me", "dik", "duk", "tik", "tuk", "digi", "dugu", "tigi", "tugu",
        "lik", "luk", "li", "lu", "siz", "suz",
    },
    key=len,
    reverse=True,
)
MIN_STEM = 4

_TOKEN = re.compile(r"[a-z0-9][a-z0-9+#.]*[a-z0-9+#]|[a-z0-9]")


def tokens(s: str | None, *, drop_stop: bool = True, min_len: int = 2) -> list[str]:
    out = []
    for t in _TOKEN.findall(fold(s)):
        if len(t) < min_len or (drop_stop and t in STOPWORDS):
            continue
        out.append(t)
    return out


def stem(word: str) -> str:
    """Hafif ek budama (ASCII'ye indirgenmiş sözcük üzerinde). Rakam/teknik terimlere dokunmaz."""
    w = word
    if not w.isalpha() or len(w) <= MIN_STEM:
        return w
    for _ in range(3):
        for suf in _SUFFIXES:
            if w.endswith(suf) and len(w) - len(suf) >= MIN_STEM:
                w = w[: -len(suf)]
                break
        else:
            break
    return w


def stems(s: str | None) -> list[str]:
    return [stem(t) for t in tokens(s)]


def sentences(text: str | None) -> list[str]:
    parts = re.split(r"(?<=[.!?…])\s+|\n+", text or "")
    return [p.strip(" -•\t") for p in parts if len(p.strip()) > 3]


def tr_num(x, digits: int = 2) -> str:
    """Sayıyı Türkçe biçimde yazar: binlik ayırıcı nokta, ondalık virgül, gereksiz sıfırlar atılır
    (12345.5 -> "12.345,5"; 0.047 -> "0,05" (digits=2); 7.5 -> "7,5"). Türkçe arayüzde gösterilen
    sunucu iletilerinde kullanılır ("{:g}" 1e+06 gibi bilimsel gösterim ve ondalık nokta üretiyordu)."""
    try:
        v = round(float(x), digits)
    except (TypeError, ValueError):
        return str(x)
    if v == 0:
        v = 0.0  # "-0" yazılmasın
    s = f"{v:,.{digits}f}"
    if "." in s:
        s = s.rstrip("0").rstrip(".")
    return s.replace(",", "\x00").replace(".", ",").replace("\x00", ".")

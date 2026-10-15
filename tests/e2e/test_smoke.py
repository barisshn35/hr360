"""Her rolle tüm ekranları açar: sayfa hatası, konsol hatası ve başarısız API çağrısı olmamalı."""

import pytest

from conftest import BASE_URL, users

# Rol başına açılan ekranlar (test_english.py de aynı listeyi İngilizce arayüzde gezer).
# Yeni bir ekran eklerken adresi (sorgu parametresi dahil) bu listeye eklemek yeterlidir.
ROUTES = [
    "/panel", "/panel/bordro", "/panel/bordrolarim", "/panel/giris-cikis", "/panel/giris-cikis/yonetim", "/panel/belge-talebi", "/panel/seyahat", "/panel/yan-haklar", "/panel/zam-donemi", "/panel/kvkk?sekme=ihlal", "/panel/kvkk?sekme=alan", "/panel/duyurular", "/panel/belgeler-kutuphanesi", "/panel/etik", "/panel/isg", "/panel/disiplin", "/panel/mulakatlarim", "/panel/yetkinlikler", "/panel/performans/dokuz-kutu", "/panel/vardiya-takasi", "/panel/zimmet/tara", "/panel/ayarlar/ozel-alanlar", "/panel/model-karti", "/panel/imzalarim", "/panel/profil?sekme=bildirimler",
    "/panel/onay-akislari", "/panel/profil?sekme=guvenlik", "/panel/bildirimler", "/panel/calisanlar", "/panel/dokumanlar", "/panel/egitim", "/panel/ik-vakalari",
    "/panel/ise-alim", "/panel/ise-alim/adaylar", "/panel/izin", "/panel/masraf", "/panel/onaylar", "/panel/onboarding",
    "/panel/organizasyon", "/panel/organizasyon/ekipler", "/panel/performans/benim", "/panel/performans/hedefler",
    "/panel/performans/geri-bildirim", "/panel/performans/degerlendirme", "/panel/puantaj", "/panel/ucret",
    "/panel/vardiya-motoru", "/panel/zimmet", "/panel/profil", "/panel/profil?sekme=takvim", "/panel/takdir",
    "/panel/kutlamalar", "/panel/anketler", "/panel/yetenek-dizini", "/panel/ofis", "/panel/bordro-simulasyonu",
    "/panel/mentorluk", "/panel/ic-ilanlar", "/panel/birebir", "/panel/ekip-sagligi", "/panel/offboarding",
    "/panel/ardil-planlama", "/panel/org-senaryolari", "/panel/analitik", "/panel/rapor-asistani", "/panel/zaman-makinesi",
    "/panel/olay-radari", "/panel/ai-araclari", "/panel/denetim", "/panel/kvkk", "/panel/belge-sablonlari",
    "/panel/kural-motoru", "/panel/entegrasyonlar", "/panel/entegrasyonlar?sekme=takvim", "/panel/entegrasyonlar?sekme=teslimat", "/panel/entegrasyonlar?sekme=hesap", "/panel/guvenlik",
    "/panel/ice-disa-aktarim", "/panel/organizasyon/sunum", "/panel/api-belgeleri",
    "/panel/veri-koruma", "/panel/veri-koruma?sekme=uyarilar", "/panel/veri-koruma?sekme=iz-kodu", "/panel/veri-koruma?sekme=ayarlar", "/panel/erisim-gozden-gecirme",
    "/panel/aday-oner", "/panel/ise-alim/oneriler", "/panel/ise-alim/analiz", "/panel/performans/kalibrasyon", "/panel/performans/okr",
    "/panel/performans/360", "/panel/performans/360?sekme=yonetim", "/panel/kariyer-yollari", "/panel/egitim-takibi",
]


@pytest.mark.parametrize("who", sorted(users()))
def test_tum_ekranlar_hatasiz(session, who):
    page, watched = session(who)
    problems = {}
    for route in ROUTES:
        page.goto(BASE_URL + route, wait_until="domcontentloaded")
        # networkidle karşılığı; SSE akışı (olay radarı) yok sayılır (bkz. conftest.Watched.settle).
        watched.settle()
        errs = [e for e in watched.take() if "events/stream" not in e]
        if errs:
            problems[route] = errs
    assert not problems, "\n".join(f"{r}: {e}" for r, e in problems.items())

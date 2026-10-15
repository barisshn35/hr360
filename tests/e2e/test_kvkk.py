"""KVKK uçtan uca (G30): İK uyum ekranı, ihlal kaydı, metin sürümleri, alan yetkileri;
çalışan tarafında aydınlatma metni, oturumlar ve passkey bölümü. Tarayıcıda, gerçek servislerle."""

import os
import random
import subprocess

from conftest import BASE_URL

ROOT = os.path.join(os.path.dirname(__file__), "..", "..")


def psql(sql):
    subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", "hr360_operational", "-Atc", sql],
                   cwd=ROOT, capture_output=True, text=True)


def no_errors(watched):
    errs = [e for e in watched.take() if "HTTP 5" in e or "PAGEERROR" in e]
    assert not errs, errs


def test_kvkk_ik_ekranlari(session):
    tag = f"E2E ihlal {random.randint(100000, 999999)}"
    page, watched = session("admin")

    page.goto(BASE_URL + "/panel/kvkk?sekme=uyum", wait_until="networkidle")
    page.get_by_text("Veri ihlalleri (72 saat)").first.wait_for(state="visible")
    page.get_by_text("Gizlilik etki değerlendirmesi").first.wait_for(state="visible")

    page.goto(BASE_URL + "/panel/kvkk?sekme=ihlal", wait_until="networkidle")
    page.get_by_role("button", name="İhlal bildir").click()
    page.get_by_label("Başlık").fill(tag)
    page.get_by_label("Ne oldu?").fill("Uçtan uca test kaydı: yanlış alıcıya e-posta.")
    page.get_by_label("Alınan ve alınacak önlemler").fill("Alıcıdan silmesi istendi.")
    page.get_by_role("button", name="Kaydet").click()
    page.get_by_text(tag).first.wait_for(state="visible")
    page.get_by_text("saat kaldı").first.wait_for(state="visible")
    # Kurul formu taslağı açılır ve kaydın başlığını içerir.
    page.get_by_role("button", name="Kurul formu").first.click()
    page.get_by_text("KİŞİSEL VERİ İHLALİ BİLDİRİM FORMU").wait_for(state="visible")
    page.keyboard.press("Escape")

    page.goto(BASE_URL + "/panel/kvkk?sekme=metinler", wait_until="networkidle")
    page.get_by_text("Aydınlatma metni (KVKK m.10)").first.wait_for(state="visible")
    page.get_by_role("button", name="Yeni sürüm").first.wait_for(state="visible")

    page.goto(BASE_URL + "/panel/kvkk?sekme=alan", wait_until="networkidle")
    page.get_by_text("T.C. kimlik no").first.wait_for(state="visible")

    page.goto(BASE_URL + "/panel/kvkk?sekme=pia", wait_until="networkidle")
    page.get_by_role("button", name="Yeni değerlendirme").click()
    page.get_by_text("Özel nitelikli kişisel veri").first.wait_for(state="visible")
    page.keyboard.press("Escape")

    page.goto(BASE_URL + "/panel/denetim", wait_until="networkidle")
    page.get_by_role("button", name="Zinciri doğrula").click()
    page.get_by_text("Zincir sağlam").first.wait_for(state="visible")
    no_errors(watched)
    psql(f"""DELETE FROM governance_data_breaches WHERE "Title" = '{tag}'""")


def test_kvkk_calisan_ekranlari(session):
    page, watched = session("ayse")
    page.goto(BASE_URL + "/panel/profil?sekme=gizlilik", wait_until="networkidle")
    page.get_by_text("Aydınlatma metni (KVKK m.10)").first.wait_for(state="visible")
    page.goto(BASE_URL + "/panel/profil?sekme=guvenlik", wait_until="networkidle")
    page.get_by_text("Oturumlarım").first.wait_for(state="visible")
    page.get_by_text("Bu oturum").first.wait_for(state="visible")
    page.get_by_role("button", name="Passkey ekle").or_(page.get_by_role("button", name="Yeni passkey ekle")).first.wait_for(state="visible")
    no_errors(watched)

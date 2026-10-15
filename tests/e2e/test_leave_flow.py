"""Uçtan uca iş akışı: çalışan arayüzden izin ister → yönetici onay kutusundan onaylar →
çalışan izni "Onaylandı" görür ve bildirim alır. Tümü tarayıcıda, gerçek servislerle."""

import datetime as dt
import json
import os
import random
import sys
import urllib.request

from conftest import BASE_URL

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "support"))
import hr360_login  # noqa: E402


def test_izin_talebi_onay_akisi(session, browser):
    def api(who, path):
        req = urllib.request.Request(BASE_URL + path, headers={"Authorization": "Bearer " + hr360_login.token(who, browser)})
        with urllib.request.urlopen(req, timeout=30) as r:
            return json.loads(r.read())

    # Önceki çalıştırmalarla çakışmayan, gelecekteki bir hafta içi gün.
    day = dt.date(2035, 1, 1) + dt.timedelta(days=random.randint(0, 2000))
    while day.weekday() >= 5:
        day += dt.timedelta(days=1)
    tag = f"e2e-{random.randint(100000, 999999)}"

    # --- Ayşe: talep
    page, watched = session("ayse")
    page.goto(BASE_URL + "/panel/izin", wait_until="networkidle")
    page.get_by_role("button", name="Yeni izin talebi").first.click()
    page.click("#leave-type")
    page.get_by_role("option", name="Ücretsiz izin").click()
    page.fill("#leave-start", day.isoformat())
    page.fill("#leave-end", day.isoformat())
    page.fill("#leave-reason", tag)
    page.get_by_role("button", name="Talebi gönder").click()
    page.get_by_text("Yeni izin talebi").first.wait_for(state="visible")
    watched.settle()  # gönderim sonrası istekler (liste yenileme) bitsin, hataları toplansın
    assert not [e for e in watched.take() if "HTTP 5" in e or "PAGEERROR" in e]

    mine = api("ayse", "/api/leave/leave-requests")
    req = next(r for r in mine if r.get("reason") == tag)
    assert req["status"] == "Submitted"
    wf = req["workflowRequestId"]

    # --- Mehmet: onay kutusunda görür ve onaylar
    mpage, mwatched = session("mehmet")
    mpage.goto(BASE_URL + "/panel/onaylar", wait_until="networkidle")
    mpage.goto(f"{BASE_URL}/panel/onaylar/{wf}", wait_until="networkidle")
    mpage.get_by_role("button", name="Onayla").first.click()
    dialog = mpage.get_by_role("dialog")
    dialog.get_by_label("Gerekçe").fill("E2E onayı")
    dialog.get_by_role("button", name="Onayla").click()
    try:  # karar kaydedilince iletişim kutusu kapanır; hata olursa açık kalır ve aşağıdaki kontrol yakalar
        dialog.wait_for(state="hidden", timeout=15000)
    except Exception:  # noqa: BLE001
        pass
    mwatched.settle()
    assert not [e for e in mwatched.take() if "HTTP" in e or "PAGEERROR" in e]

    # --- Kafka üzerinden leave-service kaydı kapatır
    for _ in range(40):
        status = next(r for r in api("ayse", "/api/leave/leave-requests") if r["id"] == req["id"])["status"]
        if status == "Approved":
            break
        page.wait_for_timeout(500)
    assert status == "Approved"

    # --- Ayşe arayüzde onaylı görür
    page.goto(BASE_URL + "/panel/izin", wait_until="networkidle")
    page.get_by_text("Onaylandı").first.wait_for(state="visible", timeout=10000)
    notes = api("ayse", "/api/notification/notifications?unreadOnly=false")
    items = notes if isinstance(notes, list) else notes.get("items", [])
    assert any("onay" in (n.get("subject", "") + n.get("body", "")).lower() for n in items[:20])

#!/usr/bin/env python3
"""Dalga 9 (izin, puantaj, vardiya) uçtan uca testi: izin ayarları ve ekip çakışması (69), yarım gün izin (70),
yasal hak ön izlemesi (71), yarım gün tatil alanı ve puantaj dönemi kilidi (72), çalışma süresi kural denetimi (66),
konum ayarları (67), kiosk uçlarının yetkisi (68), vardiya önerisi (44).

Kalıntı: yarım gün izin talebi (gerekçe "test-yarim-gun") iptal edilir ve cleanup_test_data.py siler; puantaj dönemi
satırı (2025-01, gerekçe "Entegrasyon testi dönem kilidi") yeniden açılır ve temizlikte silinir.

Çalıştırma: python3 tests/integration/test_time_leave.py
"""
import datetime as dt
import sys

from common import AYSE, FAIL, api, check, http  # noqa: E402

L = "/api/leave"
T = "/api/timeshift"
today = dt.date.today()
_, me = api("mehmet", "GET", "/api/employee/employees/me")
MEHMET = me["id"]

# ====================================================================== izin ayarları (69-71)
code, s = api("ayse", "GET", f"{L}/leave-settings")
check("İzin ayarları okunur (günlük saat, çakışma eşiği)", code == 200 and s["dayHours"] > 0 and 1 <= s["conflictThresholdPercent"] <= 100, s)
code, _ = api("ayse", "PUT", f"{L}/leave-settings", {"dayHours": 8, "conflictWarnEnabled": True, "conflictThresholdPercent": 30, "carryOverMaxDays": None})
check("Yetki: çalışan izin ayarlarını değiştiremez", code == 403, code)
code, r = api("admin", "PUT", f"{L}/leave-settings", {"dayHours": 13, "conflictWarnEnabled": True, "conflictThresholdPercent": 30, "carryOverMaxDays": None})
check("İzin ayarları doğrulanır (günlük saat 1-12)", code == 400, r)

d1 = today + dt.timedelta(days=45)
while d1.weekday() >= 5:
    d1 += dt.timedelta(days=1)
code, c = api("ayse", "GET", f"{L}/leave-requests/team-conflict?startDate={d1}&endDate={d1 + dt.timedelta(days=2)}")
check("Ekip çakışması: talep eden sayıyı görür, adları görmez", code == 200 and "exceeds" in c and c.get("people") is None, c)
code, _ = api("ayse", "GET", f"{L}/leave-requests/team-conflict?employeeId={MEHMET}&startDate={d1}&endDate={d1}")
check("Ekip çakışması: çalışan başkasının çakışmasına bakamaz", code == 404, code)
code, c = api("admin", "GET", f"{L}/leave-requests/team-conflict?employeeId={AYSE}&startDate={d1}&endDate={d1}")
check("Ekip çakışması: İK adları da görür (liste)", code == 200 and isinstance(c.get("people"), list), c)
code, _ = api("ayse", "GET", f"{L}/leave-requests/team-conflict?startDate={d1}&endDate={d1 - dt.timedelta(days=1)}")
check("Ekip çakışması: ters tarih aralığı reddedilir", code == 400, code)

# ====================================================================== yarım gün izin (70)
half = None
for i in range(10):
    day = d1 + dt.timedelta(days=i)
    if day.weekday() >= 5:
        continue
    code, half = api("ayse", "POST", f"{L}/leave-requests", {"employeeId": AYSE, "type": "Unpaid", "startDate": day.isoformat(),
                                                           "endDate": day.isoformat(), "days": 0.5, "reason": "test-yarim-gun"})
    if code != 409:
        break
check("Yarım gün izin: 0,5 gün olarak açılır", code == 201 and half and half["days"] == 0.5 and half.get("hours") is None, half)
if code == 201:
    wf = half.get("workflowRequestId")
    if wf:
        code, w = api("mehmet", "GET", f"/api/workflow/workflows/{wf}")
        payload = (w or {}).get("payload") or ""
        check("Onaycı yükünde ekip çakışması bilgisi (sayı) var", code == 200 and "teamOnLeave" in payload and "teamSize" in payload, payload)
    code, r = api("ayse", "POST", f"{L}/leave-requests/{half['id']}/cancel")
    check("Yarım gün izin iptal edildi", code == 200 and r["status"] == "Cancelled", r)

# ====================================================================== yasal hak ve tatiller (71, 72)
code, st = api("admin", "GET", f"{L}/leave-balances/statutory?year={today.year}")
check("Yasal hak ön izlemesi: tahakkuk ve uygulanınca bakiye alanları", code == 200 and (not st or ("accrued" in st[0] and "proposedEntitled" in st[0])), st[:1] if isinstance(st, list) else st)
code, hol = api("ayse", "GET", f"{L}/public-holidays?year={today.year}")
check("Resmî tatillerde yarım gün alanı", code == 200 and (not hol or "isHalfDay" in hol[0]), hol[:1] if isinstance(hol, list) else hol)

# ====================================================================== çalışma süresi kuralları (66)
code, ts = api("ayse", "GET", f"{T}/timesheet-report/settings")
check("Puantaj ayarlarında kural ve konum alanları", code == 200 and ts.get("minRestHours") and ts.get("weeklyMaxHours") and ts.get("geoOutsidePolicy") in ("Block", "Flag"), ts)
code, r = api("ayse", "PUT", f"{T}/timesheet-report/settings", {**{k: ts[k] for k in ("lateGraceMinutes", "defaultStart", "defaultEnd", "defaultBreakMinutes")}, "minRestHours": 5})
check("Yetki: çalışan kural ayarını değiştiremez", code == 403, r)
code, r = api("admin", "PUT", f"{T}/timesheet-report/settings", {**{k: ts[k] for k in ("lateGraceMinutes", "defaultStart", "defaultEnd", "defaultBreakMinutes")}, "minRestHours": 5})
check("Kural ayarı doğrulanır (dinlenme 8-24 saat)", code == 400, r)
far = today + dt.timedelta(days=60)
code, r = api("mehmet", "POST", f"{T}/shifts/rule-check", {"employeeId": AYSE, "date": far.isoformat(), "startTime": "07:00:00", "endTime": "21:00:00", "breakMinutes": 0})
check("Kural denetimi: 14 saatlik vardiya günlük sınırı aşar", code == 200 and any(w["code"] == "daily" for w in r["warnings"]), r)
code, r = api("mehmet", "POST", f"{T}/shifts/rule-check", {"employeeId": AYSE, "date": far.isoformat(), "startTime": "22:00:00", "endTime": "07:00:00", "breakMinutes": 60})
check("Kural denetimi: 8 saatlik gece vardiyası gece sınırını aşar", code == 200 and any(w["code"] == "night" for w in r["warnings"]), r)
code, _ = api("ayse", "POST", f"{T}/shifts/rule-check", {"employeeId": AYSE, "date": far.isoformat(), "startTime": "08:00:00", "endTime": "16:00:00"})
check("Yetki: çalışan kural denetimi ucunu kullanamaz", code == 403, code)

# ====================================================================== kiosk (68)
code, _ = http("GET", f"{T}/time-clock/kiosk/state")
check("Kiosk: anahtarsız istek reddedilir", code == 401, code)
code, _ = http("GET", f"{T}/time-clock/kiosk/state", headers={"X-Device-Key": "hrc_demo_gecersizanahtar"})
check("Kiosk: geçersiz anahtar reddedilir", code == 401, code)
code, _ = http("POST", f"{T}/time-clock/terminal/punch", {"badgeCode": "X1", "pin": "1234", "source": "kiosk"}, {"X-Device-Key": "hrc_demo_gecersiz"})
check("Kiosk PIN: geçersiz anahtar reddedilir", code == 401, code)

# ====================================================================== puantaj dönemi kilidi (72)
code, periods = api("admin", "GET", f"{T}/timesheet-periods?year=2025")
check("Puantaj dönemleri: 12 ay", code == 200 and len(periods) == 12, periods)
if code == 200 and periods[0]["status"] == "Closed":
    api("admin", "POST", f"{T}/timesheet-periods/2025/1/reopen", {"reason": "Entegrasyon testi dönem kilidi"})
code, r = api("admin", "POST", f"{T}/timesheet-periods/2025/1/close")
check("Puantaj dönemi kapatıldı", code == 200 and r["status"] == "Closed", r)
code, r = api("admin", "POST", f"{T}/timesheet-periods/2025/1/close")
check("Kapalı dönem yeniden kapatılamaz", code == 409, r)
code, r = api("admin", "POST", f"{T}/timesheet-periods/2025/1/reopen", {"reason": "kısa"})
check("Yeniden açma gerekçe ister", code == 400, r)
code, r = api("admin", "POST", f"{T}/timesheet-periods/2025/1/reopen", {"reason": "Entegrasyon testi dönem kilidi"})
check("Puantaj dönemi yeniden açıldı", code == 200 and r["status"] == "Open", r)
nxt = today.replace(day=1) + dt.timedelta(days=32)
code, r = api("admin", "POST", f"{T}/timesheet-periods/{nxt.year}/{nxt.month}/close")
check("Gelecek dönem kapatılamaz", code == 400, r)
code, _ = api("ayse", "GET", f"{T}/timesheet-periods?year=2025")
check("Yetki: çalışan puantaj dönemlerini göremez", code == 403, code)

# ====================================================================== vardiya önerisi (44)
code, teams = api("admin", "GET", f"{T}/shift-teams")
code2, shifts = api("admin", "GET", f"{T}/shifts")
if code == 200 and teams and code2 == 200 and shifts:
    body = {"from": (today + dt.timedelta(days=1)).isoformat(), "to": (today + dt.timedelta(days=7)).isoformat(),
            "teamId": teams[0]["id"], "demand": [{"shiftId": shifts[0]["id"], "required": 1, "weekdays": [1, 2, 3, 4, 5]}]}
    code, p = api("admin", "POST", f"{T}/shift-optimizer/propose", body)
    check("Vardiya önerisi: fark, karşılanmayan talep ve adalet özeti (kayıt yazmaz)",
          (code == 200 and isinstance(p["diff"], list) and "fairness" in p and p["solver"] in ("cp-sat", "greedy"))
          or (code == 400 and "çalışan" in (p or {}).get("message", "")), p)
    code, r = api("ayse", "POST", f"{T}/shift-optimizer/propose", body)
    check("Yetki: çalışan öneri isteyemez", code == 403, code)
    code, r = api("admin", "POST", f"{T}/shift-optimizer/propose", {**body, "from": (today - dt.timedelta(days=3)).isoformat()})
    check("Vardiya önerisi geçmiş günden başlayamaz", code == 400, r)
else:
    print("--  vardiya ekibi ya da vardiya tanımı yok; öneri testi atlandı")

code, r = api("admin", "POST", f"{T}/shift-optimizer/apply", {"upserts": [], "removeAssignmentIds": []})
check("Öneri uygulama: boş istek reddedilir", code == 400, r)

print(f"\nFAILS: {len(FAIL)}")
for f in FAIL:
    print("  -", f)
sys.exit(1 if FAIL else 0)

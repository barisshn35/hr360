#!/usr/bin/env python3
# hr360-modules: compensation,timeshift  (kapalıysa scripts/test.sh bu betiği atlar)
"""Bordro dönemi (Y2), fazla mesai (Y5) ve giriş-çıkış (Y9) uçtan uca testi.

Çalıştırma: python3 tests/integration/test_payroll_time.py
"""
import datetime as dt
import subprocess
import sys
import time

from common import AYSE, FAIL, api, check, http  # noqa: E402

C = "/api/compensation/compensation"
T = "/api/timeshift"


def psql(sql):
    out = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", "hr360_operational", "-Atc", sql],
                         capture_output=True, text=True, cwd=__import__("os").path.join(__import__("os").path.dirname(__file__), "..", ".."))
    return out.stdout.strip()


_, me = api("mehmet", "GET", "/api/employee/employees/me")
MEHMET = me["id"]
today = dt.date.today()

# ====================================================================== parametreler
# Ürün kararı: yıllık bordro parametrelerini bordro yetkilisi girer; test, sabit/tohum değerlere dayanmaz.
# Hesap motoru, kaydetmeden örnek hesap ucuyla (POST parameters/{yıl}/preview) ÖRNEK değerlerle sınanır.
TEST_SOURCE = "TEST bordro parametreleri (test_payroll_time)"
TEST_PARAMS = {"validFromMonth": 1, "minimumWageGross": 33030, "minimumWageNet": None, "sgkEmployeeRate": 0.14, "unemploymentEmployeeRate": 0.01,
               "sgkEmployerRate": 0.2175, "employerIncentivePoints": 2, "unemploymentEmployerRate": 0.02, "stampTaxRate": 0.00759,
               "sgkCeilingMultiplier": 9, "minimumWageExemption": True, "agiMonthly": None, "severanceCeilingH1": None, "severanceCeilingH2": None,
               "brackets": [{"upTo": 190000, "rate": 0.15}, {"upTo": 400000, "rate": 0.2}, {"upTo": 1500000, "rate": 0.27},
                            {"upTo": 5300000, "rate": 0.35}, {"upTo": None, "rate": 0.4}], "source": TEST_SOURCE}
code, pv = api("admin", "POST", f"{C}/payroll/parameters/2032/preview", {"parameters": TEST_PARAMS, "monthlyGross": 33030, "month": 1})
check("Örnek hesap: girilen değerlerle asgari ücret neti ve SGK tavanı", code == 200 and pv["sample"]["net"] == 28075.5
      and pv["sample"]["employerCost"] == 40214.03 and pv["sgkCeiling"] == 297270, (code, pv))
code, _ = api("ayse", "GET", f"{C}/payroll/parameters/2032")
check("Yetki: çalışan bordro parametrelerini göremez", code == 403, code)

# ====================================================================== fazla mesai
# Önceki çalıştırmaların kalıntıları temizlenir (yalnızca bu testin gerekçesiyle açılanlar).
psql("DELETE FROM timeshift_overtime_requests WHERE \"Reason\" LIKE 'test-fm-%'")
d1 = today + dt.timedelta(days=7)
while d1.weekday() >= 5:
    d1 += dt.timedelta(days=1)
d2 = d1 + dt.timedelta(days=1)
code, r = api("ayse", "POST", f"{T}/overtime", {"date": d1.isoformat(), "hours": 5, "reason": "test-fm-fazla"})
check("Fazla mesai: günde 4 saati aşamaz", code == 400, r)
code, ot1 = api("ayse", "POST", f"{T}/overtime", {"date": d1.isoformat(), "hours": 3, "reason": "test-fm-onay"})
check("Fazla mesai talebi oluşturuldu, onay akışı açıldı", code == 200 and ot1["status"] == "Pending" and ot1.get("workflowRequestId"), ot1)
code, r = api("ayse", "POST", f"{T}/overtime", {"date": d1.isoformat(), "hours": 1, "reason": "test-fm-cift"})
check("Fazla mesai: aynı güne ikinci talep reddedilir", code == 409, r)
code, ot2 = api("ayse", "POST", f"{T}/overtime", {"date": d2.isoformat(), "hours": 2, "reason": "test-fm-bekleyen"})
check("İkinci talep (onay bekleyecek)", code == 200, ot2)
code, r = api("ayse", "POST", f"{T}/overtime", {"employeeId": MEHMET, "date": d1.isoformat(), "hours": 1, "reason": "test-fm-baskasi"})
check("Yetki: çalışan başkası adına fazla mesai giremez", code == 403, r)

wf = ot1.get("workflowRequestId")
code, w = api("mehmet", "GET", f"/api/workflow/workflows/{wf}")
check("Onay kutusunda 'Fazla Mesai' türüyle görünür", code == 200 and w["type"] == "Overtime", w)
step = w["steps"][0]["id"]
code, _ = api("mehmet", "POST", f"/api/workflow/workflows/{wf}/steps/{step}/decide", {"decision": "Approved", "comment": "ok"})
check("Bölüm başı onayladı", code == 200, code)
st = None
for _ in range(30):
    _, lst = api("ayse", "GET", f"{T}/overtime?year={d1.year}")
    st = next((x["status"] for x in lst if x["id"] == ot1["id"]), None)
    if st == "Approved":
        break
    time.sleep(1)
check("Karar olayı fazla mesaiyi 'Onaylandı' yaptı", st == "Approved", st)
code, s = api("ayse", "GET", f"{T}/overtime/summary?year={d1.year}")
check("Yıllık özet: onaylı 3, bekleyen 2 saat", code == 200 and s["approvedHours"] >= 3 and s["pendingHours"] >= 2 and s["limitHours"] == 270, s)

# Yıllık 270 saat sınırı: yapay kayıtla sınıra yaklaştırılır.
psql(f"INSERT INTO timeshift_overtime_requests (\"Id\",\"TenantSlug\",\"EmployeeId\",\"Date\",\"Hours\",\"Reason\",\"Status\") "
     f"VALUES (gen_random_uuid(),'demo','{AYSE}','{d1.year}-01-02',262,'test-fm-yapay','Approved')")
d3 = d2 + dt.timedelta(days=1)
code, r = api("ayse", "POST", f"{T}/overtime", {"date": d3.isoformat(), "hours": 4, "reason": "test-fm-sinir"})
check("Fazla mesai: yıllık 270 saat sınırı aşılamaz", code == 400 and r.get("code") == "annual_limit", r)
psql("DELETE FROM timeshift_overtime_requests WHERE \"Reason\" = 'test-fm-yapay'")

# ====================================================================== bordro dönemi
y, m = d1.year, d1.month
# Dönemin ayı için parametre yoksa (temiz kurulum) test kendi örnek satırını girer ve sonda siler;
# canlıdaki (kullanıcının girdiği) satırlara dokunulmaz.
_, py = api("admin", "GET", f"{C}/payroll/parameters/{y}?month={m}")
own_params = not py.get("configured")
if own_params:
    code, r = api("admin", "PUT", f"{C}/payroll/parameters/{y}", dict(TEST_PARAMS, validFromMonth=1))
    check("Hazırlık: dönem yılı için örnek parametre satırı girildi", code == 200 and r["configured"], (code, r))
code, periods = api("admin", "GET", f"{C}/payroll/periods?year={y}")
old = next((x for x in periods if x["month"] == m), None)
if old:
    if old["status"] == "Closed":
        api("admin", "POST", f"{C}/payroll/periods/{old['id']}/reopen", {"reason": "onceki test calismasindan kalan donem"})
    api("admin", "DELETE", f"{C}/payroll/periods/{old['id']}")
code, per = api("admin", "POST", f"{C}/payroll/periods", {"year": y, "month": m})
check("Bordro dönemi açıldı", code == 200 and per["status"] == "Open", per)
pid = per["id"]
code, _ = api("admin", "POST", f"{C}/payroll/periods", {"year": y, "month": m})
check("Aynı dönem iki kez açılamaz", code == 409, code)
code, _ = api("ayse", "POST", f"{C}/payroll/periods/{pid}/calculate")
check("Yetki: çalışan bordro hesaplayamaz", code == 403, code)
code, adj = api("admin", "POST", f"{C}/payroll/periods/{pid}/adjustments", {"employeeId": AYSE, "kind": "Addition", "amount": 1000, "description": "Prim"})
check("Ek ödeme eklendi", code == 200, adj)
code, calc = api("admin", "POST", f"{C}/payroll/periods/{pid}/calculate")
check("Dönem hesaplandı", code == 200 and calc["employeeCount"] >= 1, calc)
check("Bordro denetimi (ML dalgası 2): hesaplama yanıtında denetim durumu", "anomalyChecked" in calc and "anomalyFlags" in calc, calc)
code, an = api("admin", "GET", f"{C}/payroll/periods/{pid}/anomalies")
check("Bordro denetim işaretleri bordro yetkilisine açık", code == 200 and isinstance(an.get("items"), list), (code, an))
code, _ = api("ayse", "GET", f"{C}/payroll/periods/{pid}/anomalies")
check("Yetki: çalışan bordro denetim işaretlerini göremez", code == 403, code)
code, slips = api("admin", "GET", f"{C}/payroll/periods/{pid}/payslips")
mine = next((s for s in slips or [] if s["employeeId"] == AYSE), None)
approved_h = float(psql(f"SELECT coalesce(sum(\"Hours\"),0) FROM timeshift_overtime_requests WHERE \"EmployeeId\"='{AYSE}' AND \"Status\"='Approved' "
                        f"AND date_trunc('month', \"Date\") = date '{y}-{m:02d}-01'") or 0)
check("Ayşe'nin pusulası: yalnızca ONAYLI fazla mesai girdi (bekleyen 2 saat hariç)", mine and approved_h >= 3 and float(mine["overtimeHours"]) == approved_h, (mine and mine["overtimeHours"], approved_h))
check("Pusula: ek ödeme brüte eklendi, net < brüt", mine and float(mine["additions"]) == 1000 and float(mine["net"]) < float(mine["gross"]), mine)
check("Pusula yanıtında denetim işareti alanı yok", mine and not any(k.lower().startswith("anomaly") for k in mine), mine and list(mine))
time.sleep(1)
logged = psql("SELECT count(*) FROM audit_log WHERE \"Service\"='compensation-service' AND \"Action\"='SensitiveViewed' AND \"OccurredAt\" > now() - interval '1 minute'")
check("KVKK: İK'nın bordro listesini açması erişim kaydına yazıldı", int(logged or 0) >= 1, logged)

code, mys = api("ayse", "GET", f"{C}/payslips/me")
check("Kapanmamış dönemin pusulası çalışana görünmez", code == 200 and not any(s["periodId"] == pid for s in mys), mys)
code, _ = api("ayse", "GET", f"{C}/payslips/{mine['id']}") if mine else (0, None)
check("Kapanmamış pusula doğrudan da açılamaz", code == 404, code)
code, _ = api("mehmet", "GET", f"{C}/payslips/{mine['id']}") if mine else (0, None)
check("Yetki: yönetici başkasının pusulasını göremez", code == 404, code)

# Güvenlik dalgası 2B: görevler ayrılığı — hesaplayan/ek ödeme giren (admin) dönemi kapatamaz.
code, r = api("admin", "POST", f"{C}/payroll/periods/{pid}/close")
check("Görevler ayrılığı: hazırlayan dönemi kapatamaz (409)", code == 409 and r.get("code") == "sod_same_user", (code, r))
sod = psql(f"SELECT count(*) FROM audit_log WHERE \"EntityId\"='{pid}' AND \"Action\"='SodBlocked'")
check("Görevler ayrılığı engeli denetim kaydında", sod == "1", sod)
code, per2 = api("admin", "GET", f"{C}/payroll/periods?year={y}")
check("Dönem listesinde hazırlayan görünür", code == 200 and any(p["id"] == pid and p.get("calculatedBy") for p in per2), per2)
code, cl = api("ik", "POST", f"{C}/payroll/periods/{pid}/close")
check("Dönem başka bir bordro yetkilisince (İK) kapatıldı", code == 200 and cl["status"] == "Closed", cl)
code, r = api("admin", "POST", f"{C}/payroll/periods/{pid}/calculate")
check("Kilitli bordro dönemi yeniden hesaplanamaz", code == 409 and r.get("code") == "period_closed", r)
code, r = api("admin", "POST", f"{C}/payroll/periods/{pid}/adjustments", {"employeeId": AYSE, "kind": "Deduction", "amount": 10, "description": "x"})
check("Kilitli döneme ek ödeme/kesinti eklenemez", code == 409, r)
code, mys = api("ayse", "GET", f"{C}/payslips/me")
check("Kapanan dönemin pusulası çalışana görünür", code == 200 and any(s["periodId"] == pid for s in mys), mys)
code, one = api("ayse", "GET", f"{C}/payslips/{mine['id']}")
check("Çalışan kendi pusulasını açar (oranlarla)", code == 200 and one["payslip"]["id"] == mine["id"] and "rates" in one, one)
code, r = api("admin", "POST", f"{C}/payroll/periods/{pid}/reopen", {"reason": "kisa"})
check("Yeniden açma gerekçesiz olmaz", code == 400, r)
code, r = api("admin", "POST", f"{C}/payroll/periods/{pid}/reopen", {"reason": "Test: kapanan donemin yeniden acilmasi"})
check("Kiracı yöneticisi gerekçeyle yeniden açar", code == 200 and r["status"] == "Calculated", r)
reopened = psql(f"SELECT count(*) FROM audit_log WHERE \"EntityId\"='{pid}' AND \"Action\"='Reopened'")
check("Yeniden açma denetim kaydında", reopened == "1", reopened)
code, _ = api("admin", "DELETE", f"{C}/payroll/periods/{pid}")
check("Dönem silindi (temizlik)", code == 204, code)
if own_params:
    code, _ = api("admin", "DELETE", f"{C}/payroll/parameters/{y}?validFromMonth=1")
    check("Örnek parametre satırı silindi (temizlik)", code == 204, code)

# ====================================================================== giriş-çıkış
code, site = api("admin", "POST", f"{T}/time-clock/sites", {"name": "Test Merkez Ofis", "allowQr": True, "allowTerminal": True,
                                                              "checkLocation": True, "latitude": 41.0369, "longitude": 28.9850, "radiusMeters": 200})
check("Giriş-çıkış noktası oluşturuldu", code == 200 and site["checkLocation"], site)
sid = site["id"]
code, _ = api("ayse", "POST", f"{T}/time-clock/sites", {"name": "x", "allowQr": True, "allowTerminal": True, "checkLocation": False})
check("Yetki: çalışan nokta oluşturamaz", code == 403, code)
code, qr = api("admin", "GET", f"{T}/time-clock/sites/{sid}/qr")
check("Kiosk QR jetonu", code == 200 and qr["token"].startswith(sid.replace("-", "")), qr)

# Ayşe'nin bugün açık kaydı varsa kapatılır (önceki çalıştırma).
_, st0 = api("ayse", "GET", f"{T}/time-clock/me")
psql(f"DELETE FROM timeshift_time_entries WHERE \"EmployeeId\"='{AYSE}' AND \"Date\" >= current_date - 1")
code, r = api("ayse", "POST", f"{T}/time-clock/punch", {"token": qr["token"]})
check("QR: konum izni yoksa reddedilir (bu noktada konum denetimi açık)", code == 400 and r.get("code") == "location_required", r)
code, r = api("ayse", "POST", f"{T}/time-clock/punch", {"token": qr["token"], "latitude": 41.06, "longitude": 29.01})
check("QR: nokta dışından giriş reddedilir", code == 400 and r.get("code") == "off_site", r)
code, r = api("ayse", "POST", f"{T}/time-clock/punch", {"token": qr["token"][:-3] + "AAA", "latitude": 41.0370, "longitude": 28.9851})
check("QR: sahte imza reddedilir", code == 400 and r.get("code") == "qr_invalid", r)
code, r = api("ayse", "POST", f"{T}/time-clock/punch", {"token": qr["token"], "latitude": 41.0370, "longitude": 28.9851})
check("QR + konum: giriş yapıldı (noktada)", code == 200 and r["punch"]["kind"] == "In" and r["punch"]["method"] == "Qr" and r["punch"]["onSite"] is True, r)
# Ham koordinat saklama kiracı ayarıdır ve varsayılan kapalıdır: yalnızca "noktada mı" + uzaklık aralığı tutulur.
raw = psql("SELECT count(*) FROM timeshift_clock_punches WHERE \"TenantSlug\"='demo' AND (\"RawLatitude\" IS NOT NULL OR \"RawLongitude\" IS NOT NULL)")
check("KVKK: ham koordinat saklama kapalıyken koordinat yazılmaz", raw.strip() == "0", raw)
leaked = psql("SELECT count(*) FROM audit_log WHERE \"Service\"='timeshift-service' AND \"Changes\"::text LIKE '%41.037%' AND \"OccurredAt\" > now() - interval '5 minutes'")
check("KVKK: koordinat denetim kaydına da yazılmadı", leaked == "0", leaked)

# Terminal: kart ve PIN
code, dk = api("admin", "POST", f"{T}/time-clock/sites/{sid}/device-key")
check("Terminal anahtarı üretildi", code == 200 and dk["deviceKey"].startswith("hrc_demo_"), dk)
key = dk["deviceKey"]
code, cr = api("admin", "PUT", f"{T}/time-clock/credentials/{AYSE}", {"badgeCode": "T1042", "cardNumber": "04A1B2C3D4"})
check("İK sicil kodu ve kart atadı", code == 200 and cr["hasCard"], cr)
stored = psql(f"SELECT \"CardHash\" FROM timeshift_clock_credentials WHERE \"EmployeeId\"='{AYSE}'")
check("KVKK: kart numarası düz metin saklanmaz", "04A1B2C3D4" not in stored and len(stored) == 64, stored)
time.sleep(1)
inaudit = psql(f"SELECT count(*) FROM audit_log WHERE \"Changes\"::text LIKE '%{stored[:16]}%'")
check("Denetim kaydında kart özeti de yazılmaz (maskeli)", inaudit == "0", inaudit)
code, r = api("ayse", "PUT", f"{T}/time-clock/me/pin", {"pin": "1111"})
check("Zayıf PIN reddedilir", code == 400, r)
code, r = api("ayse", "PUT", f"{T}/time-clock/me/pin", {"pin": "4826"})
check("Çalışan PIN belirledi", code == 200, r)
code, r = http("POST", f"{T}/time-clock/terminal/punch", {"cardNumber": "04A1B2C3D4"}, {"X-Device-Key": key[:-2] + "xx"})
check("Terminal: yanlış anahtar 401", code == 401, code)
code, r = http("POST", f"{T}/time-clock/terminal/punch", {"cardNumber": "04a1b2c3d4"}, {"X-Device-Key": key})
check("Terminal: kartla çıkış (ekranda yalnızca ad)", code == 200 and r["kind"] == "Out" and r["firstName"] == "Ayşe" and "lastName" not in r, r)
for i in range(5):
    code, r = http("POST", f"{T}/time-clock/terminal/punch", {"badgeCode": "T1042", "pin": "0000"}, {"X-Device-Key": key})
code, r = http("POST", f"{T}/time-clock/terminal/punch", {"badgeCode": "T1042", "pin": "4826"}, {"X-Device-Key": key})
check("Terminal: 5 hatalı PIN sonrası kilit (doğru PIN de 15 dk bekler)", code == 423, (code, r))
api("admin", "PUT", f"{T}/time-clock/credentials/{AYSE}", {"badgeCode": "T1042"})  # kilit açılır
code, me_ = api("ayse", "GET", f"{T}/time-clock/me")
check("Çalışan kendi hareketlerini görür", code == 200 and len(me_["punches"]) >= 2 and me_["hasPin"], me_)
code, _ = api("ayse", "GET", f"{T}/time-clock/punches")
check("Yetki: çalışan herkesin hareketlerini göremez", code == 403, code)

# temizlik
api("admin", "PUT", f"{T}/time-clock/credentials/{AYSE}", {"badgeCode": "", "clearCard": True})
api("admin", "DELETE", f"{T}/time-clock/sites/{sid}")
psql(f"DELETE FROM timeshift_clock_punches WHERE \"SiteId\"='{sid}'")
psql(f"UPDATE timeshift_clock_credentials SET \"PinHash\" = NULL WHERE \"EmployeeId\"='{AYSE}'")
psql("DELETE FROM timeshift_overtime_requests WHERE \"Reason\" LIKE 'test-fm-%' AND \"Status\" <> 'Pending'")
api("ayse", "POST", f"{T}/overtime/{ot2['id']}/cancel")

print(f"\nFAILS: {len(FAIL)}")
for f in FAIL:
    print("  -", f)
sys.exit(1 if FAIL else 0)

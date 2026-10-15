#!/usr/bin/env python3
"""Bordro ekosistemi ve masraf (Dalga 5b): SGK/banka/muhasebe dosyaları (Y1/Y3/Y4), avans
taksitleri (Y11), esnek yan haklar (Y13), zam dönemi (Y21), TCMB kuru/km/limit (G9), seyahat ve
harcırah (Y12), fiş okuma (Y27).

Bordro dalgası 8 (madde 58–65): parametre yürürlük satırları, SGK ayarları ve meslek kodu, APHB
doğrulama ve TXT, banka örnek şablonu (dosya özeti), masraf merkezi eşlemesi, e-bordro yayım/okundu,
fark bordrosu, kıdem/ihbar (dört göz) ve ibraname, bant uyumu. Dış sistemler (SGK, banka, e-posta)
gerçek hesaplarla denenmez: yalnızca dosya üretilir / e-posta notification kuyruğuna yazılır.

Ön koşul: HR360 çalışıyor, deploy/testing/chat-mock.yml katmanı açık (sahte TCMB kurları).
"""
import datetime as dt
import hashlib
import io
import json
import os
import subprocess
import sys
import time
import urllib.request

sys.path.insert(0, os.path.dirname(__file__))
from common import AYSE, BASE, FAIL, api, check, tok  # noqa: E402

C = "/api/compensation/compensation"
E = "/api/expense"
W = "/api/workflow/workflows"
ROOT = os.path.join(os.path.dirname(__file__), "..", "..")
FAIL.clear()
YEAR = 2031
TCKN, IBAN = "10000000146", "TR330006100519786457841326"
# Ürün kararı: yıllık bordro parametrelerini bordro yetkilisi girer; uygulama/kurulum değer varsaymaz.
# Test, {YEAR} için kendi parametrelerini API ile girer (kaynak notu "TEST ..." — cleanup_test_data.py
# {YEAR} satırlarını siler). Değerler ÖRNEKTİR, resmî değer değildir.
TEST_BRACKETS = [{"upTo": 190000, "rate": 0.15}, {"upTo": 400000, "rate": 0.2}, {"upTo": 1500000, "rate": 0.27}, {"upTo": 5300000, "rate": 0.35}, {"upTo": None, "rate": 0.4}]
TEST_PARAMS = {"validFromMonth": 1, "minimumWageGross": 33030, "minimumWageNet": None, "sgkEmployeeRate": 0.14, "unemploymentEmployeeRate": 0.01,
               "sgkEmployerRate": 0.2175, "employerIncentivePoints": 2, "unemploymentEmployerRate": 0.02, "stampTaxRate": 0.00759,
               "sgkCeilingMultiplier": 9, "brackets": TEST_BRACKETS, "minimumWageExemption": True, "agiMonthly": None,
               "severanceCeilingH1": 64948.77, "severanceCeilingH2": 73729.87, "source": "TEST bordro parametreleri (örnek değer)"}


def psql(sql):
    out = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", "hr360_operational", "-Atc", sql],
                         capture_output=True, text=True, cwd=ROOT)
    return (out.stdout + out.stderr).strip()


def download(who, path):
    req = urllib.request.Request(BASE + path, headers={"Authorization": "Bearer " + tok(who)})
    try:
        with urllib.request.urlopen(req, timeout=30) as r:
            return r.status, r.read()
    except urllib.error.HTTPError as e:
        return e.code, e.read()


def cleanup():
    psql(f"""DELETE FROM compensation_payroll_exports WHERE "PeriodId" IN (SELECT "Id" FROM compensation_payroll_periods WHERE "Year" = {YEAR});
             DELETE FROM compensation_payslips WHERE "Year" = {YEAR};
             DELETE FROM compensation_payroll_adjustments WHERE "PeriodId" IN (SELECT "Id" FROM compensation_payroll_periods WHERE "Year" = {YEAR});
             DELETE FROM compensation_payroll_periods WHERE "Year" = {YEAR};
             DELETE FROM compensation_advances WHERE "Reason" LIKE 'TEST%';
             DELETE FROM compensation_benefit_elections WHERE "PlanId" IN (SELECT "Id" FROM compensation_benefit_plans WHERE "Year" = {YEAR});
             DELETE FROM compensation_benefit_options WHERE "PlanId" IN (SELECT "Id" FROM compensation_benefit_plans WHERE "Year" = {YEAR});
             DELETE FROM compensation_benefit_plans WHERE "Year" = {YEAR};
             DELETE FROM compensation_records WHERE "Note" = 'TEST zam {YEAR}';
             UPDATE compensation_records SET "EffectiveTo" = NULL WHERE "EffectiveTo" = '{YEAR}-05-31';
             DELETE FROM compensation_raise_proposals WHERE "CycleId" IN (SELECT "Id" FROM compensation_raise_cycles WHERE "Name" LIKE 'TEST%');
             DELETE FROM compensation_raise_cycles WHERE "Name" LIKE 'TEST%';
             DELETE FROM compensation_salary_bands WHERE "Grade" LIKE 'TEST%';
             DELETE FROM workflow_approval_steps WHERE "WorkflowRequestId" IN (SELECT "Id" FROM workflow_requests WHERE "Subject" LIKE 'TEST%' OR "Subject" LIKE 'Seyahat: TEST%');
             DELETE FROM workflow_requests WHERE "Subject" LIKE 'TEST%' OR "Subject" LIKE 'Seyahat: TEST%';
             DELETE FROM expense_items WHERE "ClaimId" IN (SELECT "Id" FROM expense_claims WHERE "Title" LIKE 'TEST%' OR "Title" LIKE 'Harcırah: TEST%');
             DELETE FROM expense_claims WHERE "Title" LIKE 'TEST%' OR "Title" LIKE 'Harcırah: TEST%';
             DELETE FROM expense_travel_requests WHERE "Destination" LIKE 'TEST%';
             DELETE FROM expense_policies WHERE "TenantSlug" = 'demo';
             DELETE FROM expense_fx_rates WHERE "Source" = 'Manual' AND "TenantSlug" = 'demo';""")
    # Dalga 8 kalıntıları (tablolar 2026-10-21_payroll_tr.sql ile gelir).
    psql(f"""DELETE FROM compensation_payslip_deliveries d WHERE NOT EXISTS (SELECT 1 FROM compensation_payroll_periods p WHERE p."Id" = d."PeriodId");
             DELETE FROM compensation_retro_diffs WHERE "SourceYear" = {YEAR};
             DELETE FROM compensation_severance_calcs WHERE "EmployeeId" = '{AYSE}' AND extract(year FROM "LastWorkingDay") = {YEAR};
             DELETE FROM compensation_payroll_parameters WHERE "Year" = {YEAR};
             DELETE FROM compensation_opening_tax_bases WHERE "Year" = {YEAR};
             DELETE FROM compensation_records WHERE "Note" = 'TEST retro {YEAR}';
             UPDATE compensation_records SET "EffectiveTo" = NULL WHERE "EmployeeId" = '{AYSE}' AND "EffectiveTo" = '{YEAR}-03-31';""")


cleanup()
# Ayşe'nin profilindeki TCKN/IBAN test için geçerli değerlere ayarlanır, sonda geri alınır.
_, orig_iban = api("ayse", "GET", f"/api/engagement/profile/{AYSE}/reveal?field=iban")
_, orig_tckn = api("ayse", "GET", f"/api/engagement/profile/{AYSE}/reveal?field=nationalId")
code, _ = api("ayse", "PUT", "/api/engagement/profile/me", {"iban": IBAN, "nationalId": TCKN})
check("Hazırlık: TCKN/IBAN profilde (şifreli)", code == 200, code)

# ====================================================================== Y11 avans
code, adv = api("ayse", "POST", f"{C}/advances", {"kind": "Advance", "amount": 3000, "installments": 3, "startYear": YEAR, "startMonth": 4, "reason": "TEST avans"})
check("Çalışan avans talep eder", code == 200 and adv["status"] == "Pending" and adv["installment"] == 1000, (code, adv))
code, _ = api("ayse", "POST", f"{C}/advances", {"kind": "Advance", "amount": 10, "installments": 1, "reason": "TEST ikinci"})
check("Bekleyen talep varken ikincisi açılamaz", code == 409, code)
code, _ = api("ayse", "POST", f"{C}/advances/{adv['id']}/decide", {"approve": True})
check("Çalışan avans kararı veremez", code == 403, code)
code, _ = api("admin", "POST", f"{C}/advances/{adv['id']}/decide", {"approve": False})
check("Ret gerekçesiz olamaz", code == 400, code)
code, r = api("admin", "POST", f"{C}/advances/{adv['id']}/decide", {"approve": True})
check("İK onaylar", code == 200 and r["status"] == "Approved", (code, r))
cnt = psql(f"""SELECT count(*) FROM notification_messages WHERE "TemplateCode" = 'compensation.advance' AND "RecipientEmployeeId" = '{AYSE}' AND "Body" NOT LIKE '%3000%'""")
check("Bildirim gitti (tutar yazılmadan)", cnt.isdigit() and int(cnt) >= 1, cnt)
code, mine = api("ayse", "GET", f"{C}/advances")
check("Çalışan yalnızca kendi avanslarını görür", code == 200 and all(a["employeeId"] == AYSE for a in mine), code)

# ====================================================================== parametre yokken hesaplama yapılmaz (60)
code, p0 = api("admin", "GET", f"{C}/payroll/parameters/{YEAR}?month=4")
check("Parametre: girilmemiş yıl için değer varsayılmaz (configured=false, null)", code == 200 and p0["configured"] is False
      and p0["minimumWageGross"] is None and p0["brackets"] is None and p0["rows"] == [], p0)
code, per = api("admin", "POST", f"{C}/payroll/periods", {"year": YEAR, "month": 4})
pid = per["id"]
code, plist = api("admin", "GET", f"{C}/payroll/periods?year={YEAR}")
check("Dönem listesi: parametre girilmedi işareti", code == 200 and any(x["id"] == pid and x["parametersConfigured"] is False for x in plist), plist)
code, r = api("admin", "POST", f"{C}/payroll/periods/{pid}/calculate")
check("Parametre yoksa hesaplama 409 payroll_parameters_missing (yıl + ipucu)", code == 409 and r.get("code") == "payroll_parameters_missing"
      and str(YEAR) in r.get("message", "") and "Parametreler" in r.get("message", ""), (code, r))
code, r = api("admin", "POST", f"{C}/payroll/preview", {"year": YEAR, "month": 4, "monthlyGross": 50000, "unpaidDays": 0, "overtimeHours": 0,
                                                     "additions": 0, "deductions": 0, "priorCumulativeTaxBase": 0})
check("Parametre yoksa simülasyon da 409", code == 409 and r.get("code") == "payroll_parameters_missing", (code, r))
code, r = api("admin", "PUT", f"{C}/payroll/parameters/{YEAR}", {"validFromMonth": 1, "minimumWageGross": 33030, "brackets": TEST_BRACKETS})
fields = {e["field"] for e in r.get("errors", [])} if isinstance(r, dict) else set()
check("Parametre: eksik zorunlu alanlar koddan doldurulmaz (400, alan listesi)", code == 400 and r.get("code") == "invalid_parameters"
      and {"sgkEmployeeRate", "sgkEmployerRate", "stampTaxRate", "sgkCeilingMultiplier"} <= fields, (code, r))
code, pv = api("admin", "POST", f"{C}/payroll/parameters/{YEAR}/preview", {"parameters": TEST_PARAMS, "monthlyGross": 50000, "month": 4})
check("Parametre: kaydetmeden örnek hesap", code == 200 and pv["sample"]["gross"] == 50000 and 0 < pv["sample"]["net"] < 50000
      and pv["sgkCeiling"] == 297270 and pv["minimumWageNetCalculated"] > 0, (code, pv))
check("Örnek hesap kayıt yazmaz", psql(f"""SELECT count(*) FROM compensation_payroll_parameters WHERE "Year" = {YEAR}""") == "0")
code, _ = api("ayse", "POST", f"{C}/payroll/parameters/{YEAR}/preview", {"parameters": TEST_PARAMS, "monthlyGross": 50000})
check("Örnek hesap: çalışan yapamaz", code == 403, code)
code, r = api("admin", "PUT", f"{C}/payroll/parameters/{YEAR}", TEST_PARAMS)
check("Parametre: bordro yetkilisi yılın değerlerini girer (girildi: kim/ne zaman/kaynak)", code == 200 and r["configured"] is True
      and r["minimumWageGross"] == 33030 and r["seed"] is False and r["updatedBy"] and r["source"].startswith("TEST"), (code, r))
code, ys = api("admin", "GET", f"{C}/payroll/parameters")
check("Parametre yılları listesi", code == 200 and any(y["year"] == YEAR and y["months"] == [1] for y in ys), ys)

# ====================================================================== dönem: taksit + dosyalar
code, calc = api("admin", "POST", f"{C}/payroll/periods/{pid}/calculate")
check("Dönem hesaplandı", code == 200 and calc["employeeCount"] >= 1, (code, calc))
code, adjs = api("admin", "GET", f"{C}/payroll/periods/{pid}/adjustments")
inst = [a for a in adjs if a.get("sourceId") == adv["id"]]
check("Avans taksidi kesinti olarak otomatik eklendi", len(inst) == 1 and inst[0]["amount"] == 1000 and inst[0]["kind"] == "Deduction", adjs)
api("admin", "POST", f"{C}/payroll/periods/{pid}/calculate")
code, adjs = api("admin", "GET", f"{C}/payroll/periods/{pid}/adjustments")
check("Yeniden hesaplamada taksit çiftlenmez", len([a for a in adjs if a.get("sourceId") == adv["id"]]) == 1, adjs)
code, r = api("admin", "POST", f"{C}/payroll/periods/{pid}/exports", {"kind": "Bank"})
check("Kapanmamış dönemde dosya üretilmez", code == 400 and r.get("code") == "period_not_closed", (code, r))
# Görevler ayrılığı: hesaplayan (admin) değil, başka bir bordro yetkilisi (İK) kapatır.
code, r = api("ik", "POST", f"{C}/payroll/periods/{pid}/close")
check("Dönem İK tarafından kapatıldı (görevler ayrılığı)", code == 200, (code, r))
code, mine = api("ayse", "GET", f"{C}/advances")
a1 = next(a for a in mine if a["id"] == adv["id"])
check("Kapanışta taksit ödendi sayıldı", a1["repaidAmount"] == 1000 and a1["remaining"] == 2000, a1)

code, sgk = api("admin", "POST", f"{C}/payroll/periods/{pid}/exports", {"kind": "SgkAphb"})
check("SGK APHB dosyası üretildi", code == 200 and sgk["rowCount"] >= 1, (code, sgk))
code, body = download("admin", f"{C}/payroll/exports/{sgk['id']}/download")
xml = body.decode("utf-8", "replace")
check("SGK XML: Ayşe'nin TCKN'si ve prim günü", code == 200 and f"<TCKIMLIKNO>{TCKN}</TCKIMLIKNO>" in xml and "<PRIMGUN>" in xml and "AYLIKPRIMHIZMETBELGESI" in xml, xml[:300])
cipher = psql(f"""SELECT encode("Cipher", 'escape') FROM compensation_payroll_exports WHERE "Id" = '{sgk['id']}'""")
check("Dosya veritabanında şifreli (TCKN düz metin değil)", TCKN not in cipher and len(cipher) > 50, cipher[:60])
code, bank = api("admin", "POST", f"{C}/payroll/periods/{pid}/exports", {"kind": "Bank"})
check("Banka dosyası (tek indirme)", code == 200 and bank["singleUse"] is True, (code, bank))
code, body = download("admin", f"{C}/payroll/exports/{bank['id']}/download")
csv = body.decode("utf-8-sig", "replace")
check("Banka CSV: IBAN ve net tutar", code == 200 and IBAN in csv and "TOPLAM" in csv, csv[:200])
code, _ = download("admin", f"{C}/payroll/exports/{bank['id']}/download")
check("Banka dosyası ikinci kez indirilemez (410)", code == 410, code)
code, acc = api("admin", "POST", f"{C}/payroll/periods/{pid}/exports", {"kind": "Accounting", "format": "logo"})
code, body = download("admin", f"{C}/payroll/exports/{acc['id']}/download")
csv = body.decode("utf-8-sig", "replace")
check("Muhasebe fişi: hesap kodları, masraf merkezi, kişi verisi yok", code == 200 and "770.01" in csv and "335" in csv and "Mühendislik" in csv and TCKN not in csv and "Ayşe" not in csv and IBAN not in csv, csv[:300])
check("Muhasebe fişi uyarısız (dengeli)", acc["warnings"] == [], acc["warnings"])
code, _ = api("ayse", "POST", f"{C}/payroll/periods/{pid}/exports", {"kind": "Bank"})
check("Çalışan dosya üretemez", code == 403, code)
acc_log = psql(f"""SELECT count(*) FROM audit_log WHERE "EntityType" = 'PayrollExport' AND "Action" = 'Exported' AND "EntityId" IN ('{sgk['id']}','{bank['id']}')""")
check("SGK/banka indirmeleri erişim kaydında", acc_log == "2", acc_log)
psql(f"""UPDATE compensation_payroll_exports SET "ExpiresAt" = now() - interval '1 minute' WHERE "Id" = '{sgk['id']}'""")
code, _ = download("admin", f"{C}/payroll/exports/{sgk['id']}/download")
check("Süresi dolan dosya indirilemez", code == 410, code)
purged = ""
for _ in range(45):
    purged = psql(f"""SELECT "Cipher" IS NULL FROM compensation_payroll_exports WHERE "Id" = '{sgk['id']}'""")
    if purged == "t":
        break
    time.sleep(2)
check("Süresi dolan dosyanın içeriği silindi (temizlik işi)", purged == "t", purged)
# ====================================================================== Dalga 8: parametreler (60)
code, py = api("admin", "GET", f"{C}/payroll/parameters/{YEAR}")
check("Parametreler: yılın satırları (girildi; doğrulandı bayrağı yok)", code == 200 and [x["validFromMonth"] for x in py["rows"]] == [1]
      and py["rows"][0]["seed"] is False and py["rows"][0]["verified"] is True, py.get("rows"))
br = TEST_BRACKETS
july = dict(TEST_PARAMS, validFromMonth=7, minimumWageGross=40000, severanceCeilingH2=90000, verified=False, source="TEST Temmuz")
code, r = api("admin", "PUT", f"{C}/payroll/parameters/{YEAR}", july)
check("Parametre: Temmuz yürürlük satırı kaydedildi (eski 'verified' girdisi yok sayılır)", code == 200 and r["validFromMonth"] == 7 and r["minimumWageGross"] == 40000 and r["verified"] is True, (code, r))
code, m3 = api("admin", "GET", f"{C}/payroll/parameters/{YEAR}?month=3")
code, m8 = api("admin", "GET", f"{C}/payroll/parameters/{YEAR}?month=8")
check("Parametre: dönem ayına göre seçim (Mart Ocak satırı, Ağustos Temmuz satırı)", m3["minimumWageGross"] == 33030 and m8["minimumWageGross"] == 40000, (m3["minimumWageGross"], m8["minimumWageGross"]))
code, _ = api("ayse", "PUT", f"{C}/payroll/parameters/{YEAR}", {"minimumWageGross": 1, "sgkEmployerRate": 0.2, "employerIncentivePoints": 0, "stampTaxRate": 0.007, "sgkCeilingMultiplier": 9, "brackets": br})
check("Parametre: çalışan değiştiremez", code == 403, code)
code, _ = api("admin", "PUT", f"{C}/payroll/parameters/{YEAR}", {"validFromMonth": 13, "minimumWageGross": 1, "sgkEmployerRate": 0.2, "employerIncentivePoints": 0, "stampTaxRate": 0.007, "sgkCeilingMultiplier": 9, "brackets": br})
check("Parametre: geçersiz yürürlük ayı reddedilir", code == 400, code)
aud = psql(f"""SELECT count(*) FROM audit_log WHERE "EntityType" = 'PayrollParameterSet' AND "EntityId" = '{YEAR}-07' AND "Changes" ? 'after'""")
check("Parametre değişikliği denetim kaydında (önce/sonra)", aud.isdigit() and int(aud) >= 1, aud)
code, _ = api("admin", "DELETE", f"{C}/payroll/parameters/{YEAR}?validFromMonth=7")
code, m8 = api("admin", "GET", f"{C}/payroll/parameters/{YEAR}?month=8")
check("Parametre: satır silinince önceki yürürlük satırı geçerli (koddan varsayılan yok)", code == 200 and m8["minimumWageGross"] == 33030 and m8["validFromMonth"] == 1, m8)

# ====================================================================== Dalga 8: SGK ayarları, meslek kodu, APHB (58)
code, settings0 = api("admin", "GET", f"{C}/payroll/settings")
check("Bordro ayarları okunur (varsayılan eksik gün kodu: ücretsiz izin 21)", code == 200 and any(m["leaveType"] == "Unpaid" and m["code"] == "21" for m in settings0["sgk"]["missingDayCodes"]), settings0.get("sgk"))
code, _ = api("ayse", "GET", f"{C}/payroll/settings")
check("Bordro ayarları: çalışan göremez", code == 403, code)
code, _ = api("admin", "PUT", f"{C}/payroll/sgk/employees/{AYSE}", {"occupationCode": "25120", "sgdp": False})
check("Meslek kodu biçimi denetlenir", code == 400, code)
code, r = api("admin", "PUT", f"{C}/payroll/sgk/employees/{AYSE}", {"occupationCode": "2512.01", "documentType": None, "lawNo": None, "sgdp": False})
check("Meslek kodu kaydedildi", code == 200 and r["occupationCode"] == "2512.01", (code, r))
code, val = api("admin", "GET", f"{C}/payroll/periods/{pid}/sgk/validation")
ayse_issues = [i for i in val.get("issues", []) if i["employeeId"] == AYSE] if code == 200 else []
check("APHB doğrulaması: Ayşe dosyada, meslek kodu/TCKN uyarısı yok", code == 200 and val["included"] >= 1
      and not any(i["code"] in ("tckn", "occupation") for i in ayse_issues) and "10000000146" not in json.dumps(val), (code, ayse_issues))
code, _ = api("ayse", "GET", f"{C}/payroll/periods/{pid}/sgk/validation")
check("APHB doğrulaması: çalışan göremez", code == 403, code)
code, txt = api("admin", "POST", f"{C}/payroll/periods/{pid}/exports", {"kind": "SgkAphb", "format": "txt"})
check("APHB TXT dosyası üretildi (özetle)", code == 200 and txt["fileName"].endswith(".txt") and len(txt.get("contentSha256") or "") == 64, (code, txt))
code, body = download("admin", f"{C}/payroll/exports/{txt['id']}/download")
lines = [ln for ln in body.decode("utf-8", "replace").split("\r\n") if TCKN in ln]
check("APHB TXT: 14 sütun, meslek kodu ve özet tutarlı", code == 200 and len(lines) == 1 and len(lines[0].split(";")) == 14 and lines[0].endswith(";2512.01")
      and hashlib.sha256(body).hexdigest() == txt["contentSha256"], lines[:1])
code, xml2 = api("admin", "POST", f"{C}/payroll/periods/{pid}/exports", {"kind": "SgkAphb", "format": "xml"})
code, body = download("admin", f"{C}/payroll/exports/{xml2['id']}/download")
check("APHB XML: meslek kodu ve belge grubu", code == 200 and "<MESLEKKODU>2512.01</MESLEKKODU>" in body.decode("utf-8") and "<BELGE " in body.decode("utf-8"), code)

# ====================================================================== Dalga 8: banka örnek şablonu (62) ve muhasebe eşlemesi (64)
code, bb = api("admin", "POST", f"{C}/payroll/periods/{pid}/exports", {"kind": "Bank", "format": "ornek-b", "payDate": f"{YEAR}-05-01"})
check("Banka örnek şablon B üretildi", code == 200 and bb["fileName"].endswith(".txt"), (code, bb))
code, body = download("admin", f"{C}/payroll/exports/{bb['id']}/download")
blines = body.decode("latin-1").split("\r\n")
check("Banka TXT: başlık/detay/toplam kayıtları, IBAN, özet tutarlı", code == 200 and blines[0].startswith("H") and any(ln.startswith("D") and IBAN in ln for ln in blines)
      and any(ln.startswith("T") for ln in blines) and hashlib.sha256(body).hexdigest() == bb["contentSha256"], blines[:2])
hlog = psql(f"""SELECT count(*) FROM audit_log WHERE "EntityType" = 'PayrollExport' AND "EntityId" = '{bb['id']}' AND "Changes"->>'sha256' = '{bb['contentSha256']}'""")
check("Banka dosyası özeti denetim kaydında", hlog.isdigit() and int(hlog) >= 2, hlog)
code, _ = api("admin", "PUT", f"{C}/payroll/settings", {"costCenters": {"Mühendislik": "MM-TEST"}})
code, acc2 = api("admin", "POST", f"{C}/payroll/periods/{pid}/exports", {"kind": "Accounting", "format": "generic"})
code, body = download("admin", f"{C}/payroll/exports/{acc2['id']}/download")
check("Muhasebe: masraf merkezi kodu eşlendi, fiş dengeli", code == 200 and "MM-TEST" in body.decode("utf-8-sig") and acc2["warnings"] == [], acc2.get("warnings"))
api("admin", "PUT", f"{C}/payroll/settings", {"sgk": settings0["sgk"], "accounts": settings0["accounts"], "costCenters": settings0["costCenters"], "bank": settings0["bank"]})

# ====================================================================== Dalga 8: e-bordro (59)
code, pub = api("admin", "POST", f"{C}/payroll/periods/{pid}/e-payslips/publish")
check("e-Bordro yayımlandı", code == 200 and pub["created"] + pub["republished"] + pub["unchanged"] >= 1, (code, pub))
code, mine = api("ayse", "GET", f"{C}/payslips/me")
slip = next((x for x in mine if x["year"] == YEAR and x["month"] == 4), None)
code, ep = api("ayse", "GET", f"{C}/payslips/{slip['id']}/e-payslip") if slip else (0, {})
check("e-Bordro: çalışan görür, içerik özeti tutarlı, onaylayabilir", code == 200 and ep["integrity"] == "ok" and ep["canAcknowledge"] is True, ep)
code, ep2 = api("admin", "GET", f"{C}/payslips/{slip['id']}/e-payslip")
opened = psql(f"""SELECT "OpenCount" FROM compensation_payslip_deliveries WHERE "PayslipId" = '{slip['id']}'""")
check("e-Bordro: çalışanın açması okundu sayılır, İK'nınki sayılmaz", ep.get("firstOpenedAt") is not None and opened == "1", (ep.get("firstOpenedAt"), opened))
code, _ = api("mehmet", "POST", f"{C}/payslips/{slip['id']}/acknowledge")
check("e-Bordro: başkası teslim alamaz", code == 404, code)
code, ack = api("ayse", "POST", f"{C}/payslips/{slip['id']}/acknowledge")
check("e-Bordro: Okudum, teslim aldım", code == 200 and ack["contentSha256"] == ep["currentSha256"], (code, ack))
code, r = api("ayse", "POST", f"{C}/payslips/{slip['id']}/acknowledge")
check("e-Bordro: ikinci onay reddedilir", code == 409, code)
code, st = api("admin", "GET", f"{C}/payroll/periods/{pid}/e-payslips")
check("e-Bordro: İK teslim durumunu görür", code == 200 and st["acknowledged"] >= 1, st)
nb = psql(f"""SELECT count(*) FROM notification_messages WHERE "TemplateCode" = 'compensation.epayslip' AND "RecipientEmployeeId" = '{AYSE}'
              AND "Subject" = 'e-Bordro: {YEAR}/04' AND "Body" NOT LIKE '%{slip['net']}%'""")
check("e-Bordro bildirimi gitti (tutar yazılmadan)", nb.isdigit() and int(nb) >= 1, nb)

# ====================================================================== Dalga 8: fark bordrosu (63)
code, recs = api("admin", "GET", f"{C}/records?employeeId={AYSE}")
cur = next((x for x in recs if x["effectiveTo"] is None), None)
code, newrec = api("admin", "POST", f"{C}/records", {"employeeId": AYSE, "baseSalary": round(cur["baseSalary"] * 1.1, 2), "currency": cur["currency"],
                                                    "grade": cur.get("grade"), "reason": "AnnualIncrease", "effectiveFrom": f"{YEAR}-04-01", "note": f"TEST retro {YEAR}"})
check("Fark: geriye dönük zam kaydı (kapanmış Nisan dönemine)", code == 201, (code, newrec))
code, cands = api("admin", "GET", f"{C}/payroll/retro/candidates?employeeId={AYSE}")
cand = next((c for c in cands if c["sourcePeriodId"] == pid), None) if code == 200 else None
check("Fark: aday listede, brüt fark pozitif", cand is not None and cand["diffGross"] > 0 and cand["applicable"] and cand["label"] == f"Fark: {YEAR}/04", cands)
code, per5 = api("admin", "POST", f"{C}/payroll/periods", {"year": YEAR, "month": 5})
code, r = api("admin", "POST", f"{C}/payroll/retro/apply", {"targetPeriodId": pid, "items": [{"employeeId": AYSE, "sourcePeriodId": pid}]})
check("Fark: kapanmış döneme eklenemez", code == 409, (code, r))
code, r = api("ayse", "POST", f"{C}/payroll/retro/apply", {"targetPeriodId": per5["id"], "items": [{"employeeId": AYSE, "sourcePeriodId": pid}]})
check("Fark: çalışan onaylayamaz", code == 403, code)
code, r = api("admin", "POST", f"{C}/payroll/retro/apply", {"targetPeriodId": per5["id"], "items": [{"employeeId": AYSE, "sourcePeriodId": pid}]})
check("Fark: İK onayıyla hedef döneme eklendi", code == 200 and r["applied"] == 1, (code, r))
code, adjs5 = api("admin", "GET", f"{C}/payroll/periods/{per5['id']}/adjustments")
fark = [a for a in adjs5 if a["description"] == f"Fark: {YEAR}/04" and a["employeeId"] == AYSE]
check("Fark: 'Fark: YYYY/AA' ek ödemesi (onaylayan kayıtlı)", len(fark) == 1 and abs(fark[0]["amount"] - cand["diffGross"]) < 0.01 and fark[0].get("createdBy"), fark)
code, _ = api("admin", "POST", f"{C}/payroll/periods/{per5['id']}/calculate")
code, slips5 = api("admin", "GET", f"{C}/payroll/periods/{per5['id']}/payslips")
s5 = next((x for x in slips5 if x["employeeId"] == AYSE), None)
check("Fark: hedef dönem pusulasında ek ödeme olarak", s5 is not None and s5["additions"] >= cand["diffGross"] - 0.01, s5 and s5["additions"])
st_old = psql(f"""SELECT "Status" FROM compensation_payroll_periods WHERE "Id" = '{pid}'""")
check("Fark: kaynak (kapanmış) dönem açılmadı", st_old == "Closed", st_old)
code, cands2 = api("admin", "GET", f"{C}/payroll/retro/candidates?employeeId={AYSE}")
check("Fark: ödenen fark yeniden önerilmez", code == 200 and not any(c["sourcePeriodId"] == pid for c in cands2), cands2)
code, cl = api("admin", "POST", f"{C}/payroll/periods/{per5['id']}/close")
check("Fark: görevler ayrılığı — hazırlayan/onaylayan kapatamaz", code == 409 and cl.get("code") == "sod_same_user", (code, cl))
code, _ = api("admin", "DELETE", f"{C}/payroll/periods/{per5['id']}")
code, cands3 = api("admin", "GET", f"{C}/payroll/retro/candidates?employeeId={AYSE}")
check("Fark: hedef dönem silinince fark yeniden önerilir", code == 200 and any(c["sourcePeriodId"] == pid for c in cands3), cands3)
psql(f"""DELETE FROM compensation_records WHERE "Note" = 'TEST retro {YEAR}';
         UPDATE compensation_records SET "EffectiveTo" = NULL WHERE "EmployeeId" = '{AYSE}' AND "EffectiveTo" = '{YEAR}-03-31';""")

# ====================================================================== Dalga 8: kıdem ve ihbar (61)
sev_body = {"employeeId": AYSE, "lastWorkingDay": f"{YEAR}-12-31", "reason": "Termination", "otherBenefitsMonthly": 1000, "unusedLeaveDays": 5}
code, pv = api("admin", "POST", f"{C}/severance/preview", sev_body)
check("Kıdem/ihbar: önizleme (kıdem, ihbar GV'si, giydirme)", code == 200 and pv["result"]["severanceEligible"] and pv["result"]["severanceGross"] > 0
      and pv["result"]["noticeGross"] > 0 and pv["result"]["noticeIncomeTax"] > 0 and pv["input"]["otherBenefitsMonthly"] == 1000, (code, pv.get("result")))
code, _ = api("ayse", "POST", f"{C}/severance/preview", sev_body)
check("Kıdem/ihbar: çalışan hesaplayamaz", code == 403, code)
# İhbar GV'si kümülatif matrahı: yalnızca kapanmış dönemler (açık Mayıs dönemi silindi) + devreden matrah.
closed_base = psql(f"""SELECT coalesce(sum(s."TaxBase"), 0) FROM compensation_payslips s JOIN compensation_payroll_periods p ON p."Id" = s."PeriodId"
                       WHERE s."EmployeeId" = '{AYSE}' AND s."Year" = {YEAR} AND p."Status" = 'Closed'""")
check("Kıdem/ihbar: kümülatif matrah yalnızca kapanmış dönemlerden", abs(pv["sources"]["priorCumulativeTaxBase"] - float(closed_base or 0)) < 0.01,
      (pv["sources"]["priorCumulativeTaxBase"], closed_base))
code, _ = api("ayse", "PUT", f"{C}/payroll/opening-tax-bases/{AYSE}/{YEAR}", {"amount": 5000000, "note": "TEST devreden"})
check("Devreden matrah: çalışan giremez", code == 403, code)
code, ob = api("admin", "PUT", f"{C}/payroll/opening-tax-bases/{AYSE}/{YEAR}", {"amount": 5000000, "note": "TEST devreden"})
check("Devreden matrah: İK girer", code == 200 and ob["amount"] == 5000000, (code, ob))
code, pv2 = api("admin", "POST", f"{C}/severance/preview", sev_body)
check("Devreden matrah ihbar GV'sine girer (üst dilim)", code == 200 and pv2["sources"]["openingTaxBase"] == 5000000
      and pv2["result"]["noticeIncomeTax"] > pv["result"]["noticeIncomeTax"], (code, pv2.get("sources"), pv2.get("result", {}).get("noticeIncomeTax")))
code, obl = api("admin", "GET", f"{C}/payroll/opening-tax-bases?year={YEAR}")
check("Devreden matrah listesi", code == 200 and any(x["employeeId"] == AYSE for x in obl), (code, obl))
olog = psql(f"""SELECT count(*) FROM audit_log WHERE "EntityType" = 'OpeningTaxBase' AND "EntityId" = '{AYSE}' AND "Action" = 'Created'""")
check("Devreden matrah denetim kaydında", olog.isdigit() and int(olog) >= 1, olog)
code, _ = api("admin", "PUT", f"{C}/payroll/opening-tax-bases/{AYSE}/{YEAR}", {"amount": 0})
check("Devreden matrah: 0 girilince silinir", code == 204, code)
code, sv = api("admin", "POST", f"{C}/severance", sev_body)
check("Kıdem/ihbar: taslak kaydedildi", code == 200 and sv["status"] == "Draft", (code, sv))
code, _ = api("admin", "GET", f"{C}/severance/{sv['id']}/document")
check("Kıdem/ihbar: onaysız ibraname üretilmez", code == 409, code)
code, d = api("admin", "POST", f"{C}/severance/{sv['id']}/decide", {"approve": True})
check("Kıdem/ihbar: hazırlayan onaylayamaz (dört göz)", code == 409 and d.get("code") == "sod_same_user", (code, d))
code, d = api("ik", "POST", f"{C}/severance/{sv['id']}/decide", {"approve": True})
check("Kıdem/ihbar: başka bordro yetkilisi onayladı", code == 200 and d["status"] == "Approved", (code, d))
code, doc = api("admin", "GET", f"{C}/severance/{sv['id']}/document")
check("Kıdem/ihbar: ibraname belgesi (yer tutucular dolu)", code == 200 and "İBRANAME" in doc["html"] and "{{" not in doc["html"] and "TL" in doc["html"], (code, doc.get("html", "")[:200]))
dl_log = psql(f"""SELECT count(*) FROM audit_log WHERE "EntityType" = 'SeveranceCalc' AND "EntityId" = '{sv['id']}' AND "Action" = 'Exported'""")
check("İbraname üretimi denetim kaydında", dl_log == "1", dl_log)

# ====================================================================== Dalga 8: bant uyumu (65)
code, cov = api("admin", "GET", f"{C}/bands/coverage")
check("Bant kapsama raporu (5'ten küçük grup gizli)", code == 200 and cov["minGroup"] == 5 and all(g["hidden"] or g["count"] >= 5 for g in cov["byDepartment"]), (code, cov))
code, _ = api("ayse", "GET", f"{C}/bands/coverage")
check("Bant kapsama: çalışan göremez", code == 403, code)
code, cr = api("admin", "GET", f"{C}/bands/compa-ratios")
check("Compa-ratio listesi", code == 200 and any(x["employeeId"] == AYSE for x in cr), code)
cr_log = psql("""SELECT count(*) FROM audit_log WHERE "EntityType" = 'CompensationRecord' AND "EntityId" = 'compa-ratio' AND "OccurredAt" > now() - interval '5 minutes'""")
check("Compa-ratio görüntülemesi erişim kaydında", cr_log.isdigit() and int(cr_log) >= 1, cr_log)
psql(f"""DELETE FROM compensation_employee_sgk WHERE "EmployeeId" = '{AYSE}' AND "OccupationCode" = '2512.01' AND "UpdatedAt" > now() - interval '1 hour'""")

code, r = api("admin", "POST", f"{C}/payroll/periods/{pid}/reopen", {"reason": "TEST yeniden açma gerekçesi"})
code, mine = api("ayse", "GET", f"{C}/advances")
a1 = next(a for a in mine if a["id"] == adv["id"])
check("Dönem yeniden açılınca taksit geri alınır", a1["repaidAmount"] == 0 and a1["status"] == "Approved", a1)
code, r = api("ayse", "POST", f"{C}/payslips/{slip['id']}/acknowledge") if slip else (0, {})
check("e-Bordro: yeniden açılan dönemde teslim alınamaz", code == 409 and r.get("code") == "period_not_closed", (code, r))
code, r = api("admin", "DELETE", f"{C}/payroll/periods/{pid}")
check("Teslim alınmış e-bordrosu olan dönem silinmez", code == 409 and r.get("code") == "epayslips_acknowledged", (code, r))

# ====================================================================== Y13 yan haklar
today = dt.date.today()
code, _ = api("admin", "PUT", f"{C}/benefits/plan", {"year": YEAR, "budgetPerEmployee": 15000, "windowStart": (today - dt.timedelta(days=1)).isoformat(), "windowEnd": (today + dt.timedelta(days=30)).isoformat()})
check("Yan hak planı", code == 200, code)
ids = {}
for name, cat, cost in (("TEST Yemek kartı", "Meal", 6000), ("TEST Özel sağlık A", "Health", 10000), ("TEST Özel sağlık B", "Health", 8000)):
    code, o = api("admin", "POST", f"{C}/benefits/{YEAR}/options", {"name": name, "category": cat, "annualCost": cost, "description": None, "isActive": True})
    ids[name] = o["id"]
code, _ = api("ayse", "POST", f"{C}/benefits/{YEAR}/options", {"name": "x", "category": "Meal", "annualCost": 1, "isActive": True})
check("Çalışan seçenek ekleyemez", code == 403, code)
code, r = api("ayse", "PUT", f"{C}/benefits/{YEAR}/election", {"optionIds": [ids["TEST Yemek kartı"], ids["TEST Özel sağlık A"]]})
check("Bütçeyi aşan seçim reddedilir", code == 400 and r.get("code") == "over_budget", (code, r))
code, r = api("ayse", "PUT", f"{C}/benefits/{YEAR}/election", {"optionIds": [ids["TEST Özel sağlık A"], ids["TEST Özel sağlık B"]]})
check("Aynı kategoriden iki seçenek reddedilir", code == 400, code)
code, r = api("ayse", "PUT", f"{C}/benefits/{YEAR}/election", {"optionIds": [ids["TEST Yemek kartı"], ids["TEST Özel sağlık B"]]})
check("Bütçe içinde seçim kaydedilir", code == 200 and r["total"] == 14000 and r["remaining"] == 1000, (code, r))
code, st = api("admin", "GET", f"{C}/benefits/{YEAR}")
check("İK özeti: seçim sayıları", code == 200 and st["summary"]["elections"] >= 1, st.get("summary"))
code, st = api("ayse", "GET", f"{C}/benefits/{YEAR}")
check("Çalışan özet göremez, kendi seçimini görür", st["summary"] is None and set(st["election"]["optionIds"]) == {ids["TEST Yemek kartı"], ids["TEST Özel sağlık B"]}, st)

# ====================================================================== Y21 zam dönemi
# Dönem yılı makul aralıkta olmalı (bu yıl-1 .. bu yıl+2); yürürlük tarihi test yılında kalır.
CYCLE_YEAR = dt.date.today().year + 1
code, _ = api("admin", "POST", f"{C}/raise-cycles", {"name": "TEST zam 1900", "year": 1900, "budgetPercent": 10, "effectiveDate": "1900-06-01"})
check("Zam dönemi: makul olmayan yıl reddedilir", code == 400, code)
code, tmp = api("admin", "POST", f"{C}/raise-cycles", {"name": "TEST zam silinecek", "year": CYCLE_YEAR, "budgetPercent": 5, "effectiveDate": f"{YEAR}-06-01"})
code, _ = api("admin", "DELETE", f"{C}/raise-cycles/{tmp['id']}")
check("Öneri girilmemiş taslak dönem silinir", code == 204, code)
code, cyc = api("admin", "POST", f"{C}/raise-cycles", {"name": f"TEST zam {YEAR}", "year": CYCLE_YEAR, "budgetPercent": 10, "effectiveDate": f"{YEAR}-06-01"})
cid = cyc["id"]
# Ücret bandı düzenleme/silme: açık zam dönemi bandın yılını okuyorsa silinemez (409).
code, band = api("admin", "POST", f"{C}/bands", {"grade": "TEST-Z", "title": "TEST", "minAmount": 10000, "midAmount": 15000, "maxAmount": 20000, "currency": "TRY", "year": CYCLE_YEAR})
code, _ = api("admin", "PUT", f"{C}/bands/{band['id']}", {"grade": "TEST-Z", "title": "TEST", "minAmount": 16000, "midAmount": 15000, "maxAmount": 20000, "currency": "TRY", "year": CYCLE_YEAR})
check("Bant düzenleme: alt ≤ orta ≤ üst doğrulanır", code == 400, code)
code, b2 = api("admin", "PUT", f"{C}/bands/{band['id']}", {"grade": "TEST-Z", "title": "TEST 2", "minAmount": 11000, "midAmount": 15000, "maxAmount": 21000, "currency": "TRY", "year": CYCLE_YEAR})
check("Bant düzenlenir", code == 200 and b2["maxAmount"] == 21000, (code, b2))
code, _ = api("admin", "DELETE", f"{C}/bands/{band['id']}")
check("Uygulanmamış zam döneminin okuduğu bant silinemez", code == 409, code)
code, b3 = api("admin", "POST", f"{C}/bands", {"grade": "TEST-Y", "minAmount": 1, "midAmount": 2, "maxAmount": 3, "currency": "TRY", "year": YEAR + 5})
code, _ = api("admin", "DELETE", f"{C}/bands/{b3['id']}")
check("Kullanılmayan bant silinir", code == 204, code)
code, _ = api("mehmet", "GET", f"{C}/raise-cycles/{cid}/worksheet")
check("Taslak dönem yöneticiye kapalı", code == 404, code)
api("admin", "POST", f"{C}/raise-cycles/{cid}/status", {"status": "Open"})
code, ws = api("mehmet", "GET", f"{C}/raise-cycles/{cid}/worksheet")
names = [r["employeeId"] for r in ws.get("rows", [])] if code == 200 else []
check("Yönetici yalnızca kendi bölümünü görür (kendisi hariç)", code == 200 and AYSE in names and ws["rows"] and all(r["department"] == "Mühendislik" for r in ws["rows"]), (code, ws.get("rows") if code == 200 else ws))
code, _ = api("ayse", "GET", f"{C}/raise-cycles")
check("Çalışan zam dönemlerini göremez", code == 403, code)
code, r = api("mehmet", "PUT", f"{C}/raise-cycles/{cid}/proposals", {"employeeId": AYSE, "proposedPercent": 12.5, "note": "TEST"})
check("Yönetici öneri yapar", code == 200 and r["status"] == "Proposed", (code, r))
_, me = api("mehmet", "GET", "/api/employee/employees/me")
code, _ = api("mehmet", "PUT", f"{C}/raise-cycles/{cid}/proposals", {"employeeId": me["id"], "proposedPercent": 50})
check("Kendine zam önerilemez", code == 403, code)
sv = psql(f"""SELECT count(*) FROM audit_log WHERE "EntityType" = 'RaiseCycle' AND "EntityId" = '{cid}' AND "Action" = 'SensitiveViewed'""")
check("Ücret görüntülemesi erişim kaydında", sv.isdigit() and int(sv) >= 1, sv)
code, ws = api("admin", "GET", f"{C}/raise-cycles/{cid}/worksheet")
row = next(r for r in ws["rows"] if r["employeeId"] == AYSE)
code, d = api("admin", "POST", f"{C}/raise-cycles/{cid}/decide", {"proposalIds": [row["proposal"]["id"]], "approve": True})
check("İK öneriyi onaylar", code == 200 and d["decided"] == 1, (code, d))
code, sm = api("admin", "GET", f"{C}/raise-cycles/{cid}/summary")
eng = next((g for g in sm["groups"] if g["department"] == "Mühendislik"), {})
check("Özet: 5 kişiden az grup gizli", eng.get("hidden") is True and eng.get("avgPercent") is None and sm["overallAvg"] is None, sm)
code, ap = api("admin", "POST", f"{C}/raise-cycles/{cid}/apply")
check("Zam uygulandı", code == 200 and ap["applied"] == 1, (code, ap))
newsal = psql(f"""SELECT "BaseSalary" FROM compensation_records WHERE "EmployeeId" = '{AYSE}' AND "Note" = 'TEST zam {YEAR}'""")
check("Yeni ücret kaydı yürürlük tarihiyle", newsal and abs(float(newsal) - row["currentSalary"] * 1.125) < 0.02, (newsal, row["currentSalary"]))
code, _ = api("admin", "POST", f"{C}/raise-cycles/{cid}/apply")
check("Zam dönemi iki kez uygulanamaz", code == 409, code)
code, _ = api("admin", "DELETE", f"{C}/raise-cycles/{cid}")
check("Uygulanmış zam dönemi silinemez", code == 409, code)

# ====================================================================== G9 masraf politikası ve kur
code, _ = api("ayse", "PUT", f"{E}/expense-policy", {"limits": {}, "kmRate": 1, "perDiemDomestic": 1, "perDiemAbroad": 1, "perDiemAbroadCurrency": "EUR"})
check("Politikayı yalnızca İK değiştirir", code == 403, code)
code, _ = api("admin", "PUT", f"{E}/expense-policy", {"limits": {"Meal": {"perItem": 500, "monthly": 1000, "receiptAbove": 200}}, "kmRate": 10, "perDiemDomestic": 1200, "perDiemAbroad": 100, "perDiemAbroadCurrency": "EUR"})
check("Masraf politikası kaydedildi", code == 200, code)
wd = today - dt.timedelta(days=1)
while wd.weekday() >= 5:
    wd -= dt.timedelta(days=1)
sat = today - dt.timedelta(days=(today.weekday() - 5) % 7 or 7)
code, fx = api("ayse", "GET", f"{E}/expense-fx?currency=USD&date={wd.isoformat()}")
check("TCMB kuru (sahte TCMB)", code == 200 and fx["rate"] == 41.5 and fx["source"] == "TCMB", (code, fx))
code, fx = api("ayse", "GET", f"{E}/expense-fx?currency=USD&date={sat.isoformat()}")
check("Hafta sonu: önceki iş gününün kuru", code == 200 and fx["rateDate"] == (sat - dt.timedelta(days=1)).isoformat(), (code, fx, sat))
code, cl = api("ayse", "POST", f"{E}/expense-claims", {"employeeId": AYSE, "title": "TEST kur ve km", "currency": "TRY", "items": [
    {"category": "Travel", "amount": 0, "expenseDate": wd.isoformat(), "originalCurrency": "USD", "originalAmount": 100},
    {"category": "Mileage", "amount": 0, "expenseDate": wd.isoformat(), "km": 120},
]})
items = {i["category"]: i for i in cl.get("items", [])} if code in (200, 201) else {}
check("Yabancı para kalemi TCMB kuruyla TL'ye çevrildi", items.get("Travel", {}).get("amount") == 4150 and items["Travel"]["fxRate"] == 41.5, (code, cl))
check("Km masrafı km × km ücreti", items.get("Mileage", {}).get("amount") == 1200, items.get("Mileage"))
code, r = api("admin", "PUT", f"{E}/expense-fx", {"currency": "GBP", "date": wd.isoformat(), "rate": 55.5})
code, fx = api("ayse", "GET", f"{E}/expense-fx?currency=GBP&date={wd.isoformat()}")
check("İK'nın elle girdiği kur önceliklidir", code == 200 and fx["rate"] == 55.5 and fx["source"] == "Manual", (code, fx))
code, cl2 = api("ayse", "POST", f"{E}/expense-claims", {"employeeId": AYSE, "title": "TEST yemek limiti", "currency": "TRY", "items": [
    {"category": "Meal", "amount": 600, "expenseDate": today.isoformat()}]})
code, r = api("ayse", "POST", f"{E}/expense-claims/{cl2['id']}/submit", {})
check("Politika ihlali: kalem limiti ve fiş zorunluluğu", code == 400 and r.get("code") == "policy_violation" and len(r.get("violations", [])) >= 2, (code, r))
code, cl3 = api("ayse", "POST", f"{E}/expense-claims", {"employeeId": AYSE, "title": "TEST yemek uygun", "currency": "TRY", "items": [
    {"category": "Meal", "amount": 150, "expenseDate": today.isoformat()}]})
code, r = api("ayse", "POST", f"{E}/expense-claims/{cl3['id']}/submit", {})
check("Limit içindeki beyan onaya gider", code == 200 and r["status"] == "Submitted", (code, r))

# ====================================================================== Y12 seyahat
start = today + dt.timedelta(days=10)
while start.weekday() >= 5:
    start += dt.timedelta(days=1)
code, r = api("ayse", "POST", f"{E}/travel", {"destination": "TEST Ankara", "abroad": False, "startDate": start.isoformat(), "endDate": start.isoformat(),
                                               "purpose": "Müşteri", "transport": "Train", "needsAccommodation": False, "passportNumber": "U1234567"})
check("Yurt içi seyahatte pasaport istenmez", code == 400, code)
code, tr = api("ayse", "POST", f"{E}/travel", {"destination": "TEST Berlin", "abroad": True, "startDate": start.isoformat(), "endDate": (start + dt.timedelta(days=2)).isoformat(),
                                                "purpose": "Fuar", "transport": "Plane", "needsAccommodation": True, "advanceRequested": 5000, "passportNumber": "u1234567"})
check("Yurt dışı seyahat: harcırah 3 gün × 100 EUR, onay akışında", code == 200 and tr["perDiemTotal"] == 300 and tr["perDiemCurrency"] == "EUR" and tr["workflowRequestId"], (code, tr))
cipher = psql(f"""SELECT "PassportCipher" FROM expense_travel_requests WHERE "Id" = '{tr['id']}'""")
check("Pasaport no şifreli", cipher.startswith(("enc1:", "enc2:")) and "U1234567" not in cipher, cipher[:20])
code, pp = api("ayse", "GET", f"{E}/travel/{tr['id']}/passport")
check("Sahibi pasaport noyu görür", code == 200 and pp["passportNumber"] == "U1234567", (code, pp))
code, _ = api("mehmet", "GET", f"{E}/travel/{tr['id']}/passport")
check("Yönetici pasaport noyu göremez", code == 404, code)
code, lst = api("mehmet", "GET", f"{E}/travel")
check("Liste yanıtında pasaport no yok", all(x.get("passportNumber") is None for x in lst) if isinstance(lst, list) else True, lst)
code, wf = api("mehmet", "GET", f"{W}/{tr['workflowRequestId']}")
check("Seyahat akışı bölüm başında", code == 200 and wf["type"] == "Travel", (code, wf.get("type") if isinstance(wf, dict) else wf))
code, _ = api("mehmet", "POST", f"{W}/{tr['workflowRequestId']}/steps/{wf['steps'][0]['id']}/decide", {"decision": "Approved", "comment": "iyi yolculuklar"})
status = None
for _ in range(30):
    _, lst = api("ayse", "GET", f"{E}/travel")
    status = next((x["status"] for x in lst if x["id"] == tr["id"]), None)
    if status == "Approved":
        break
    time.sleep(1)
check("Onay akışı kararı seyahate işlendi", status == "Approved", status)
code, pd = api("ayse", "POST", f"{E}/travel/{tr['id']}/per-diem-claim")
check("Harcırah beyanı: 300 EUR × TCMB kuru", code == 200 and pd["amount"] == 300 * 48.25, (code, pd))
code, _ = api("ayse", "POST", f"{E}/travel/{tr['id']}/per-diem-claim")
check("Harcırah iki kez beyan edilemez", code == 409, code)
rev = psql(f"""SELECT count(*) FROM audit_log WHERE "EntityType" = 'TravelRequest' AND "Action" = 'Revealed' AND "EntityId" = '{AYSE}'""")
check("Pasaport açılışı erişim kaydında", rev.isdigit() and int(rev) >= 1, rev)
psql(f"""UPDATE expense_travel_requests SET "StartDate" = current_date - 12, "EndDate" = current_date - 10 WHERE "Id" = '{tr['id']}'""")
gone = ""
for _ in range(45):
    gone = psql(f"""SELECT "PassportCipher" IS NULL FROM expense_travel_requests WHERE "Id" = '{tr['id']}'""")
    if gone == "t":
        break
    time.sleep(2)
check("Biten seyahatin pasaport bilgisi 7 gün sonra silindi", gone == "t", gone)

# ====================================================================== Y27 fiş okuma (yerel OCR)
try:
    from PIL import Image, ImageDraw, ImageFont
    img = Image.new("RGB", (900, 520), "white")
    d = ImageDraw.Draw(img)
    font = ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf", 40)
    for i, line in enumerate(["ORNEK KAFE LTD", "VKN: 1234567890", "TARIH 03.10.2026", "KDV 12,73", "TOPLAM 140,00"]):
        d.text((40, 30 + i * 95), line, fill="black", font=font)
    buf = io.BytesIO()
    img.save(buf, format="PNG")
    boundary = "----hr360ocr"
    body = (f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"fis.png\"\r\nContent-Type: image/png\r\n\r\n").encode() + buf.getvalue() + f"\r\n--{boundary}--\r\n".encode()
    req = urllib.request.Request(BASE + f"{E}/expense-claims/ocr", data=body, method="POST",
                                 headers={"Authorization": "Bearer " + tok("ayse"), "Content-Type": f"multipart/form-data; boundary={boundary}"})
    try:
        with urllib.request.urlopen(req, timeout=60) as resp:
            code, ocr = resp.status, json.loads(resp.read())
    except urllib.error.HTTPError as e:
        code, ocr = e.code, e.read().decode()
    check("Fiş okuma: tutar, tarih ve VKN önerisi", code == 200 and ocr["amount"] == 140 and ocr["date"] == "2026-10-03" and ocr["taxNo"] == "1234567890", (code, ocr))
    check("Fiş okuma ham metni döndürmez", code == 200 and "text" not in ocr, ocr)
except ImportError:
    check("PIL kurulu (fiş testi için)", False, "pip install pillow")

# ---------------------------------------------------------------------- temizlik
api("ayse", "PUT", "/api/engagement/profile/me", {"iban": (orig_iban or {}).get("value") or "", "nationalId": (orig_tckn or {}).get("value") or ""})
cleanup()
psql(f"""DELETE FROM notification_messages WHERE "TemplateCode" IN ('compensation.advance','compensation.raise') AND "RecipientEmployeeId" = '{AYSE}'""")

print(f"\nFAILS: {len(FAIL)}")
for f in FAIL:
    print("  -", f)
sys.exit(1 if FAIL else 0)

"""Temiz kurulum + seed_demo.py sonrası duman denetimi (yalnızca okuma; kayıt bırakmaz).

CI "Temiz kurulum" iş akışı (.github/workflows/fresh-install.yml) install.sh ve seed_demo.py'den
sonra çalıştırır; canlı/demo ortamda da güvenle çalıştırılabilir (yalnızca GET istekleri).
Test kullanıcıları tests/credentials.json'dan (seed_demo.py üretir). Parola/jeton yazdırılmaz.

Kullanım: python3 tests/support/fresh_install_check.py      (HR360_BASE_URL, varsayılan http://localhost)
Çıktı: her denetim için ok/FAIL ve sonda "FAILS: n"; n > 0 ise çıkış kodu 1.
"""

import json
import sys
import urllib.error
import urllib.request
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import hr360_login as login  # noqa: E402

FAILS = 0


def check(name, cond, detail=""):
    global FAILS
    if cond:
        print(f"  ok    {name}")
    else:
        FAILS += 1
        print(f"  FAIL  {name}" + (f" — {detail}" if detail else ""))


def get(path, who=None):
    headers = {"Accept": "application/json"}
    if who:
        headers["Authorization"] = "Bearer " + login.token(who)
    req = urllib.request.Request(login.BASE_URL + path, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            code, txt = r.status, r.read().decode()
    except urllib.error.HTTPError as ex:
        code, txt = ex.code, ex.read().decode()
    except Exception as ex:  # noqa: BLE001  (bağlantı hatası denetimi başarısız sayar)
        return 0, str(ex)[:200]
    try:
        return code, (json.loads(txt) if txt else None)
    except ValueError:
        return code, txt


def items(res):
    if isinstance(res, dict):
        for k in ("items", "data", "results"):
            if isinstance(res.get(k), list):
                return res[k]
    return res if isinstance(res, list) else []


def main():
    print(f"Temiz kurulum denetimi: {login.BASE_URL}")
    code, _ = get("/api/governance/ethics/public/demo")
    check("anonim uç (gateway + governance) 200", code == 200, f"HTTP {code}")

    code, res = get("/api/tenant/tenants", "platform")
    check("platform yöneticisi: 'demo' şirketi var", code == 200 and any(t.get("slug") == "demo" for t in items(res)), f"HTTP {code}")

    code, res = get("/api/employee/employees?pageSize=50", "admin")
    emails = {e.get("email") for e in items(res) if isinstance(e, dict)}
    want = {"ayse.yilmaz@demo.hr360", "mehmet.demir@demo.hr360", "zeynep.kaya@demo.hr360", "elif.sahin@demo.hr360"}
    check("İK yöneticisi: dört demo çalışanı listelenir", code == 200 and want <= emails, f"HTTP {code}, eksik {sorted(want - emails)}")

    for who, email in (("ayse", "ayse.yilmaz@demo.hr360"), ("mehmet", "mehmet.demir@demo.hr360"), ("ik", "elif.sahin@demo.hr360")):
        code, res = get("/api/employee/employees/me", who)
        check(f"{who}: giriş yapar ve kendi çalışan kaydını görür", code == 200 and isinstance(res, dict) and res.get("email") == email, f"HTTP {code}")

    code, res = get("/api/leave/leave-requests", "ayse")
    check("ayse: izin talebi listesi (seed'deki yıllık izin)", code == 200 and len(items(res)) >= 1, f"HTTP {code}")

    code, res = get("/api/organization/departments", "admin")
    names = {d.get("name") for d in items(res) if isinstance(d, dict)}
    check("departmanlar: Mühendislik ve İnsan Kaynakları", code == 200 and {"Mühendislik", "İnsan Kaynakları"} <= names, f"HTTP {code}")

    print(f"FAILS: {FAILS}")
    return 1 if FAILS else 0


if __name__ == "__main__":
    sys.exit(main())

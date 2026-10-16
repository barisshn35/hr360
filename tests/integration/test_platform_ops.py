#!/usr/bin/env python3
"""Arayüzden bakım işleri: /api/tenant/platform/ops + scripts/ops-runner.py.

Denetlenenler: yalnızca platform yöneticisi; iş türü ve bağımsız değişken doğrulaması (bilinmeyen
tür, bilinmeyen etiket/yedek, yazılı onay); bekleyen iş iptali; yıkıcı iş sürmekte olan iş varken
kuyruğa alınmaz; çalıştırıcı izinli işi çalıştırıp çıktıyı yazar; istekler denetim kaydına girer.
Gerçek güncelleme / geri yükleme / geri alma ÇALIŞTIRILMAZ (yalnızca doğrulama ve iptal).
Test işleri, çıktı dosyaları sonda silinir.
Çalıştırma: timeout 300 python3 tests/integration/test_platform_ops.py
"""
import os
import subprocess
import sys

sys.path.insert(0, os.path.dirname(__file__))
from common import FAIL, api, check  # noqa: E402

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
FAIL.clear()
B = "/api/tenant/platform/ops"


def psql(sql):
    out = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", "hr360_operational", "-Atc", sql],
                         capture_output=True, text=True, cwd=ROOT)
    return (out.stdout + out.stderr).strip()


def runner():
    return subprocess.run(["scripts/ops-runner.sh", "run"], cwd=ROOT, capture_output=True, text=True, timeout=240).returncode


start = psql("SELECT now()")
busy = psql("""SELECT count(*) FROM platform_ops_jobs WHERE "Status" IN ('Pending','Running')""")
if busy != "0":
    print(f"ATLANDI: kuyrukta {busy} gerçek bakım işi var")
    sys.exit(0)

try:
    check("PO0: çalıştırıcı turu başarılı", runner() == 0)
    code, _ = api("ik", "GET", f"{B}/status")
    check("PO1: İK erişemez", code == 403, code)
    code, _ = api("admin", "GET", f"{B}/status")
    check("PO1: şirket yöneticisi erişemez", code == 403, code)
    code, st = api("platform", "GET", f"{B}/status")
    check("PO1: platform yöneticisi durumu görür", code == 200 and st["runnerOk"] and st["snapshot"]["version"], (code, st and {k: st[k] for k in ("runnerOk", "updatedAt")}))
    tag = (st["snapshot"].get("tags") or [None])[0]

    code, _ = api("platform", "POST", f"{B}/jobs", {"kind": "shell", "args": {"cmd": "id"}})
    check("PO2: izinli olmayan iş türü reddedilir", code == 400, code)
    code, _ = api("platform", "POST", f"{B}/jobs", {"kind": "update", "args": {"ref": "v9.9.9"}})
    check("PO2: bilinmeyen sürüm etiketi reddedilir", code == 400, code)
    code, _ = api("platform", "POST", f"{B}/jobs", {"kind": "update", "args": {"ref": "main; reboot"}})
    check("PO2: etiket biçimi dışı değer reddedilir", code == 400, code)
    code, _ = api("platform", "POST", f"{B}/jobs", {"kind": "restore", "args": {"archive": "../../etc/passwd"}, "confirm": "GERİ YÜKLE"})
    check("PO2: listede olmayan yedek reddedilir", code == 400, code)
    code, _ = api("platform", "POST", f"{B}/jobs", {"kind": "rollback", "args": {}})
    check("PO2: yazılı onaysız geri alma reddedilir", code == 400, code)
    code, _ = api("platform", "POST", f"{B}/jobs", {"kind": "schedule", "args": {"at": "25:00", "keep": 14}})
    check("PO2: geçersiz saat reddedilir", code == 400, code)

    code, j1 = api("platform", "POST", f"{B}/jobs", {"kind": "check-updates", "args": {}})
    check("PO3: iş kuyruğa alınır", code == 200 and j1["status"] == "Pending", (code, j1))
    if tag:
        code, _ = api("platform", "POST", f"{B}/jobs", {"kind": "update", "args": {"ref": tag}})
        check("PO3: bekleyen iş varken güncelleme kuyruğa alınmaz", code == 409, code)
    code, c = api("platform", "POST", f"{B}/jobs/{j1['id']}/cancel")
    check("PO3: bekleyen iş iptal edilir", code == 200 and c["status"] == "Cancelled", (code, c))
    code, _ = api("platform", "POST", f"{B}/jobs/{j1['id']}/cancel")
    check("PO3: iptal edilmiş iş yeniden iptal edilemez", code == 400, code)

    code, j2 = api("platform", "POST", f"{B}/jobs", {"kind": "check-updates", "args": {}})
    runner()
    code, job = api("platform", "GET", f"{B}/jobs/{j2['id']}")
    check("PO4: çalıştırıcı işi çalıştırır, çıktı yazılır", code == 200 and job["status"] == "Succeeded" and job["exitCode"] == 0 and "git fetch" in (job["output"] or ""), (code, job))
    _, jobs = api("platform", "GET", f"{B}/jobs")
    check("PO4: iş listesinde çıktı taşınmaz", all(x["output"] is None for x in jobs) and any(x["id"] == j2["id"] for x in jobs), len(jobs))
    n = psql(f"""SELECT count(*) FROM audit_log WHERE "EntityType" = 'PlatformOps' AND "OccurredAt" >= '{start}'""")
    check("PO5: istekler denetim kaydına yazılır", n not in ("", "0") and n.isdigit() and int(n) >= 3, n)
finally:
    ids = psql(f"""SELECT "Id" FROM platform_ops_jobs WHERE "RequestedAt" >= '{start}'""").split()
    psql(f"""DELETE FROM platform_ops_jobs WHERE "RequestedAt" >= '{start}'""")
    for i in ids:
        for ext in (".json", ".log"):
            p = os.path.join(ROOT, "ops-logs", i + ext)
            if os.path.exists(p):
                os.remove(p)

print("\nSONUÇ:", "BAŞARILI" if not FAIL else f"{len(FAIL)} HATA: {FAIL}")
sys.exit(1 if FAIL else 0)

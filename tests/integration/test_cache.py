"""Redis (Valkey) önbelleği: anahtarların kiracı/kullanıcıya göre ayrılması, yazınca
eskitme, açık API hız sınırının paylaşılan sayaçla çalışması ve Redis kapalıyken
servislerin veritabanından çalışmaya devam etmesi.

Ön koşul: kurulum çalışıyor (redis servisi dahil). Test redis konteynerini bir süre
durdurup yeniden başlatır.
"""

import datetime as dt
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(__file__))
from common import FAIL, api, check  # noqa: E402

ROOT = os.path.join(os.path.dirname(__file__), "..", "..")
FAIL.clear()


def compose(*args):
    return subprocess.run(["docker", "compose", *args], cwd=ROOT, capture_output=True, text=True)


def redis(*cmd):
    out = compose("exec", "-T", "redis", "sh", "-c",
                  'valkey-cli -a "$REDIS_PASSWORD" --no-auth-warning ' + " ".join(f"'{c}'" for c in cmd))
    return out.stdout.strip()


def keys(pattern):
    return [k for k in redis("--scan", "--pattern", pattern).splitlines() if k]


redis("FLUSHALL")

# --- kim nerede: önbellek + yazınca eskitme
code, p1 = api("ayse", "GET", "/api/engagement/workplace/presence")
check("Ofis doluluğu 200", code == 200, code)
pk = keys("hr360:presence:*")
check("Ofis doluluğu önbelleğe yazıldı (anahtar kiracıyı içeriyor)", any(":presence:" in k and k.count(":") >= 4 for k in pk), pk)
code, p2 = api("mehmet", "GET", "/api/engagement/workplace/presence")
check("İkinci okuma aynı sonucu verir", code == 200 and p1 == p2)

# İzinli olmadığı (izin, yer bilgisinin önüne geçer) gelecek bir hafta içi gün bulunur.
today = dt.date.today()
day = q = None
for w in range(1, 12):
    week = today - dt.timedelta(days=today.weekday()) + dt.timedelta(weeks=w)
    q = f"?from={week.isoformat()}&to={(week + dt.timedelta(days=4)).isoformat()}"
    code, before = api("ayse", "GET", "/api/engagement/workplace/presence" + q)
    me = next((p for p in before["people"] if "Ayşe" in p["name"]), None)
    free = [d for d in (me["days"] if me else []) if d["mode"] != "Leave"]
    if free:
        day, old_mode = dt.date.fromisoformat(free[0]["date"]), free[0]["mode"]
        break
check("İzinsiz bir test günü bulundu", day is not None)
new_mode = "Office" if old_mode != "Office" else "Remote"
code, _ = api("ayse", "PUT", "/api/engagement/workplace/presence", {"date": day.isoformat(), "mode": new_mode, "note": "önbellek testi"})
check("Yer güncellendi", code == 200, code)
code, after = api("mehmet", "GET", "/api/engagement/workplace/presence" + q)
me2 = next((p for p in after["people"] if "Ayşe" in p["name"]), None)
mode2 = next((d["mode"] for d in me2["days"] if d["date"] == day.isoformat()), None) if me2 else None
check("Güncelleme önbelleği eskitti; başka kullanıcı yeni değeri hemen görür", mode2 == new_mode, (old_mode, new_mode, mode2))

# --- dizin (çalışan listesi) önbelleği
check("Çalışan dizini önbellekte", bool(keys("hr360:people:*")), keys("hr360:*"))

# --- ekip sağlığı: kullanıcıya göre ayrı anahtar
code, th_m = api("mehmet", "GET", "/api/engagement/team-health")
code2, th_a = api("admin", "GET", "/api/engagement/team-health")
tk = keys("hr360:team-health:*")
check("Ekip sağlığı: yönetici ve İK için ayrı anahtar", code == 200 and code2 == 200 and len(tk) >= 2, tk)
code, th_m2 = api("mehmet", "GET", "/api/engagement/team-health")
check("Yöneticinin ikinci okuması kendi önbelleğinden, İK'nınkiyle karışmaz",
      th_m2 == th_m and ({m["employeeId"] for m in th_m.get("members", [])} != {m["employeeId"] for m in th_a.get("members", [])}
                         or th_m == th_a), (len(th_m.get("members", [])), len(th_a.get("members", []))))
code, _ = api("ayse", "GET", "/api/engagement/team-health")
check("Yetki önbellekten önce: çalışan ekip sağlığını göremez", code == 403, code)

# --- analitik
code, a12 = api("admin", "GET", "/api/governance/analytics/overview?months=12")
code6, a6 = api("admin", "GET", "/api/governance/analytics/overview?months=6")
check("Analitik: dönem başına ayrı anahtar", code == 200 and code6 == 200 and a12["months"] == 12 and a6["months"] == 6
      and len(keys("hr360:analytics-overview:*")) >= 2)
code, a12b = api("admin", "GET", "/api/governance/analytics/overview?months=12")
check("Analitik önbellekten aynı gövdeyle döner", a12b == a12)

# --- açık API hız sınırı: tüm servis kopyaları Redis'teki ortak sayacı kullanır
import urllib.request  # noqa: E402

from common import BASE  # noqa: E402

code, k = api("admin", "POST", "/api/governance/api-keys", {"name": "önbellek testi", "scopes": ["employees:read"]})
check("API anahtarı oluşturuldu", code == 200 and k.get("key"), (code, k))
remaining = []
for _ in range(3):
    req = urllib.request.Request(BASE + "/api/governance/public/v1/employees", headers={"X-Api-Key": k["key"]})
    with urllib.request.urlopen(req, timeout=30) as r:
        remaining.append(int(r.headers["X-RateLimit-Remaining"]))
check("Açık API: kalan istek sayacı azalıyor", remaining == [119, 118, 117], remaining)
check("Açık API sayacı Redis'te", bool(keys("hr360:public-api-rate:*")), keys("hr360:public-api-rate:*"))
api("admin", "DELETE", f"/api/governance/api-keys/{k['id']}")

# --- Redis kapalıyken
compose("stop", "redis")
time.sleep(1)
codes = [api("ayse", "GET", "/api/engagement/workplace/presence")[0],
         api("mehmet", "GET", "/api/engagement/team-health")[0],
         api("admin", "GET", "/api/governance/analytics/overview?months=12")[0],
         api("admin", "GET", "/api/employee/employees")[0]]
check("Redis kapalıyken uçlar veritabanından çalışır", codes == [200, 200, 200, 200], codes)
t0 = time.time()
code, _ = api("ayse", "GET", "/api/engagement/workplace/presence")
check("Redis kapalıyken gecikme eklenmez (devre kesici)", code == 200 and time.time() - t0 < 1.5, round(time.time() - t0, 2))
compose("start", "redis")
for _ in range(30):
    if "PONG" in redis("PING"):
        break
    time.sleep(1)

# Devre kesici 30 sn sonra yeniden dener.
deadline = time.time() + 45
ok = False
while time.time() < deadline:
    api("ayse", "GET", "/api/engagement/workplace/presence")
    if keys("hr360:presence:*"):
        ok = True
        break
    time.sleep(3)
check("Redis geri gelince önbellek yeniden kullanılır", ok)

print(f"\nFAILS: {len(FAIL)}")
for f in FAIL:
    print("  -", f)
sys.exit(1 if FAIL else 0)

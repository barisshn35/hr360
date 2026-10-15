"""Rapor asistanı ve İK asistanı: Türkçe ve İngilizce soru, yanıt arayüz dilinde (X-HR360-Lang)."""

import os
import sys

sys.path.insert(0, os.path.dirname(__file__))
from common import FAIL, check, http, tok  # noqa: E402

R = "/api/governance/insights"
FAIL.clear()


def ask(who, path, question, lang):
    return http("POST", path, {"question": question}, {"Authorization": "Bearer " + tok(who), "X-HR360-Lang": lang})


cases = [
    ("Leave days by department in the last 6 months", "leave", "department"),
    ("Monthly hires this year", "hires", "month"),
    ("Number of leavers last year", "exits", "none"),
    ("Headcount by department", "headcount", "department"),
    ("Employees with the most overtime last month", "overtime", "person"),
    ("Average performance score by department", "performance", "department"),
    ("Monthly expense total this year", "expense", "month"),
    ("Leave by type this year", "leave", "type"),
]
for q, metric, group in cases:
    code, r = ask("admin", f"{R}/report", q, "en")
    check(f"EN: '{q}' → {metric}/{group}", code == 200 and r["understood"] and r["metric"] == metric and r["groupBy"] == group,
          (code, r.get("metric"), r.get("groupBy"), r.get("interpretation")))

code, r = ask("admin", f"{R}/report", "Leave days by department in the last 6 months", "en")
check("EN: yorum ve sütunlar İngilizce", r["interpretation"].startswith("Approved leave days by department") and "last 6 months" in r["interpretation"]
      and r["columns"] == ["Department", "Leave days"], (r["interpretation"], r["columns"]))
check("EN: örnek öneriler İngilizce", all(not any(c in s for c in "çğıöşüİ") for s in r["suggestions"]), r["suggestions"])

code, r = ask("admin", f"{R}/report", "Son 6 ayda departmanlara göre izin günleri", "tr")
check("TR: Türkçe soru Türkçe yanıt", code == 200 and r["understood"] and r["columns"] == ["Departman", "İzin günü"]
      and r["interpretation"].startswith("Son 6 ayda departmanlara göre"), (r.get("interpretation"), r.get("columns")))

code, r = ask("admin", f"{R}/report", "Son 6 ayda departmanlara göre izin günleri", "en")
check("Türkçe soru, İngilizce arayüz: anlaşılır, yanıt İngilizce", code == 200 and r["understood"] and r["columns"][0] == "Department", r.get("columns"))

code, r = ask("admin", f"{R}/report", "What is the weather like?", "en")
check("EN: anlaşılmayan soru İngilizce açıklama", code == 200 and not r["understood"] and r["interpretation"].startswith("I couldn't understand"), r.get("interpretation"))

code, ex = http("GET", f"{R}/examples", None, {"Authorization": "Bearer " + tok("admin"), "X-HR360-Lang": "en"})
check("EN: örnek sorular İngilizce", code == 200 and ex[0].startswith("Leave days"), ex[:2])
code, ex = http("GET", f"{R}/examples", None, {"Authorization": "Bearer " + tok("admin")})
check("Başlık yoksa Türkçe (varsayılan)", code == 200 and ex[0].startswith("Son 6 ayda"), ex[:2])

# İK asistanı
code, r = ask("ayse", f"{R}/assistant", "hello", "en")
check("Asistan EN: yardım iletisi", code == 200 and r["reply"].startswith("Hello!"), r.get("reply", "")[:80])
code, r = ask("ayse", f"{R}/assistant", "What is my leave balance?", "en")
check("Asistan EN: izin bakiyesi", code == 200 and r["source"] == "data" and ("days available" in r["reply"] or "no leave balance" in r["reply"]), r.get("reply", "")[:120])
code, r = ask("ayse", f"{R}/assistant", "izin bakiyem ne kadar", "tr")
check("Asistan TR: izin bakiyesi Türkçe", code == 200 and r["source"] == "data" and "izin" in r["reply"].lower(), r.get("reply", "")[:120])
code, r = ask("mehmet", f"{R}/assistant", "How many employees by department?", "en")
check("Asistan EN: yönetici analitik sorusu rapora gider", code == 200 and r["source"] == "report" and r["report"]["metric"] == "headcount", (r.get("source"), r.get("reply")))

print(f"\nFAILS: {len(FAIL)}")
for f in FAIL:
    print("  -", f)
sys.exit(1 if FAIL else 0)

"""Cobertura kapsam raporlarını servis başına Markdown tabloya çevirir (CI özeti).

Kullanım: python3 tests/support/coverage_summary.py BAŞLIK rapor.xml [rapor.xml ...]
Birden çok raporda aynı satır geçerse (bir servise iki test projesi bakıyorsa) en az bir raporda
çalışmışsa kapsanmış sayılır. Test projelerinin kendisi (*.Tests, tests/) sayılmaz.
"""
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict


def collect(paths):
    # paket -> {(dosya, satır): kapsandı mı}
    lines = defaultdict(dict)
    for path in paths:
        for pkg in ET.parse(path).getroot().iter("package"):
            name = pkg.get("name", "")
            if name.endswith(".Tests") or name in ("tests", "test"):
                continue
            for cls in pkg.iter("class"):
                fn = cls.get("filename", "")
                if "/tests/" in fn or "/obj/" in fn:
                    continue
                for ln in cls.iter("line"):
                    key = (fn, ln.get("number"))
                    hit = int(ln.get("hits", "0")) > 0
                    lines[name][key] = lines[name].get(key, False) or hit
    return lines


def main():
    title, paths = sys.argv[1], sys.argv[2:]
    lines = collect(paths)
    if not lines:
        print(f"### {title}\n\nKapsam raporu bulunamadı.")
        return
    rows, tot_cov, tot_all = [], 0, 0
    for name in sorted(lines):
        cov, alln = sum(lines[name].values()), len(lines[name])
        tot_cov, tot_all = tot_cov + cov, tot_all + alln
        rows.append(f"| {name} | %{100 * cov / alln:.1f} | {cov}/{alln} |")
    print(f"### {title}\n\n| Bileşen | Satır kapsamı | Satır |\n|---|---|---|")
    print("\n".join(rows))
    print(f"| **Toplam** | **%{100 * tot_cov / max(tot_all, 1):.1f}** | {tot_cov}/{tot_all} |")


if __name__ == "__main__":
    main()

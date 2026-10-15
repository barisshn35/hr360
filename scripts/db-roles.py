#!/usr/bin/env python3
"""HR360 servis başına PostgreSQL rolleri: üretici, denetleyici, parola atayıcı.

Kullanım (genelde scripts/db-roles.sh üzerinden):
  db-roles.py generate [--sql deploy/postgres/roles.sql] [--review deploy/postgres/roles-review.md] [--json F]
  db-roles.py check    [--container AD] [--service leave ...]   # canlıya yalnızca OKUMA sorguları
  db-roles.py passwords [--container AD] [--secrets-dir DIR] [--env-file .env] [--service ...]

Eşleme koddan çıkarılır: her servisin DbContext'indeki ToTable("...") / [Table("...")] eşlemeleri
tam yetki (SELECT, INSERT, UPDATE, DELETE); .cs dosyalarındaki SQL metinlerinde INSERT INTO / UPDATE /
DELETE FROM / TRUNCATE / ON CONFLICT DO UPDATE / FOR UPDATE ilgili yazma yetkisini; metinlerde geçen
her tablo adı SELECT'i getirir. Tablo evreni data/migrations/sql-all-schemas.sql ve scripts/sql/*.sql'deki
CREATE TABLE / CREATE VIEW'lerdir. Tetikleyici fonksiyonlarının (invoker olarak çalışır) dokunduğu tablolar
da tetikleyiciyi çalıştıran role eklenir. audit_log'da hiçbir servis rolüne UPDATE/DELETE/TRUNCATE
verilmez; saklama süresi silmesi yalnızca hr360_retention rolündedir.

Yalnızca standart kütüphane. Veritabanına `docker exec -i <konteyner> psql` ile bağlanılır.
Parola ve gizli değerler ekrana basılmaz.
"""
from __future__ import annotations

import argparse
import base64
import hashlib
import hmac
import json
import os
import re
import secrets
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SERVICES_DIR = ROOT / "apps" / "services"

# Servis dizini -> (.env/compose anahtarı, rol adı). Compose: HR360_DB_USER_<KEY>, HR360_DB_PASSWORD_<KEY>.
SERVICES = {
    "organization-service": "ORGANIZATION",
    "employee-service": "EMPLOYEE",
    "workflow-service": "WORKFLOW",
    "leave-service": "LEAVE",
    "recruitment-service": "RECRUITMENT",
    "onboarding-service": "ONBOARDING",
    "timeshift-service": "TIMESHIFT",
    "performance-service": "PERFORMANCE",
    "learning-service": "LEARNING",
    "engagement-service": "ENGAGEMENT",
    "governance-service": "GOVERNANCE",
    "compensation-service": "COMPENSATION",
    "expense-service": "EXPENSE",
    "notification-service": "NOTIFICATION",
    "tenant-service": "TENANT",
}
RETENTION_KEY = "RETENTION"
RETENTION_ROLE = "hr360_retention"
# Tablo ön eki -> sahibi servis (inceleme listesinde "başka servisin tablosuna yazma" tespiti için).
PREFIX_OWNER = {
    "organization": "organization-service", "employee": "employee-service", "workflow": "workflow-service",
    "leave": "leave-service", "recruitment": "recruitment-service", "onboarding": "onboarding-service",
    "timeshift": "timeshift-service", "performance": "performance-service", "learning": "learning-service",
    "engagement": "engagement-service", "governance": "governance-service", "compensation": "compensation-service",
    "expense": "expense-service", "notification": "notification-service", "tenant": "tenant-service",
    "platform": "tenant-service",
}
SHARED_TABLES = {"audit_log", "messaging_outbox", "messaging_processed_events"}
# audit_log: değiştirilemez denetim kaydı. Servis rolleri yalnızca okur/ekler.
AUDIT_ALLOWED = {"SELECT", "INSERT"}
PRIV_ORDER = ["SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE"]


def role_of(key: str) -> str:
    return "hr360_" + key.lower()


# ------------------------------------------------------------------ şema evreni
def schema_files() -> list[Path]:
    files = [ROOT / "data" / "migrations" / "sql-all-schemas.sql"]
    files += sorted((ROOT / "scripts" / "sql").glob("*.sql"))
    return [f for f in files if f.exists()]


RX_CREATE_TABLE = re.compile(r'CREATE\s+(?:UNLOGGED\s+)?TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?"?(?:public\.)?"?([a-z_][a-z0-9_]*)"?', re.I)
RX_CREATE_VIEW = re.compile(r'CREATE\s+(?:OR\s+REPLACE\s+)?(?:MATERIALIZED\s+)?VIEW\s+(?:IF\s+NOT\s+EXISTS\s+)?"?(?:public\.)?([a-z_][a-z0-9_]*)', re.I)
RX_FUNC = re.compile(r'CREATE\s+(?:OR\s+REPLACE\s+)?FUNCTION\s+(?:public\.)?([a-z_][a-z0-9_]*)\s*\(.*?\$\$(.*?)\$\$', re.I | re.S)
RX_TRIGGER = re.compile(r'CREATE\s+TRIGGER\s+\w+\s+(?:BEFORE|AFTER|INSTEAD\s+OF)\s+([\w\s]+?)\s+ON\s+"?([a-z_][a-z0-9_]*)"?.*?EXECUTE\s+(?:FUNCTION|PROCEDURE)\s+(?:public\.)?([a-z_][a-z0-9_]*)', re.I | re.S)


def load_schema():
    tables, views, funcs, triggers = set(), set(), {}, []
    for f in schema_files():
        s = f.read_text(encoding="utf-8")
        s = re.sub(r"--[^\n]*", "", s)
        tables.update(m.group(1).lower() for m in RX_CREATE_TABLE.finditer(s))
        views.update(m.group(1).lower() for m in RX_CREATE_VIEW.finditer(s))
        for m in RX_FUNC.finditer(s):
            funcs[m.group(1).lower()] = m.group(2)
        for m in RX_TRIGGER.finditer(s):
            events = {e.upper() for e in re.findall(r"INSERT|UPDATE|DELETE|TRUNCATE", m.group(1), re.I)}
            triggers.append((m.group(2).lower(), events, m.group(3).lower()))
    return tables, views, funcs, triggers


# ------------------------------------------------------------------ C# sözcük çözümleyici (metin sabitleri)
def cs_strings(src: str) -> list[tuple[int, str]]:
    """C# kaynağındaki metin sabitlerini (satır, içerik) döndürür; yorumları atlar.
    Ham (üç tırnaklı, $ önekli dahil), verbatim (@"..."), ara değerli ($"...{...}...") metinleri destekler.
    Ara değer delikleri metne dahil edilir (içlerindeki tablo adları da bulunsun diye)."""
    out: list[tuple[int, str]] = []
    i, n = 0, len(src)

    def line_at(pos: int) -> int:
        return src.count("\n", 0, pos) + 1

    while i < n:
        c = src[i]
        if src.startswith("//", i):
            j = src.find("\n", i)
            i = n if j < 0 else j
            continue
        if src.startswith("/*", i):
            j = src.find("*/", i + 2)
            i = n if j < 0 else j + 2
            continue
        if c == "'":  # karakter sabiti
            j = i + 1
            while j < n and src[j] != "'":
                j += 2 if src[j] == "\\" else 1
            i = j + 1
            continue
        # önek: $, $$, @, $@, @$
        m = re.match(r'(\$*@?\$*)("{3,}|")', src[i:i + 12])
        if m and (m.group(1) or c == '"'):
            prefix, quotes = m.group(1), m.group(2)
            start = i + len(prefix) + len(quotes)
            if len(quotes) >= 3:  # ham metin
                end = src.find(quotes, start)
                end = n if end < 0 else end
                out.append((line_at(i), src[start:end]))
                i = end + len(quotes)
                continue
            verbatim = "@" in prefix
            j, buf, depth = start, [], 0
            while j < n:
                ch = src[j]
                if depth == 0 and verbatim and ch == '"':
                    if src.startswith('""', j):
                        buf.append('"'); j += 2; continue
                    break
                if depth == 0 and not verbatim:
                    if ch == "\\":
                        buf.append(src[j:j + 2]); j += 2; continue
                    if ch == '"' or ch == "\n":
                        break
                if "$" in prefix:
                    if ch == "{" and not src.startswith("{{", j):
                        depth += 1
                    elif ch == "{" and depth == 0:
                        buf.append("{{"); j += 2; continue
                    elif ch == "}" and depth > 0:
                        depth -= 1
                    elif ch == '"' and depth > 0:  # delikteki iç metin: kapanışına atla
                        k = j + 1
                        while k < n and src[k] != '"':
                            k += 2 if src[k] == "\\" else 1
                        buf.append(src[j:k + 1]); j = k + 1; continue
                buf.append(ch); j += 1
            out.append((line_at(i), "".join(buf)))
            i = j + 1
            continue
        i += 1
    return out


RX_TOTABLE = re.compile(r'ToTable\(\s*"([a-z_][a-z0-9_]*)"')
RX_TABLE_ATTR = re.compile(r'\[Table\(\s*"([a-z_][a-z0-9_]*)"')
RX_INSERT = re.compile(r'\bINSERT\s+INTO\s+"?(?:public\.)?([a-z_][a-z0-9_]*)', re.I)
RX_UPDATE = re.compile(r'\bUPDATE\s+"?(?:public\.)?([a-z_][a-z0-9_]*)"?(?:\s+(?:AS\s+)?[a-z_]\w*)?\s+SET\b', re.I)
RX_DELETE = re.compile(r'\bDELETE\s+FROM\s+"?(?:public\.)?([a-z_][a-z0-9_]*)', re.I)
RX_TRUNC = re.compile(r'\bTRUNCATE\s+(?:TABLE\s+)?"?(?:public\.)?([a-z_][a-z0-9_]*)', re.I)
RX_UPSERT = re.compile(r'\bON\s+CONFLICT\b.*?\bDO\s+UPDATE\b', re.I | re.S)
RX_LOCK = re.compile(r'\bFOR\s+(?:NO\s+KEY\s+)?(?:UPDATE|SHARE)\b', re.I)
RX_DYNAMIC = re.compile(r'\b(?:FROM|JOIN|INTO|UPDATE|TABLE)\s+\{', re.I)


RX_ENCCOL = re.compile(r'new\(\s*"([a-z_][a-z0-9_]*)"\s*,\s*"\w+"\s*,\s*[\w.]*EncKind\.\w+')


def scan_service(svc_dir: Path, universe: set[str]):
    """-> (privs: {table: set}, evidence: {(table, priv): [file:line]}, ef: set, uncertain: [str])"""
    privs: dict[str, set[str]] = {}
    evidence: dict[tuple[str, str], list[str]] = {}
    ef: set[str] = set()
    uncertain: list[str] = []
    word = re.compile(r"\b(" + "|".join(sorted(universe, key=len, reverse=True)) + r")\b")

    def add(t: str, p: str, where: str):
        if t not in universe:
            return
        privs.setdefault(t, set()).add(p)
        ev = evidence.setdefault((t, p), [])
        if len(ev) < 3:
            ev.append(where)

    for f in sorted(svc_dir.rglob("*.cs")):
        rel = f.relative_to(ROOT).as_posix()
        if "/bin/" in rel or "/obj/" in rel:
            continue
        src = f.read_text(encoding="utf-8", errors="replace")
        for m in RX_TOTABLE.finditer(src):
            t = m.group(1)
            ef.add(t)
            for p in ("SELECT", "INSERT", "UPDATE", "DELETE"):
                add(t, p, f"{rel}:{src.count(chr(10), 0, m.start()) + 1} (EF)")
        for m in RX_TABLE_ATTR.finditer(src):
            t = m.group(1)
            ef.add(t)
            for p in ("SELECT", "INSERT", "UPDATE", "DELETE"):
                add(t, p, f"{rel}:{src.count(chr(10), 0, m.start()) + 1} (EF [Table])")
        # Anahtar yenileme işi (Security/KeyRotationJob.cs) Program.cs'teki encryptedColumns listesindeki
        # tabloları dinamik UPDATE ile yeniden yazar: new("tablo", "Sütun", ...EncKind.X).
        for m in RX_ENCCOL.finditer(src):
            t = m.group(1)
            where = f"{rel}:{src.count(chr(10), 0, m.start()) + 1} (anahtar yenileme)"
            add(t, "SELECT", where)
            add(t, "UPDATE", where)
        for line, s in cs_strings(src):
            where = f"{rel}:{line}"
            names = set(word.findall(s))
            if not names:
                if RX_DYNAMIC.search(s) and re.search(r"\b(SELECT|INSERT|UPDATE|DELETE)\b", s):
                    uncertain.append(f"{where}: tablo adı çalışma anında birleştiriliyor (`{s.strip()[:90]}`)")
                continue
            for t in names:
                add(t, "SELECT", where)
            for m in RX_INSERT.finditer(s):
                add(m.group(1).lower(), "INSERT", where)
                if RX_UPSERT.search(s[m.start():]):
                    add(m.group(1).lower(), "UPDATE", where + " (ON CONFLICT DO UPDATE)")
            for m in RX_UPDATE.finditer(s):
                add(m.group(1).lower(), "UPDATE", where)
            for m in RX_DELETE.finditer(s):
                add(m.group(1).lower(), "DELETE", where)
            for m in RX_TRUNC.finditer(s):
                add(m.group(1).lower(), "TRUNCATE", where)
            if RX_LOCK.search(s):
                # SELECT ... FOR UPDATE/SHARE, kilitlenen tabloda UPDATE yetkisi ister.
                for t in names:
                    add(t, "UPDATE", where + " (FOR UPDATE/SHARE)")
                uncertain.append(f"{where}: satır kilidi (FOR UPDATE/SHARE) — metindeki tüm tablolara UPDATE verildi: {', '.join(sorted(names))}")
            if RX_DYNAMIC.search(s):
                uncertain.append(f"{where}: dinamik tablo/parça birleştirme — metindeki tablolar: {', '.join(sorted(names))}")
    return privs, evidence, ef, uncertain


def trigger_requirements(funcs, triggers, universe):
    """{(tablo, olay): {tablo2: set(yetki)}} — tetikleyici fonksiyonu invoker yetkisiyle çalışır."""
    req = {}
    for table, events, fn in triggers:
        body = funcs.get(fn, "")
        need: dict[str, set[str]] = {}
        for m in RX_INSERT.finditer(body):
            need.setdefault(m.group(1).lower(), set()).update({"INSERT"})
        for m in RX_UPDATE.finditer(body):
            need.setdefault(m.group(1).lower(), set()).update({"UPDATE", "SELECT"})
        for m in RX_DELETE.finditer(body):
            need.setdefault(m.group(1).lower(), set()).update({"DELETE", "SELECT"})
        for t in re.findall(r"\bFROM\s+([a-z_][a-z0-9_]*)", body, re.I):
            need.setdefault(t.lower(), set()).add("SELECT")
        need = {t: p for t, p in need.items() if t in universe}
        for ev in events:
            if need:
                req.setdefault((table, ev), {}).update(need)
    return req


def build_mapping():
    tables, views, funcs, triggers = load_schema()
    universe = tables | views
    trig = trigger_requirements(funcs, triggers, universe)
    result = {}
    for d, key in SERVICES.items():
        privs, evidence, ef, uncertain = scan_service(SERVICES_DIR / d, universe)
        notes = []
        # Tetikleyici bağımlılıkları (yineleyerek; zincir derinliği küçük).
        for _ in range(3):
            for (t, ev), need in trig.items():
                if ev in privs.get(t, set()):
                    for t2, ps in need.items():
                        new = ps - privs.get(t2, set())
                        if new:
                            privs.setdefault(t2, set()).update(new)
                            for pp in new:
                                evidence.setdefault((t2, pp), []).append(f"tetikleyici: {t} {ev}")
                            notes.append(f"{t} üzerindeki {ev} tetikleyicisi {t2} için {', '.join(sorted(new))} istiyor")
        # audit_log kuralı
        if "audit_log" in privs:
            dropped = privs["audit_log"] - AUDIT_ALLOWED
            if dropped:
                notes.append("audit_log: " + ", ".join(sorted(dropped)) + " VERİLMEDİ (değiştirilemez denetim kaydı; silme hr360_retention'da)")
            privs["audit_log"] = privs["audit_log"] & AUDIT_ALLOWED
        if "audit_log" in privs and "INSERT" in privs["audit_log"]:
            privs["audit_log"].add("SELECT")  # zincir tetikleyicisi önceki satırı okur
        own = d
        owned = sorted(t for t in privs if PREFIX_OWNER.get(t.split("_")[0]) == own)
        foreign_write = sorted(
            (t, sorted(p - {"SELECT"})) for t, p in privs.items()
            if t not in SHARED_TABLES and PREFIX_OWNER.get(t.split("_")[0]) not in (own, None) and p - {"SELECT"})
        foreign_ef = sorted(t for t in ef if t not in SHARED_TABLES and PREFIX_OWNER.get(t.split("_")[0]) not in (own, None))
        result[d] = {
            "key": key, "role": role_of(key),
            "privs": {t: [p for p in PRIV_ORDER if p in ps] for t, ps in sorted(privs.items())},
            "owned": owned,
            "read_foreign": sorted(t for t, p in privs.items() if t not in owned and t not in SHARED_TABLES and p == {"SELECT"}),
            "foreign_write": foreign_write,
            "foreign_ef": foreign_ef,
            "evidence": {f"{t}|{p}": w for (t, p), w in evidence.items()},
            "uncertain": sorted(set(uncertain)),
            "notes": sorted(set(notes)),
        }
    unused = sorted(universe - {t for r in result.values() for t in r["privs"]})
    return result, sorted(universe), sorted(views), unused


# ------------------------------------------------------------------ üretim
def render_sql(mapping) -> str:
    L = [
        "-- HR360 servis başına PostgreSQL rolleri ve yetkileri.",
        "-- OTOMATİK ÜRETİLDİ: scripts/db-roles.sh generate (elle düzenlemeyin; kod değişince yeniden üretin).",
        "-- İdempotent; parola İÇERMEZ (roller NOLOGIN oluşturulur; `scripts/db-roles.sh apply` LOGIN + parola verir).",
        "-- Her rolün public şemasındaki tablo/dizi yetkileri önce geri alınır, sonra listeden yeniden verilir;",
        "-- hepsi tek işlemde (çalışan servisler ara durumu görmez). Yeni tablolar (migration sonrası) için yeniden",
        "-- üretip uygulayın: hr360admin'in varsayılan yetkileri (DEFAULT PRIVILEGES) bilerek açılmadı.",
        "BEGIN;",
        "",
    ]
    roles = [(r["role"], r["privs"]) for r in mapping.values()]
    roles.append((RETENTION_ROLE, {"audit_log": ["SELECT", "DELETE"]}))
    for role, privs in roles:
        L.append(f"-- ---------------------------------------------------------------- {role}")
        L.append(f"DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{role}') THEN CREATE ROLE {role} NOLOGIN; END IF; END $$;")
        L.append(f"ALTER ROLE {role} NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS CONNECTION LIMIT 40;")
        L.append(f"REVOKE ALL ON ALL TABLES IN SCHEMA public FROM {role};")
        L.append(f"REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM {role};")
        L.append(f"REVOKE CREATE ON SCHEMA public FROM {role};")
        L.append(f"GRANT USAGE ON SCHEMA public TO {role};")
        L.append("DO $$ DECLARE g text[]; BEGIN")
        L.append("  FOREACH g SLICE 1 IN ARRAY ARRAY[")
        rows = [f"    ['{t}', '{', '.join(ps)}']" for t, ps in privs.items()]
        L.append(",\n".join(rows))
        L.append("  ]::text[] LOOP")
        L.append("    IF to_regclass('public.' || quote_ident(g[1])) IS NOT NULL THEN")
        L.append(f"      EXECUTE format('GRANT %s ON TABLE public.%I TO {role}', g[2], g[1]);")
        L.append("    END IF;")
        L.append("  END LOOP;")
        L.append("END $$;")
        # Ekleme yapılan tabloların serial/identity dizileri
        L.append("DO $$ DECLARE s text; BEGIN")
        L.append("  FOR s IN SELECT DISTINCT seq.oid::regclass::text FROM pg_class seq")
        L.append("      JOIN pg_depend d ON d.objid = seq.oid AND d.deptype IN ('a', 'i')")
        L.append("      JOIN pg_class t ON t.oid = d.refobjid")
        L.append(f"     WHERE seq.relkind = 'S' AND has_table_privilege('{role}', t.oid, 'INSERT') LOOP")
        L.append(f"    EXECUTE format('GRANT USAGE, SELECT ON SEQUENCE %s TO {role}', s);")
        L.append("  END LOOP;")
        L.append("END $$;")
        L.append("")
    L.append("COMMIT;")
    return "\n".join(L) + "\n"


def render_review(mapping, universe, views, unused) -> str:
    L = ["# Veritabanı rolleri — elle gözden geçirme listesi", "",
         "OTOMATİK ÜRETİLDİ: `scripts/db-roles.sh generate`. Eşleme kod taramasıyla çıkarılır; aşağıdakiler",
         "taramanın kesin karar veremediği ya da bilinçli bir tercih gerektiren noktalardır.", "",
         "## Genel", "",
         "- Yeni migration (yeni tablo) sonrasında `scripts/db-roles.sh generate && scripts/db-roles.sh grants`",
         "  çalıştırılmalı. `hr360admin` için `ALTER DEFAULT PRIVILEGES` bilerek açılmadı (en az yetki).",
         "- `audit_log`: servis rolleri yalnızca SELECT + INSERT (zincir tetikleyicisi önceki satırı okur).",
         "  UPDATE zaten tetikleyiciyle (`audit_log_immutable`) engelli. Saklama süresi silmesi",
         f"  (governance `Retention`, kategori AuditLog) `{RETENTION_ROLE}` rolü ister.",
         "- `messaging_outbox`, `messaging_processed_events` tüm servislerce paylaşılır (tam yetki):",
         "  bir servis rolü başka servisin outbox satırını değiştirebilir. Ayırmak için tablo bölünmeli.",
         "- Fonksiyonlar (`hr360_fold`, `audit_row_hash`, tetikleyici fonksiyonları) PUBLIC EXECUTE ile çalışır.",
         "- Görünümler (`" + "`, `".join(views) + "`) sahibinin (hr360admin) yetkisiyle çalışır; RLS'yi atlar.",
         "- Yalnızca tablo adının bir metin sabitinde geçmesi SELECT verir (yorumlar hariç). Bu, sütun adı/ileti",
         "  gibi yanlış pozitiflerle fazladan SELECT verebilir; yazma yetkileri yalnızca SQL kalıplarından gelir.",
         "- EF ile eşlenmiş tablolarda (ToTable) tam yetki verilir: SaveChanges'in yazıp yazmadığı statik",
         "  olarak ayırt edilemez.",
         "- employee-service ve organization-service açılışta `EnsureCreated()` çağırır: rol tablolarını",
         "  gördüğü sürece (information_schema yetkili tabloları gösterir) işlem yapmaz; rolün hiç tablosu",
         "  görünmezse CREATE TABLE dener ve şema CREATE yetkisi olmadığı için açılış hata verir.",
         "- Python ML servisi, Keycloak, MLflow ve postgres-exporter `hr360admin` ile kalır (kapsam dışı).", ""]
    L += ["## Servis özeti", "", "| Servis | Rol | Toplam tablo | Kendi | Başka servisten salt okuma | Başka servise yazma |", "|---|---|---|---|---|---|"]
    for d, r in mapping.items():
        L.append(f"| {d} | `{r['role']}` | {len(r['privs'])} | {len(r['owned'])} | {len(r['read_foreign'])} | {len(r['foreign_write'])} |")
    L.append("")
    for d, r in mapping.items():
        items = []
        for t, ps in r["foreign_write"]:
            ev = r["evidence"].get(f"{t}|{ps[0]}", [])
            items.append(f"- Başka servisin tablosuna yazma: `{t}` ({', '.join(ps)}) — {'; '.join(ev)}")
        for t in r["foreign_ef"]:
            items.append(f"- Başka servisin tablosu EF ile eşlenmiş (tam yetki verildi; salt okuma olabilir): `{t}`")
        items += [f"- {u}" for u in r["uncertain"]]
        items += [f"- Not: {n}" for n in r["notes"]]
        if items:
            L += [f"## {d} (`{r['role']}`)", ""] + items + [""]
    if unused:
        L += ["## Hiçbir servisin kodunda geçmeyen tablolar", "",
              "Bu tablolara hiçbir rol yetki almadı (ölü tablo, yalnızca betik/ML kullanımı ya da dinamik ad olabilir):", ""]
        L += [f"- `{t}`" for t in unused] + [""]
    return "\n".join(L)


def cmd_generate(a):
    mapping, universe, views, unused = build_mapping()
    sql_path, rev_path = ROOT / a.sql, ROOT / a.review
    sql_path.write_text(render_sql(mapping), encoding="utf-8")
    rev_path.write_text(render_review(mapping, universe, views, unused), encoding="utf-8")
    if a.json:
        Path(a.json).write_text(json.dumps(mapping, ensure_ascii=False, indent=1), encoding="utf-8")
    for d, r in mapping.items():
        print(f"{r['role']:22} tablo={len(r['privs']):3} kendi={len(r['owned']):3} salt-okuma(başka)={len(r['read_foreign']):3} "
              f"başkasına-yazma={len(r['foreign_write']):2} belirsiz={len(r['uncertain']):2}")
    print(f"Yazıldı: {sql_path.relative_to(ROOT)}, {rev_path.relative_to(ROOT)} (tablo evreni: {len(universe)}, kullanılmayan: {len(unused)})")


# ------------------------------------------------------------------ veritabanı erişimi
def find_container(explicit: str | None) -> str:
    if explicit:
        return explicit
    try:
        cid = subprocess.run(["docker", "compose", "ps", "-q", "postgres"], cwd=ROOT, capture_output=True, text=True, timeout=30).stdout.strip()
        if cid:
            return cid
    except (OSError, subprocess.SubprocessError):
        pass
    return "hr360-postgres-1"


def psql(container: str, sql: str, db: str = "hr360_operational") -> str:
    p = subprocess.run(["docker", "exec", "-i", container, "psql", "-X", "-q", "-At", "-F", "\t", "-v", "ON_ERROR_STOP=1",
                        "-U", "hr360admin", "-d", db, "-f", "-"], input=sql, capture_output=True, text=True)
    if p.returncode != 0:
        raise SystemExit(f"psql hatası: {p.stderr.strip()}")
    return p.stdout


def lit(s: str) -> str:
    return "'" + s.replace("'", "''") + "'"


def cmd_check(a):
    mapping, *_ = build_mapping()
    c = find_container(a.container)
    selected = {d: r for d, r in mapping.items() if not a.service or r["key"].lower() in {s.lower() for s in a.service} or d in a.service}
    # Tüm denetimler tek sorguda; yalnızca katalog fonksiyonları (okuma).
    rows = []
    for d, r in selected.items():
        for t, ps in r["privs"].items():
            for p in ps:
                rows.append(f"({lit(r['role'])},{lit(t)},{lit(p)})")
    sql = f"""
WITH need(role, tbl, priv) AS (VALUES {', '.join(rows)}),
     roles AS (SELECT DISTINCT role FROM need)
SELECT 'norole', r.role, '', '' FROM roles r WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = r.role)
UNION ALL
SELECT 'notable', n.role, n.tbl, n.priv FROM need n
 WHERE EXISTS (SELECT 1 FROM pg_roles WHERE rolname = n.role) AND to_regclass('public.' || quote_ident(n.tbl)) IS NULL
UNION ALL
SELECT 'missing', n.role, n.tbl, n.priv FROM need n
 WHERE EXISTS (SELECT 1 FROM pg_roles WHERE rolname = n.role) AND to_regclass('public.' || quote_ident(n.tbl)) IS NOT NULL
   AND NOT has_table_privilege(n.role, ('public.' || quote_ident(n.tbl))::regclass, n.priv)
UNION ALL
SELECT 'seq', r.role, seq.oid::regclass::text, 'USAGE' FROM roles r
  JOIN pg_roles pr ON pr.rolname = r.role
  JOIN pg_class t ON t.relnamespace = 'public'::regnamespace AND t.relkind = 'r' AND has_table_privilege(r.role, t.oid, 'INSERT')
  JOIN pg_depend d ON d.refobjid = t.oid AND d.deptype IN ('a', 'i')
  JOIN pg_class seq ON seq.oid = d.objid AND seq.relkind = 'S'
 WHERE NOT has_sequence_privilege(r.role, seq.oid, 'USAGE')
UNION ALL
SELECT 'schema', r.role, 'public', 'USAGE' FROM roles r JOIN pg_roles pr ON pr.rolname = r.role
 WHERE NOT has_schema_privilege(r.role, 'public', 'USAGE')
UNION ALL
SELECT 'audit', pr.rolname, 'audit_log', p FROM pg_roles pr CROSS JOIN unnest(ARRAY['UPDATE','DELETE','TRUNCATE']) p
 WHERE pr.rolname LIKE 'hr360\\_%' AND pr.rolname <> {lit(RETENTION_ROLE)} AND NOT pr.rolsuper
   AND to_regclass('public.audit_log') IS NOT NULL AND has_table_privilege(pr.rolname, 'public.audit_log', p)
UNION ALL
SELECT 'nologin', r.role, '', '' FROM roles r JOIN pg_roles pr ON pr.rolname = r.role WHERE NOT pr.rolcanlogin;
"""
    out = psql(c, sql)
    problems = {}
    nologin = []
    for line in out.splitlines():
        if not line.strip():
            continue
        kind, role, tbl, priv = (line.split("\t") + ["", "", ""])[:4]
        if kind == "nologin":
            nologin.append(role)
            continue
        problems.setdefault(role, []).append((kind, tbl, priv))
    # Gerçek sorgu denemesi: rol olarak her okunan tabloda SELECT ... LIMIT 0 (işlem geri alınır).
    probe_fail = {}
    if a.probe:
        for d, r in selected.items():
            if any(k == "norole" for k, *_ in problems.get(r["role"], [])):
                continue
            stmts = ["BEGIN;", "SET LOCAL statement_timeout = '5s';", f"SET LOCAL ROLE {r['role']};"]
            for t in r["privs"]:
                stmts.append(f"SELECT 1 FROM public.\"{t}\" LIMIT 0;" if t else "")
            stmts.append("ROLLBACK;")
            # Tablo yoksa hata olmasın diye var olanlarla sınırla:
            exists = set(psql(c, "SELECT relname FROM pg_class WHERE relnamespace = 'public'::regnamespace AND relkind IN ('r','v','m','p');").split())
            stmts = [s for s in stmts if not s.startswith("SELECT 1 FROM") or s.split('"')[1] in exists]
            p = subprocess.run(["docker", "exec", "-i", c, "psql", "-X", "-q", "-At", "-v", "ON_ERROR_STOP=1", "-U", "hr360admin",
                                "-d", "hr360_operational", "-f", "-"], input="\n".join(stmts), capture_output=True, text=True)
            if p.returncode != 0:
                probe_fail[r["role"]] = p.stderr.strip().splitlines()[-1] if p.stderr.strip() else "bilinmeyen hata"
    total = 0
    for d, r in selected.items():
        pr = problems.get(r["role"], [])
        mark = "TAMAM" if not pr and r["role"] not in probe_fail else "EKSİK"
        print(f"{mark:6} {r['role']:22} ({d}, {sum(len(v) for v in r['privs'].values())} yetki denetlendi)")
        for kind, tbl, priv in pr:
            total += 1
            msg = {"norole": "rol yok (önce: scripts/db-roles.sh grants)", "notable": f"tablo veritabanında yok: {tbl} ({priv})",
                   "missing": f"eksik yetki: {priv} ON {tbl}", "seq": f"eksik dizi yetkisi: USAGE ON SEQUENCE {tbl}",
                   "schema": "public şemasında USAGE yok", "audit": f"YASAK yetki: {priv} ON audit_log"}[kind]
            print(f"         - {msg}")
        if r["role"] in probe_fail:
            total += 1
            print(f"         - deneme sorgusu başarısız: {probe_fail[r['role']]}")
    for role, pr in problems.items():
        if role not in {r["role"] for r in selected.values()}:
            for kind, tbl, priv in pr:
                if kind == "audit":
                    total += 1
                    print(f"EKSİK  {role}: YASAK yetki {priv} ON audit_log")
    if nologin:
        print(f"Bilgi: giriş izni (LOGIN/parola) olmayan roller: {', '.join(sorted(nologin))} — `scripts/db-roles.sh apply` verir.")
    print(f"Sonuç: {total} sorun." if total else "Sonuç: eksik yetki yok.")
    return 1 if total else 0


# ------------------------------------------------------------------ parolalar (SCRAM doğrulayıcısı istemcide hesaplanır)
def scram_verifier(password: str, iterations: int = 4096) -> str:
    salt = secrets.token_bytes(16)
    salted = hashlib.pbkdf2_hmac("sha256", password.encode("utf-8"), salt, iterations)
    client_key = hmac.new(salted, b"Client Key", hashlib.sha256).digest()
    stored_key = hashlib.sha256(client_key).digest()
    server_key = hmac.new(salted, b"Server Key", hashlib.sha256).digest()
    b = lambda x: base64.b64encode(x).decode()  # noqa: E731
    return f"SCRAM-SHA-256${iterations}:{b(salt)}${b(stored_key)}:{b(server_key)}"


def read_env_file(path: Path) -> dict[str, str]:
    env = {}
    if path.exists():
        for line in path.read_text(encoding="utf-8").splitlines():
            m = re.match(r"^\s*([A-Za-z_][A-Za-z0-9_]*)=(.*)$", line)
            if m:
                v = m.group(2).strip()
                if len(v) >= 2 and v[0] == v[-1] and v[0] in "'\"":
                    v = v[1:-1]
                env[m.group(1)] = v
    return env


def append_env(path: Path, key: str, value: str):
    existed = path.exists()
    with open(path, "a", encoding="utf-8") as f:
        if existed and path.stat().st_size and not path.read_text(encoding="utf-8").endswith("\n"):
            f.write("\n")
        f.write(f"{key}={value}\n")
    os.chmod(path, 0o600)


def cmd_passwords(a):
    env_path = ROOT / a.env_file
    env = read_env_file(env_path)
    sdir = ROOT / a.secrets_dir if a.secrets_dir else None
    keys = [(r, k) for r, k in ((role_of(k), k) for k in SERVICES.values())] + [(RETENTION_ROLE, RETENTION_KEY)]
    if a.service:
        want = {s.lower() for s in a.service}
        keys = [(r, k) for r, k in keys if k.lower() in want or r in want]
    stmts, created = [], []
    for role, key in keys:
        var = f"HR360_DB_PASSWORD_{key}"
        fname = f"hr360_db_password_{key.lower()}.txt"
        pw = os.environ.get(var) or env.get(var)
        if not pw and os.environ.get(var + "_FILE") and Path(os.environ[var + "_FILE"]).exists():
            pw = Path(os.environ[var + "_FILE"]).read_text(encoding="utf-8").strip()
        if not pw:
            for d in [sdir, ROOT / "secrets"]:
                if d and (d / fname).exists():
                    pw = (d / fname).read_text(encoding="utf-8").strip()
                    break
        if not pw:
            pw = secrets.token_urlsafe(32)
            if sdir:
                sdir.mkdir(parents=True, exist_ok=True)
                os.chmod(sdir, 0o700)
                (sdir / fname).write_text(pw + "\n", encoding="utf-8")
                os.chmod(sdir / fname, 0o600)
                created.append(f"{(sdir / fname).relative_to(ROOT) if sdir.is_relative_to(ROOT) else sdir / fname}")
            else:
                append_env(env_path, var, pw)
                created.append(f"{var} (.env)")
        stmts.append(f"ALTER ROLE {role} LOGIN PASSWORD {lit(scram_verifier(pw))};")
    c = find_container(a.container)
    exists = set(psql(c, "SELECT rolname FROM pg_roles WHERE rolname LIKE 'hr360\\_%';").split())
    missing = [r for r, _ in keys if r not in exists]
    if missing:
        raise SystemExit("Roller yok: " + ", ".join(missing) + " — önce `scripts/db-roles.sh grants`.")
    # Parolalar psql'e stdin ile, SCRAM doğrulayıcısı olarak gider (düz parola sunucu günlüğüne düşmez).
    psql(c, "SET log_statement = 'none';\n" + "\n".join(stmts))
    print(f"{len(stmts)} role giriş izni ve parola verildi.")
    if created:
        print("Yeni parola üretildi (değerler yazdırılmaz): " + ", ".join(created))
    return 0


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    sub = ap.add_subparsers(dest="cmd", required=True)
    g = sub.add_parser("generate")
    g.add_argument("--sql", default="deploy/postgres/roles.sql")
    g.add_argument("--review", default="deploy/postgres/roles-review.md")
    g.add_argument("--json")
    c = sub.add_parser("check")
    c.add_argument("--container")
    c.add_argument("--service", action="append")
    c.add_argument("--no-probe", dest="probe", action="store_false")
    p = sub.add_parser("passwords")
    p.add_argument("--container")
    p.add_argument("--service", action="append")
    p.add_argument("--secrets-dir")
    p.add_argument("--env-file", default=".env")
    m = sub.add_parser("services", help="servis anahtarlarını listeler (betikler için)")
    m.set_defaults(fn=lambda a: print("\n".join(f"{k} {role_of(k)} {d}" for d, k in SERVICES.items())) or 0)
    g.set_defaults(fn=cmd_generate)
    c.set_defaults(fn=cmd_check)
    p.set_defaults(fn=cmd_passwords)
    a = ap.parse_args()
    sys.exit(a.fn(a) or 0)


if __name__ == "__main__":
    main()

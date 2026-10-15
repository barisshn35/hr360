#!/usr/bin/env python3
"""G24: liste sorgularının indeks kullanımını EXPLAIN (ANALYZE) ile gösterir.

Demo kiracısına toplu veri YAZILMAZ. Sentetik satırlar tek bir işlem (transaction) içinde,
ayrı bir TEST kiracısına ('test-perf-bench') eklenir, planlar alınır ve işlem GERİ ALINIR
(ROLLBACK) - veritabanında iz kalmaz. Diğer oturumlar işlenmemiş satırları görmez.

Kullanım:  python3 tests/perf/bench_lists.py [--employees 20000] [--leaves 200000]
Çıktı: her sorgu için plan (Index Scan / Bitmap Index Scan beklenir) ve süre.
"""
import argparse
import os
import subprocess
import sys

ROOT = os.path.join(os.path.dirname(__file__), "..", "..")
T = "test-perf-bench"

ap = argparse.ArgumentParser()
ap.add_argument("--employees", type=int, default=20000)
ap.add_argument("--leaves", type=int, default=200000)
ap.add_argument("--departments", type=int, default=2000)
ap.add_argument("--reviews", type=int, default=60000)
args = ap.parse_args()

E, L, D, R = args.employees, args.leaves, args.departments, args.reviews

QUERIES = [
    ("Çalışanlar: 3. sayfa, ada göre (20'lik)",
     f"""SELECT e."Id", e."FirstName", e."LastName" FROM employee_employees e
         WHERE e."TenantSlug" = '{T}' ORDER BY e."FirstName", e."LastName", e."Id" LIMIT 20 OFFSET 40"""),
    ("Çalışanlar: toplam (COUNT) durum filtresiyle",
     f"""SELECT count(*) FROM employee_employees e WHERE e."TenantSlug" = '{T}' AND e."Status" = 'OnLeave'"""),
    ("Çalışanlar: işe giriş tarihine göre azalan",
     f"""SELECT e."Id" FROM employee_employees e WHERE e."TenantSlug" = '{T}' ORDER BY e."HireDate" DESC, e."Id" LIMIT 20"""),
    ("Departman üyeleri (güncel atama)",
     f"""SELECT a."EmployeeId" FROM employee_assignments a WHERE a."TenantSlug" = '{T}'
         AND a."DepartmentId" = (SELECT "Id" FROM organization_departments WHERE "TenantSlug" = '{T}' ORDER BY "Name" LIMIT 1)
         AND a."EffectiveTo" IS NULL"""),
    ("İzin talepleri: en yeni önce, 1. sayfa",
     f"""SELECT r."Id" FROM leave_requests r WHERE r."TenantSlug" = '{T}' ORDER BY r."CreatedAt" DESC, r."Id" LIMIT 10"""),
    ("İzin talepleri: durum = Submitted, en yeni önce",
     f"""SELECT r."Id" FROM leave_requests r WHERE r."TenantSlug" = '{T}' AND r."Status" = 'Submitted'
         ORDER BY r."CreatedAt" DESC, r."Id" LIMIT 10"""),
    ("İzin talepleri: bir çalışanınkiler (çalışan görünümü)",
     f"""SELECT r."Id" FROM leave_requests r WHERE r."TenantSlug" = '{T}'
         AND r."EmployeeId" = (SELECT "Id" FROM employee_employees WHERE "TenantSlug" = '{T}' ORDER BY "Id" LIMIT 1)
         ORDER BY r."CreatedAt" DESC, r."Id" LIMIT 10"""),
    # Takvim görünümü: bu ay ile çakışan izinler (sentetik veri 2024-01 .. 2026-09 arası).
    ("İzin talepleri: bu ayla çakışan (takvim)",
     f"""SELECT r."Id" FROM leave_requests r WHERE r."TenantSlug" = '{T}'
         AND r."StartDate" <= DATE '2026-09-30' AND r."EndDate" >= DATE '2026-09-01'"""),
    ("Departmanlar: ada göre 1. sayfa",
     f"""SELECT d."Id", d."Name" FROM organization_departments d WHERE d."TenantSlug" = '{T}' ORDER BY d."Name", d."Id" LIMIT 50"""),
    ("Değerlendirmeler: en yeni önce",
     f"""SELECT v."Id" FROM performance_reviews v WHERE v."TenantSlug" = '{T}' ORDER BY v."CreatedAt" DESC, v."Id" LIMIT 20"""),
    ("Değerlendirmeler: bir çalışan hakkında",
     f"""SELECT v."Id" FROM performance_reviews v WHERE v."TenantSlug" = '{T}'
         AND v."EmployeeId" = (SELECT "Id" FROM employee_employees WHERE "TenantSlug" = '{T}' ORDER BY "Id" LIMIT 1)
         ORDER BY v."CreatedAt" DESC LIMIT 20"""),
    ("Hedefler: bir çalışanınkiler",
     f"""SELECT g."Id" FROM performance_goals g WHERE g."TenantSlug" = '{T}'
         AND g."EmployeeId" = (SELECT "Id" FROM employee_employees WHERE "TenantSlug" = '{T}' ORDER BY "Id" LIMIT 1)
         ORDER BY g."CreatedAt" DESC LIMIT 20"""),
]

SETUP = f"""
\\set ON_ERROR_STOP 1
\\pset pager off
BEGIN;
SET LOCAL synchronous_commit = off;
INSERT INTO organization_companies ("Id","TenantSlug","Name","CreatedAt")
  VALUES ('00000000-0000-4000-8000-00000000c0de','{T}','TEST Bench A.Ş.',now());
INSERT INTO organization_departments ("Id","TenantSlug","Name","CompanyId","CreatedAt")
  SELECT gen_random_uuid(),'{T}','TEST Dept '||lpad(i::text,5,'0'),'00000000-0000-4000-8000-00000000c0de',now()
  FROM generate_series(1,{D}) i;
CREATE TEMP TABLE bench_dept ON COMMIT DROP AS
  SELECT "Id", row_number() OVER (ORDER BY "Id") rn FROM organization_departments WHERE "TenantSlug"='{T}';
INSERT INTO employee_employees ("Id","TenantSlug","FirstName","LastName","Email","HireDate","Status","CreatedAt")
  SELECT gen_random_uuid(),'{T}','TEST'||(i % 997),'Bench'||i,'bench'||i||'@test.invalid',
         DATE '2010-01-01' + (i % 5000), (ARRAY['Active','Active','Active','OnLeave','Terminated'])[1 + i % 5], now()
  FROM generate_series(1,{E}) i;
CREATE TEMP TABLE bench_emp ON COMMIT DROP AS
  SELECT "Id", row_number() OVER (ORDER BY "Id") rn FROM employee_employees WHERE "TenantSlug"='{T}';
INSERT INTO employee_assignments ("Id","TenantSlug","EmployeeId","DepartmentId","EffectiveFrom","CreatedAt")
  SELECT gen_random_uuid(),'{T}',e."Id",d."Id",DATE '2020-01-01',now()
  FROM bench_emp e JOIN bench_dept d ON d.rn = 1 + e.rn % {D};
INSERT INTO leave_requests ("Id","TenantSlug","EmployeeId","Type","StartDate","EndDate","Days","Status","CreatedAt")
  SELECT gen_random_uuid(),'{T}',e."Id",(ARRAY['Annual','Sick','Unpaid','Excuse'])[1 + i % 4],
         DATE '2024-01-01' + (i % 1000), DATE '2024-01-01' + (i % 1000) + (i % 5), 1 + i % 5,
         (ARRAY['Submitted','Approved','Approved','Approved','Rejected','Cancelled'])[1 + i % 6],
         now() - (i || ' minutes')::interval
  FROM generate_series(1,{L}) i JOIN bench_emp e ON e.rn = 1 + i % {E};
INSERT INTO performance_cycles ("Id","TenantSlug","Name","Year","Period","StartDate","EndDate","Status","CreatedAt")
  VALUES ('00000000-0000-4000-8000-0000000c7c1e','{T}','TEST Bench',2026,'Annual',DATE '2026-01-01',DATE '2026-12-31','Active',now());
INSERT INTO performance_reviews ("Id","TenantSlug","CycleId","EmployeeId","ReviewerEmployeeId","Type","CreatedAt")
  SELECT gen_random_uuid(),'{T}','00000000-0000-4000-8000-0000000c7c1e',e."Id",e."Id",'Self',now() - (i || ' minutes')::interval
  FROM generate_series(1,{R}) i JOIN bench_emp e ON e.rn = 1 + i % {E};
INSERT INTO performance_goals ("Id","TenantSlug","CycleId","EmployeeId","Title","Weight","Status","CreatedAt")
  SELECT gen_random_uuid(),'{T}','00000000-0000-4000-8000-0000000c7c1e',e."Id",'TEST hedef '||i,100,'Active',now() - (i || ' minutes')::interval
  FROM generate_series(1,{R}) i JOIN bench_emp e ON e.rn = 1 + i % {E};
ANALYZE employee_employees; ANALYZE employee_assignments; ANALYZE leave_requests;
ANALYZE organization_departments; ANALYZE performance_reviews; ANALYZE performance_goals;
"""

script = [SETUP]
for title, sql in QUERIES:
    script.append(f"\\echo '=== {title}'")
    script.append(f"EXPLAIN (ANALYZE, COSTS OFF, TIMING ON, SUMMARY ON) {' '.join(sql.split())};")
script.append("ROLLBACK;")
# Geri alınan işlemin istatistik izleri kalmasın: gerçek verilerle yeniden ölç.
script.append("ANALYZE employee_employees; ANALYZE employee_assignments; ANALYZE leave_requests;"
              " ANALYZE organization_departments; ANALYZE performance_reviews; ANALYZE performance_goals;")

print(f"Sentetik TEST kiracısı '{T}': {E} çalışan, {L} izin talebi, {D} departman, {R} değerlendirme/hedef "
      "(tek işlemde, sonunda ROLLBACK)\n", flush=True)
out = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-q", "-U", "hr360admin", "-d", "hr360_operational"],
                     input="\n".join(script), capture_output=True, text=True, cwd=ROOT)
print(out.stdout)
if out.returncode != 0:
    print(out.stderr, file=sys.stderr)
    sys.exit(1)

seq = [l for l in out.stdout.splitlines() if "Seq Scan on" in l and "bench_" not in l]
left = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-Atq", "-U", "hr360admin", "-d", "hr360_operational",
                       "-c", f"""SELECT count(*) FROM employee_employees WHERE "TenantSlug"='{T}'"""],
                      capture_output=True, text=True, cwd=ROOT).stdout.strip()
print(f"Ana tablolarda sıralı tarama (Seq Scan) sayısı: {len(seq)}")
for l in seq:
    print("   ", l.strip())
print(f"Geri alma sonrası TEST kiracısında kalan çalışan: {left}")
sys.exit(1 if seq or left != "0" else 0)

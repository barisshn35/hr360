"""Demo şirketinin test kişilerinin çalışan kimlikleri.

Kimlikler kurulumdan kuruluma değişir (temiz kurulumda tests/support/seed_demo.py üretir); bu yüzden
testlerde sabit yazılmaz, e-posta adresinden veritabanında çözülür.
"""

import os
import subprocess

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))

EMAILS = {
    "AYSE": "ayse.yilmaz@demo.hr360",
    "MEHMET": "mehmet.demir@demo.hr360",
    "ZEYNEP": "zeynep.kaya@demo.hr360",
    "ELIF": "elif.sahin@demo.hr360",
}


def _psql(sql, db="hr360_operational"):
    out = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", db, "-AtF", "|", "-c", sql],
                         cwd=ROOT, capture_output=True, text=True)
    return out.stdout.strip()


def _lookup():
    emails = ",".join(f"'{e}'" for e in EMAILS.values())
    sql = f"""SELECT "Email", "Id" FROM employee_employees WHERE "TenantSlug" = 'demo' AND "Email" IN ({emails})"""
    by_email = dict(line.split("|", 1) for line in _psql(sql).split() if "|" in line)
    missing = [k for k, e in EMAILS.items() if e not in by_email]
    if missing:
        raise SystemExit(f"Demo çalışanları bulunamadı ({', '.join(missing)}): önce tests/support/seed_demo.py çalıştırın.")
    return {k: by_email[e] for k, e in EMAILS.items()}


_IDS = _lookup()
AYSE = _IDS["AYSE"]
MEHMET = _IDS["MEHMET"]
ZEYNEP = _IDS["ZEYNEP"]
ELIF = _IDS["ELIF"]

# Mühendislik departmanı (Ayşe'nin departmanı, başı Mehmet; seed_demo.py oluşturur).
ENG = _psql("""SELECT "Id" FROM organization_departments WHERE "TenantSlug" = 'demo' AND "Name" = 'Mühendislik' LIMIT 1""")


def kc_user_id(email):
    """Demo kişisinin Keycloak kullanıcı kimliği (hr360 realm'i)."""
    return _psql(f"""SELECT u.id FROM user_entity u JOIN realm r ON r.id = u.realm_id
                     WHERE r.name = 'hr360' AND lower(u.email) = lower('{email}')""", db="keycloak")

#!/usr/bin/env python3
"""scripts/db-roles.py birim testleri (yalnızca standart kütüphane, veritabanı gerekmez).

    python3 tests/support/test_db_roles.py
"""
import importlib.util
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("db_roles", ROOT / "scripts" / "db-roles.py")
dr = importlib.util.module_from_spec(spec)
spec.loader.exec_module(dr)


class LexerTests(unittest.TestCase):
    def strings(self, src):
        return [s for _, s in dr.cs_strings(src)]

    def test_regular_verbatim_raw_and_comments(self):
        src = '''
        // yorum: leave_requests
        /* blok: leave_balances */
        var a = "SELECT 1 FROM leave_requests";
        var b = @"DELETE FROM ""x"" ";
        var c = """
            UPDATE leave_balances SET "Used" = 1
            """;
        var d = $"INSERT INTO audit_log VALUES ({(x ? "a" : "b")})";
        char q = '"';
        '''
        s = self.strings(src)
        self.assertIn("SELECT 1 FROM leave_requests", s)
        self.assertIn('DELETE FROM "x" ', s)
        self.assertTrue(any("UPDATE leave_balances" in x for x in s))
        self.assertTrue(any(x.startswith("INSERT INTO audit_log") for x in s))
        self.assertFalse(any("yorum" in x or "blok" in x for x in s))


class MappingTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.mapping, cls.universe, cls.views, cls.unused = dr.build_mapping()

    def test_all_services_present(self):
        self.assertEqual(set(self.mapping), set(dr.SERVICES))

    def test_audit_log_never_update_delete(self):
        for d, r in self.mapping.items():
            p = set(r["privs"].get("audit_log", []))
            self.assertFalse(p & {"UPDATE", "DELETE", "TRUNCATE"}, d)

    def test_tenant_gate_tables_readable(self):
        for d, r in self.mapping.items():
            self.assertIn("SELECT", r["privs"].get("platform_tenants", []), d)

    def test_owned_tables_writable(self):
        self.assertEqual(self.mapping["leave-service"]["privs"]["leave_requests"], ["SELECT", "INSERT", "UPDATE", "DELETE"])
        self.assertNotIn("INSERT", self.mapping["leave-service"]["privs"].get("employee_employees", []))

    def test_trigger_dependency(self):
        # expense_documents silinince tetikleyici governance_signatures satırlarını siler (invoker yetkisi).
        self.assertIn("DELETE", self.mapping["expense-service"]["privs"].get("governance_signatures", []))

    def test_generated_files_up_to_date(self):
        sql = (ROOT / "deploy" / "postgres" / "roles.sql").read_text(encoding="utf-8")
        self.assertEqual(sql, dr.render_sql(self.mapping),
                         "deploy/postgres/roles.sql güncel değil: scripts/db-roles.sh generate")

    def test_scram_verifier_format(self):
        v = dr.scram_verifier("deneme")
        self.assertTrue(v.startswith("SCRAM-SHA-256$4096:"))
        self.assertNotIn("deneme", v)


if __name__ == "__main__":
    unittest.main(verbosity=1)

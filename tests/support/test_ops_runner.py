#!/usr/bin/env python3
"""scripts/ops-runner.py birim testleri (yalnızca standart kütüphane; docker/veritabanı gerekmez).

    python3 tests/support/test_ops_runner.py
"""
import importlib.util
import tempfile
import unittest
from pathlib import Path
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("ops_runner", ROOT / "scripts" / "ops-runner.py")
r = importlib.util.module_from_spec(spec)
spec.loader.exec_module(r)

STATUS_ON = """durum=acik
wal_dosya=42
wal_boyut=180M
temel_boyut=96M
temel=20261018T030012Z bitis=2026-10-18T03:01:40Z
temel=20261019T030010Z bitis=2026-10-19T03:01:22Z
eski_veri=pre-restore-20261017T101500Z 1.2G
son_arsiv=00000001000000000000002A 2026-10-19T11:58:03Z
hata_sayisi=3 son_hata=00000001000000000000002B
"""


class PitrCommandTests(unittest.TestCase):
    def test_izinli_komutlar(self):
        self.assertEqual(r.command("pitr-enable", {}), ["scripts/pitr.sh", "enable"])
        self.assertEqual(r.command("pitr-disable", {}), ["scripts/pitr.sh", "disable"])
        self.assertEqual(r.command("pitr-disable", {"purge": True}), ["scripts/pitr.sh", "disable", "--purge"])
        # yalnızca gerçek true; "true" metni ya da 1 arşivi silmez
        self.assertEqual(r.command("pitr-disable", {"purge": "true"}), ["scripts/pitr.sh", "disable"])
        self.assertEqual(r.command("pitr-disable", {"purge": 1}), ["scripts/pitr.sh", "disable"])
        self.assertEqual(r.command("pitr-cleanup", {"x": "; rm -rf /"}), ["scripts/pitr.sh", "cleanup"])
        self.assertEqual(r.command("pitr-restore", {"target": "2026-10-19 14:05"}),
                         ["scripts/pitr.sh", "restore", "--to", "2026-10-19 14:05", "--yes"])

    def test_gecersiz_hedef_reddedilir(self):
        bad = [None, "", 202610191405, "2026-10-19", "2026-10-19 14:05:00", "2026-10-19T14:05", "2026-10-19  14:05",
               "2026-10-19 14:05\n", " 2026-10-19 14:05", "2026-13-01 10:00", "2026-02-30 10:00", "2026-10-19 24:00",
               "2026-10-19 14:60", "2026-10-19 14:05; reboot", "2026-10-19 14:05 --yes", "$(id) 2026-10-19 14:05",
               "2026-10-19 14:05`id`", "２０２６-10-19 14:05", ["2026-10-19 14:05"]]
        for t in bad:
            with self.subTest(t=t), self.assertRaises(ValueError):
                r.command("pitr-restore", {"target": t})
        with self.assertRaises(ValueError):
            r.command("pitr-restore", {})

    def test_bilinmeyen_tur(self):
        for k in ("pitr", "pitr-base", "pitr-status", "PITR-ENABLE"):
            with self.subTest(k=k), self.assertRaises(ValueError):
                r.command(k, {})


class PitrStatusTests(unittest.TestCase):
    def test_acik_tam_cikti(self):
        p = r.parse_pitr(0, STATUS_ON)
        self.assertEqual(p["state"], "on")
        self.assertTrue(p["enabled"])
        self.assertEqual(p["walFiles"], 42)
        self.assertEqual(p["walSize"], "180M")
        self.assertEqual(p["baseSize"], "96M")
        self.assertEqual([b["name"] for b in p["bases"]], ["20261018T030012Z", "20261019T030010Z"])
        self.assertEqual(p["oldestRecoverable"], "2026-10-18T03:01:40Z")
        self.assertEqual(p["lastArchivedWal"], "00000001000000000000002A")
        self.assertEqual(p["lastArchivedAt"], "2026-10-19T11:58:03Z")
        self.assertEqual(p["failedCount"], 3)
        self.assertEqual(p["lastFailedWal"], "00000001000000000000002B")
        self.assertEqual(p["preRestore"], [{"name": "pre-restore-20261017T101500Z", "size": "1.2G"}])

    def test_kapali_arsiv_yok(self):
        p = r.parse_pitr(0, "durum=kapali\nson_arsiv=- -\nhata_sayisi=0\n")
        self.assertEqual(p["state"], "off")
        self.assertFalse(p["enabled"])
        self.assertIsNone(p["lastArchivedAt"])
        self.assertIsNone(p["lastArchivedWal"])
        self.assertIsNone(p["oldestRecoverable"])
        self.assertEqual(p["failedCount"], 0)
        self.assertIsNone(p["lastFailedWal"])
        self.assertEqual(p["bases"], [])

    def test_hazir_degil(self):
        p = r.parse_pitr(0, "durum=hazir-degil (archive_mode kapali; postgres yeniden baslatilmali)\n")
        self.assertEqual(p["state"], "not-ready")
        self.assertFalse(p["enabled"])

    def test_postgres_calismiyor_ya_da_bozuk_cikti(self):
        self.assertEqual(r.parse_pitr(1, "HATA: postgres calismiyor")["state"], "unknown")
        p = r.parse_pitr(0, "beklenmeyen\ndurum=tuhaf\ntemel=bozuk\nhata_sayisi=x\n")
        self.assertEqual(p["state"], "unknown")
        self.assertEqual(p["bases"], [])
        self.assertEqual(p["failedCount"], 0)

    def test_betik_yoksa_unknown(self):
        with tempfile.TemporaryDirectory() as d, mock.patch.object(r, "ROOT", d):
            self.assertEqual(r.pitr_status()["state"], "unknown")


MODULES_JSON = (
    '{"enabled":["timeshift","compensation","expense"],'
    '"all":["recruitment","onboarding","timeshift","performance","learning","compensation","ml","expense"],'
    '"details":[{"id":"ml","name":"Yapay zekâ","memoryMb":1792,"services":["ml-inference","mlflow"]},'
    '{"id":"expense","name":"Masraf ve seyahat (yalnız ekranlar)","memoryMb":0,"services":[]},'
    '{"id":"bilinmeyen","name":"x","memoryMb":1,"services":[]}]}'
)


class ModulesCommandTests(unittest.TestCase):
    def test_secim_tablo_sirasiyla_yazilir(self):
        self.assertEqual(r.command("modules-set", {"modules": ["compensation", "timeshift", "expense"]}),
                         ["scripts/modules.sh", "set", "timeshift,compensation,expense"])
        self.assertEqual(r.command("modules-set", {"modules": ["ml"]}), ["scripts/modules.sh", "set", "ml"])

    def test_hepsi_all_bos_secim_yalnizca_cekirdek(self):
        self.assertEqual(r.command("modules-set", {"modules": list(reversed(r.MODULES))}), ["scripts/modules.sh", "set", "all"])
        self.assertEqual(r.command("modules-set", {"modules": []}), ["scripts/modules.sh", "disable", *r.MODULES])

    def test_gecersiz_secim_reddedilir(self):
        bad = [None, "timeshift", "timeshift,ml", "all", {"timeshift": True}, ["timeshift", "timeshift"], ["bordro"],
               ["employee"], ["Timeshift"], ["timeshift "], ["timeshift,ml"], ["all"], ["ml; reboot"], ["$(id)"],
               ["--no-start"], ["../ml"], [1], [None], [["ml"]], ["ml\n"], ["ｍｌ"]]
        for m in bad:
            with self.subTest(m=m), self.assertRaises(ValueError):
                r.command("modules-set", {"modules": m})
        with self.assertRaises(ValueError):
            r.command("modules-set", {})

    def test_sirket_kipi(self):
        self.assertEqual(r.command("tenancy-multi", {"x": "; rm -rf /"}), ["scripts/tenancy.sh", "multi"])
        self.assertEqual(r.command("tenancy-single", {}), ["scripts/tenancy.sh", "single"])
        for k in ("tenancy", "tenancy-status", "modules", "modules-enable"):
            with self.subTest(k=k), self.assertRaises(ValueError):
                r.command(k, {})


class ModulesStatusTests(unittest.TestCase):
    def test_json_ozeti(self):
        m = r.parse_modules(MODULES_JSON)
        self.assertEqual(m["enabled"], ["timeshift", "compensation", "expense"])
        self.assertEqual(m["all"], list(r.MODULES))
        self.assertEqual([d["id"] for d in m["details"]], ["ml", "expense"])  # bilinmeyen atılır
        self.assertEqual(m["details"][0], {"id": "ml", "name": "Yapay zekâ", "memoryMb": 1792, "services": ["ml-inference", "mlflow"]})
        self.assertEqual(m["details"][1]["services"], [])

    def test_bozuk_cikti(self):
        for out in (None, "", "recruitment=on", "[]", '{"all":"x","enabled":[]}', '{"enabled":[]}'):
            with self.subTest(out=out):
                self.assertIsNone(r.parse_modules(out))
        m = r.parse_modules('{"enabled":["ml","bilinmeyen"],"all":["ml","bilinmeyen"],"details":[{"id":"ml","memoryMb":"x"}]}')
        self.assertEqual(m["enabled"], ["ml"])
        self.assertEqual(m["details"], [{"id": "ml", "name": "ml", "memoryMb": None, "services": []}])

    def test_sirket_kipi_durumu(self):
        self.assertEqual(r.parse_tenancy("tenancy=single\ntenants=1\n"), {"mode": "single", "tenants": 1})
        self.assertEqual(r.parse_tenancy("tenancy=multi\ntenants=?\n"), {"mode": "multi", "tenants": None})
        self.assertEqual(r.parse_tenancy("tenancy=tuhaf\ntenants=-1\n"), {"mode": "unknown", "tenants": None})
        self.assertEqual(r.parse_tenancy("tenants=２\n"), {"mode": "unknown", "tenants": None})
        self.assertIsNone(r.parse_tenancy(None))

    def test_betikler_yoksa_alanlar_yok(self):
        with tempfile.TemporaryDirectory() as d, mock.patch.object(r, "ROOT", d):
            self.assertIsNone(r._run_script(["scripts/modules.sh", "json"]))
            self.assertIsNone(r._run_script(["scripts/tenancy.sh", "status"]))

    def test_betik_cikis_kodu_hatasi_none(self):
        with tempfile.TemporaryDirectory() as d, mock.patch.object(r, "ROOT", d):
            Path(d, "scripts").mkdir()
            sh = Path(d, "scripts", "tenancy.sh")
            sh.write_text("#!/bin/sh\necho tenancy=multi\nexit 3\n")
            sh.chmod(0o755)
            self.assertIsNone(r._run_script(["scripts/tenancy.sh", "status"]))
            sh.write_text("#!/bin/sh\necho tenancy=multi\necho tenants=4\n")
            self.assertEqual(r.parse_tenancy(r._run_script(["scripts/tenancy.sh", "status"])), {"mode": "multi", "tenants": 4})


class AfterRestoreTests(unittest.TestCase):
    def test_geri_donus_sonrasi_kuyruk_duzeltilir(self):
        job = {"id": "j1", "kind": "pitr-restore", "args": {"target": "2026-10-19 14:05"}, "requestedAt": "2026-10-19T11:10:00+00:00"}
        jobs = iter([job, None])
        with mock.patch.object(r, "claim", side_effect=lambda: next(jobs)), \
             mock.patch.object(r, "run") as run, mock.patch.object(r, "reconcile") as rec, \
             mock.patch.object(r, "save_snapshot"), mock.patch.object(r, "psql") as psql, \
             tempfile.TemporaryDirectory() as d, mock.patch.object(r, "LOGS", d):
            self.assertEqual(r.main(), 0)
        run.assert_called_once_with(job)
        self.assertEqual(rec.call_count, 2)  # tur başı + geri dönüş sonrası
        sql, kw = psql.call_args.args[0], psql.call_args.kwargs
        self.assertIn("'Cancelled'", sql)
        self.assertEqual(kw, {"reqat": job["requestedAt"]})

    def test_siradan_iste_kuyruga_dokunulmaz(self):
        job = {"id": "j2", "kind": "pitr-enable", "args": {}, "requestedAt": "2026-10-19T11:10:00+00:00"}
        jobs = iter([job, None])
        with mock.patch.object(r, "claim", side_effect=lambda: next(jobs)), \
             mock.patch.object(r, "run"), mock.patch.object(r, "reconcile") as rec, \
             mock.patch.object(r, "save_snapshot"), mock.patch.object(r, "psql") as psql, \
             tempfile.TemporaryDirectory() as d, mock.patch.object(r, "LOGS", d):
            self.assertEqual(r.main(), 0)
        self.assertEqual(rec.call_count, 1)
        psql.assert_not_called()


class MonitoringTests(unittest.TestCase):
    SLACK = "https://hooks.slack.com/services/T000/B000/XXXXSECRET"
    TEAMS = "https://prod-00.westeurope.logic.azure.com:443/workflows/abc/triggers/manual/paths/invoke?api-version=2016-06-01&sp=%2Ftriggers&sig=SECRET"

    def test_izinli_komutlar(self):
        self.assertEqual(r.command("monitoring-enable", {"x": "; id"}), ["scripts/monitoring.sh", "enable"])
        self.assertEqual(r.command("monitoring-disable", {}), ["scripts/monitoring.sh", "disable"])
        self.assertEqual(r.command("monitoring-disable", {"purge": True}), ["scripts/monitoring.sh", "purge"])
        self.assertEqual(r.command("monitoring-disable", {"purge": "true"}), ["scripts/monitoring.sh", "disable"])
        self.assertEqual(r.command("monitoring-test", {}), ["scripts/monitoring.sh", "alerts", "test"])
        self.assertEqual(r.command("monitoring-alerts", {"channel": "email", "value": "ops@a.com,it@b.com.tr"}),
                         ["scripts/monitoring.sh", "alerts", "email", "ops@a.com,it@b.com.tr"])
        self.assertEqual(r.command("monitoring-alerts", {"channel": "slack", "value": self.SLACK}),
                         ["scripts/monitoring.sh", "alerts", "slack", self.SLACK])
        self.assertEqual(r.command("monitoring-alerts", {"channel": "teams", "value": self.TEAMS})[3], self.TEAMS)
        for off in (None, ""):
            self.assertEqual(r.command("monitoring-alerts", {"channel": "teams", "value": off}), ["scripts/monitoring.sh", "alerts", "teams", "off"])
        self.assertEqual(r.command("monitoring-tracing", {"on": True, "ratio": "0.25"}), ["scripts/monitoring.sh", "tracing", "on", "0.25"])
        self.assertEqual(r.command("monitoring-tracing", {"on": "true", "ratio": "1"}), ["scripts/monitoring.sh", "tracing", "off"])

    def test_gecersiz_degerler_reddedilir(self):
        bad = [("email", "ops"), ("email", "a@b.com, c@d.com"), ("email", "a@b.com\nx@y.com"), ("email", ",".join(["a@b.co"] * 11)),
               ("email", "a'b@c.com"), ("email", "a@b.com;id"), ("email", 5), ("slack", "http://hooks.slack.com/x/y/zzzz"),
               ("slack", "https://h.com/a b"), ("slack", "https://h.com/#frag"), ("teams", "https://x.com/" + "a" * 600),
               ("teams", "https://x.com/abcdefgh\n"), ("slack", "https://x.com/a${HOME}b"), ("slack", "https://x.com/a\\b"), ("slack", "https://x.com/`id`"), ("sms", "https://x.com/abcdefgh"), (None, "a@b.com")]
        for ch, v in bad:
            with self.subTest(ch=ch, v=v), self.assertRaises(ValueError):
                r.command("monitoring-alerts", {"channel": ch, "value": v})
        for ratio in (None, 0.5, "2", "-1", "0.5; id", "1.5", ".5", "0.12345"):
            with self.subTest(ratio=ratio), self.assertRaises(ValueError):
                r.command("monitoring-tracing", {"on": True, "ratio": ratio})

    def test_webhook_kayitlarda_maskelenir(self):
        job = {"id": "m1", "kind": "monitoring-alerts", "args": {"channel": "teams", "value": self.TEAMS}, "requestedAt": "x"}
        recorded = []
        with tempfile.TemporaryDirectory() as d, mock.patch.object(r, "LOGS", d), \
             mock.patch.object(r, "record", side_effect=lambda j: recorded.append(dict(j))), \
             mock.patch.object(r.subprocess, "run", return_value=mock.Mock(returncode=0)) as run:
            r.run(job)
            self.assertEqual(run.call_args.args[0][3], self.TEAMS)  # komut gerçek adresi alır
            log = (Path(d) / "m1.log").read_text()
        self.assertNotIn("SECRET", log)
        self.assertNotIn("SECRET", str(recorded))
        self.assertEqual(recorded[-1]["args"]["value"], "https://prod-00.westeurope.logic.azure.com:443/…")

    def test_reddedilen_iste_de_maskelenir(self):
        job = {"id": "m2", "kind": "monitoring-alerts", "args": {"channel": "slack", "value": "https://x.com/SECRET token"}, "requestedAt": "x"}
        recorded = []
        with mock.patch.object(r, "record", side_effect=lambda j: recorded.append(dict(j))):
            r.run(job)
        self.assertEqual(recorded[-1]["status"], "Failed")
        self.assertNotIn("SECRET", str(recorded))

    def test_durum_sir_icermez(self):
        env = {"COMPOSE_PROFILES": "mailer,monitoring", "ALERT_EMAIL_TO": "ops@a.com, it@b.com", "ALERT_SLACK_WEBHOOK_URL": self.SLACK,
               "SMTP_HOST": "smtp.office365.com", "GRAFANA_SSO_ENABLED": "true", "GRAFANA_OAUTH_CLIENT_SECRET": "S3CRET",
               "GRAFANA_ADMIN_PASSWORD": "PW"}
        with mock.patch.object(r, "sh", return_value=(0, "grafana\nprometheus\nmailpit\n")), \
             tempfile.TemporaryDirectory() as d, mock.patch.object(r, "ROOT", d):
            m = r.monitoring_status(env)
        self.assertEqual(m["enabled"], True)
        self.assertEqual(m["running"], 2)
        self.assertEqual(m["alerts"], {"email": ["ops@a.com", "it@b.com"], "slack": "https://hooks.slack.com/…", "teams": None})
        self.assertTrue(m["sso"])
        self.assertEqual(m["tracing"], {"on": False, "ratio": None})
        for secret in ("SECRET", "S3CRET", "PW\""):
            self.assertNotIn(secret, str(m))

    def test_kapali_izleme(self):
        with mock.patch.object(r, "sh") as sh:
            m = r.monitoring_status({"COMPOSE_PROFILES": "mailer", "SMTP_HOST": "mailpit"})
        sh.assert_not_called()
        self.assertEqual((m["enabled"], m["running"], m["sso"]), (False, None, False))

    def test_izleme_orani_dosyadan(self):
        with tempfile.TemporaryDirectory() as d, mock.patch.object(r, "ROOT", d):
            (Path(d) / "deploy" / "monitoring").mkdir(parents=True)
            (Path(d) / "deploy" / "monitoring" / "tracing.env").write_text("OTEL_EXPORTER_OTLP_ENDPOINT=x\nOTEL_TRACES_SAMPLER_ARG=0.2\n")
            m = r.monitoring_status({})
        self.assertEqual(m["tracing"], {"on": True, "ratio": "0.2"})


if __name__ == "__main__":
    unittest.main()

#!/usr/bin/env python3
"""G25: OpenTelemetry dağıtık izleme + KVKK maskeleme + iş metrikleri.

Ön koşul: "monitoring" profilinde otel-collector ve tempo çalışıyor ve ortak outbox'u
paylaşan dört servis (employee, leave, expense, workflow) OTEL_EXPORTER_OTLP_ENDPOINT ile başlatılmış:
    docker compose --profile monitoring up -d otel-collector tempo
    docker compose -f docker-compose.yml -f deploy/testing/chat-mock.yml up -d \
        employee-service leave-service expense-service workflow-service
Bunlar yoksa test açık bir mesajla ATLANIR (FAILS: 0).

Doğrulananlar:
  - İstemcinin gönderdiği W3C traceparent ile istek izi Tempo'ya ulaşır; span adı ve
    http.route rota şablonudur; yol (url.path) içindeki GUID maskelenir.
  - Sorgu dizesi, Authorization/Cookie/X-Device-Key/X-Follow-Up-Code/X-Internal-Token
    başlıkları, enduser.id hiçbir span'de yok; ham GUID / e-posta / JWT / TCKN / IBAN yok.
  - leave-service -> workflow-service HTTP çağrısı aynı izde (bağlam yayılımı).
  - Kafka: outbox yayını -> leave-service tüketimi aynı izde (traceparent başlığı).
  - İş metrikleri: hr360_leave_requests_total ve hr360_workflow_* artar, kişi/kiracı etiketi yok.
"""
import datetime as dt
import json
import os
import random
import re
import subprocess
import sys
import time
import urllib.parse

sys.path.insert(0, os.path.dirname(__file__))
from common import AYSE, FAIL, api, check, http, tok  # noqa: E402

ROOT = os.path.join(os.path.dirname(__file__), "..", "..")
FAIL.clear()
L = "/api/leave/leave-requests"
W = "/api/workflow/workflows"
REASON = "TEST-telemetry"

GUID_RE = re.compile(r"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")
EMAIL_RE = re.compile(r"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")
JWT_RE = re.compile(r"eyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}")
TCKN_RE = re.compile(r"(?<!\d)\d{11}(?!\d)")
IBAN_RE = re.compile(r"TR\d{2}\s?\d{4}")
FORBIDDEN_KEYS = re.compile(r"^(url\.query|http\.request\.header\..*|http\.response\.header\..*|enduser\..*|"
                            r"user_agent\.original|client\.address|db\.connection_string|db\.query\.parameter\..*)$")


def sh(*args):
    out = subprocess.run(list(args), capture_output=True, text=True, cwd=ROOT)
    return out.returncode, out.stdout


def internal_get(url):
    """hr360-net içinden (gateway konteyneri) GET; Tempo ve /metrics dışarıya açık değil."""
    code, out = sh("docker", "exec", "hr360-gateway-1", "wget", "-qO-", "-T", "10", url)
    return out if code == 0 else None


def tempo(path):
    out = internal_get("http://tempo:3200" + path)
    try:
        return json.loads(out) if out else None
    except ValueError:
        return None


def psql(sql):
    _, out = sh("docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", "hr360_operational", "-Atc", sql)
    return out.strip()


def skip(msg):
    print("ATLANDI: " + msg)
    print("FAILS: 0")
    sys.exit(0)


def env_of(container):
    code, out = sh("docker", "inspect", container, "--format", "{{json .Config.Env}}")
    return json.loads(out) if code == 0 and out.strip() else []


def new_trace():
    tid = "%032x" % random.getrandbits(128)
    return tid, f"00-{tid}-{'%016x' % random.getrandbits(64)}-01"


def spans_of(trace):
    """Tempo /api/traces yanıtını (servis, span, öznitelikler, olaylar) listesine çevirir."""
    out = []
    for b in (trace or {}).get("batches", []):
        res = {a["key"]: next(iter(a["value"].values()), None) for a in b.get("resource", {}).get("attributes", [])}
        for ss in b.get("scopeSpans", b.get("instrumentationLibrarySpans", [])):
            for sp in ss.get("spans", []):
                attrs = {a["key"]: next(iter(a["value"].values()), None) for a in sp.get("attributes", [])}
                events = [{a["key"]: next(iter(a["value"].values()), None) for a in e.get("attributes", [])} for e in sp.get("events", [])]
                out.append({"service": res.get("service.name"), "name": sp.get("name"), "kind": sp.get("kind"),
                            "attrs": attrs, "events": events, "resource": res,
                            "status": (sp.get("status") or {}).get("message", "")})
    return out


def wait_trace(tid, pred, timeout=60):
    end = time.time() + timeout
    spans = []
    while time.time() < end:
        spans = spans_of(tempo(f"/api/traces/{tid}"))
        if spans and pred(spans):
            return spans
        time.sleep(2)
    return spans


def pii_findings(spans):
    bad = []
    for s in spans:
        values = [("name", s["name"]), ("status", s["status"])] + list(s["attrs"].items()) + list(s["resource"].items())
        for e in s["events"]:
            values += list(e.items())
        for k, v in values:
            if FORBIDDEN_KEYS.match(str(k)):
                bad.append(f"{s['service']}:{s['name']} yasak öznitelik {k}")
            if not isinstance(v, str):
                continue
            for label, rx in (("GUID", GUID_RE), ("e-posta", EMAIL_RE), ("JWT", JWT_RE), ("TCKN", TCKN_RE), ("IBAN", IBAN_RE)):
                if rx.search(v):
                    bad.append(f"{s['service']}:{s['name']} {k} içinde ham {label}: {v[:120]}")
            if "Bearer " in v or "secret-telemetry" in v:
                bad.append(f"{s['service']}:{s['name']} {k} içinde jeton: {v[:120]}")
    return bad


def metric_value(text, name, **labels):
    total, found = 0.0, False
    for line in (text or "").splitlines():
        if not line.startswith(name):
            continue
        m = re.match(r"^([a-zA-Z_:][a-zA-Z0-9_:]*)(\{[^}]*\})?\s+([0-9.eE+-]+)", line)
        if not m or m.group(1) != name:
            continue
        lbl = dict(re.findall(r'(\w+)="([^"]*)"', m.group(2) or ""))
        if all(lbl.get(k) == v for k, v in labels.items()):
            total += float(m.group(3))
            found = True
    return total if found else None


def label_names(text, name):
    names = set()
    for line in (text or "").splitlines():
        m = re.match(r"^([a-zA-Z_:][a-zA-Z0-9_:]*)\{([^}]*)\}", line)
        if m and m.group(1) == name:
            names |= set(re.findall(r"(\w+)=", m.group(2)))
    return names


# ------------------------------------------------------------------ ön koşullar
if internal_get("http://tempo:3200/api/echo") is None:
    skip("Tempo erişilemiyor - monitoring profili kapalı ya da tempo çalışmıyor "
         "(docker compose --profile monitoring up -d otel-collector tempo).")
if internal_get("http://otel-collector:13133/") is None:
    skip("otel-collector çalışmıyor (docker compose --profile monitoring up -d otel-collector).")
for svc in ("hr360-employee-service-1", "hr360-leave-service-1", "hr360-expense-service-1", "hr360-workflow-service-1"):
    if not any(e.startswith("OTEL_EXPORTER_OTLP_ENDPOINT=") and e.split("=", 1)[1] for e in env_of(svc)):
        skip(f"{svc} izleme kapalı başlatılmış (deploy/testing/chat-mock.yml ile yeniden başlatın).")

leave_metrics_before = internal_get("http://leave-service:8080/metrics") or ""
wf_metrics_before = internal_get("http://workflow-service:8080/metrics") or ""

# ------------------------------------------------------------------ 1) PII içeren istek
t1, tp1 = new_trace()
fake_id = "8c7dd608-46e2-4bee-900f-6a175b21d3b2"
q = urllib.parse.urlencode({"email": "ayse.test@ornek.com", "tckn": "12345678901", "iban": "TR330006100519786457841326"})
code, _ = http("GET", f"{L}/{fake_id}?{q}", headers={
    "Authorization": "Bearer " + tok("ayse"), "traceparent": tp1,
    "Cookie": "sid=secret-telemetry-cookie", "X-Device-Key": "secret-telemetry-device",
    "X-Follow-Up-Code": "secret-telemetry-code", "X-Internal-Token": "secret-telemetry-internal"})
check("PII içeren istek yanıtlandı", code in (200, 403, 404), code)
spans1 = wait_trace(t1, lambda s: any(x["kind"] == "SPAN_KIND_SERVER" for x in s))
server = next((s for s in spans1 if s["kind"] == "SPAN_KIND_SERVER" and s["service"] == "leave-service"), None)
check("İz Tempo'ya ulaştı (istemcinin traceparent'i ile)", server is not None, spans1)
if server:
    check("Span adı rota şablonu (ham yol değil)", server["name"] == "GET api/leave-requests/{id}", server["name"])
    check("http.route şablon", server["attrs"].get("http.route") == "api/leave-requests/{id}", server["attrs"])
    check("url.path içindeki GUID maskeli", server["attrs"].get("url.path") == "/api/leave-requests/{guid}", server["attrs"].get("url.path"))
    check("Sorgu dizesi (url.query) yok", "url.query" not in server["attrs"], server["attrs"])
    check("enduser.id yok", not any(k.startswith("enduser.") for k in server["attrs"]), server["attrs"])
db_spans = [s for s in spans1 if s["attrs"].get("db.system") == "postgresql"]
check("Veritabanı span'leri isteğin altında", len(db_spans) >= 1, [s["name"] for s in spans1])
check("SQL'de parametre değeri yok (yer tutucu)", all("8c7dd608" not in (s["attrs"].get("db.statement") or "") for s in db_spans), db_spans[:1])
bad = pii_findings(spans1)
check("İz 1: ham kişisel veri / yasak öznitelik yok", not bad, bad[:5])

# ------------------------------------------------------------------ 2) servisler arası + Kafka
code = None
lv = {}
for _try in range(10):
    # Yalnızca hafta içi (2032-01-05 pazartesi); çakışırsa (409) başka gün denenir.
    day = (dt.date(2032, 1, 5) + dt.timedelta(weeks=random.randint(0, 150), days=random.randint(0, 4))).isoformat()
    t2, tp2 = new_trace()
    code, lv = http("POST", L, {"employeeId": AYSE, "type": "Unpaid", "startDate": day, "endDate": day, "days": 0, "reason": REASON},
                    headers={"Authorization": "Bearer " + tok("ayse"), "traceparent": tp2})
    if code != 409 and not (code == 400 and "iş günü" in str(lv)):  # çakışma ya da resmi tatil
        break
check("İzin talebi oluşturuldu", code in (200, 201) and lv and lv.get("workflowRequestId"), lv)
leave_id = (lv or {}).get("id")
wf_id = (lv or {}).get("workflowRequestId")
try:
    if wf_id:
        spans2 = wait_trace(t2, lambda s: {"leave-service", "workflow-service"} <= {x["service"] for x in s})
        services = {s["service"] for s in spans2}
        check("leave-service -> workflow-service çağrısı aynı izde", {"leave-service", "workflow-service"} <= services, services)
        client = [s for s in spans2 if s["service"] == "leave-service" and s["kind"] == "SPAN_KIND_CLIENT" and "url.full" in s["attrs"]]
        check("Giden HTTP span'inde URL GUID'i maskeli", client and all(not GUID_RE.search(s["attrs"]["url.full"]) for s in client),
              [s["attrs"].get("url.full") for s in client])
        bad = pii_findings(spans2)
        check("İz 2: ham kişisel veri / yasak öznitelik yok", not bad, bad[:5])

        # Reddet -> workflow.rejected (outbox -> Kafka) -> leave-service tüketicisi.
        _, w = api("ayse", "GET", f"{W}/{wf_id}")
        t_dec = int(time.time()) - 5
        code, _ = api("mehmet", "POST", f"{W}/{wf_id}/steps/{w['steps'][0]['id']}/decide", {"decision": "Rejected", "comment": REASON})
        check("Yönetici reddetti", code == 200, code)
        status = None
        for _ in range(30):
            _, cur = api("ayse", "GET", f"{L}/{leave_id}")
            status = (cur or {}).get("status")
            if status == "Rejected":
                break
            time.sleep(1)
        check("Ret Kafka ile leave-service'e ulaştı", status == "Rejected", status)

        q = urllib.parse.quote('{ resource.service.name = "leave-service" && name = "hr360.workflow.events process" }')
        found = None
        end = time.time() + 60
        while time.time() < end and not found:
            res = tempo(f"/api/search?q={q}&start={t_dec}&end={int(time.time()) + 5}&limit=20") or {}
            for tr in res.get("traces", []):
                # messaging_outbox employee/leave/expense/workflow servislerinin ORTAK tablosu:
                # olayi hangisinin yayinladigi belirsiz; kok span herhangi birinin yayin span'i olabilir.
                if tr.get("rootTraceName") == "hr360.workflow.events publish":
                    sp = spans_of(tempo(f"/api/traces/{tr['traceID']}"))
                    if any(s["service"] == "leave-service" and s["kind"] == "SPAN_KIND_CONSUMER" for s in sp):
                        found = sp
                        break
            if not found:
                time.sleep(3)
        check("Kafka: outbox yayını ve tüketim (leave-service) aynı izde (traceparent başlığı)", bool(found),
              "eşleşen iz bulunamadı")
        if found:
            check("Kafka izi: ham kişisel veri yok", not pii_findings(found), pii_findings(found)[:5])

        # ------------------------------------------------------------- 3) iş metrikleri
        lm = internal_get("http://leave-service:8080/metrics") or ""
        wm = internal_get("http://workflow-service:8080/metrics") or ""
        before = metric_value(leave_metrics_before, "hr360_leave_requests_total", action="created", type="Unpaid") or 0
        after = metric_value(lm, "hr360_leave_requests_total", action="created", type="Unpaid") or 0
        check("hr360_leave_requests_total{action=created,type=Unpaid} arttı", after >= before + 1, (before, after))
        rb = metric_value(leave_metrics_before, "hr360_leave_requests_total", action="rejected", type="Unpaid") or 0
        ra = metric_value(lm, "hr360_leave_requests_total", action="rejected", type="Unpaid") or 0
        check("hr360_leave_requests_total{action=rejected} arttı (Kafka yolu)", ra >= rb + 1, (rb, ra))
        hb = metric_value(wf_metrics_before, "hr360_workflow_approval_duration_seconds_count", outcome="rejected", type="LeaveRequest") or 0
        ha = metric_value(wm, "hr360_workflow_approval_duration_seconds_count", outcome="rejected", type="LeaveRequest") or 0
        check("hr360_workflow_approval_duration_seconds (ret) gözlendi", ha >= hb + 1, (hb, ha))
        db_ = metric_value(wm, "hr360_workflow_step_decisions_total", decision="Rejected")
        check("hr360_workflow_step_decisions_total sayıldı", (db_ or 0) >= 1, db_)
        labels = label_names(lm, "hr360_leave_requests_total") | label_names(wm, "hr360_workflow_requests_total")
        check("Metriklerde kişi/kiracı etiketi yok (varsayılan)", labels <= {"action", "type", "le"}, labels)
finally:
    # ------------------------------------------------------------------ temizlik
    if leave_id:
        psql(f"DELETE FROM leave_requests WHERE \"Id\" = '{leave_id}' AND \"Reason\" = '{REASON}'")
    if wf_id:
        psql(f"DELETE FROM workflow_approval_steps WHERE \"WorkflowRequestId\" = '{wf_id}'")
        psql(f"DELETE FROM workflow_requests WHERE \"Id\" = '{wf_id}'")

print(f"FAILS: {len(FAIL)}")
for f in FAIL:
    print("  -", f)
sys.exit(1 if FAIL else 0)

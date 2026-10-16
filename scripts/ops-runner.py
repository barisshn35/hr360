#!/usr/bin/env python3
"""HR360 bakım çalıştırıcısı (yenilik haritası #34): arayüzden istenen yedek / geri yükleme / güncelleme
işlerini sunucuda çalıştırır.

Konteynerlere docker soketi verilmez. Platform yöneticisi arayüzden platform_ops_jobs tablosuna iş yazar;
bu betik (kullanıcı cron'u, dakikada bir; kurulum: scripts/ops-runner.sh install) yalnızca aşağıdaki
izinli komutları, doğrulanmış bağımsız değişkenlerle çalıştırır:

  backup          scripts/backup.sh
  backup-verify   scripts/backup.sh verify [ARŞİV]
  schedule        scripts/backup.sh schedule --at SS:DD --keep N [--verify-weekly]
  unschedule      scripts/backup.sh unschedule
  check-updates   git fetch --tags (yalnızca etiketler)
  update          scripts/update.sh --ref vX.Y.Z --yes
  rollback        scripts/update.sh rollback --yes
  restore         scripts/restore.sh backups/ARŞİV --yes [--only-db]

Sonuç hem veritabanına hem ops-logs/<iş>.json dosyasına yazılır: geri yükleme veritabanını değiştirdiği
için iş kaydı kaybolursa sonraki turda dosyadan geri yazılır. Her turda sunucu durum özeti
(sürüm, etiketler, yedekler, zamanlama, disk) platform_ops_status tablosuna yazılır.
Tek seferde tek çalıştırıcı (dosya kilidi).
"""
import datetime as dt
import fcntl
import json
import os
import re
import shutil
import subprocess
import sys
import time

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
LOGS = os.path.join(ROOT, "ops-logs")
BACKUPS = os.path.join(ROOT, "backups")
TIMEOUT = int(os.environ.get("OPS_JOB_TIMEOUT", "7200"))
OUTPUT_LIMIT = 64 * 1024
FINAL = ("Succeeded", "Failed", "Cancelled")

TAG = re.compile(r"^v\d+\.\d+\.\d+$")
ARCHIVE = re.compile(r"^hr360-[A-Za-z0-9_.-]+\.tar\.gz(\.enc)?$")
AT = re.compile(r"^([01]\d|2[0-3]):[0-5]\d$")


def now():
    return dt.datetime.now(dt.timezone.utc).isoformat()


def sh(args, timeout=120):
    r = subprocess.run(args, cwd=ROOT, capture_output=True, text=True, timeout=timeout)
    return r.returncode, (r.stdout + r.stderr).strip()


def psql(sql, **vars_):
    """Değerler psql değişkeni olarak (:'ad') geçer; SQL'e metin olarak eklenmez."""
    cmd = ["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "hr360admin", "-d", "hr360_operational",
           "-qAt", "-v", "ON_ERROR_STOP=1"]
    for k, v in vars_.items():
        cmd += ["-v", f"{k}={'' if v is None else v}"]
    r = subprocess.run(cmd, cwd=ROOT, input=sql, capture_output=True, text=True, timeout=120)
    if r.returncode != 0:
        raise RuntimeError(r.stderr.strip()[:500])
    return r.stdout.strip()


def tags():
    _, out = sh(["git", "tag", "-l", "v*", "--sort=-v:refname"])
    return [t for t in out.splitlines() if TAG.match(t)][:30]


def backups():
    items = []
    if os.path.isdir(BACKUPS):
        for n in sorted(os.listdir(BACKUPS), reverse=True):
            if ARCHIVE.match(n):
                st = os.stat(os.path.join(BACKUPS, n))
                items.append({"name": n, "size": st.st_size, "createdAt": dt.datetime.fromtimestamp(st.st_mtime, dt.timezone.utc).isoformat(),
                              "encrypted": n.endswith(".enc"), "manifest": os.path.exists(os.path.join(BACKUPS, n + ".manifest"))})
    return items[:60]


def snapshot():
    def read(p):
        try:
            with open(os.path.join(ROOT, p)) as f:
                return f.read().strip()
        except OSError:
            return None
    _, commit = sh(["git", "rev-parse", "--short", "HEAD"])
    _, date = sh(["git", "log", "-1", "--format=%cs"])
    _, bstatus = sh(["scripts/backup.sh", "status"])
    _, ustatus = sh(["scripts/update.sh", "status"])
    usage = shutil.disk_usage(ROOT)
    return {
        "version": read("VERSION"), "commit": commit, "commitDate": date, "tags": tags(), "backups": backups(),
        "backupStatus": bstatus, "updateStatus": ustatus, "diskFree": usage.free, "diskTotal": usage.total,
        "host": os.uname().nodename, "runnerAt": now(),
    }


def save_snapshot():
    psql("""INSERT INTO platform_ops_status ("Id","Snapshot","UpdatedAt") VALUES (1, :'snap'::jsonb, now())
            ON CONFLICT ("Id") DO UPDATE SET "Snapshot" = EXCLUDED."Snapshot", "UpdatedAt" = now()""", snap=json.dumps(snapshot()))


def command(kind, args):
    """İzinli komut ve doğrulanmış bağımsız değişkenler; uygun değilse ValueError."""
    if kind == "backup":
        return ["scripts/backup.sh"]
    if kind == "backup-verify":
        a = args.get("archive")
        if a is None:
            return ["scripts/backup.sh", "verify"]
        if not (isinstance(a, str) and ARCHIVE.match(a) and os.path.exists(os.path.join(BACKUPS, a))):
            raise ValueError("geçersiz arşiv")
        return ["scripts/backup.sh", "verify", os.path.join("backups", a)]
    if kind == "schedule":
        at, keep = args.get("at"), args.get("keep")
        if not (isinstance(at, str) and AT.match(at)) or not (isinstance(keep, int) and 1 <= keep <= 365):
            raise ValueError("geçersiz zamanlama")
        return ["scripts/backup.sh", "schedule", "--at", at, "--keep", str(keep)] + (["--verify-weekly"] if args.get("verifyWeekly") is True else [])
    if kind == "unschedule":
        return ["scripts/backup.sh", "unschedule"]
    if kind == "check-updates":
        return ["git", "fetch", "--tags", "--quiet", "origin"]
    if kind == "update":
        ref = args.get("ref")
        if not (isinstance(ref, str) and TAG.match(ref) and ref in tags()):
            raise ValueError("geçersiz sürüm etiketi")
        return ["scripts/update.sh", "--ref", ref, "--yes"]
    if kind == "rollback":
        return ["scripts/update.sh", "rollback", "--yes"]
    if kind == "restore":
        a = args.get("archive")
        if not (isinstance(a, str) and ARCHIVE.match(a) and os.path.exists(os.path.join(BACKUPS, a))):
            raise ValueError("geçersiz arşiv")
        return ["scripts/restore.sh", os.path.join("backups", a), "--yes"] + (["--only-db"] if args.get("onlyDb") is True else [])
    raise ValueError("izinli olmayan iş türü")


def record(job):
    """Sonucu dosyaya ve veritabanına yazar (geri yüklemede kaybolan kayıt geri gelir)."""
    with open(os.path.join(LOGS, f"{job['id']}.json"), "w") as f:
        json.dump(job, f)
    psql("""INSERT INTO platform_ops_jobs ("Id","Kind","Args","Status","RequestedBy","RequestedAt","StartedAt","FinishedAt","ExitCode","Output")
            VALUES (:'id', :'kind', :'args'::jsonb, :'status', NULLIF(:'by',''), :'reqat'::timestamptz, NULLIF(:'start','')::timestamptz,
                    NULLIF(:'fin','')::timestamptz, NULLIF(:'code','')::int, :'out')
            ON CONFLICT ("Id") DO UPDATE SET "Status" = EXCLUDED."Status", "StartedAt" = EXCLUDED."StartedAt", "FinishedAt" = EXCLUDED."FinishedAt",
                "ExitCode" = EXCLUDED."ExitCode", "Output" = EXCLUDED."Output" """,
         id=job["id"], kind=job["kind"], args=json.dumps(job["args"]), status=job["status"], by=job.get("requestedBy") or "",
         reqat=job["requestedAt"], start=job.get("startedAt") or "", fin=job.get("finishedAt") or "",
         code="" if job.get("exitCode") is None else job["exitCode"], out=job.get("output") or "")


def reconcile():
    """Dosyada sonucu olan ama veritabanında bitmemiş görünen işleri düzeltir; sahipsiz 'Running' işleri kapatır."""
    for n in os.listdir(LOGS):
        if not n.endswith(".json"):
            continue
        with open(os.path.join(LOGS, n)) as f:
            job = json.load(f)
        if job.get("status") not in FINAL:
            continue
        st = psql("""SELECT "Status" FROM platform_ops_jobs WHERE "Id" = :'id'""", id=job["id"])
        if st not in FINAL:
            record(job)
    # Kilit bizdeyken çalışan başka çalıştırıcı yoktur: kalan 'Running' işler yarıda kesilmiştir.
    psql("""UPDATE platform_ops_jobs SET "Status" = 'Failed', "FinishedAt" = now(),
            "Output" = coalesce("Output", '') || E'\\n[çalıştırıcı] iş yarıda kesildi' WHERE "Status" = 'Running'""")


def claim():
    row = psql("""UPDATE platform_ops_jobs SET "Status" = 'Running', "StartedAt" = now()
                  WHERE "Id" = (SELECT "Id" FROM platform_ops_jobs WHERE "Status" = 'Pending' ORDER BY "RequestedAt" LIMIT 1 FOR UPDATE SKIP LOCKED)
                  RETURNING json_build_object('id', "Id", 'kind', "Kind", 'args', "Args", 'requestedBy', "RequestedBy",
                                              'requestedAt', "RequestedAt", 'startedAt', "StartedAt")""")
    return json.loads(row) if row else None


def run(job):
    try:
        cmd = command(job["kind"], job["args"] or {})
    except ValueError as e:
        job.update(status="Failed", exitCode=None, finishedAt=now(), output=f"Reddedildi: {e}")
        record(job)
        return
    log = os.path.join(LOGS, f"{job['id']}.log")
    started = time.time()
    with open(log, "w") as out:
        out.write(f"$ {' '.join(cmd)}\n")
        out.flush()
        try:
            code = subprocess.run(cmd, cwd=ROOT, stdout=out, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL, timeout=TIMEOUT).returncode
        except subprocess.TimeoutExpired:
            code = 124
            out.write(f"\n[çalıştırıcı] {TIMEOUT} sn zaman aşımı\n")
    with open(log, errors="replace") as f:
        text = f.read()
    if len(text) > OUTPUT_LIMIT:
        text = "…\n" + text[-OUTPUT_LIMIT:]
    job.update(status="Succeeded" if code == 0 else "Failed", exitCode=code, finishedAt=now(),
               output=text + f"\n[çalıştırıcı] süre {int(time.time() - started)} sn, çıkış kodu {code}")
    record(job)


def main():
    os.makedirs(LOGS, exist_ok=True)
    lock = open(os.path.join(LOGS, ".runner.lock"), "w")
    try:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError:
        return 0  # başka bir tur sürüyor
    try:
        reconcile()
        save_snapshot()
        for _ in range(5):
            job = claim()
            if not job:
                break
            run(job)
            save_snapshot()
    except Exception as e:  # noqa: BLE001 — cron günlüğüne yazılır, bir sonraki turda yeniden denenir
        print(f"{now()} ops-runner: {e}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())

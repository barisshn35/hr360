-- Arayüzden yedek / geri yükleme / güncelleme (yenilik haritası #34). Sahibi tenant-service.
-- Konteynerler sunucu komutu çalıştırmaz (docker soketi verilmez): platform yöneticisi arayüzden iş
-- kuyruğa yazar; sunucudaki scripts/ops-runner.py (kullanıcı cron'u, dakikada bir) yalnızca izinli
-- komutları çalıştırır, sonucu ve sunucu durum özetini buraya yazar. Kiracıya ait değildir. İdempotent.
CREATE TABLE IF NOT EXISTS platform_ops_jobs (
    "Id"           uuid PRIMARY KEY,
    -- backup | backup-verify | schedule | unschedule | check-updates | update | rollback | restore
    "Kind"         character varying(32) NOT NULL,
    "Args"         jsonb NOT NULL DEFAULT '{}'::jsonb,
    -- Pending | Running | Succeeded | Failed | Cancelled
    "Status"       character varying(16) NOT NULL DEFAULT 'Pending',
    "RequestedBy"  text,
    "RequestedAt"  timestamptz NOT NULL DEFAULT now(),
    "StartedAt"    timestamptz,
    "FinishedAt"   timestamptz,
    "ExitCode"     integer,
    "Output"       text
);
CREATE INDEX IF NOT EXISTS "IX_platform_ops_jobs_Status" ON platform_ops_jobs ("Status", "RequestedAt");

CREATE TABLE IF NOT EXISTS platform_ops_status (
    "Id"         integer PRIMARY KEY,
    "Snapshot"   jsonb NOT NULL,
    "UpdatedAt"  timestamptz NOT NULL DEFAULT now()
);

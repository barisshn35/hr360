using Npgsql;
using OnboardingService.Models;

namespace OnboardingService.Services;

/// <summary>
/// Arka plan işleri (ONBOARDING_JOBS_SECONDS, varsayılan 900 sn):
///  1) G14 ilk gün karşılama: başlangıç günü gelen planın yeni çalışanına uygulama içi + e-posta
///     karşılama iletisi. "WelcomeSentAt" koşullu güncellemeyle sahiplenilir ve iletiler aynı
///     işlemde yazılır — birden çok örnek ya da yeniden başlatma iki kez göndermez.
///  2) G16 zimmet iade hatırlatması: beklenen iade tarihinden N gün önce zimmet sahibine; tarih
///     geçince sahibe ve İK sorumlusuna (ayar yoksa departman başına) birer kez.
/// Kiracılar arası çalışır; her satır kendi kiracı slug'ıyla yazılır.
/// </summary>
public sealed class OnboardingJobsWorker : BackgroundService
{
    private readonly Sql _sql;
    private readonly ILogger<OnboardingJobsWorker> _log;
    private readonly TimeSpan _interval;

    public OnboardingJobsWorker(Sql sql, ILogger<OnboardingJobsWorker> log)
    {
        _sql = sql;
        _log = log;
        var s = int.TryParse(Environment.GetEnvironmentVariable("ONBOARDING_JOBS_SECONDS"), out var v) && v > 0 ? v : 900;
        _interval = TimeSpan.FromSeconds(s);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, _interval.TotalSeconds)), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { await RunOnceAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "İşe alışma arka plan işi başarısız"); }
            try { await Task.Delay(_interval, ct); } catch (OperationCanceledException) { return; }
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        var sent = await SendWelcomesAsync(ct);
        var reminded = await SendReturnRemindersAsync(ct);
        if (sent + reminded > 0) _log.LogInformation("Karşılama: {W}, zimmet hatırlatma: {R}", sent, reminded);
    }

    private sealed record Settings(string? Subject, string? Body, Guid? HrContact, int DaysBefore);

    private async Task<Settings> SettingsAsync(string tenant, CancellationToken ct) =>
        (await _sql.QueryAsync("""
            SELECT "WelcomeSubject","WelcomeBody","HrContactEmployeeId","ReminderDaysBefore" FROM onboarding_settings WHERE "TenantSlug" = $1
            """, r => new Settings(r.Str(0), r.Str(1), r.GuidOrNull(2), r.GetInt32(3)), ct, tenant)).FirstOrDefault()
        ?? new Settings(null, null, null, 3);

    private static async Task InsertMessageAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string tenant, Guid recipient, string? email,
        string channel, string code, string subject, string body, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO notification_messages ("Id","TenantSlug","RecipientEmployeeId","RecipientEmail","Channel","TemplateCode","Subject","Body","Status","AttemptCount","CreatedAt")
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,'Pending',0,now())
            """, conn, tx);
        foreach (var v in new object?[] { Guid.NewGuid(), tenant, recipient, email, channel, code, subject, body })
            cmd.Parameters.Add(new NpgsqlParameter { Value = v ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /* ------------------------------------------------------------------ karşılama */

    private async Task<int> SendWelcomesAsync(CancellationToken ct)
    {
        var today = BusinessClock.Today;
        // Bir haftadan eski başlangıçlar için geriye dönük karşılama gönderilmez.
        var due = await _sql.QueryAsync("""
            SELECT p."Id", p."TenantSlug", p."EmployeeId", p."StartDate", p."BuddyEmployeeId", p."Location"
            FROM onboarding_plans p
            WHERE p."WelcomeSentAt" IS NULL AND p."Status" <> 'Cancelled' AND p."StartDate" <= $1 AND p."StartDate" >= $2
            ORDER BY p."StartDate" LIMIT 200
            """, r => (Id: r.GetGuid(0), Tenant: r.GetString(1), Emp: r.GetGuid(2), Start: r.GetFieldValue<DateOnly>(3), Buddy: r.GuidOrNull(4), Loc: r.Str(5)),
            ct, today, today.AddDays(-7));
        var n = 0;
        foreach (var p in due)
        {
            var person = await People.FindAsync(_sql, p.Tenant, p.Emp, ct);
            if (person is null || person.Status == "Terminated") continue;
            var st = await SettingsAsync(p.Tenant, ct);
            var (subject, body) = await Controllers.OnboardingPlansController.RenderWelcomeAsync(_sql, p.Tenant, p.Emp, p.Start, p.Buddy, p.Loc,
                new OnboardingSettings { WelcomeSubject = st.Subject, WelcomeBody = st.Body }, ct);

            await using var conn = await _sql.DataSource.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using (var claim = new NpgsqlCommand("""UPDATE onboarding_plans SET "WelcomeSentAt" = now() WHERE "Id" = $1 AND "WelcomeSentAt" IS NULL""", conn, tx))
            {
                claim.Parameters.Add(new NpgsqlParameter { Value = p.Id });
                if (await claim.ExecuteNonQueryAsync(ct) != 1) { await tx.RollbackAsync(ct); continue; }
            }
            await InsertMessageAsync(conn, tx, p.Tenant, p.Emp, null, "InApp", "onboarding.welcome", subject, body, ct);
            if (!string.IsNullOrWhiteSpace(person.Email))
                await InsertMessageAsync(conn, tx, p.Tenant, p.Emp, person.Email, "Email", "onboarding.welcome", subject, body, ct);
            await tx.CommitAsync(ct);
            n++;
        }
        return n;
    }

    /* ------------------------------------------------------------------ zimmet iade hatırlatması */

    private async Task<int> SendReturnRemindersAsync(CancellationToken ct)
    {
        var today = BusinessClock.Today;
        var rows = await _sql.QueryAsync("""
            SELECT x."Id", x."TenantSlug", x."EmployeeId", x."ExpectedReturnOn", a."AssetTag", a."Type", a."Model",
                   x."ReminderBeforeSentAt" IS NOT NULL, x."ReminderOverdueSentAt" IS NOT NULL
            FROM onboarding_asset_assignments x JOIN onboarding_assets a ON a."Id" = x."AssetId"
            WHERE x."ReturnedOn" IS NULL AND x."ExpectedReturnOn" IS NOT NULL
              AND (x."ReminderBeforeSentAt" IS NULL OR x."ReminderOverdueSentAt" IS NULL)
              AND x."ExpectedReturnOn" <= $1
            LIMIT 500
            """, r => (Id: r.GetGuid(0), Tenant: r.GetString(1), Emp: r.GetGuid(2), Due: r.GetFieldValue<DateOnly>(3), Tag: r.GetString(4),
                       Type: r.GetString(5), Model: r.Str(6), BeforeSent: r.GetBoolean(7), OverdueSent: r.GetBoolean(8)),
            ct, today.AddDays(60));
        var n = 0;
        foreach (var x in rows)
        {
            var st = await SettingsAsync(x.Tenant, ct);
            var overdue = x.Due < today;
            var label = $"{x.Tag}{(string.IsNullOrWhiteSpace(x.Model) ? "" : $" ({x.Model})")}";
            string column;
            if (overdue && !x.OverdueSent) column = "ReminderOverdueSentAt";
            else if (!overdue && !x.BeforeSent && x.Due <= today.AddDays(st.DaysBefore)) column = "ReminderBeforeSentAt";
            else continue;

            var holder = await People.FindAsync(_sql, x.Tenant, x.Emp, ct);
            Guid? hr = st.HrContact ?? (holder?.DepartmentHeadId is { } h && h != x.Emp ? h : null);

            await using var conn = await _sql.DataSource.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            var sqlClaim = column == "ReminderOverdueSentAt"
                ? """UPDATE onboarding_asset_assignments SET "ReminderOverdueSentAt" = now(), "ReminderBeforeSentAt" = coalesce("ReminderBeforeSentAt", now()) WHERE "Id" = $1 AND "ReminderOverdueSentAt" IS NULL AND "ReturnedOn" IS NULL"""
                : """UPDATE onboarding_asset_assignments SET "ReminderBeforeSentAt" = now() WHERE "Id" = $1 AND "ReminderBeforeSentAt" IS NULL AND "ReturnedOn" IS NULL""";
            await using (var claim = new NpgsqlCommand(sqlClaim, conn, tx))
            {
                claim.Parameters.Add(new NpgsqlParameter { Value = x.Id });
                if (await claim.ExecuteNonQueryAsync(ct) != 1) { await tx.RollbackAsync(ct); continue; }
            }
            if (overdue)
            {
                await InsertMessageAsync(conn, tx, x.Tenant, x.Emp, null, "InApp", "asset.return.overdue", "Zimmet iadesi gecikti",
                    $"{label} etiketli demirbaşın iade tarihi {x.Due:dd.MM.yyyy} idi. Lütfen en kısa sürede İK/BT ile iade için görüşün.", ct);
                if (hr is { } hrId && hrId != x.Emp)
                    await InsertMessageAsync(conn, tx, x.Tenant, hrId, null, "InApp", "asset.return.overdue.hr", "Zimmet iadesi gecikti",
                        $"{label} — zimmetli: {holder?.FullName ?? "çalışan"}; beklenen iade {x.Due:dd.MM.yyyy}.", ct);
            }
            else
            {
                await InsertMessageAsync(conn, tx, x.Tenant, x.Emp, null, "InApp", "asset.return.reminder", "Zimmet iade tarihi yaklaşıyor",
                    $"{label} etiketli demirbaşın iade tarihi {x.Due:dd.MM.yyyy}. İade için İK/BT ile görüşebilirsiniz.", ct);
                if (hr is { } hrId && hrId != x.Emp)
                    await InsertMessageAsync(conn, tx, x.Tenant, hrId, null, "InApp", "asset.return.reminder.hr", "Zimmet iadesi yaklaşıyor",
                        $"{label} — zimmetli: {holder?.FullName ?? "çalışan"}; beklenen iade {x.Due:dd.MM.yyyy}.", ct);
            }
            await tx.CommitAsync(ct);
            n++;
        }
        return n;
    }
}

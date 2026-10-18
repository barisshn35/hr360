using Npgsql;

namespace EngagementService.Infrastructure.NewHire;

/// <summary>
/// İlk 90 gün takibi (yenilik haritası #8): işe girişin 30., 60. ve 90. gününe gelen çalışana uyum formu,
/// yöneticisine (departman başı) görüşme hatırlatması gönderir. Kilometre taşı son 7 gün içindeyse kayıt
/// açılır (benzersiz indeks tekrar göndermez); eski çalışanlara geriye dönük form gitmez.
/// Aralık NEWHIRE_TICK_SECONDS (varsayılan 3600).
/// </summary>
public sealed class NewHireWorker : BackgroundService
{
    public static readonly int[] Milestones = [30, 60, 90];
    private readonly Sql _sql;
    private readonly Notifier _notify;
    private readonly ILogger<NewHireWorker> _log;
    private readonly TimeSpan _interval;

    public NewHireWorker(Sql sql, Notifier notify, ILogger<NewHireWorker> log)
    {
        _sql = sql; _notify = notify; _log = log;
        _interval = TimeSpan.FromSeconds(int.TryParse(Environment.GetEnvironmentVariable("NEWHIRE_TICK_SECONDS"), out var v) && v > 0 ? v : 3600);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        HR360.Platform.LeaderGate.RunAsync("engagement/NewHireWorker", stoppingToken, LeaderLoopAsync);

    private async Task LeaderLoopAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(40), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { await RunOnceAsync(ct); }
            catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703") { _log.LogInformation("İlk 90 gün turu atlandı: şema hazır değil"); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "İlk 90 gün turu başarısız"); }
            try { await Task.Delay(_interval, ct); } catch (OperationCanceledException) { return; }
        }
    }

    private static string Link => (Environment.GetEnvironmentVariable("PUBLIC_ORIGIN") ?? "http://localhost").TrimEnd('/') + "/panel/ilk-90-gun";

    public async Task<int> RunOnceAsync(CancellationToken ct, string? tenant = null)
    {
        // Bugün (Türkiye) kilometre taşı son 7 gündeki aktif çalışanlar; yöneticisi güncel departmanın başı.
        var due = await _sql.QueryAsync("""
            SELECT e."TenantSlug", e."Id", e."FirstName", m.ms, (e."HireDate" + m.ms) AS due, d."HeadEmployeeId"
            FROM employee_employees e
            CROSS JOIN (VALUES (30), (60), (90)) AS m(ms)
            LEFT JOIN LATERAL (SELECT x."DepartmentId" FROM employee_assignments x
                WHERE x."EmployeeId" = e."Id" AND x."EffectiveFrom" <= current_date AND (x."EffectiveTo" IS NULL OR x."EffectiveTo" >= current_date)
                ORDER BY x."EffectiveFrom" DESC LIMIT 1) a ON true
            LEFT JOIN organization_departments d ON d."Id" = a."DepartmentId"
            WHERE e."Status" <> 'Terminated' AND ($1::text IS NULL OR e."TenantSlug" = $1)
              AND (e."HireDate" + m.ms) BETWEEN (now() AT TIME ZONE 'Europe/Istanbul')::date - 7 AND (now() AT TIME ZONE 'Europe/Istanbul')::date
              AND NOT EXISTS (SELECT 1 FROM engagement_newhire_checkins c WHERE c."TenantSlug" = e."TenantSlug" AND c."EmployeeId" = e."Id" AND c."Milestone" = m.ms)
            """, r => (Tenant: r.GetString(0), Emp: r.GetGuid(1), First: r.GetString(2), Ms: r.GetInt32(3), Due: r.GetFieldValue<DateOnly>(4), Head: r.GuidOrNull(5)), ct, tenant);
        var sent = 0;
        foreach (var x in due)
        {
            var manager = x.Head == x.Emp ? null : x.Head;
            var n = await _sql.ExecuteAsync("""
                INSERT INTO engagement_newhire_checkins ("Id","TenantSlug","EmployeeId","Milestone","DueOn","ManagerEmployeeId","SentAt")
                VALUES ($1,$2,$3,$4,$5,$6,now()) ON CONFLICT DO NOTHING
                """, ct, Guid.NewGuid(), x.Tenant, x.Emp, x.Ms, x.Due, manager);
            if (n == 0) continue;
            sent++;
            foreach (var ch in new[] { "InApp", "Email" })
                await _notify.LocalizedAsync(x.Tenant, x.Emp,
                    $"{x.Ms}. gününüz: nasıl gidiyor?", $"Day {x.Ms}: how is it going?",
                    $"Merhaba {x.First}, aramızdaki {x.Ms}. gününüz. Üç kısa soruyla ilk günlerinizi değerlendirin; yanıtınız yöneticinize ve İK'ya iletilir.",
                    $"Hi {x.First}, it's your day {x.Ms} with us. Tell us how it's going in three short questions; your answers go to your manager and HR.",
                    "newhire.checkin", ct, ch, Link, "Formu doldur", "Fill in the form");
            if (manager is { } m)
                await _notify.LocalizedAsync(x.Tenant, m,
                    $"Ekibinizde {x.Ms}. gün: {x.First}", $"Day {x.Ms} in your team: {x.First}",
                    $"{x.First} işe girişinin {x.Ms}. gününde. Kısa bir birebir görüşme planlamak için iyi bir zaman; uyum formunun yanıtı geldiğinde size haber verilecek.",
                    $"{x.First} is on day {x.Ms}. A good time for a short 1:1; you'll be notified when the onboarding check-in is answered.",
                    "newhire.manager", ct, "InApp", Link, "Ayrıntılar", "Details");
        }
        if (sent > 0) _log.LogInformation("İlk 90 gün: {Count} uyum formu gönderildi", sent);
        return sent;
    }
}

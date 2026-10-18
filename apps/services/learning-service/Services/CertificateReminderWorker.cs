using Microsoft.EntityFrameworkCore;
using LearningService.Data;

namespace LearningService.Services;

/// <summary>G17: hangi hatırlatmanın gideceği — saf karar (birim testli).</summary>
public static class ReminderPlan
{
    public const string D30 = "D30";
    public const string D7 = "D7";
    public const string Expired = "Expired";

    /// <summary>
    /// 30 gün kala (8-30 gün arası), 7 gün kala (1-7 gün) ve bitişte (bugün ya da son 30 gün içinde
    /// dolmuş). Aynı aralık içinde yalnızca o aralığın türü gider — geç eklenen bir sertifikaya
    /// geriye dönük "30 gün kaldı" gönderilmez. Çok eski bitişler (30 günden fazla) sessizdir.
    /// </summary>
    public static string? KindFor(int daysLeft) => daysLeft switch
    {
        > 30 => null,
        >= 8 => D30,
        >= 1 => D7,
        >= -30 => Expired,
        _ => null,
    };
}

/// <summary>
/// G17 sertifika bitiş hatırlatmaları. CERT_REMINDER_SECONDS (varsayılan 3600) aralıkla tüm
/// kiracıları tarar; çalışana ve zorunlu sertifikalarda departman başkanına uygulama içi bildirim
/// yazar. Gönderilenler learning_cert_reminders'a kaydedilir (benzersiz anahtar), aynı hatırlatma
/// ikinci kez gitmez; bitiş tarihi değişirse (yenileme) yeni döngü başlar.
/// </summary>
public sealed class CertificateReminderWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<CertificateReminderWorker> _log;
    private readonly TimeSpan _interval;

    public CertificateReminderWorker(IServiceScopeFactory scopes, ILogger<CertificateReminderWorker> log)
    {
        _scopes = scopes;
        _log = log;
        var s = int.TryParse(Environment.GetEnvironmentVariable("CERT_REMINDER_SECONDS"), out var v) && v > 0 ? v : 3600;
        _interval = TimeSpan.FromSeconds(s);
    }

    public sealed class DueRow
    {
        public Guid Id { get; set; }
        public string TenantSlug { get; set; } = "";
        public Guid EmployeeId { get; set; }
        public string EmployeeName { get; set; } = "";
        public string Name { get; set; } = "";
        public DateOnly ExpiresOn { get; set; }
        public bool Mandatory { get; set; }
        public Guid? HeadId { get; set; }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        HR360.Platform.LeaderGate.RunAsync("learning/CertificateReminderWorker", stoppingToken, LeaderLoopAsync);

    private async Task LeaderLoopAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, _interval.TotalSeconds)), stoppingToken); }
        catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "Sertifika hatırlatma turu başarısız"); }
            try { await Task.Delay(_interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LearningDbContext>();
        var today = TrTime.Today;
        var rows = await db.Database.SqlQueryRaw<DueRow>("""
            SELECT c."Id", c."TenantSlug", c."EmployeeId", e."FirstName" || ' ' || e."LastName" AS "EmployeeName",
                   c."Name", c."ExpiresOn", (c."IsMandatory" OR coalesce(co."IsMandatory", false)) AS "Mandatory",
                   d."HeadEmployeeId" AS "HeadId"
            FROM learning_certifications c
            JOIN employee_employees e ON e."Id" = c."EmployeeId" AND e."TenantSlug" = c."TenantSlug" AND e."Status" <> 'Terminated'
            LEFT JOIN learning_courses co ON co."Id" = c."CourseId"
            LEFT JOIN LATERAL (
                SELECT x."DepartmentId" FROM employee_assignments x
                WHERE x."EmployeeId" = c."EmployeeId" AND x."EffectiveFrom" <= {0}
                  AND (x."EffectiveTo" IS NULL OR x."EffectiveTo" >= {0})
                ORDER BY x."EffectiveFrom" DESC LIMIT 1) a ON true
            LEFT JOIN organization_departments d ON d."Id" = a."DepartmentId"
            WHERE c."ExpiresOn" IS NOT NULL AND c."ExpiresOn" BETWEEN {1} AND {2}
            """, today, today.AddDays(-30), today.AddDays(30)).ToListAsync(ct);

        var sent = 0;
        foreach (var r in rows)
        {
            var days = r.ExpiresOn.DayNumber - today.DayNumber;
            var kind = ReminderPlan.KindFor(days);
            if (kind is null) continue;
            var date = r.ExpiresOn.ToString("dd.MM.yyyy");
            var (subject, body) = kind switch
            {
                ReminderPlan.D30 => ("Sertifikanızın süresi yaklaşıyor", $"«{r.Name}» sertifikanızın geçerliliği {date} tarihinde ({days} gün sonra) bitiyor. Yenileme için İK ile görüşün."),
                ReminderPlan.D7 => ("Sertifikanızın süresi 7 gün içinde bitiyor", $"«{r.Name}» sertifikanızın geçerliliği {date} tarihinde ({days} gün sonra) bitiyor."),
                _ => ("Sertifikanızın süresi doldu", $"«{r.Name}» sertifikanızın geçerliliği {date} tarihinde doldu."),
            };
            sent += await SendOnceAsync(db, r, kind, r.EmployeeId, subject, body, ct);
            if (r.Mandatory && r.HeadId is { } head && head != r.EmployeeId)
            {
                var mSubject = kind == ReminderPlan.Expired ? "Ekibinizde zorunlu sertifika süresi doldu" : "Ekibinizde zorunlu sertifika süresi yaklaşıyor";
                var mBody = kind == ReminderPlan.Expired
                    ? $"{r.EmployeeName} adlı çalışanın zorunlu «{r.Name}» sertifikasının geçerliliği {date} tarihinde doldu."
                    : $"{r.EmployeeName} adlı çalışanın zorunlu «{r.Name}» sertifikasının geçerliliği {date} tarihinde ({days} gün sonra) bitiyor.";
                sent += await SendOnceAsync(db, r, kind, head, mSubject, mBody, ct);
            }
        }
        if (sent > 0) _log.LogInformation("Sertifika hatırlatması: {Count} bildirim", sent);
        return sent;
    }

    /// <summary>Hatırlatma kaydı + bildirim tek işlemde; benzersiz anahtar çakışırsa hiçbir şey yazılmaz.</summary>
    private static async Task<int> SendOnceAsync(LearningDbContext db, DueRow r, string kind, Guid recipient, string subject, string body, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var inserted = await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO learning_cert_reminders ("Id","TenantSlug","CertificationId","Kind","RecipientEmployeeId","ExpiresOn","SentAt")
            VALUES ({0},{1},{2},{3},{4},{5},now()) ON CONFLICT DO NOTHING
            """, Guid.NewGuid(), r.TenantSlug, r.Id, kind, recipient, r.ExpiresOn);
        if (inserted == 0) { await tx.RollbackAsync(ct); return 0; }
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO notification_messages ("Id","TenantSlug","RecipientEmployeeId","RecipientEmail","Channel","TemplateCode","Subject","Body","Status","AttemptCount","CreatedAt","ActionUrl")
            VALUES ({0},{1},{2},NULL,'InApp',{3},{4},{5},'Pending',0,now(),'/panel/egitim?gorunum=sertifikalar')
            """, Guid.NewGuid(), r.TenantSlug, recipient, "learning.certificate." + kind.ToLowerInvariant(), subject, body);
        await tx.CommitAsync(ct);
        return 1;
    }
}

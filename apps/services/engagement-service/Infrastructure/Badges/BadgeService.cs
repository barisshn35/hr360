using Npgsql;

namespace EngagementService.Infrastructure.Badges;

/// <summary>
/// Rozet ölçütlerini tek sorguda hesaplar ve yeni kazanılan rozetleri kaydeder. Kişinin ilk eşitlemesi
/// sessizdir (geçmişte kazanılmış rozetler için toplu bildirim gitmez); sonrakilerde her yeni rozet için
/// uygulama içi bildirim. BadgeWorker tüm kiracıları BADGES_TICK_SECONDS (varsayılan 21600) aralıkla tarar.
/// </summary>
public sealed class BadgeService : BackgroundService
{
    private const string Init = "_init";
    private readonly Sql _sql;
    private readonly Notifier _notify;
    private readonly ILogger<BadgeService> _log;
    private readonly TimeSpan _interval;

    public BadgeService(Sql sql, Notifier notify, ILogger<BadgeService> log)
    {
        _sql = sql; _notify = notify; _log = log;
        _interval = TimeSpan.FromSeconds(int.TryParse(Environment.GetEnvironmentVariable("BADGES_TICK_SECONDS"), out var v) && v > 0 ? v : 21600);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        HR360.Platform.LeaderGate.RunAsync("engagement/BadgeService", stoppingToken, LeaderLoopAsync);

    private async Task LeaderLoopAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(60), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                foreach (var t in await _sql.QueryAsync("""SELECT DISTINCT "TenantSlug" FROM employee_employees WHERE "Status" <> 'Terminated'""", r => r.GetString(0), ct))
                    await SyncAsync(t, null, ct);
            }
            catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703") { _log.LogInformation("Rozet turu atlandı: şema hazır değil"); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "Rozet turu başarısız"); }
            try { await Task.Delay(_interval, ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Kiracıdaki (ya da tek bir) aktif çalışanın ölçütleri ve işe giriş tarihi.</summary>
    public async Task<Dictionary<Guid, (DateOnly Hire, BadgeCatalog.Metrics M)>> MetricsAsync(string tenant, Guid? employeeId, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
        var rows = await _sql.QueryAsync("""
            SELECT e."Id", e."HireDate",
              (SELECT count(*) FROM engagement_kudos k WHERE k."TenantSlug" = $1 AND k."ToEmployeeId" = e."Id"),
              (SELECT count(*) FROM engagement_kudos k WHERE k."TenantSlug" = $1 AND k."FromEmployeeId" = e."Id"),
              (SELECT count(DISTINCT u."PeriodId") FROM engagement_eom_units u WHERE u."TenantSlug" = $1 AND u."WinnerEmployeeId" = e."Id" AND u."ClosedAt" IS NOT NULL),
              (SELECT count(*) FROM engagement_ideas i WHERE i."TenantSlug" = $1 AND i."AuthorEmployeeId" = e."Id"),
              (SELECT count(*) FROM engagement_ideas i WHERE i."TenantSlug" = $1 AND i."AuthorEmployeeId" = e."Id" AND i."Status" = 'Done'),
              (SELECT coalesce(sum(v."VolunteerHours"), 0) FROM engagement_event_rsvps r JOIN engagement_events v ON v."Id" = r."EventId"
                 WHERE r."TenantSlug" = $1 AND r."EmployeeId" = e."Id" AND r."Attended" AND v."Kind" = 'Volunteer' AND v."Status" = 'Published'),
              (SELECT count(*) FROM engagement_event_rsvps r WHERE r."TenantSlug" = $1 AND r."EmployeeId" = e."Id" AND r."Attended"),
              (SELECT count(*) FROM engagement_coffee_matches m WHERE m."TenantSlug" = $1 AND e."Id" = ANY(m."MemberIds") AND m."Met"),
              (SELECT count(*) FROM engagement_card_signatures s WHERE s."TenantSlug" = $1 AND s."EmployeeId" = e."Id"),
              (SELECT count(*) FROM learning_certifications c WHERE c."TenantSlug" = $1 AND c."EmployeeId" = e."Id")
            FROM employee_employees e
            WHERE e."TenantSlug" = $1 AND e."Status" <> 'Terminated' AND ($2::uuid IS NULL OR e."Id" = $2)
            """, r => (Id: r.GetGuid(0), Hire: r.GetFieldValue<DateOnly>(1), M: new BadgeCatalog.Metrics
            {
                [BadgeCatalog.Tenure] = BadgeCatalog.TenureYears(r.GetFieldValue<DateOnly>(1), today),
                [BadgeCatalog.KudosReceived] = r.GetInt64(2), [BadgeCatalog.KudosGiven] = r.GetInt64(3), [BadgeCatalog.EomWins] = r.GetInt64(4),
                [BadgeCatalog.Ideas] = r.GetInt64(5), [BadgeCatalog.IdeasDone] = r.GetInt64(6), [BadgeCatalog.VolunteerHours] = r.GetDecimal(7),
                [BadgeCatalog.EventsAttended] = r.GetInt64(8), [BadgeCatalog.CoffeeMet] = r.GetInt64(9), [BadgeCatalog.CardsSigned] = r.GetInt64(10),
                [BadgeCatalog.Certifications] = r.GetInt64(11),
            }), ct, tenant, employeeId);
        return rows.ToDictionary(x => x.Id, x => (x.Hire, x.M));
    }

    /// <summary>Yeni kazanılan rozetleri kaydeder (ve ilk eşitleme değilse bildirir). Kişi → rozet → kazanma anı döner.</summary>
    public async Task<Dictionary<Guid, Dictionary<string, DateTime>>> SyncAsync(string tenant, Guid? employeeId, CancellationToken ct)
    {
        var metrics = await MetricsAsync(tenant, employeeId, ct);
        var awards = (await _sql.QueryAsync("""
            SELECT "EmployeeId", "BadgeId", "AwardedAt" FROM engagement_badge_awards WHERE "TenantSlug" = $1 AND ($2::uuid IS NULL OR "EmployeeId" = $2)
            """, r => (Emp: r.GetGuid(0), Badge: r.GetString(1), At: r.GetFieldValue<DateTime>(2)), ct, tenant, employeeId))
            .GroupBy(a => a.Emp).ToDictionary(g => g.Key, g => g.ToDictionary(a => a.Badge, a => a.At));
        var notified = 0;
        foreach (var (emp, (hire, m)) in metrics)
        {
            var have = awards.TryGetValue(emp, out var h) ? h : awards[emp] = new Dictionary<string, DateTime>();
            var first = !have.ContainsKey(Init);
            foreach (var b in BadgeCatalog.Evaluate(m).Where(b => b.Earned && !have.ContainsKey(b.Id)))
            {
                // Kıdem rozetinin tarihi yıl dönümüdür; diğerleri ilk görüldüğü an.
                var at = b.Metric == BadgeCatalog.Tenure
                    ? hire.AddYears((int)b.Target).ToDateTime(new TimeOnly(9, 0), DateTimeKind.Utc).AddHours(-3)
                    : DateTime.UtcNow;
                var n = await _sql.ExecuteAsync("""
                    INSERT INTO engagement_badge_awards ("Id","TenantSlug","EmployeeId","BadgeId","AwardedAt") VALUES ($1,$2,$3,$4,$5) ON CONFLICT DO NOTHING
                    """, ct, Guid.NewGuid(), tenant, emp, b.Id, at);
                have[b.Id] = at;
                if (n > 0 && !first)
                {
                    notified++;
                    await _notify.LocalizedAsync(tenant, emp, "Yeni rozet kazandınız 🏅", "You earned a new badge 🏅",
                        "Profilinizdeki rozet koleksiyonuna yeni bir rozet eklendi.", "A new badge was added to the collection on your profile.",
                        "badges.earned", ct, "InApp", (Environment.GetEnvironmentVariable("PUBLIC_ORIGIN") ?? "http://localhost").TrimEnd('/') + "/panel/profil?sekme=rozetler", "Rozetlerim", "My badges");
                }
            }
            if (first)
            {
                await _sql.ExecuteAsync("""
                    INSERT INTO engagement_badge_awards ("Id","TenantSlug","EmployeeId","BadgeId","AwardedAt") VALUES ($1,$2,$3,$4,now()) ON CONFLICT DO NOTHING
                    """, ct, Guid.NewGuid(), tenant, emp, Init);
                have[Init] = DateTime.UtcNow;
            }
        }
        if (notified > 0) _log.LogInformation("Rozet: {Tenant} için {Count} yeni rozet bildirildi", tenant, notified);
        return awards.Where(a => metrics.ContainsKey(a.Key)).ToDictionary(a => a.Key, a => a.Value.Where(x => x.Key != Init).ToDictionary(x => x.Key, x => x.Value));
    }
}

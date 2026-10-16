using Npgsql;

namespace EngagementService.Infrastructure.Coffee;

/// <summary>
/// Kahve eşleştirme turları (yenilik haritası #27). Ayarlanan günde (Türkiye saatiyle 09:00 sonrası)
/// açık kiracılarda, o dönem için tur yoksa katılımcıları eşler ve her birine eşini bildirir. Dönem
/// anahtarı benzersiz olduğundan aynı dönemde ikinci tur açılmaz. Aralık COFFEE_TICK_SECONDS (varsayılan 900).
/// </summary>
public sealed class CoffeeWorker : BackgroundService
{
    private const int RecentRounds = 3;
    private readonly Sql _sql;
    private readonly PeopleDirectory _people;
    private readonly Notifier _notify;
    private readonly ILogger<CoffeeWorker> _log;
    private readonly TimeSpan _interval;

    public CoffeeWorker(Sql sql, PeopleDirectory people, Notifier notify, ILogger<CoffeeWorker> log)
    {
        _sql = sql; _people = people; _notify = notify; _log = log;
        _interval = TimeSpan.FromSeconds(int.TryParse(Environment.GetEnvironmentVariable("COFFEE_TICK_SECONDS"), out var v) && v > 0 ? v : 900);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(50), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { await RunOnceAsync(ct); }
            catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703") { _log.LogInformation("Kahve turu atlandı: şema hazır değil"); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "Kahve turu başarısız"); }
            try { await Task.Delay(_interval, ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Zamanı gelen kiracılarda tur açar. <paramref name="force"/>: gün/saat beklemeden (İK, yalnızca o kiracı).</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct, string? tenant = null, bool force = false)
    {
        var tr = DateTime.UtcNow.AddHours(3);
        var trDay = (int)tr.DayOfWeek == 0 ? 7 : (int)tr.DayOfWeek;
        var tenants = await _sql.QueryAsync("""
            SELECT "TenantSlug", "Cadence", "CrossDepartment", "DayOfWeek" FROM engagement_coffee_settings
            WHERE "Enabled" AND ($1::text IS NULL OR "TenantSlug" = $1)
            """, r => (Tenant: r.GetString(0), Cadence: r.GetString(1), Cross: r.GetBoolean(2), Day: r.GetInt32(3)), ct, tenant);
        var groups = 0;
        foreach (var t in tenants)
        {
            if (!force && (t.Day != trDay || tr.Hour < 9)) continue;
            groups += await RunTenantAsync(t.Tenant, CoffeeMatcher.PeriodKey(t.Cadence, DateOnly.FromDateTime(tr)), t.Cross, ct);
        }
        return groups;
    }

    private async Task<int> RunTenantAsync(string tenant, string period, bool cross, CancellationToken ct)
    {
        // Ayrılmış (Terminated) çalışanlar ListAsync'te yoktur; iki kişiden azsa tur açılmaz (dönem harcanmaz).
        var ids = (await _sql.QueryAsync("""SELECT "EmployeeId" FROM engagement_coffee_participants WHERE "TenantSlug" = $1""", r => r.GetGuid(0), ct, tenant)).ToHashSet();
        var people = (await _people.ListAsync(tenant, ct)).Where(p => ids.Contains(p.Id)).ToDictionary(p => p.Id);
        if (people.Count < 2) return 0;
        var roundId = Guid.NewGuid();
        var created = await _sql.ExecuteAsync("""
            INSERT INTO engagement_coffee_rounds ("Id","TenantSlug","PeriodKey","CreatedAt") VALUES ($1,$2,$3,now()) ON CONFLICT DO NOTHING
            """, ct, roundId, tenant, period);
        if (created == 0) return 0;
        var recent = (await _sql.QueryAsync("""
            SELECT m."MemberIds" FROM engagement_coffee_matches m
            WHERE m."TenantSlug" = $1 AND m."RoundId" IN (SELECT "Id" FROM engagement_coffee_rounds WHERE "TenantSlug" = $1 AND "Id" <> $2 ORDER BY "CreatedAt" DESC LIMIT $3)
            """, r => r.GetFieldValue<Guid[]>(0), ct, tenant, roundId, RecentRounds))
            .SelectMany(g => g.SelectMany((a, i) => g.Skip(i + 1).Select(b => CoffeeMatcher.PairKey(a, b)))).ToHashSet();

        var matches = CoffeeMatcher.Match(people.Values.Select(p => new CoffeeMatcher.Member(p.Id, p.DepartmentId)).ToList(), recent, cross, Random.Shared);
        foreach (var g in matches)
        {
            await _sql.ExecuteAsync("""
                INSERT INTO engagement_coffee_matches ("Id","TenantSlug","RoundId","MemberIds","Met","CreatedAt") VALUES ($1,$2,$3,$4,false,now())
                """, ct, Guid.NewGuid(), tenant, roundId, g.Select(m => m.Id).ToArray());
            foreach (var m in g)
            {
                var others = g.Where(o => o.Id != m.Id).Select(o => people[o.Id]).ToList();
                var names = string.Join(", ", others.Select(o => o.Department is null ? o.Name : $"{o.Name} ({o.Department})"));
                foreach (var ch in new[] { "InApp", "Email" })
                    await _notify.LocalizedAsync(tenant, m.Id,
                        "Kahve eşleşmeniz hazır ☕", "Your coffee match is ready ☕",
                        $"Bu turda {names} ile eşleştiniz. Uygun bir zamanda 20-30 dakikalık bir kahve ya da çevrim içi sohbet planlayın.",
                        $"This round you're matched with {names}. Find 20-30 minutes for a coffee or a video chat.",
                        "coffee.match", ct, ch, Link, "Ayrıntılar", "Details");
            }
        }
        _log.LogInformation("Kahve turu {Tenant} {Period}: {Groups} grup", tenant, period, matches.Count);
        return matches.Count;
    }

    private static string Link => (Environment.GetEnvironmentVariable("PUBLIC_ORIGIN") ?? "http://localhost").TrimEnd('/') + "/panel/kahve";
}

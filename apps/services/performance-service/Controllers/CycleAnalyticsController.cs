using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PerformanceService.Data;
using PerformanceService.Models;
using PerformanceService.Services;

namespace PerformanceService.Controllers;

/// <summary>
/// Donem bazli degerlendirme sonuclari.
///
/// Zaman serisi analizinden (AnalyticsController) farkli bir soruyu
/// cevaplar:
///   - Zaman serisi : "son 3 ayda nasil gidiyor?" - surekli izleme
///   - Donemsel     : "Q3'te ne aldi, Q2'ye gore nasil?" - resmi sonuc
///
/// Resmi sonuc donem KAPANDIGINDA sabitlenir; sonradan gelen
/// degerlendirmeler onu degistirmez.
/// </summary>
[ApiController]
[Route("api/cycle-analytics")]
[Authorize]
public class CycleAnalyticsController : ControllerBase
{
    private readonly PerformanceDbContext _db;
    private readonly SnapshotService _snapshots;
    private readonly DirectoryClient _directory;

    public CycleAnalyticsController(
        PerformanceDbContext db, SnapshotService snapshots, DirectoryClient directory)
    {
        _db = db;
        _snapshots = snapshots;
        _directory = directory;
    }

    /// <summary>
    /// Bir calisanin donem donem puanlari: Q1 -> Q2 -> Q3 seklinde.
    /// Zaman serisinden farki, x ekseni tarih degil DONEM.
    /// </summary>
    [HttpGet("employee/{employeeId}")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> EmployeeByCycle(
        Guid employeeId, [FromQuery] int? year, CancellationToken ct = default)
        => Ok(await BuildEmployeeCycleHistoryAsync(employeeId, year, ct));

    /// <summary>Calisanin kendi donemsel gecmisi.</summary>
    [HttpGet("me")]
    public async Task<IActionResult> MyCycles([FromQuery] int? year, [FromQuery] bool optional = false, CancellationToken ct = default)
    {
        var me = await _directory.FindMeAsync(ct);
        if (me is null && optional) return NoContent();
        if (me is null)
            return NotFound(new { message = "Bu hesaba bağlı çalışan kaydı bulunamadı" });

        return Ok(await BuildEmployeeCycleHistoryAsync(me.Id, year, ct));
    }

    /// <summary>
    /// Bir donemin tum sonuclari: sirket geneli ya da ekip bazinda.
    /// Donem kapanmissa resmi sonuc, kapanmamissa anlik durum doner.
    /// </summary>
    [HttpGet("cycle/{cycleId}")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> CycleResults(
        Guid cycleId, [FromQuery] Guid? teamId, CancellationToken ct = default)
    {
        var cycle = await _db.Cycles.FirstOrDefaultAsync(c => c.Id == cycleId, ct);
        if (cycle is null) return NotFound(new { message = "Dönem bulunamadı" });

        var snapshots = await CycleSnapshotsAsync(cycleId, ct);

        if (teamId.HasValue)
        {
            var members = await _directory.GetTeamMembersAsync(teamId.Value, ct);
            var ids = members.Select(m => m.EmployeeId).ToHashSet();
            snapshots = snapshots.Where(s => ids.Contains(s.EmployeeId)).ToList();
        }

        var scores = snapshots.Select(s => s.Score).ToList();
        var names = await _directory.GetEmployeeNamesAsync(
            snapshots.Select(s => s.EmployeeId).ToList(), ct);

        return Ok(new
        {
            cycle = new
            {
                cycle.Id, cycle.Name, cycle.Year,
                period = cycle.Period.ToString(),
                status = cycle.Status.ToString(),
                cycle.StartDate, cycle.EndDate,
                isFinal = cycle.Status == CycleStatus.Closed,
            },
            teamId,
            participantCount = snapshots.Count,
            summary = scores.Count == 0 ? null : new
            {
                average = Math.Round(scores.Average(), 2),
                median = Statistics.Median(scores),
                min = scores.Min(),
                max = scores.Max(),
                stdDev = Statistics.StdDev(scores),
                provisionalCount = snapshots.Count(s => s.IsProvisional),
            },
            distribution = BuildDistribution(scores),
            results = snapshots
                .OrderByDescending(s => s.Score)
                .Select(s => new
                {
                    employeeId = s.EmployeeId,
                    name = names.GetValueOrDefault(s.EmployeeId, "—"),
                    score = s.Score,
                    goalScore = s.GoalScore,
                    metricScore = s.MetricScore,
                    isProvisional = s.IsProvisional,
                    provisionalReason = s.ProvisionalReason,
                    reviewCount = s.ReviewCount,
                    percentile = Statistics.Percentile(s.Score, scores),
                    teamId = s.TeamId,
                }),
        });
    }

    /// <summary>
    /// Iki ya da daha fazla donemi karsilastirir.
    /// "Ekip Q2'den Q3'e nasil degisti" sorusu icin.
    /// </summary>
    [HttpGet("compare")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> Compare(
        [FromQuery] string cycleIds,
        [FromQuery] Guid? teamId,
        CancellationToken ct = default)
    {
        var ids = cycleIds.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => Guid.TryParse(s.Trim(), out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty).ToList();

        if (ids.Count < 2)
            return BadRequest(new { message = "En az iki dönem kimliği verilmeli" });

        var cycles = await _db.Cycles
            .Where(c => ids.Contains(c.Id))
            .OrderBy(c => c.Year).ThenBy(c => c.StartDate)
            .ToListAsync(ct);

        HashSet<Guid>? teamMembers = null;
        if (teamId.HasValue)
        {
            var members = await _directory.GetTeamMembersAsync(teamId.Value, ct);
            teamMembers = members.Select(m => m.EmployeeId).ToHashSet();
        }

        var periods = new List<object>();
        var allEmployees = new HashSet<Guid>();
        var byEmployee = new Dictionary<Guid, Dictionary<Guid, decimal>>();

        foreach (var cycle in cycles)
        {
            var snaps = await CycleSnapshotsAsync(cycle.Id, ct);
            if (teamMembers is not null)
                snaps = snaps.Where(s => teamMembers.Contains(s.EmployeeId)).ToList();

            var scores = snaps.Select(s => s.Score).ToList();

            periods.Add(new
            {
                cycleId = cycle.Id,
                name = cycle.Name,
                period = cycle.Period.ToString(),
                cycle.Year,
                participantCount = snaps.Count,
                average = scores.Count == 0 ? (decimal?)null : Math.Round(scores.Average(), 2),
                median = scores.Count == 0 ? (decimal?)null : Statistics.Median(scores),
                stdDev = scores.Count == 0 ? (decimal?)null : Statistics.StdDev(scores),
            });

            foreach (var s in snaps)
            {
                allEmployees.Add(s.EmployeeId);
                if (!byEmployee.TryGetValue(s.EmployeeId, out var map))
                    byEmployee[s.EmployeeId] = map = new Dictionary<Guid, decimal>();
                map[cycle.Id] = s.Score;
            }
        }

        var names = await _directory.GetEmployeeNamesAsync(allEmployees.ToList(), ct);
        var cycleOrder = cycles.Select(c => c.Id).ToList();

        // Kisi bazinda donemler arasi degisim
        var movements = byEmployee.Select(kv =>
        {
            var series = cycleOrder
                .Select(cid => kv.Value.TryGetValue(cid, out var v) ? (decimal?)v : null)
                .ToList();

            var known = series.Where(v => v is not null).Select(v => v!.Value).ToList();
            var first = known.FirstOrDefault();
            var last = known.LastOrDefault();

            return new
            {
                employeeId = kv.Key,
                name = names.GetValueOrDefault(kv.Key, "—"),
                scores = series,
                change = known.Count >= 2 ? Math.Round(last - first, 2) : (decimal?)null,
                trendLabel = known.Count >= 2
                    ? Statistics.TrendLabel(Statistics.TrendSlope(known))
                    : "Yetersiz veri",
            };
        })
        .OrderByDescending(m => m.change ?? decimal.MinValue)
        .ToList();

        return Ok(new
        {
            teamId,
            cycles = periods,
            employees = movements,
            // Yukselenler ve dusenler ayrica: yoneticinin dikkatini
            // toplamasi gereken iki uc.
            improved = movements.Where(m => m.change is > 5m).Take(10),
            declined = movements.Where(m => m.change is < -5m)
                .OrderBy(m => m.change).Take(10),
        });
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// Bir donemin puanlari.
    ///
    /// Donem kapanmissa CycleClosed kaynakli anlik goruntu resmi sonuctur.
    /// Kapanmamissa her calisanin o donemdeki EN GUNCEL goruntusu alinir.
    /// </summary>
    private async Task<List<PerformanceSnapshot>> CycleSnapshotsAsync(
        Guid cycleId, CancellationToken ct)
    {
        var all = await _db.Snapshots
            .Where(s => s.CycleId == cycleId)
            .ToListAsync(ct);

        return all
            .GroupBy(s => s.EmployeeId)
            .Select(g =>
                g.FirstOrDefault(s => s.Source == SnapshotSource.CycleClosed)
                ?? g.OrderByDescending(s => s.CapturedAt).First())
            .ToList();
    }

    private async Task<object> BuildEmployeeCycleHistoryAsync(
        Guid employeeId, int? year, CancellationToken ct)
    {
        var cq = _db.Cycles.AsQueryable();
        if (year.HasValue) cq = cq.Where(c => c.Year == year.Value);
        var cycles = await cq.OrderBy(c => c.Year).ThenBy(c => c.StartDate).ToListAsync(ct);

        var cycleIds = cycles.Select(c => c.Id).ToList();
        var snaps = await _db.Snapshots
            .Where(s => s.EmployeeId == employeeId && s.CycleId != null
                        && cycleIds.Contains(s.CycleId!.Value))
            .ToListAsync(ct);

        var byCycle = snaps
            .GroupBy(s => s.CycleId!.Value)
            .ToDictionary(
                g => g.Key,
                g => g.FirstOrDefault(s => s.Source == SnapshotSource.CycleClosed)
                     ?? g.OrderByDescending(s => s.CapturedAt).First());

        var history = cycles
            .Where(c => byCycle.ContainsKey(c.Id))
            .Select(c =>
            {
                var s = byCycle[c.Id];
                return new
                {
                    cycleId = c.Id,
                    name = c.Name,
                    period = c.Period.ToString(),
                    c.Year,
                    status = c.Status.ToString(),
                    isFinal = c.Status == CycleStatus.Closed,
                    score = s.Score,
                    goalScore = s.GoalScore,
                    metricScore = s.MetricScore,
                    isProvisional = s.IsProvisional,
                    reviewCount = s.ReviewCount,
                    configVersion = s.ConfigVersion,
                };
            })
            .ToList();

        var series = history.Select(h => h.score).ToList();
        var slope = Statistics.TrendSlope(series);

        return new
        {
            employeeId,
            year,
            cycleCount = history.Count,
            latest = history.LastOrDefault(),
            changeFromPrevious = history.Count >= 2
                ? Math.Round(history[^1].score - history[^2].score, 2)
                : (decimal?)null,
            trendSlope = slope,
            trendLabel = history.Count >= 2 ? Statistics.TrendLabel(slope) : "Yetersiz veri",
            best = history.Count == 0 ? null : history.OrderByDescending(h => h.score).First(),
            history,
        };
    }

    /// <summary>
    /// Puan dagilimi - histogram icin. Sabit 20'lik dilimler:
    /// kiraci esikleri farkli olsa da dagilimin sekli karsilastirilabilir kalsin.
    /// </summary>
    private static object BuildDistribution(IReadOnlyList<decimal> scores)
    {
        var buckets = new[]
        {
            ("0-20", 0m, 20m), ("21-40", 20m, 40m), ("41-60", 40m, 60m),
            ("61-80", 60m, 80m), ("81-100", 80m, 100.01m),
        };

        return buckets.Select(b => new
        {
            range = b.Item1,
            count = scores.Count(s => s >= b.Item2 && s < b.Item3),
        });
    }
}

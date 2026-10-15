using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PerformanceService.Data;
using PerformanceService.Models;
using PerformanceService.Services;

namespace PerformanceService.Controllers;

/// <summary>
/// Donemsel performans analizi.
///
/// Yetki: baskasinin verisini gormek yonetici yetkisi ister. Calisan
/// kendi verisini /api/analytics/me uzerinden gorur.
/// </summary>
[ApiController]
[Route("api/analytics")]
[Authorize]
public class AnalyticsController : ControllerBase
{
    private readonly PerformanceDbContext _db;
    private readonly DirectoryClient _directory;

    public AnalyticsController(PerformanceDbContext db, DirectoryClient directory)
    {
        _db = db;
        _directory = directory;
    }

    /// <summary>
    /// Bir calisanin performans trendi.
    /// Donem secimi: week | month | quarter | halfYear | year | all
    /// </summary>
    [HttpGet("employee/{employeeId}")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> Employee(
        Guid employeeId,
        [FromQuery] AnalyticsPeriod period = AnalyticsPeriod.Quarter,
        CancellationToken ct = default)
        => Ok(await BuildEmployeeReportAsync(employeeId, period, ct));

    /// <summary>
    /// Calisanin ekibiyle karsilastirmasi: ekip ortalamasi, ortanca,
    /// yuzdelik dilim ve siralama.
    /// </summary>
    [HttpGet("employee/{employeeId}/vs-team")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> EmployeeVsTeam(
        Guid employeeId,
        [FromQuery] Guid? teamId,
        [FromQuery] AnalyticsPeriod period = AnalyticsPeriod.Quarter,
        CancellationToken ct = default)
    {
        var resolvedTeam = teamId;
        if (resolvedTeam is null)
        {
            var teams = await _directory.GetTeamsForEmployeeAsync(employeeId, ct);
            resolvedTeam = teams.FirstOrDefault()?.TeamId;
        }

        if (resolvedTeam is null)
            return BadRequest(new { message = "Çalışan hiçbir ekibe bağlı değil" });

        return Ok(await BuildComparisonAsync(employeeId, resolvedTeam.Value, period, ct));
    }

    /// <summary>Ekip genel gorunumu: ortalama trend, dagilim, metrik zayif noktalari.</summary>
    [HttpGet("team/{teamId}")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> Team(
        Guid teamId,
        [FromQuery] AnalyticsPeriod period = AnalyticsPeriod.Quarter,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var start = period.StartOf(now);

        // NOT: Onceki halinde "ekip yok" ile "ekip var ama henuz uyesi yok"
        // ayni 404'e dusuyordu - yeni olusturulmus (bos) bir ekip secildiginde
        // Performans > Analiz ekrani kirmizi "Ekip analizi alinamadi" hatasi
        // ve ise yaramayan bir "Tekrar dene" dugmesi gosteriyordu (hardcore
        // test, son tur). Bos ekip normal bir durum: 200 + bos sonuc doner,
        // arayuz kendi bos durumunu gosterir. 404 yalnizca ekip gercekten yoksa.
        var members = await _directory.GetTeamMembersOrNullAsync(teamId, ct);
        if (members is null)
            return NotFound(new { message = "Ekip bulunamadı" });

        var memberIds = members.Select(m => m.EmployeeId).ToList();

        var q = _db.Snapshots.Where(s => memberIds.Contains(s.EmployeeId));
        if (start is not null) q = q.Where(s => s.CapturedAt >= start.Value);
        var snapshots = memberIds.Count == 0
            ? new List<PerformanceSnapshot>()
            : await q.OrderBy(s => s.CapturedAt).ToListAsync(ct);

        // Her uyenin en guncel puani
        var latest = snapshots
            .GroupBy(s => s.EmployeeId)
            .Select(g => g.OrderByDescending(s => s.CapturedAt).First())
            .ToList();

        var scores = latest.Select(s => s.Score).ToList();
        var names = await _directory.GetEmployeeNamesAsync(memberIds, ct);

        // Zaman icinde ekip ortalamasi
        var trend = snapshots
            .GroupBy(s => period.BucketOf(s.CapturedAt))
            .OrderBy(g => g.Key)
            .Select(g => new
            {
                bucket = g.Key,
                average = Math.Round(g.Average(s => s.Score), 2),
                sampleSize = g.Select(s => s.EmployeeId).Distinct().Count(),
            })
            .ToList();

        return Ok(new
        {
            teamId,
            period = period.ToString(),
            periodLabel = period.Label(),
            memberCount = members.Count,
            scoredMemberCount = latest.Count,
            summary = scores.Count == 0 ? null : new
            {
                average = Math.Round(scores.Average(), 2),
                median = Statistics.Median(scores),
                min = scores.Min(),
                max = scores.Max(),
                stdDev = Statistics.StdDev(scores),
                trendSlope = Statistics.TrendSlope(trend.Select(t => t.average).ToList()),
                trendLabel = Statistics.TrendLabel(
                    Statistics.TrendSlope(trend.Select(t => t.average).ToList())),
            },
            trend,
            members = latest
                .OrderByDescending(s => s.Score)
                .Select(s => new
                {
                    employeeId = s.EmployeeId,
                    name = names.GetValueOrDefault(s.EmployeeId, "—"),
                    isLead = members.FirstOrDefault(m => m.EmployeeId == s.EmployeeId)?.IsLead ?? false,
                    score = s.Score,
                    isProvisional = s.IsProvisional,
                    percentile = Statistics.Percentile(s.Score, scores),
                    capturedAt = s.CapturedAt,
                }),
            // Puani hic olmayan uyeler - yoneticinin gozunden kacmasin
            unscored = memberIds
                .Where(id => latest.All(s => s.EmployeeId != id))
                .Select(id => new { employeeId = id, name = names.GetValueOrDefault(id, "—") }),
        });
    }

    /// <summary>
    /// Calisanin kendi gorunumu. Yonetici yetkisi gerektirmez ama
    /// yalnizca token sahibinin kendi verisini dondurur.
    /// </summary>
    [HttpGet("me")]
    public async Task<IActionResult> Me(
        [FromQuery] AnalyticsPeriod period = AnalyticsPeriod.Quarter,
        [FromQuery] bool optional = false,
        CancellationToken ct = default)
    {
        var me = await _directory.FindMeAsync(ct);
        // optional=true: calisan kaydi olmayan hesaplar icin 204 (bkz. EmployeesController.GetMe).
        if (me is null && optional) return NoContent();
        if (me is null)
            return NotFound(new { message = "Bu hesaba bağlı çalışan kaydı bulunamadı" });

        var report = await BuildEmployeeReportAsync(me.Id, period, ct);

        // Calisanin kendi geri bildirimleri - KIMDEN geldigi dahil.
        var since = period.StartOf(DateTimeOffset.UtcNow);
        var fq = _db.Feedback.Where(f => f.ToEmployeeId == me.Id && f.VisibleToEmployee);
        if (since is not null) fq = fq.Where(f => f.CreatedAt >= since.Value);
        var feedback = await fq.OrderByDescending(f => f.CreatedAt).ToListAsync(ct);

        var senderIds = feedback.Select(f => f.FromEmployeeId).Distinct().ToList();
        var senderNames = await _directory.GetEmployeeNamesAsync(senderIds, ct);

        return Ok(new
        {
            employee = new { me.Id, me.FirstName, me.LastName, me.Email },
            report,
            feedback = feedback.Select(f => new
            {
                f.Id,
                from = new
                {
                    employeeId = f.FromEmployeeId,
                    name = senderNames.GetValueOrDefault(f.FromEmployeeId, "—"),
                },
                reason = f.Reason.ToString(),
                f.ReasonDetail,
                sentiment = f.Sentiment.ToString(),
                f.Body,
                f.CreatedAt,
                f.ReadAt,
            }),
            unreadFeedback = feedback.Count(f => f.ReadAt is null),
        });
    }

    // ------------------------------------------------------------------

    private async Task<object> BuildEmployeeReportAsync(
        Guid employeeId, AnalyticsPeriod period, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var start = period.StartOf(now);

        var q = _db.Snapshots.Where(s => s.EmployeeId == employeeId);
        if (start is not null) q = q.Where(s => s.CapturedAt >= start.Value);
        var snapshots = await q.OrderBy(s => s.CapturedAt).ToListAsync(ct);

        // Onceki esdeger pencere - degisim orani icin
        decimal? previousAverage = null;
        var prev = period.PreviousWindow(now);
        if (prev is not null)
        {
            var prevScores = await _db.Snapshots
                .Where(s => s.EmployeeId == employeeId
                         && s.CapturedAt >= prev.Value.Start
                         && s.CapturedAt < prev.Value.End)
                .Select(s => s.Score).ToListAsync(ct);

            if (prevScores.Count > 0) previousAverage = Math.Round(prevScores.Average(), 2);
        }

        var series = snapshots
            .GroupBy(s => period.BucketOf(s.CapturedAt))
            .OrderBy(g => g.Key)
            .Select(g => new
            {
                bucket = g.Key,
                score = Math.Round(g.Average(s => s.Score), 2),
                goalScore = Math.Round(g.Average(s => s.GoalScore), 2),
                metricScore = Math.Round(g.Average(s => s.MetricScore), 2),
                isProvisional = g.All(s => s.IsProvisional),
            })
            .ToList();

        var latest = snapshots.LastOrDefault();
        var current = latest?.Score;
        var slope = Statistics.TrendSlope(series.Select(s => s.score).ToList());

        // Geri bildirim ozeti
        var fq = _db.Feedback.Where(f => f.ToEmployeeId == employeeId);
        if (start is not null) fq = fq.Where(f => f.CreatedAt >= start.Value);
        var feedback = await fq.ToListAsync(ct);

        return new
        {
            employeeId,
            period = period.ToString(),
            periodLabel = period.Label(),
            dataPoints = snapshots.Count,
            current,
            isProvisional = latest?.IsProvisional ?? true,
            provisionalReason = latest?.ProvisionalReason,
            previousAverage,
            change = current is not null && previousAverage is not null
                ? Math.Round(current.Value - previousAverage.Value, 2)
                : (decimal?)null,
            trendSlope = slope,
            trendLabel = Statistics.TrendLabel(slope),
            best = snapshots.Count == 0 ? null : (decimal?)snapshots.Max(s => s.Score),
            worst = snapshots.Count == 0 ? null : (decimal?)snapshots.Min(s => s.Score),
            series,
            feedbackSummary = new
            {
                total = feedback.Count,
                positive = feedback.Count(f => f.Sentiment == FeedbackSentiment.Positive),
                constructive = feedback.Count(f => f.Sentiment == FeedbackSentiment.Constructive),
                byReason = feedback.GroupBy(f => f.Reason)
                    .Select(g => new { reason = g.Key.ToString(), count = g.Count() })
                    .OrderByDescending(x => x.count),
            },
        };
    }

    private async Task<object> BuildComparisonAsync(
        Guid employeeId, Guid teamId, AnalyticsPeriod period, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var start = period.StartOf(now);

        var members = await _directory.GetTeamMembersAsync(teamId, ct);
        var memberIds = members.Select(m => m.EmployeeId).ToList();

        var q = _db.Snapshots.Where(s => memberIds.Contains(s.EmployeeId));
        if (start is not null) q = q.Where(s => s.CapturedAt >= start.Value);
        var snapshots = await q.ToListAsync(ct);

        // Her uyenin en guncel puani
        var latestByEmployee = snapshots
            .GroupBy(s => s.EmployeeId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.CapturedAt).First());

        var teamScores = latestByEmployee.Values.Select(s => s.Score).ToList();
        var mine = latestByEmployee.GetValueOrDefault(employeeId);

        // Zaman serisinde calisan ve ekip ortalamasi yan yana
        var buckets = snapshots
            .GroupBy(s => period.BucketOf(s.CapturedAt))
            .OrderBy(g => g.Key)
            .Select(g => new
            {
                bucket = g.Key,
                employee = g.Where(s => s.EmployeeId == employeeId)
                    .Select(s => (decimal?)s.Score).FirstOrDefault(),
                teamAverage = Math.Round(g.Average(s => s.Score), 2),
            })
            .ToList();

        var rank = mine is null
            ? (int?)null
            : teamScores.Count(v => v > mine.Score) + 1;

        return new
        {
            employeeId,
            teamId,
            period = period.ToString(),
            periodLabel = period.Label(),
            employeeScore = mine?.Score,
            employeeIsProvisional = mine?.IsProvisional,
            team = teamScores.Count == 0 ? null : new
            {
                memberCount = members.Count,
                scoredCount = teamScores.Count,
                average = Math.Round(teamScores.Average(), 2),
                median = Statistics.Median(teamScores),
                min = teamScores.Min(),
                max = teamScores.Max(),
                stdDev = Statistics.StdDev(teamScores),
            },
            comparison = mine is null || teamScores.Count == 0 ? null : new
            {
                differenceFromAverage = Math.Round(mine.Score - teamScores.Average(), 2),
                percentile = Statistics.Percentile(mine.Score, teamScores),
                rank,
                outOf = teamScores.Count,
                // Ekip dagilimi dar mi genis mi - tek basina siralama
                // yaniltici olabilir, 1 puan farkla birinci olmak gibi.
                spreadNote = Statistics.StdDev(teamScores) < 5m
                    ? "Ekip içi farklar küçük; sıralama tek başına anlamlı değil"
                    : null,
            },
            series = buckets,
        };
    }
}

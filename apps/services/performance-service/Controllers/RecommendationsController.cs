using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PerformanceService.Data;
using PerformanceService.Models;
using PerformanceService.Services;

namespace PerformanceService.Controllers;

/// <summary>
/// Yoneticiye aksiyon onerileri: kimi terfi degerlendirmesine almali,
/// kime gelisim plani yapmali, kimi izlemeli.
///
/// Oneriler KURAL TABANLI uretilir ve sirketin kendi esikleriyle
/// (ScoringConfig) hesaplanir. Her oneri, hangi etkenlerin ne yonde
/// etkiledigini acikca tasir - kariyeri etkileyen bir karar
/// gerekcelendirilebilir olmali.
///
/// ML katmani ayri bir alanda sunulur (anomali, yorunge tahmini);
/// karari o vermez, yoneticiye ek bilgi verir.
/// </summary>
[ApiController]
[Route("api/recommendations")]
[Authorize(Policy = "RequireManagerOrAbove")]
public class RecommendationsController : ControllerBase
{
    private readonly PerformanceDbContext _db;
    private readonly RecommendationEngine _engine;
    private readonly SnapshotService _snapshots;
    private readonly DirectoryClient _directory;
    private readonly PerformanceMlClient _ml;

    public RecommendationsController(
        PerformanceDbContext db, RecommendationEngine engine, SnapshotService snapshots,
        DirectoryClient directory, PerformanceMlClient ml)
    {
        _db = db;
        _engine = engine;
        _snapshots = snapshots;
        _directory = directory;
        _ml = ml;
    }

    /// <summary>
    /// Bir ekip (ya da teamId verilmezse tum kiraci) icin aksiyon onerileri.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] Guid? teamId,
        [FromQuery] bool includeMlSignals = true,
        CancellationToken ct = default)
    {
        var config = await _snapshots.ActiveConfigAsync(ct);

        // Kapsamdaki calisanlar
        List<Guid> employeeIds;
        if (teamId.HasValue)
        {
            var members = await _directory.GetTeamMembersAsync(teamId.Value, ct);
            employeeIds = members.Select(m => m.EmployeeId).ToList();
        }
        else
        {
            employeeIds = await _db.Snapshots
                .Select(s => s.EmployeeId).Distinct().ToListAsync(ct);
        }

        if (employeeIds.Count == 0)
            return Ok(new { teamId, count = 0, recommendations = Array.Empty<object>() });

        // Tum anlik goruntuleri tek seferde cek - calisan basina sorgu atmak
        // 100 kisilik ekipte 100 gidis-donus demek olurdu.
        var allSnapshots = await _db.Snapshots
            .Where(s => employeeIds.Contains(s.EmployeeId))
            .OrderBy(s => s.CapturedAt)
            .ToListAsync(ct);

        var allFeedback = await _db.Feedback
            .Where(f => employeeIds.Contains(f.ToEmployeeId))
            .ToListAsync(ct);

        var byEmployee = allSnapshots.GroupBy(s => s.EmployeeId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Ekip yuzdelik dilimi icin herkesin son puani
        var latestScores = byEmployee.Values
            .Select(list => list[^1].Score).ToList();

        var signals = new List<EmployeeSignals>();
        foreach (var id in employeeIds)
        {
            if (!byEmployee.TryGetValue(id, out var snaps) || snaps.Count == 0)
            {
                signals.Add(new EmployeeSignals(id, null, true, 0, 0m, 0m, null, 0, 0, 0, null, 0, 0, 0));
                continue;
            }

            var latest = snaps[^1];
            var series = snaps.Select(s => s.Score).ToList();
            var fb = allFeedback.Where(f => f.ToEmployeeId == id).ToList();

            signals.Add(new EmployeeSignals(
                EmployeeId: id,
                CurrentScore: latest.Score,
                IsProvisional: latest.IsProvisional,
                ReviewCount: latest.ReviewCount,
                TrendSlope: Statistics.TrendSlope(series),
                Volatility: Statistics.StdDev(series),
                TeamPercentile: latestScores.Count > 1
                    ? Statistics.Percentile(latest.Score, latestScores) : null,
                ConsecutivePeriodsAbovePromotion: CountTrailing(series, v => v >= config.PromotionThreshold),
                ConsecutivePeriodsBelowImprovement: CountTrailing(series, v => v < config.ImprovementThreshold),
                CycleCount: snaps.Select(s => s.CycleId).Distinct().Count(),
                GoalScore: latest.GoalScore,
                PositiveFeedback: fb.Count(f => f.Sentiment == FeedbackSentiment.Positive),
                ConstructiveFeedback: fb.Count(f => f.Sentiment == FeedbackSentiment.Constructive),
                MonthsSinceHire: 24));   // employee-service'ten zenginlestirilebilir

        }

        var recommendations = signals
            .Select(s => _engine.Evaluate(s, config))
            .ToList();

        // --- ML sinyalleri (opsiyonel, karari etkilemez) ---
        MlAnalyzeResponse? ml = null;
        if (includeMlSignals)
        {
            var payload = new
            {
                employees = byEmployee.Select(kv => new
                {
                    employee_id = kv.Key.ToString(),
                    points = kv.Value.Select(s => new
                    {
                        captured_at = s.CapturedAt,
                        score = s.Score,
                        is_provisional = s.IsProvisional,
                    }),
                    goal_score = kv.Value[^1].GoalScore,
                    review_count = kv.Value[^1].ReviewCount,
                    months_since_hire = 24,
                }),
                forecast_periods = 2,
            };

            ml = await _ml.AnalyzeAsync(payload, ct);
        }

        var mlByEmployee = ml?.Results
            .ToDictionary(r => r.EmployeeId, r => r)
            ?? new Dictionary<string, MlEmployeeAnalysis>();

        var names = await _directory.GetEmployeeNamesAsync(employeeIds, ct);

        var rows = recommendations.Select(r =>
        {
            mlByEmployee.TryGetValue(r.EmployeeId.ToString(), out var mlRow);
            return new
            {
                employeeId = r.EmployeeId,
                name = names.GetValueOrDefault(r.EmployeeId, "—"),
                action = r.Action.ToString(),
                actionLabel = r.ActionLabel,
                confidence = r.Confidence,
                summary = r.Summary,
                factors = r.Factors.Select(f => new
                {
                    f.Code, f.Label, f.Contribution, f.Explanation,
                }),
                cautions = r.Cautions,
                // ML ayri alanda: karari etkilemedigi acikca gorulsun
                mlSignals = mlRow is null ? null : new
                {
                    anomaly = mlRow.Anomaly,
                    anomalyReason = mlRow.AnomalyReason,
                    forecast = mlRow.Forecast is null ? null : new
                    {
                        periodsAhead = mlRow.Forecast.PeriodsAhead,
                        predictedScore = mlRow.Forecast.PredictedScore,
                        confidence = mlRow.Forecast.Confidence,
                        basis = mlRow.Forecast.Basis,
                    },
                    dataQuality = mlRow.DataQuality,
                    notes = mlRow.Notes,
                },
            };
        })
        .OrderBy(r => ActionOrder(r.action))
        .ToList();

        return Ok(new
        {
            teamId,
            configVersion = config.Version,
            thresholds = new
            {
                promotion = config.PromotionThreshold,
                recognition = config.RecognitionThreshold,
                improvement = config.ImprovementThreshold,
                critical = config.CriticalThreshold,
                consecutivePeriodsForPromotion = config.PromotionConsecutivePeriods,
            },
            count = rows.Count,
            summary = rows.GroupBy(r => r.actionLabel)
                .Select(g => new { action = g.Key, count = g.Count() })
                .OrderByDescending(x => x.count),
            mlLayer = new
            {
                used = ml?.ModelUsed ?? false,
                skipReason = ml?.ModelSkipReason
                    ?? (includeMlSignals ? "ML servisine ulaşılamadı" : "İstenmedi"),
                anomalyCount = ml?.AnomalyCount ?? 0,
                note = "ML katmanı karar vermez; kural motoruna ek sinyal sağlar.",
            },
            recommendations = rows,
        });
    }

    /// <summary>Tek bir calisan icin ayrintili oneri.</summary>
    [HttpGet("employee/{employeeId}")]
    public async Task<IActionResult> ForEmployee(Guid employeeId, CancellationToken ct = default)
    {
        var config = await _snapshots.ActiveConfigAsync(ct);

        var snaps = await _db.Snapshots
            .Where(s => s.EmployeeId == employeeId)
            .OrderBy(s => s.CapturedAt).ToListAsync(ct);

        if (snaps.Count == 0)
            return NotFound(new { message = "Bu çalışan için puan verisi yok" });

        var fb = await _db.Feedback.Where(f => f.ToEmployeeId == employeeId).ToListAsync(ct);
        var series = snaps.Select(s => s.Score).ToList();
        var latest = snaps[^1];

        var signals = new EmployeeSignals(
            employeeId, latest.Score, latest.IsProvisional, latest.ReviewCount,
            Statistics.TrendSlope(series), Statistics.StdDev(series), null,
            CountTrailing(series, v => v >= config.PromotionThreshold),
            CountTrailing(series, v => v < config.ImprovementThreshold),
            snaps.Select(s => s.CycleId).Distinct().Count(),
            latest.GoalScore,
            fb.Count(f => f.Sentiment == FeedbackSentiment.Positive),
            fb.Count(f => f.Sentiment == FeedbackSentiment.Constructive),
            24);

        var r = _engine.Evaluate(signals, config);

        return Ok(new
        {
            employeeId,
            action = r.Action.ToString(),
            actionLabel = r.ActionLabel,
            confidence = r.Confidence,
            summary = r.Summary,
            factors = r.Factors,
            cautions = r.Cautions,
            signals = new
            {
                currentScore = signals.CurrentScore,
                trendSlope = signals.TrendSlope,
                volatility = signals.Volatility,
                consecutiveAbovePromotion = signals.ConsecutivePeriodsAbovePromotion,
                consecutiveBelowImprovement = signals.ConsecutivePeriodsBelowImprovement,
                cycleCount = signals.CycleCount,
            },
            thresholds = new
            {
                promotion = config.PromotionThreshold,
                recognition = config.RecognitionThreshold,
                improvement = config.ImprovementThreshold,
                critical = config.CriticalThreshold,
            },
        });
    }

    /// <summary>Serinin SONUNDAN geriye dogru kosulu saglayan ardisik nokta sayisi.</summary>
    private static int CountTrailing(IReadOnlyList<decimal> series, Func<decimal, bool> predicate)
    {
        var count = 0;
        for (var i = series.Count - 1; i >= 0; i--)
        {
            if (!predicate(series[i])) break;
            count++;
        }
        return count;
    }

    /// <summary>Listede once dikkat gerektirenler gorunsun.</summary>
    private static int ActionOrder(string action) => action switch
    {
        nameof(RecommendedAction.UrgentAction) => 0,
        nameof(RecommendedAction.DevelopmentPlan) => 1,
        nameof(RecommendedAction.PromotionCandidate) => 2,
        nameof(RecommendedAction.Monitor) => 3,
        nameof(RecommendedAction.Recognition) => 4,
        _ => 5,
    };
}

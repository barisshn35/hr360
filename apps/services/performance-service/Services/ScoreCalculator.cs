using PerformanceService.Models;

namespace PerformanceService.Services;

/// <summary>Tek bir metrigin, tum degerlendiriciler birlestirildikten sonraki sonucu.</summary>
public record MetricBreakdown(
    Guid MetricId,
    string Code,
    string Name,
    MetricCategory Category,
    decimal NormalizedScore,
    decimal Weight,
    int ReviewCount);

/// <summary>Kategori bazinda toplanmis sonuc.</summary>
public record CategoryBreakdown(
    MetricCategory Category,
    decimal Score,
    decimal Weight,
    IReadOnlyList<MetricBreakdown> Metrics);

/// <summary>Nihai puan ve nasil olustugunun tam dokumu.</summary>
public record ScoreResult(
    decimal FinalScore,
    decimal GoalScore,
    decimal MetricScore,
    bool IsProvisional,
    string? ProvisionalReason,
    int ReviewCount,
    IReadOnlyList<CategoryBreakdown> Categories,
    IReadOnlyList<GoalBreakdown> Goals);

public record GoalBreakdown(
    Guid GoalId,
    string Title,
    decimal Achievement,
    int Weight,
    GoalStatus Status);

/// <summary>
/// Performans puanini hesaplar.
///
/// Hesap dort katmanli:
///
///   1. Her metrik icin: degerlendiricilerin puanlari, DEGERLENDIRICI TIPI
///      agirligiyla birlestirilir (yonetici x2, oz degerlendirme x0.5 gibi).
///      Farkli olcekler (1-5, 1-10, %) once 0-100'e normalize edilir.
///
///   2. Kategori icinde: metrikler kendi agirliklariyla birlestirilir.
///      Agirliklar oransal - yonetici metrik ekleyip cikardiginda
///      digerlerini elle duzeltmek zorunda kalmaz.
///
///   3. Kategoriler arasi: kategori agirliklariyla birlestirilir.
///
///   4. Hedefler + metrikler: ScoringConfig'teki yuzde dagilimiyla
///      nihai puan bulunur.
///
/// Her adimin dokumu ScoreResult icinde doner - "bu puan nereden geldi"
/// sorusu arayuzde tam olarak cevaplanabilsin diye. Kapali kutu bir puan
/// performans degerlendirmesinde guven kirar.
/// </summary>
public class ScoreCalculator
{
    /// <param name="config">Kiracinin puanlama ayari.</param>
    /// <param name="metrics">Gecerli metrik tanimlari.</param>
    /// <param name="reviews">Donemdeki degerlendirmeler (gonderilmis olanlar).</param>
    /// <param name="scores">Degerlendirmelere ait metrik puanlari.</param>
    /// <param name="goals">Donemdeki hedefler.</param>
    public ScoreResult Calculate(
        ScoringConfig config,
        IReadOnlyList<MetricDefinition> metrics,
        IReadOnlyList<Review> reviews,
        IReadOnlyList<ReviewScore> scores,
        IReadOnlyList<Goal> goals)
    {
        var submitted = reviews.Where(r => r.SubmittedAt is not null).ToList();

        // --- Gecerlilik kontrolu ---
        string? provisionalReason = null;
        if (submitted.Count == 0)
            provisionalReason = "Gönderilmiş değerlendirme yok.";
        else if (submitted.Count < config.MinReviewsForValidScore)
            provisionalReason = $"En az {config.MinReviewsForValidScore} değerlendirme gerekiyor, {submitted.Count} var.";
        else if (!config.AllowSelfOnlyScore && submitted.All(r => r.Type == ReviewType.Self))
            provisionalReason = "Yalnızca öz değerlendirme var.";

        // --- 1 & 2: metrik ve kategori puanlari ---
        var scoresByMetric = scores
            .Where(s => submitted.Any(r => r.Id == s.ReviewId))
            .GroupBy(s => s.MetricId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var reviewById = submitted.ToDictionary(r => r.Id);
        var metricResults = new List<MetricBreakdown>();

        foreach (var metric in metrics.Where(m => m.IsActive))
        {
            if (!scoresByMetric.TryGetValue(metric.Id, out var metricScores) || metricScores.Count == 0)
                continue;

            // Degerlendirici tipi agirligiyla toplama
            decimal weightedSum = 0m, weightTotal = 0m;
            foreach (var s in metricScores)
            {
                if (!reviewById.TryGetValue(s.ReviewId, out var review)) continue;
                var w = config.WeightFor(review.Type);
                if (w <= 0m) continue;   // agirligi sifir olan kaynak hesaba girmez

                weightedSum += metric.Normalize(s.Value) * w;
                weightTotal += w;
            }

            if (weightTotal <= 0m) continue;

            metricResults.Add(new MetricBreakdown(
                metric.Id, metric.Code, metric.Name, metric.Category,
                Math.Round(weightedSum / weightTotal, 2),
                metric.Weight,
                metricScores.Count));
        }

        // --- 3: kategoriler ---
        var categories = new List<CategoryBreakdown>();
        foreach (var group in metricResults.GroupBy(m => m.Category))
        {
            var catWeight = config.WeightFor(group.Key);
            if (catWeight <= 0m) continue;   // kiraci bu kategoriyi kullanmiyor

            // Metrik agirliklari oransal normalize edilir
            var wTotal = group.Sum(m => m.Weight);
            var catScore = wTotal <= 0m
                ? group.Average(m => m.NormalizedScore)
                : group.Sum(m => m.NormalizedScore * m.Weight) / wTotal;

            categories.Add(new CategoryBreakdown(
                group.Key, Math.Round(catScore, 2), catWeight, group.ToList()));
        }

        var categoryWeightTotal = categories.Sum(c => c.Weight);
        var metricScore = categoryWeightTotal <= 0m
            ? 0m
            : categories.Sum(c => c.Score * c.Weight) / categoryWeightTotal;

        // --- Hedefler ---
        var goalResults = goals
            .Where(g => g.Status != GoalStatus.Cancelled && g.Status != GoalStatus.Draft)
            .Select(g => new GoalBreakdown(g.Id, g.Title, GoalAchievement(g), g.Weight, g.Status))
            .ToList();

        var goalWeightTotal = goalResults.Sum(g => g.Weight);
        var goalScore = goalWeightTotal <= 0m
            ? 0m
            : goalResults.Sum(g => g.Achievement * g.Weight) / goalWeightTotal;

        // --- 4: nihai birlestirme ---
        // Bir taraf hic veri icermiyorsa (hedef yok ya da metrik yok) digerini
        // tam agirlikla kullaniriz - aksi halde eksik veri puani haksiz dusurur.
        decimal finalScore;
        var hasGoals = goalResults.Count > 0;
        var hasMetrics = metricResults.Count > 0;

        if (hasGoals && hasMetrics)
        {
            var gw = config.GoalWeightPercent;
            var mw = config.MetricWeightPercent;
            var total = gw + mw;
            finalScore = total <= 0 ? 0m : (goalScore * gw + metricScore * mw) / total;
        }
        else if (hasGoals) finalScore = goalScore;
        else if (hasMetrics) finalScore = metricScore;
        else
        {
            finalScore = 0m;
            provisionalReason ??= "Hesaplanacak hedef ya da metrik puani yok";
        }

        return new ScoreResult(
            Math.Round(finalScore, 2),
            Math.Round(goalScore, 2),
            Math.Round(metricScore, 2),
            provisionalReason is not null,
            provisionalReason,
            submitted.Count,
            categories,
            goalResults);
    }

    /// <summary>
    /// Hedefin gerceklesme yuzdesi.
    /// Sayisal hedefte oran, degilse duruma gore sabit deger.
    /// Asirt gerceklestirme 100'de kirpilir - %300 yapan bir hedef
    /// digerlerinin zayifligini ortmesin.
    /// </summary>
    public static decimal GoalAchievement(Goal g)
    {
        if (g.TargetValue is > 0m && g.CurrentValue is not null)
            return Math.Clamp(g.CurrentValue.Value / g.TargetValue.Value * 100m, 0m, 100m);

        return g.Status switch
        {
            GoalStatus.Achieved => 100m,
            GoalStatus.Missed => 0m,
            GoalStatus.Active => 50m,   // devam ediyor - notr sayilir
            _ => 0m,
        };
    }
}

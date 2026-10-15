using PerformanceService.Models;

namespace PerformanceService.Services;

/// <summary>Yoneticiye onerilen aksiyon.</summary>
public enum RecommendedAction
{
    /// <summary>Terfi degerlendirmesine alinmali.</summary>
    PromotionCandidate,
    /// <summary>Takdir edilmeli - terfi esiginde degil ama guclu.</summary>
    Recognition,
    /// <summary>Beklenen aralikta, ozel aksiyon gerekmiyor.</summary>
    NoAction,
    /// <summary>Izlenmeli - dusus egilimi ya da belirsizlik var.</summary>
    Monitor,
    /// <summary>Gelisim plani yapilmali.</summary>
    DevelopmentPlan,
    /// <summary>Acil mudahale gerekiyor.</summary>
    UrgentAction,
}

/// <summary>
/// Karara katkida bulunan tek bir etken.
/// Her oneri, hangi etkenlerin ne yonde etkiledigini acikca tasir -
/// "sistem boyle dedi" savunulamaz bir gerekcedir.
/// </summary>
public record DecisionFactor(
    string Code,
    string Label,
    /// <summary>Karara katki: pozitif olumlu yonde, negatif olumsuz yonde.</summary>
    decimal Contribution,
    string Explanation);

public record ActionRecommendation(
    Guid EmployeeId,
    RecommendedAction Action,
    string ActionLabel,
    /// <summary>0-1. Dusuk guven, verinin yetersiz oldugu anlamina gelir.</summary>
    decimal Confidence,
    string Summary,
    IReadOnlyList<DecisionFactor> Factors,
    IReadOnlyList<string> Cautions);

/// <summary>Motoru besleyen, bir calisan hakkinda toplanmis veriler.</summary>
public record EmployeeSignals(
    Guid EmployeeId,
    decimal? CurrentScore,
    bool IsProvisional,
    int ReviewCount,
    decimal TrendSlope,
    decimal Volatility,
    decimal? TeamPercentile,
    int ConsecutivePeriodsAbovePromotion,
    int ConsecutivePeriodsBelowImprovement,
    int CycleCount,
    decimal? GoalScore,
    int PositiveFeedback,
    int ConstructiveFeedback,
    int MonthsSinceHire);

/// <summary>
/// Aksiyon onerisi uretir.
///
/// TASARIM TERCIHI: Bu motor KURAL TABANLI, kapali kutu bir model degil.
///
/// Sebep: Terfi ve gelisim plani kararlari insanlarin kariyerini
/// etkiler. Birinin terfisi reddedildiginde gerekce gosterebilmek
/// gerekir; "model boyle hesapladi" hukuken de etik olarak da
/// savunulamaz. Ayrica esikleri her sirket kendi belirliyor
/// (ScoringConfig) - kurallar bu ayarlari dogrudan kullanir.
///
/// Makine ogrenmesi, kurallarin ifade EDEMEDIGI yerde devreye giriyor
/// (anomali tespiti, yorunge tahmini) ve ayri bir sinyal olarak
/// sunuluyor - karari o vermiyor, yoneticiye ek bilgi veriyor.
/// </summary>
public class RecommendationEngine
{
    public ActionRecommendation Evaluate(EmployeeSignals s, ScoringConfig config)
    {
        var factors = new List<DecisionFactor>();
        var cautions = new List<string>();

        // --- Veri yeterliligi: once guvenilirlik ---
        decimal confidence = 1.0m;

        if (s.CurrentScore is null)
        {
            return new ActionRecommendation(
                s.EmployeeId, RecommendedAction.NoAction, Label(RecommendedAction.NoAction),
                0m, "Henüz puan hesaplanmamış.",
                Array.Empty<DecisionFactor>(),
                new[] { "Bu çalışan için değerlendirme verisi yok." });
        }

        var score = s.CurrentScore.Value;

        if (s.IsProvisional)
        {
            confidence -= 0.35m;
            cautions.Add("Puan geçici: yeterli değerlendirme yok, sonuç değişebilir.");
        }

        if (s.ReviewCount < config.MinReviewsForValidScore)
        {
            confidence -= 0.15m;
            cautions.Add($"Yalnızca {s.ReviewCount} değerlendirme var.");
        }

        if (s.CycleCount < 2)
        {
            confidence -= 0.2m;
            cautions.Add("Tek dönem verisi var; eğilim çıkarılamıyor.");
        }

        if (s.MonthsSinceHire < 6)
        {
            confidence -= 0.1m;
            cautions.Add($"İşe girişten bu yana {s.MonthsSinceHire} ay geçmiş.");
        }

        // --- Ana etken: mevcut puan ---
        if (score >= config.PromotionThreshold)
            factors.Add(new DecisionFactor("score", "Mevcut puan", 2.0m,
                $"{score} puan, terfi eşiğinin ({config.PromotionThreshold}) üzerinde."));
        else if (score >= config.RecognitionThreshold)
            factors.Add(new DecisionFactor("score", "Mevcut puan", 1.0m,
                $"{score} puan, takdir eşiğinin ({config.RecognitionThreshold}) üzerinde."));
        else if (score < config.CriticalThreshold)
            factors.Add(new DecisionFactor("score", "Mevcut puan", -2.5m,
                $"{score} puan, kritik eşiğin ({config.CriticalThreshold}) altında."));
        else if (score < config.ImprovementThreshold)
            factors.Add(new DecisionFactor("score", "Mevcut puan", -1.5m,
                $"{score} puan, gelişim eşiğinin ({config.ImprovementThreshold}) altında."));
        else
            factors.Add(new DecisionFactor("score", "Mevcut puan", 0m,
                $"{score} puan, beklenen aralıkta."));

        // --- Sureklilik: tek donemlik parlama terfi sebebi degil ---
        if (s.ConsecutivePeriodsAbovePromotion >= config.PromotionConsecutivePeriods)
            factors.Add(new DecisionFactor("consistency", "Süreklilik", 1.5m,
                $"{s.ConsecutivePeriodsAbovePromotion} dönem üst üste terfi eşiğinin üzerinde."));
        else if (score >= config.PromotionThreshold && s.ConsecutivePeriodsAbovePromotion < config.PromotionConsecutivePeriods)
            factors.Add(new DecisionFactor("consistency", "Süreklilik", -0.8m,
                $"Puan yüksek ama süreklilik yok ({s.ConsecutivePeriodsAbovePromotion}/{config.PromotionConsecutivePeriods} dönem)."));

        if (s.ConsecutivePeriodsBelowImprovement >= 2)
            factors.Add(new DecisionFactor("persistent-low", "Süregelen düşüklük", -1.5m,
                $"{s.ConsecutivePeriodsBelowImprovement} dönem üst üste gelişim eşiğinin altında."));

        // --- Egilim ---
        if (s.TrendSlope > 1.5m)
            factors.Add(new DecisionFactor("trend", "Eğilim", 1.2m,
                "Belirgin yükseliş eğilimi."));
        else if (s.TrendSlope > 0.3m)
            factors.Add(new DecisionFactor("trend", "Eğilim", 0.6m, "Yükseliş eğilimi."));
        else if (s.TrendSlope < -1.5m)
            factors.Add(new DecisionFactor("trend", "Eğilim", -1.5m,
                "Belirgin düşüş eğilimi."));
        else if (s.TrendSlope < -0.3m)
            factors.Add(new DecisionFactor("trend", "Eğilim", -0.7m, "Düşüş eğilimi."));

        // --- Ekip icindeki yeri ---
        if (s.TeamPercentile is { } pct)
        {
            if (pct >= 80m)
                factors.Add(new DecisionFactor("percentile", "Ekip içi konum", 0.8m,
                    $"Ekipte üst %{Math.Round(100 - pct)} dilimde."));
            else if (pct <= 20m)
                factors.Add(new DecisionFactor("percentile", "Ekip içi konum", -0.8m,
                    $"Ekipte alt %{Math.Round(pct)} dilimde."));
        }

        // --- Oynaklik ---
        if (s.Volatility > 15m)
        {
            factors.Add(new DecisionFactor("volatility", "Oynaklık", -0.5m,
                $"Puanlar dönemler arası çok değişken (sapma {s.Volatility})."));
            cautions.Add("Performans dalgalı; tek bir dönemin sonucuna dayanmayın.");
        }

        // --- Geri bildirim tonu ---
        var totalFeedback = s.PositiveFeedback + s.ConstructiveFeedback;
        if (totalFeedback >= 3)
        {
            var ratio = (decimal)s.PositiveFeedback / totalFeedback;
            if (ratio >= 0.75m)
                factors.Add(new DecisionFactor("feedback", "Geri bildirim tonu", 0.5m,
                    $"{s.PositiveFeedback}/{totalFeedback} geri bildirim olumlu."));
            else if (ratio <= 0.3m)
                factors.Add(new DecisionFactor("feedback", "Geri bildirim tonu", -0.6m,
                    $"{s.ConstructiveFeedback}/{totalFeedback} geri bildirim yapıcı/eleştirel."));
        }
        else if (totalFeedback == 0)
        {
            cautions.Add("Bu çalışana hiç geri bildirim yazılmamış.");
        }

        // --- Hedef gerceklestirme ---
        if (s.GoalScore is { } g)
        {
            if (g >= 90m)
                factors.Add(new DecisionFactor("goals", "Hedefler", 0.7m,
                    $"Hedeflerin %{Math.Round(g)}'i gerçekleşmiş."));
            else if (g < 50m)
                factors.Add(new DecisionFactor("goals", "Hedefler", -0.8m,
                    $"Hedeflerin yalnızca %{Math.Round(g)}'i gerçekleşmiş."));
        }

        // --- Karar ---
        var net = factors.Sum(f => f.Contribution);
        var action = Decide(score, net, s, config);

        confidence = Math.Clamp(confidence, 0.1m, 1.0m);

        // Terfi onerisi dusuk guvenle verilmemeli: veri yetersizse
        // oneriyi "izle"ye dusuruyoruz, yanlis terfi onerisi pahaliya
        // mal olur.
        if (action == RecommendedAction.PromotionCandidate && confidence < 0.6m)
        {
            action = RecommendedAction.Monitor;
            cautions.Add("Terfi önerisi için veri yeterli değil; önce daha fazla değerlendirme toplanmalı.");
        }

        return new ActionRecommendation(
            s.EmployeeId, action, Label(action), Math.Round(confidence, 2),
            Summarize(action, score, s), 
            factors.OrderByDescending(f => Math.Abs(f.Contribution)).ToList(),
            cautions);
    }

    private static RecommendedAction Decide(
        decimal score, decimal net, EmployeeSignals s, ScoringConfig config)
    {
        // Kritik esigin altiysa baska hicbir etken bunu degistirmemeli.
        if (score < config.CriticalThreshold) return RecommendedAction.UrgentAction;

        if (score < config.ImprovementThreshold) return RecommendedAction.DevelopmentPlan;

        if (score >= config.PromotionThreshold
            && s.ConsecutivePeriodsAbovePromotion >= config.PromotionConsecutivePeriods
            && net >= 3.0m)
            return RecommendedAction.PromotionCandidate;

        if (score >= config.RecognitionThreshold && net >= 1.0m)
            return RecommendedAction.Recognition;

        // Beklenen aralikta ama dususte: izlenmeli.
        if (s.TrendSlope < -1.0m || net <= -1.5m) return RecommendedAction.Monitor;

        return RecommendedAction.NoAction;
    }

    private static string Label(RecommendedAction a) => a switch
    {
        RecommendedAction.PromotionCandidate => "Terfi adayı",
        RecommendedAction.Recognition => "Takdir edilmeli",
        RecommendedAction.NoAction => "Aksiyon gerekmiyor",
        RecommendedAction.Monitor => "İzlenmeli",
        RecommendedAction.DevelopmentPlan => "Gelişim planı",
        RecommendedAction.UrgentAction => "Acil aksiyon",
        _ => "",
    };

    private static string Summarize(RecommendedAction a, decimal score, EmployeeSignals s) => a switch
    {
        RecommendedAction.PromotionCandidate =>
            $"{score} puanla {s.ConsecutivePeriodsAbovePromotion} dönemdir eşiğin üzerinde; terfi değerlendirmesine alınabilir.",
        RecommendedAction.Recognition =>
            $"{score} puanla güçlü performans; takdir edilmesi motivasyonu pekiştirir.",
        RecommendedAction.Monitor =>
            $"{score} puan beklenen aralıkta ama eğilim dikkat gerektiriyor.",
        RecommendedAction.DevelopmentPlan =>
            $"{score} puan gelişim eşiğinin altında; birlikte bir gelişim planı yapılmalı.",
        RecommendedAction.UrgentAction =>
            $"{score} puan kritik eşiğin altında; ivedi görüşme ve destek gerekiyor.",
        _ => $"{score} puan beklenen aralıkta.",
    };
}

using LearningService.Models;

namespace LearningService.Services;

public sealed record CareerStepInput(string? PositionTitle, string? Description, int? MinMonths, List<CareerRequirementInput>? Requirements);
public sealed record CareerRequirementInput(Guid CompetencyId, int RequiredLevel);

/// <summary>
/// Dalga 11 (madde 83) kariyer yolu hesapları — saf (birim testli). Kişinin basamağı yalnızca güncel pozisyon
/// unvanından bulunur; hazırlık oranı bilgi amaçlıdır, terfi/aday kararı otomatik verilmez.
/// </summary>
public static class CareerMath
{
    public const int MaxSteps = 15;
    public const int MaxRequirements = 30;

    /// <summary>Unvanı eşleşen basamağın sırası (0 tabanlı, sıralanmış listede); yoksa null.</summary>
    public static int? CurrentIndex(IReadOnlyList<CareerStep> ordered, string? positionTitle)
    {
        var t = CompetencyMath.Normalize(positionTitle);
        if (t.Length == 0) return null;
        for (var i = 0; i < ordered.Count; i++)
            if (CompetencyMath.Normalize(ordered[i].PositionTitle) == t) return i;
        return null;
    }

    /// <summary>Basamağın beklentisine göre açıklar (yalnızca etkin yetkinlikler); güncel = son değerlendirme.</summary>
    public static List<GapItem> StepGaps(CareerStep step, IReadOnlyDictionary<Guid, int> current, ISet<Guid> activeCompetencies) =>
        CompetencyMath.Gaps(step.Requirements.Where(r => activeCompetencies.Contains(r.CompetencyId))
            .GroupBy(r => r.CompetencyId)
            .ToDictionary(g => g.Key, g => (g.Max(x => x.RequiredLevel), "Career")), current);

    /// <summary>Hazırlık yüzdesi: Σ min(güncel, beklenen) / Σ beklenen. Beklenti yoksa 100.</summary>
    public static int Readiness(IReadOnlyCollection<GapItem> gaps)
    {
        var total = gaps.Sum(g => g.Required);
        if (total == 0) return 100;
        var met = gaps.Sum(g => Math.Min(g.Required, g.Current ?? 0));
        return (int)Math.Round(100.0 * met / total, MidpointRounding.AwayFromZero);
    }

    /// <summary>Kariyer yolu girdisinin doğrulaması; hata metni ya da null.</summary>
    public static string? Validate(string? name, string? description, IReadOnlyList<CareerStepInput>? steps, ISet<Guid> knownCompetencies)
    {
        var n = name?.Trim() ?? "";
        if (n.Length is < 2 or > 150) return "Kariyer yolu adı 2-150 karakter olmalı";
        if (description is { Length: > 1000 }) return "Açıklama en fazla 1000 karakter olabilir";
        if (steps is null || steps.Count == 0) return "En az bir basamak ekleyin";
        if (steps.Count > MaxSteps) return $"En fazla {MaxSteps} basamak olabilir";
        var titles = new HashSet<string>();
        foreach (var s in steps)
        {
            var t = s.PositionTitle?.Trim() ?? "";
            if (t.Length is < 2 or > 150) return "Basamak unvanı 2-150 karakter olmalı";
            if (!titles.Add(CompetencyMath.Normalize(t))) return "Aynı unvan bir yolda iki kez kullanılamaz";
            if (s.Description is { Length: > 1000 }) return "Basamak açıklaması en fazla 1000 karakter olabilir";
            if (s.MinMonths is < 0 or > 240) return "Asgari süre 0-240 ay olmalı";
            var reqs = s.Requirements ?? new();
            if (reqs.Count > MaxRequirements) return $"Bir basamakta en fazla {MaxRequirements} yetkinlik olabilir";
            if (reqs.Select(r => r.CompetencyId).Distinct().Count() != reqs.Count) return "Bir basamakta aynı yetkinlik iki kez kullanılamaz";
            foreach (var r in reqs)
            {
                if (!knownCompetencies.Contains(r.CompetencyId)) return "Yetkinlik bulunamadı";
                if (r.RequiredLevel is < 1 or > 5) return "Beklenen seviye 1-5 arasında olmalı";
            }
        }
        return null;
    }
}

/// <summary>Dalga 11 (madde 84) son tarih durumu — saf.</summary>
public static class DuePlan
{
    public const string Overdue = "Overdue";
    public const string DueSoon = "DueSoon";
    public const string Ok = "Ok";
    public const int SoonDays = 30;

    public static string State(int daysLeft) => daysLeft < 0 ? Overdue : daysLeft <= SoonDays ? DueSoon : Ok;
}

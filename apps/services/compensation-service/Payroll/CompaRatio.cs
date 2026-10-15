using CompensationService.Models;

namespace CompensationService.Payroll;

/// <summary>
/// Madde 65 — ücret bandı konumu ve kapsama raporu (saf; birim testli). Bant, çalışanın ücret kaydındaki
/// kademe (Grade) ile eşleşir; aynı kademenin birden fazla bandı varsa bugün yürürlükte olan en yenisi
/// (EffectiveFrom, boşsa yılın 1 Ocak'ı) seçilir. Para birimi farklı bant kullanılmaz (kur çevrimi yok).
/// Ücret adaleti analizi (ML dalgası 2) aynı kademe/unvan bilgisini meşru etken olarak kullanır.
/// </summary>
public static class CompaRatio
{
    public const int MinGroup = 5;

    public static DateOnly Start(SalaryBand b) => b.EffectiveFrom ?? new DateOnly(b.Year, 1, 1);

    public static SalaryBand? BandFor(IEnumerable<SalaryBand> bands, string? grade, string currency, DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(grade)) return null;
        return bands.Where(b => string.Equals(b.Grade, grade.Trim(), StringComparison.OrdinalIgnoreCase) && b.Currency == currency && Start(b) <= today)
            .OrderByDescending(Start).FirstOrDefault();
    }

    public static decimal? Ratio(decimal salary, SalaryBand? band) =>
        band is { MidAmount: > 0 } ? Math.Round(salary / band.MidAmount, 3) : null;

    /// <summary>below | within | above | none</summary>
    public static string Position(decimal salary, SalaryBand? band) =>
        band is null ? "none" : salary < band.MinAmount ? "below" : salary > band.MaxAmount ? "above" : "within";

    public sealed record Item(string? Band, string? Department, decimal? CompaRatio, string Position);

    public sealed record Group(string Key, int Count, int? Below, int? Within, int? Above, decimal? AvgCompaRatio, bool Hidden);

    private static Group Summarize(string key, IReadOnlyCollection<Item> items)
    {
        if (items.Count < MinGroup) return new(key, items.Count, null, null, null, null, true);
        var ratios = items.Where(i => i.CompaRatio is not null).Select(i => i.CompaRatio!.Value).ToList();
        return new(key, items.Count, items.Count(i => i.Position == "below"), items.Count(i => i.Position == "within"),
            items.Count(i => i.Position == "above"), ratios.Count >= MinGroup ? Math.Round(ratios.Average(), 3) : null, false);
    }

    /// <summary>
    /// Bant ve bölüm kırılımı; 5'ten küçük gruplarda kırılım ve ortalama gizlenir (yalnızca kişi sayısı).
    /// Bölüm kırılımı yalnızca bantlı çalışanları sayar.
    /// </summary>
    public static object Coverage(IReadOnlyList<Item> items)
    {
        var banded = items.Where(i => i.Band is not null).ToList();
        return new
        {
            total = items.Count,
            noBand = items.Count(i => i.Band is null),
            outsideBand = banded.Count >= MinGroup ? banded.Count(i => i.Position is "below" or "above") : (int?)null,
            overall = Summarize("*", banded),
            byBand = banded.GroupBy(i => i.Band!).OrderBy(g => g.Key).Select(g => Summarize(g.Key, g.ToList())).ToList(),
            byDepartment = banded.GroupBy(i => i.Department ?? "—").OrderBy(g => g.Key).Select(g => Summarize(g.Key, g.ToList())).ToList(),
            minGroup = MinGroup,
        };
    }
}

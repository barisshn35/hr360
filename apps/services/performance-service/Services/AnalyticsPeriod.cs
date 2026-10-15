namespace PerformanceService.Services;

/// <summary>Analiz penceresi. Arayuzdeki donem secicisiyle birebir.</summary>
public enum AnalyticsPeriod
{
    Week,
    Month,
    Quarter,
    HalfYear,
    Year,
    All,
}

public static class AnalyticsPeriodExtensions
{
    /// <summary>Pencerenin baslangic tarihi. All icin null (sinir yok).</summary>
    public static DateTimeOffset? StartOf(this AnalyticsPeriod p, DateTimeOffset now)
        => p switch
        {
            AnalyticsPeriod.Week => now.AddDays(-7),
            AnalyticsPeriod.Month => now.AddMonths(-1),
            AnalyticsPeriod.Quarter => now.AddMonths(-3),
            AnalyticsPeriod.HalfYear => now.AddMonths(-6),
            AnalyticsPeriod.Year => now.AddYears(-1),
            AnalyticsPeriod.All => null,
            _ => null,
        };

    /// <summary>
    /// Onceki esdeger pencere - degisim orani icin.
    /// Ornek: son 1 ay ile ondan onceki 1 ay karsilastirilir.
    /// </summary>
    public static (DateTimeOffset Start, DateTimeOffset End)? PreviousWindow(
        this AnalyticsPeriod p, DateTimeOffset now)
    {
        var start = p.StartOf(now);
        if (start is null) return null;
        var span = now - start.Value;
        return (start.Value - span, start.Value);
    }

    public static string Label(this AnalyticsPeriod p) => p switch
    {
        AnalyticsPeriod.Week => "Son 7 gün",
        AnalyticsPeriod.Month => "Son 1 ay",
        AnalyticsPeriod.Quarter => "Son 3 ay",
        AnalyticsPeriod.HalfYear => "Son 6 ay",
        AnalyticsPeriod.Year => "Son 1 yıl",
        AnalyticsPeriod.All => "Tüm zamanlar",
        _ => "",
    };

    /// <summary>Trend grafiginde noktalarin gruplanacagi aralik.</summary>
    public static string BucketOf(this AnalyticsPeriod p, DateTimeOffset t) => p switch
    {
        // Kisa pencerelerde gun, uzunlarda ay bazinda grupla - aksi halde
        // 1 yillik grafikte 365 nokta olur, okunmaz.
        AnalyticsPeriod.Week or AnalyticsPeriod.Month => t.ToString("yyyy-MM-dd"),
        AnalyticsPeriod.Quarter or AnalyticsPeriod.HalfYear => $"{t:yyyy}-H{(t.DayOfYear - 1) / 7 + 1:00}",
        _ => t.ToString("yyyy-MM"),
    };
}

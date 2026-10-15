namespace PerformanceService.Services;

/// <summary>
/// Karsilastirma icin temel istatistikler.
/// Ayri tutuluyor cunku hem calisan-ekip hem ekip-sirket
/// karsilastirmalarinda ayni hesaplar kullaniliyor.
/// </summary>
public static class Statistics
{
    public static decimal Median(IReadOnlyList<decimal> values)
    {
        if (values.Count == 0) return 0m;
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[mid]
            : Math.Round((sorted[mid - 1] + sorted[mid]) / 2m, 2);
    }

    /// <summary>Standart sapma - ekip icindeki dagilimin ne kadar yayvan oldugu.</summary>
    public static decimal StdDev(IReadOnlyList<decimal> values)
    {
        if (values.Count < 2) return 0m;
        var mean = values.Average();
        var variance = values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1);
        return Math.Round((decimal)Math.Sqrt((double)variance), 2);
    }

    /// <summary>
    /// Bir degerin kume icindeki yuzdelik dilimi (0-100).
    /// "Ekipte ilk %20'de" gibi ifadeler bunun uzerinden kuruluyor.
    /// </summary>
    public static decimal Percentile(decimal value, IReadOnlyList<decimal> population)
    {
        if (population.Count == 0) return 0m;
        var below = population.Count(v => v < value);
        var equal = population.Count(v => v == value);
        // Ortanca yontem: esit degerlerin yarisi asagida sayilir.
        return Math.Round((below + equal / 2m) / population.Count * 100m, 1);
    }

    /// <summary>
    /// Basit dogrusal egilim (en kucuk kareler egimi).
    /// Pozitifse yukselen, negatifse dusen trend. Birim: puan / nokta.
    /// </summary>
    public static decimal TrendSlope(IReadOnlyList<decimal> series)
    {
        var n = series.Count;
        if (n < 2) return 0m;

        decimal sumX = 0, sumY = 0, sumXY = 0, sumXX = 0;
        for (var i = 0; i < n; i++)
        {
            sumX += i; sumY += series[i];
            sumXY += i * series[i]; sumXX += i * i;
        }

        var denom = n * sumXX - sumX * sumX;
        if (denom == 0) return 0m;
        return Math.Round((n * sumXY - sumX * sumY) / denom, 3);
    }

    /// <summary>Trendin insan tarafindan okunabilir yorumu.</summary>
    public static string TrendLabel(decimal slope) => slope switch
    {
        > 1.5m => "Belirgin yükseliş",
        > 0.3m => "Yükseliş",
        < -1.5m => "Belirgin düşüş",
        < -0.3m => "Düşüş",
        _ => "Sabit",
    };
}

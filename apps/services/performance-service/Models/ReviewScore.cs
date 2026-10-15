using PerformanceService.Tenancy;

namespace PerformanceService.Models;

/// <summary>
/// Bir degerlendirmede tek bir metrige verilen puan.
/// Review 1-N ReviewScore: degerlendirici her metrigi ayri puanlar.
/// </summary>
public class ReviewScore : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ReviewId { get; set; }
    public Review? Review { get; set; }

    public Guid MetricId { get; set; }
    public MetricDefinition? Metric { get; set; }

    /// <summary>Metrigin kendi olceginde ham puan (1-5, 1-10 ya da 0-100).</summary>
    public decimal Value { get; set; }

    /// <summary>Bu metrige ozel not - "neden bu puan" sorusunun cevabi.</summary>
    public string? Comment { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

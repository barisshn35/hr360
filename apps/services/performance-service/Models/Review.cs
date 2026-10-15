using PerformanceService.Tenancy;

namespace PerformanceService.Models;

/// <summary>
/// Degerlendirmenin kaynagi. Her tipin ScoringConfig'te ayri bir agirligi var
/// (yonetici degerlendirmesi akran degerlendirmesinden agir basar gibi).
/// </summary>
public enum ReviewType
{
    /// <summary>Oz degerlendirme.</summary>
    Self,
    /// <summary>Ust yonetici.</summary>
    Manager,
    /// <summary>Takim lideri (ekibin Lead'i, yonetici olmayabilir).</summary>
    TeamLead,
    /// <summary>Akran.</summary>
    Peer,
    /// <summary>Astin ustunu degerlendirmesi.</summary>
    Upward,
}

public class Review : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CycleId { get; set; }
    public Guid EmployeeId { get; set; }
    public Guid ReviewerEmployeeId { get; set; }
    public ReviewType Type { get; set; }

    /// <summary>
    /// Metrik bazli puanlar. Nihai puan bunlardan ScoreCalculator ile
    /// hesaplanir - tek bir "genel puan" alani artik yok, cunku her kiraci
    /// kendi metriklerini tanimliyor.
    /// </summary>
    public List<ReviewScore> Scores { get; set; } = new();

    public string? Strengths { get; set; }
    public string? Improvements { get; set; }
    public string? Comments { get; set; }

    public DateTimeOffset? SubmittedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

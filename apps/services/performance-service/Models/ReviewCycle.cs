using PerformanceService.Tenancy;

namespace PerformanceService.Models;

public enum CyclePeriod { Q1, Q2, Q3, Q4, H1, H2, Annual }
public enum CycleStatus { Planned, Open, InReview, Closed }

public class ReviewCycle : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public int Year { get; set; }
    public CyclePeriod Period { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public CycleStatus Status { get; set; } = CycleStatus.Planned;
    /// <summary>G12: dönem şablondan açıldıysa kaynak şablon.</summary>
    public Guid? TemplateId { get; set; }
    /// <summary>G12: bölümler/sorular, ağırlıklar ve ölçek (JSON). Boşsa metrik tanımları geçerlidir.</summary>
    public string? ConfigJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<Goal> Goals { get; set; } = new();
}

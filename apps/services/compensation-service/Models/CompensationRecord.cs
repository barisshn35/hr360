using CompensationService.Tenancy;

namespace CompensationService.Models;

public enum CompensationChangeReason
{
    Hire, AnnualIncrease, Promotion, MarketAdjustment, Demotion, Other
}

/// <summary>Ucret gecmisi: EffectiveFrom/To ile tarihsel kayit tutulur.</summary>
public class CompensationRecord : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EmployeeId { get; set; }
    public decimal BaseSalary { get; set; }
    public string Currency { get; set; } = "TRY";
    public string? Grade { get; set; }
    public CompensationChangeReason Reason { get; set; } = CompensationChangeReason.Other;
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

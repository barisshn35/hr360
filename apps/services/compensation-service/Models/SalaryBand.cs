using CompensationService.Tenancy;

namespace CompensationService.Models;

public class SalaryBand : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Grade { get; set; }
    public string? Title { get; set; }
    public decimal MinAmount { get; set; }
    public decimal MidAmount { get; set; }
    public decimal MaxAmount { get; set; }
    public string Currency { get; set; } = "TRY";
    public int Year { get; set; }
    /// <summary>Yürürlük tarihi (boşsa yılın başı).</summary>
    public DateOnly? EffectiveFrom { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

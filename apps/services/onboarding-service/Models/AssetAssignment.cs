using OnboardingService.Tenancy;

namespace OnboardingService.Models;

public class AssetAssignment : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AssetId { get; set; }
    public Asset? Asset { get; set; }
    public Guid EmployeeId { get; set; }
    public DateOnly AssignedOn { get; set; }
    public DateOnly? ReturnedOn { get; set; }
    public string? ConditionOnReturn { get; set; }
    public string? Notes { get; set; }
    /// <summary>Beklenen iade tarihi (istege bagli) - hatirlatma isi bu tarihe gore calisir.</summary>
    public DateOnly? ExpectedReturnOn { get; set; }
    public DateTimeOffset? ReminderBeforeSentAt { get; set; }
    public DateTimeOffset? ReminderOverdueSentAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

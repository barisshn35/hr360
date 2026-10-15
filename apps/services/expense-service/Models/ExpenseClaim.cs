using ExpenseService.Tenancy;

namespace ExpenseService.Models;

public enum ClaimStatus { Draft, Submitted, Approved, Rejected, Paid }

public class ExpenseClaim : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EmployeeId { get; set; }
    public required string Title { get; set; }
    public string Currency { get; set; } = "TRY";
    public decimal TotalAmount { get; set; }
    public ClaimStatus Status { get; set; } = ClaimStatus.Draft;
    /// <summary>Workflow Service'teki onay akisi.</summary>
    public Guid? WorkflowRequestId { get; set; }
    /// <summary>
    /// Beyani onaylayan calisan (workflow karari ya da elle sonuclandirma). Gorev
    /// ayriligi icin: onaylayan kisi ayni beyani "odendi" isaretleyemez.
    /// </summary>
    public Guid? ApprovedByEmployeeId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SubmittedAt { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    /// <summary>Masraf denetimi (olağan dışı tutar / mükerrer fiş) çalıştı mı; null: çalışmadı (ML erişilemedi).</summary>
    public DateTimeOffset? AnomalyCheckedAt { get; set; }
    public List<ExpenseItem> Items { get; set; } = new();
}

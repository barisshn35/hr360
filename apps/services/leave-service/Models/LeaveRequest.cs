using LeaveService.Tenancy;

namespace LeaveService.Models;

public enum LeaveRequestStatus
{
    Draft,
    Submitted,   // Workflow'a gonderildi, onay bekliyor
    Approved,
    Rejected,
    Cancelled
}

public class LeaveRequest : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EmployeeId { get; set; }
    public LeaveType Type { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public decimal Days { get; set; }
    /// <summary>Saatlik izin (tek gün): istenen saat; Days = saat / günlük çalışma saati.</summary>
    public decimal? Hours { get; set; }
    public string? Reason { get; set; }
    public LeaveRequestStatus Status { get; set; } = LeaveRequestStatus.Draft;
    /// <summary>Workflow Service'teki onay akisinin kimligi (mikroservis sinirlari arasi ID referansi).</summary>
    public Guid? WorkflowRequestId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DecidedAt { get; set; }
}

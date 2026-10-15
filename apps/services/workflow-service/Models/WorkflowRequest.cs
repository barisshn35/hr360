using WorkflowService.Tenancy;

namespace WorkflowService.Models;

public enum WorkflowStatus
{
    Pending,
    Approved,
    Rejected,
    Cancelled
}

public enum WorkflowType
{
    LeaveRequest,
    ExpenseClaim,
    PositionChange,
    AssetRequest,
    Other,
    // Sona eklenir: sayısal değerler (0-4) servisler arası çağrılarda kullanılıyor.
    Overtime,
    DocumentRequest,
    Travel,
    // Dalga 5c (Y18): recruitment-service iş teklifi onayı (sayısal değer 8).
    OfferApproval
}

public class WorkflowRequest : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public WorkflowType Type { get; set; }
    public WorkflowStatus Status { get; set; } = WorkflowStatus.Pending;
    public Guid RequesterEmployeeId { get; set; }
    public string? Subject { get; set; }
    public string? Payload { get; set; }
    public DateTimeOffset? SlaDueAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public List<ApprovalStep> Steps { get; set; } = new();
}

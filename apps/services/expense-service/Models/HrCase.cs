using ExpenseService.Tenancy;

namespace ExpenseService.Models;

public enum CaseCategory { Payroll, Benefits, Policy, Complaint, ITSupport, Other }
public enum CasePriority { Low, Normal, High, Urgent }
public enum CaseStatus { Open, InProgress, WaitingOnEmployee, Resolved, Closed }

public class HrCase : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EmployeeId { get; set; }
    public required string Subject { get; set; }
    public string? Description { get; set; }
    public CaseCategory Category { get; set; } = CaseCategory.Other;
    public CasePriority Priority { get; set; } = CasePriority.Normal;
    public CaseStatus Status { get; set; } = CaseStatus.Open;
    public Guid? AssignedToEmployeeId { get; set; }
    public string? Resolution { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ResolvedAt { get; set; }
}

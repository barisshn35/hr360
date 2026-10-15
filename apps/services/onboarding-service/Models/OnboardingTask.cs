using OnboardingService.Tenancy;

namespace OnboardingService.Models;

public enum TaskCategory { IT, HR, Facility, Training, Legal, Other }
public enum OnboardingTaskStatus { Pending, InProgress, Done, Blocked }

public class OnboardingTask : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PlanId { get; set; }
    public OnboardingPlan? Plan { get; set; }
    public required string Title { get; set; }
    public TaskCategory Category { get; set; } = TaskCategory.Other;
    public DateOnly? DueDate { get; set; }
    public Guid? AssigneeEmployeeId { get; set; }
    public OnboardingTaskStatus Status { get; set; } = OnboardingTaskStatus.Pending;
    public int Order { get; set; }
    /// <summary>Gorevin sahibi rol: HR | Manager | IT | Buddy | Employee (sablondan gelir).</summary>
    public string? OwnerRole { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

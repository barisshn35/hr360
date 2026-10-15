using PerformanceService.Tenancy;

namespace PerformanceService.Models;

public enum GoalStatus { Draft, Active, Achieved, Missed, Cancelled }

public class Goal : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CycleId { get; set; }
    public ReviewCycle? Cycle { get; set; }
    public Guid EmployeeId { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    /// <summary>Hedefin donem icindeki agirligi (yuzde).</summary>
    public int Weight { get; set; } = 100;
    public decimal? TargetValue { get; set; }
    public decimal? CurrentValue { get; set; }
    public string? Unit { get; set; }
    public GoalStatus Status { get; set; } = GoalStatus.Draft;
    /// <summary>İsteğe bağlı son tarih; dönem başlangıcı ile bitişi arasında olmalı.</summary>
    public DateOnly? DueDate { get; set; }
    /// <summary>Dalga 11 (OKR): bağlı olduğu şirket/departman amacı (isteğe bağlı).</summary>
    public Guid? ParentObjectiveId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

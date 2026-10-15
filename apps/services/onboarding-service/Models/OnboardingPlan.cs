using OnboardingService.Tenancy;

namespace OnboardingService.Models;

public enum PlanStatus { NotStarted, InProgress, Completed, Cancelled }

public class OnboardingPlan : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EmployeeId { get; set; }
    public DateOnly StartDate { get; set; }
    public string? TemplateName { get; set; }
    public PlanStatus Status { get; set; } = PlanStatus.NotStarted;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    /// <summary>Yol arkadasi (buddy) - yeni calisana ilk haftalarda eslik eden calisan.</summary>
    public Guid? BuddyEmployeeId { get; set; }
    /// <summary>Ilk gun bulusma yeri (orn. "Istanbul ofis, 3. kat resepsiyon"). Kisisel veri yazilmaz.</summary>
    public string? Location { get; set; }
    /// <summary>Plana uygulanan gorev sablonlarinin adlari (virgulle).</summary>
    public string? AppliedTemplates { get; set; }
    /// <summary>Ilk gun karsilama iletisi gonderildiyse zamani (tekrar gonderimi engeller).</summary>
    public DateTimeOffset? WelcomeSentAt { get; set; }
    public List<OnboardingTask> Tasks { get; set; } = new();
}

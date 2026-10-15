using LearningService.Tenancy;

namespace LearningService.Models;

public enum EnrollmentStatus { Enrolled, InProgress, Completed, Failed, Dropped }

public class Enrollment : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CourseId { get; set; }
    public Course? Course { get; set; }
    public Guid EmployeeId { get; set; }
    public EnrollmentStatus Status { get; set; } = EnrollmentStatus.Enrolled;
    public decimal? Score { get; set; }
    public DateTimeOffset EnrolledAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    /// <summary>Dalga 11 (madde 84): atanan eğitimin son tarihi (zorunlu eğitimlerde hatırlatma bu tarihe göre).</summary>
    public DateOnly? DueOn { get; set; }
}

using LearningService.Tenancy;

namespace LearningService.Models;

public enum CourseCategory { Technical, Compliance, Leadership, Soft, Safety, Other }

public class Course : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Title { get; set; }
    public string? Description { get; set; }
    public string? Provider { get; set; }
    public decimal DurationHours { get; set; }
    public CourseCategory Category { get; set; } = CourseCategory.Other;
    /// <summary>Zorunlu egitimler uyum (compliance) raporlarinda takip edilir.</summary>
    public bool IsMandatory { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>Tamamlanınca verilen sertifikanın geçerlilik süresi (ay). Boşsa süresiz.</summary>
    public int? CertificateValidityMonths { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<Enrollment> Enrollments { get; set; } = new();
}

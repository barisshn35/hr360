using LearningService.Tenancy;

namespace LearningService.Models;

/// <summary>Y19: yetkinlik tanımı (ör. "C# ile geliştirme", "Sunum becerisi"). Seviye ölçeği 1-5.</summary>
public class Competency : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public string? Description { get; set; }
    public string? Category { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Rol profili: bir pozisyon unvanı YA DA departman için yetkinlik başına beklenen seviye.
/// Pozisyona özel tanım, aynı yetkinlik için departman tanımını geçersiz kılar (daha özgül).
/// </summary>
public class RoleProfileEntry : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompetencyId { get; set; }
    public string? PositionTitle { get; set; }
    public Guid? DepartmentId { get; set; }
    public int RequiredLevel { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class AssessmentSource
{
    public const string Self = "Self";
    public const string Manager = "Manager";
    public const string Hr = "Hr";
}

/// <summary>Yetkinlik değerlendirmesi (öz/yönetici/İK). Geçmiş tutulur; güncel seviye en son kayıttır.</summary>
public class CompetencyAssessment : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EmployeeId { get; set; }
    public Guid CompetencyId { get; set; }
    public int Level { get; set; }
    public string Source { get; set; } = AssessmentSource.Self;
    public Guid? AssessedByEmployeeId { get; set; }
    public string? AssessedByName { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset AssessedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Eğitimin geliştirdiği yetkinlik ve eğitim sonunda hedeflenen seviye.</summary>
public class CourseCompetency : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CourseId { get; set; }
    public Guid CompetencyId { get; set; }
    public int TargetLevel { get; set; }
}

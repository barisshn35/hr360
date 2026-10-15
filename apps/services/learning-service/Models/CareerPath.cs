using LearningService.Tenancy;

namespace LearningService.Models;

/// <summary>Dalga 11 (madde 83): kariyer yolu — sıralı rol basamakları (pozisyon unvanları).</summary>
public class CareerPath : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<CareerStep> Steps { get; set; } = new();
}

/// <summary>Kariyer yolundaki bir basamak; çalışanın güncel pozisyon unvanı basamağın unvanıyla eşleşir.</summary>
public class CareerStep : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PathId { get; set; }
    public int StepOrder { get; set; }
    public required string PositionTitle { get; set; }
    public string? Description { get; set; }
    /// <summary>Basamakta önerilen asgari süre (ay) — bilgi amaçlı; otomatik terfi kararı verilmez.</summary>
    public int? MinMonths { get; set; }
    public List<CareerStepRequirement> Requirements { get; set; } = new();
}

/// <summary>Basamak için beklenen yetkinlik seviyesi (1-5).</summary>
public class CareerStepRequirement : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StepId { get; set; }
    public Guid CompetencyId { get; set; }
    public int RequiredLevel { get; set; }
}

/// <summary>Gönderilmiş son tarih hatırlatması (eğitim ataması ya da İSG eğitimi); tekrar gönderimi engeller.</summary>
public class DueReminder : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string SourceType { get; set; }
    public Guid SourceId { get; set; }
    public Guid SubjectEmployeeId { get; set; }
    public required string Kind { get; set; }
    public Guid RecipientEmployeeId { get; set; }
    public DateOnly DueOn { get; set; }
    public DateTimeOffset SentAt { get; set; } = DateTimeOffset.UtcNow;
}

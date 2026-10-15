using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using System.Text.Json.Serialization;
using PerformanceService.Tenancy;

namespace PerformanceService.Models;

// Dalga 11 — tablolar scripts/sql/2026-10-24_performance_w11.sql'den.

public enum CalibrationStatus { Open, Finalized, Cancelled }

/// <summary>79: İK'nın bir dönem için yürüttüğü kalibrasyon oturumu.</summary>
public class CalibrationSession : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CycleId { get; set; }
    public required string Name { get; set; }
    public CalibrationStatus Status { get; set; } = CalibrationStatus.Open;
    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? FinalizedBy { get; set; }
    public DateTimeOffset? FinalizedAt { get; set; }
}

/// <summary>Oturumdaki bir çalışan: başlangıç hücresi (oturum açılırken) ve tartışılan hücre.</summary>
public class CalibrationItem : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public Guid EmployeeId { get; set; }
    public int OriginalPerformanceBand { get; set; }
    public int OriginalPotentialBand { get; set; }
    public int PerformanceBand { get; set; }
    public int PotentialBand { get; set; }
    public decimal? Score { get; set; }
    public string? DecisionNote { get; set; }
    public bool Confirmed { get; set; }
    public string? ConfirmedBy { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Kalibrasyon değişiklik günlüğü (yalnızca ekleme).</summary>
public class CalibrationChange : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public Guid EmployeeId { get; set; }
    public required string Action { get; set; }
    public int? FromCell { get; set; }
    public int? ToCell { get; set; }
    public string? Note { get; set; }
    public required string ChangedBy { get; set; }
    public DateTimeOffset ChangedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum ObjectiveLevel { Company, Department }

/// <summary>80: şirket ya da departman amacı; kişisel hedefler (Goal.ParentObjectiveId) bunlara bağlanır.</summary>
public class Objective : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CycleId { get; set; }
    public ObjectiveLevel Level { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? ParentId { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public int Weight { get; set; } = 100;
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public record F360Competency(string Key, string Label);

/// <summary>81: anonim 360 talebi.</summary>
public class F360Request : ITenantOwned
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? CycleId { get; set; }
    public Guid SubjectEmployeeId { get; set; }
    public required string Title { get; set; }
    [JsonIgnore] public string CompetenciesJson { get; set; } = "[]";
    public string Status { get; set; } = "Open";
    public DateOnly? DueDate { get; set; }
    public int MinResponses { get; set; } = 5;
    public bool ReleasedToSubject { get; set; }
    public Guid? CreatedByEmployeeId { get; set; }
    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ClosedAt { get; set; }

    [NotMapped]
    public List<F360Competency> Competencies
    {
        get => JsonSerializer.Deserialize<List<F360Competency>>(CompetenciesJson, Json) ?? new();
        set => CompetenciesJson = JsonSerializer.Serialize(value, Json);
    }
}

/// <summary>Katılım: kim davet edildi ve yanıtladı mı. Yanıtla ilişkilendirilmez; zaman damgası yok.</summary>
public class F360Participant : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RequestId { get; set; }
    public Guid ReviewerEmployeeId { get; set; }
    public string Relationship { get; set; } = "Peer";
    public bool Submitted { get; set; }
}

/// <summary>
/// Anonim yanıt. Değerlendiren kimliği, ilişki türü ve zaman yoktur. EF ile YAZILMAZ (denetim
/// kaydı yanıtı kullanıcıyla eşleştirirdi): F360Controller ham SQL ile ekler; burada yalnızca okunur.
/// </summary>
public class F360Response : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; }
    public Guid RequestId { get; set; }
    public string RatingsJson { get; set; } = "{}";
    public string? Comment { get; set; }
}

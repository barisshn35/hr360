using WorkflowService.Tenancy;

namespace WorkflowService.Models;

public enum StepDecision
{
    Pending,
    Approved,
    Rejected,
    Delegated
}

public class ApprovalStep : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkflowRequestId { get; set; }
    public WorkflowRequest? WorkflowRequest { get; set; }
    public int Order { get; set; }
    public Guid ApproverEmployeeId { get; set; }
    public Guid? DelegatedToEmployeeId { get; set; }
    public StepDecision Decision { get; set; } = StepDecision.Pending;
    public string? Comment { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Adımın karar süresi (saat); akış tanımından gelir. Sıra bu adıma gelince SLA buna göre kurulur.</summary>
    public int? SlaHours { get; set; }
    /// <summary>Adım bir vekâlet kaydıyla vekile geçtiyse o kayıt (vekâlet bitince adım asıl onaycıya döner).</summary>
    public Guid? DelegationId { get; set; }
    /// <summary>Süre aşımında üst yöneticiye iletildiği an.</summary>
    public DateTimeOffset? EscalatedAt { get; set; }
    /// <summary>E-postadan tek tıkla karar bağlantısının SHA-256 özeti (tek kullanımlık).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string? ActionTokenHash { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public DateTimeOffset? ActionTokenExpiresAt { get; set; }
}

public enum ApproverKind { DepartmentHead, ParentDepartmentHead, Employee }
public enum ConditionField { Days, Amount, Hours }

/// <summary>Görsel akış tasarımcısında bir adım: kim onaylar, hangi koşulda, kaç saatte.</summary>
public class DefinitionStep
{
    public ApproverKind Approver { get; set; }
    public Guid? EmployeeId { get; set; }
    public ConditionField? ConditionField { get; set; }
    /// <summary>"&gt;", "&gt;=", "&lt;", "&lt;="</summary>
    public string? ConditionOp { get; set; }
    public decimal? ConditionValue { get; set; }
    public int? SlaHours { get; set; }
}

/// <summary>Talep türü başına onay zinciri tanımı (kiracı başına bir etkin tanım).</summary>
public class WorkflowDefinition : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public WorkflowType Type { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
    /// <summary>DefinitionStep listesi (JSON).</summary>
    public string StepsJson { get; set; } = "[]";
    /// <summary>Onaycılara gösterilmeyecek talep alanları (ör. "reason"), JSON dizi.</summary>
    public string HiddenFieldsJson { get; set; } = "[]";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Vekâlet: bir onaycı belirli tarihler arasında onaylarını başkasına bırakır. KVKK: vekil
/// yalnızca kendisine düşen onay kaydını görür; vekâlet bitince erişimi kapanır.
/// </summary>
public class Delegation : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FromEmployeeId { get; set; }
    public Guid ToEmployeeId { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public string? Reason { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RevokedAt { get; set; }
}

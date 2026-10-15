using OrganizationService.Tenancy;

namespace OrganizationService.Models;

/// <summary>
/// Departman altindaki calisma ekibi.
///
/// Sirket -> Departman -> Ekip -> Uyeler hiyerarsisi.
///
/// LeadEmployeeId opsiyoneldir: her ekibin takim lideri olmak zorunda degil.
/// Takim liderligi bilincli olarak REALM ROLU DEGIL, veri alanidir. Rol
/// yapilsaydi her lider butun ekipleri gorurdu; burada "bu ekibi gorebilir
/// miyim" sorusu bir iliski sorusu - kisi bu ekibin lideri mi, yoksa
/// ustundeki bir yonetici mi.
/// </summary>
public class Team : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string Name { get; set; }
    public string? Description { get; set; }

    public Guid DepartmentId { get; set; }
    public Department? Department { get; set; }

    /// <summary>Takim lideri. Opsiyonel - liderliksiz ekip olabilir.</summary>
    public Guid? LeadEmployeeId { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<TeamMember> Members { get; set; } = new();
}

/// <summary>
/// Ekip uyeligi. Ayrilma tarihli tutulur - gecmis donem raporlarinda
/// "o donemde bu ekipteydi" bilgisi korunsun diye uyelik silinmez.
/// </summary>
public class TeamMember : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TeamId { get; set; }
    public Team? Team { get; set; }

    /// <summary>Employee Service'teki calisana ID referansi.</summary>
    public Guid EmployeeId { get; set; }

    /// <summary>Ekip icindeki rolu ("Backend", "QA" gibi). Yetki DEGIL, etiket.</summary>
    public string? RoleInTeam { get; set; }

    public DateOnly JoinedOn { get; set; }
    public DateOnly? LeftOn { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool IsCurrent => LeftOn is null;
}

using OnboardingService.Tenancy;

namespace OnboardingService.Models;

/// <summary>Gorev sahibi roller (sablon maddeleri ve plan gorevleri).</summary>
public static class OwnerRoles
{
    public const string Hr = "HR", Manager = "Manager", It = "IT", Buddy = "Buddy", Employee = "Employee";
    public static readonly string[] All = { Hr, Manager, It, Buddy, Employee };
}

/// <summary>
/// Unvan ve/veya departmana gore ise alisma gorev sablonu. Plan olusturulurken yeni
/// calisanin etkin atamasina (unvan, departman) uyan TUM etkin sablonlar uygulanir;
/// unvan ve departmani bos sablon herkese uygulanir.
/// </summary>
public class TaskTemplate : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public string? PositionTitle { get; set; }
    public Guid? DepartmentId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<TaskTemplateItem> Items { get; set; } = new();
}

public class TaskTemplateItem : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TemplateId { get; set; }
    public TaskTemplate? Template { get; set; }
    public required string Title { get; set; }
    public TaskCategory Category { get; set; } = TaskCategory.Other;
    public string OwnerRole { get; set; } = OwnerRoles.Hr;
    /// <summary>Baslangic tarihine gore gun farki (negatif: baslamadan once).</summary>
    public int OffsetDays { get; set; }
    public int Order { get; set; }
}

/// <summary>Kiraci ayarlari: ilk gun karsilama sablonu, zimmet hatirlatma sorumlusu.</summary>
public class OnboardingSettings : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? WelcomeSubject { get; set; }
    public string? WelcomeBody { get; set; }
    public Guid? HrContactEmployeeId { get; set; }
    public int ReminderDaysBefore { get; set; } = 3;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum MaintenanceType { Periodic, Repair, Inspection, Other }

/// <summary>Demirbas bakim kaydi (tarih, tur, maliyet, servis, sonraki bakim).</summary>
public class AssetMaintenance : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AssetId { get; set; }
    public DateOnly Date { get; set; }
    public MaintenanceType Type { get; set; } = MaintenanceType.Periodic;
    public decimal? Cost { get; set; }
    public string? Vendor { get; set; }
    public string? Notes { get; set; }
    public DateOnly? NextMaintenanceOn { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

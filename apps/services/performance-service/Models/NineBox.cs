using PerformanceService.Tenancy;

namespace PerformanceService.Models;

/// <summary>
/// G12: yöneticinin döneme özel potansiyel değerlendirmesi (1 düşük – 3 yüksek). Yalnızca İK ve
/// çalışanın yöneticisi görür; İK yayımlamadıkça çalışana gösterilmez.
/// </summary>
public class PotentialRating : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CycleId { get; set; }
    public Guid EmployeeId { get; set; }
    public int Rating { get; set; }
    public string? Note { get; set; }
    public Guid? RatedByEmployeeId { get; set; }
    public string? RatedByName { get; set; }
    public bool PublishedToEmployee { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>G12 kalibrasyon: İK'nın gerekçeli hücre düzeltmesi. Geçmiş korunur; en son kayıt geçerli.</summary>
public class NineBoxOverride : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CycleId { get; set; }
    public Guid EmployeeId { get; set; }
    public int PerformanceBand { get; set; }
    public int PotentialBand { get; set; }
    public required string Reason { get; set; }
    public required string OverriddenBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>G12 dönem şablonu: ad, bölümler/sorular, ağırlıklar, ölçek. Yeni dönem şablondan açılır.</summary>
public class CycleTemplate : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public string? Description { get; set; }
    public CyclePeriod Period { get; set; }
    public int DurationDays { get; set; }
    public string ConfigJson { get; set; } = "{}";
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

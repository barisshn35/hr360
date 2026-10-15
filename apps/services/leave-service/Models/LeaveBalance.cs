using LeaveService.Tenancy;

namespace LeaveService.Models;

/// <summary>Bir calisanin belirli bir yil ve izin turu icin hak/kullanim durumu.</summary>
public class LeaveBalance : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EmployeeId { get; set; }
    public int Year { get; set; }
    public LeaveType Type { get; set; }
    public decimal EntitledDays { get; set; }
    public decimal UsedDays { get; set; }
    public decimal PendingDays { get; set; }
    /// <summary>Önceki yıldan devreden gün (EntitledDays'e dahildir).</summary>
    public decimal CarriedOverDays { get; set; }
    /// <summary>Sonraki yıla devredilen gün (bu yılın kullanılmayan kısmı).</summary>
    public decimal CarriedOutDays { get; set; }
    public decimal RemainingDays => EntitledDays - UsedDays - PendingDays;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

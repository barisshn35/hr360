using OrganizationService.Tenancy;

namespace OrganizationService.Models;

/// <summary>
/// Norm kadro: departmanda onaylı pozisyon. Dolu kadro çalışan atamalarındaki unvandan hesaplanır
/// (büyük/küçük harf duyarsız); bütçe ücret bilgisidir, yalnızca İK görür.
/// </summary>
public class Position : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DepartmentId { get; set; }
    public string Title { get; set; } = "";
    public int Headcount { get; set; }
    public string? Grade { get; set; }
    public decimal? BudgetMonthly { get; set; }
    public string Currency { get; set; } = "TRY";
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

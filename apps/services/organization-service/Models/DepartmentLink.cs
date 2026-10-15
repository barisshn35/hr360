using OrganizationService.Tenancy;

namespace OrganizationService.Models;

/// <summary>
/// Matris organizasyon: iki departman arasındaki noktalı çizgi (ikincil) raporlama ilişkisi.
///
/// Ana hiyerarşi <see cref="Department.ParentDepartmentId"/> zinciridir; bu kayıt ona ek olarak
/// "fonksiyonel" (ör. bölge satış ekibi → merkez pazarlama) ya da "proje" bağını tutar.
/// Yön anlamlıdır: <c>From</c> departmanı <c>To</c> departmanına noktalı çizgiyle raporlar.
/// Departman silinince bağları da gider (SQL'de ON DELETE CASCADE).
/// </summary>
public class DepartmentLink : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FromDepartmentId { get; set; }
    public Guid ToDepartmentId { get; set; }

    /// <summary><see cref="DepartmentLinkKinds"/> değerlerinden biri.</summary>
    public string Kind { get; set; } = DepartmentLinkKinds.Functional;

    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class DepartmentLinkKinds
{
    public const string Functional = "Functional";
    public const string Project = "Project";
    public static readonly IReadOnlyList<string> All = new[] { Functional, Project };
}

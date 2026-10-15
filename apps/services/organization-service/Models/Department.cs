using OrganizationService.Tenancy;

namespace OrganizationService.Models;

public class Department : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public Guid CompanyId { get; set; }
    public Company? Company { get; set; }
    public Guid? ParentDepartmentId { get; set; }

    /// <summary>Departman basi - employee-service'teki bir Employee.Id.
    /// Baska bir servise ait oldugu icin FK kisiti YOK, sadece Guid olarak
    /// tutulur (mikroservis mimarisinde yaygin pratik - dogrulama, tipki
    /// diger tum "EmployeeId" alanlarinda oldugu gibi, cagiran taraf ya da
    /// event tuketimi ile saglanir, senkron cross-service call ile degil).</summary>
    public Guid? HeadEmployeeId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

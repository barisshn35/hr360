using OrganizationService.Tenancy;

namespace OrganizationService.Models;

public class Company : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public string? TaxNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<Department> Departments { get; set; } = new();
}

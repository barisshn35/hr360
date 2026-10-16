using EmployeeService.Tenancy;

namespace EmployeeService.Models;

public enum EmployeeStatus
{
    Active,
    OnLeave,
    Terminated
}

public class Employee : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string FirstName { get; set; }
    public required string LastName { get; set; }
    public required string Email { get; set; }
    public string? Phone { get; set; }

    /// <summary>
    /// Keycloak'taki kullanici kimligi (sub claim). Bu calisanin
    /// SISTEME GIRIS ERISIMI olup olmadigini gosterir - null ise
    /// henuz davet edilmemis/hesap acilmamis demektir. Rol atama/kaldirma
    /// islemleri bu ID uzerinden yapilir (Keycloak Admin API email
    /// degil, user ID bekler).
    /// </summary>
    public string? KeycloakUserId { get; set; }

    public DateOnly HireDate { get; set; }
    public EmployeeStatus Status { get; set; } = EmployeeStatus.Active;

    /// <summary>
    /// Fotoğraf sürümü (yoksa null). Fotoğrafın kendisi ayrı tabloda (employee_photos) tutulur; bu sayı
    /// her yüklemede artar ve istemcide önbellek anahtarı olur (/employees/{id}/photo?v=N).
    /// </summary>
    public int? PhotoVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<Assignment> Assignments { get; set; } = new();
}

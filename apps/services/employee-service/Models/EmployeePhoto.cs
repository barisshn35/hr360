using EmployeeService.Tenancy;

namespace EmployeeService.Models;

/// <summary>
/// Çalışan fotoğrafı. Kişisel veri: herkese açık depoya konmaz, kiracı yalıtımlı tabloda durur ve
/// yalnızca oturum açmış aynı şirket kullanıcılarına servis ucundan verilir. Bayt içeriği denetim
/// kaydına girmesin diye bu tablo EF ile değil SQL ile yazılır; değişiklik denetimde
/// <see cref="Employee.PhotoVersion"/> üzerinden görünür.
/// </summary>
public class EmployeePhoto : ITenantOwned
{
    public Guid EmployeeId { get; set; }
    public string TenantSlug { get; set; } = "";
    public string ContentType { get; set; } = "";
    public byte[] Data { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; }
}

using TimeShiftService.Tenancy;

namespace TimeShiftService.Models;

public class ShiftAssignment : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EmployeeId { get; set; }
    public Guid ShiftId { get; set; }
    public Shift? Shift { get; set; }
    public DateOnly Date { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

using TimeShiftService.Tenancy;

namespace TimeShiftService.Models;

public enum TimeEntrySource { Manual, Device, Import, Qr, Card, Pin, Web, Chat }

public class TimeEntry : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EmployeeId { get; set; }
    public DateOnly Date { get; set; }
    public DateTimeOffset? ClockIn { get; set; }
    public DateTimeOffset? ClockOut { get; set; }
    public int WorkedMinutes { get; set; }
    public int OvertimeMinutes { get; set; }
    public TimeEntrySource Source { get; set; } = TimeEntrySource.Manual;
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

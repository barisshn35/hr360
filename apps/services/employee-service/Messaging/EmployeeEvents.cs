namespace EmployeeService.Messaging;

/// <summary>Bu servisin yayinladigi konu ve event tipleri.</summary>
public static class EmployeeTopics
{
    public const string Events = "hr360.employee.events";
}

public static class EmployeeEventTypes
{
    public const string Hired = "employee.hired";
    public const string Assigned = "employee.assigned";
    public const string StatusChanged = "employee.status-changed";
}

/// <summary>employee.hired olayinin govdesi.</summary>
public record EmployeeHiredEvent(
    string TenantSlug,
    Guid EmployeeId,
    string FirstName,
    string LastName,
    string Email,
    DateOnly HireDate,
    DateTimeOffset OccurredAt);

/// <summary>employee.assigned olayinin govdesi.</summary>
public record EmployeeAssignedEvent(
    string TenantSlug,
    Guid EmployeeId,
    Guid AssignmentId,
    Guid DepartmentId,
    string? PositionTitle,
    DateOnly EffectiveFrom,
    DateTimeOffset OccurredAt,
    string Email,
    string FirstName,
    string LastName);

/// <summary>Calisan durumu degisti (ise devam / izinde / ayrildi).</summary>
public record EmployeeStatusChangedEvent(
    string TenantSlug, Guid EmployeeId, string FirstName, string LastName,
    string OldStatus, string NewStatus, DateOnly? EffectiveDate, DateTimeOffset OccurredAt);

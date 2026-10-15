using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TimeShiftService.Data;
using TimeShiftService.Models;

namespace TimeShiftService.Messaging;

/// <summary>
/// workflow-service karar olaylarını dinler; fazla mesai talebini onaylı/reddedildi yapar.
/// Akışın talep sahibi kaydın sahibiyle eşleşmezse karar uygulanmaz.
/// </summary>
public class WorkflowEventConsumer : KafkaConsumerBase
{
    public WorkflowEventConsumer(IServiceProvider services, ILogger<WorkflowEventConsumer> logger)
        : base(services, logger, "timeshift-service-workflow", "hr360.workflow.events") { }

    protected override string ConsumerName => "timeshift-service-workflow";

    private record Decided(Guid WorkflowRequestId, string WorkflowType, Guid RequesterEmployeeId, bool Approved);

    protected override async Task HandleAsync(string eventType, string payload, TimeShiftDbContext db, CancellationToken ct)
    {
        if (eventType is not ("workflow.approved" or "workflow.rejected")) return;
        var evt = JsonSerializer.Deserialize<Decided>(payload, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (evt is null || !string.Equals(evt.WorkflowType, "Overtime", StringComparison.OrdinalIgnoreCase)) return;
        var o = await db.OvertimeRequests.FirstOrDefaultAsync(x => x.WorkflowRequestId == evt.WorkflowRequestId, ct);
        if (o is null || o.Status != OvertimeStatus.Pending) return;
        if (o.EmployeeId != evt.RequesterEmployeeId)
        {
            Logger.LogWarning("Fazla mesai {Id}: akış talep sahibi eşleşmiyor, karar uygulanmadı", o.Id);
            return;
        }
        o.Status = evt.Approved ? OvertimeStatus.Approved : OvertimeStatus.Rejected;
        o.DecidedAt = DateTimeOffset.UtcNow;
    }
}

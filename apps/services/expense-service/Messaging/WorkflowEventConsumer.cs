using System.Text.Json;
using ExpenseService.Data;
using ExpenseService.Models;

namespace ExpenseService.Messaging;

/// <summary>
/// Workflow Service'in karar event'lerini dinler ve ilgili masraf beyanini
/// OTOMATIK sonuclandirir. Bu olmadan birinin /resolve ucunu elle cagirmasi
/// gerekiyordu; event ile is akisi kendi kendine kapaniyor.
///
/// leave-service'teki ayni desenin ExpenseClaim karsiligidir.
/// </summary>
public class WorkflowEventConsumer : KafkaConsumerBase
{
    public WorkflowEventConsumer(IServiceProvider services, ILogger<WorkflowEventConsumer> logger)
        : base(services, logger, "expense-service", "hr360.workflow.events") { }

    protected override string ConsumerName => "expense-service";

    protected override async Task HandleAsync(
        string eventType, string payload, ExpenseDbContext db, CancellationToken ct)
    {
        if (eventType is not ("workflow.approved" or "workflow.rejected")) return;

        var evt = JsonSerializer.Deserialize<WorkflowDecidedPayload>(payload,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (evt is null) return;

        // Y12: seyahat talebi kararı.
        if (string.Equals(evt.WorkflowType, "Travel", StringComparison.OrdinalIgnoreCase))
        {
            var travel = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                .FirstOrDefaultAsync(db.Travels.Where(t => t.WorkflowRequestId == evt.WorkflowRequestId), ct);
            if (travel is null || travel.Status != TravelStatus.Submitted || travel.EmployeeId != evt.RequesterEmployeeId) return;
            travel.Status = evt.Approved ? TravelStatus.Approved : TravelStatus.Rejected;
            travel.DecidedByEmployeeId = evt.DecidedByEmployeeId;
            // Reddedilen seyahatin pasaport bilgisi hemen silinir.
            if (!evt.Approved && travel.PassportCipher is not null) { travel.PassportCipher = null; travel.PassportPurgedAt = DateTimeOffset.UtcNow; }
            return;
        }

        // Yalnizca masraf beyanlariyla ilgileniyoruz.
        if (!string.Equals(evt.WorkflowType, "ExpenseClaim", StringComparison.OrdinalIgnoreCase))
            return;

        var claim = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .FirstOrDefaultAsync(Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                // G25: kalem kategorileri is metrigi etiketi icin (hr360_expense_claims_total).
                .Include(db.Claims, c => c.Items)
                .Where(c => c.WorkflowRequestId == evt.WorkflowRequestId), ct);

        if (claim is null)
        {
            Logger.LogWarning(
                "Workflow {Id} için eşleşen masraf beyanı bulunamadı", evt.WorkflowRequestId);
            return;
        }

        if (claim.Status != ClaimStatus.Submitted)
        {
            Logger.LogInformation(
                "Masraf beyanı {Id} zaten {Status} durumunda, atlanıyor", claim.Id, claim.Status);
            return;
        }

        // GUVENLIK: Akis ile beyan yalnizca WorkflowRequestId ile eslestiriliyordu.
        // Akisin talep sahibi beyanin sahibi degilse (baska birinin akisi bu beyana
        // baglanmissa) karar UYGULANMAZ.
        if (evt.RequesterEmployeeId != claim.EmployeeId)
        {
            Logger.LogWarning(
                "Workflow {Wf} talep sahibi ({Req}) masraf beyanı {Claim} sahibiyle ({Owner}) eşleşmiyor - karar uygulanmadı",
                evt.WorkflowRequestId, evt.RequesterEmployeeId, claim.Id, claim.EmployeeId);
            return;
        }

        claim.Status = evt.Approved ? ClaimStatus.Approved : ClaimStatus.Rejected;
        if (evt.Approved) claim.ApprovedByEmployeeId = evt.DecidedByEmployeeId;

        Logger.LogInformation(
            "Masraf beyanı {Id} workflow kararıyla {Status} yapıldı", claim.Id, claim.Status);
    }

    private record WorkflowDecidedPayload(
        Guid WorkflowRequestId, string WorkflowType, Guid RequesterEmployeeId,
        string? Subject, bool Approved, Guid DecidedByEmployeeId,
        string? Comment, DateTimeOffset OccurredAt);
}

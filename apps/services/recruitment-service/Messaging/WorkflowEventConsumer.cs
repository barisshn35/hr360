using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RecruitmentService.Data;
using RecruitmentService.Models;
using RecruitmentService.Services;

namespace RecruitmentService.Messaging;

/// <summary>
/// Y18: workflow-service karar olaylarını dinler; "OfferApproval" türündeki akış sonuçlanınca
/// ilgili teklifi Approved/Rejected yapar (expense-service WorkflowEventConsumer deseni).
/// </summary>
public class WorkflowEventConsumer : KafkaConsumerBase
{
    public WorkflowEventConsumer(IServiceProvider services, ILogger<WorkflowEventConsumer> logger)
        : base(services, logger, "recruitment-service", "hr360.workflow.events") { }

    protected override string ConsumerName => "recruitment-service";

    protected override async Task HandleAsync(string eventType, string payload, RecruitmentDbContext db, CancellationToken ct)
    {
        if (eventType is not ("workflow.approved" or "workflow.rejected")) return;
        var evt = JsonSerializer.Deserialize<WorkflowDecidedPayload>(payload, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (evt is null || !string.Equals(evt.WorkflowType, "OfferApproval", StringComparison.OrdinalIgnoreCase)) return;

        var offer = await db.Offers.FirstOrDefaultAsync(o => o.WorkflowRequestId == evt.WorkflowRequestId, ct);
        if (offer is null)
        {
            Logger.LogWarning("Workflow {Id} için eşleşen teklif bulunamadı", evt.WorkflowRequestId);
            return;
        }
        // Akış başka bir kiracıya ait olamaz (olay kiracıyı taşır).
        if (!string.IsNullOrEmpty(evt.TenantSlug) && evt.TenantSlug != offer.TenantSlug) return;
        if (offer.Status != OfferStatus.PendingApproval)
        {
            Logger.LogInformation("Teklif {Id} zaten {Status}, karar atlanıyor", offer.Id, offer.Status);
            return;
        }
        offer.Status = evt.Approved ? OfferStatus.Approved : OfferStatus.Rejected;
        offer.DecidedByEmployeeId = evt.DecidedByEmployeeId;
        offer.DecisionNote = evt.Comment;
        offer.DecidedAt = evt.OccurredAt == default ? DateTimeOffset.UtcNow : evt.OccurredAt;

        // Teklifi açan İK kullanıcısının çalışan kaydı varsa ona uygulama içi bildirim (yoksa ekranda görür).
        // KVKK: bildirim metninde ücret ve aday adı yer almaz.
        var creator = await db.MyEmployeeIdAsync(offer.TenantSlug, offer.CreatedByUserId, ct);
        if (creator is { } c)
            await db.NotifyAsync(offer.TenantSlug, c, evt.Approved ? "Teklif onaylandı" : "Teklif reddedildi",
                $"{offer.PositionTitle} pozisyonu için hazırlanan iş teklifi {(evt.Approved ? "onaylandı; adaya gönderebilirsiniz" : "onaylanmadı")}.",
                "recruitment.offer.decided", ct);
        Logger.LogInformation("Teklif {Id} workflow kararıyla {Status}", offer.Id, offer.Status);
    }

    private record WorkflowDecidedPayload(
        string? TenantSlug, Guid WorkflowRequestId, string WorkflowType, Guid RequesterEmployeeId,
        string? Subject, bool Approved, Guid DecidedByEmployeeId, string? Comment, DateTimeOffset OccurredAt);
}

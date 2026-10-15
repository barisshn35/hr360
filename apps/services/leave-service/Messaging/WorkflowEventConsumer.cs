using System.Text.Json;
using LeaveService.Data;
using LeaveService.Models;

namespace LeaveService.Messaging;

/// <summary>
/// Workflow Service'in karar event'lerini dinler ve ilgili izin talebini
/// OTOMATIK sonuclandirir. Bu olmadan birinin /resolve ucunu elle cagirmasi
/// gerekiyordu; event ile is akisi kendi kendine kapaniyor.
///
/// Ayrica kendi "leave.approved"/"leave.rejected" event'ini OUTBOX'a ekler -
/// timeshift-service gibi digger servisler (izin gunlerini otomatik
/// "izinli" isaretlemek icin) bunu dinleyebilir.
/// </summary>
public class WorkflowEventConsumer : KafkaConsumerBase
{
    private const string LeaveEventsTopic = "hr360.leave.events";

    public WorkflowEventConsumer(IServiceProvider services, ILogger<WorkflowEventConsumer> logger)
        : base(services, logger, "leave-service", "hr360.workflow.events") { }

    protected override string ConsumerName => "leave-service";

    protected override async Task HandleAsync(
        string eventType, string payload, LeaveDbContext db, CancellationToken ct)
    {
        if (eventType is not ("workflow.approved" or "workflow.rejected")) return;

        var evt = JsonSerializer.Deserialize<WorkflowDecidedPayload>(payload,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (evt is null) return;

        // Yalnizca izin talepleriyle ilgileniyoruz.
        if (!string.Equals(evt.WorkflowType, "LeaveRequest", StringComparison.OrdinalIgnoreCase))
            return;

        var leave = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .FirstOrDefaultAsync(db.LeaveRequests
                .Where(r => r.WorkflowRequestId == evt.WorkflowRequestId), ct);

        if (leave is null)
        {
            Logger.LogWarning(
                "Workflow {Id} için eşleşen izin talebi bulunamadı", evt.WorkflowRequestId);
            return;
        }

        // GUVENLIK: Akis ile talep yalnizca WorkflowRequestId ile eslesiyordu; akisin
        // talep sahibi iznin sahibi degilse karar UYGULANMAZ (bkz. expense-service'teki
        // ayni duzeltme - baskasinin akisini kendi kaydina baglayip onaylatma).
        if (evt.RequesterEmployeeId != leave.EmployeeId)
        {
            Logger.LogWarning(
                "Workflow {Wf} talep sahibi ({Req}) izin talebi {Leave} sahibiyle ({Owner}) eşleşmiyor - karar uygulanmadı",
                evt.WorkflowRequestId, evt.RequesterEmployeeId, leave.Id, leave.EmployeeId);
            return;
        }

        if (leave.Status != LeaveRequestStatus.Submitted)
        {
            Logger.LogInformation(
                "İzin talebi {Id} zaten {Status} durumunda, atlanıyor", leave.Id, leave.Status);
            return;
        }

        // Bakiyeyi kesinlestir: pending'den dus, onaylandiysa used'a ekle.
        var balance = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .FirstOrDefaultAsync(db.LeaveBalances
                .Where(b => b.EmployeeId == leave.EmployeeId
                         && b.Year == leave.StartDate.Year
                         && b.Type == leave.Type), ct);

        if (balance is not null)
        {
            balance.PendingDays -= leave.Days;
            if (evt.Approved) balance.UsedDays += leave.Days;
            balance.UpdatedAt = DateTimeOffset.UtcNow;
        }

        leave.Status = evt.Approved ? LeaveRequestStatus.Approved : LeaveRequestStatus.Rejected;
        leave.DecidedAt = DateTimeOffset.UtcNow;

        db.OutboxMessages.Add(new OutboxMessage
        {
            Topic = LeaveEventsTopic,
            EventType = evt.Approved ? "leave.approved" : "leave.rejected",
            PartitionKey = leave.EmployeeId.ToString(),
            // NOT: db.CurrentTenantSlug burada HER ZAMAN bos/null doner - bu bir
            // arka plan tuketicisi, HTTP baglami (dolayisiyla TenantContext) yok
            // (bkz. KafkaConsumerBase, IsPlatformAdmin=true yalnizca SORGULARI
            // gecerli kilar, TenantContext.TenantSlug'i DOLDURMAZ). Onceki halinde
            // bu satir "db.CurrentTenantSlug ?? \"\"" kullaniyordu - yayinlanan
            // leave.approved/rejected event'i HER ZAMAN TenantSlug="" ile cikiyordu,
            // bu da timeshift-service'teki ShiftOverride olusturma bugunu besleyen
            // kok nedendi (hardcore test, canli ikinci kullanici ile dogrulandi).
            // Zaten normal HTTP akisiyla olusmus ve dogru TenantSlug'a sahip olan
            // `leave` entity'sinin kendi TenantSlug'ini kullaniyoruz.
            Payload = JsonSerializer.Serialize(new LeaveDecidedEvent(
                leave.TenantSlug,
                leave.Id, leave.EmployeeId, leave.StartDate, leave.EndDate,
                evt.Approved, DateTimeOffset.UtcNow, leave.Type.ToString(), leave.Days, leave.Hours)),
        });

        Logger.LogInformation(
            "İzin talebi {Id} workflow kararıyla {Status} yapıldı", leave.Id, leave.Status);
    }

    private record WorkflowDecidedPayload(
        Guid WorkflowRequestId, string WorkflowType, Guid RequesterEmployeeId,
        string? Subject, bool Approved, Guid DecidedByEmployeeId,
        string? Comment, DateTimeOffset OccurredAt);

}

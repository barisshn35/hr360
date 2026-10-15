using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using NotificationService.Data;
using NotificationService.Models;

namespace NotificationService.Messaging;

/// <summary>
/// Employee ve Workflow olaylarini dinler, ilgili kisiye bildirim uretir.
/// Bildirim yalnizca kuyruga alinir; gercek gonderim (SMTP/push) ayri bir
/// worker'in isi - bu servis "ne gonderilecek" sorusunu cevaplar.
/// </summary>
public class HrEventConsumer : KafkaConsumerBase
{
    private static readonly JsonSerializerOptions JsonOpts =
        new() { PropertyNameCaseInsensitive = true };

    public HrEventConsumer(IServiceProvider services, ILogger<HrEventConsumer> logger)
        : base(services, logger, "notification-service",
               "hr360.employee.events", "hr360.workflow.events") { }

    protected override string ConsumerName => "notification-service";

    /// <summary>Alıcının bildirim dili; tercih yoksa Türkçe.</summary>
    private static async Task<string> LangOfAsync(NotificationDbContext db, string tenant, Guid employeeId, CancellationToken ct) =>
        NotificationTexts.Normalize(await db.Preferences.AsNoTracking()
            .Where(p => p.TenantSlug == tenant && p.EmployeeId == employeeId)
            .Select(p => p.Language).FirstOrDefaultAsync(ct));

    protected override async Task HandleAsync(
        string eventType, string payload, NotificationDbContext db, CancellationToken ct)
    {
        switch (eventType)
        {
            case "employee.hired":
            {
                var e = JsonSerializer.Deserialize<HiredPayload>(payload, JsonOpts);
                if (e is null) break;
                var lang = await LangOfAsync(db, e.TenantSlug, e.EmployeeId, ct);
                var (subject, body) = NotificationTexts.Hired(lang, e.FirstName, e.LastName, e.HireDate);
                db.Notifications.Add(new Notification
                {
                    // Arka plan tuketicisinde TenantContext doldurulmadigi icin
                    // StampTenant() bu alani yazamiyor; olayin kendi TenantSlug'i atanir.
                    TenantSlug = e.TenantSlug,
                    RecipientEmployeeId = e.EmployeeId,
                    RecipientEmail = e.Email,
                    Channel = NotificationChannel.Email,
                    TemplateCode = "employee.hired",
                    Subject = subject,
                    Body = body,
                    Language = lang,
                });
                break;
            }

            case "employee.assigned":
            {
                var e = JsonSerializer.Deserialize<AssignedPayload>(payload, JsonOpts);
                if (e is null) break;
                var lang = await LangOfAsync(db, e.TenantSlug, e.EmployeeId, ct);
                var (subject, body) = NotificationTexts.Assigned(lang, e.FirstName, e.EffectiveFrom, e.PositionTitle);
                db.Notifications.Add(new Notification
                {
                    TenantSlug = e.TenantSlug,
                    RecipientEmployeeId = e.EmployeeId,
                    RecipientEmail = e.Email,
                    Channel = NotificationChannel.Email,
                    TemplateCode = "employee.assigned",
                    Subject = subject,
                    Body = body,
                    Language = lang,
                });
                break;
            }

            case "workflow.submitted":
            {
                var e = JsonSerializer.Deserialize<SubmittedPayload>(payload, JsonOpts);
                if (e is null) break;
                var lang = await LangOfAsync(db, e.TenantSlug, e.ApproverEmployeeId, ct);
                var (subject, body) = NotificationTexts.Submitted(lang, e.ApproverFirstName, e.RequesterName, e.WorkflowType, e.SlaDueAt,
                    e.Escalated, e.OnBehalfOfEmployeeId is not null, hasLink: e.ActionToken is not null);
                db.Notifications.Add(new Notification
                {
                    TenantSlug = e.TenantSlug,
                    RecipientEmployeeId = e.ApproverEmployeeId,
                    RecipientEmail = e.ApproverEmail,
                    Channel = NotificationChannel.Email,
                    TemplateCode = "workflow.submitted",
                    Subject = subject,
                    Body = body,
                    Language = lang,
                    // Tek kullanımlık karar sayfası (e-posta tarayıcıları açsa da karar vermez; sayfada ayrıca onay istenir).
                    ActionUrl = e.ActionToken is null ? null : $"{PublicOrigin}/onay-eposta?t={Uri.EscapeDataString(e.ActionToken)}",
                    ActionLabel = e.ActionToken is null ? null : (lang == "en" ? "Review and decide" : "İncele ve karar ver"),
                });
                break;
            }

            case "workflow.approved":
            case "workflow.rejected":
            {
                var e = JsonSerializer.Deserialize<WorkflowPayload>(payload, JsonOpts);
                if (e is null) break;
                var lang = await LangOfAsync(db, e.TenantSlug, e.RequesterEmployeeId, ct);
                var (subject, body) = NotificationTexts.Decided(lang, e.Approved, e.Subject ?? e.WorkflowType, e.Comment);
                db.Notifications.Add(new Notification
                {
                    TenantSlug = e.TenantSlug,
                    RecipientEmployeeId = e.RequesterEmployeeId,
                    Channel = NotificationChannel.InApp,
                    TemplateCode = eventType,
                    Subject = subject,
                    Body = body,
                    Language = lang,
                });
                break;
            }
        }
    }

    // NOT: bu 4 kayit onceden TenantSlug alanini HIC bildirmiyordu - ilgili
    // upstream event'ler (EmployeeHiredEvent/EmployeeAssignedEvent/
    // WorkflowSubmittedEvent/WorkflowDecidedEvent) bunu tasisa da,
    // System.Text.Json (PropertyNameCaseInsensitive) eslesmeyen alani
    // sessizce yok sayiyordu, deserialize sonrasi kayboluyordu. Simdi
    // bildiriyoruz ki yukarida Notification.TenantSlug'e atayabilelim
    // (hardcore test, 3. tur - bildirimler gercek kiracı kullanicilarina
    // hic gorunmuyordu).
    private record HiredPayload(
        string TenantSlug, Guid EmployeeId, string FirstName, string LastName, string Email,
        DateOnly HireDate, DateTimeOffset OccurredAt);

    private static readonly string PublicOrigin =
        (Environment.GetEnvironmentVariable("PUBLIC_ORIGIN") is { Length: > 0 } o ? o : "http://localhost").TrimEnd('/');

    private record SubmittedPayload(
        string TenantSlug, Guid WorkflowRequestId, string WorkflowType, Guid RequesterEmployeeId,
        string? RequesterName, string? Subject, Guid ApproverEmployeeId,
        string ApproverEmail, string ApproverFirstName, DateTimeOffset? SlaDueAt,
        DateTimeOffset OccurredAt, string? ActionToken = null, Guid? OnBehalfOfEmployeeId = null, bool Escalated = false);

    private record AssignedPayload(
        string TenantSlug, Guid EmployeeId, Guid AssignmentId, Guid DepartmentId,
        string? PositionTitle, DateOnly EffectiveFrom, DateTimeOffset OccurredAt,
        string Email, string FirstName, string LastName);

    private record WorkflowPayload(
        string TenantSlug, Guid WorkflowRequestId, string WorkflowType, Guid RequesterEmployeeId,
        string? Subject, bool Approved, Guid DecidedByEmployeeId,
        string? Comment, DateTimeOffset OccurredAt);
}

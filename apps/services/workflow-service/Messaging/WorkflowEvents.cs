namespace WorkflowService.Messaging;

public static class WorkflowTopics
{
    public const string Events = "hr360.workflow.events";
}

public static class WorkflowEventTypes
{
    public const string Submitted = "workflow.submitted";
    public const string Approved = "workflow.approved";
    public const string Rejected = "workflow.rejected";
}

/// <summary>
/// Bir onay adimi olusturuldugunda (yeni talep veya sira bir sonraki
/// adima gectiginde) yayinlanir - o adimin onaycisina "karar bekleyen
/// bir talebiniz var" bildirimi gondermek icin.
/// </summary>
public record WorkflowSubmittedEvent(
    string TenantSlug,
    Guid WorkflowRequestId,
    string WorkflowType,
    Guid RequesterEmployeeId,
    string? RequesterName,
    string? Subject,
    Guid ApproverEmployeeId,
    string ApproverEmail,
    string ApproverFirstName,
    DateTimeOffset? SlaDueAt,
    DateTimeOffset OccurredAt,
    // E-postadan tek tıkla karar için tek kullanımlık jeton (yalnızca e-posta bildirimi kullanır).
    string? ActionToken = null,
    // Adım vekile geçtiyse asıl onaycı (bilgi amaçlı).
    Guid? OnBehalfOfEmployeeId = null,
    // Süre aşımıyla üst yöneticiye iletildiyse true.
    bool Escalated = false);

/// <summary>
/// Onay akisi sonuclandiginda yayinlanir. Talebi baslatan servisler
/// (leave, expense) bunu dinleyip kendi kayitlarini kapatir.
/// </summary>
public record WorkflowDecidedEvent(
    string TenantSlug,
    Guid WorkflowRequestId,
    string WorkflowType,
    Guid RequesterEmployeeId,
    string? Subject,
    bool Approved,
    Guid DecidedByEmployeeId,
    string? Comment,
    DateTimeOffset OccurredAt);

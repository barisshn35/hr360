using NotificationService.Tenancy;

namespace NotificationService.Models;

public enum NotificationChannel { InApp, Email, Push, Sms }

public class NotificationTemplate : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Kod ile cagrilir: ornegin "leave.approved".</summary>
    public required string Code { get; set; }
    public NotificationChannel Channel { get; set; } = NotificationChannel.InApp;
    public string Locale { get; set; } = "tr";
    public string? SubjectTemplate { get; set; }
    public required string BodyTemplate { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

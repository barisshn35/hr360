using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NotificationService.Data;
using NotificationService.Models;

namespace NotificationService.Controllers;

/// <summary>
/// İzleme alarmlarının e-postası (Alertmanager webhook alıcısı). Alertmanager SMTP'ye kendisi bağlanmaz:
/// alarm buraya gelir ve platformun e-posta ayarıyla (Platform › Sistem ayarları ya da kurulumdaki SMTP)
/// gönderilir. Alıcılar Alertmanager yapılandırmasındaki "to" sorgu parametresidir
/// (deploy/monitoring/alertmanager-entrypoint.sh, ALERT_EMAIL_TO). Anahtar: Authorization: Bearer
/// INTERNAL_SERVICE_TOKEN (anahtar yoksa ya da yanlışsa 404); gateway /api/internal/ yollarını dışarıya kapatır.
/// </summary>
[ApiController]
[AllowAnonymous]
public class InternalAlertsController : ControllerBase
{
    public const string PlatformSlug = "platform";
    public const string TemplateCode = "security.monitoring-alert";
    public const int MaxRecipients = 10;
    public const int MaxAlertsInMail = 20;

    private readonly NotificationDbContext _db;
    public InternalAlertsController(NotificationDbContext db) => _db = db;

    [HttpPost("/api/internal/alerts")]
    public async Task<IActionResult> Receive([FromQuery] string? to, [FromBody] AlertmanagerPayload payload,
        [FromServices] Tenancy.TenantContext tenant, CancellationToken ct)
    {
        var auth = Request.Headers.Authorization.FirstOrDefault();
        var token = auth is not null && auth.StartsWith("Bearer ", StringComparison.Ordinal) ? auth["Bearer ".Length..] : null;
        if (!Security.InternalServiceToken.Matches(token)) return NotFound();

        var recipients = Recipients(to);
        if (recipients.Count == 0) return BadRequest(new { message = "Alıcı belirtilmedi" });
        if (payload.Alerts is not { Count: > 0 }) return Ok(new { queued = 0 });

        var (subject, body) = Render(payload);
        var origin = Environment.GetEnvironmentVariable("PUBLIC_ORIGIN")?.TrimEnd('/');
        tenant.TenantSlug = PlatformSlug;
        tenant.IsPlatformAdmin = false;
        foreach (var email in recipients)
        {
            _db.Notifications.Add(new Notification
            {
                TenantSlug = PlatformSlug,
                RecipientEmployeeId = Guid.Empty,
                RecipientEmail = email,
                Channel = NotificationChannel.Email,
                TemplateCode = TemplateCode,
                Subject = subject,
                Body = body,
                Language = "tr",
                Status = NotificationStatus.Pending,
                ActionUrl = string.IsNullOrEmpty(origin) ? null : $"{origin}/grafana/alerting/groups?dataSource=alertmanager",
                ActionLabel = string.IsNullOrEmpty(origin) ? null : "Grafana'da aç",
            });
        }
        await _db.SaveChangesAsync(ct);
        return Ok(new { queued = recipients.Count });
    }

    /// <summary>Virgüllü alıcı listesi; geçersiz adresler atılır, en fazla <see cref="MaxRecipients"/>.</summary>
    public static List<string> Recipients(string? to) =>
        (to ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(a => a.Length <= 254 && System.Net.Mail.MailAddress.TryCreate(a, out var m) && m.Address == a)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxRecipients)
            .ToList();

    /// <summary>Alertmanager grubu → konu ve düz metin gövde (alarm adı, servis, özet, başlangıç).</summary>
    public static (string Subject, string Body) Render(AlertmanagerPayload p)
    {
        var alerts = p.Alerts ?? [];
        var firing = alerts.Count(a => a.Status == "firing");
        var name = Label(p.CommonLabels, "alertname") ?? Label(alerts.FirstOrDefault()?.Labels, "alertname") ?? "Alarm";
        var critical = alerts.Any(a => a.Status == "firing" && Label(a.Labels, "severity") == "critical");
        var subject = firing > 0
            ? $"[HR360] {(critical ? "KRİTİK" : "Uyarı")}: {name}{(alerts.Count > 1 ? $" ({firing} alarm)" : "")}"
            : $"[HR360] Düzeldi: {name}";
        var sb = new StringBuilder();
        sb.AppendLine(firing > 0 ? $"{firing} alarm etkin." : "Alarmlar düzeldi.");
        foreach (var a in alerts.Take(MaxAlertsInMail))
        {
            sb.AppendLine();
            var service = Label(a.Labels, "service");
            sb.AppendLine($"• {(a.Status == "firing" ? "Etkin" : "Düzeldi")} — {Label(a.Labels, "alertname") ?? name}{(service is null ? "" : $" ({service})")}");
            if (Label(a.Annotations, "summary") is { } summary) sb.AppendLine($"  {summary}");
            if (Label(a.Annotations, "description") is { } desc) sb.AppendLine($"  {desc}");
            if (a.StartsAt is { } s && s.Year > 2000)
                sb.AppendLine($"  Başlangıç: {TimeZoneInfo.ConvertTime(s, Istanbul):dd.MM.yyyy HH:mm} (İstanbul)");
        }
        if (alerts.Count > MaxAlertsInMail) sb.AppendLine().AppendLine($"…ve {alerts.Count - MaxAlertsInMail} alarm daha.");
        var body = sb.ToString().TrimEnd();
        return (Clip(subject, InternalChatController.MaxSubject), Clip(body, InternalChatController.MaxBody));
    }

    private static string? Label(Dictionary<string, string>? d, string key) =>
        d is not null && d.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static readonly TimeZoneInfo Istanbul = FindIstanbul();
    private static TimeZoneInfo FindIstanbul()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.CreateCustomTimeZone("TRT", TimeSpan.FromHours(3), "TRT", "TRT"); }
    }
}

/// <summary>Alertmanager webhook gövdesi (sürüm 4); yalnızca kullanılan alanlar.</summary>
public sealed class AlertmanagerPayload
{
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("commonLabels")] public Dictionary<string, string>? CommonLabels { get; set; }
    [JsonPropertyName("alerts")] public List<AlertmanagerAlert>? Alerts { get; set; }
}

public sealed class AlertmanagerAlert
{
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("labels")] public Dictionary<string, string>? Labels { get; set; }
    [JsonPropertyName("annotations")] public Dictionary<string, string>? Annotations { get; set; }
    [JsonPropertyName("startsAt")] public DateTimeOffset? StartsAt { get; set; }
}

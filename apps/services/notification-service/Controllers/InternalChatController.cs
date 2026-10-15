using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NotificationService.Data;
using NotificationService.Messaging;
using NotificationService.Models;

namespace NotificationService.Controllers;

/// <summary>
/// Sohbet botunun (governance-service) servisler arası uçları. Bot bildirim tablosuna doğrudan
/// yazmaz; uygulama içi bildirim bu servisin kendi modeliyle (StampTenant, denetim, push/sessiz
/// saat kuralları) oluşturulur. X-Internal-Token ile korunur (anahtar yoksa uç kapalı, 404);
/// gateway /api/*/internal/ yollarını dışarıya kapatır.
/// </summary>
[ApiController]
[AllowAnonymous]
public partial class InternalChatController : ControllerBase
{
    public const int MaxSubject = 300;
    public const int MaxBody = 4000;

    private readonly NotificationDbContext _db;
    public InternalChatController(NotificationDbContext db) => _db = db;

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{0,99}$")]
    private static partial Regex TemplateCodeRegex();

    /// <summary>
    /// Uygulama içi bildirim (gelen kutusu). Metni çağıran verir (bot metinleri Türkçe: dil
    /// varsayılan "tr"); kategori şablon kodundan çıkar (ör. engagement.kudos → duyurular),
    /// kişinin kapattığı kategoriler gelen kutusunda zaten gizlenir. Anlık bildirim (Web Push)
    /// ve sessiz saat, diğer uygulama içi bildirimlerdeki gibi WebPush işçisince uygulanır.
    /// </summary>
    [HttpPost("/api/internal/chat/notify")]
    public async Task<IActionResult> Notify([FromBody] InternalChatNotifyRequest request,
        [FromServices] Tenancy.TenantContext tenant, CancellationToken ct)
    {
        if (!InternalToken.Valid(Request)) return NotFound();
        var error = Validate(request);
        if (error is not null) return BadRequest(new { message = error });

        tenant.TenantSlug = request.TenantSlug;
        tenant.IsPlatformAdmin = false;
        // Çalışan kaydı olmayan hesap (ör. yalnızca yönetim için açılmış şirket yöneticisi): gelen kutusu
        // çalışan kimliği ister; bildirim e-postayla gider (EmailSenderWorker, "security." kapatılamaz).
        var emailOnly = request.RecipientEmployeeId == Guid.Empty;
        var n = new Notification
        {
            TenantSlug = request.TenantSlug,
            RecipientEmployeeId = request.RecipientEmployeeId,
            RecipientEmail = emailOnly ? request.RecipientEmail!.Trim() : null,
            Channel = emailOnly ? NotificationChannel.Email : NotificationChannel.InApp,
            TemplateCode = string.IsNullOrWhiteSpace(request.TemplateCode) ? null : request.TemplateCode.Trim(),
            Subject = string.IsNullOrWhiteSpace(request.Subject) ? null : request.Subject.Trim(),
            Body = request.Body!.Trim(),
            Language = NotificationTexts.Normalize(request.Language),
            Status = NotificationStatus.Pending,
            ActionUrl = string.IsNullOrWhiteSpace(request.ActionUrl) ? null : request.ActionUrl.Trim(),
            ActionLabel = string.IsNullOrWhiteSpace(request.ActionLabel) ? null : request.ActionLabel.Trim(),
        };
        _db.Notifications.Add(n);
        await _db.SaveChangesAsync(ct);
        return Ok(new { n.Id, status = n.Status.ToString() });
    }

    /// <summary>Kural denetimi; hata yoksa null, varsa Türkçe ileti.</summary>
    public static string? Validate(InternalChatNotifyRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.TenantSlug) || r.TenantSlug.Length > 64) return "Kiracı belirtilmedi";
        if (r.RecipientEmployeeId == Guid.Empty)
        {
            // E-posta ile gönderim yalnızca güvenlik bildirimleri için (çalışan kaydı olmayan yönetici hesabı).
            if (string.IsNullOrWhiteSpace(r.RecipientEmail)) return "Alıcı belirtilmedi";
            if (r.RecipientEmail.Length > 254 || !System.Net.Mail.MailAddress.TryCreate(r.RecipientEmail.Trim(), out _)) return "Alıcı e-posta adresi geçersiz";
            if (r.TemplateCode?.Trim().StartsWith("security.", StringComparison.Ordinal) != true) return "E-posta ile bildirim yalnızca güvenlik bildirimleri için";
        }
        if (string.IsNullOrWhiteSpace(r.Body)) return "Bildirim metni boş olamaz";
        if (r.Body.Length > MaxBody) return $"Bildirim metni en fazla {MaxBody} karakter olabilir";
        if (r.Subject is { Length: > MaxSubject }) return $"Konu en fazla {MaxSubject} karakter olabilir";
        if (!string.IsNullOrWhiteSpace(r.TemplateCode) && !TemplateCodeRegex().IsMatch(r.TemplateCode.Trim()))
            return "Geçersiz şablon kodu";
        if (r.Language is not (null or "tr" or "en")) return "Dil 'tr' ya da 'en' olmalı.";
        // E-postadaki düğme (ör. parola belirleme linki): yalnızca mutlak http(s) adres.
        if (!string.IsNullOrWhiteSpace(r.ActionUrl)
            && (r.ActionUrl.Length > 1000 || !Uri.TryCreate(r.ActionUrl.Trim(), UriKind.Absolute, out var au) || au.Scheme is not ("http" or "https")))
            return "Geçersiz eylem adresi";
        if (r.ActionLabel is { Length: > 80 }) return "Düğme metni en fazla 80 karakter olabilir";
        return null;
    }
}

/// <summary>Servisler arası anahtar denetimi (PreferencesController.Effective ile aynı kural).</summary>
public static class InternalToken
{
    public static bool Valid(HttpRequest request) =>
        Security.InternalServiceToken.Matches(request.Headers[Security.InternalServiceToken.Header].FirstOrDefault());
}

public record InternalChatNotifyRequest(string TenantSlug, Guid RecipientEmployeeId, string? Subject, string? Body,
    string? TemplateCode, string? Language = null, string? RecipientEmail = null, string? ActionUrl = null, string? ActionLabel = null);

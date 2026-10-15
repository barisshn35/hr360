using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NotificationService.Data;
using NotificationService.Messaging;
using NotificationService.Models;
using NotificationService.Preferences;
using NotificationService.Services;

namespace NotificationService.Controllers;

[ApiController]
[Route("api/notifications")]
[Authorize]
public partial class NotificationsController : ControllerBase
{
    private readonly NotificationDbContext _db;
    private readonly EmployeeDirectoryClient _employees;
    public NotificationsController(NotificationDbContext db, EmployeeDirectoryClient employees)
    {
        _db = db;
        _employees = employees;
    }

    [GeneratedRegex(@"\{\{(\w+)\}\}")]
    private static partial Regex PlaceholderRegex();

    /// <summary>{{key}} yer tutucularini verilen degerlerle degistirir.</summary>
    private static string Render(string template, IDictionary<string, string>? data)
    {
        if (data is null || data.Count == 0) return template;
        return PlaceholderRegex().Replace(template, m =>
            data.TryGetValue(m.Groups[1].Value, out var value) ? value : m.Value);
    }

    /// <summary>"RequireHrAdmin" politikasiyla ayni rol kumesi - baskalarinin
    /// bildirimlerini gorebilen/yonetebilen roller.</summary>
    private bool CanSeeAllNotifications =>
        User.IsInRole("hr-admin") || User.IsInRole("tenant-admin") ||
        User.IsInRole("platform-admin") || User.IsInRole("ext-notification-manage");

    /// <summary>
    /// GUVENLIK: Onceki halinde GetAll / unread-count / mark-read yalnizca
    /// [Authorize] ile korunuyordu - kiracidaki HERHANGI bir kullanici
    /// recipientId vermeden TUM kiracinin bildirimlerini, ya da baskasinin
    /// recipientId'siyle onun gelen kutusunu (red gerekceleri dahil) okuyup
    /// okundu isaretleyebiliyordu (hardcore test, son tur - canli olarak
    /// Mehmet'in Ayse'nin kutusunu okuyabildigi dogrulandi). Artik yonetici
    /// olmayanlar yalnizca kendi Employee.Id'lerine kilitli. /me cozulemezse
    /// FAIL-CLOSED (null doner, cagiran Forbid eder).
    /// </summary>
    private async Task<(bool Allowed, Guid? EffectiveRecipient)> ResolveRecipientScopeAsync(Guid? requested)
    {
        if (CanSeeAllNotifications) return (true, requested);

        var me = await _employees.FindMyEmployeeIdAsync(HttpContext.RequestAborted);
        if (me is null) return (false, null);
        if (requested.HasValue && requested.Value != me.Value) return (false, null);
        return (true, me.Value);
    }

    /// <summary>G11: kişinin uygulama içinde kapattığı (zorunlu olmayan) kategoriler listede ve sayaçta görünmez.</summary>
    private async Task<IQueryable<Notification>> HideMutedAsync(IQueryable<Notification> q, Guid recipient)
    {
        var muted = await _db.CategoryPreferences.AsNoTracking()
            .Where(c => c.EmployeeId == recipient && !c.InApp).Select(c => c.Category).ToListAsync(HttpContext.RequestAborted);
        return muted.Count == 0 ? q : q.Where(NotificationCategories.VisibleInApp(muted));
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] Guid? recipientId, [FromQuery] NotificationStatus? status, [FromQuery] int limit = 50)
    {
        var (allowed, effective) = await ResolveRecipientScopeAsync(recipientId);
        if (!allowed) return Forbid();
        recipientId = effective;

        var q = _db.Notifications.AsQueryable();
        if (recipientId.HasValue) q = await HideMutedAsync(q.Where(n => n.RecipientEmployeeId == recipientId.Value), recipientId.Value);
        if (status.HasValue) q = q.Where(n => n.Status == status.Value);
        return Ok(await q.OrderByDescending(n => n.CreatedAt).Take(Math.Clamp(limit, 1, 200)).ToListAsync());
    }

    /// <summary>Serbest metinle veya sablon koduyla bildirim kuyruga alinir.</summary>
    [HttpPost]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> Create([FromBody] CreateNotificationRequest request)
    {
        string subject;
        string body;

        if (!string.IsNullOrWhiteSpace(request.TemplateCode))
        {
            var tpl = await _db.Templates.FirstOrDefaultAsync(t =>
                t.Code == request.TemplateCode &&
                t.Channel == request.Channel &&
                t.Locale == (request.Locale ?? "tr") &&
                t.IsActive);
            if (tpl is null) return BadRequest("Sablon bulunamadi");

            subject = Render(tpl.SubjectTemplate ?? string.Empty, request.Data);
            body = Render(tpl.BodyTemplate, request.Data);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(request.Body))
                return BadRequest("Sablon kodu ya da govde metni verilmeli");
            subject = request.Subject ?? string.Empty;
            body = request.Body;
        }

        var notification = new Notification
        {
            RecipientEmployeeId = request.RecipientEmployeeId,
            Channel = request.Channel,
            TemplateCode = request.TemplateCode,
            Subject = string.IsNullOrWhiteSpace(subject) ? null : subject,
            Body = body
        };

        // NOT: RecipientEmail burada doldurulmadan once bu uc her zaman
        // null birakiyordu - Email kanalli her bildirim EmailSenderWorker'a
        // ulastiginda "Alici e-posta adresi yok" ile kesin basarisiz
        // oluyordu (hardcore test sirasinda bulundu). Sadece Email kanali
        // icin cozuyoruz - diger kanallar (InApp/Push/Sms) buna ihtiyac
        // duymuyor ve gereksiz bir cross-service cagridan kacinilmis olur.
        // Cozulemezse (calisan bulunamadi, servis erisilemez) sessizce null
        // birakilir - EmailSenderWorker zaten bunu acik bir hata mesajiyla
        // Failed'e dusurup onceki (guvenli) davranisi korur.
        if (request.Channel == NotificationChannel.Email)
            notification.RecipientEmail = await _employees.GetEmailAsync(request.RecipientEmployeeId, HttpContext.RequestAborted);

        // E-posta cercevesi (dugme, alt bilgi) alicinin dil tercihine gore; metni cagiran verir.
        notification.Language = NotificationTexts.Normalize(await _db.Preferences.AsNoTracking()
            .Where(p => p.EmployeeId == request.RecipientEmployeeId)
            .Select(p => p.Language).FirstOrDefaultAsync(HttpContext.RequestAborted));

        _db.Notifications.Add(notification);
        await _db.SaveChangesAsync();
        return Created($"/api/notifications/{notification.Id}", notification);
    }

    /// <summary>Gonderim worker'i tarafindan cagrilir.</summary>
    [HttpPost("{id}/mark-sent")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> MarkSent(Guid id, [FromBody] MarkSentRequest request)
    {
        var n = await _db.Notifications.FirstOrDefaultAsync(x => x.Id == id);
        if (n is null) return NotFound();

        n.AttemptCount++;
        if (request.Success)
        {
            n.Status = NotificationStatus.Sent;
            n.SentAt = DateTimeOffset.UtcNow;
            n.FailureReason = null;
        }
        else
        {
            n.Status = NotificationStatus.Failed;
            n.FailureReason = request.FailureReason;
        }

        await _db.SaveChangesAsync();
        return Ok(n);
    }

    [HttpPost("{id}/mark-read")]
    public async Task<IActionResult> MarkRead(Guid id)
    {
        var n = await _db.Notifications.FirstOrDefaultAsync(x => x.Id == id);
        if (n is null) return NotFound();

        // Baskasinin bildirimi: varligini sizdirmamak icin 404.
        var (allowed, _) = await ResolveRecipientScopeAsync(
            CanSeeAllNotifications ? null : n.RecipientEmployeeId);
        if (!allowed) return NotFound();

        n.Status = NotificationStatus.Read;
        n.ReadAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(n);
    }

    [HttpGet("unread-count")]
    public async Task<IActionResult> GetUnreadCount([FromQuery] Guid recipientId)
    {
        var (allowed, _) = await ResolveRecipientScopeAsync(recipientId);
        if (!allowed) return Forbid();

        var q = await HideMutedAsync(_db.Notifications.Where(n => n.RecipientEmployeeId == recipientId), recipientId);
        var count = await q.CountAsync(n => n.Status != NotificationStatus.Read);
        return Ok(new { recipientId, unreadCount = count });
    }
}

public record CreateNotificationRequest(
    Guid RecipientEmployeeId, NotificationChannel Channel, string? TemplateCode,
    string? Locale, string? Subject, string? Body, Dictionary<string, string>? Data);
public record MarkSentRequest(bool Success, string? FailureReason);

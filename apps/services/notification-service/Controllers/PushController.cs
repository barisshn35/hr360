using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NotificationService.Data;
using NotificationService.Models;
using NotificationService.Push;
using NotificationService.Services;

namespace NotificationService.Controllers;

/// <summary>
/// Anlık bildirim (PWA / Web Push) aboneliği. Kişi her cihazda ayrı açar; oturum kapanınca
/// arayüz aboneliği siler. Push içeriğinde kişisel veri yoktur.
/// </summary>
[ApiController]
[Route("api/notifications/push")]
[Authorize]
public class PushController : ControllerBase
{
    private readonly NotificationDbContext _db;
    private readonly EmployeeDirectoryClient _employees;
    private readonly VapidKeys _keys;

    public PushController(NotificationDbContext db, EmployeeDirectoryClient employees, VapidKeys keys)
    {
        _db = db; _employees = employees; _keys = keys;
    }

    [HttpGet("public-key")]
    public async Task<IActionResult> PublicKey(CancellationToken ct) => Ok(new { publicKey = (await _keys.GetAsync(ct)).Public });

    public record KeysInput(string P256dh, string Auth);
    public record SubscribeInput(string Endpoint, KeysInput Keys, string? Device);

    [HttpPost("subscriptions")]
    public async Task<IActionResult> Subscribe([FromBody] SubscribeInput body, CancellationToken ct)
    {
        var me = await _employees.FindMyEmployeeIdAsync(ct);
        if (me is null) return StatusCode(403, new { message = "Hesabınıza bağlı çalışan kaydı yok" });
        if (string.IsNullOrWhiteSpace(body.Endpoint) || body.Endpoint.Length > 1000 || !PushEndpointPolicy.IsAllowed(body.Endpoint))
            return BadRequest(new { message = "Desteklenmeyen anlık bildirim servisi" });
        byte[] p256, auth;
        try { p256 = WebPushCrypto.UnB64(body.Keys.P256dh); auth = WebPushCrypto.UnB64(body.Keys.Auth); }
        catch (FormatException) { return BadRequest(new { message = "Abonelik anahtarları geçersiz" }); }
        if (p256.Length != 65 || p256[0] != 4 || auth.Length != 16) return BadRequest(new { message = "Abonelik anahtarları geçersiz" });

        // Uç nokta tekildir: aynı tarayıcı başka bir hesapla açılırsa abonelik yeni hesaba geçer.
        var existing = await _db.PushSubscriptions.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.Endpoint == body.Endpoint, ct);
        if (existing is not null) _db.PushSubscriptions.Remove(existing);
        _db.PushSubscriptions.Add(new PushSubscription
        {
            EmployeeId = me.Value, Endpoint = body.Endpoint, P256dh = body.Keys.P256dh, Auth = body.Keys.Auth,
            Device = body.Device is { Length: > 0 } d ? d[..Math.Min(d.Length, 120)] : null,
        });
        await _db.SaveChangesAsync(ct);
        return Ok(new { subscribed = true });
    }

    public record UnsubscribeInput(string Endpoint);

    [HttpPost("subscriptions/delete")]
    public async Task<IActionResult> Unsubscribe([FromBody] UnsubscribeInput body, CancellationToken ct)
    {
        var me = await _employees.FindMyEmployeeIdAsync(ct);
        var row = await _db.PushSubscriptions.FirstOrDefaultAsync(p => p.Endpoint == body.Endpoint && p.EmployeeId == me, ct);
        if (row is not null) { _db.PushSubscriptions.Remove(row); await _db.SaveChangesAsync(ct); }
        return NoContent();
    }

    [HttpGet("subscriptions/me")]
    public async Task<IActionResult> Mine(CancellationToken ct)
    {
        var me = await _employees.FindMyEmployeeIdAsync(ct);
        return Ok(await _db.PushSubscriptions.AsNoTracking().Where(p => p.EmployeeId == me)
            .Select(p => new { p.Id, p.Device, p.CreatedAt, p.LastSuccessAt }).ToListAsync(ct));
    }

    /// <summary>Kendi cihazlarına deneme bildirimi (uygulama içi bildirim olarak düşer, push ile iletilir).</summary>
    [HttpPost("test")]
    public async Task<IActionResult> Test(CancellationToken ct)
    {
        var me = await _employees.FindMyEmployeeIdAsync(ct);
        if (me is null) return StatusCode(403, new { message = "Hesabınıza bağlı çalışan kaydı yok" });
        var lang = await _db.Preferences.AsNoTracking().Where(p => p.EmployeeId == me).Select(p => p.Language).FirstOrDefaultAsync(ct) ?? "tr";
        _db.Notifications.Add(new Notification
        {
            RecipientEmployeeId = me.Value, Channel = NotificationChannel.InApp, Language = lang,
            Subject = lang == "en" ? "Test notification" : "Deneme bildirimi",
            Body = lang == "en" ? "Push notifications work on this device." : "Anlık bildirimler bu cihazda çalışıyor.",
            Status = NotificationStatus.Sent, SentAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        return Ok(new { queued = true });
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NotificationService.Data;
using NotificationService.Models;
using NotificationService.Security;
using NotificationService.Sms;
using NotificationService.Tenancy;

namespace NotificationService.Controllers;

/// <summary>
/// Kiracının SMS sağlayıcısı (Ayarlar › SMS; yalnızca şirket yöneticisi). Gizli anahtar şifreli saklanır ve
/// hiçbir yanıtta dönmez; boş bırakılırsa mevcut anahtar korunur. Deneme ucu ayarlarla tek bir SMS gönderir.
/// </summary>
[ApiController]
[Route("api/notifications/sms-settings")]
[Authorize(Policy = "RequireTenantAdmin")]
public class SmsSettingsController : ControllerBase
{
    private readonly NotificationDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly SmtpCredentialProtector _protector;
    private readonly IHttpClientFactory _http;

    public SmsSettingsController(NotificationDbContext db, ITenantContext tenant, SmtpCredentialProtector protector, IHttpClientFactory http)
    {
        _db = db;
        _tenant = tenant;
        _protector = protector;
        _http = http;
    }

    public record SmsSettingsInput(bool Enabled, string Provider, string Username, string? Secret, string Sender, int DailyLimit);
    public record SmsTestInput(string Phone);

    static object Shape(SmsSettings? s, DateTimeOffset now, int sentToday) => new
    {
        configured = s is not null,
        enabled = s?.Enabled ?? false,
        provider = s?.Provider ?? SmsProviders.Netgsm,
        username = s?.Username ?? "",
        hasSecret = !string.IsNullOrEmpty(s?.SecretEnc),
        sender = s?.Sender ?? "",
        dailyLimit = s?.DailyLimit ?? 200,
        sentToday,
        providers = SmsProviders.All,
        updatedAt = s?.UpdatedAt,
    };

    bool NoTenant => string.IsNullOrWhiteSpace(_tenant.TenantSlug);

    async Task<int> SentTodayAsync(CancellationToken ct)
    {
        var start = SmsSenderWorker.DayStartUtc(DateTimeOffset.UtcNow);
        return await _db.Notifications.CountAsync(n => n.Channel == NotificationChannel.Sms
            && n.Status == NotificationStatus.Sent && n.SentAt >= start, ct);
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (NoTenant) return NotFound(new { message = "Kullanıcı bir şirkete bağlı değil" });
        var s = await _db.SmsSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        return Ok(Shape(s, DateTimeOffset.UtcNow, await SentTodayAsync(ct)));
    }

    [HttpPut]
    public async Task<IActionResult> Put([FromBody] SmsSettingsInput body, CancellationToken ct)
    {
        if (NoTenant) return NotFound(new { message = "Kullanıcı bir şirkete bağlı değil" });
        var provider = (body.Provider ?? "").Trim().ToLowerInvariant();
        if (!SmsProviders.All.Contains(provider)) return BadRequest(new { message = "Geçersiz SMS sağlayıcısı" });
        var user = (body.Username ?? "").Trim();
        var sender = (body.Sender ?? "").Trim();
        if (user.Length is 0 or > 120) return BadRequest(new { message = "Kullanıcı adı / API anahtarı gerekli (en fazla 120 karakter)" });
        // Gönderici adı (başlık): sağlayıcıda onaylı, en fazla 11 karakter (harf, rakam, boşluk, nokta, tire).
        if (sender.Length is 0 or > 11 || !System.Text.RegularExpressions.Regex.IsMatch(sender, @"^[\p{L}0-9 .\-]+$"))
            return BadRequest(new { message = "Gönderici adı 1–11 karakter olmalı (harf, rakam, boşluk, nokta, tire)" });
        if (body.DailyLimit is < 1 or > 100_000) return BadRequest(new { message = "Günlük sınır 1–100000 arasında olmalı" });
        if (body.Secret is { Length: > 200 }) return BadRequest(new { message = "Parola / gizli anahtar en fazla 200 karakter olabilir" });

        var s = await _db.SmsSettings.FirstOrDefaultAsync(ct);
        if (s is null)
        {
            s = new SmsSettings { TenantSlug = _tenant.TenantSlug! };
            _db.SmsSettings.Add(s);
        }
        if (!string.IsNullOrEmpty(body.Secret)) s.SecretEnc = _protector.Encrypt(body.Secret);
        // Sağlayıcı değiştiyse eski sağlayıcının anahtarı geçersizdir.
        else if (s.Provider != provider && !string.IsNullOrEmpty(s.SecretEnc))
            return BadRequest(new { message = "Sağlayıcı değişti: yeni sağlayıcının parolasını / gizli anahtarını girin" });
        if (string.IsNullOrEmpty(s.SecretEnc)) return BadRequest(new { message = "Parola / gizli anahtar gerekli" });
        s.Enabled = body.Enabled;
        s.Provider = provider;
        s.Username = user;
        s.Sender = sender;
        s.DailyLimit = body.DailyLimit;
        s.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(Shape(s, s.UpdatedAt, await SentTodayAsync(ct)));
    }

    /// <summary>SMS ayarını siler; bekleyen SMS'ler bir sonraki turda "sağlayıcı tanımlı değil" ile sonuçlanır.</summary>
    [HttpDelete]
    public async Task<IActionResult> Delete(CancellationToken ct)
    {
        if (NoTenant) return NotFound(new { message = "Kullanıcı bir şirkete bağlı değil" });
        await _db.SmsSettings.ExecuteDeleteAsync(ct);
        return NoContent();
    }

    /// <summary>Kayıtlı ayarla verilen numaraya deneme SMS'i (kayıt tutulmaz; numara günlüğe maskeli yazılır).</summary>
    [HttpPost("test")]
    public async Task<IActionResult> Test([FromBody] SmsTestInput body, [FromServices] ILogger<SmsSettingsController> log, CancellationToken ct)
    {
        if (NoTenant) return NotFound(new { message = "Kullanıcı bir şirkete bağlı değil" });
        var s = await _db.SmsSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (s is null) return BadRequest(new { message = "Önce SMS ayarlarını kaydedin" });
        var phone = SmsFormat.NormalizePhone(body.Phone);
        if (phone is null) return BadRequest(new { message = "Geçerli bir telefon numarası girin (ör. 0532 123 45 67)" });
        string secret;
        try { secret = _protector.Decrypt(s.SecretEnc); }
        catch (Exception) { return BadRequest(new { message = "Kayıtlı anahtar çözülemedi; parolayı yeniden girip kaydedin" }); }
        var r = await SmsProviders.SendAsync(_http.CreateClient("sms"), new SmsAccount(s.Provider, s.Username, secret, s.Sender),
            new SmsMessage(phone, "HR360 deneme iletisi: SMS ayarlarınız çalışıyor."), ct);
        log.LogInformation("Deneme SMS'i ({Provider}) {Phone}: {Result}", s.Provider, SmsFormat.Mask(phone), r.Ok ? "gonderildi" : r.Error);
        return r.Ok ? Ok(new { ok = true, reference = r.Reference }) : StatusCode(502, new { ok = false, message = r.Error });
    }
}

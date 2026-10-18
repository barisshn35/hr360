using Microsoft.EntityFrameworkCore;
using NotificationService.Data;
using NotificationService.Models;
using NotificationService.Preferences;
using NotificationService.Security;
using NotificationService.Tenancy;

namespace NotificationService.Sms;

/// <summary>
/// Bekleyen SMS kanallı bildirimleri kiracının sağlayıcısıyla (Ayarlar › SMS) gönderir.
/// Tercihler: kişi SMS'i kapattıysa Suppressed, sessiz saatte ertelenir (bkz. DeliveryRules.DecideSms).
/// Günlük sınır aşılırsa kalanlar ertesi gün 08:00'e (Europe/Istanbul) ertelenir. Geçici hata 3 denemeye
/// kadar yeniden denenir; kalıcı hata (yanlış parola, tanımsız başlık) hemen Failed. Sonuçlanan kayıtta
/// telefon numarası silinir (KVKK, veri en aza indirme).
/// </summary>
public class SmsSenderWorker : BackgroundService
{
    private const int BatchSize = 20;
    private const int MaxAttempts = 3;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("SMS_POLL_SECONDS"), out var s) ? Math.Max(1, s) : 30);

    private readonly IServiceProvider _services;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<SmsSenderWorker> _logger;

    public SmsSenderWorker(IServiceProvider services, IHttpClientFactory http, ILogger<SmsSenderWorker> logger)
    {
        _services = services;
        _http = http;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        HR360.Platform.LeaderGate.RunAsync("notification/SmsSenderWorker", stoppingToken, LeaderLoopAsync);

    private async Task LeaderLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ProcessBatchAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "SmsSenderWorker turu basarisiz oldu, bir sonraki turda tekrar denenecek");
            }
            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    /// <summary>Kişi için sonuçlanmış kayıt: numara tutulmaz.</summary>
    static void Finish(Notification n, NotificationStatus status, string? reason)
    {
        n.Status = status;
        n.FailureReason = reason;
        n.RecipientPhone = null;
        if (status == NotificationStatus.Sent) n.SentAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Bugünün (Europe/Istanbul) başlangıcı, UTC.</summary>
    public static DateTimeOffset DayStartUtc(DateTimeOffset nowUtc) =>
        QuietHoursCalc.ToUtc(QuietHoursCalc.ToLocal(nowUtc).Date);

    /// <summary>Günlük sınır dolunca: ertesi gün 08:00 (Europe/Istanbul), UTC.</summary>
    public static DateTimeOffset NextMorningUtc(DateTimeOffset nowUtc) =>
        QuietHoursCalc.ToUtc(QuietHoursCalc.ToLocal(nowUtc).Date.AddDays(1).AddHours(8));

    private async Task ProcessBatchAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var tenantContext = scope.ServiceProvider.GetRequiredService<TenantContext>();
        tenantContext.IsPlatformAdmin = true;
        var db = scope.ServiceProvider.GetRequiredService<NotificationDbContext>();

        var now = DateTimeOffset.UtcNow;
        var pending = await db.Notifications
            .Where(n => n.Channel == NotificationChannel.Sms && n.Status == NotificationStatus.Pending
                        && (n.DeferredUntil == null || n.DeferredUntil <= now))
            .OrderBy(n => n.AttemptCount).ThenBy(n => n.CreatedAt)
            .Take(BatchSize)
            .ToListAsync(ct);
        if (pending.Count == 0) return;

        var prefs = await PreferenceStore.LoadAsync(db, pending.Select(n => (n.TenantSlug, n.RecipientEmployeeId)), ct);
        var tenants = pending.Select(n => n.TenantSlug).Distinct().ToList();
        var settings = await db.SmsSettings.IgnoreQueryFilters().AsNoTracking()
            .Where(x => tenants.Contains(x.TenantSlug)).ToDictionaryAsync(x => x.TenantSlug, ct);
        var dayStart = DayStartUtc(now);
        var sentToday = await db.Notifications.IgnoreQueryFilters().AsNoTracking()
            .Where(n => tenants.Contains(n.TenantSlug) && n.Channel == NotificationChannel.Sms
                        && n.Status == NotificationStatus.Sent && n.SentAt >= dayStart)
            .GroupBy(n => n.TenantSlug).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var protector = scope.ServiceProvider.GetRequiredService<SmtpCredentialProtector>();
        var http = _http.CreateClient("sms");
        foreach (var n in pending)
        {
            if (!settings.TryGetValue(n.TenantSlug, out var cfg) || !cfg.Enabled)
            {
                Finish(n, NotificationStatus.Failed, "SMS sağlayıcısı tanımlı değil ya da kapalı (Ayarlar › SMS)");
                continue;
            }
            if (string.IsNullOrEmpty(n.RecipientPhone))
            {
                Finish(n, NotificationStatus.Failed, "Alıcının geçerli bir telefon numarası yok");
                continue;
            }
            prefs.TryGetValue((n.TenantSlug, n.RecipientEmployeeId), out var p);
            var (decision, until) = DeliveryRules.DecideSms(n.TemplateCode, p, now);
            if (decision == Delivery.Suppress) { Finish(n, NotificationStatus.Suppressed, "Kişi SMS almayı kapatmış"); continue; }
            if (decision == Delivery.Defer) { n.DeferredUntil = until; continue; }

            var used = sentToday.GetValueOrDefault(n.TenantSlug);
            if (used >= cfg.DailyLimit)
            {
                n.DeferredUntil = NextMorningUtc(now);
                n.FailureReason = "Günlük SMS sınırı doldu; ertesi sabah gönderilecek";
                continue;
            }

            string secret;
            try { secret = protector.Decrypt(cfg.SecretEnc); }
            catch (Exception)
            {
                Finish(n, NotificationStatus.Failed, "SMS sağlayıcı anahtarı çözülemedi; ayarları yeniden kaydedin");
                continue;
            }
            var result = await SmsProviders.SendAsync(http,
                new SmsAccount(cfg.Provider, cfg.Username, secret, cfg.Sender),
                new SmsMessage(n.RecipientPhone, SmsFormat.Compose(n.Subject, n.Body, n.ActionUrl)), ct);
            n.AttemptCount++;
            if (result.Ok)
            {
                Finish(n, NotificationStatus.Sent, null);
                sentToday[n.TenantSlug] = used + 1;
                _logger.LogInformation("SMS gonderildi: {NotificationId} ({Provider}, is {Reference})", n.Id, cfg.Provider, result.Reference);
            }
            else if (result.Permanent || n.AttemptCount >= MaxAttempts)
            {
                Finish(n, NotificationStatus.Failed, result.Error);
                _logger.LogWarning("SMS gonderilemedi: {NotificationId} ({Provider}): {Error}", n.Id, cfg.Provider, result.Error);
            }
            else
            {
                n.FailureReason = result.Error;
                n.DeferredUntil = now.AddSeconds(30 * n.AttemptCount);
            }
        }
        await db.SaveChangesAsync(ct);
    }
}

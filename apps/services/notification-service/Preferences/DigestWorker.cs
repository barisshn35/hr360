using Microsoft.EntityFrameworkCore;
using NotificationService.Data;
using NotificationService.Models;
using NotificationService.Tenancy;

namespace NotificationService.Preferences;

/// <summary>
/// Günlük özet: özete alınmış (DigestQueued) e-postaları kişinin seçtiği saatte (Europe/Istanbul)
/// tek bir e-postada toplar. Özette yalnızca konu satırları vardır (KVKK: gövde metinleri yok).
/// Özet e-postası normal e-posta kuyruğuna (digest.daily) düşer; sessiz saatlere uyar.
/// Aralık DIGEST_CHECK_SECONDS (eski adı DIGEST_POLL_SECONDS; varsayılan 300 sn).
/// </summary>
public class DigestWorker : BackgroundService
{
    static readonly TimeSpan Poll = TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("DIGEST_CHECK_SECONDS") ?? Environment.GetEnvironmentVariable("DIGEST_POLL_SECONDS"), out var s)
            ? Math.Max(1, s) : 300);

    readonly IServiceProvider _sp;
    readonly ILogger<DigestWorker> _log;

    public DigestWorker(IServiceProvider sp, ILogger<DigestWorker> log)
    {
        _sp = sp;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await RoundAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "Günlük özet turu başarısız"); }
            await Task.Delay(Poll, ct);
        }
    }

    public async Task<int> RoundAsync(CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().IsPlatformAdmin = true;
        var db = scope.ServiceProvider.GetRequiredService<NotificationDbContext>();
        var now = DateTimeOffset.UtcNow;

        var subscribers = await db.Preferences.Where(p => p.DigestEnabled).ToListAsync(ct);
        var created = 0;
        foreach (var p in subscribers)
        {
            if (!QuietHoursCalc.DigestDue(now, p.DigestHour, p.DigestLastSentAt)) continue;
            p.DigestLastSentAt = now;
            var items = await db.Notifications
                .Where(n => n.TenantSlug == p.TenantSlug && n.RecipientEmployeeId == p.EmployeeId
                            && n.Channel == NotificationChannel.Email && n.Status == NotificationStatus.DigestQueued)
                .OrderBy(n => n.CreatedAt).Take(500).ToListAsync(ct);
            if (items.Count == 0) continue;

            var email = items.Select(i => i.RecipientEmail).LastOrDefault(e => !string.IsNullOrWhiteSpace(e));
            var lang = p.Language == "en" ? "en" : "tr";
            var (subject, body) = DigestBuilder.Build(items.Select(i => new DigestItem(i.TemplateCode, i.Subject, i.CreatedAt)).ToList(), lang);
            db.Notifications.Add(new Notification
            {
                TenantSlug = p.TenantSlug,
                RecipientEmployeeId = p.EmployeeId,
                RecipientEmail = email,
                Channel = NotificationChannel.Email,
                TemplateCode = NotificationCategories.DigestCode,
                Subject = subject,
                Body = body,
                Language = lang,
            });
            foreach (var i in items)
            {
                i.Status = NotificationStatus.Digested;
                i.SentAt = now;
            }
            created++;
        }
        if (subscribers.Count > 0) await db.SaveChangesAsync(ct);
        if (created > 0) _log.LogInformation("{Count} günlük özet e-postası kuyruğa alındı", created);
        return created;
    }
}

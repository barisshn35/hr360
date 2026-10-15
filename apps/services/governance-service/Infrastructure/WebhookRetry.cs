using System.Text.Json;
using GovernanceService.Data;
using GovernanceService.Models;
using GovernanceService.Tenancy;
using GovernanceService.Infrastructure.Chat;
using Microsoft.EntityFrameworkCore;

namespace GovernanceService.Infrastructure;

/// <summary>
/// Dalga 12 (madde 93): webhook teslimatının otomatik yeniden denemesi.
/// Geçici hatalar (ağ hatası, zaman aşımı, 408/425/429, 5xx) üstel geri çekilmeyle
/// yeniden denenir: taban × {1, 5, 30, 120, 720} (varsayılan taban 60 sn →
/// 1 dk, 5 dk, 30 dk, 2 sa, 12 sa). İlk gönderim dahil en çok 6 deneme.
/// Kalıcı hatalar (diğer 4xx, KVKK aktarım kilidi) yeniden denenmez; İK elle
/// yeniden gönderebilir. Alıcı aynı olayı X-HR360-Delivery (olay kimliği) ile
/// tekilleştirebilir; X-HR360-Attempt deneme sırasını taşır.
/// </summary>
public static class WebhookRetry
{
    public static readonly int BaseSeconds = int.TryParse(EnvVar.Or("WEBHOOK_RETRY_BASE_SECONDS", "60"), out var b) && b > 0 ? b : 60;
    public static readonly int[] Steps = { 1, 5, 30, 120, 720 };
    public const int MaxAttempts = 6;

    /// <summary>Hata geçici mi (yeniden denemeye değer mi)?</summary>
    public static bool IsRetryable(int? status, string? error)
    {
        if (error is null && status is >= 200 and < 300) return false;
        if (error is "transfer_basis_required" or "hook_disabled") return false;
        if (status is null) return true; // ağ hatası / zaman aşımı / DNS
        return status is 408 or 425 or 429 or >= 500;
    }

    /// <summary>
    /// <paramref name="attempt"/>. deneme başarısız olduktan sonraki deneme zamanı;
    /// hak bittiyse null.
    /// </summary>
    public static DateTime? NextRetryAt(int attempt, DateTime failedAt, int? baseSeconds = null)
    {
        if (attempt < 1 || attempt >= MaxAttempts) return null;
        var step = Steps[Math.Min(attempt - 1, Steps.Length - 1)];
        return failedAt.AddSeconds((double)(baseSeconds ?? BaseSeconds) * step);
    }

    /// <summary>Ekranda gösterilen plan (saniye): her denemeden sonra beklenen süre.</summary>
    public static int[] ScheduleSeconds(int? baseSeconds = null) => Steps.Select(s => s * (baseSeconds ?? BaseSeconds)).ToArray();

    /// <summary>Teslimat sonrasında yeniden deneme durumunu ayarlar (ping elle tetiklendiği için otomatik denenmez).</summary>
    public static void Schedule(WebhookDelivery d, Webhook hook, string type)
    {
        if (d.Error is null) { d.RetryState = null; d.NextRetryAt = null; return; }
        if (type == "ping" || d.EventId is null || !IsRetryable(d.StatusCode, d.Error)) { d.RetryState = null; d.NextRetryAt = null; return; }
        if (!hook.IsEnabled) { d.RetryState = "gave_up"; d.NextRetryAt = null; return; }
        var next = NextRetryAt(d.Attempt, d.OccurredAt);
        d.RetryState = next is null ? "gave_up" : "pending";
        d.NextRetryAt = next;
    }

    /// <summary>
    /// Bir teslimatı yeniden gönderir (otomatik deneme ya da İK'nın elle gönderimi).
    /// Yük governance_events'ten okunur (30 gün saklanır); olay silinmişse
    /// <c>event_expired</c> döner. Ping teslimatı yeni bir ping olarak gönderilir.
    /// </summary>
    public static async Task<(WebhookDelivery? Delivery, string? Error)> ResendAsync(
        GovernanceDbContext db, Sql sql, Dispatcher dispatcher, WebhookDelivery original, bool manual, string? by, CancellationToken ct)
    {
        var hook = await db.Webhooks.FirstOrDefaultAsync(w => w.Id == original.WebhookId && w.TenantSlug == original.TenantSlug, ct);
        if (hook is null) return (null, "hook_missing");
        if (!manual && !hook.IsEnabled) return (null, "hook_disabled");
        JsonElement? payload;
        if (original.EventType == "ping" || original.EventId is null)
            payload = JsonDocument.Parse(JsonSerializer.Serialize(new { TenantSlug = hook.TenantSlug, message = "HR360 webhook testi (yeniden gönderim)", at = DateTime.UtcNow })).RootElement;
        else
        {
            var raw = await sql.ScalarAsync("""
                SELECT "Payload"::text FROM governance_events WHERE "Id" = $1 AND "TenantSlug" = $2
                """, ct, original.EventId.Value, original.TenantSlug) as string;
            if (raw is null)
            {
                var exists = await sql.ScalarAsync("SELECT 1 FROM governance_events WHERE \"Id\" = $1 AND \"TenantSlug\" = $2", ct, original.EventId.Value, original.TenantSlug);
                if (exists is null) return (null, "event_expired");
                payload = null;
            }
            else payload = JsonDocument.Parse(raw).RootElement.Clone();
        }
        var d = await dispatcher.DeliverWebhookAsync(db, hook, original.EventId ?? Guid.NewGuid(), original.EventType, payload, ct,
            attempt: original.Attempt + 1, parentId: original.Id, manual: manual, by: by);
        original.NextRetryAt = null;
        original.RetryState = manual ? "resent" : "retried";
        return (d, null);
    }
}

/// <summary>
/// Sırası gelen webhook yeniden denemelerini işler (30 sn'de bir; test ortamında
/// WEBHOOK_RETRY_BASE_SECONDS küçükse daha sık). Birden çok servis kopyası
/// aynı teslimatı iki kez göndermesin diye satırlar FOR UPDATE SKIP LOCKED ile
/// "retrying" durumuna alınarak sahiplenilir; 10 dk'da bitmeyen sahiplenme geri açılır.
/// Saatte bir 180 günden eski API anahtarı kullanım sayaçlarını siler.
/// </summary>
public sealed class WebhookRetryWorker(IServiceProvider sp, ILogger<WebhookRetryWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var period = TimeSpan.FromSeconds(Math.Clamp(WebhookRetry.BaseSeconds / 2, 1, 30));
        var lastCleanup = DateTime.MinValue;
        await Task.Delay(TimeSpan.FromSeconds(10), ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = sp.CreateScope();
                scope.ServiceProvider.GetRequiredService<TenantContext>().IsPlatformAdmin = true;
                var sql = scope.ServiceProvider.GetRequiredService<Sql>();
                var ids = await sql.QueryAsync("""
                    UPDATE governance_webhook_deliveries SET "RetryState" = 'retrying', "NextRetryAt" = now()
                    WHERE "Id" IN (
                        SELECT "Id" FROM governance_webhook_deliveries
                        WHERE ("RetryState" = 'pending' AND "NextRetryAt" <= now())
                           OR ("RetryState" = 'retrying' AND "NextRetryAt" <= now() - interval '10 minutes')
                        ORDER BY "NextRetryAt" LIMIT 50 FOR UPDATE SKIP LOCKED)
                    RETURNING "Id"
                    """, r => r.GetGuid(0), ct);
                if (ids.Count > 0)
                {
                    var db = scope.ServiceProvider.GetRequiredService<GovernanceDbContext>();
                    var dispatcher = scope.ServiceProvider.GetRequiredService<Dispatcher>();
                    foreach (var id in ids)
                    {
                        var d = await db.WebhookDeliveries.FirstOrDefaultAsync(x => x.Id == id, ct);
                        if (d is null) continue;
                        var (_, err) = await WebhookRetry.ResendAsync(db, sql, dispatcher, d, false, null, ct);
                        if (err is not null) { d.RetryState = "gave_up"; d.NextRetryAt = null; }
                    }
                    await db.SaveChangesAsync(ct);
                }
                if (DateTime.UtcNow - lastCleanup > TimeSpan.FromHours(1))
                {
                    lastCleanup = DateTime.UtcNow;
                    await sql.ExecuteAsync("DELETE FROM governance_api_key_usage WHERE \"Day\" < current_date - 180", ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Webhook yeniden deneme turu tamamlanamadı");
            }
            await Task.Delay(period, ct);
        }
    }
}

/// <summary>
/// Dalga 12 (madde 94): API anahtarı yetkileri (scope). "read-only" tüm
/// ":read" yetkilerini kapsayan üst yetkidir; yazma (hooks:write) içermez.
/// Tekil yazımlar (leave:read, employee:read…) çoğul karşılığına çevrilir.
/// </summary>
public static class ApiScopes
{
    public const string ReadOnly = "read-only";
    public static readonly string[] Concrete = { "employees:read", "departments:read", "leaves:read", "events:read", "hooks:write" };
    public static readonly string[] All = new[] { ReadOnly }.Concat(Concrete).ToArray();

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["leave:read"] = "leaves:read", ["employee:read"] = "employees:read", ["department:read"] = "departments:read",
        ["event:read"] = "events:read", ["hook:write"] = "hooks:write", ["readonly"] = ReadOnly, ["read:*"] = ReadOnly,
    };

    /// <summary>Gelen yetki listesini bilinen, tekil, kanonik yetkilere indirger (bilinmeyenler atılır).</summary>
    public static List<string> Normalize(IEnumerable<string>? scopes) =>
        (scopes ?? Array.Empty<string>())
            .Select(s => s?.Trim().ToLowerInvariant() ?? "")
            .Select(s => Aliases.TryGetValue(s, out var c) ? c : s)
            .Where(s => All.Contains(s)).Distinct().OrderBy(s => Array.IndexOf(All, s)).ToList();

    /// <summary>Anahtarın yetkileri istenen yetkiyi karşılıyor mu?</summary>
    public static bool Allows(IEnumerable<string> granted, string required)
    {
        var g = Normalize(granted);
        if (g.Contains(required)) return true;
        return g.Contains(ReadOnly) && required.EndsWith(":read", StringComparison.Ordinal);
    }

    /// <summary>Anahtar kullanılabilir mi? (iptal / süre dolumu)</summary>
    public static string? Inactive(DateTime? revokedAt, DateTime? expiresAt, DateTime now) =>
        revokedAt is not null ? "revoked" : expiresAt is { } e && e <= now ? "expired" : null;

    /// <summary>Son kullanma seçeneği (gün) → tarih; 0/null süresiz. En çok 2 yıl.</summary>
    public static DateTime? ExpiryFromDays(int? days, DateTime now) =>
        days is null or <= 0 ? null : now.AddDays(Math.Min(days.Value, 730));
}

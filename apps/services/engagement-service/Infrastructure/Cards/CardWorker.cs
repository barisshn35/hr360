using Npgsql;

namespace EngagementService.Infrastructure.Cards;

/// <summary>
/// Grup kartı teslimi (yenilik haritası #26): teslim zamanı gelen kartları teslim edilmiş işaretler ve
/// alıcıya haber verir. İmzasız kart sessizce kapanır (boş kart gönderilmez). Aralık CARDS_TICK_SECONDS
/// (varsayılan 300).
/// </summary>
public sealed class CardWorker : BackgroundService
{
    private readonly Sql _sql;
    private readonly Notifier _notify;
    private readonly ILogger<CardWorker> _log;
    private readonly TimeSpan _interval;

    public CardWorker(Sql sql, Notifier notify, ILogger<CardWorker> log)
    {
        _sql = sql; _notify = notify; _log = log;
        _interval = TimeSpan.FromSeconds(int.TryParse(Environment.GetEnvironmentVariable("CARDS_TICK_SECONDS"), out var v) && v > 0 ? v : 300);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        HR360.Platform.LeaderGate.RunAsync("engagement/CardWorker", stoppingToken, LeaderLoopAsync);

    private async Task LeaderLoopAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(45), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { await RunOnceAsync(ct); }
            catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703") { _log.LogInformation("Kart teslim turu atlandı: şema hazır değil"); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "Kart teslim turu başarısız"); }
            try { await Task.Delay(_interval, ct); } catch (OperationCanceledException) { return; }
        }
    }

    public async Task<int> RunOnceAsync(CancellationToken ct, string? tenant = null)
    {
        var due = await _sql.QueryAsync("""
            UPDATE engagement_cards c SET "DeliveredAt" = now()
            WHERE c."DeliveredAt" IS NULL AND c."DeliverAt" <= now() AND ($1::text IS NULL OR c."TenantSlug" = $1)
            RETURNING c."TenantSlug", c."Id", c."RecipientEmployeeId", c."Title",
                (SELECT count(*)::int FROM engagement_card_signatures s WHERE s."CardId" = c."Id")
            """, r => (Tenant: r.GetString(0), Id: r.GetGuid(1), To: r.GetGuid(2), Title: r.GetString(3), Signers: r.GetInt32(4)), ct, tenant);
        foreach (var c in due.Where(c => c.Signers > 0))
            await NotifyDeliveredAsync(_notify, c.Tenant, c.To, c.Title, c.Id, c.Signers, ct);
        if (due.Count > 0) _log.LogInformation("Grup kartı: {Count} kart teslim edildi", due.Count);
        return due.Count;
    }

    public static Task NotifyDeliveredAsync(Notifier notify, string tenant, Guid recipient, string title, Guid cardId, int signers, CancellationToken ct) =>
        notify.LocalizedAsync(tenant, recipient,
            $"Size bir kart var: {title}", $"You have a card: {title}",
            $"{signers} çalışma arkadaşınız sizin için bir kart imzaladı.", $"{signers} colleagues signed a card for you.",
            "cards.delivered", ct, "InApp",
            (Environment.GetEnvironmentVariable("PUBLIC_ORIGIN") ?? "http://localhost").TrimEnd('/') + $"/panel/kartlar?kart={cardId}", "Kartı aç", "Open card");
}

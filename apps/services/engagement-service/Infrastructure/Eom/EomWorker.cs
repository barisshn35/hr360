using Npgsql;

namespace EngagementService.Infrastructure.Eom;

/// <summary>
/// "Ayın çalışanı" zamanlayıcısı: ayarı açık kiracılarda dönemi kurar, aşamaları zamanında açıp kapatır
/// ve bildirimleri gönderir. Aralık EOM_TICK_SECONDS (varsayılan 300 sn); geçişler en geç bir tur gecikir.
/// </summary>
public sealed class EomWorker : BackgroundService
{
    private readonly EomStore _store;
    private readonly ILogger<EomWorker> _log;
    private readonly TimeSpan _interval;

    public EomWorker(EomStore store, ILogger<EomWorker> log)
    {
        _store = store;
        _log = log;
        var s = int.TryParse(Environment.GetEnvironmentVariable("EOM_TICK_SECONDS"), out var v) && v > 0 ? v : 300;
        _interval = TimeSpan.FromSeconds(s);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        HR360.Platform.LeaderGate.RunAsync("engagement/EomWorker", stoppingToken, LeaderLoopAsync);

    private async Task LeaderLoopAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
        catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var tenant in await _store.TenantsAsync(stoppingToken))
                {
                    try { await _store.TickAsync(tenant, DateTime.UtcNow, stoppingToken); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "Ayın çalışanı turu başarısız: {Tenant}", tenant); }
                }
            }
            catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
            {
                _log.LogInformation("Ayın çalışanı turu atlandı: şema henüz hazır değil ({State})", ex.SqlState);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "Ayın çalışanı turu başarısız"); }
            try { await Task.Delay(_interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}

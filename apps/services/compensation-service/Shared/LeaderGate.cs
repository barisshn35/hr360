// OTOMATIK KOPYA: kaynak apps/shared-dotnet (scripts/sync-shared.sh). Bu dosyayi duzenlemeyin.
using Microsoft.Extensions.Logging;
using Npgsql;

namespace HR360.Platform;

/// <summary>
/// Zamanlanmış arka plan işinin servis birden çok kopyayla çalışırken yalnızca BİR kopyada yürümesi.
/// Her iş için PostgreSQL oturum danışma kilidi (pg_try_advisory_lock) alınır; süreç başına tek, havuz dışı
/// bağlantı bütün kilitleri tutar. Kilidi alamayan kopya bekler ve düzenli olarak yeniden dener; kilit sahibi
/// düşerse (süreç ya da bağlantı) kilit kendiliğinden bırakılır ve bekleyen kopya devralır.
///
/// Kullanım (Program.cs bir kez): <c>LeaderGate.Configure(connectionString)</c>
/// İşte: <c>protected override Task ExecuteAsync(CancellationToken s) => LeaderGate.RunAsync("servis/İş", s, LoopAsync);</c>
/// Yapılandırılmamışsa (birim testi) iş doğrudan çalışır.
/// </summary>
public static class LeaderGate
{
    public static TimeSpan RetryInterval { get; set; } = TimeSpan.FromSeconds(30);
    public static TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(15);

    private static string? _connectionString;
    private static ILogger? _log;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HashSet<string> Held = new(StringComparer.Ordinal);
    private static NpgsqlConnection? _conn;
    private static CancellationTokenSource _lost = new();
    private static int _keepAliveStarted;

    public static void Configure(string? connectionString, ILogger? log = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        var b = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false, // kilit bu bağlantıya bağlı; havuza dönüp sıfırlanmamalı
            ApplicationName = "hr360-leader",
            KeepAlive = 30,
        };
        _connectionString = b.ConnectionString;
        _log = log;
    }

    /// <summary>İşi kilit altında çalıştırır; kilit kaybedilirse işi durdurur, yeniden alınca sürdürür.</summary>
    public static async Task RunAsync(string job, CancellationToken stopping, Func<CancellationToken, Task> body)
    {
        if (_connectionString is null) { await body(stopping); return; }
        while (!stopping.IsCancellationRequested)
        {
            CancellationToken lost;
            try { lost = await WaitAsync(job, stopping); }
            catch (OperationCanceledException) { return; }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopping, lost);
            try
            {
                await body(linked.Token);
                if (!lost.IsCancellationRequested) return; // iş kendiliğinden bitti (tek seferlik iş)
            }
            catch (OperationCanceledException) when (!stopping.IsCancellationRequested) { }
            _log?.LogWarning("Lider kilidi kaybedildi, yeniden bekleniyor: {Job}", job);
        }
    }

    private static async Task<CancellationToken> WaitAsync(string job, CancellationToken stopping)
    {
        var waitingLogged = false;
        while (true)
        {
            stopping.ThrowIfCancellationRequested();
            try
            {
                if (await TryAcquireAsync(job, stopping) is { } token) return token;
                if (!waitingLogged) { _log?.LogInformation("Başka bir kopya çalıştırıyor, bekleniyor: {Job}", job); waitingLogged = true; }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log?.LogWarning("Lider kilidi alınamadı ({Job}): {Error}", job, ex.Message);
                await ResetAsync();
            }
            await Task.Delay(RetryInterval, stopping);
        }
    }

    private static async Task<CancellationToken?> TryAcquireAsync(string job, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            if (_conn is null)
            {
                var c = new NpgsqlConnection(_connectionString);
                await c.OpenAsync(ct);
                _conn = c;
                if (Interlocked.Exchange(ref _keepAliveStarted, 1) == 0) _ = Task.Run(KeepAliveLoopAsync);
            }
            if (Held.Contains(job)) return _lost.Token;
            await using var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtext(@k))", _conn);
            cmd.Parameters.AddWithValue("k", "hr360-leader:" + job);
            if (await cmd.ExecuteScalarAsync(ct) is true) { Held.Add(job); return _lost.Token; }
            return null;
        }
        finally { Gate.Release(); }
    }

    /// <summary>Bağlantı ölürse kilitler de gitmiştir: elindeki işleri durdur, yeniden yarışsınlar.</summary>
    private static async Task KeepAliveLoopAsync()
    {
        while (true)
        {
            await Task.Delay(KeepAliveInterval);
            var dead = false;
            await Gate.WaitAsync();
            try
            {
                if (_conn is null) continue;
                await using var cmd = new NpgsqlCommand("SELECT 1", _conn);
                cmd.CommandTimeout = 10;
                await cmd.ExecuteScalarAsync();
            }
            catch (Exception) { dead = true; }
            finally { Gate.Release(); }
            if (dead) await ResetAsync();
        }
    }

    private static async Task ResetAsync()
    {
        await Gate.WaitAsync();
        try
        {
            var old = _lost;
            _lost = new CancellationTokenSource();
            Held.Clear();
            if (_conn is not null) { try { await _conn.DisposeAsync(); } catch { /* zaten kopuk */ } _conn = null; }
            old.Cancel(); // elden çıkarılmaz: o anda bağlı jeton kuran iş ObjectDisposedException almasın
        }
        finally { Gate.Release(); }
    }
}

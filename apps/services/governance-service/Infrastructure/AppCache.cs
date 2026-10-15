using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Prometheus;
using StackExchange.Redis;

namespace GovernanceService.Infrastructure;

/// <summary>
/// Redis üzerinde kısa ömürlü paylaşılan önbellek (REDIS_URL). Tanımlı değilse ya da
/// Redis'e ulaşılamıyorsa her çağrı doğrudan veritabanına gider: önbellek bir
/// hızlandırıcıdır, doğruluk ona bağlı değildir.
///
/// Anahtarlar her zaman kiracıyı içerir. Kullanıcıya göre değişen bir sonuç
/// önbelleğe alınacaksa anahtara kullanıcı da eklenmelidir; yetki denetimi her
/// zaman önbellekten ÖNCE yapılır.
/// </summary>
public sealed class AppCache : IDisposable
{
    private static readonly Counter Requests = Metrics.CreateCounter(
        "hr360_cache_requests_total", "Önbellek istekleri (hit, miss, error, bypass).", "cache", "result");

    private readonly ILogger<AppCache> _log;
    private readonly string? _config;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private ConnectionMultiplexer? _mux;
    private DateTime _retryAfter = DateTime.MinValue;

    public AppCache(IConfiguration cfg, ILogger<AppCache> log)
    {
        _log = log;
        var url = cfg["REDIS_URL"];
        if (!string.IsNullOrWhiteSpace(url))
        {
            // Kısa zaman aşımları: Redis yavaşlarsa istek beklemez, doğrudan veritabanına gider.
            _config = url + (url.Contains("abortConnect", StringComparison.OrdinalIgnoreCase) ? "" : ",abortConnect=false")
                + ",connectTimeout=2000,syncTimeout=500,asyncTimeout=500,connectRetry=1";
        }
    }

    public bool Enabled => _config is not null;

    private async Task<IDatabase?> DbAsync()
    {
        if (_config is null || DateTime.UtcNow < _retryAfter) return null;
        if (_mux is { IsConnected: true }) return _mux.GetDatabase();
        await _connectLock.WaitAsync();
        try
        {
            if (_mux is null)
            {
                _mux = await ConnectionMultiplexer.ConnectAsync(_config);
                _log.LogInformation("Redis önbelleği bağlı");
            }
            if (!_mux.IsConnected) { Trip(null); return null; }
            return _mux.GetDatabase();
        }
        catch (Exception ex)
        {
            Trip(ex);
            return null;
        }
        finally { _connectLock.Release(); }
    }

    /// <summary>Hata sonrası 30 sn Redis'i atla; her istekte zaman aşımı beklenmesin.</summary>
    private void Trip(Exception? ex)
    {
        if (DateTime.UtcNow >= _retryAfter)
            _log.LogWarning(ex, "Redis önbelleğine ulaşılamıyor; 30 sn veritabanından okunacak");
        _retryAfter = DateTime.UtcNow.AddSeconds(30);
    }

    private static string Key(string name, string tenant, string rest) => $"hr360:{name}:{tenant}:{rest}";

    /// <summary>Önbellekte varsa onu, yoksa üretip yazar (JSON).</summary>
    public async Task<T> GetOrSetAsync<T>(string name, string tenant, string rest, TimeSpan ttl,
        Func<CancellationToken, Task<T>> factory, CancellationToken ct, JsonSerializerOptions? json = null)
    {
        var raw = await GetOrSetRawAsync(name, tenant, rest, ttl, async c => JsonSerializer.Serialize(await factory(c), json), ct);
        return JsonSerializer.Deserialize<T>(raw, json)!;
    }

    /// <summary>
    /// Bir uç noktanın JSON yanıtını önbellekler. Serileştirme MVC'nin ayarlarıyla
    /// yapılır; önbellekten dönen gövde normal yanıtla bayt bayt aynıdır.
    /// </summary>
    public async Task<IActionResult> JsonAsync(HttpContext http, string name, string tenant, string rest, TimeSpan ttl,
        Func<CancellationToken, Task<object>> factory, CancellationToken ct)
    {
        var opts = http.RequestServices.GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>().Value.JsonSerializerOptions;
        var raw = await GetOrSetRawAsync(name, tenant, rest, ttl, async c => JsonSerializer.Serialize(await factory(c), opts), ct);
        return new ContentResult { Content = raw, ContentType = "application/json; charset=utf-8", StatusCode = 200 };
    }

    private async Task<string> GetOrSetRawAsync(string name, string tenant, string rest, TimeSpan ttl,
        Func<CancellationToken, Task<string>> factory, CancellationToken ct)
    {
        var db = await DbAsync();
        if (db is null)
        {
            Requests.WithLabels(name, "bypass").Inc();
            return await factory(ct);
        }
        var key = Key(name, tenant, rest);
        try
        {
            var hit = await db.StringGetAsync(key);
            if (hit.HasValue)
            {
                Requests.WithLabels(name, "hit").Inc();
                return hit.ToString();
            }
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            Trip(ex);
            Requests.WithLabels(name, "error").Inc();
            return await factory(ct);
        }

        Requests.WithLabels(name, "miss").Inc();
        var value = await factory(ct);
        try { await db.StringSetAsync(key, value, ttl); }
        catch (Exception ex) when (ex is RedisException or TimeoutException) { Trip(ex); }
        return value;
    }

    /// <summary>Bir kiracının belirli önbelleğinin sürümü; yazma işlemleri <see cref="BumpAsync"/> ile eskitir.</summary>
    public async Task<string> VersionAsync(string name, string tenant)
    {
        var db = await DbAsync();
        if (db is null) return "0";
        try { return (await db.StringGetAsync(Key(name, tenant, "v"))).ToString() is { Length: > 0 } v ? v : "0"; }
        catch (Exception ex) when (ex is RedisException or TimeoutException) { Trip(ex); return "0"; }
    }

    public async Task BumpAsync(string name, string tenant)
    {
        var db = await DbAsync();
        if (db is null) return;
        try
        {
            var key = Key(name, tenant, "v");
            await db.StringIncrementAsync(key);
            await db.KeyExpireAsync(key, TimeSpan.FromDays(1));
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException) { Trip(ex); }
    }

    /// <summary>
    /// Sabit pencereli sayaç (hız sınırı için). Tüm servis kopyaları aynı sayacı paylaşır.
    /// Redis yoksa null döner; çağıran kendi bellek içi sayacına düşer.
    /// </summary>
    public async Task<long?> CountInWindowAsync(string name, string id, TimeSpan window)
    {
        var db = await DbAsync();
        if (db is null) return null;
        try
        {
            var slot = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / (long)window.TotalSeconds;
            var key = $"hr360:{name}:{id}:{slot}";
            var n = await db.StringIncrementAsync(key);
            if (n == 1) await db.KeyExpireAsync(key, window + TimeSpan.FromSeconds(5));
            return n;
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException) { Trip(ex); return null; }
    }

    public void Dispose() => _mux?.Dispose();
}

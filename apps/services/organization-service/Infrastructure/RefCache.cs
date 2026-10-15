using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using OrganizationService.Tenancy;
using Prometheus;

namespace OrganizationService.Infrastructure;

/// <summary>
/// Kısa ömürlü yanıt önbelleği (G24) - YALNIZCA kişisel veri içermeyen referans verisi için
/// (departman listesi). Bu serviste Redis bağlantısı yok; süreç içi bellek kullanılır.
///
/// - Anahtar her zaman kiracıyı içerir (platform yöneticisi ayrı anahtar: filtresiz sonuç).
/// - Yazma işlemleri kiracının sürümünü artırır: aynı süreçte eski liste hiç dönmez;
///   birden fazla kopya çalışıyorsa diğer kopyalar en fazla <see cref="Ttl"/> kadar eski görür.
/// - Maaş, sağlık ya da kişisel veri içeren yanıtlar BURAYA KONMAZ.
/// </summary>
public sealed class RefCache
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    private static readonly Counter Requests = Metrics.CreateCounter(
        "hr360_cache_requests_total", "Önbellek istekleri (hit, miss, bypass).", "cache", "result");

    private readonly IMemoryCache _cache;
    private readonly ConcurrentDictionary<string, long> _versions = new();

    public RefCache(IMemoryCache cache) => _cache = cache;

    /// <summary>Önbellek anahtarındaki kiracı; çözülemiyorsa null (önbellek atlanır).</summary>
    public static string? Scope(ITenantContext tenant) =>
        tenant.IsPlatformAdmin ? "~platform" : string.IsNullOrWhiteSpace(tenant.TenantSlug) ? null : tenant.TenantSlug;

    public async Task<T> GetOrSetAsync<T>(string name, string? scope, string rest, Func<Task<T>> factory)
    {
        if (scope is null)
        {
            Requests.WithLabels(name, "bypass").Inc();
            return await factory();
        }
        var key = $"{name}:{scope}:{_versions.GetValueOrDefault(Ver(name, scope))}:{rest}";
        if (_cache.TryGetValue(key, out T? hit) && hit is not null)
        {
            Requests.WithLabels(name, "hit").Inc();
            return hit;
        }
        Requests.WithLabels(name, "miss").Inc();
        var value = await factory();
        _cache.Set(key, value, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Ttl, Size = 1 });
        return value;
    }

    /// <summary>Kiracının (ve platform görünümünün) önbelleğini eskitir.</summary>
    public void Bump(string name, string? tenantSlug)
    {
        if (!string.IsNullOrWhiteSpace(tenantSlug)) _versions.AddOrUpdate(Ver(name, tenantSlug), 1, (_, v) => v + 1);
        _versions.AddOrUpdate(Ver(name, "~platform"), 1, (_, v) => v + 1);
    }

    private static string Ver(string name, string scope) => name + ":" + scope;
}

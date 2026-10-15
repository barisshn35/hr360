using System.Collections.Concurrent;
using System.Net;
using Npgsql;

namespace ExpenseService.Tenancy;

/// <summary>
/// GUVENLIK (CTO denetimi): Askiya alinan bir kiracinin kullanicilari Keycloak'ta
/// kapatiliyor ve oturumlari sonlandiriliyor; ancak ellerindeki erisim jetonu omru
/// dolana kadar (varsayilan 5 dk) API'lerde gecerli kaliyordu. Bu kapi her kimligi
/// dogrulanmis istekte kiracinin durumunu (platform_tenants, 30 sn onbellek) kontrol
/// eder; askidaysa 403 doner. Platform yoneticisi etkilenmez.
///
/// G22: Kiraci bir IP izin listesi tanimladiysa (platform_tenants.IpAllowlist, CIDR
/// listesi) gateway'den gelen (X-Real-IP tasiyan) istekler yalnizca bu adreslerden
/// kabul edilir. Servisler arasi ic cagrilar (gateway'den gecmeyen) etkilenmez.
///
/// Guvenlik dalgasi 2A: platform yoneticisinin kiraci verisi istekleri sureli erisim izni
/// ister (PlatformAccessGate).
///
/// Baglanti: TENANT_STATUS_DB_CONNECTION (tanimsizsa kapi devre disi). Veritabani
/// gecici olarak okunamazsa istek gecirilir (kesinti yerine kisa sureli izin) ve
/// uyari yazilir.
/// </summary>
public class TenantStatusGate
{
    private sealed record Entry(bool Suspended, IReadOnlyList<IPNetwork> Allow, DateTime At);
    private static readonly ConcurrentDictionary<string, Entry> Cache = new();
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
    private static readonly string? ConnectionString =
        Environment.GetEnvironmentVariable("TENANT_STATUS_DB_CONNECTION");

    private readonly RequestDelegate _next;
    private readonly ILogger<TenantStatusGate> _logger;

    public TenantStatusGate(RequestDelegate next, ILogger<TenantStatusGate> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, TenantContext tenant)
    {
        // Guvenlik dalgasi 2A: platform yoneticisi kiraci verisine yalnizca sureli erisim
        // izniyle ulasir (bkz. PlatformAccessGate). Izinle sinirlanan istekte kiracinin durum/IP
        // kurali platform yoneticisine uygulanmaz (eskisi gibi).
        var viaGrant = false;
        if (!string.IsNullOrEmpty(ConnectionString)
            && context.User?.Identity?.IsAuthenticated == true
            && tenant.IsPlatformAdmin
            && PlatformAccessGate.Enforced
            && !PlatformAccessGate.IsExempt(context.Request.Path.Value))
        {
            if (!await PlatformAccessGate.AuthorizeAsync(context, tenant, ConnectionString!, _logger)) return;
            viaGrant = true;
        }

        if (!viaGrant
            && !string.IsNullOrEmpty(ConnectionString)
            && context.User?.Identity?.IsAuthenticated == true
            && !tenant.IsPlatformAdmin
            && !string.IsNullOrWhiteSpace(tenant.TenantSlug))
        {
            var e = await ReadAsync(tenant.TenantSlug!, context.RequestAborted);
            if (e.Suspended)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    message = "Şirket hesabı askıya alınmış. Lütfen yöneticinizle iletişime geçin.",
                    code = "tenant_suspended",
                });
                return;
            }
            var realIp = context.Request.Headers["X-Real-IP"].FirstOrDefault();
            if (e.Allow.Count > 0 && realIp is not null && !Allowed(e.Allow, realIp))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    message = "Bu ağdan erişim şirketiniz tarafından kısıtlanmış.",
                    code = "ip_not_allowed",
                });
                return;
            }
        }
        await _next(context);
    }

    public static bool Allowed(IReadOnlyList<IPNetwork> allow, string ip)
    {
        if (!IPAddress.TryParse(ip.Trim(), out var addr)) return false;
        if (addr.IsIPv4MappedToIPv6) addr = addr.MapToIPv4();
        return allow.Any(n => n.Contains(addr));
    }

    public static List<IPNetwork> ParseList(string? raw)
    {
        var list = new List<IPNetwork>();
        foreach (var part in (raw ?? "").Split(new[] { ',', '\n', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var s = part.Contains('/') ? part : part + (part.Contains(':') ? "/128" : "/32");
            if (IPNetwork.TryParse(s, out var n)) list.Add(n);
        }
        return list;
    }

    private async Task<Entry> ReadAsync(string slug, CancellationToken ct)
    {
        if (Cache.TryGetValue(slug, out var hit) && DateTime.UtcNow - hit.At < Ttl)
            return hit;
        try
        {
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "SELECT \"Status\", \"IpAllowlist\" FROM platform_tenants WHERE \"Slug\" = @s LIMIT 1", conn);
            cmd.Parameters.AddWithValue("s", slug);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            string? status = null, allow = null;
            if (await r.ReadAsync(ct))
            {
                status = r.IsDBNull(0) ? null : r.GetString(0);
                allow = r.IsDBNull(1) ? null : r.GetString(1);
            }
            var e = new Entry(string.Equals(status, "Suspended", StringComparison.OrdinalIgnoreCase), ParseList(allow), DateTime.UtcNow);
            Cache[slug] = e;
            return e;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Kiraci durumu okunamadi ({Slug}); istek geciriliyor", slug);
            return hit ?? new Entry(false, Array.Empty<IPNetwork>(), default);
        }
    }
}

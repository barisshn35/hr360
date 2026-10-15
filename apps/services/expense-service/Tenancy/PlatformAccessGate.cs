using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace ExpenseService.Tenancy;

/// <summary>
/// Guvenlik dalgasi 2A (break-glass): platform yoneticisi kiraci verisine yalnizca sureli,
/// gerekceli bir erisim izniyle (platform_access_grants; tenant-service
/// POST /api/platform-access/grants) ulasir. TenantStatusGate her platform yoneticisi isteginde
/// bunu cagirir:
///
///   * Platform seviyesindeki yollar (saglik, metrik, ic uclar) izinsiz gecer;
///     davranis eskisi gibidir (kiraci filtresi yok).
///   * Hedef kiraci X-HR360-Tenant basligindan (arayuz platform yoneticisinin sectigi kiraciyi
///     gonderir) ya da jetondaki organization claim'inden alinir. Etkin izin yoksa 403
///     (code = platform_access_grant_required).
///   * Izin varsa istek o kiraciyla SINIRLANIR (TenantContext.TenantSlug = kiraci,
///     IsPlatformAdmin = false: kiraci filtresi uygulanir) ve erisim audit_log'a yazilir
///     (izin + yontem + kimliksiz yol; ayni yol icin 10 dk'da bir satir).
///   * Izin tablosu okunamazsa platform yoneticisi kiraci verisine ulasamaz (kapali hata, 503).
///
/// PLATFORM_ACCESS_GRANTS=off ile kapatilir (eski davranis: platform yoneticisi tum kiracilari
/// gorur; PlatformAccessAudit kaydi surer). Bu dosya tum servislerde birebir aynidir
/// (yalnizca namespace farkli).
/// </summary>
public static class PlatformAccessGate
{
    public const string Header = "X-HR360-Tenant";

    /// <summary>Izinsiz gecen platform seviyesi yollar (servis ici yol; gateway /api/&lt;servis&gt; onekini atar).</summary>
    private static readonly string[] ExemptPrefixes =
    {
        "/health", "/metrics", "/swagger", "/api/internal/",
    };

    private static readonly TimeSpan PositiveTtl = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan AuditEvery = TimeSpan.FromMinutes(10);
    private static readonly ConcurrentDictionary<string, (Guid? Grant, DateTime At)> Cache = new();
    private static readonly ConcurrentDictionary<string, DateTime> Audited = new();
    private static readonly Regex SlugRe = new("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.Compiled);
    private static readonly Regex IdSegment = new(
        "^([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}|[0-9]+|.*@.*|[A-Za-z0-9_-]{24,})$", RegexOptions.Compiled);

    public static bool Enforced =>
        !string.Equals(Environment.GetEnvironmentVariable("PLATFORM_ACCESS_GRANTS")?.Trim(), "off", StringComparison.OrdinalIgnoreCase);

    public static bool IsExempt(string? path)
    {
        var p = path ?? "";
        return ExemptPrefixes.Any(x => p.Equals(x.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(x.EndsWith('/') ? x : x + "/", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Hedef kiraci: once baslik, yoksa jetondaki organization; gecersiz biciminde null.</summary>
    public static string? TargetTenant(string? header, string? claimSlug)
    {
        var s = (string.IsNullOrWhiteSpace(header) ? claimSlug : header)?.Trim().ToLowerInvariant();
        return s is not null && SlugRe.IsMatch(s) ? s : null;
    }

    /// <summary>Denetim icin yol: kimlik, sayi, e-posta ve uzun belirtecler {id} olur (KVKK: veri en aza).</summary>
    public static string NormalizePath(string? path)
    {
        var parts = (path ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(seg => IdSegment.IsMatch(seg) ? "{id}" : seg);
        var p = "/" + string.Join('/', parts);
        return p.Length > 200 ? p[..200] : p;
    }

    /// <summary>Ayni izin + yontem + yol icin denetim satiri yazilmali mi (10 dk'da bir)?</summary>
    public static bool ShouldAudit(string key, DateTime now)
    {
        if (Audited.TryGetValue(key, out var last) && now - last < AuditEvery) return false;
        Audited[key] = now;
        if (Audited.Count > 5000)
            foreach (var k in Audited.Where(kv => now - kv.Value > AuditEvery).Select(kv => kv.Key).ToList()) Audited.TryRemove(k, out _);
        return true;
    }

    private static readonly string ServiceName = Kebab(Assembly.GetEntryAssembly()?.GetName().Name ?? "service");

    private static string Kebab(string s) => Regex.Replace(s, "(?<!^)([A-Z])", "-$1").ToLowerInvariant();

    /// <summary>
    /// Platform yoneticisi istegini denetler. false donerse yanit yazilmistir (403/503) ve
    /// istek durdurulmalidir. true: izinli; tenant kiraciya sinirlanmistir.
    /// </summary>
    public static async Task<bool> AuthorizeAsync(HttpContext context, TenantContext tenant, string connectionString, ILogger logger)
    {
        var sub = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? context.User.FindFirst("sub")?.Value;
        var target = TargetTenant(context.Request.Headers[Header].FirstOrDefault(), tenant.TenantSlug);
        if (sub is null || target is null)
        {
            await DenyAsync(context, 403, "platform_access_grant_required",
                "Kiracı verisine erişmek için önce şirketi seçip süreli erişim izni açın.");
            return false;
        }
        Guid? grant;
        try
        {
            grant = await FindGrantAsync(connectionString, sub, target, context.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Platform erişim izni okunamadı ({Tenant}): {Message}", target, ex.Message);
            await DenyAsync(context, 503, "platform_access_grant_unavailable",
                "Erişim izni doğrulanamadı; lütfen biraz sonra tekrar deneyin.");
            return false;
        }
        if (grant is null)
        {
            await DenyAsync(context, 403, "platform_access_grant_required",
                "Bu şirketin verisine erişim izniniz yok. Kiracılar ekranından gerekçeli, süreli erişim izni açın.");
            return false;
        }

        tenant.TenantSlug = target;
        tenant.IsPlatformAdmin = false;

        var method = context.Request.Method;
        var path = NormalizePath(context.Request.Path.Value);
        if (ShouldAudit($"{grant}|{method}|{path}", DateTime.UtcNow))
            await AuditAsync(connectionString, context, sub, target, grant.Value, method, path, logger);
        return true;
    }

    private static async Task DenyAsync(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { message, code });
    }

    private static async Task<Guid?> FindGrantAsync(string cs, string userId, string slug, CancellationToken ct)
    {
        var key = userId + "|" + slug;
        var now = DateTime.UtcNow;
        if (Cache.TryGetValue(key, out var hit) && now - hit.At < (hit.Grant is null ? NegativeTtl : PositiveTtl))
            return hit.Grant;
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT \"Id\" FROM platform_access_grants WHERE \"TenantSlug\" = @s AND \"GrantedToUserId\" = @u " +
            "AND \"RevokedAt\" IS NULL AND \"ExpiresAt\" > now() ORDER BY \"ExpiresAt\" DESC LIMIT 1", conn);
        cmd.Parameters.AddWithValue("s", slug);
        cmd.Parameters.AddWithValue("u", userId);
        var id = await cmd.ExecuteScalarAsync(ct) as Guid?;
        Cache[key] = (id, now);
        return id;
    }

    private static async Task AuditAsync(string cs, HttpContext http, string sub, string tenant, Guid grant, string method, string path, ILogger logger)
    {
        try
        {
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync(http.RequestAborted);
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO audit_log (\"TenantSlug\",\"Service\",\"EntityType\",\"EntityId\",\"Action\",\"Changes\",\"UserId\",\"UserName\",\"CorrelationId\",\"IpAddress\",\"OccurredAt\") " +
                "VALUES (@t,@svc,'PlatformAccessGrant',@g,'PlatformAccess',@c::jsonb,@u,@n,@corr,@ip,now())", conn);
            cmd.Parameters.AddWithValue("t", tenant);
            cmd.Parameters.AddWithValue("svc", ServiceName);
            cmd.Parameters.AddWithValue("g", grant.ToString());
            cmd.Parameters.AddWithValue("c", JsonSerializer.Serialize(new { method, path }));
            cmd.Parameters.AddWithValue("u", sub);
            cmd.Parameters.AddWithValue("n", (object?)(http.User.FindFirst("name")?.Value ?? http.User.FindFirst("preferred_username")?.Value) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("corr", (object?)(http.Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? http.TraceIdentifier) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("ip", (object?)(http.Request.Headers["X-Real-IP"].FirstOrDefault() ?? http.Connection.RemoteIpAddress?.ToString()) ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(http.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError("Platform erişim denetim kaydı yazılamadı: {Message}", ex.Message);
        }
    }
}

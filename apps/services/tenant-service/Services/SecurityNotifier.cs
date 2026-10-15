using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TenantService.Data;

namespace TenantService.Services;

/// <summary>
/// Guvenlik dalgasi 2A: kimlik guvenligi olaylari icin bildirim ve denetim kaydi yardimcisi
/// (supheli giris, platform yoneticisi erisim izni).
///
/// * Bildirim notification-service'in ic ucuyla (/api/internal/chat/notify, X-Internal-Token)
///   olusturulur; bu servis bildirim tablosuna yazmaz. Alici calisan kaydidir
///   (employee_employees."KeycloakUserId" ile eslenir); calisan kaydi olmayan hesaba
///   uygulama ici bildirim gonderilemez (denetim kaydi yine yazilir).
/// * audit_log'a dogrudan INSERT (hash zinciri veritabani tetikleyicisinde); yazilamazsa is
///   akisi bozulmaz, hata log'a duser.
/// * Kullanicinin kiracisi: once calisan kaydi, yoksa Keycloak organizasyon uyelikleri
///   (15 dk onbellek).
/// KVKK: bildirim ve denetim metinlerinde IP adresi yazilmaz.
/// </summary>
public sealed class SecurityNotifier
{
    private static readonly string? Cs = Environment.GetEnvironmentVariable("TENANT_DB_CONNECTION");
    private static readonly string NotificationBase =
        (Environment.GetEnvironmentVariable("NOTIFICATION_SERVICE_URL") ?? "http://notification-service:8080").TrimEnd('/');

    private static readonly SemaphoreSlim MembershipLock = new(1, 1);
    private static IReadOnlyDictionary<string, string> _membership = new Dictionary<string, string>();
    private static DateTimeOffset _membershipAt = DateTimeOffset.MinValue;
    private static readonly ConcurrentDictionary<string, (HashSet<string> Ids, DateTimeOffset At)> RoleCache = new();

    private readonly TenantDbContext _db;
    private readonly KeycloakAdminClient _kc;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<SecurityNotifier> _log;

    public SecurityNotifier(TenantDbContext db, KeycloakAdminClient kc, IHttpClientFactory http, ILogger<SecurityNotifier> log)
    {
        _db = db; _kc = kc; _http = http; _log = log;
    }

    /// <summary>Keycloak kullanicisinin kiracisi (calisan kaydi ya da organizasyon uyeligi); yoksa null.</summary>
    public async Task<string?> TenantOfUserAsync(string userId, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(Cs))
        {
            try
            {
                await using var conn = new NpgsqlConnection(Cs);
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand(
                    "SELECT \"TenantSlug\" FROM employee_employees WHERE \"KeycloakUserId\" = @u LIMIT 1", conn);
                cmd.Parameters.AddWithValue("u", userId);
                if (await cmd.ExecuteScalarAsync(ct) is string slug && slug.Length > 0) return slug;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogDebug(ex, "Çalışan kaydından kiracı bulunamadı");
            }
        }
        var map = await MembershipAsync(ct);
        return map.TryGetValue(userId, out var s) ? s : null;
    }

    private async Task<IReadOnlyDictionary<string, string>> MembershipAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow - _membershipAt < TimeSpan.FromMinutes(15)) return _membership;
        await MembershipLock.WaitAsync(ct);
        try
        {
            if (DateTimeOffset.UtcNow - _membershipAt < TimeSpan.FromMinutes(15)) return _membership;
            var tenants = await _db.Tenants.AsNoTracking().Where(t => t.KeycloakOrgId != null)
                .Select(t => new { t.Slug, t.KeycloakOrgId }).ToListAsync(ct);
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var t in tenants)
            {
                try
                {
                    foreach (var id in await _kc.ListOrganizationMemberIdsAsync(t.KeycloakOrgId!, ct)) map.TryAdd(id, t.Slug);
                }
                catch (InvalidOperationException ex)
                {
                    _log.LogDebug("Organizasyon üyeleri okunamadı ({Slug}): {Message}", t.Slug, ex.Message);
                }
            }
            _membership = map;
            _membershipAt = DateTimeOffset.UtcNow;
            return map;
        }
        finally
        {
            MembershipLock.Release();
        }
    }

    /// <summary>Kiracinin verilen rollerden birine sahip Keycloak kullanicilari (organizasyon uyesi olanlar).</summary>
    public async Task<HashSet<string>> TenantUsersWithRolesAsync(string tenantSlug, IEnumerable<string> roles, CancellationToken ct)
    {
        var orgId = await _db.Tenants.AsNoTracking().Where(t => t.Slug == tenantSlug).Select(t => t.KeycloakOrgId).FirstOrDefaultAsync(ct);
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(orgId)) return result;
        var members = new HashSet<string>(await _kc.ListOrganizationMemberIdsAsync(orgId, ct), StringComparer.Ordinal);
        foreach (var role in roles)
            foreach (var id in await RoleUsersAsync(role, ct))
                if (members.Contains(id)) result.Add(id);
        return result;
    }

    /// <summary>Rol kullanicilari (5 dk onbellek; realm genelinde).</summary>
    public async Task<HashSet<string>> RoleUsersAsync(string role, CancellationToken ct)
    {
        if (RoleCache.TryGetValue(role, out var hit) && DateTimeOffset.UtcNow - hit.At < TimeSpan.FromMinutes(5)) return hit.Ids;
        var ids = await _kc.ListRoleUserIdsAsync(role, ct);
        RoleCache[role] = (ids, DateTimeOffset.UtcNow);
        return ids;
    }

    /// <summary>Keycloak kimliklerinin kiracidaki calisan kayitlari.</summary>
    public async Task<List<Guid>> EmployeesOfUsersAsync(string tenantSlug, IEnumerable<string> userIds, CancellationToken ct)
    {
        var ids = userIds.Distinct().ToArray();
        var list = new List<Guid>();
        if (ids.Length == 0 || string.IsNullOrEmpty(Cs)) return list;
        try
        {
            await using var conn = new NpgsqlConnection(Cs);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "SELECT \"Id\" FROM employee_employees WHERE \"TenantSlug\" = @t AND \"KeycloakUserId\" = ANY(@u) AND \"Status\" <> 'Terminated'", conn);
            cmd.Parameters.AddWithValue("t", tenantSlug);
            cmd.Parameters.AddWithValue("u", ids);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) list.Add(r.GetGuid(0));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning("Bildirim alıcıları bulunamadı ({Tenant}): {Message}", tenantSlug, ex.Message);
        }
        return list;
    }

    /// <summary>
    /// Calisan kaydi OLMAYAN hesaplarin e-posta adresleri (organizasyon uyeliginden). Uygulama ici
    /// bildirim calisan kimligi ister; bu hesaplar (ornegin yalnizca yonetim icin acilmis sirket
    /// yoneticisi) guvenlik bildirimlerini e-postayla alir.
    /// </summary>
    public async Task<List<string>> EmailsOfUsersWithoutEmployeeAsync(string tenantSlug, IEnumerable<string> userIds, CancellationToken ct)
    {
        var ids = userIds.Distinct().ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0 || string.IsNullOrEmpty(Cs)) return new();
        try
        {
            await using var conn = new NpgsqlConnection(Cs);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "SELECT \"KeycloakUserId\" FROM employee_employees WHERE \"TenantSlug\" = @t AND \"KeycloakUserId\" = ANY(@u)", conn);
            cmd.Parameters.AddWithValue("t", tenantSlug);
            cmd.Parameters.AddWithValue("u", ids.ToArray());
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) ids.Remove(r.GetString(0));
            if (ids.Count == 0) return new();
            var orgId = await _db.Tenants.AsNoTracking().Where(t => t.Slug == tenantSlug).Select(t => t.KeycloakOrgId).FirstOrDefaultAsync(ct);
            if (string.IsNullOrEmpty(orgId)) return new();
            return (await _kc.ListOrganizationMembersAsync(orgId, ct))
                .Where(m => ids.Contains(m.Id) && m.Enabled && !string.IsNullOrWhiteSpace(m.Email))
                .Select(m => m.Email!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning("Çalışan kaydı olmayan alıcılar bulunamadı ({Tenant}): {Message}", tenantSlug, ex.Message);
            return new();
        }
    }

    /// <summary>
    /// E-posta bildirimi (en iyi caba): notification-service ic ucuna alici e-postasiyla gonderilir
    /// (calisan kimligi yok). "security." sablonlari kapatilamaz; gonderim e-posta iscisiyle yapilir.
    /// </summary>
    public async Task NotifyEmailAsync(string tenantSlug, IEnumerable<string> emails, string subject, string body, string templateCode, CancellationToken ct)
    {
        var token = Environment.GetEnvironmentVariable("INTERNAL_SERVICE_TOKEN");
        if (string.IsNullOrEmpty(token)) return;
        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        foreach (var email in emails.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, NotificationBase + "/api/internal/chat/notify")
                {
                    Content = JsonContent.Create(new { tenantSlug, recipientEmployeeId = Guid.Empty, recipientEmail = email, subject, body, templateCode, language = "tr" }),
                };
                req.Headers.Add(Security.InternalServiceToken.Header, token);
                using var resp = await client.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode)
                    _log.LogWarning("Güvenlik e-postası oluşturulamadı (HTTP {Status}, {Code})", (int)resp.StatusCode, templateCode);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning("Güvenlik e-postası gönderilemedi ({Code}): {Message}", templateCode, ex.Message);
            }
        }
    }

    /// <summary>
    /// Uygulama ici bildirim (en iyi caba). templateCode "security." ile baslar: kapatilamayan
    /// guvenlik kategorisi (notification-service DeliveryRules).
    /// </summary>
    public async Task NotifyAsync(string tenantSlug, IEnumerable<Guid> recipients, string subject, string body, string templateCode, CancellationToken ct)
    {
        var token = Environment.GetEnvironmentVariable("INTERNAL_SERVICE_TOKEN");
        if (string.IsNullOrEmpty(token))
        {
            _log.LogWarning("INTERNAL_SERVICE_TOKEN tanımlı değil; güvenlik bildirimi gönderilemedi ({Code})", templateCode);
            return;
        }
        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        foreach (var r in recipients.Distinct())
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, NotificationBase + "/api/internal/chat/notify")
                {
                    Content = JsonContent.Create(new { tenantSlug, recipientEmployeeId = r, subject, body, templateCode, language = "tr" }),
                };
                req.Headers.Add(Security.InternalServiceToken.Header, token);
                using var resp = await client.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode)
                    _log.LogWarning("Güvenlik bildirimi oluşturulamadı (HTTP {Status}, {Code})", (int)resp.StatusCode, templateCode);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning("Güvenlik bildirimi gönderilemedi ({Code}): {Message}", templateCode, ex.Message);
            }
        }
    }

    /// <summary>audit_log'a satir (TenantSlug null olabilir: kiraciya baglanamayan olay).</summary>
    public async Task AuditAsync(string? tenantSlug, string entityType, string entityId, string action, object changes,
        string? userId, string? userName, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(Cs)) return;
        try
        {
            await using var conn = new NpgsqlConnection(Cs);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO audit_log (\"TenantSlug\",\"Service\",\"EntityType\",\"EntityId\",\"Action\",\"Changes\",\"UserId\",\"UserName\",\"OccurredAt\") " +
                "VALUES (@t,'tenant-service',@e,@i,@a,@c::jsonb,@u,@n,now())", conn);
            cmd.Parameters.AddWithValue("t", (object?)tenantSlug ?? DBNull.Value);
            cmd.Parameters.AddWithValue("e", entityType);
            cmd.Parameters.AddWithValue("i", entityId);
            cmd.Parameters.AddWithValue("a", action);
            cmd.Parameters.AddWithValue("c", JsonSerializer.Serialize(changes));
            cmd.Parameters.AddWithValue("u", (object?)userId ?? "system");
            cmd.Parameters.AddWithValue("n", (object?)userName ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError("Denetim kaydı yazılamadı ({Action}): {Message}", action, ex.Message);
        }
    }
}

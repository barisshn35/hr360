using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace EmployeeService.Auditing;

/// <summary>
/// Platform yöneticisinin kiracı çalışan verisine erişimi (destek amaçlı) kayıt altına alınır:
/// platform-admin rolüyle, kendi kiracısı dışındaki (ya da organization claim'i olmadan)
/// çalışan kayıtları okunduğunda <c>audit_log</c>'a "PlatformAccess" satırı yazılır.
///
/// * TenantSlug = okunan kaydın kiracısı: kiracı kendi denetim kaydında platform erişimini görür.
/// * Liste okumalarında kiracı başına TEK satır ve yalnızca kayıt SAYISI (veri en aza indirme;
///   çalışan kimlikleri yazılmaz).
/// * Hash zinciri (ChainSeq/PrevHash/Hash) veritabanı tetikleyicisiyle üretilir; burada düz
///   INSERT yeterlidir (diğer servislerdeki SensitiveViewed yazımlarıyla aynı biçim).
/// * Denetim yazılamazsa iş akışı bozulmaz; hata log'a düşer.
/// </summary>
public static class PlatformAccessAudit
{
    /// <summary>Platform yöneticisinin <paramref name="recordTenant"/> kiracısına erişimi kaydedilmeli mi?</summary>
    public static bool ShouldLog(bool isPlatformAdmin, string? callerTenant, string? recordTenant) =>
        isPlatformAdmin && !string.IsNullOrEmpty(recordTenant)
        && (string.IsNullOrEmpty(callerTenant) || !string.Equals(callerTenant, recordTenant, StringComparison.Ordinal));

    public static async Task WriteAsync(DbContext db, HttpContext http, string entityType, string entityId,
        IEnumerable<(string Tenant, object Changes)> rows, CancellationToken ct, string action = "PlatformAccess")
    {
        var list = rows.ToList();
        if (list.Count == 0) return;
        var user = http.User;
        var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.FindFirst("sub")?.Value ?? "unknown";
        var userName = user.FindFirst("name")?.Value ?? user.FindFirst("preferred_username")?.Value;
        var correlation = http.Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? http.TraceIdentifier;
        var ip = http.Request.Headers["X-Real-IP"].FirstOrDefault() ?? http.Connection.RemoteIpAddress?.ToString();
        try
        {
            var conn = (NpgsqlConnection)db.Database.GetDbConnection();
            var opened = false;
            if (conn.State != System.Data.ConnectionState.Open)
            {
                await conn.OpenAsync(ct);
                opened = true;
            }
            try
            {
                await using var batch = new NpgsqlBatch(conn);
                foreach (var (tenant, changes) in list)
                {
                    var cmd = new NpgsqlBatchCommand(
                        "INSERT INTO audit_log (\"TenantSlug\",\"Service\",\"EntityType\",\"EntityId\",\"Action\",\"Changes\",\"UserId\",\"UserName\",\"CorrelationId\",\"IpAddress\",\"OccurredAt\") " +
                        "VALUES ($1,'employee-service',$2,$3,$9,$4::jsonb,$5,$6,$7,$8,now())");
                    cmd.Parameters.Add(new NpgsqlParameter { Value = tenant });
                    cmd.Parameters.Add(new NpgsqlParameter { Value = entityType });
                    cmd.Parameters.Add(new NpgsqlParameter { Value = entityId });
                    cmd.Parameters.Add(new NpgsqlParameter { Value = JsonSerializer.Serialize(changes) });
                    cmd.Parameters.Add(new NpgsqlParameter { Value = userId });
                    cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)userName ?? DBNull.Value });
                    cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)correlation ?? DBNull.Value });
                    cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)ip ?? DBNull.Value });
                    cmd.Parameters.Add(new NpgsqlParameter { Value = action });
                    batch.BatchCommands.Add(cmd);
                }
                await batch.ExecuteNonQueryAsync(ct);
            }
            finally
            {
                if (opened) await conn.CloseAsync();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[audit] employee-service: platform erişim kaydı yazılamadı: {ex.Message}");
        }
    }
}

/// <summary>
/// Güvenlik dalgası 2B: yönetici/İK'nın BAŞKA bir çalışanın tam kaydını açması audit_log'a "Viewed" olarak
/// yazılır; governance-service'in toplu görüntüleme dedektörü (kısa sürede çok sayıda farklı kayıt)
/// bu satırları sayar. Gürültüyü azaltmak için aynı kişi-kayıt çifti 10 dakikada bir kez yazılır;
/// yalnızca gateway'den gelen (X-Real-IP taşıyan) istekler sayılır — servislerin kullanıcı jetonuyla
/// yaptığı iç okumalar (bildirim, onaycı bulma) kişinin görüntülemesi değildir.
/// </summary>
public static class RecordViewLog
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> Recent = new();
    public static readonly TimeSpan Dedupe = TimeSpan.FromMinutes(10);

    public static bool ShouldLog(string tenant, string userId, Guid employeeId, DateTime? now = null)
    {
        var t = now ?? DateTime.UtcNow;
        if (Recent.Count > 50_000)
            foreach (var kv in Recent.Where(kv => t - kv.Value > Dedupe).ToList()) Recent.TryRemove(kv.Key, out _);
        var key = $"{tenant}|{userId}|{employeeId}";
        var fresh = true;
        Recent.AddOrUpdate(key, t, (_, prev) => { if (t - prev < Dedupe) { fresh = false; return prev; } return t; });
        return fresh;
    }
}

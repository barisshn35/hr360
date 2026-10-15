using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace WorkflowService.Auditing;

/// <summary>
/// Platform yöneticisinin kiracı onay taleplerine erişimi (destek amaçlı) kayıt altına alınır:
/// platform-admin rolüyle, kendi kiracısı dışındaki (ya da organization claim'i olmadan)
/// onay talepleri okunduğunda <c>audit_log</c>'a "PlatformAccess" satırı yazılır.
///
/// * TenantSlug = okunan kaydın kiracısı: kiracı kendi denetim kaydında platform erişimini görür.
/// * Liste okumalarında kiracı başına TEK satır ve yalnızca kayıt SAYISI (veri en aza indirme;
///   talep kimlikleri yazılmaz).
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
        IEnumerable<(string Tenant, object Changes)> rows, CancellationToken ct)
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
                        "VALUES ($1,'workflow-service',$2,$3,'PlatformAccess',$4::jsonb,$5,$6,$7,$8,now())");
                    cmd.Parameters.Add(new NpgsqlParameter { Value = tenant });
                    cmd.Parameters.Add(new NpgsqlParameter { Value = entityType });
                    cmd.Parameters.Add(new NpgsqlParameter { Value = entityId });
                    cmd.Parameters.Add(new NpgsqlParameter { Value = JsonSerializer.Serialize(changes) });
                    cmd.Parameters.Add(new NpgsqlParameter { Value = userId });
                    cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)userName ?? DBNull.Value });
                    cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)correlation ?? DBNull.Value });
                    cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)ip ?? DBNull.Value });
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
            Console.Error.WriteLine($"[audit] workflow-service: platform erişim kaydı yazılamadı: {ex.Message}");
        }
    }
}

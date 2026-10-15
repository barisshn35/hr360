using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace RecruitmentService.Auditing;

/// <summary>
/// Denetim kaydı (audit trail). Bu servisin DbContext'inde kaydedilen her
/// ekleme/güncelleme/silme için ortak <c>audit_log</c> tablosuna bir satır yazar:
/// hangi varlık, hangi alanlar (eski → yeni), kim, ne zaman, hangi istek
/// (X-Correlation-Id, gateway'de her isteğe atanır) ve hangi IP.
///
/// Tasarım notları:
///  * Değişiklikler SaveChanges'TAN ÖNCE yakalanır (sonra özgün değerler
///    sıfırlanır), satırlar kayıt BAŞARILI olduktan sonra aynı bağlantı ve —
///    varsa — aynı işlem (transaction) içinde yazılır.
///  * Hassas alanlar (maaş, IBAN, TCKN, parola, gizli anahtar…) değer olarak
///    asla kaydedilmez; yalnızca "değişti" bilgisi "***" ile tutulur.
///  * Denetim yazılamazsa (tablo yok, bağlantı hatası) iş işlemi BOZULMAZ;
///    hata log'a düşer. Denetim, iş akışını durdurmamalı.
///  * Kafka tüketicileri gibi HTTP dışı yazımlar "system" kullanıcısıyla kaydedilir.
///
/// Bu dosya tüm servislerde birebir aynıdır (yalnızca namespace farklı);
/// governance-service /audit uçları bu tabloyu okur.
/// </summary>
public sealed class AuditInterceptor : SaveChangesInterceptor
{
    private static readonly HttpContextAccessor Http = new();

    private static readonly string[] SensitiveFragments =
    {
        "salary", "iban", "nationalid", "tckn", "identitynumber", "password", "secret",
        "keyhash", "token", "bankaccount", "privatenotes",
    };

    /// <summary>Teknik/altyapı tabloları denetlenmez (gürültü olur).</summary>
    private static readonly HashSet<string> SkipTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ProcessedEvent", "OutboxMessage", "AuditEntry", "GovernanceEvent", "RuleRun",
        "WebhookDelivery", "ProvisioningLogEntry",
        // Anonim anket yanıtı kimle ilişkilendirilmemeli: denetim satırı yanıtı
        // yanıtlayanla (UserId) eşleştirirdi.
        "SurveyResponse",
    };

    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly string _service;
    private readonly ConditionalWeakTable<DbContext, List<Pending>> _pending = new();

    public AuditInterceptor(string service) => _service = service;

    private sealed record Pending(EntityEntry Entry, string Type, string Action, Dictionary<string, object?> Changes);

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Capture(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Capture(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        FlushAsync(eventData.Context, CancellationToken.None).GetAwaiter().GetResult();
        return base.SavedChanges(eventData, result);
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        await FlushAsync(eventData.Context, cancellationToken);
        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        if (eventData.Context is not null) _pending.Remove(eventData.Context);
        base.SaveChangesFailed(eventData);
    }

    private void Capture(DbContext? db)
    {
        if (db is null) return;
        var list = new List<Pending>();
        foreach (var entry in db.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;
            if (entry.Metadata.IsOwned()) continue;
            var type = entry.Metadata.ClrType.Name;
            if (SkipTypes.Contains(type)) continue;

            var changes = new Dictionary<string, object?>();
            foreach (var prop in entry.Properties)
            {
                var name = prop.Metadata.Name;
                if (name == "TenantSlug") continue;
                var sensitive = IsSensitive(name);
                switch (entry.State)
                {
                    case EntityState.Added:
                        if (prop.CurrentValue is null) continue;
                        changes[name] = sensitive ? "***" : Simplify(prop.CurrentValue);
                        break;
                    case EntityState.Deleted:
                        changes[name] = sensitive ? "***" : Simplify(prop.OriginalValue);
                        break;
                    case EntityState.Modified:
                        if (!prop.IsModified || Equals(prop.OriginalValue, prop.CurrentValue)) continue;
                        changes[name] = sensitive
                            ? new { old = "***", @new = "***" }
                            : new { old = Simplify(prop.OriginalValue), @new = Simplify(prop.CurrentValue) };
                        break;
                }
            }
            if (entry.State == EntityState.Modified && changes.Count == 0) continue;

            var action = entry.State switch
            {
                EntityState.Added => "Created",
                EntityState.Deleted => "Deleted",
                _ => "Updated",
            };
            list.Add(new Pending(entry, type, action, changes));
        }
        if (list.Count > 0) _pending.AddOrUpdate(db, list);
    }

    private async Task FlushAsync(DbContext? db, CancellationToken ct)
    {
        if (db is null || !_pending.TryGetValue(db, out var list)) return;
        _pending.Remove(db);

        var http = Http.HttpContext;
        var user = http?.User;
        var userId = user?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user?.FindFirst("sub")?.Value ?? "system";
        var userName = user?.FindFirst("name")?.Value ?? user?.FindFirst("preferred_username")?.Value ?? (http is null ? "Sistem (arka plan)" : null);
        var correlation = http?.Request.Headers["X-Correlation-Id"].FirstOrDefault()
            ?? http?.Request.Headers["X-Request-Id"].FirstOrDefault()
            ?? http?.TraceIdentifier;
        var ip = http?.Request.Headers["X-Real-IP"].FirstOrDefault() ?? http?.Connection.RemoteIpAddress?.ToString();
        var claimTenant = OrganizationSlug(user);

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
                var tx = db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;
                if (tx is not null) batch.Transaction = tx;
                foreach (var p in list)
                {
                    var tenant = p.Entry.Metadata.FindProperty("TenantSlug") is not null
                        ? p.Entry.Property("TenantSlug").CurrentValue as string
                        : null;
                    var key = string.Join(",", p.Entry.Metadata.FindPrimaryKey()?.Properties
                        .Select(k => p.Entry.Property(k.Name).CurrentValue?.ToString() ?? "") ?? Array.Empty<string>());
                    var cmd = new NpgsqlBatchCommand(
                        "INSERT INTO audit_log (\"TenantSlug\",\"Service\",\"EntityType\",\"EntityId\",\"Action\",\"Changes\",\"UserId\",\"UserName\",\"CorrelationId\",\"IpAddress\",\"OccurredAt\") " +
                        "VALUES ($1,$2,$3,$4,$5,$6::jsonb,$7,$8,$9,$10,now())");
                    cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)(tenant ?? claimTenant) ?? DBNull.Value });
                    cmd.Parameters.Add(new NpgsqlParameter { Value = _service });
                    cmd.Parameters.Add(new NpgsqlParameter { Value = p.Type });
                    cmd.Parameters.Add(new NpgsqlParameter { Value = key });
                    cmd.Parameters.Add(new NpgsqlParameter { Value = p.Action });
                    cmd.Parameters.Add(new NpgsqlParameter { Value = JsonSerializer.Serialize(p.Changes, Json) });
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
            Console.Error.WriteLine($"[audit] {_service}: denetim kaydı yazılamadı: {ex.Message}");
        }
    }

    private static bool IsSensitive(string name)
    {
        var n = name.ToLowerInvariant();
        return SensitiveFragments.Any(n.Contains);
    }

    private static object? Simplify(object? value) => value switch
    {
        null => null,
        string s => s.Length > 500 ? s[..500] + "…" : s,
        DateTime or DateTimeOffset or DateOnly or TimeOnly or Guid or bool => value,
        Enum e => e.ToString(),
        byte[] => "(ikili veri)",
        _ when value.GetType().IsPrimitive || value is decimal => value,
        // Liste/nesne (jsonb sütunları, text[]) — okunur JSON olarak.
        _ => TryJson(value),
    };

    private static object? TryJson(object value)
    {
        try
        {
            var el = JsonSerializer.SerializeToElement(value, Json);
            return el.GetRawText().Length > 4000 ? "(büyük içerik)" : el;
        }
        catch (Exception) { return value.ToString(); }
    }

    private static string? OrganizationSlug(ClaimsPrincipal? user)
    {
        var raw = user?.FindFirst("organization")?.Value;
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.ValueKind switch
            {
                JsonValueKind.Object => doc.RootElement.EnumerateObject().Select(p => p.Name).FirstOrDefault(),
                JsonValueKind.Array => doc.RootElement.EnumerateArray().Select(e => e.GetString()).FirstOrDefault(),
                JsonValueKind.String => doc.RootElement.GetString(),
                _ => null,
            };
        }
        catch (JsonException) { return raw.Trim('[', ']', '"', ' '); }
    }
}

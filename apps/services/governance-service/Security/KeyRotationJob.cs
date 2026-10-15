using Npgsql;

namespace GovernanceService.Security;

/// <summary>Şifreli sütunun türü: öneksiz değeri şifreli mi (Text), düz metin mi (Prefixed) sayılır; Bytes = bytea.</summary>
public enum EncKind { Text, Prefixed, Bytes }

/// <summary>Servisin sahibi olduğu şifreli sütun.</summary>
public sealed record EncColumn(string Table, string Column, EncKind Kind, string IdColumn = "Id");

/// <summary>
/// Anahtar yenileme: etkin olmayan anahtarla (eski biçimler dahil) şifrelenmiş değerleri etkin anahtarla
/// yeniden yazar. Açılıştan CRYPTO_REENCRYPT_DELAY_SECONDS (60) sn sonra ve 6 saatte bir çalışır;
/// halkada yalnızca eski k0 anahtarı varsa hiçbir şey yapmaz. 200'lük partiler, kimliğe göre sıralı;
/// UPDATE eski değer hâlâ yerindeyse yapılır (eşzamanlı yazımda veri ezilmez, yeniden çalıştırmak güvenli).
/// Günlüğe yalnızca sayılar yazılır, değer asla. Kapatmak: CRYPTO_REENCRYPT=off.
/// İlerleme: scripts/crypto-keys.sh status (sütun başına anahtar kimliği sayıları).
/// </summary>
public sealed class KeyRotationJob(string connectionEnv, IReadOnlyList<EncColumn> columns, ILogger<KeyRotationJob> log) : BackgroundService
{
    private const int Batch = 200;

    /// <summary>Değer etkin anahtarla değilse yeniden şifrelenmiş hâlini, değilse null döner.</summary>
    public static string? Reencrypt(string stored, EncKind kind)
    {
        var id = KeyRing.KeyIdOf(stored, rawIsSealed: kind == EncKind.Text);
        if (id is null || id == KeyRing.ActiveId) return null;
        return KeyRing.Seal(KeyRing.Open(stored), kind == EncKind.Text ? "" : KeyRing.V1);
    }

    public static byte[]? Reencrypt(byte[] stored) =>
        KeyRing.BytesKeyId(stored) == KeyRing.ActiveId ? null : KeyRing.SealBytes(KeyRing.OpenBytes(stored));

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("CRYPTO_REENCRYPT"), "off", StringComparison.OrdinalIgnoreCase)) return;
        var delay = int.TryParse(Environment.GetEnvironmentVariable("CRYPTO_REENCRYPT_DELAY_SECONDS"), out var d) ? d : 60;
        try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, delay)), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { await RunOnceAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning("Anahtar yenileme turu başarısız: {Type}; 6 saat sonra yeniden denenecek.", ex.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromHours(6), ct); } catch (OperationCanceledException) { return; }
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        if (!KeyRing.Enabled) { log.LogWarning("Anahtar yenileme atlandı: {Error}", KeyRing.Error); return; }
        var active = KeyRing.ActiveId!;
        if (active == KeyRing.LegacyId && KeyRing.KeyIds.Count == 1) return;
        var cs = Environment.GetEnvironmentVariable(connectionEnv);
        if (string.IsNullOrEmpty(cs)) return;
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(ct);
        foreach (var c in columns)
        {
            try
            {
                var (done, failed) = await RunColumnAsync(conn, c, active, ct);
                if (done > 0 || failed > 0)
                    log.LogInformation("Anahtar yenileme {Table}.{Column}: {Done} değer etkin anahtarla ({Active}) yeniden şifrelendi, {Failed} değer açılamadı.",
                        c.Table, c.Column, done, active, failed);
            }
            catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
            {
                log.LogDebug("Anahtar yenileme {Table}.{Column} atlandı (tablo/sütun yok).", c.Table, c.Column);
            }
            catch (PostgresException ex)
            {
                // Ör. 42501: servis rolünde UPDATE yetkisi yok (scripts/db-roles.sh generate/grants). Diğer sütunlar sürer.
                log.LogWarning("Anahtar yenileme {Table}.{Column} başarısız (SQLSTATE {State}).", c.Table, c.Column, ex.SqlState);
            }
        }
    }

    private static async Task<(long Done, long Failed)> RunColumnAsync(NpgsqlConnection conn, EncColumn c, string active, CancellationToken ct)
    {
        string col = $"\"{c.Column}\"", id = $"\"{c.IdColumn}\"";
        var filter = c.Kind switch
        {
            EncKind.Bytes => "",
            // Etkin anahtar k0 iken eski biçimler günceldir; yalnızca başka kimlikli enc2 değerleri yenilenir.
            _ when active == KeyRing.LegacyId => $" AND {col} LIKE 'enc2:%' AND {col} NOT LIKE @cur",
            EncKind.Prefixed => $" AND ({col} LIKE 'enc1:%' OR {col} LIKE 'enc2:%') AND {col} NOT LIKE @cur",
            _ => $" AND {col} <> '' AND {col} NOT LIKE @cur",
        };
        long done = 0, failed = 0;
        object? last = null;
        while (true)
        {
            var rows = new List<(object Id, object Value)>();
            await using (var q = new NpgsqlCommand(
                $"SELECT {id}, {col} FROM {c.Table} WHERE {col} IS NOT NULL{filter}{(last is null ? "" : $" AND {id} > @last")} ORDER BY {id} LIMIT {Batch}", conn))
            {
                if (filter.Contains("@cur")) q.Parameters.AddWithValue("cur", KeyRing.V2 + active + ":%");
                if (last is not null) q.Parameters.AddWithValue("last", last);
                await using var r = await q.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) rows.Add((r.GetValue(0), r.GetValue(1)));
            }
            if (rows.Count == 0) break;
            foreach (var (rowId, value) in rows)
            {
                last = rowId;
                object? next;
                try { next = value is byte[] b ? Reencrypt(b) : Reencrypt((string)value, c.Kind); }
                catch (Exception) { failed++; continue; }
                if (next is null) continue;
                await using var u = new NpgsqlCommand($"UPDATE {c.Table} SET {col} = @new WHERE {id} = @id AND {col} = @old", conn);
                u.Parameters.AddWithValue("new", next);
                u.Parameters.AddWithValue("id", rowId);
                u.Parameters.AddWithValue("old", value);
                done += await u.ExecuteNonQueryAsync(ct);
            }
            if (rows.Count < Batch) break;
        }
        return (done, failed);
    }
}

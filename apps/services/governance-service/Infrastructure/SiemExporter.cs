using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using GovernanceService.Infrastructure.Chat;

namespace GovernanceService.Infrastructure;

/// <summary>
/// G21: Denetim kaydını SIEM'e (syslog, RFC 5424) aktarır. SIEM_SYSLOG_ENDPOINT
/// (udp://host:514 ya da tcp://host:601) tanımlıysa çalışır.
///
/// KVKK — veri en aza indirme: kullanıcı ve kayıt kimlikleri kiracıya özgü olmayan
/// anahtarla HMAC-SHA256 takma adına çevrilir (SIEM_PSEUDONYM_KEY), ad/e-posta ve
/// değişiklik içeriği (Changes) gönderilmez, IP adresinin son okteti sıfırlanır.
/// Aynı kişi SIEM'de hep aynı takma adla görünür (korelasyon), ama kim olduğu
/// HR360 dışında çözülemez. Zincir özeti (hash) bütünlük kanıtı olarak eklenir.
/// </summary>
public sealed class SiemExporter : BackgroundService
{
    public static readonly Uri? Endpoint = Uri.TryCreate(EnvVar.Or("SIEM_SYSLOG_ENDPOINT", ""), UriKind.Absolute, out var u)
        && u.Scheme is "udp" or "tcp" && u.Port > 0 ? u : null;
    public static string? EndpointDisplay => Endpoint is null ? null : $"{Endpoint.Scheme}://{Endpoint.Host}:{Endpoint.Port}";

    private static readonly byte[] Key = Encoding.UTF8.GetBytes(EnvVar.Or("SIEM_PSEUDONYM_KEY",
        "siem:" + EnvVar.Or("INTERNAL_SERVICE_TOKEN", "hr360")));
    private static readonly int IntervalSeconds = int.TryParse(EnvVar.Or("SIEM_INTERVAL_SECONDS", "15"), out var s) ? Math.Clamp(s, 1, 3600) : 15;

    private readonly IServiceProvider _sp;
    private readonly ILogger<SiemExporter> _log;
    public SiemExporter(IServiceProvider sp, ILogger<SiemExporter> log) { _sp = sp; _log = log; }

    public static string Pseudonym(string? value) => string.IsNullOrEmpty(value) ? "-"
        : "p_" + Convert.ToHexString(HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();

    public static string MaskIp(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip) || !System.Net.IPAddress.TryParse(ip, out var a)) return "-";
        var b = a.GetAddressBytes();
        if (b.Length == 4) { b[3] = 0; return new System.Net.IPAddress(b) + "/24"; }
        for (var i = 6; i < b.Length; i++) b[i] = 0;
        return new System.Net.IPAddress(b) + "/48";
    }

    private static string Sd(string? v) => (v ?? "-").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("]", "\\]");

    public static string Format(DateTime at, string? tenant, string service, string entity, string? entityId, string action,
        string? userId, string? ip, long seq, string? hash)
    {
        // PRI: facility 13 (log audit) * 8 + severity 6 (informational) = 110
        var host = Environment.MachineName;
        return $"<110>1 {at.ToUniversalTime():yyyy-MM-ddTHH:mm:ss.ffffffZ} {host} hr360 - audit " +
               $"[hr360@32473 tenant=\"{Sd(tenant)}\" service=\"{Sd(service)}\" entity=\"{Sd(entity)}\" entityRef=\"{Pseudonym(entityId)}\" " +
               $"action=\"{Sd(action)}\" user=\"{Pseudonym(userId)}\" net=\"{MaskIp(ip)}\" seq=\"{seq}\" hash=\"{Sd(hash)}\"] " +
               $"{Sd(service)} {Sd(entity)} {Sd(action)}";
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (Endpoint is null) return;
        _log.LogInformation("SIEM aktarımı açık: {Endpoint}", EndpointDisplay);
        await Task.Delay(TimeSpan.FromSeconds(5), ct);
        while (!ct.IsCancellationRequested)
        {
            try { await RunOnceAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning("SIEM aktarımı başarısız: {Message}", ex.Message);
                try
                {
                    using var scope = _sp.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<Sql>().ExecuteAsync(
                        """INSERT INTO governance_siem_cursor ("Id","LastError") VALUES (1,$1) ON CONFLICT ("Id") DO UPDATE SET "LastError" = $1""",
                        ct, ex.Message.Length > 500 ? ex.Message[..500] : ex.Message);
                }
                catch { /* yoksay */ }
            }
            await Task.Delay(TimeSpan.FromSeconds(IntervalSeconds), ct);
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var sql = scope.ServiceProvider.GetRequiredService<Sql>();
        await sql.ExecuteAsync("""INSERT INTO governance_siem_cursor ("Id","LastAuditId") VALUES (1, (SELECT coalesce(max("Id"),0) FROM audit_log)) ON CONFLICT DO NOTHING""", ct);
        var last = Convert.ToInt64(await sql.ScalarAsync("""SELECT "LastAuditId" FROM governance_siem_cursor WHERE "Id" = 1""", ct));
        var rows = await sql.QueryAsync("""
            SELECT "Id","OccurredAt","TenantSlug","Service","EntityType","EntityId","Action","UserId","IpAddress",coalesce("ChainSeq",0),"Hash"
            FROM audit_log WHERE "Id" > $1 ORDER BY "Id" LIMIT 500
            """, r => (Id: r.GetInt64(0), Line: Format(r.GetFieldValue<DateTime>(1), r.Str(2), r.GetString(3), r.GetString(4), r.Str(5),
                r.GetString(6), r.Str(7), r.Str(8), r.GetInt64(9), r.Str(10))), ct, last);
        if (rows.Count == 0) return;

        if (Endpoint!.Scheme == "udp")
        {
            using var udp = new UdpClient();
            udp.Connect(Endpoint.Host, Endpoint.Port);
            foreach (var (_, line) in rows)
            {
                var bytes = Encoding.UTF8.GetBytes(line);
                await udp.SendAsync(bytes.Length > 8000 ? bytes[..8000] : bytes, ct);
            }
        }
        else
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(Endpoint.Host, Endpoint.Port, ct);
            await using var stream = tcp.GetStream();
            foreach (var (_, line) in rows)
            {
                // RFC 6587 sekizli sayım çerçevesi
                var bytes = Encoding.UTF8.GetBytes(line);
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"{bytes.Length} "), ct);
                await stream.WriteAsync(bytes, ct);
            }
            await stream.FlushAsync(ct);
        }
        await sql.ExecuteAsync("""
            UPDATE governance_siem_cursor SET "LastAuditId" = $1, "LastSentAt" = now(), "Sent" = "Sent" + $2, "LastError" = NULL WHERE "Id" = 1
            """, ct, rows[^1].Id, (long)rows.Count);
    }
}

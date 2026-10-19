using System.Net;
using System.Net.Sockets;

namespace GovernanceService.Infrastructure;

/// <summary>
/// İK webhook hedefleri için SSRF koruması. Oluşturma/güncellemede yalnızca https ve iç ağ
/// dışı (TransferGuard.IsInternalHost) adres kabul edilir; gönderimde bağlantı anındaki gerçek
/// IP de denetlenir (DNS rebinding). İzinli iç adlar WEBHOOK_ALLOWED_HOSTS (virgülle) ile verilir.
/// Servisin kendi test alıcısı (/api/webhooks/inbox/{token}) her zaman serbesttir.
/// REST hook (açık API, kiracının kendi n8n'i) aboneliklerinde kiracının kendi iç ağı serbesttir;
/// yalnızca loopback, link-local/bulut meta veri, 0.0.0.0 ve HR360'ın kendi altyapısı engellenir
/// (bkz. <see cref="ValidateRestHook"/>, <see cref="RestHookClient"/>).
/// </summary>
public static class WebhookTargetGuard
{
    private static readonly HashSet<string> AllowedHosts = (Environment.GetEnvironmentVariable("WEBHOOK_ALLOWED_HOSTS") ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(h => h.ToLowerInvariant()).ToHashSet();

    public const string SelfInboxPrefix = "http://governance-service:8080/api/webhooks/inbox/";

    public static bool IsSelfInbox(string? url) =>
        url is not null && url.StartsWith(SelfInboxPrefix, StringComparison.Ordinal) && !url[SelfInboxPrefix.Length..].Contains('/');

    private static bool IsAllowedHost(string host) => AllowedHosts.Contains(host.TrimEnd('.').ToLowerInvariant());

    /// <summary>Geçerliyse null, değilse kullanıcıya gösterilecek hata.</summary>
    public static string? Validate(string? url)
    {
        if (IsSelfInbox(url)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
            return "Geçerli bir https adresi girin.";
        if (!IsAllowedHost(u.Host) && TransferGuard.IsInternalHost(url))
            return "İç ağ, yerel ya da özel IP adreslerine webhook gönderilemez.";
        return null;
    }

    /// <summary>Loopback, RFC 1918, CGNAT, link-local, ULA, 0.0.0.0/8 ve çok noktaya yayın adresleri.</summary>
    public static bool IsPrivate(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6None)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;
        var b = ip.GetAddressBytes();
        return b[0] is 0 or 10 or 127 || b[0] >= 224 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] is >= 64 and <= 127);
    }

    /// <summary>
    /// Bağlantı anında çözülen IP'yi denetleyen istemci: ad dışarıda bir adrese çözülse bile
    /// gönderim anında iç ağa çözülürse bağlanılmaz. Yönlendirme izlenmez.
    /// </summary>
    public static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = async (ctx, ct) =>
        {
            var host = ctx.DnsEndPoint.Host;
            var addrs = IPAddress.TryParse(host.Trim('[', ']'), out var lit) ? new[] { lit } : await Dns.GetHostAddressesAsync(host, ct);
            if (!IsAllowedHost(host)) addrs = addrs.Where(a => !IsPrivate(a)).ToArray();
            if (addrs.Length == 0) throw new HttpRequestException("Hedef iç ağ adresine çözülüyor; webhook gönderilmedi.");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addrs, ctx.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    })
    { Timeout = TimeSpan.FromSeconds(5) };

    /* ------------------------------------------------------------------ REST hook (açık API) */

    /// <summary>
    /// HR360'ın kendi altyapı ana bilgisayar adları (docker-compose.yml servis adları). Kiracının REST hook
    /// aboneliği bunlara gidemez: aksi halde API anahtarı olan biri olay yükünü iç servislere (postgres,
    /// keycloak, iç uçlar...) POST ettirebilirdi. Test alıcısı chatmock bir HR360 servisi değildir, listede yok.
    /// Ayrıca: "-service" ile biten adlar, "hr360-" ile başlayan konteyner/ağ adları, ".hr360-net" soneki.
    /// </summary>
    public static readonly IReadOnlyCollection<string> InfraHosts = new[]
    {
        "postgres", "redis", "valkey", "minio", "minio-init", "kafka", "kafka-init", "mailpit", "mailer", "keycloak",
        "organization-service", "employee-service", "workflow-service", "leave-service", "recruitment-service",
        "onboarding-service", "timeshift-service", "performance-service", "learning-service", "engagement-service",
        "governance-service", "compensation-service", "expense-service", "notification-service", "tenant-service",
        "mlflow", "ml-inference", "web", "prometheus", "alertmanager", "postgres-exporter", "node-exporter", "blackbox-exporter", "loki",
        "promtail", "grafana", "otel-collector", "tempo", "certbot", "gateway", "openldap",
    };

    private static readonly HashSet<string> InfraHostSet = new(InfraHosts, StringComparer.OrdinalIgnoreCase);

    /// <summary>Bulut meta veri servislerinin ad ve adresleri (kimlik bilgisi sızdırma hedefi).</summary>
    private static readonly HashSet<string> MetadataHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "metadata", "metadata.google.internal", "metadata.goog", "instance-data", "instance-data.ec2.internal",
        "metadata.azure.com", "metadata.tencentyun.com",
    };

    private static readonly IPAddress[] MetadataIps =
    {
        IPAddress.Parse("100.100.100.200"), // Alibaba Cloud
        IPAddress.Parse("fd00:ec2::254"),   // AWS IPv6
    };

    /// <summary>Ad HR360 altyapısına ya da yerel makineye/meta veri servisine mi işaret ediyor?</summary>
    public static bool IsBlockedRestHookHost(string host)
    {
        var h = host.Trim().Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        if (h.Length == 0) return true;
        if (h == "localhost" || h.EndsWith(".localhost", StringComparison.Ordinal)) return true;
        if (MetadataHosts.Contains(h)) return true;
        if (h.EndsWith(".hr360-net", StringComparison.Ordinal)) h = h[..^".hr360-net".Length];
        if (InfraHostSet.Contains(h)) return true;
        if (!h.Contains('.') && (h.EndsWith("-service", StringComparison.Ordinal) || h.StartsWith("hr360-", StringComparison.Ordinal)
            || h.StartsWith("hr360_", StringComparison.Ordinal))) return true;
        return false;
    }

    /// <summary>Loopback, 0.0.0.0/8 / ::, link-local (169.254/16, fe80::/10 — bulut meta veri dahil), çok noktaya yayın.
    /// Özel ağlar (10/8, 172.16/12, 192.168/16, ULA) SERBEST: kiracının kendi iç ağı.</summary>
    public static bool IsBlockedRestHookIp(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6None)) return true;
        if (MetadataIps.Any(m => m.Equals(ip))) return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6) return ip.IsIPv6LinkLocal || ip.IsIPv6Multicast;
        var b = ip.GetAddressBytes();
        return b[0] is 0 or 127 || b[0] >= 224 || (b[0] == 169 && b[1] == 254);
    }

    /// <summary>REST hook hedefi: geçerliyse null, değilse kullanıcıya gösterilecek hata. http ve https kabul edilir.</summary>
    public static string? ValidateRestHook(string? url)
    {
        if (IsSelfInbox(url)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
            return "target_url geçerli bir http(s) adresi olmalı.";
        var host = u.Host.Trim('[', ']');
        if (IPAddress.TryParse(host, out var ip) ? IsBlockedRestHookIp(ip) : IsBlockedRestHookHost(host))
            return "REST hook yerel makineye, bulut meta veri servisine ya da HR360 altyapı servislerine yönlendirilemez.";
        return null;
    }

    private static readonly SemaphoreSlim InfraLock = new(1, 1);
    private static HashSet<IPAddress> _infraIps = new();
    private static DateTime _infraIpsAt = DateTime.MinValue;

    /// <summary>
    /// HR360 altyapı adlarının şu an çözüldüğü IP'ler (1 dk önbellek) ve bu konteynerin kendi adresleri.
    /// Docker alt ağının tamamı engellenmez: aynı ağdaki kiracı dışı yardımcılar (ör. test alıcısı) serbest kalır.
    /// </summary>
    private static async Task<HashSet<IPAddress>> InfraIpsAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _infraIpsAt < TimeSpan.FromMinutes(1)) return _infraIps;
        await InfraLock.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow - _infraIpsAt < TimeSpan.FromMinutes(1)) return _infraIps;
            var set = new HashSet<IPAddress>();
            var lookups = InfraHosts.Select(async h =>
            {
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(2));
                    return await Dns.GetHostAddressesAsync(h, cts.Token);
                }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException) { return Array.Empty<IPAddress>(); }
            });
            foreach (var addrs in await Task.WhenAll(lookups))
                foreach (var a in addrs) set.Add(a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a);
            try
            {
                foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                    foreach (var ua in nic.GetIPProperties().UnicastAddresses) set.Add(ua.Address);
            }
            catch (System.Net.NetworkInformation.NetworkInformationException) { }
            _infraIps = set;
            _infraIpsAt = DateTime.UtcNow;
            return set;
        }
        finally { InfraLock.Release(); }
    }

    /// <summary>
    /// REST hook gönderim istemcisi: bağlantı anında çözülen adres yerel/meta veri adresi ya da bir HR360
    /// altyapı servisinin adresiyse bağlanılmaz (DNS rebinding dahil). Kiracının özel ağ adresleri serbest.
    /// </summary>
    public static readonly HttpClient RestHookClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = async (ctx, ct) =>
        {
            var host = ctx.DnsEndPoint.Host;
            var isLiteral = IPAddress.TryParse(host.Trim('[', ']'), out var lit);
            if (!isLiteral && IsBlockedRestHookHost(host))
                throw new HttpRequestException("Hedef HR360 altyapısına işaret ediyor; REST hook gönderilmedi.");
            var addrs = isLiteral ? new[] { lit! } : await Dns.GetHostAddressesAsync(host, ct);
            var infra = await InfraIpsAsync(ct);
            addrs = addrs.Where(a => !IsBlockedRestHookIp(a) && !infra.Contains(a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a)).ToArray();
            if (addrs.Length == 0) throw new HttpRequestException("Hedef yerel ya da HR360 altyapı adresine çözülüyor; REST hook gönderilmedi.");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addrs, ctx.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    })
    { Timeout = TimeSpan.FromSeconds(5) };
}

using System.Net;
using System.Net.Sockets;
using Novell.Directory.Ldap;

namespace TenantService.Directory;

/// <summary>Dogrulanmis LDAP baglanti hedefi.</summary>
public sealed record LdapTarget(bool Secure, string Host, int Port, string? Warning, IPAddress? PinnedAddress);

public static class LdapUrl
{
    public static readonly int[] AllowedPorts = { 389, 636, 3268, 3269 };

    /// <summary>
    /// ldap(s)://host[:port] bicimini dogrular (yol/sorgu kabul edilmez). Test ortaminda
    /// LDAP_ALLOW_PRIVATE_HOSTS listesindeki adlar icin port kisiti uygulanmaz.
    /// </summary>
    public static (bool Secure, string Host, int Port, string? Warning, string? Error) Parse(string? url, IReadOnlyCollection<string> allowPrivateHosts)
    {
        if (string.IsNullOrWhiteSpace(url)) return (false, "", 0, null, "LDAP adresi zorunlu");
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return (false, "", 0, null, "LDAP adresi ldaps://sunucu:636 biçiminde olmalı");
        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme is not ("ldap" or "ldaps")) return (false, "", 0, null, "Adres ldaps:// (önerilen) ya da ldap:// ile başlamalı");
        if (!string.IsNullOrEmpty(uri.UserInfo) || (uri.AbsolutePath is not ("" or "/")) || !string.IsNullOrEmpty(uri.Query))
            return (false, "", 0, null, "LDAP adresinde yalnızca sunucu ve port olabilir");
        var host = uri.IdnHost.ToLowerInvariant();
        var secure = scheme == "ldaps";
        var port = uri.IsDefaultPort || uri.Port <= 0 ? (secure ? 636 : 389) : uri.Port;
        var testHost = allowPrivateHosts.Contains(host, StringComparer.OrdinalIgnoreCase);
        if (!testHost && !AllowedPorts.Contains(port))
            return (false, "", 0, null, "LDAP portu 389, 636, 3268 ya da 3269 olmalı");
        var warning = secure ? null
            : "UYARI: ldap:// şifresiz bağlantıdır; bağlama parolası ve kişisel veriler ağda açık metin taşınır. ldaps:// kullanın.";
        return (secure, host, port, warning, null);
    }

    public static IReadOnlyList<string> AllowPrivateHostsFromEnv() =>
        (Environment.GetEnvironmentVariable("LDAP_ALLOW_PRIVATE_HOSTS") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static bool IsPrivate(IPAddress ip)
    {
        var a = ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
        if (IPAddress.IsLoopback(a) || a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6UniqueLocal || a.Equals(IPAddress.IPv6Any)) return true;
        if (a.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = a.GetAddressBytes();
        return b[0] == 10 || b[0] == 127 || b[0] == 0
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
            || b[0] >= 224;
    }

    /// <summary>
    /// GUVENLIK (SSRF): kiraci yoneticisi LDAP adresi olarak ic agdaki servisleri (postgres,
    /// keycloak, minio...) gosterememeli. Ad cozulur; ozel/ic adres reddedilir (test icin izinli
    /// adlar haric). ldap:// (TLS'siz) baglantida DNS yeniden baglama (rebinding) riskine karsi
    /// dogrulanan adrese baglanilir; ldaps:// icin sertifika ad dogrulamasi bu riski kapatir.
    /// </summary>
    public static async Task<(LdapTarget? Target, string? Error)> ResolveAsync(string? url, CancellationToken ct)
    {
        var allow = AllowPrivateHostsFromEnv();
        var (secure, host, port, warning, error) = Parse(url, allow);
        if (error is not null) return (null, error);
        IPAddress[] addrs;
        try { addrs = IPAddress.TryParse(host, out var lit) ? new[] { lit } : await Dns.GetHostAddressesAsync(host, ct); }
        catch (Exception) { return (null, "LDAP sunucusunun adı çözümlenemedi"); }
        if (addrs.Length == 0) return (null, "LDAP sunucusunun adı çözümlenemedi");
        var testHost = allow.Contains(host, StringComparer.OrdinalIgnoreCase);
        if (!testHost && addrs.Any(IsPrivate)) return (null, "LDAP sunucusu iç ağ adresine işaret edemez");
        var pinned = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addrs[0];
        return (new LdapTarget(secure, host, port, warning, secure ? null : pinned), null);
    }
}

/// <summary>Novell.Directory.Ldap ile salt-okunur dizin aramasi.</summary>
public sealed class LdapDirectoryReader
{
    private readonly ILogger<LdapDirectoryReader> _log;
    public LdapDirectoryReader(ILogger<LdapDirectoryReader> log) => _log = log;

    public static int MaxEntries =>
        int.TryParse(Environment.GetEnvironmentVariable("LDAP_SYNC_MAX_ENTRIES"), out var n) && n > 0 ? n : 5000;

    public sealed class LdapReadException : Exception
    {
        public LdapReadException(string message) : base(message) { }
    }

    public async Task<List<LdapEntry>> SearchAsync(
        LdapTarget target, string bindDn, string bindPassword, string baseDn, string filter, string[] attributes, CancellationToken ct)
    {
        var options = new LdapConnectionOptions();
        if (target.Secure) options.UseSsl();
        using var conn = new LdapConnection(options) { ConnectionTimeout = 10_000 };
        try
        {
            await conn.ConnectAsync(target.PinnedAddress?.ToString() ?? target.Host, target.Port, ct);
            await conn.BindAsync(LdapConnection.LdapV3, bindDn, bindPassword, ct);
            var constraints = new LdapSearchConstraints
            {
                MaxResults = MaxEntries,
                TimeLimit = 30_000,
                ServerTimeLimit = 30,
                ReferralFollowing = false,
            };
            var results = await conn.SearchAsync(baseDn, LdapConnection.ScopeSub, filter, attributes, false, constraints, ct);
            var list = new List<LdapEntry>();
            while (await results.HasMoreAsync(ct))
            {
                LdapEntry? mapped = null;
                try
                {
                    var entry = await results.NextAsync(ct);
                    var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var attr in entry.GetAttributeSet())
                    {
                        if (!attributes.Contains(attr.Name, StringComparer.OrdinalIgnoreCase)) continue;
                        dict[attr.Name] = attr.Name.Equals("objectGUID", StringComparison.OrdinalIgnoreCase)
                            ? new Guid(attr.ByteValue).ToString()
                            : attr.StringValue;
                    }
                    mapped = new LdapEntry(entry.Dn, dict);
                }
                catch (LdapReferralException) { /* yonlendirmeler izlenmez */ }
                if (mapped is not null) list.Add(mapped);
                if (list.Count >= MaxEntries) break;
            }
            return list;
        }
        catch (LdapException ex)
        {
            _log.LogWarning("LDAP hatasi ({Code}): {Message}", ex.ResultCode, ex.Message);
            throw new LdapReadException(ex.ResultCode switch
            {
                LdapException.InvalidCredentials => "LDAP bağlama kimlik bilgileri geçersiz",
                LdapException.NoSuchObject => "Base DN dizinde bulunamadı",
                LdapException.FilterError => "LDAP filtresi geçersiz",
                LdapException.SizeLimitExceeded => $"Dizin {MaxEntries} kayıttan fazlasını döndürdü; filtreyi daraltın",
                LdapException.ConnectError or LdapException.ServerDown => "LDAP sunucusuna bağlanılamadı",
                _ => $"LDAP hatası: {ex.ResultCodeToString()}",
            });
        }
        catch (Exception ex) when (ex is SocketException or IOException or TimeoutException or System.Security.Authentication.AuthenticationException)
        {
            _log.LogWarning("LDAP baglanti hatasi: {Message}", ex.Message);
            throw new LdapReadException(ex is System.Security.Authentication.AuthenticationException
                ? "LDAPS sertifikası doğrulanamadı"
                : "LDAP sunucusuna bağlanılamadı");
        }
    }

    /// <summary>Filtre ve DN icin kaba bicim kontrolu (sunucu da ayrica dogrular).</summary>
    public static string? ValidateFilterAndDn(string? baseDn, string? filter, string? bindDn)
    {
        if (string.IsNullOrWhiteSpace(baseDn) || baseDn.Length > 512 || !baseDn.Contains('=')) return "Base DN geçerli değil (örn. ou=people,dc=sirket,dc=com)";
        if (string.IsNullOrWhiteSpace(bindDn) || bindDn.Length > 512) return "Bağlama DN'i zorunlu";
        if (string.IsNullOrWhiteSpace(filter)) return null;
        var f = filter.Trim();
        if (f.Length > 512 || !f.StartsWith('(') || !f.EndsWith(')')) return "LDAP filtresi parantez içinde olmalı, örn. (objectClass=person)";
        var depth = 0;
        foreach (var c in f)
        {
            if (c == '(') depth++;
            else if (c == ')' && --depth < 0) return "LDAP filtresinde parantezler dengesiz";
        }
        return depth == 0 ? null : "LDAP filtresinde parantezler dengesiz";
    }
}

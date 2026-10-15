using System.Net;

namespace TenantService.Tenancy;

/// <summary>IP izin listesi yardımcıları (diğer servislerdeki TenantStatusGate ile aynı kurallar).</summary>
public static class TenantStatusGate
{
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
}

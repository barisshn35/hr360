using System.Collections.Concurrent;

namespace EngagementService.Infrastructure;

/// <summary>
/// G20 alan düzeyinde yetki. Kiracı her profil alanının başkalarına hangi düzeyden
/// itibaren görüneceğini belirler (governance_field_policies; KVKK ekranından yönetilir).
/// Düzeyler: everyone (tüm çalışanlar) &lt; manager (bölüm yöneticisi) &lt; hr &lt; self (yalnızca kendisi).
/// Kişi kendi alanlarını her zaman görür. 30 sn önbellek.
/// </summary>
public static class FieldPolicies
{
    public static readonly Dictionary<string, string> Defaults = new()
    {
        ["bio"] = "everyone", ["pronouns"] = "everyone", ["skills"] = "everyone", ["interests"] = "everyone", ["linkedInUrl"] = "everyone",
        ["birthDate"] = "hr", ["address"] = "hr", ["emergencyContact"] = "hr", ["iban"] = "hr", ["nationalId"] = "hr",
    };

    public static readonly string[] Levels = { "everyone", "manager", "hr", "self" };

    private static readonly ConcurrentDictionary<string, (Dictionary<string, string> Map, DateTime At)> Cache = new();

    public static async Task<Dictionary<string, string>> ForTenantAsync(Sql sql, string tenant, CancellationToken ct)
    {
        if (Cache.TryGetValue(tenant, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromSeconds(30)) return hit.Map;
        var map = new Dictionary<string, string>(Defaults);
        try
        {
            var rows = await sql.QueryAsync("""SELECT "Field", "MinLevel" FROM governance_field_policies WHERE "TenantSlug" = $1""",
                r => (r.GetString(0), r.GetString(1)), ct, tenant);
            foreach (var (f, l) in rows)
                if (map.ContainsKey(f) && Levels.Contains(l)) map[f] = l;
        }
        catch (Npgsql.PostgresException) { /* tablo henüz yoksa varsayılanlar */ }
        Cache[tenant] = (map, DateTime.UtcNow);
        return map;
    }

    /// <summary>viewer: self | hr | manager | other</summary>
    public static bool CanSee(string fieldLevel, string viewer) => viewer switch
    {
        "self" => true,
        "hr" => fieldLevel is "everyone" or "manager" or "hr",
        "manager" => fieldLevel is "everyone" or "manager",
        _ => fieldLevel == "everyone",
    };
}

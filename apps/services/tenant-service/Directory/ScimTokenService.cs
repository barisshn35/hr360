using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TenantService.Data;
using TenantService.Models;

namespace TenantService.Directory;

/// <summary>
/// Kiraci basina SCIM erisim jetonlari. Jeton yalnizca uretildigi anda bir kez gosterilir;
/// veritabaninda SHA-256 ozeti tutulur (yuksek entropili rastgele deger oldugu icin tuzsuz
/// ozet yeterli; sizan bir veritabani yedeginden jeton geri elde edilemez).
/// </summary>
public sealed class ScimTokenService
{
    public const string Prefix = "hr360scim_";
    public const int MaxActivePerTenant = 5;

    private readonly TenantDbContext _db;
    public ScimTokenService(TenantDbContext db) => _db = db;

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    public static string NewToken() =>
        Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Yeni jeton uretir; (kayit, acik jeton) doner. Acik jeton hicbir yerde saklanmaz.</summary>
    public async Task<(ScimToken Row, string Token)> CreateAsync(string tenantSlug, string name, string? createdBy, CancellationToken ct)
    {
        var token = NewToken();
        var row = new ScimToken
        {
            TenantSlug = tenantSlug,
            Name = name,
            TokenHash = Hash(token),
            TokenPrefix = token[..(Prefix.Length + 6)],
            CreatedBy = createdBy,
        };
        _db.ScimTokens.Add(row);
        await _db.SaveChangesAsync(ct);
        return (row, token);
    }

    /// <summary>
    /// Authorization basligindaki jetonu dogrular; gecerliyse jeton kaydi doner. Son kullanim
    /// zamani en fazla dakikada bir guncellenir (her istekte yazma yapilmasin).
    /// </summary>
    public async Task<ScimToken?> ValidateAsync(string? authorizationHeader, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader)) return null;
        const string bearer = "Bearer ";
        if (!authorizationHeader.StartsWith(bearer, StringComparison.OrdinalIgnoreCase)) return null;
        var token = authorizationHeader[bearer.Length..].Trim();
        if (!token.StartsWith(Prefix, StringComparison.Ordinal) || token.Length > 200) return null;
        var hash = Hash(token);
        var row = await _db.ScimTokens.FirstOrDefaultAsync(t => t.TokenHash == hash && t.RevokedAt == null, ct);
        if (row is null) return null;
        if (row.LastUsedAt is null || row.LastUsedAt < DateTimeOffset.UtcNow.AddMinutes(-1))
        {
            // Denetim kaydini sisirmemek icin dogrudan SQL (kullanim zamani denetlenecek bir is degisikligi degil).
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $@"UPDATE tenant_scim_tokens SET ""LastUsedAt"" = now() WHERE ""Id"" = {row.Id}", ct);
        }
        return row;
    }
}

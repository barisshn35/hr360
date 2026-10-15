namespace ExpenseService.Tenancy;

/// <summary>
/// Istek basina tenant kimligi. Middleware tarafindan JWT'den doldurulur,
/// DbContext global query filter'i tarafindan okunur.
/// </summary>
public interface ITenantContext
{
    /// <summary>Tenant slug'i (JWT'deki organization claim'i).</summary>
    string? TenantSlug { get; }

    /// <summary>
    /// Platform yoneticisi mi? Oyleyse tenant filtresi UYGULANMAZ -
    /// tum tenant'larin verisini gorebilir.
    /// </summary>
    bool IsPlatformAdmin { get; }

    /// <summary>Tenant belirlenemedi mi? (kimlik dogrulanmamis istekler)</summary>
    bool IsResolved { get; }
}

public class TenantContext : ITenantContext
{
    public string? TenantSlug { get; set; }
    public bool IsPlatformAdmin { get; set; }
    public bool IsResolved => !string.IsNullOrWhiteSpace(TenantSlug) || IsPlatformAdmin;
}

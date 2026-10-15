namespace TenantService.Models;

/// <summary>
/// Guvenlik dalgasi 2A (break-glass): platform yoneticisinin bir kiracinin verisine sureli,
/// gerekceli erisim izni. Izin acikken servislerin kiraci kapisi (TenantStatusGate) platform
/// yoneticisinin isteklerini YALNIZCA bu kiraciyla sinirlar ve her erisimi audit_log'a yazar.
/// Kiracinin sirket yoneticileri bildirim alir ve Guvenlik ekraninda izinleri gorup kapatabilir.
/// </summary>
public class PlatformAccessGrant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string TenantSlug { get; set; }
    /// <summary>Platform yoneticisinin Keycloak kimligi (JWT sub).</summary>
    public required string GrantedToUserId { get; set; }
    public string? GrantedToName { get; set; }
    public required string Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedByUserId { get; set; }
    public string? RevokedByName { get; set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;
}

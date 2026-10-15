using OnboardingService.Tenancy;

namespace OnboardingService.Models;

public enum AssetType { Laptop, Phone, Monitor, AccessCard, Vehicle, Other }
public enum AssetStatus { Available, Assigned, Maintenance, Retired, Lost }

public class Asset : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string AssetTag { get; set; }
    public AssetType Type { get; set; }
    public string? Model { get; set; }
    public string? SerialNumber { get; set; }
    public AssetStatus Status { get; set; } = AssetStatus.Available;
    /// <summary>QR etiketi icin opak kod (kisisel veri icermez, tahmin edilemez). Bkz. AssetCodes.</summary>
    public string? QrCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<AssetAssignment> Assignments { get; set; } = new();
}

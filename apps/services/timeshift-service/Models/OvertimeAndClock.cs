using TimeShiftService.Tenancy;

namespace TimeShiftService.Models;

public enum OvertimeStatus { Pending, Approved, Rejected, Cancelled }

/// <summary>
/// Fazla mesai talebi (İş Kanunu m.41): yılda en fazla 270 saat. Onay akışı workflow-service'te
/// yürür; yalnızca onaylanan saatler bordroya girer. KVKK: gerekçede sağlık bilgisi istenmez.
/// </summary>
public class OvertimeRequest : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EmployeeId { get; set; }
    public DateOnly Date { get; set; }
    public decimal Hours { get; set; }
    public string? Reason { get; set; }
    public OvertimeStatus Status { get; set; } = OvertimeStatus.Pending;
    public Guid? WorkflowRequestId { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DecidedAt { get; set; }
}

/// <summary>
/// Giriş-çıkış noktası (ofis, şube, kapı). KVKK: biyometri kullanılmaz (Kurul kararı 2026/921).
/// Konum denetimi isteğe bağlıdır; koordinat yalnızca o anki istekte "noktada mı" hesabı için
/// kullanılır, saklanmaz ve kaydedilmez.
/// </summary>
public class TimeClockSite : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public bool AllowQr { get; set; } = true;
    public bool AllowTerminal { get; set; } = true;
    /// <summary>Web/mobilden giriş-çıkışta konum denetimi (isteğe bağlı).</summary>
    public bool CheckLocation { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public int RadiusMeters { get; set; } = 200;
    /// <summary>QR kodunun dakikalık imzası için gizli anahtar (kişisel veri değildir).</summary>
    public string QrSecret { get; set; } = "";
    /// <summary>Kart okuyucu / PIN terminali anahtarının SHA-256 özeti.</summary>
    public string? DeviceKeyHash { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Kişinin terminal kimlik bilgileri: kart numarası ve PIN yalnızca özet olarak tutulur.</summary>
public class TimeClockCredential : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EmployeeId { get; set; }
    /// <summary>Terminalde PIN ile birlikte girilen kısa sicil kodu (İK atar).</summary>
    public string? BadgeCode { get; set; }
    public string? CardHash { get; set; }
    public string? PinHash { get; set; }
    public int FailedPinAttempts { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum PunchKind { In, Out }

/// <summary>
/// Giriş-çıkış hareketi: zaman, yöntem, "noktada mı" ve uzaklık aralığı (ör. "50-100" m). Ham koordinat
/// yalnızca şirket ayarında açıksa ve saklama süresi kadar tutulur (RawExpiresAt geçince silinir).
/// </summary>
public class TimeClockPunch : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EmployeeId { get; set; }
    public Guid? SiteId { get; set; }
    public PunchKind Kind { get; set; }
    public TimeEntrySource Method { get; set; }
    public bool? OnSite { get; set; }
    /// <summary>Noktaya uzaklık aralığı (metre): "0-50", "50-100", "100-250", "250-500", "500-1000", "1000+".</summary>
    public string? DistanceBucket { get; set; }
    public double? RawLatitude { get; set; }
    public double? RawLongitude { get; set; }
    public DateTimeOffset? RawExpiresAt { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

using TimeShiftService.Tenancy;

namespace TimeShiftService.Models;

/// <summary>
/// G6: çalışanın vardiya tercihleri. Gün numaraları ISO (1 = Pazartesi ... 7 = Pazar);
/// vardiya türleri "Day" | "Night". Planlayıcı atama yaparken görür; uyuşmazlıklar uyarıdır, engel değil.
/// KVKK: gerekçe (Note) serbest metindir — sağlık bilgisi istenmez.
/// </summary>
public class ShiftPreference : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EmployeeId { get; set; }
    public int[] PreferredDays { get; set; } = Array.Empty<int>();
    public int[] UnavailableDays { get; set; } = Array.Empty<int>();
    public string[] PreferredShiftTypes { get; set; } = Array.Empty<string>();
    public string[] AvoidShiftTypes { get; set; } = Array.Empty<string>();
    public int? MaxNightsPerWeek { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class SwapStatus
{
    public const string PendingPeer = "PendingPeer", PendingApproval = "PendingApproval", Approved = "Approved",
        Rejected = "Rejected", Declined = "Declined", Cancelled = "Cancelled";
}

/// <summary>
/// G6: vardiya takası. A kendi vardiyasını B'nin vardiyasıyla değiştirmek (ya da devretmek, hedef
/// atama boş) ister → B kabul eder → ekip planlayıcısı/yöneticisi onaylar → atamalar tek işlemde
/// değiştirilir. Kurallar: aynı ekip, çakışma yok, vardiyalar arası en az 11 saat dinlenme,
/// haftalık en fazla 45 saat (4857 s. İş Kanunu m.63; Çalışma Süreleri Yön. m.5).
/// </summary>
public class ShiftSwapRequest : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RequesterEmployeeId { get; set; }
    public Guid RequesterAssignmentId { get; set; }
    public Guid TargetEmployeeId { get; set; }
    public Guid? TargetAssignmentId { get; set; }
    public string Status { get; set; } = SwapStatus.PendingPeer;
    public string? Note { get; set; }
    public DateTimeOffset? PeerRespondedAt { get; set; }
    public string? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? RejectReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>G7: puantaj ayarları (kiracı başına): geç kalma toleransı ve vardiyasız varsayılan mesai.</summary>
public class TimesheetSettings : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public int LateGraceMinutes { get; set; } = 5;
    public TimeOnly DefaultStart { get; set; } = new(9, 0);
    public TimeOnly DefaultEnd { get; set; } = new(18, 0);
    public int DefaultBreakMinutes { get; set; } = 60;

    // Dalga 9 / madde 66: çalışma süresi kuralları (sektörel istisnalar için şirket değiştirebilir).
    /// <summary>İki vardiya arası en az dinlenme (Çalışma Süreleri Yön.: 11 saat).</summary>
    public decimal MinRestHours { get; set; } = 11;
    /// <summary>Haftalık en çok çalışma (İş K. m.63: 45 saat).</summary>
    public decimal WeeklyMaxHours { get; set; } = 45;
    /// <summary>Günlük en çok çalışma (İş K. m.63: 11 saat).</summary>
    public decimal DailyMaxHours { get; set; } = 11;
    /// <summary>Gece çalışması en çok (İş K. m.69: 7,5 saat).</summary>
    public decimal NightMaxHours { get; set; } = 7.5m;
    /// <summary>Ardışık çalışma günü üst sınırı (haftada en az bir gün dinlenme: 6).</summary>
    public int MaxConsecutiveDays { get; set; } = 6;

    // Madde 67: konum doğrulama. KVKK: varsayılan yalnızca "noktada mı + uzaklık aralığı".
    /// <summary>"Block" = nokta dışından giriş reddedilir; "Flag" = kaydedilir, noktada değil işaretlenir.</summary>
    public string GeoOutsidePolicy { get; set; } = "Block";
    /// <summary>Ham koordinat saklama (varsayılan kapalı; açıksa saklama süresiyle).</summary>
    public bool GeoStoreRaw { get; set; }
    public int GeoRawRetentionDays { get; set; } = 30;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Madde 72: puantaj dönemi (ay). "Closed" iken o ayın giriş-çıkış, puantaj düzeltmesi ve fazla mesai
/// talebi/kararı yapılamaz; bordro kapanmış dönemin onaylı fazla mesaisini okur. Kayıt yoksa dönem açıktır.
/// </summary>
public class TimesheetPeriod : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public int Year { get; set; }
    public int Month { get; set; }
    public string Status { get; set; } = "Open";
    public DateTimeOffset? ClosedAt { get; set; }
    public string? ClosedBy { get; set; }
    public DateTimeOffset? ReopenedAt { get; set; }
    public string? ReopenedBy { get; set; }
    public string? ReopenReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

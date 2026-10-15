using LeaveService.Tenancy;

namespace LeaveService.Models;

/// <summary>
/// Dalga 9: şirketin izin ayarları (tek satır). Satır yoksa varsayılanlar geçerlidir.
///  - DayHours: saatlik izinde gün = saat / günlük çalışma saati (boşsa LEAVE_DAY_HOURS, o da yoksa 7,5).
///  - ConflictWarnEnabled / ConflictThresholdPercent: aynı departmanda aynı günlerde izinli ya da izin
///    bekleyen oranı eşiği aşınca talep formunda ve onayda uyarı (madde 69). Engel değildir.
///  - CarryOverMaxDays: yıllık izin devrinde önerilen üst sınır (boş = sınırsız; madde 71).
/// </summary>
public class LeaveSettings : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public decimal? DayHours { get; set; }
    public bool ConflictWarnEnabled { get; set; } = true;
    public int ConflictThresholdPercent { get; set; } = 30;
    public decimal? CarryOverMaxDays { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

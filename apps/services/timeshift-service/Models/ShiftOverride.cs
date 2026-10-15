using TimeShiftService.Tenancy;

namespace TimeShiftService.Models;

public enum ShiftOverrideType { Leave, Holiday, Manual }

/// <summary>
/// Belirli bir calisan+tarih icin, HESAPLANAN (pattern'den turetilen) vardiyanin
/// YERINE GECEN istisna. Leave tipi, leave-service'ten gelen "leave.approved"
/// Kafka event'iyle OTOMATIK olusturulur (bkz. LeaveEventConsumer) - kullanici
/// elle bir sey yapmaz, izin onaylanan gun otomatik "izinli" gorunur.
/// </summary>
public class ShiftOverride : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EmployeeId { get; set; }
    public DateOnly Date { get; set; }
    public ShiftOverrideType Type { get; set; }
    public string? Note { get; set; }

    /// <summary>Sadece Manual override icin: o gun bu saatte calisilacak.
    /// Leave/Holiday'de her zaman null (o gun hic calisilmiyor).</summary>
    public TimeOnly? StartTime { get; set; }
    public TimeOnly? EndTime { get; set; }

    /// <summary>Leave override'lar LeaveEventConsumer tarafindan otomatik
    /// olusturulur/kaldirilir - elle silinmemeli/duzenlenmemeli. Manual/Holiday
    /// kullanici tarafindan CRUD uzerinden yonetilir.</summary>
    public bool IsSystemManaged { get; set; }

    /// <summary>Kısmi gün izni (yarım gün / saatlik): çalışan o gün kısmen çalışır; takvimde ayrı gösterilir.</summary>
    public bool IsPartial { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

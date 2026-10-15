using TimeShiftService.Tenancy;

namespace TimeShiftService.Models;

public enum PatternDayType { Day, Night, Off }

/// <summary>
/// Sirketin tanimladigi, tekrarlanan bir vardiya dongusu.
/// Ornek: "3 Gece / 3 Off / 3 Gunduz" - 9 gunluk bir dizi, PatternDays
/// listesindeki DayIndex sirasina gore. Dongu bitince bastan basa (donguyu
/// tamamlayan gunlerin toplami CycleLengthDays).
/// </summary>
public class ShiftPattern : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<ShiftPatternDay> Days { get; set; } = new();
}

/// <summary>
/// Bir desenin, DayIndex (0'dan baslayan) sirasindaki tek bir gunu.
/// Day/Night ise StartTime/EndTime dolu olmali; Off ise ikisi de null.
/// </summary>
public class ShiftPatternDay : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ShiftPatternId { get; set; }
    public ShiftPattern? ShiftPattern { get; set; }
    public int DayIndex { get; set; }
    public PatternDayType Type { get; set; }
    public TimeOnly? StartTime { get; set; }
    public TimeOnly? EndTime { get; set; }
}

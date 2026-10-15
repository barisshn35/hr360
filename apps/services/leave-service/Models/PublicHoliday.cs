using LeaveService.Tenancy;

namespace LeaveService.Models;

/// <summary>
/// Sirketin resmi tatil takvimindeki bir gun. Izin gunu hesabinda (is gunu) bu gunler
/// dusulur. Takvimi IK girer ya da "Türkiye tatillerini ekle" ile yuklenir (2025-2030 dini
/// bayramlar Diyanet takviminden gomulu; bkz. PublicHolidaysController).
/// IsHalfDay: arife gunleri ve 28 Ekim ogleden sonra tatil - izin hesabinda 0,5 gun sayilir.
/// </summary>
public class PublicHoliday : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateOnly Date { get; set; }
    public required string Name { get; set; }
    public bool IsHalfDay { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

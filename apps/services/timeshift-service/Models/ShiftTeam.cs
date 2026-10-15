using TimeShiftService.Tenancy;

namespace TimeShiftService.Models;

/// <summary>
/// Bir calisan grubu, belirli bir ShiftPattern'i belirli bir baslangic
/// tarihinden (AnchorDate) takip eder. Farkli ekipler AYNI deseni FARKLI
/// baslangic noktalariyla takip ederek surekli (7/24) kapsama saglayabilir
/// - ornegin A Ekibi gunduz baslarken B Ekibi gece baslar, sonra donerler.
/// </summary>
public class ShiftTeam : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public Guid ShiftPatternId { get; set; }
    public ShiftPattern? ShiftPattern { get; set; }

    /// <summary>Bu tarihte desenin 0. gunu baslar.</summary>
    public DateOnly AnchorDate { get; set; }

    public Guid? DepartmentId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<ShiftTeamMember> Members { get; set; } = new();
}

/// <summary>
/// Bir calisanin bir ekibe uyeligi. Rank, ekip icindeki hiyerarsik sirayi
/// belirler (KUCUK sayi = daha yuksek yetki/kidem - 1=Ekip Lideri gibi).
/// Vardiya listesi gorunumu uyeleri Rank'e gore siralar. Tag, isteğe bagli
/// aciklayici bir etiket (orn. "Ekip Lideri", "Kidemli Operator").
///
/// EffectiveTo NULL ise uyelik halen aktif. Bir calisan ekip degistirdiginde
/// eski kayit EffectiveTo ile kapatilir, gecmisi kaybolmaz.
/// </summary>
public class ShiftTeamMember : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ShiftTeamId { get; set; }
    public ShiftTeam? ShiftTeam { get; set; }
    public Guid EmployeeId { get; set; }
    public int Rank { get; set; } = 100;
    public string? Tag { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

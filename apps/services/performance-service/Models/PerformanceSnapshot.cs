using PerformanceService.Tenancy;

namespace PerformanceService.Models;

/// <summary>Anlik goruntunun neden alindigi.</summary>
public enum SnapshotSource
{
    /// <summary>Bir degerlendirme gonderildi, puan yeniden hesaplandi.</summary>
    ReviewSubmitted,
    /// <summary>Donem kapandi.</summary>
    CycleClosed,
    /// <summary>Zamanlanmis periyodik hesaplama.</summary>
    Scheduled,
    /// <summary>Elle tetiklendi.</summary>
    Manual,
}

/// <summary>
/// Bir calisanin belirli bir andaki hesaplanmis performans puani.
///
/// NEDEN GEREKLI: Degerlendirmeler doneme bagli (ceyreklik, yariyil).
/// Ceyreklik degerlendirmeden haftalik trend cikmaz. Puan her
/// degistiginde bir anlik goruntu yazarak gercek bir zaman serisi
/// olusturuyoruz; haftalik/aylik/3-6 aylik gorunumler bunun uzerinden
/// hesaplaniyor.
///
/// Puan HESAPLANDIGI ANDAKI ayarla saklanir (ConfigVersion). Ayar sonradan
/// degisse bile gecmis nokta degismez - trend grafigi tutarli kalir.
/// </summary>
public class PerformanceSnapshot : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid EmployeeId { get; set; }

    /// <summary>Hangi donemin puani. Bos ise donemden bagimsiz anlik hesap.</summary>
    public Guid? CycleId { get; set; }

    /// <summary>
    /// Anlik goruntu alindiginda calisanin bagli oldugu ekip.
    /// Denormalize tutuluyor: kisi ekip degistirse bile gecmis noktalar
    /// o zamanki ekiple karsilastirilabilsin.
    /// </summary>
    public Guid? TeamId { get; set; }

    public Guid? DepartmentId { get; set; }

    public decimal Score { get; set; }
    public decimal GoalScore { get; set; }
    public decimal MetricScore { get; set; }

    /// <summary>Yeterli/gecerli degerlendirme yoksa true. Trendde zayif nokta olarak isaretlenir.</summary>
    public bool IsProvisional { get; set; }
    public string? ProvisionalReason { get; set; }

    public int ReviewCount { get; set; }

    /// <summary>Hesaplamada kullanilan ScoringConfig surumu.</summary>
    public int ConfigVersion { get; set; }

    /// <summary>
    /// Kategori bazli dokum, JSON olarak. Ayri tablo yerine burada:
    /// trend sorgulari zaten satiri okuyor, ek join maliyeti olmasin.
    /// </summary>
    public string? CategoryBreakdownJson { get; set; }

    public SnapshotSource Source { get; set; } = SnapshotSource.ReviewSubmitted;
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
}

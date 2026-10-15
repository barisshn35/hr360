using PerformanceService.Tenancy;

namespace PerformanceService.Models;

/// <summary>Metrigin hangi olcekle puanlandigi. Normalizasyon buna gore yapilir.</summary>
public enum MetricScale
{
    /// <summary>1-5 arasi (en yaygin).</summary>
    OneToFive,
    /// <summary>1-10 arasi.</summary>
    OneToTen,
    /// <summary>0-100 yuzde.</summary>
    Percentage,
}

/// <summary>
/// Metrigin turu. Ayni sirket icinde farkli kategorilere farkli agirlik
/// verilebilsin diye ayri tutuluyor (bkz. MetricCategoryWeight).
/// </summary>
public enum MetricCategory
{
    /// <summary>Teknik yetkinlik, uzmanlik.</summary>
    Technical,
    /// <summary>Davranissal: iletisim, takim calismasi, sahiplenme.</summary>
    Behavioral,
    /// <summary>Liderlik, yonlendirme, mentorluk.</summary>
    Leadership,
    /// <summary>Teslimat: zamaninda bitirme, kalite, guvenilirlik.</summary>
    Delivery,
    /// <summary>Sirkete ozel, yukaridakilere girmeyen.</summary>
    Custom,
}

/// <summary>
/// Bir sirketin (kiracinin) kendi tanimladigi degerlendirme metrigi.
///
/// Onceki surumde tek bir OverallScore alani vardi; her sirket ayni sekilde
/// degerlendirmek zorundaydi. Artik her kiraci kendi metodolojisini kurar:
/// metrikleri, olceklerini ve agirliklarini kendisi belirler.
/// </summary>
public class MetricDefinition : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Kisa kod: "teknik-yetkinlik". API ve raporlarda sabit referans.</summary>
    public required string Code { get; set; }

    public required string Name { get; set; }
    public string? Description { get; set; }

    public MetricCategory Category { get; set; } = MetricCategory.Custom;
    public MetricScale Scale { get; set; } = MetricScale.OneToFive;

    /// <summary>
    /// Kategori icindeki goreli agirlik. Mutlak yuzde DEGIL - hesaplama
    /// sirasinda ayni kategorideki metriklerin agirliklari toplamina
    /// bolunerek normalize edilir. Boylece yonetici metrik ekleyip
    /// cikardiginda diger agirliklari elle duzeltmek zorunda kalmaz.
    /// </summary>
    public decimal Weight { get; set; } = 1m;

    /// <summary>
    /// Yalnizca belirli bir departman icin gecerliyse doldurulur.
    /// Bos ise sirket genelinde uygulanir. (Ornek: "Kod Kalitesi" yalnizca
    /// muhendislik departmaninda anlamli.)
    /// </summary>
    public Guid? DepartmentId { get; set; }

    /// <summary>Puanlamanin zorunlu olup olmadigi. Zorunlu degilse degerlendirici atlayabilir.</summary>
    public bool IsRequired { get; set; } = true;

    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ArchivedAt { get; set; }

    /// <summary>Puani 0-100 araligina tasir. Farkli olcekli metrikler ancak boyle toplanabilir.</summary>
    public decimal Normalize(decimal raw) => Scale switch
    {
        MetricScale.OneToFive => (Math.Clamp(raw, 1m, 5m) - 1m) / 4m * 100m,
        MetricScale.OneToTen => (Math.Clamp(raw, 1m, 10m) - 1m) / 9m * 100m,
        MetricScale.Percentage => Math.Clamp(raw, 0m, 100m),
        _ => 0m,
    };

    /// <summary>Olcegin gecerli araligi - dogrulama ve arayuz icin.</summary>
    public (decimal Min, decimal Max) Range => Scale switch
    {
        MetricScale.OneToFive => (1m, 5m),
        MetricScale.OneToTen => (1m, 10m),
        MetricScale.Percentage => (0m, 100m),
        _ => (0m, 0m),
    };
}

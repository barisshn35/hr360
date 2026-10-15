using PerformanceService.Tenancy;

namespace PerformanceService.Models;

/// <summary>
/// Bir kiracinin puanlama metodolojisi. Surumlenir: gecmis donemlerin
/// puanlari, o donemde gecerli olan ayarla hesaplanmis kalir - ayar
/// degistiginde eski sonuclar degismez.
/// </summary>
public class ScoringConfig : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();

    public int Version { get; set; } = 1;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset EffectiveFrom { get; set; } = DateTimeOffset.UtcNow;

    // --- Ana dagilim: hedefler mi, yetkinlikler mi agir basar? ---

    /// <summary>Nihai puanda hedef gerceklesmesinin payi (%).</summary>
    public int GoalWeightPercent { get; set; } = 40;

    /// <summary>Nihai puanda metrik (yetkinlik) puanlarinin payi (%).</summary>
    public int MetricWeightPercent { get; set; } = 60;

    // --- Kategori agirliklari (metrik payinin kendi icinde dagilimi) ---
    // Sifir birakilan kategori hesaba girmez. Toplamlari 100 olmak
    // zorunda degil - oransal olarak normalize edilir.

    public decimal TechnicalWeight { get; set; } = 1m;
    public decimal BehavioralWeight { get; set; } = 1m;
    public decimal LeadershipWeight { get; set; } = 0m;
    public decimal DeliveryWeight { get; set; } = 1m;
    public decimal CustomWeight { get; set; } = 1m;

    // --- Degerlendirici tipi katsayilari ---
    // 360 derece degerlendirmede her kaynagin agirligi ayni olmamali.
    // Varsayilanlar sektor pratigine yakin: yonetici en agir, oz
    // degerlendirme en hafif.

    public decimal SelfReviewWeight { get; set; } = 0.5m;
    public decimal ManagerReviewWeight { get; set; } = 2.0m;
    public decimal TeamLeadReviewWeight { get; set; } = 1.5m;
    public decimal PeerReviewWeight { get; set; } = 1.0m;
    public decimal UpwardReviewWeight { get; set; } = 1.0m;

    // --- Gecerlilik kurallari ---

    /// <summary>
    /// Puanin "gecerli" sayilmasi icin gereken en az degerlendirme sayisi.
    /// Altinda kalirsa puan hesaplanir ama IsProvisional=true isaretlenir;
    /// arayuz ve ML bunu dusuk guvenilirlikli sayar.
    /// </summary>
    public int MinReviewsForValidScore { get; set; } = 2;

    /// <summary>
    /// Oz degerlendirme tek basina puani belirleyebilsin mi? Kapaliysa
    /// yalnizca oz degerlendirme varsa puan gecersiz sayilir - kisinin
    /// kendine 5 verip yuksek puan almasini engeller.
    /// </summary>
    public bool AllowSelfOnlyScore { get; set; }

    // --- Aksiyon esikleri (ML onerileri bunlari kullanir) ---

    /// <summary>Terfi adayi esigi (0-100). Ustundekiler degerlendirmeye alinir.</summary>
    public decimal PromotionThreshold { get; set; } = 85m;

    /// <summary>Takdir esigi (0-100).</summary>
    public decimal RecognitionThreshold { get; set; } = 75m;

    /// <summary>Gelisim plani esigi - altina dusenler icin plan onerilir.</summary>
    public decimal ImprovementThreshold { get; set; } = 55m;

    /// <summary>Acil aksiyon esigi.</summary>
    public decimal CriticalThreshold { get; set; } = 40m;

    /// <summary>
    /// Terfi onerisi icin ust uste kac donem esigin ustunde kalinmali.
    /// Tek donemlik parlamayi terfi sebebi saymamak icin.
    /// </summary>
    public int PromotionConsecutivePeriods { get; set; } = 2;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? CreatedByEmployeeId { get; set; }

    /// <summary>Kategori agirligini enum'dan okur.</summary>
    public decimal WeightFor(MetricCategory c) => c switch
    {
        MetricCategory.Technical => TechnicalWeight,
        MetricCategory.Behavioral => BehavioralWeight,
        MetricCategory.Leadership => LeadershipWeight,
        MetricCategory.Delivery => DeliveryWeight,
        MetricCategory.Custom => CustomWeight,
        _ => 0m,
    };

    /// <summary>Degerlendirici tipinin katsayisini okur.</summary>
    public decimal WeightFor(ReviewType t) => t switch
    {
        ReviewType.Self => SelfReviewWeight,
        ReviewType.Manager => ManagerReviewWeight,
        ReviewType.TeamLead => TeamLeadReviewWeight,
        ReviewType.Peer => PeerReviewWeight,
        ReviewType.Upward => UpwardReviewWeight,
        _ => 1m,
    };
}

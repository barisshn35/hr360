using PerformanceService.Tenancy;

namespace PerformanceService.Models;

/// <summary>
/// Geri bildirimin ne icin verildigi. Serbest metin tek basina birakilsa
/// raporlanamaz; yapisal sebep sayesinde "bu ekipte en cok hangi konuda
/// geri bildirim veriliyor" gibi sorular cevaplanabilir.
/// </summary>
public enum FeedbackReason
{
    /// <summary>Takdir, iyi is.</summary>
    Recognition,
    /// <summary>Hedef ilerlemesi uzerine.</summary>
    GoalProgress,
    /// <summary>Gelistirilmesi gereken bir davranis/yetkinlik.</summary>
    Improvement,
    /// <summary>Yonlendirme, mentorluk.</summary>
    Coaching,
    /// <summary>Belirli bir olay uzerine (olumlu ya da olumsuz).</summary>
    Incident,
    /// <summary>Akran gozlemi.</summary>
    PeerObservation,
    /// <summary>Donem degerlendirmesi kapanisi.</summary>
    ReviewSummary,
    Other,
}

/// <summary>Geri bildirimin tonu - raporlamada olumlu/olumsuz ayrimi icin.</summary>
public enum FeedbackSentiment
{
    Positive,
    Neutral,
    Constructive,
}

/// <summary>
/// Bir calisana yazilan geri bildirim.
///
/// Calisan geri bildirimi KIMDEN geldigini gorur (bilincli tercih:
/// anonim geri bildirim hesap sorulabilirligi zayiflatir ve kotuye
/// kullanilabilir). Gizli tutulmasi gereken notlar icin
/// Visibility=ManagerOnly kullanilir - o zaman calisana hic gosterilmez.
/// </summary>
public class Feedback : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid FromEmployeeId { get; set; }
    public Guid ToEmployeeId { get; set; }

    /// <summary>Ilgili degerlendirme donemi. Bos ise donemden bagimsiz anlik geri bildirim.</summary>
    public Guid? CycleId { get; set; }

    /// <summary>Belirli bir metrige baglanabilir - "iletisim" hakkinda geri bildirim gibi.</summary>
    public Guid? MetricId { get; set; }

    /// <summary>NEDEN yazildi - yapisal sebep.</summary>
    public FeedbackReason Reason { get; set; } = FeedbackReason.Other;

    /// <summary>
    /// Sebebin aciklamasi. Reason kategoriyi verir, bu alan baglami:
    /// "Q3 lansmaninda kritik hatayi yayina cikmadan yakaladi" gibi.
    /// </summary>
    public string? ReasonDetail { get; set; }

    public FeedbackSentiment Sentiment { get; set; } = FeedbackSentiment.Neutral;

    /// <summary>Geri bildirimin kendisi.</summary>
    public required string Body { get; set; }

    /// <summary>Calisan gorebilsin mi? False ise yalnizca yoneticiler gorur.</summary>
    public bool VisibleToEmployee { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReadAt { get; set; }
}

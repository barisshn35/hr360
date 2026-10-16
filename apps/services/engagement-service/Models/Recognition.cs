using EngagementService.Tenancy;

namespace EngagementService.Models;

/* ======================================================================
 * Takdir puanları ve ödül kataloğu (yenilik haritası #25). Kiracı başına
 * isteğe bağlı; kapalıyken takdir duvarı eskisi gibi puansız çalışır.
 * Bakiye, hareket defterinin toplamıdır (PointsEntry).
 * ==================================================================== */

public class RecognitionSettings : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    public bool Enabled { get; set; }
    /// <summary>Bir çalışanın bir takvim ayında verebileceği toplam puan.</summary>
    public int MonthlyAllowance { get; set; } = 100;
    /// <summary>Tek takdirde verilebilecek en fazla puan.</summary>
    public int MaxPerKudos { get; set; } = 50;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class PointsEntry : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    public Guid EmployeeId { get; set; }
    public int Delta { get; set; }
    /// <summary>Kudos | KudosReversed | Redeem | Refund | Grant</summary>
    public string Reason { get; set; } = "";
    public Guid? RefId { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Reward : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public int Cost { get; set; }
    /// <summary>Kalan adet; null ise sınırsız.</summary>
    public int? Stock { get; set; }
    public bool Active { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class RewardRedemption : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    public Guid RewardId { get; set; }
    public string RewardTitle { get; set; } = "";
    public Guid EmployeeId { get; set; }
    public string Name { get; set; } = "";
    public int Cost { get; set; }
    /// <summary>Pending | Fulfilled | Rejected</summary>
    public string Status { get; set; } = "Pending";
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DecidedAt { get; set; }
}

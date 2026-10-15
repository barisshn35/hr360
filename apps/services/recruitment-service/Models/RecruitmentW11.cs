using RecruitmentService.Tenancy;

namespace RecruitmentService.Models;

/// <summary>Dalga 11: kiracının işe alım program ayarları (öneri ödülü, ilan yayın tercihleri).</summary>
public class RecruitmentProgramSettings : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool ReferralEnabled { get; set; } = true;
    public decimal? ReferralRewardAmount { get; set; }
    public string ReferralRewardCurrency { get; set; } = "TRY";
    public int ReferralProbationDays { get; set; } = 60;
    public string? ReferralRewardNote { get; set; }
    /// <summary>Google for Jobs çıktısında ücret aralığı yalnızca bu açıksa yayımlanır.</summary>
    public bool PublishSalaryInJobPostings { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Öneri ödülü durumu. Hak ediş otomatik hesaplanır; onay ve ödeme İK kararıdır (otomatik karar yok).</summary>
public enum ReferralRewardStatus { None, Waiting, Eligible, Approved, Paid, Forfeited, NotEligible }

/// <summary>Dalga 11 (73): çalışanın bir ilana aday önerisi. Aday kişisel verisi aday kaydında tutulur.</summary>
public class Referral : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobPostingId { get; set; }
    public Guid ReferrerEmployeeId { get; set; }
    public Guid? CandidateId { get; set; }
    public Guid? ApplicationId { get; set; }
    public string? Relationship { get; set; }
    public string? Note { get; set; }
    public bool CandidateConsentConfirmed { get; set; }
    public DateTimeOffset ConsentConfirmedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? NoticeSentAt { get; set; }
    public ReferralRewardStatus RewardStatus { get; set; } = ReferralRewardStatus.None;
    public DateTimeOffset? HiredAt { get; set; }
    public DateTimeOffset? RewardEligibleAt { get; set; }
    public decimal? RewardAmount { get; set; }
    public string? RewardCurrency { get; set; }
    public DateTimeOffset? RewardDecidedAt { get; set; }
    public string? RewardDecidedByUserId { get; set; }
    public string? RewardNote { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Dalga 11 (74): oturumsuz aday durum bağlantısı (yalnızca özet saklanır).</summary>
public class StatusLink : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ApplicationId { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public string TokenHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedBy { get; set; }
    public DateTimeOffset? LastViewedAt { get; set; }
    public int ViewCount { get; set; }
    public string? CreatedByUserId { get; set; }
}

/// <summary>Dalga 11 (78): başvuru aşama geçmişi (veritabanı tetikleyicisi yazar; servis yalnızca okur).</summary>
public class StageEvent : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; }
    public Guid ApplicationId { get; set; }
    public string? FromStatus { get; set; }
    public string ToStatus { get; set; } = "";
    public DateTimeOffset ChangedAt { get; set; }
}

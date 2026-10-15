using RecruitmentService.Tenancy;

namespace RecruitmentService.Models;

public enum ApplicationStatus
{
    Applied, Screening, Interview, Offer, Hired, Rejected, Withdrawn
}

public class Application : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobPostingId { get; set; }
    public JobPosting? JobPosting { get; set; }
    public Guid CandidateId { get; set; }
    public Candidate? Candidate { get; set; }
    public ApplicationStatus Status { get; set; } = ApplicationStatus.Applied;
    public string? Notes { get; set; }
    public DateTimeOffset AppliedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StatusChangedAt { get; set; }
    public List<Interview> Interviews { get; set; } = new();

    /// <summary>Manual (IK girdi) | Career (herkese acik kariyer sayfasi).</summary>
    public string Channel { get; set; } = "Manual";
    /// <summary>Adayin on yazisi (kariyer sayfasi).</summary>
    public string? CoverNote { get; set; }
    /// <summary>Aday oz-hizmet baglantisinin SHA-256 ozeti; baglantinin kendisi saklanmaz.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string? SelfServiceTokenHash { get; set; }
    /// <summary>Aday kaydi bu basvuruyla mi olustu (oz-hizmette kisisel verinin tamami gosterilir/silinir)?</summary>
    public bool OwnsCandidate { get; set; }
    public string? PrivacyNoticeVersion { get; set; }
    /// <summary>Mevcut adaya baglandiysa/olasi tekrar ise nedeni (IK'ya gosterilir).</summary>
    public string? DuplicateReason { get; set; }
}

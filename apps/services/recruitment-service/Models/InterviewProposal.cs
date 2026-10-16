using RecruitmentService.Tenancy;

namespace RecruitmentService.Models;

/// <summary>Adaya önerilen mülakat saatleri; aday tek kullanımlık bağlantıyla birini seçer.</summary>
public class InterviewProposal : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ApplicationId { get; set; }
    public string TokenHash { get; set; } = "";
    public InterviewType Type { get; set; }
    public int DurationMinutes { get; set; } = 60;
    public List<Guid> InterviewerIds { get; set; } = new();
    public string? Location { get; set; }
    public string? MeetingUrl { get; set; }
    public List<DateTimeOffset> Slots { get; set; } = new();
    /// <summary>Open | Booked | Cancelled</summary>
    public string Status { get; set; } = "Open";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ChosenAt { get; set; }
    public Guid? InterviewId { get; set; }
    public string? CreatedByName { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

using RecruitmentService.Tenancy;

namespace RecruitmentService.Models;

public enum InterviewType { Phone, Technical, HR, Final }
/// <summary>Cancelled: planlanan mülakat iptal edildi (metin olarak saklanır; şema değişmez).</summary>
public enum InterviewResult { Pending, Pass, Fail, NoShow, Cancelled }

public class Interview : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ApplicationId { get; set; }
    public Application? Application { get; set; }
    public InterviewType Type { get; set; }
    public DateTimeOffset ScheduledAt { get; set; }
    /// <summary>Employee Service'teki gorusmeciye ID referansi.</summary>
    public Guid InterviewerEmployeeId { get; set; }
    public InterviewResult Result { get; set; } = InterviewResult.Pending;
    public int? Score { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Y17: planlama ayrintilari. InterviewerIds TUM gorusmecileri (birincil dahil) icerir.
    public int DurationMinutes { get; set; } = 60;
    public string? Location { get; set; }
    public string? MeetingUrl { get; set; }
    public List<Guid> InterviewerIds { get; set; } = new();
    public DateTimeOffset? CandidateNotifiedAt { get; set; }

    public IEnumerable<Guid> AllInterviewers() =>
        InterviewerIds.Append(InterviewerEmployeeId).Where(g => g != Guid.Empty).Distinct();
}

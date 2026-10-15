using EngagementService.Tenancy;

namespace EngagementService.Models;

/* ============================================================ Takdir duvarı */

public class Kudos : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    public string FromUserId { get; set; } = "";
    public Guid? FromEmployeeId { get; set; }
    public string FromName { get; set; } = "";
    public Guid ToEmployeeId { get; set; }
    public string ToName { get; set; } = "";
    public string Badge { get; set; } = "";
    public string Message { get; set; } = "";
    public List<string> LikedBy { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/* ============================================== Çalışan self-servis profili */

public class EmployeeProfile : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    public Guid EmployeeId { get; set; }
    public DateOnly? BirthDate { get; set; }
    public bool ShowBirthday { get; set; } = true;
    public string? Bio { get; set; }
    public string? Pronouns { get; set; }
    public List<string> Skills { get; set; } = new();
    public List<string> Interests { get; set; } = new();
    public string? Address { get; set; }
    public string? EmergencyContactName { get; set; }
    public string? EmergencyContactPhone { get; set; }
    /// <summary>Hassas — API yanıtlarında maskelenir.</summary>
    public string? Iban { get; set; }
    /// <summary>Hassas — API yanıtlarında maskelenir.</summary>
    public string? NationalId { get; set; }
    public string? LinkedInUrl { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/* ============================================== Ofis: masa/oda ve kim nerede */

public class Desk : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Desk | Room</summary>
    public string Kind { get; set; } = "Desk";
    public string? Floor { get; set; }
    public string? Zone { get; set; }
    public int Capacity { get; set; } = 1;
    public List<string> Features { get; set; } = new();
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class DeskBooking : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    public Guid DeskId { get; set; }
    public string UserId { get; set; } = "";
    public Guid? EmployeeId { get; set; }
    public string PersonName { get; set; } = "";
    public DateOnly Date { get; set; }
    public int StartMinute { get; set; }
    public int EndMinute { get; set; }
    public string? Title { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Presence : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    public string UserId { get; set; } = "";
    public Guid? EmployeeId { get; set; }
    public string PersonName { get; set; } = "";
    public DateOnly Date { get; set; }
    /// <summary>Office | Remote | Travel | Off</summary>
    public string Mode { get; set; } = "Office";
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/* ============================================================== Mentorluk */

public class MentorProfile : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    public string UserId { get; set; } = "";
    public Guid? EmployeeId { get; set; }
    public string PersonName { get; set; } = "";
    public bool IsMentor { get; set; }
    public bool IsMentee { get; set; }
    public List<string> Offers { get; set; } = new();
    public List<string> Wants { get; set; } = new();
    public int Capacity { get; set; } = 2;
    public string? Bio { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class Mentorship : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    public string MentorUserId { get; set; } = "";
    public string MentorName { get; set; } = "";
    public string MenteeUserId { get; set; } = "";
    public string MenteeName { get; set; } = "";
    public string? Goal { get; set; }
    /// <summary>Requested | Active | Completed | Declined</summary>
    public string Status { get; set; } = "Requested";
    public int MatchScore { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
}

/* ============================================================ İç ilan panosu */

public class InternalApplication : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    public Guid JobPostingId { get; set; }
    public string JobTitle { get; set; } = "";
    public string UserId { get; set; } = "";
    public Guid? EmployeeId { get; set; }
    public string PersonName { get; set; } = "";
    public string? Motivation { get; set; }
    /// <summary>Submitted | Reviewing | Interview | Accepted | Rejected | Withdrawn</summary>
    public string Status { get; set; } = "Submitted";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/* ============================================================== 1:1 defteri */

public class AgendaItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Text { get; set; } = "";
    public bool Done { get; set; }
    public string? By { get; set; }
    public DateOnly? Due { get; set; }
}

public class OneOnOne : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    public string ManagerUserId { get; set; } = "";
    public string ManagerName { get; set; } = "";
    public Guid EmployeeId { get; set; }
    public string? EmployeeUserId { get; set; }
    public string EmployeeName { get; set; } = "";
    public DateTime ScheduledAt { get; set; }
    /// <summary>Planned | Done | Cancelled</summary>
    public string Status { get; set; } = "Planned";
    public List<AgendaItem> Agenda { get; set; } = new();
    public string? SharedNotes { get; set; }
    /// <summary>Yalnızca yönetici görür.</summary>
    public string? PrivateNotes { get; set; }
    public List<AgendaItem> ActionItems { get; set; } = new();
    public int? Mood { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/* ========================================================= Ardıl planlama */

public class SuccessorCandidate
{
    public Guid EmployeeId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>ReadyNow | OneToTwoYears | ThreePlusYears</summary>
    public string Readiness { get; set; } = "OneToTwoYears";
    public string? Notes { get; set; }
}

public class SuccessionPlan : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    public string PositionTitle { get; set; } = "";
    public string? DepartmentName { get; set; }
    public Guid? IncumbentEmployeeId { get; set; }
    public string? IncumbentName { get; set; }
    /// <summary>High | Medium | Low</summary>
    public string Criticality { get; set; } = "Medium";
    /// <summary>High | Medium | Low — görevdekinin ayrılma riski</summary>
    public string VacancyRisk { get; set; } = "Low";
    public List<SuccessorCandidate> Candidates { get; set; } = new();
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/* ============================================================ Anket / eNPS */

public class SurveyQuestion
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Text { get; set; } = "";
    /// <summary>Nps (0-10) | Scale (1-5) | Choice | Text</summary>
    public string Type { get; set; } = "Scale";
    public List<string> Options { get; set; } = new();
    public bool Required { get; set; } = true;
}

public class SurveyAnswer
{
    public string QuestionId { get; set; } = "";
    public int? Score { get; set; }
    public string? Choice { get; set; }
    public string? Text { get; set; }
}

public class Survey : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>eNPS | Pulse | Custom</summary>
    public string Kind { get; set; } = "Pulse";
    public List<SurveyQuestion> Questions { get; set; } = new();
    public bool IsAnonymous { get; set; } = true;
    /// <summary>Draft | Open | Closed</summary>
    public string Status { get; set; } = "Draft";
    public DateTime? ClosesAt { get; set; }
    public string? CreatedByName { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class SurveyResponse : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    public Guid SurveyId { get; set; }
    /// <summary>sha256(anket + kullanıcı) — kim olduğu değil, yalnızca "bir kez" bilgisi.</summary>
    public string RespondentKey { get; set; } = "";
    public string? DepartmentName { get; set; }
    public List<SurveyAnswer> Answers { get; set; } = new();
    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;
}

/* ============================================================== Offboarding */

public class ChecklistItem
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>İK | BT | Yönetici | Bordro | Çalışan</summary>
    public string Owner { get; set; } = "İK";
    public bool Done { get; set; }
    public DateTime? DoneAt { get; set; }
    public string? DoneBy { get; set; }
    public string? Hint { get; set; }
}

public class ExitInterview
{
    public string? PrimaryReason { get; set; }
    public int? ManagerScore { get; set; }
    public int? CultureScore { get; set; }
    public int? GrowthScore { get; set; }
    public int? CompensationScore { get; set; }
    public bool? WouldRecommend { get; set; }
    public string? Comments { get; set; }
}

public class OffboardingCase : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    public Guid EmployeeId { get; set; }
    public string EmployeeName { get; set; } = "";
    public DateOnly LastWorkingDay { get; set; }
    /// <summary>Resignation | Termination | Retirement | ContractEnd | Other</summary>
    public string Reason { get; set; } = "Resignation";
    /// <summary>Open | Completed | Cancelled</summary>
    public string Status { get; set; } = "Open";
    public List<ChecklistItem> Checklist { get; set; } = new();
    public ExitInterview? ExitInterview { get; set; }
    public bool? RehireEligible { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    /// <summary>G15: zimmet iade kontrol listesi (onboarding tablolarından canlı yenilenir).</summary>
    public List<AssetCheck> AssetChecks { get; set; } = new();
    /// <summary>Disabled | NoAccount | Failed | Skipped (hesap kapatma adımı).</summary>
    public string? AccountStatus { get; set; }
    public DateTime? AccountDisabledAt { get; set; }
    public string? AccountNote { get; set; }
    /// <summary>"TerminatedEmployees" saklama süresi (ay) ve buna göre planlanan anonimleştirme tarihi.</summary>
    public int? RetentionMonths { get; set; }
    public DateOnly? PlannedAnonymizationOn { get; set; }
}

/// <summary>Ayrılan çalışanın bir zimmeti: iade edildi mi ya da İK istisnasıyla kayıp/kayıttan düşüldü mü.</summary>
public class AssetCheck
{
    public Guid AssignmentId { get; set; }
    public Guid AssetId { get; set; }
    public string AssetTag { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>Open | Returned | Lost | WrittenOff</summary>
    public string Resolution { get; set; } = "Open";
    public DateOnly? ReturnedOn { get; set; }
    public string? Note { get; set; }
    public string? ResolvedBy { get; set; }
    public DateTime? ResolvedAt { get; set; }
}

/* ======================================================= Org senaryo planı */

public class OrgMove
{
    public Guid EmployeeId { get; set; }
    public string Name { get; set; } = "";
    public Guid? FromDepartmentId { get; set; }
    public Guid? ToDepartmentId { get; set; }
    public string? NewPosition { get; set; }
    /// <summary>Move | Exit | Hire (Hire: EmployeeId boş, yeni kadro)</summary>
    public string Kind { get; set; } = "Move";
    public decimal? PlannedSalary { get; set; }
}

public class OrgScenario : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public List<OrgMove> Moves { get; set; } = new();
    /// <summary>Draft | Shared | Archived</summary>
    public string Status { get; set; } = "Draft";
    public string? CreatedByName { get; set; }
    /// <summary>Taslağın sahibi (Keycloak kullanıcı kimliği); yönetici yalnızca kendi taslaklarını görür.</summary>
    public string? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

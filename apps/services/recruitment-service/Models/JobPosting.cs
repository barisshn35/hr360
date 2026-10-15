using RecruitmentService.Tenancy;

namespace RecruitmentService.Models;

public enum JobPostingStatus { Draft, Published, OnHold, Closed }
public enum EmploymentType { FullTime, PartTime, Contract, Intern }

public class JobPosting : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Title { get; set; }
    /// <summary>Organization Service'teki departmana ID referansi.</summary>
    public Guid DepartmentId { get; set; }
    public string? Description { get; set; }
    public EmploymentType EmploymentType { get; set; } = EmploymentType.FullTime;
    public JobPostingStatus Status { get; set; } = JobPostingStatus.Draft;
    public int Headcount { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public List<Application> Applications { get; set; } = new();

    // Dalga 11 (75): herkese açık yapılandırılmış veri (Google for Jobs JobPosting) alanları.
    public string? Location { get; set; }
    public string? Region { get; set; }
    /// <summary>ISO 3166-1 alfa-2 (varsayılan TR).</summary>
    public string? Country { get; set; }
    public bool RemoteAllowed { get; set; }
    public DateTimeOffset? ValidThrough { get; set; }
    /// <summary>İlan ücret aralığı; yalnızca kiracı açarsa herkese açık çıktıda yer alır.</summary>
    public decimal? SalaryMin { get; set; }
    public decimal? SalaryMax { get; set; }
    public string? SalaryCurrency { get; set; }
    /// <summary>MONTH | YEAR | HOUR.</summary>
    public string? SalaryPeriod { get; set; }
    /// <summary>Liste ucunda başvuru sayısı (yalnızca aday görme yetkisi olana); tabloda tutulmaz.</summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public int? ApplicationCount { get; set; }
}

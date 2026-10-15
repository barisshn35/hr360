using Microsoft.EntityFrameworkCore;
using RecruitmentService.Models;
using RecruitmentService.Tenancy;

namespace RecruitmentService.Data;

public class RecruitmentDbContext : DbContext, ITenantAwareContext
{
    private readonly ITenantContext _tenant;

    public RecruitmentDbContext(DbContextOptions<RecruitmentDbContext> options, ITenantContext tenant)
        : base(options) => _tenant = tenant;

    public string? CurrentTenantSlug => _tenant.TenantSlug;
    public bool CurrentIsPlatformAdmin => _tenant.IsPlatformAdmin;

    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        this.StampTenant(_tenant);
        return base.SaveChangesAsync(ct);
    }

    public DbSet<JobPosting> JobPostings => Set<JobPosting>();
    public DbSet<Candidate> Candidates => Set<Candidate>();
    public DbSet<Application> Applications => Set<Application>();
    public DbSet<Interview> Interviews => Set<Interview>();
    public DbSet<ScorecardTemplate> ScorecardTemplates => Set<ScorecardTemplate>();
    public DbSet<Scorecard> Scorecards => Set<Scorecard>();
    public DbSet<Offer> Offers => Set<Offer>();
    public DbSet<OfferTemplate> OfferTemplates => Set<OfferTemplate>();
    public DbSet<RecruitmentProgramSettings> ProgramSettings => Set<RecruitmentProgramSettings>();
    public DbSet<Referral> Referrals => Set<Referral>();
    public DbSet<StatusLink> StatusLinks => Set<StatusLink>();
    public DbSet<StageEvent> StageEvents => Set<StageEvent>();
    public DbSet<RecruitmentService.Messaging.ProcessedEvent> ProcessedEvents => Set<RecruitmentService.Messaging.ProcessedEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<JobPosting>().ConfigureTenantColumn();
        modelBuilder.Entity<JobPosting>().ToTable("recruitment_job_postings");
        modelBuilder.Entity<JobPosting>().Property(p => p.Status).HasConversion<string>();
        modelBuilder.Entity<JobPosting>().Property(p => p.EmploymentType).HasConversion<string>();

        modelBuilder.Entity<Candidate>().ConfigureTenantColumn();
        modelBuilder.Entity<Candidate>().ToTable("recruitment_candidates");
        modelBuilder.Entity<Candidate>().HasIndex(c => new { c.TenantSlug, c.Email }).IsUnique();

        modelBuilder.Entity<Application>().ConfigureTenantColumn();
        modelBuilder.Entity<Application>().ToTable("recruitment_applications");
        modelBuilder.Entity<Application>().Property(a => a.Status).HasConversion<string>();
        modelBuilder.Entity<Application>()
            .HasOne(a => a.JobPosting).WithMany(p => p.Applications)
            .HasForeignKey(a => a.JobPostingId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Application>()
            .HasOne(a => a.Candidate).WithMany(c => c.Applications)
            .HasForeignKey(a => a.CandidateId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Application>()
            .HasIndex(a => new { a.JobPostingId, a.CandidateId }).IsUnique();

        modelBuilder.Entity<Interview>().ConfigureTenantColumn();
        modelBuilder.Entity<Interview>().ToTable("recruitment_interviews");
        modelBuilder.Entity<Interview>().Property(i => i.Type).HasConversion<string>();
        modelBuilder.Entity<Interview>().Property(i => i.Result).HasConversion<string>();
        modelBuilder.Entity<Interview>()
            .HasOne(i => i.Application).WithMany(a => a.Interviews)
            .HasForeignKey(i => i.ApplicationId).OnDelete(DeleteBehavior.Cascade);

        // Dalga 5c (scripts/sql/2026-10-08_recruitment_plus.sql)
        modelBuilder.Entity<ScorecardTemplate>().ConfigureTenantColumn();
        modelBuilder.Entity<ScorecardTemplate>().ToTable("recruitment_scorecard_templates");
        modelBuilder.Entity<ScorecardTemplate>().Ignore(t => t.Criteria);

        modelBuilder.Entity<Scorecard>().ConfigureTenantColumn();
        modelBuilder.Entity<Scorecard>().ToTable("recruitment_scorecards");
        modelBuilder.Entity<Scorecard>().Ignore(t => t.Scores);
        modelBuilder.Entity<Scorecard>().Property(s => s.OverallScore).HasPrecision(4, 2);

        modelBuilder.Entity<Offer>().ConfigureTenantColumn();
        modelBuilder.Entity<Offer>().ToTable("recruitment_offers");
        modelBuilder.Entity<Offer>().Property(o => o.Status).HasConversion<string>();
        modelBuilder.Entity<Offer>().Property(o => o.GrossSalary).HasPrecision(14, 2);
        modelBuilder.Entity<Offer>().Property(o => o.SalaryLetterText).HasColumnName("LetterText");
        modelBuilder.Entity<Offer>().Property(o => o.SignedSalaryLetterHtml).HasColumnName("SignedLetterHtml");

        modelBuilder.Entity<OfferTemplate>().ConfigureTenantColumn();
        modelBuilder.Entity<OfferTemplate>().ToTable("recruitment_offer_templates");

        // Dalga 11 (scripts/sql/2026-10-24_recruitment_w11.sql)
        modelBuilder.Entity<JobPosting>().Property(p => p.SalaryMin).HasPrecision(14, 2);
        modelBuilder.Entity<JobPosting>().Property(p => p.SalaryMax).HasPrecision(14, 2);
        modelBuilder.Entity<RecruitmentProgramSettings>().ConfigureTenantColumn();
        modelBuilder.Entity<RecruitmentProgramSettings>().ToTable("recruitment_program_settings");
        modelBuilder.Entity<RecruitmentProgramSettings>().Property(s => s.ReferralRewardAmount).HasPrecision(14, 2);
        modelBuilder.Entity<Referral>().ConfigureTenantColumn();
        modelBuilder.Entity<Referral>().ToTable("recruitment_referrals");
        modelBuilder.Entity<Referral>().Property(r => r.RewardStatus).HasConversion<string>();
        modelBuilder.Entity<Referral>().Property(r => r.RewardAmount).HasPrecision(14, 2);
        modelBuilder.Entity<StatusLink>().ConfigureTenantColumn();
        modelBuilder.Entity<StatusLink>().ToTable("recruitment_status_links");
        modelBuilder.Entity<StageEvent>().ConfigureTenantColumn();
        modelBuilder.Entity<StageEvent>().ToTable("recruitment_application_stage_events");

        modelBuilder.Entity<RecruitmentService.Messaging.ProcessedEvent>().ToTable("messaging_processed_events");
        modelBuilder.Entity<RecruitmentService.Messaging.ProcessedEvent>().HasKey(p => new { p.EventId, p.Consumer });

        modelBuilder.ApplyTenantFilters(this);
    }
}

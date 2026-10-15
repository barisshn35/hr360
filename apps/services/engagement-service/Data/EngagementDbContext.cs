using Microsoft.EntityFrameworkCore;
using EngagementService.Models;
using EngagementService.Tenancy;

namespace EngagementService.Data;

/// <summary>
/// Çalışan deneyimi modülünün tabloları (engagement_*). Şema
/// scripts/sql/2026-10-02_engagement_governance.sql ile oluşturulur; EF yalnızca eşler.
/// </summary>
public class EngagementDbContext : DbContext, ITenantAwareContext
{
    private readonly ITenantContext _tenant;

    public EngagementDbContext(DbContextOptions<EngagementDbContext> options, ITenantContext tenant)
        : base(options) => _tenant = tenant;

    public string? CurrentTenantSlug => _tenant.TenantSlug;
    public bool CurrentIsPlatformAdmin => _tenant.IsPlatformAdmin;

    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        this.StampTenant(_tenant);
        return base.SaveChangesAsync(ct);
    }

    public DbSet<Kudos> Kudos => Set<Kudos>();
    public DbSet<EmployeeProfile> Profiles => Set<EmployeeProfile>();
    public DbSet<Desk> Desks => Set<Desk>();
    public DbSet<DeskBooking> DeskBookings => Set<DeskBooking>();
    public DbSet<Presence> Presence => Set<Presence>();
    public DbSet<MentorProfile> MentorProfiles => Set<MentorProfile>();
    public DbSet<Mentorship> Mentorships => Set<Mentorship>();
    public DbSet<InternalApplication> InternalApplications => Set<InternalApplication>();
    public DbSet<OneOnOne> OneOnOnes => Set<OneOnOne>();
    public DbSet<SuccessionPlan> SuccessionPlans => Set<SuccessionPlan>();
    public DbSet<Survey> Surveys => Set<Survey>();
    public DbSet<SurveyResponse> SurveyResponses => Set<SurveyResponse>();
    public DbSet<OffboardingCase> OffboardingCases => Set<OffboardingCase>();
    public DbSet<OrgScenario> OrgScenarios => Set<OrgScenario>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Kudos>().ToTable("engagement_kudos").ConfigureTenantColumn();
        b.Entity<EmployeeProfile>().ToTable("engagement_profiles").ConfigureTenantColumn();
        // KVKK m.12: TCKN ve IBAN veritabanında şifreli (bkz. PiiProtector).
        b.Entity<EmployeeProfile>().Property(x => x.Iban).HasConversion(EngagementService.Infrastructure.PiiProtector.Converter);
        b.Entity<EmployeeProfile>().Property(x => x.NationalId).HasConversion(EngagementService.Infrastructure.PiiProtector.Converter);
        b.Entity<Desk>().ToTable("engagement_desks").ConfigureTenantColumn();
        b.Entity<DeskBooking>().ToTable("engagement_desk_bookings").ConfigureTenantColumn();
        b.Entity<Presence>().ToTable("engagement_presence").ConfigureTenantColumn();
        b.Entity<MentorProfile>().ToTable("engagement_mentor_profiles").ConfigureTenantColumn();
        b.Entity<Mentorship>().ToTable("engagement_mentorships").ConfigureTenantColumn();
        b.Entity<InternalApplication>().ToTable("engagement_internal_applications").ConfigureTenantColumn();

        b.Entity<OneOnOne>().ToTable("engagement_one_on_ones").ConfigureTenantColumn();
        b.Entity<OneOnOne>().Property(x => x.Agenda).HasColumnType("jsonb");
        b.Entity<OneOnOne>().Property(x => x.ActionItems).HasColumnType("jsonb");

        b.Entity<SuccessionPlan>().ToTable("engagement_succession_plans").ConfigureTenantColumn();
        b.Entity<SuccessionPlan>().Property(x => x.Candidates).HasColumnType("jsonb");

        b.Entity<Survey>().ToTable("engagement_surveys").ConfigureTenantColumn();
        b.Entity<Survey>().Property(x => x.Questions).HasColumnType("jsonb");
        b.Entity<SurveyResponse>().ToTable("engagement_survey_responses").ConfigureTenantColumn();
        b.Entity<SurveyResponse>().Property(x => x.Answers).HasColumnType("jsonb");

        b.Entity<OffboardingCase>().ToTable("engagement_offboarding_cases").ConfigureTenantColumn();
        b.Entity<OffboardingCase>().Property(x => x.Checklist).HasColumnType("jsonb");
        b.Entity<OffboardingCase>().Property(x => x.ExitInterview).HasColumnType("jsonb");
        b.Entity<OffboardingCase>().Property(x => x.AssetChecks).HasColumnType("jsonb");

        b.Entity<OrgScenario>().ToTable("engagement_org_scenarios").ConfigureTenantColumn();
        b.Entity<OrgScenario>().Property(x => x.Moves).HasColumnType("jsonb");

        b.ApplyTenantFilters(this);
    }
}

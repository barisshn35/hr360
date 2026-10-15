using Microsoft.EntityFrameworkCore;
using LearningService.Models;
using LearningService.Tenancy;

namespace LearningService.Data;

public class LearningDbContext : DbContext, ITenantAwareContext
{
    private readonly ITenantContext _tenant;

    public LearningDbContext(DbContextOptions<LearningDbContext> options, ITenantContext tenant)
        : base(options) => _tenant = tenant;

    public string? CurrentTenantSlug => _tenant.TenantSlug;
    public bool CurrentIsPlatformAdmin => _tenant.IsPlatformAdmin;

    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        this.StampTenant(_tenant);
        return base.SaveChangesAsync(ct);
    }

    public DbSet<Course> Courses => Set<Course>();
    public DbSet<Enrollment> Enrollments => Set<Enrollment>();
    public DbSet<Certification> Certifications => Set<Certification>();
    public DbSet<Competency> Competencies => Set<Competency>();
    public DbSet<RoleProfileEntry> RoleProfiles => Set<RoleProfileEntry>();
    public DbSet<CompetencyAssessment> Assessments => Set<CompetencyAssessment>();
    public DbSet<CourseCompetency> CourseCompetencies => Set<CourseCompetency>();
    public DbSet<CourseModule> Modules => Set<CourseModule>();
    public DbSet<QuizQuestion> QuizQuestions => Set<QuizQuestion>();
    public DbSet<QuizAttempt> QuizAttempts => Set<QuizAttempt>();
    public DbSet<ModuleProgress> ModuleProgress => Set<ModuleProgress>();
    public DbSet<ScormPackage> ScormPackages => Set<ScormPackage>();
    public DbSet<ScormFile> ScormFiles => Set<ScormFile>();
    public DbSet<ScormRuntime> ScormRuntime => Set<ScormRuntime>();
    public DbSet<CertReminder> CertReminders => Set<CertReminder>();
    public DbSet<CareerPath> CareerPaths => Set<CareerPath>();
    public DbSet<CareerStep> CareerSteps => Set<CareerStep>();
    public DbSet<CareerStepRequirement> CareerStepRequirements => Set<CareerStepRequirement>();
    public DbSet<DueReminder> DueReminders => Set<DueReminder>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Course>().ConfigureTenantColumn();
        modelBuilder.Entity<Course>().ToTable("learning_courses");
        modelBuilder.Entity<Course>().Property(c => c.Category).HasConversion<string>();

        modelBuilder.Entity<Enrollment>().ConfigureTenantColumn();
        modelBuilder.Entity<Enrollment>().ToTable("learning_enrollments");
        modelBuilder.Entity<Enrollment>().Property(e => e.Status).HasConversion<string>();
        modelBuilder.Entity<Enrollment>()
            .HasOne(e => e.Course).WithMany(c => c.Enrollments)
            .HasForeignKey(e => e.CourseId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Enrollment>()
            .HasIndex(e => new { e.CourseId, e.EmployeeId }).IsUnique();

        modelBuilder.Entity<Certification>().ConfigureTenantColumn();
        modelBuilder.Entity<Certification>().ToTable("learning_certifications");
        modelBuilder.Entity<Certification>().HasIndex(c => c.EmployeeId);

        // Dalga 5c (Y19/Y20/G17) — tablolar scripts/sql/2026-10-08_learning_performance.sql'den.
        modelBuilder.Entity<Competency>().ConfigureTenantColumn();
        modelBuilder.Entity<Competency>().ToTable("learning_competencies");
        modelBuilder.Entity<RoleProfileEntry>().ConfigureTenantColumn();
        modelBuilder.Entity<RoleProfileEntry>().ToTable("learning_role_profiles");
        modelBuilder.Entity<CompetencyAssessment>().ConfigureTenantColumn();
        modelBuilder.Entity<CompetencyAssessment>().ToTable("learning_competency_assessments");
        modelBuilder.Entity<CourseCompetency>().ConfigureTenantColumn();
        modelBuilder.Entity<CourseCompetency>().ToTable("learning_course_competencies");
        modelBuilder.Entity<CourseModule>().ConfigureTenantColumn();
        modelBuilder.Entity<CourseModule>().ToTable("learning_course_modules");
        modelBuilder.Entity<QuizQuestion>().ConfigureTenantColumn();
        modelBuilder.Entity<QuizQuestion>().ToTable("learning_quiz_questions");
        modelBuilder.Entity<QuizQuestion>().Property(q => q.OptionsJson).HasColumnType("jsonb");
        modelBuilder.Entity<QuizQuestion>().Property(q => q.CorrectJson).HasColumnType("jsonb");
        modelBuilder.Entity<QuizAttempt>().ConfigureTenantColumn();
        modelBuilder.Entity<QuizAttempt>().ToTable("learning_quiz_attempts");
        modelBuilder.Entity<QuizAttempt>().Property(q => q.AnswersJson).HasColumnType("jsonb");
        modelBuilder.Entity<ModuleProgress>().ConfigureTenantColumn();
        modelBuilder.Entity<ModuleProgress>().ToTable("learning_module_progress");
        modelBuilder.Entity<ScormPackage>().ConfigureTenantColumn();
        modelBuilder.Entity<ScormPackage>().ToTable("learning_scorm_packages");
        modelBuilder.Entity<ScormFile>().ConfigureTenantColumn();
        modelBuilder.Entity<ScormFile>().ToTable("learning_scorm_files");
        // İlişki EF'e bildirilir ki yüklemede paket satırı dosyalardan ÖNCE eklensin.
        modelBuilder.Entity<ScormFile>().HasOne<ScormPackage>().WithMany()
            .HasForeignKey(f => f.PackageId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ScormRuntime>().ConfigureTenantColumn();
        modelBuilder.Entity<ScormRuntime>().ToTable("learning_scorm_runtime");
        modelBuilder.Entity<CertReminder>().ConfigureTenantColumn();
        modelBuilder.Entity<CertReminder>().ToTable("learning_cert_reminders");

        // Dalga 11 (madde 83/84) — tablolar scripts/sql/2026-10-24_learning_w11.sql'den.
        modelBuilder.Entity<CareerPath>().ConfigureTenantColumn();
        modelBuilder.Entity<CareerPath>().ToTable("learning_career_paths");
        modelBuilder.Entity<CareerStep>().ConfigureTenantColumn();
        modelBuilder.Entity<CareerStep>().ToTable("learning_career_steps");
        modelBuilder.Entity<CareerPath>().HasMany(p => p.Steps).WithOne()
            .HasForeignKey(s => s.PathId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<CareerStepRequirement>().ConfigureTenantColumn();
        modelBuilder.Entity<CareerStepRequirement>().ToTable("learning_career_step_requirements");
        modelBuilder.Entity<CareerStep>().HasMany(s => s.Requirements).WithOne()
            .HasForeignKey(r => r.StepId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<DueReminder>().ConfigureTenantColumn();
        modelBuilder.Entity<DueReminder>().ToTable("learning_due_reminders");


        modelBuilder.ApplyTenantFilters(this);
    }
}

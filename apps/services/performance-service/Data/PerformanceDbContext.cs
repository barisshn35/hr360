using Microsoft.EntityFrameworkCore;
using PerformanceService.Models;
using PerformanceService.Tenancy;

namespace PerformanceService.Data;

public class PerformanceDbContext : DbContext, ITenantAwareContext
{
    private readonly ITenantContext _tenant;

    public PerformanceDbContext(DbContextOptions<PerformanceDbContext> options, ITenantContext tenant)
        : base(options) => _tenant = tenant;

    public string? CurrentTenantSlug => _tenant.TenantSlug;
    public bool CurrentIsPlatformAdmin => _tenant.IsPlatformAdmin;

    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        this.StampTenant(_tenant);
        return base.SaveChangesAsync(ct);
    }

    public DbSet<ReviewCycle> Cycles => Set<ReviewCycle>();
    public DbSet<Goal> Goals => Set<Goal>();
    public DbSet<Review> Reviews => Set<Review>();
    public DbSet<MetricDefinition> Metrics => Set<MetricDefinition>();
    public DbSet<ScoringConfig> ScoringConfigs => Set<ScoringConfig>();
    public DbSet<ReviewScore> ReviewScores => Set<ReviewScore>();
    public DbSet<Feedback> Feedback => Set<Feedback>();
    public DbSet<PerformanceSnapshot> Snapshots => Set<PerformanceSnapshot>();
    public DbSet<PotentialRating> PotentialRatings => Set<PotentialRating>();
    public DbSet<NineBoxOverride> NineBoxOverrides => Set<NineBoxOverride>();
    public DbSet<CycleTemplate> CycleTemplates => Set<CycleTemplate>();
    public DbSet<CalibrationSession> CalibrationSessions => Set<CalibrationSession>();
    public DbSet<CalibrationItem> CalibrationItems => Set<CalibrationItem>();
    public DbSet<CalibrationChange> CalibrationChanges => Set<CalibrationChange>();
    public DbSet<Objective> Objectives => Set<Objective>();
    public DbSet<F360Request> F360Requests => Set<F360Request>();
    public DbSet<F360Participant> F360Participants => Set<F360Participant>();
    public DbSet<F360Response> F360Responses => Set<F360Response>();

    /// <summary>
    /// Türkçe harf katlamalı küçük harf (SQL: public.hr360_fold, scripts/sql/2026-10-09_paging_indexes.sql).
    /// Sunucu tarafı aramanın "ayse" ile "Ayşe"yi eşleştirmesi için; yalnızca LINQ sorgularında çevrilir.
    /// </summary>
    public static string Fold(string value) => throw new NotSupportedException("Yalnızca LINQ sorgusunda kullanılır.");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDbFunction(typeof(PerformanceDbContext).GetMethod(nameof(Fold), new[] { typeof(string) })!)
            .HasName("hr360_fold").HasSchema("public");
        modelBuilder.Entity<ReviewCycle>().ConfigureTenantColumn();
        modelBuilder.Entity<ReviewCycle>().ToTable("performance_cycles");
        modelBuilder.Entity<ReviewCycle>().Property(c => c.Period).HasConversion<string>();
        modelBuilder.Entity<ReviewCycle>().Property(c => c.Status).HasConversion<string>();

        modelBuilder.Entity<Goal>().ConfigureTenantColumn();
        modelBuilder.Entity<Goal>().ToTable("performance_goals");
        modelBuilder.Entity<Goal>().Property(g => g.Status).HasConversion<string>();
        modelBuilder.Entity<Goal>()
            .HasOne(g => g.Cycle).WithMany(c => c.Goals)
            .HasForeignKey(g => g.CycleId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Goal>().HasIndex(g => g.EmployeeId);

        modelBuilder.Entity<Review>().ConfigureTenantColumn();
        modelBuilder.Entity<Review>().ToTable("performance_reviews");
        modelBuilder.Entity<Review>().Property(r => r.Type).HasConversion<string>();
        modelBuilder.Entity<Review>().HasIndex(r => new { r.CycleId, r.EmployeeId });
    
        // --- Yapilandirilabilir degerlendirme modeli ---

        modelBuilder.Entity<MetricDefinition>().ConfigureTenantColumn();
        modelBuilder.Entity<MetricDefinition>().ToTable("performance_metrics");
        modelBuilder.Entity<MetricDefinition>().Property(m => m.Category).HasConversion<string>();
        modelBuilder.Entity<MetricDefinition>().Property(m => m.Scale).HasConversion<string>();
        // Kod kiraci icinde benzersiz - raporlarda sabit referans olarak kullaniliyor.
        modelBuilder.Entity<MetricDefinition>()
            .HasIndex(m => new { m.TenantSlug, m.Code }).IsUnique();

        modelBuilder.Entity<ScoringConfig>().ConfigureTenantColumn();
        modelBuilder.Entity<ScoringConfig>().ToTable("performance_scoring_config");
        modelBuilder.Entity<ScoringConfig>()
            .HasIndex(c => new { c.TenantSlug, c.Version }).IsUnique();

        modelBuilder.Entity<ReviewScore>().ConfigureTenantColumn();
        modelBuilder.Entity<ReviewScore>().ToTable("performance_review_scores");
        modelBuilder.Entity<ReviewScore>()
            .HasOne(s => s.Review).WithMany(r => r.Scores)
            .HasForeignKey(s => s.ReviewId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ReviewScore>()
            .HasOne(s => s.Metric).WithMany()
            .HasForeignKey(s => s.MetricId).OnDelete(DeleteBehavior.Restrict);
        // Ayni degerlendirmede ayni metrik iki kez puanlanamaz.
        modelBuilder.Entity<ReviewScore>()
            .HasIndex(s => new { s.ReviewId, s.MetricId }).IsUnique();

        modelBuilder.Entity<Feedback>().ConfigureTenantColumn();
        modelBuilder.Entity<Feedback>().ToTable("performance_feedback");
        modelBuilder.Entity<Feedback>().Property(f => f.Reason).HasConversion<string>();
        modelBuilder.Entity<Feedback>().Property(f => f.Sentiment).HasConversion<string>();
        modelBuilder.Entity<Feedback>().HasIndex(f => f.ToEmployeeId);
        modelBuilder.Entity<Feedback>().HasIndex(f => f.FromEmployeeId);

        modelBuilder.Entity<PerformanceSnapshot>().ConfigureTenantColumn();
        modelBuilder.Entity<PerformanceSnapshot>().ToTable("performance_snapshots");
        modelBuilder.Entity<PerformanceSnapshot>().Property(s => s.Source).HasConversion<string>();
        // Trend sorgulari calisan + zaman uzerinden gidiyor.
        modelBuilder.Entity<PerformanceSnapshot>()
            .HasIndex(s => new { s.EmployeeId, s.CapturedAt });
        // Ekip karsilastirmasi ekip + zaman uzerinden.
        modelBuilder.Entity<PerformanceSnapshot>()
            .HasIndex(s => new { s.TeamId, s.CapturedAt });

        // G12 (Dalga 5c) — tablolar scripts/sql/2026-10-08_learning_performance.sql'den.
        modelBuilder.Entity<ReviewCycle>().Property(c => c.ConfigJson).HasColumnType("jsonb");
        modelBuilder.Entity<PotentialRating>().ConfigureTenantColumn();
        modelBuilder.Entity<PotentialRating>().ToTable("performance_potential_ratings");
        modelBuilder.Entity<NineBoxOverride>().ConfigureTenantColumn();
        modelBuilder.Entity<NineBoxOverride>().ToTable("performance_ninebox_overrides");
        modelBuilder.Entity<CycleTemplate>().ConfigureTenantColumn();
        modelBuilder.Entity<CycleTemplate>().ToTable("performance_cycle_templates");
        modelBuilder.Entity<CycleTemplate>().Property(c => c.Period).HasConversion<string>();
        modelBuilder.Entity<CycleTemplate>().Property(c => c.ConfigJson).HasColumnType("jsonb");

        // Dalga 11 — tablolar scripts/sql/2026-10-24_performance_w11.sql'den.
        modelBuilder.Entity<CalibrationSession>().ConfigureTenantColumn();
        modelBuilder.Entity<CalibrationSession>().ToTable("performance_calibration_sessions");
        modelBuilder.Entity<CalibrationSession>().Property(s => s.Status).HasConversion<string>();
        modelBuilder.Entity<CalibrationItem>().ConfigureTenantColumn();
        modelBuilder.Entity<CalibrationItem>().ToTable("performance_calibration_items");
        modelBuilder.Entity<CalibrationChange>().ConfigureTenantColumn();
        modelBuilder.Entity<CalibrationChange>().ToTable("performance_calibration_changes");
        modelBuilder.Entity<Objective>().ConfigureTenantColumn();
        modelBuilder.Entity<Objective>().ToTable("performance_objectives");
        modelBuilder.Entity<Objective>().Property(o => o.Level).HasConversion<string>();
        modelBuilder.Entity<F360Request>().ConfigureTenantColumn();
        modelBuilder.Entity<F360Request>().ToTable("performance_f360_requests");
        modelBuilder.Entity<F360Request>().Property(r => r.CompetenciesJson).HasColumnType("jsonb");
        modelBuilder.Entity<F360Participant>().ConfigureTenantColumn();
        modelBuilder.Entity<F360Participant>().ToTable("performance_f360_participants");
        modelBuilder.Entity<F360Response>().ConfigureTenantColumn();
        modelBuilder.Entity<F360Response>().ToTable("performance_f360_responses");
        modelBuilder.Entity<F360Response>().Property(r => r.RatingsJson).HasColumnType("jsonb");
        // Yabancı anahtarlar modelde de tanımlı olmalı: oturum + satırları (talep + katılımcıları) tek SaveChanges'ta
        // eklenirken EF ekleme sırasını bağımlılığa göre kurar (aksi hâlde satır önce eklenip FK 23503 verir).
        modelBuilder.Entity<CalibrationItem>().HasOne<CalibrationSession>().WithMany().HasForeignKey(i => i.SessionId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<CalibrationChange>().HasOne<CalibrationSession>().WithMany().HasForeignKey(c => c.SessionId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<F360Participant>().HasOne<F360Request>().WithMany().HasForeignKey(p => p.RequestId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<F360Response>().HasOne<F360Request>().WithMany().HasForeignKey(r => r.RequestId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Objective>().HasOne<Objective>().WithMany().HasForeignKey(o => o.ParentId).OnDelete(DeleteBehavior.ClientSetNull);

        modelBuilder.ApplyTenantFilters(this);
    }
}

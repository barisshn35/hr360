using Microsoft.EntityFrameworkCore;
using OnboardingService.Models;
using OnboardingService.Tenancy;

namespace OnboardingService.Data;

public class OnboardingDbContext : DbContext, ITenantAwareContext
{
    private readonly ITenantContext _tenant;

    public OnboardingDbContext(DbContextOptions<OnboardingDbContext> options, ITenantContext tenant)
        : base(options) => _tenant = tenant;

    public string? CurrentTenantSlug => _tenant.TenantSlug;
    public bool CurrentIsPlatformAdmin => _tenant.IsPlatformAdmin;

    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        this.StampTenant(_tenant);
        return base.SaveChangesAsync(ct);
    }

    public DbSet<OnboardingPlan> Plans => Set<OnboardingPlan>();
    public DbSet<OnboardingTask> Tasks => Set<OnboardingTask>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<AssetAssignment> AssetAssignments => Set<AssetAssignment>();
    public DbSet<TaskTemplate> TaskTemplates => Set<TaskTemplate>();
    public DbSet<TaskTemplateItem> TaskTemplateItems => Set<TaskTemplateItem>();
    public DbSet<OnboardingSettings> Settings => Set<OnboardingSettings>();
    public DbSet<AssetMaintenance> AssetMaintenance => Set<AssetMaintenance>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OnboardingPlan>().ConfigureTenantColumn();
        modelBuilder.Entity<OnboardingPlan>().ToTable("onboarding_plans");
        modelBuilder.Entity<OnboardingPlan>().Property(p => p.Status).HasConversion<string>();
        modelBuilder.Entity<OnboardingPlan>().HasIndex(p => p.EmployeeId);

        modelBuilder.Entity<OnboardingTask>().ConfigureTenantColumn();
        modelBuilder.Entity<OnboardingTask>().ToTable("onboarding_tasks");
        modelBuilder.Entity<OnboardingTask>().Property(t => t.Category).HasConversion<string>();
        modelBuilder.Entity<OnboardingTask>().Property(t => t.Status).HasConversion<string>();
        modelBuilder.Entity<OnboardingTask>()
            .HasOne(t => t.Plan).WithMany(p => p.Tasks)
            .HasForeignKey(t => t.PlanId).OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Asset>().ConfigureTenantColumn();
        modelBuilder.Entity<Asset>().ToTable("onboarding_assets");
        modelBuilder.Entity<Asset>().Property(a => a.Type).HasConversion<string>();
        modelBuilder.Entity<Asset>().Property(a => a.Status).HasConversion<string>();
        modelBuilder.Entity<Asset>().HasIndex(a => new { a.TenantSlug, a.AssetTag }).IsUnique();
        // Bir zimmetin ayni anda tek acik atamasi olabilir (esz. zamanli iki atama
        // GetAll'daki ToDictionary'yi patlatip tum zimmet listesini 500'e dusuruyordu).
        modelBuilder.Entity<AssetAssignment>().HasIndex(a => a.AssetId).IsUnique()
            .HasFilter("\"ReturnedOn\" IS NULL").HasDatabaseName("UX_onboarding_asset_assignments_open");

        modelBuilder.Entity<AssetAssignment>().ConfigureTenantColumn();
        modelBuilder.Entity<AssetAssignment>().ToTable("onboarding_asset_assignments");
        modelBuilder.Entity<AssetAssignment>()
            .HasOne(a => a.Asset).WithMany(x => x.Assignments)
            .HasForeignKey(a => a.AssetId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<AssetAssignment>().HasIndex(a => a.EmployeeId);
    
        // Dalga 5c tablolari: scripts/sql/2026-10-08_ops_plus.sql
        modelBuilder.Entity<TaskTemplate>().ConfigureTenantColumn();
        modelBuilder.Entity<TaskTemplate>().ToTable("onboarding_task_templates");
        modelBuilder.Entity<TaskTemplateItem>().ConfigureTenantColumn();
        modelBuilder.Entity<TaskTemplateItem>().ToTable("onboarding_task_template_items");
        modelBuilder.Entity<TaskTemplateItem>().Property(i => i.Category).HasConversion<string>();
        modelBuilder.Entity<TaskTemplateItem>()
            .HasOne(i => i.Template).WithMany(t => t.Items)
            .HasForeignKey(i => i.TemplateId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<OnboardingSettings>().ConfigureTenantColumn();
        modelBuilder.Entity<OnboardingSettings>().ToTable("onboarding_settings");
        modelBuilder.Entity<AssetMaintenance>().ConfigureTenantColumn();
        modelBuilder.Entity<AssetMaintenance>().ToTable("onboarding_asset_maintenance");
        modelBuilder.Entity<AssetMaintenance>().Property(m => m.Type).HasConversion<string>();
        modelBuilder.Entity<AssetMaintenance>().Property(m => m.Cost).HasColumnType("numeric(12,2)");

        modelBuilder.ApplyTenantFilters(this);
    }
}

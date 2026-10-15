using Microsoft.EntityFrameworkCore;
using WorkflowService.Models;
using WorkflowService.Messaging;
using WorkflowService.Tenancy;

namespace WorkflowService.Data;

public class WorkflowDbContext : DbContext, ITenantAwareContext
{
    private readonly ITenantContext _tenant;

    public WorkflowDbContext(DbContextOptions<WorkflowDbContext> options, ITenantContext tenant)
        : base(options) => _tenant = tenant;

    public string? CurrentTenantSlug => _tenant.TenantSlug;
    public bool CurrentIsPlatformAdmin => _tenant.IsPlatformAdmin;

    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        this.StampTenant(_tenant);
        return base.SaveChangesAsync(ct);
    }

    public DbSet<WorkflowRequest> WorkflowRequests => Set<WorkflowRequest>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<ApprovalStep> ApprovalSteps => Set<ApprovalStep>();
    public DbSet<WorkflowDefinition> Definitions => Set<WorkflowDefinition>();
    public DbSet<Delegation> Delegations => Set<Delegation>();
    public DbSet<WorkflowSettings> Settings => Set<WorkflowSettings>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxMessage>().ToTable("messaging_outbox");
        modelBuilder.Entity<OutboxMessage>().HasIndex(m => new { m.PublishedAt, m.CreatedAt });

        modelBuilder.Entity<WorkflowRequest>().ConfigureTenantColumn();
        modelBuilder.Entity<ApprovalStep>().ConfigureTenantColumn();

        modelBuilder.Entity<WorkflowRequest>().ToTable("workflow_requests");
        modelBuilder.Entity<WorkflowRequest>().Property(w => w.Status).HasConversion<string>();
        modelBuilder.Entity<WorkflowRequest>().Property(w => w.Type).HasConversion<string>();

        modelBuilder.Entity<ApprovalStep>().ToTable("workflow_approval_steps");
        modelBuilder.Entity<ApprovalStep>().Property(s => s.Decision).HasConversion<string>();
        modelBuilder.Entity<ApprovalStep>()
            .HasOne(s => s.WorkflowRequest)
            .WithMany(w => w.Steps)
            .HasForeignKey(s => s.WorkflowRequestId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<WorkflowDefinition>().ConfigureTenantColumn();
        modelBuilder.Entity<WorkflowDefinition>().ToTable("workflow_definitions");
        modelBuilder.Entity<WorkflowDefinition>().Property(d => d.Type).HasConversion<string>();
        modelBuilder.Entity<WorkflowDefinition>().HasIndex(d => new { d.TenantSlug, d.Type });

        modelBuilder.Entity<Delegation>().ConfigureTenantColumn();
        modelBuilder.Entity<Delegation>().ToTable("workflow_delegations");
        modelBuilder.Entity<Delegation>().HasIndex(d => new { d.TenantSlug, d.FromEmployeeId });

        modelBuilder.Entity<WorkflowSettings>().ConfigureTenantColumn();
        modelBuilder.Entity<WorkflowSettings>().ToTable("workflow_settings");

        modelBuilder.ApplyTenantFilters(this);
    }
}

using Microsoft.EntityFrameworkCore;
using ExpenseService.Models;
using ExpenseService.Messaging;
using ExpenseService.Tenancy;

namespace ExpenseService.Data;

public class ExpenseDbContext : DbContext, ITenantAwareContext
{
    private readonly ITenantContext _tenant;

    public ExpenseDbContext(DbContextOptions<ExpenseDbContext> options, ITenantContext tenant)
        : base(options) => _tenant = tenant;

    public string? CurrentTenantSlug => _tenant.TenantSlug;
    public bool CurrentIsPlatformAdmin => _tenant.IsPlatformAdmin;

    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        this.StampTenant(_tenant);
        return base.SaveChangesAsync(ct);
    }

    public DbSet<ExpenseClaim> Claims => Set<ExpenseClaim>();
    public DbSet<ExpenseItem> Items => Set<ExpenseItem>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentSignature> DocumentSignatures => Set<DocumentSignature>();
    public DbSet<SignatureEvidence> SignatureEvidence => Set<SignatureEvidence>();
    public DbSet<HrCase> Cases => Set<HrCase>();
    public DbSet<FxRate> FxRates => Set<FxRate>();
    public DbSet<ExpensePolicy> Policies => Set<ExpensePolicy>();
    public DbSet<TravelRequest> Travels => Set<TravelRequest>();
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ExpenseClaim>().ConfigureTenantColumn();
        modelBuilder.Entity<ExpenseClaim>().ToTable("expense_claims");
        modelBuilder.Entity<ExpenseClaim>().Property(c => c.Status).HasConversion<string>();
        modelBuilder.Entity<ExpenseClaim>().HasIndex(c => c.EmployeeId);

        modelBuilder.Entity<ExpenseItem>().ConfigureTenantColumn();
        modelBuilder.Entity<ExpenseItem>().ToTable("expense_items");
        modelBuilder.Entity<ExpenseItem>().Property(i => i.Category).HasConversion<string>();
        modelBuilder.Entity<ExpenseItem>()
            .HasOne(i => i.Claim).WithMany(c => c.Items)
            .HasForeignKey(i => i.ClaimId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ExpenseItem>().Property(i => i.AnomalyFlagsJson).HasColumnName("AnomalyFlags").HasColumnType("jsonb");

        modelBuilder.Entity<Document>().ConfigureTenantColumn();
        modelBuilder.Entity<Document>().ToTable("expense_documents");
        modelBuilder.Entity<Document>().Property(d => d.Type).HasConversion<string>();
        modelBuilder.Entity<Document>().HasIndex(d => d.EmployeeId);

        // Y28 basit e-imza (tablolar scripts/sql/2026-10-09_identity_sign.sql).
        modelBuilder.Entity<DocumentSignature>().ConfigureTenantColumn();
        modelBuilder.Entity<DocumentSignature>().ToTable("expense_document_signatures");
        modelBuilder.Entity<SignatureEvidence>().ConfigureTenantColumn();
        modelBuilder.Entity<SignatureEvidence>().ToTable("expense_signature_evidence");

        modelBuilder.Entity<HrCase>().ConfigureTenantColumn();
        modelBuilder.Entity<HrCase>().ToTable("expense_hr_cases");
        modelBuilder.Entity<HrCase>().Property(c => c.Category).HasConversion<string>();
        modelBuilder.Entity<HrCase>().Property(c => c.Priority).HasConversion<string>();
        modelBuilder.Entity<HrCase>().Property(c => c.Status).HasConversion<string>();
        modelBuilder.Entity<HrCase>().HasIndex(c => c.Status);

        modelBuilder.Entity<FxRate>().ToTable("expense_fx_rates");
        modelBuilder.Entity<FxRate>().HasIndex(r => new { r.Date, r.Currency, r.TenantSlug });
        modelBuilder.Entity<ExpensePolicy>().ConfigureTenantColumn();
        modelBuilder.Entity<ExpensePolicy>().ToTable("expense_policies");
        modelBuilder.Entity<TravelRequest>().ConfigureTenantColumn();
        modelBuilder.Entity<TravelRequest>().ToTable("expense_travel_requests");
        modelBuilder.Entity<TravelRequest>().Property(t => t.Status).HasConversion<string>();
        modelBuilder.Entity<TravelRequest>().HasIndex(t => t.EmployeeId);

        // Kafka idempotency ve outbox tablolari - TUM servisler bu ORTAK
        // tablolari paylasir (bkz. leave-service ile ayni desen).
        modelBuilder.Entity<ProcessedEvent>().ToTable("messaging_processed_events");
        modelBuilder.Entity<ProcessedEvent>().HasKey(p => new { p.EventId, p.Consumer });

        modelBuilder.Entity<OutboxMessage>().ToTable("messaging_outbox");
        modelBuilder.Entity<OutboxMessage>().HasIndex(m => new { m.PublishedAt, m.CreatedAt });

        modelBuilder.ApplyTenantFilters(this);
    }
}

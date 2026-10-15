using Microsoft.EntityFrameworkCore;
using LeaveService.Messaging;
using LeaveService.Models;
using LeaveService.Tenancy;

namespace LeaveService.Data;

public class LeaveDbContext : DbContext, ITenantAwareContext
{
    private readonly ITenantContext _tenant;

    public LeaveDbContext(DbContextOptions<LeaveDbContext> options, ITenantContext tenant)
        : base(options) => _tenant = tenant;

    public string? CurrentTenantSlug => _tenant.TenantSlug;
    public bool CurrentIsPlatformAdmin => _tenant.IsPlatformAdmin;

    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        this.StampTenant(_tenant);
        return base.SaveChangesAsync(ct);
    }

    public DbSet<LeaveBalance> LeaveBalances => Set<LeaveBalance>();
    public DbSet<LeaveRequest> LeaveRequests => Set<LeaveRequest>();
    public DbSet<PublicHoliday> PublicHolidays => Set<PublicHoliday>();
    public DbSet<LeaveSettings> LeaveSettings => Set<LeaveSettings>();
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    /// <summary>
    /// Türkçe harf katlamalı küçük harf (SQL: public.hr360_fold, scripts/sql/2026-10-09_paging_indexes.sql).
    /// Sunucu tarafı aramanın arayüzdeki gibi "ayse" ile "Ayşe"yi eşleştirmesi için; yalnızca sorgularda çevrilir.
    /// </summary>
    public static string Fold(string value) => throw new NotSupportedException("Yalnızca LINQ sorgusunda kullanılır.");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDbFunction(typeof(LeaveDbContext).GetMethod(nameof(Fold), new[] { typeof(string) })!)
            .HasName("hr360_fold").HasSchema("public");
        modelBuilder.Entity<LeaveBalance>().ConfigureTenantColumn();
        modelBuilder.Entity<LeaveBalance>().ToTable("leave_balances");
        modelBuilder.Entity<LeaveBalance>().Property(b => b.Type).HasConversion<string>();
        modelBuilder.Entity<LeaveBalance>().Ignore(b => b.RemainingDays);
        // Eszamanlilik: Postgres'in satir surumu (xmin) belirtec olarak kullanilir. Onceden
        // ayni anda gelen iki talep ayni bakiyeyi okuyup ikisi de "yeterli" goruyor ve
        // PendingDays guncellemelerinden biri kayboluyordu (bakiye asilabiliyordu).
        modelBuilder.Entity<LeaveBalance>().Property<uint>("xmin")
            .HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        modelBuilder.Entity<LeaveBalance>()
            .HasIndex(b => new { b.EmployeeId, b.Year, b.Type }).IsUnique();

        modelBuilder.Entity<PublicHoliday>().ConfigureTenantColumn();
        modelBuilder.Entity<PublicHoliday>().ToTable("leave_public_holidays");
        modelBuilder.Entity<PublicHoliday>().HasIndex(h => new { h.TenantSlug, h.Date }).IsUnique();

        // Dalga 9: scripts/sql/2026-10-22_time_leave.sql
        modelBuilder.Entity<LeaveSettings>().ConfigureTenantColumn();
        modelBuilder.Entity<LeaveSettings>().ToTable("leave_settings");

        modelBuilder.Entity<LeaveRequest>().ConfigureTenantColumn();
        modelBuilder.Entity<LeaveRequest>().ToTable("leave_requests");
        modelBuilder.Entity<LeaveRequest>().Property(r => r.Type).HasConversion<string>();
        modelBuilder.Entity<LeaveRequest>().Property(r => r.Status).HasConversion<string>();
        modelBuilder.Entity<LeaveRequest>().HasIndex(r => r.EmployeeId);
        modelBuilder.Entity<LeaveRequest>().HasIndex(r => r.Status);
    
                // Kafka idempotency tablosu - TUM tuketici servisler bu ORTAK tabloyu
        // paylasir (Consumer sutunu hangi servisin isledigini ayirt eder).
        // TenantSlug yok, tenant filtrelerine dahil edilmemeli.
        modelBuilder.Entity<ProcessedEvent>().ToTable("messaging_processed_events");
        modelBuilder.Entity<ProcessedEvent>().HasKey(p => new { p.EventId, p.Consumer });

        // Outbox tablosu da ORTAK - TUM yayinlayan servisler paylasir.
        modelBuilder.Entity<OutboxMessage>().ToTable("messaging_outbox");
        modelBuilder.Entity<OutboxMessage>().HasIndex(m => new { m.PublishedAt, m.CreatedAt });

        modelBuilder.ApplyTenantFilters(this);
    }
}

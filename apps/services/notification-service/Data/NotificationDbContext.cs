using Microsoft.EntityFrameworkCore;
using NotificationService.Messaging;
using NotificationService.Models;
using NotificationService.Tenancy;

namespace NotificationService.Data;

public class NotificationDbContext : DbContext, ITenantAwareContext
{
    private readonly ITenantContext _tenant;

    public NotificationDbContext(DbContextOptions<NotificationDbContext> options, ITenantContext tenant)
        : base(options) => _tenant = tenant;

    public string? CurrentTenantSlug => _tenant.TenantSlug;
    public bool CurrentIsPlatformAdmin => _tenant.IsPlatformAdmin;

    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        this.StampTenant(_tenant);
        return base.SaveChangesAsync(ct);
    }

    public DbSet<NotificationTemplate> Templates => Set<NotificationTemplate>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();
    public DbSet<NotificationPreference> Preferences => Set<NotificationPreference>();
    public DbSet<CategoryPreference> CategoryPreferences => Set<CategoryPreference>();
    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();
    public DbSet<VapidKeyRow> VapidKeys => Set<VapidKeyRow>();
    public DbSet<UiPreference> UiPreferences => Set<UiPreference>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NotificationTemplate>().ConfigureTenantColumn();
        modelBuilder.Entity<NotificationTemplate>().ToTable("notification_templates");
        modelBuilder.Entity<NotificationTemplate>().Property(t => t.Channel).HasConversion<string>();
        modelBuilder.Entity<NotificationTemplate>()
            .HasIndex(t => new { t.TenantSlug, t.Code, t.Channel, t.Locale }).IsUnique();

        modelBuilder.Entity<Notification>().ConfigureTenantColumn();
        modelBuilder.Entity<Notification>().ToTable("notification_messages");
        modelBuilder.Entity<Notification>().Property(n => n.Channel).HasConversion<string>();
        modelBuilder.Entity<Notification>().Property(n => n.Status).HasConversion<string>();
        modelBuilder.Entity<Notification>()
            .HasIndex(n => new { n.RecipientEmployeeId, n.Status });
    
                // Kafka idempotency tablosu - TUM tuketici servisler bu ORTAK tabloyu
        // paylasir (Consumer sutunu hangi servisin isledigini ayirt eder).
        // TenantSlug yok, tenant filtrelerine dahil edilmemeli.
        modelBuilder.Entity<ProcessedEvent>().ToTable("messaging_processed_events");
        modelBuilder.Entity<ProcessedEvent>().HasKey(p => new { p.EventId, p.Consumer });

        modelBuilder.Entity<NotificationPreference>().ConfigureTenantColumn();
        modelBuilder.Entity<NotificationPreference>().ToTable("notification_preferences");
        modelBuilder.Entity<NotificationPreference>().HasKey(p => new { p.TenantSlug, p.EmployeeId });

        modelBuilder.Entity<CategoryPreference>().ConfigureTenantColumn();
        modelBuilder.Entity<CategoryPreference>().ToTable("notification_category_prefs");
        modelBuilder.Entity<CategoryPreference>().HasKey(p => new { p.TenantSlug, p.EmployeeId, p.Category });

        modelBuilder.Entity<PushSubscription>().ConfigureTenantColumn();
        modelBuilder.Entity<PushSubscription>().ToTable("notification_push_subscriptions");
        modelBuilder.Entity<PushSubscription>().HasIndex(p => p.Endpoint).IsUnique();
        modelBuilder.Entity<PushSubscription>().HasIndex(p => new { p.TenantSlug, p.EmployeeId });

        modelBuilder.Entity<VapidKeyRow>().ToTable("notification_vapid_keys");
        modelBuilder.Entity<VapidKeyRow>().Property(v => v.Id).ValueGeneratedNever();

        // Dalga 12: arayüz tercihleri (yazım ham SQL upsert ile; bkz. UiPrefsController).
        modelBuilder.Entity<UiPreference>().ConfigureTenantColumn();
        modelBuilder.Entity<UiPreference>().ToTable("notification_ui_prefs");
        modelBuilder.Entity<UiPreference>().Property(p => p.Value).HasColumnType("jsonb");
        modelBuilder.Entity<UiPreference>().HasIndex(p => new { p.TenantSlug, p.UserSub, p.Key }).IsUnique();

        modelBuilder.ApplyTenantFilters(this);
    }
}

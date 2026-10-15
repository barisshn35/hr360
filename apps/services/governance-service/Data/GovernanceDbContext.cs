using Microsoft.EntityFrameworkCore;
using GovernanceService.Models;
using GovernanceService.Tenancy;

namespace GovernanceService.Data;

/// <summary>Yönetişim modülü tabloları (governance_*). Şema SQL göçüyle oluşturulur.</summary>
public class GovernanceDbContext : DbContext, ITenantAwareContext
{
    private readonly ITenantContext _tenant;

    public GovernanceDbContext(DbContextOptions<GovernanceDbContext> options, ITenantContext tenant)
        : base(options) => _tenant = tenant;

    public string? CurrentTenantSlug => _tenant.TenantSlug;
    public bool CurrentIsPlatformAdmin => _tenant.IsPlatformAdmin;

    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        this.StampTenant(_tenant);
        return base.SaveChangesAsync(ct);
    }

    public DbSet<GovernanceEvent> Events => Set<GovernanceEvent>();
    public DbSet<Consent> Consents => Set<Consent>();
    public DbSet<DataRequest> DataRequests => Set<DataRequest>();
    public DbSet<PrivacyNotice> PrivacyNotices => Set<PrivacyNotice>();
    public DbSet<DataBreach> DataBreaches => Set<DataBreach>();
    public DbSet<PrivacyAssessment> PrivacyAssessments => Set<PrivacyAssessment>();
    public DbSet<RetentionPolicy> RetentionPolicies => Set<RetentionPolicy>();
    public DbSet<TransferAgreement> TransferAgreements => Set<TransferAgreement>();
    public DbSet<DestructionLog> DestructionLogs => Set<DestructionLog>();
    public DbSet<AnalysisObjection> AnalysisObjections => Set<AnalysisObjection>();
    public DbSet<DocTemplate> DocTemplates => Set<DocTemplate>();
    public DbSet<DocumentRequest> DocumentRequests => Set<DocumentRequest>();
    public DbSet<Rule> Rules => Set<Rule>();
    public DbSet<RuleRun> RuleRuns => Set<RuleRun>();
    public DbSet<Webhook> Webhooks => Set<Webhook>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<Integration> Integrations => Set<Integration>();
    public DbSet<ChatApp> ChatApps => Set<ChatApp>();
    public DbSet<ChatIdentity> ChatIdentities => Set<ChatIdentity>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<ChatOutbox> ChatOutbox => Set<ChatOutbox>();
    public DbSet<ProviderConfig> ProviderConfigs => Set<ProviderConfig>();
    public DbSet<CalendarConnection> CalendarConnections => Set<CalendarConnection>();
    public DbSet<OAuthState> OAuthStates => Set<OAuthState>();
    public DbSet<CalendarEventLink> CalendarEventLinks => Set<CalendarEventLink>();
    public DbSet<Meeting> Meetings => Set<Meeting>();
    public DbSet<AiSettings> AiSettings => Set<AiSettings>();
    public DbSet<AiUsage> AiUsage => Set<AiUsage>();
    public DbSet<CalendarFeed> CalendarFeeds => Set<CalendarFeed>();
    public DbSet<KbArticle> KbArticles => Set<KbArticle>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Olaylar kiracıya göre elle filtrelenir (TenantSlug boş olabilir), ITenantOwned değil.
        b.Entity<GovernanceEvent>().ToTable("governance_events");
        b.Entity<Consent>().ToTable("governance_consents").ConfigureTenantColumn();
        b.Entity<DataRequest>().ToTable("governance_data_requests").ConfigureTenantColumn();
        b.Entity<RetentionPolicy>().ToTable("governance_retention_policies").ConfigureTenantColumn();
        b.Entity<TransferAgreement>().ToTable("governance_transfer_agreements").ConfigureTenantColumn();
        b.Entity<DestructionLog>().ToTable("governance_destruction_logs").ConfigureTenantColumn();
        b.Entity<AnalysisObjection>().ToTable("governance_analysis_objections").ConfigureTenantColumn();
        b.Entity<DocTemplate>().ToTable("governance_doc_templates").ConfigureTenantColumn();
        b.Entity<PrivacyNotice>().ToTable("governance_privacy_notices").ConfigureTenantColumn();
        b.Entity<DataBreach>().ToTable("governance_data_breaches").ConfigureTenantColumn();
        b.Entity<PrivacyAssessment>().ToTable("governance_privacy_assessments").ConfigureTenantColumn();
        b.Entity<DocumentRequest>().ToTable("governance_document_requests").ConfigureTenantColumn();
        b.Entity<DocumentRequest>().HasIndex(r => r.VerificationCode).IsUnique();
        b.Entity<DocumentRequest>().HasIndex(r => new { r.TenantSlug, r.EmployeeId });
        b.Entity<Rule>().ToTable("governance_rules").ConfigureTenantColumn();
        b.Entity<Rule>().Property(x => x.Conditions).HasColumnType("jsonb");
        b.Entity<Rule>().Property(x => x.Actions).HasColumnType("jsonb");
        b.Entity<RuleRun>().ToTable("governance_rule_runs").ConfigureTenantColumn();
        b.Entity<Webhook>().ToTable("governance_webhooks").ConfigureTenantColumn();
        b.Entity<WebhookDelivery>().ToTable("governance_webhook_deliveries").ConfigureTenantColumn();
        b.Entity<ApiKey>().ToTable("governance_api_keys").ConfigureTenantColumn();
        b.Entity<Integration>().ToTable("governance_integrations").ConfigureTenantColumn();
        b.Entity<ChatApp>().ToTable("governance_chat_apps").ConfigureTenantColumn();
        b.Entity<ChatIdentity>().ToTable("governance_chat_identities").ConfigureTenantColumn();
        b.Entity<ChatMessage>().ToTable("governance_chat_messages").ConfigureTenantColumn();
        b.Entity<ChatOutbox>().ToTable("governance_chat_outbox").ConfigureTenantColumn();
        b.Entity<ProviderConfig>().ToTable("governance_provider_configs").ConfigureTenantColumn();
        b.Entity<CalendarConnection>().ToTable("governance_calendar_connections").ConfigureTenantColumn();
        b.Entity<OAuthState>().ToTable("governance_oauth_states").HasKey(x => x.State);
        b.Entity<OAuthState>().ConfigureTenantColumn();
        b.Entity<CalendarEventLink>().ToTable("governance_calendar_events").ConfigureTenantColumn();
        b.Entity<Meeting>().ToTable("governance_meetings").ConfigureTenantColumn();
        b.Entity<AiSettings>().ToTable("governance_ai_settings").ConfigureTenantColumn();
        b.Entity<AiUsage>().ToTable("governance_ai_usage").ConfigureTenantColumn();
        b.Entity<CalendarFeed>().ToTable("governance_calendar_feeds").ConfigureTenantColumn();
        b.Entity<KbArticle>().ToTable("governance_kb_articles").ConfigureTenantColumn();

        b.ApplyTenantFilters(this);
    }
}

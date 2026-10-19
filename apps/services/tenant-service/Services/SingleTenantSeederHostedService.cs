using Microsoft.EntityFrameworkCore;
using TenantService.Controllers;
using TenantService.Data;
using TenantService.Models;
using TenantService.Security;

namespace TenantService.Services;

/// <summary>
/// Tek şirket kipi (HR360_TENANCY=single): kurulumda adı verilen şirketi (HR360_COMPANY_NAME) bir kez
/// oluşturur ve realm şablonundaki "yonetici" hesabını (COMPANY_ADMIN_EMAIL; tenant-admin +
/// single-platform-manage) o şirketin Keycloak organizasyonuna ekler. Akış DemoTenantSeederHostedService
/// ile aynıdır: kullanıcı zaten var, yalnızca organizasyon, üyelik ve organization_companies satırı açılır.
/// Herhangi bir etkin/askıdaki şirket varsa hiçbir şey yapmaz (çok şirketliden geçişte mevcut şirket kalır).
/// </summary>
public class SingleTenantSeederHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SingleTenantSeederHostedService> _logger;
    private readonly string _companyName;
    private readonly string? _adminEmail;

    public SingleTenantSeederHostedService(
        IServiceScopeFactory scopeFactory, ILogger<SingleTenantSeederHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _companyName = SingleTenantRules.CompanyNameFromEnv() ?? "";
        _adminEmail = SingleTenantRules.AdminEmailFromEnv();
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        HR360.Platform.LeaderGate.RunAsync("tenant/SingleTenantSeederHostedService", stoppingToken, LeaderLoopAsync);

    private async Task LeaderLoopAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_adminEmail))
        {
            _logger.LogWarning("Tek şirket kipi: COMPANY_ADMIN_EMAIL tanımlı değil, şirket otomatik oluşturulmadı.");
            return;
        }
        // Keycloak'ın ilk açılışı (realm içe aktarma) küçük sunucularda dakikalar sürebilir: 10 dk denenir.
        const int maxAttempts = 120;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (stoppingToken.IsCancellationRequested) return;
            try
            {
                if (await TrySeedAsync(stoppingToken)) return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Tek şirket kurulumu denemesi {Attempt}/{Max} başarısız, tekrar denenecek", attempt, maxAttempts);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
        _logger.LogWarning("Tek şirket oluşturulamadı ({Max} deneme sonrası vazgeçildi)", maxAttempts);
    }

    /// <returns>true: iş kesinleşti (oluşturuldu ya da gerek yok); false: yönetici hesabı henüz yok, yeniden dene.</returns>
    private async Task<bool> TrySeedAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
        var keycloak = scope.ServiceProvider.GetRequiredService<KeycloakAdminClient>();
        var provisioning = scope.ServiceProvider.GetRequiredService<TenantProvisioningService>();

        var existing = await db.Tenants.ToListAsync(ct);
        var decision = SingleTenantRules.Decide(existing.Select(t => t.Status));
        if (decision == SingleTenantRules.Decision.AlreadyHasCompany)
        {
            _logger.LogInformation("Tek şirket kipi: şirket zaten var, oluşturma atlanıyor.");
            return true;
        }

        // Yarım kalmış önceki deneme: Pending satırlar ve varsa açılmış organizasyon temizlenir.
        foreach (var stale in existing.Where(t => t.Status == TenantStatus.Pending))
        {
            _logger.LogWarning("Yarım kalmış şirket kaydı temizleniyor: {Slug}", stale.Slug);
            if (stale.KeycloakOrgId is not null) await keycloak.DeleteOrganizationAsync(stale.KeycloakOrgId, ct);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $@"DELETE FROM organization_companies WHERE ""TenantSlug"" = {stale.Slug}", ct);
            db.Tenants.Remove(stale);
        }
        await db.SaveChangesAsync(ct);

        var userId = await keycloak.FindUserByEmailAsync(_adminEmail!, ct);
        if (userId is null)
        {
            _logger.LogInformation("Tek şirket kipi: Keycloak'ta '{Email}' henüz yok, tekrar denenecek.", _adminEmail);
            return false;
        }

        var slug = await provisioning.BuildUniqueSlugAsync(_companyName, ct);
        if (RegistrationController.ReservedSlugs.Contains(slug))
            slug = await provisioning.BuildUniqueSlugAsync($"{slug} sirket", ct);

        var tenant = new Tenant
        {
            Name = _companyName,
            Slug = slug,
            AdminEmail = _adminEmail!,
            AdminFullName = "Şirket Yöneticisi",
            Status = TenantStatus.Pending,
            MfaPolicy = MfaPolicyRules.NewTenantDefault,
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(ct);
        await provisioning.LogStepAsync(tenant.Id, ProvisioningStep.TenantRecordCreated, true, slug, ct);

        var orgId = await keycloak.CreateOrganizationAsync(_companyName, slug, null, ct);
        tenant.KeycloakOrgId = orgId;
        await db.SaveChangesAsync(ct);
        await provisioning.LogStepAsync(tenant.Id, ProvisioningStep.KeycloakOrgCreated, true, orgId, ct);

        // Rol (tenant-admin, single-platform-manage) realm şablonunda atanmış durumda.
        await keycloak.AddOrganizationMemberAsync(orgId, userId, ct);
        tenant.AdminUserId = userId;
        await db.SaveChangesAsync(ct);
        await provisioning.LogStepAsync(tenant.Id, ProvisioningStep.AdminUserCreated, true, userId, ct);
        // Diğer yeni şirketlerle aynı: iki adımlı doğrulama ilk girişte kurulur.
        await keycloak.RequireOtpSetupAsync(userId, ct);

        var companyId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO organization_companies (""Id"", ""Name"", ""TaxNumber"", ""TenantSlug"", ""CreatedAt"")
            VALUES ({companyId}, {_companyName}, {(string?)null}, {slug}, {DateTimeOffset.UtcNow})
            ON CONFLICT DO NOTHING", ct);
        await provisioning.LogStepAsync(tenant.Id, ProvisioningStep.CompanyRecordCreated, true, companyId.ToString(), ct);

        tenant.Status = TenantStatus.Active;
        tenant.ActivatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await provisioning.LogStepAsync(tenant.Id, ProvisioningStep.Completed, true, null, ct);

        _logger.LogInformation("Tek şirket oluşturuldu: slug={Slug}, keycloakOrgId={OrgId}, adminUserId={UserId}", slug, orgId, userId);
        return true;
    }
}

/// <summary>Tek şirket kipi kararları (birim testleri için saf yardımcılar).</summary>
public static class SingleTenantRules
{
    /// <summary>Tek şirket kipinde şirket yöneticisine sistem ayarları ve bakım yetkisi veren realm rolü.
    /// Çok şirketli kipte hiçbir yerde geçerli değildir; IsPlatformAdmin'i (kiracı filtresi atlama) asla açmaz.</summary>
    public const string PlatformManageRole = "single-platform-manage";

    /// <summary>RequirePlatformAdmin politikası: platform yöneticisi, "platform:manage" ek izni ya da
    /// (yalnızca tek şirket kipinde) şirket yöneticisinin bakım rolü.</summary>
    public static bool CanManagePlatform(System.Security.Claims.ClaimsPrincipal user) =>
        user.IsInRole("platform-admin") || user.IsInRole("ext-platform-manage") ||
        (HR360.Platform.Modules.IsSingleTenant && user.IsInRole(PlatformManageRole));

    public enum Decision { Seed, AlreadyHasCompany }

    /// <summary>Pending dışında (Active/Suspended/Cancelled) bir şirket varsa yeni şirket açılmaz.</summary>
    public static Decision Decide(IEnumerable<TenantStatus> statuses) =>
        statuses.Any(s => s != TenantStatus.Pending) ? Decision.AlreadyHasCompany : Decision.Seed;

    /// <summary>Tohumlayıcı yalnızca tek şirket kipinde ve şirket adı verilmişken çalışır.</summary>
    public static bool ShouldRegister(bool singleTenant, string? companyName) =>
        singleTenant && !string.IsNullOrWhiteSpace(companyName);

    public static string? CompanyNameFromEnv() => Clean(Environment.GetEnvironmentVariable("HR360_COMPANY_NAME"));

    public static string? AdminEmailFromEnv() => Clean(Environment.GetEnvironmentVariable("COMPANY_ADMIN_EMAIL"));

    /// <summary>Boşlukları ve .env'den kalmış çevreleyen tırnakları ayıklar; boşsa null.</summary>
    public static string? Clean(string? raw)
    {
        var v = raw?.Trim();
        if (v is { Length: >= 2 } && ((v[0] == '\'' && v[^1] == '\'') || (v[0] == '"' && v[^1] == '"')))
            v = v[1..^1].Trim();
        return string.IsNullOrEmpty(v) ? null : v;
    }
}

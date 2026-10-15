using Microsoft.EntityFrameworkCore;
using TenantService.Data;
using TenantService.Models;

namespace TenantService.Services;

/// <summary>
/// install.sh, "demo.admin" adinda statik bir Keycloak kullanicisi (realm-export.json
/// icinde tanimli) uretip "Demo giris: demo.admin / ..." diye kullaniciya soyluyor -
/// ama bu kullanici Keycloak'ta HICBIR organizasyona/tenant'a bagli degildi ve
/// platform_tenants tablosunda karsilik gelen bir satir HICBIR ZAMAN yoktu
/// (hardcore test sirasinda bulundu). Sonuc: demo.admin parolayla dogru sekilde
/// Keycloak'a giris yapabiliyor ama frontend "/api/tenant/my-tenant" 404
/// donuyor ("Kullanıcı bir şirkete bağlı değil") ve panel hicbir zaman acilmiyor -
/// yani reklam edilen demo giris islevsizdi.
///
/// Bu servis, ayni akisi (KeycloakAdminClient.CreateOrganizationAsync +
/// AddOrganizationMemberAsync) gercek "sirket kaydi" sihirbazinin kullandigi
/// KOD ILE, sadece "demo.admin" zaten var olan bir Keycloak kullanicisi
/// oldugu icin CreateUserAsync yerine FindUserByEmailAsync ile calistirarak,
/// tek seferlik "demo" tenant'ini olusturur. platform_tenants'ta "demo" slug'i
/// zaten varsa hicbir sey yapmaz (idempotent). demo.admin Keycloak'ta yoksa
/// (ornegin production kurulumunda kullanici bu adimi atlamayi secmisse ya da
/// ileride realm-export'tan kaldirilirsa) sessizce atlar - bu servis gercek
/// kurulumlari asla bozmamali.
/// </summary>
public class DemoTenantSeederHostedService : BackgroundService
{
    private const string DemoSlug = "demo";
    private const string DemoEmail = "demo.admin@hr360.local";
    private const string DemoName = "Demo Şirket";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DemoTenantSeederHostedService> _logger;

    public DemoTenantSeederHostedService(
        IServiceScopeFactory scopeFactory, ILogger<DemoTenantSeederHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Keycloak realm-import'u ve bu servisin kendi DB semasi (postgres init
        // sirasinda tek seferde olusturuluyor) tenant-service ayaga kalktiginda
        // henuz hazir olmayabilir (docker-compose "service_started" ile bekliyor,
        // "service_healthy" ile degil). Uretim modundaki Keycloak'in ilk acilisi
        // (veritabani kurulumu + realm import) kucuk sunucularda birkac dakika
        // surebilir; 12 x 5 sn'de vazgecilince demo sirketi hic olusmuyordu.
        // 10 dakika boyunca denenir.
        const int maxAttempts = 120;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (stoppingToken.IsCancellationRequested) return;
            try
            {
                var done = await TrySeedAsync(stoppingToken);
                if (done) return;
                // demo.admin Keycloak'ta yok - baska bir deneme faydasiz, sessizce cik.
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Demo tenant seed denemesi {Attempt}/{Max} basarisiz, tekrar denenecek",
                    attempt, maxAttempts);
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (TaskCanceledException) { return; }
            }
        }
        _logger.LogWarning("Demo tenant seed edilemedi ({Max} deneme sonrasi vazgecildi)", maxAttempts);
    }

    /// <returns>true: islem kesinlesti (basariyla olusturuldu, zaten vardi, ya da
    /// demo.admin Keycloak'ta bulunamadi) - tekrar denemeye gerek yok.</returns>
    private async Task<bool> TrySeedAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
        var keycloak = scope.ServiceProvider.GetRequiredService<KeycloakAdminClient>();

        var existing = await db.Tenants.FirstOrDefaultAsync(t => t.Slug == DemoSlug, ct);
        if (existing is not null)
        {
            if (existing.Status == TenantStatus.Active)
            {
                _logger.LogInformation("Demo tenant zaten mevcut ve aktif, seed atlaniyor.");
                return true;
            }
            // Onceki deneme yarida kalmis (orn. Keycloak cagrisi patladi) - yetim
            // "Pending" satiri temizleyip yeniden deniyoruz, ProvisionAsync'deki
            // telafi/rollback mantigiyla ayni yaklasim.
            _logger.LogWarning(
                "Yarim kalmis demo tenant kaydi bulundu (Status={Status}), temizlenip yeniden denenecek.",
                existing.Status);
            db.Tenants.Remove(existing);
            await db.SaveChangesAsync(ct);
        }

        var userId = await keycloak.FindUserByEmailAsync(DemoEmail, ct);
        if (userId is null)
        {
            _logger.LogInformation(
                "Keycloak'ta '{Email}' kullanicisi bulunamadi (demo hesabi devre disi/kaldirilmis olabilir) - demo tenant seed atlaniyor.",
                DemoEmail);
            return true;
        }

        var tenant = new Tenant
        {
            Name = DemoName,
            Slug = DemoSlug,
            AdminEmail = DemoEmail,
            AdminFullName = "Demo Admin",
            Status = TenantStatus.Pending,
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(ct);

        var orgId = await keycloak.CreateOrganizationAsync(DemoName, DemoSlug, null, ct);
        tenant.KeycloakOrgId = orgId;
        await keycloak.AddOrganizationMemberAsync(orgId, userId, ct);
        // tenant-admin rolu demo.admin'e zaten realm sablonunda atanmis durumda -
        // burada tekrar atamaya gerek yok.
        tenant.AdminUserId = userId;

        var companyId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO organization_companies (""Id"", ""Name"", ""TaxNumber"", ""TenantSlug"", ""CreatedAt"")
            VALUES ({companyId}, {DemoName}, {(string?)null}, {DemoSlug}, {DateTimeOffset.UtcNow})
            ON CONFLICT DO NOTHING", ct);

        tenant.Status = TenantStatus.Active;
        tenant.ActivatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Demo tenant olusturuldu: slug={Slug}, keycloakOrgId={OrgId}, adminUserId={UserId}",
            DemoSlug, orgId, userId);
        return true;
    }
}

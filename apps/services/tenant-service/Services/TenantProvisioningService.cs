using Microsoft.EntityFrameworkCore;
using TenantService.Data;
using TenantService.Models;
using TenantService.Security;

namespace TenantService.Services;

/// <summary>
/// Sirket kayit akisini yurutur. Birden fazla dis sistemi etkiledigi icin
/// (PostgreSQL + Keycloak) her adim loglanir; ortada hata olursa
/// olusturulanlar geri alinir (telafi/compensation).
/// </summary>
public class TenantProvisioningService
{
    private readonly TenantDbContext _db;
    private readonly KeycloakAdminClient _keycloak;
    private readonly ILogger<TenantProvisioningService> _logger;

    public TenantProvisioningService(
        TenantDbContext db, KeycloakAdminClient keycloak,
        ILogger<TenantProvisioningService> logger)
    {
        _db = db;
        _keycloak = keycloak;
        _logger = logger;
    }

    private async Task LogAsync(
        Guid tenantId, ProvisioningStep step, bool success, string? detail, CancellationToken ct)
    {
        _db.ProvisioningLogs.Add(new TenantProvisioningLog
        {
            TenantId = tenantId,
            Step = step,
            Success = success,
            Detail = detail,
        });
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Saglama adimi kaydi (parola linki sonucu gibi cagiran tarafin adimlari icin).</summary>
    public Task LogStepAsync(Guid tenantId, ProvisioningStep step, bool ok, string? detail, CancellationToken ct) =>
        LogAsync(tenantId, step, ok, detail, ct);

    public async Task<Tenant> ProvisionAsync(
        string name, string slug, string adminEmail, string? adminFullName,
        string? emailDomain, string? taxNumber, CancellationToken ct)
    {
        // --- 1. Tenant kaydi ---
        var tenant = new Tenant
        {
            Name = name,
            Slug = slug,
            AdminEmail = adminEmail,
            AdminFullName = adminFullName,
            EmailDomain = emailDomain,
            TaxNumber = taxNumber,
            Status = TenantStatus.Pending,
            MfaPolicy = MfaPolicyRules.NewTenantDefault,
        };
        _db.Tenants.Add(tenant);
        await _db.SaveChangesAsync(ct);
        await LogAsync(tenant.Id, ProvisioningStep.TenantRecordCreated, true, slug, ct);

        string? orgId = null;
        string? userId = null;

        try
        {
            // --- 2. Keycloak organizasyonu ---
            orgId = await _keycloak.CreateOrganizationAsync(name, slug, emailDomain, ct);
            tenant.KeycloakOrgId = orgId;
            await _db.SaveChangesAsync(ct);
            await LogAsync(tenant.Id, ProvisioningStep.KeycloakOrgCreated, true, orgId, ct);

            // --- 3. Yonetici kullanici ---
            var parts = (adminFullName ?? "").Split(' ', 2);
            userId = await _keycloak.CreateUserAsync(
                adminEmail,
                parts.ElementAtOrDefault(0),
                parts.ElementAtOrDefault(1),
                tenant.Id, ct);

            tenant.AdminUserId = userId;
            await _db.SaveChangesAsync(ct);
            await LogAsync(tenant.Id, ProvisioningStep.AdminUserCreated, true, userId, ct);

            await _keycloak.AddOrganizationMemberAsync(orgId, userId, ct);

            // --- 4. tenant-admin rolu ---
            await _keycloak.AssignRealmRoleAsync(userId, "tenant-admin", ct);
            await LogAsync(tenant.Id, ProvisioningStep.AdminRoleAssigned, true, "tenant-admin", ct);

            // Iki adimli dogrulama yeni sirketlerde varsayilan olarak zorunlu: kurucu ilk girisinde
            // parolasini belirledikten hemen sonra dogrulayici kurar (duzenli tur 10 dk sonra gelirdi).
            await _keycloak.RequireOtpSetupAsync(userId, ct);

            // --- 4b. Uygulama-ici Company kaydi (organization-service) ---
            //
            // Onceki surumde bu adim YOKTU: kayit sihirbazi platform_tenants'a
            // (bu servisin kendi tablosu) satir dusuyordu ama organization-service'in
            // organization_companies tablosuna hic dokunmuyordu. Kullanici giris
            // yapip "Organizasyon" ekranina gidince orada hicbir sirket bulamiyor,
            // sistem "sirket kaydedin" diyordu - sanki hic kayit olmamis gibi.
            //
            // organization-service ayri bir mikroservis ama AYNI FIZIKSEL
            // VERITABANINI (hr360_operational) paylasiyor - HTTP uzerinden
            // gitmek icin anonim bir "internal provisioning" ucu acmak
            // (guvenlik riski) yerine, dogrudan SQL ile tek satir eklemek
            // burada daha basit ve daha az riskli. Sirket adini/vergi
            // numarasini kullanici zaten sihirbazda girdi, ikinci kez
            // sormaya gerek yok.
            var companyId = Guid.NewGuid();
            await _db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO organization_companies (""Id"", ""Name"", ""TaxNumber"", ""TenantSlug"", ""CreatedAt"")
                VALUES ({companyId}, {name}, {taxNumber}, {slug}, {DateTimeOffset.UtcNow})
                ON CONFLICT DO NOTHING", ct);
            await LogAsync(tenant.Id, ProvisioningStep.CompanyRecordCreated, true, companyId.ToString(), ct);

            // --- 5. Parola belirleme ---
            // Link (ya da eski yontemde Keycloak e-postasi) cagiran tarafta PasswordLinkService ile
            // uretilir: kimin gorecegi (kayit olan / platform yoneticisi) orada belirlenir.

            tenant.Status = TenantStatus.Active;
            tenant.ActivatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            await LogAsync(tenant.Id, ProvisioningStep.Completed, true, null, ct);

            _logger.LogInformation("Tenant saglandi: {Slug} ({Id})", slug, tenant.Id);
            return tenant;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tenant saglama basarisiz: {Slug}. Telafi calisiyor.", slug);
            await LogAsync(tenant.Id, ProvisioningStep.Failed, false, ex.Message, ct);

            // --- Telafi: ters sirada geri al ---
            if (userId is not null)
                await _keycloak.DeleteUserAsync(userId, ct);
            if (orgId is not null)
                await _keycloak.DeleteOrganizationAsync(orgId, ct);

            // Company kaydi olusmus olabilir (4b adimindan sonra patladiysa) -
            // yetim satir kalmasin.
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $@"DELETE FROM organization_companies WHERE ""TenantSlug"" = {slug}", ct);

            _db.Tenants.Remove(tenant);
            await _db.SaveChangesAsync(ct);

            throw;
        }
    }

    /// <summary>Slug'i normalize eder ve benzersiz olmasini saglar.</summary>
    public async Task<string> BuildUniqueSlugAsync(string name, CancellationToken ct)
    {
        var map = new Dictionary<char, char>
        {
            ['ç'] = 'c', ['ğ'] = 'g', ['ı'] = 'i', ['ö'] = 'o', ['ş'] = 's', ['ü'] = 'u',
        };

        // NOT: Yalnizca a-z/0-9 tutulur (RegistrationController'daki slug kuraliyla
        // ayni). Onceden char.IsLetterOrDigit ASCII disi harfleri (e, kiril, "İ"nin
        // ToLowerInvariant ile olusan birlesik noktasi) birakiyordu. Taban 34
        // karakterle sinirli ki "-NN" eki eklense de 40'i asmasin; 3 karakterden
        // kisa adlar "-sirket" ile tamamlanir.
        var chars = name.Replace('İ', 'i').Replace('I', 'ı').ToLowerInvariant()
            .Select(c => map.TryGetValue(c, out var r) ? r : c)
            .Select(c => (c is >= 'a' and <= 'z') || (c is >= '0' and <= '9') ? c : '-');

        var baseSlug = new string(chars.ToArray());
        while (baseSlug.Contains("--")) baseSlug = baseSlug.Replace("--", "-");
        baseSlug = baseSlug.Trim('-');
        if (baseSlug.Length > 34) baseSlug = baseSlug[..34].Trim('-');
        if (string.IsNullOrWhiteSpace(baseSlug)) baseSlug = "sirket";
        else if (baseSlug.Length < 3) baseSlug = $"{baseSlug}-sirket";

        var slug = baseSlug;
        var suffix = 2;
        while (await _db.Tenants.AnyAsync(t => t.Slug == slug, ct))
        {
            slug = $"{baseSlug}-{suffix++}";
        }
        return slug;
    }
}

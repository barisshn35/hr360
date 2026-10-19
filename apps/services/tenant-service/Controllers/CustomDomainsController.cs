using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using TenantService.Data;
using TenantService.Domains;
using TenantService.Models;
using TenantService.Security;
using TenantService.Services;

namespace TenantService.Controllers;

/// <summary>
/// G28: kiraciya ozel alan adi (ornegin ik.acme.com.tr) - beyaz etiketleme.
/// Sahiplik DNS TXT kaydiyla kanitlanir: _hr360-verify.&lt;alan adi&gt; = dogrulama jetonu. Dogrulanan
/// alan adi, giris ekraninin kiraci markasini gostermesi ve uygulama istemcisinin (hr360-web) o
/// adrese geri donebilmesi icin kullanilir. TLS sertifikasi (mevcut ACME betikleri) ve gateway
/// server_name ayari isletme adimidir - bkz. README "Özel alan adı".
/// </summary>
[ApiController]
[Route("api/my-tenant/domains")]
[Authorize(Policy = "RequireTenantAdmin")]
public class CustomDomainsController : ControllerBase
{
    public const int MaxDomainsPerTenant = 5;

    private readonly TenantDbContext _db;
    private readonly ITxtResolver _dns;
    private readonly KeycloakAdminClient _kc;
    private readonly ILogger<CustomDomainsController> _log;

    public CustomDomainsController(TenantDbContext db, ITxtResolver dns, KeycloakAdminClient kc, ILogger<CustomDomainsController> log)
    {
        _db = db;
        _dns = dns;
        _kc = kc;
        _log = log;
    }

    private async Task<Tenant?> MyTenantAsync(CancellationToken ct)
    {
        var slug = OrganizationClaimParser.ParseSlug(User.FindFirst("organization")?.Value);
        return string.IsNullOrWhiteSpace(slug) ? null : await _db.Tenants.FirstOrDefaultAsync(t => t.Slug == slug, ct);
    }

    private static object View(CustomDomain d) => new
    {
        d.Id, d.Domain, d.Status, d.CreatedAt, d.VerifiedAt, d.LastCheckedAt, d.LastCheckError,
        txtName = DomainValidator.TxtName(d.Domain),
        txtValue = d.VerificationToken,
        loginUrl = $"https://{d.Domain}/giris",
    };

    /// <summary>Uygulama istemcisinin yonlendirme listesine yazilacak koken (varsayilan https).</summary>
    private static string OriginOf(string domain) =>
        $"{(Environment.GetEnvironmentVariable("CUSTOM_DOMAIN_SCHEME") ?? "https").Trim()}://{domain}";

    private static bool KeycloakSyncEnabled =>
        !string.Equals(Environment.GetEnvironmentVariable("CUSTOM_DOMAIN_KEYCLOAK_SYNC"), "false", StringComparison.OrdinalIgnoreCase);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var tenant = await MyTenantAsync(ct);
        if (tenant is null) return NotFound(new { message = "Kullanıcı bir şirkete bağlı değil" });
        var rows = await _db.CustomDomains.Where(d => d.TenantSlug == tenant.Slug).OrderBy(d => d.CreatedAt).ToListAsync(ct);
        return Ok(new { items = rows.Select(View) });
    }

    public record AddDomainInput(string? Domain);

    [HttpPost]
    public async Task<IActionResult> Add([FromBody] AddDomainInput body, CancellationToken ct)
    {
        var tenant = await MyTenantAsync(ct);
        if (tenant is null) return NotFound(new { message = "Kullanıcı bir şirkete bağlı değil" });
        var (domain, error) = DomainValidator.Normalize(body.Domain, DomainValidator.PlatformHostsFromEnv());
        if (error is not null) return BadRequest(new { message = error });
        if (await _db.CustomDomains.AnyAsync(d => d.TenantSlug == tenant.Slug && d.Domain == domain, ct))
            return Conflict(new { message = "Bu alan adı zaten eklenmiş" });
        if (await _db.CustomDomains.AnyAsync(d => d.Domain == domain && d.Status == CustomDomainStatus.Verified, ct))
            return Conflict(new { message = "Bu alan adı başka bir şirket tarafından kullanılıyor" });
        if (await _db.CustomDomains.CountAsync(d => d.TenantSlug == tenant.Slug, ct) >= MaxDomainsPerTenant)
            return Conflict(new { message = $"En fazla {MaxDomainsPerTenant} alan adı eklenebilir" });
        var row = new CustomDomain { TenantSlug = tenant.Slug, Domain = domain!, VerificationToken = DomainValidator.NewToken() };
        _db.CustomDomains.Add(row);
        await _db.SaveChangesAsync(ct);
        return Ok(View(row));
    }

    /// <summary>TXT kaydini sorgular; eslesirse alan adi dogrulanmis olur.</summary>
    [HttpPost("{id:guid}/verify")]
    [EnableRateLimiting("domain-verify")]
    public async Task<IActionResult> Verify(Guid id, CancellationToken ct)
    {
        var tenant = await MyTenantAsync(ct);
        if (tenant is null) return NotFound(new { message = "Kullanıcı bir şirkete bağlı değil" });
        var row = await _db.CustomDomains.FirstOrDefaultAsync(d => d.Id == id && d.TenantSlug == tenant.Slug, ct);
        if (row is null) return NotFound(new { message = "Alan adı bulunamadı" });

        var check = await DomainVerifier.CheckAsync(_dns, row.Domain, row.VerificationToken, ct);
        row.LastCheckedAt = DateTimeOffset.UtcNow;
        if (!check.Verified)
        {
            // Dogrulanmis alan adi sonraki kontrolde kayit kaldirilmis olsa da kapatilmaz; yonetici siler.
            row.LastCheckError = check.Error;
            await _db.SaveChangesAsync(ct);
            return BadRequest(new { message = $"{row.LastCheckError}. DNS'e {DomainValidator.TxtName(row.Domain)} adına TXT kaydı ekleyin; yayılması birkaç dakika sürebilir.", domain = View(row) });
        }
        if (await _db.CustomDomains.AnyAsync(d => d.Domain == row.Domain && d.Status == CustomDomainStatus.Verified && d.Id != row.Id, ct))
            return Conflict(new { message = "Bu alan adı başka bir şirkette doğrulanmış" });

        var firstVerification = row.Status != CustomDomainStatus.Verified;
        row.Status = CustomDomainStatus.Verified;
        row.VerifiedAt ??= DateTimeOffset.UtcNow;
        row.LastCheckError = null;
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { return Conflict(new { message = "Bu alan adı başka bir şirkette doğrulanmış" }); }

        string? warning = null;
        if (firstVerification && KeycloakSyncEnabled)
        {
            try { await _kc.SetWebClientOriginAsync(OriginOf(row.Domain), true, ct); }
            catch (InvalidOperationException ex)
            {
                _log.LogWarning("Ozel alan adi Keycloak istemcisine eklenemedi {Domain}: {Message}", row.Domain, ex.Message);
                warning = "Alan adı doğrulandı ancak giriş yönlendirmesi kaydedilemedi; platform yöneticisine bildirin.";
            }
        }
        return Ok(new { message = "Alan adı doğrulandı", warning, domain = View(row) });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var tenant = await MyTenantAsync(ct);
        if (tenant is null) return NotFound(new { message = "Kullanıcı bir şirkete bağlı değil" });
        var row = await _db.CustomDomains.FirstOrDefaultAsync(d => d.Id == id && d.TenantSlug == tenant.Slug, ct);
        if (row is null) return NotFound(new { message = "Alan adı bulunamadı" });
        var wasVerified = row.Status == CustomDomainStatus.Verified;
        _db.CustomDomains.Remove(row);
        await _db.SaveChangesAsync(ct);
        if (wasVerified && KeycloakSyncEnabled)
        {
            try { await _kc.SetWebClientOriginAsync(OriginOf(row.Domain), false, ct); }
            catch (InvalidOperationException ex) { _log.LogWarning("Ozel alan adi Keycloak istemcisinden kaldirilamadi {Domain}: {Message}", row.Domain, ex.Message); }
        }
        return Ok(new { message = "Alan adı kaldırıldı" });
    }
}

/// <summary>
/// Anonim, herkese acik uc: GET /api/tenant/public/branding?host=ik.acme.com.tr - giris ekrani,
/// tarayicinin bulundugu ozel alan adina gore kiracinin kisa adini (slug), adini, logosunu ve ana
/// rengini alir; kiraciyi on-secer ve temayi uygular. Yalnizca DOGRULANMIS alan adlari ve aktif
/// kiracilar icin yanit doner; baska hicbir kiraci bilgisi sizdirilmaz. Taninmayan host icin
/// 204 No Content: giris ekrani her acilista sorar, "marka yok" bir hata degildir (tarayici
/// konsolunda her giriste kirmizi 404 cikmasin).
/// </summary>
[ApiController]
[Route("api/public")]
[AllowAnonymous]
public class PublicTenantController : ControllerBase
{
    private readonly TenantDbContext _db;
    public PublicTenantController(TenantDbContext db) => _db = db;

    /// <summary>
    /// Giris ekraninin ihtiyac duydugu kurulum ayarlari (anonim): herkese acik sirket kaydi
    /// acik mi. Kapaliysa web "Sirketinizi kaydedin" girisini gizler, /kayit "Kayit kapali" gosterir.
    /// </summary>
    [HttpGet("config")]
    [EnableRateLimiting("public-resolve")]
    public async Task<IActionResult> Config([FromServices] PlatformSettingsService settings, CancellationToken ct)
    {
        Response.Headers.CacheControl = "public, max-age=30";
        // passwordResetByEmail: "Parolamı unuttum" e-postayla mı (platform ya da en az bir şirketin SMTP'si var),
        // yoksa "İK yöneticinize başvurun" mu gösterilecek.
        var resetByEmail = (await settings.SmtpAsync(ct)).Configured || await _db.Tenants.AnyAsync(t => t.SmtpHost != null, ct);
        // tenancy/companyName: tek şirket kipinde giriş ekranı şirket adını gösterir, kayıt girişini hiç sunmaz.
        string? companyName = null;
        if (HR360.Platform.Modules.IsSingleTenant)
            companyName = await _db.Tenants.Where(t => t.Status == TenantStatus.Active).OrderBy(t => t.CreatedAt)
                .Select(t => t.Name).FirstOrDefaultAsync(ct) ?? SingleTenantRules.CompanyNameFromEnv();
        return Ok(new
        {
            signupEnabled = await settings.SignupEnabledAsync(ct),
            passwordResetByEmail = resetByEmail,
            tenancy = HR360.Platform.Modules.Tenancy,
            companyName,
        });
    }

    [HttpGet("branding")]
    [EnableRateLimiting("public-resolve")]
    public async Task<IActionResult> Resolve([FromQuery] string? host, CancellationToken ct)
    {
        var domain = DomainValidator.NormalizeHost(host);
        if (domain is null) return NoContent();
        var hit = await (from d in _db.CustomDomains
                         join t in _db.Tenants on d.TenantSlug equals t.Slug
                         where d.Domain == domain && d.Status == CustomDomainStatus.Verified && t.Status == TenantStatus.Active
                         select new { t.Slug, t.Name, t.LogoUrl, t.PrimaryColorHex }).FirstOrDefaultAsync(ct);
        if (hit is null) return NoContent();
        Response.Headers.CacheControl = "public, max-age=300";
        return Ok(new
        {
            slug = hit.Slug,
            name = hit.Name,
            logoUrl = LogoStorageService.ToPublicUrl(hit.LogoUrl),
            primaryColorHex = hit.PrimaryColorHex,
        });
    }
}

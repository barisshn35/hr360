using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TenantService.Data;
using TenantService.Models;
using TenantService.Services;

namespace TenantService.Controllers;

/// <summary>
/// Sirket kayit ucu. Tek ANONIM erisilebilir controller - yeni musteriler
/// buradan kaydolur. Herkese acik kayit kapaliysa (PUBLIC_SIGNUP_ENABLED=false) kayit ve slug
/// denetimi 404 doner; marka ucu (notification-service kullanir) etkilenmez.
/// </summary>
[ApiController]
[Route("api/registration")]
[AllowAnonymous]
public partial class RegistrationController : ControllerBase
{
    private readonly TenantDbContext _db;
    private readonly TenantProvisioningService _provisioning;
    private readonly ILogger<RegistrationController> _logger;

    public RegistrationController(
        TenantDbContext db, TenantProvisioningService provisioning,
        ILogger<RegistrationController> logger)
    {
        _db = db;
        _provisioning = provisioning;
        _logger = logger;
    }

    [GeneratedRegex(@"^[a-z0-9](?:[a-z0-9-]{1,38})[a-z0-9]$")]

    private static partial Regex SlugRegex();


    /// <summary>Sirket kisa adi olarak alinamayan adlar (tek sirket kipi tohumlayicisi da kullanir).</summary>
    public static readonly IReadOnlySet<string> ReservedSlugs = new HashSet<string>(StringComparer.Ordinal)

    {

        "admin", "api", "auth", "www", "app", "platform", "panel", "static", "logos",

        "keycloak", "support", "destek", "mail", "root", "system", "hr360",

    };


    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailRegex();

    /// <summary>Slug musait mi - kayit formunda anlik kontrol icin.</summary>
    [HttpGet("slug-available")]
    [RequirePublicSignup]
    public async Task<IActionResult> SlugAvailable([FromQuery] string slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return BadRequest(new { message = "Slug bos olamaz" });

        var taken = await _db.Tenants.AnyAsync(t => t.Slug == slug.ToLowerInvariant());
        return Ok(new { slug = slug.ToLowerInvariant(), available = !taken });
    }

    /// <summary>
    /// Kiracinin marka bilgisi (logo, ana renk) - ANONIM. LogoStorageService
    /// zaten logoyu "/logos/" altinda herkese acik yayinliyor; bu uc da ayni
    /// mantikla, oturum acilmadan ONCE veya baska bir servisten erisilmesi
    /// gereken yerler icin marka bilgisini dondurur. Su an tek tuketicisi
    /// notification-service (e-posta bildirim basligi) - cross-service
    /// cagirir, uc anonim oldugu icin JWT pass-through'a ihtiyac yoktur.
    /// Ileride tenant-farkinda bir giris ekrani icin de kullanilabilir.
    /// </summary>
    [HttpGet("branding/{slug}")]
    public async Task<IActionResult> Branding(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return BadRequest(new { message = "Slug bos olamaz" });

        var tenant = await _db.Tenants
            .Where(t => t.Slug == slug.ToLowerInvariant())
            .Select(t => new { t.Name, t.LogoUrl, t.PrimaryColorHex })
            .FirstOrDefaultAsync();

        return tenant is null
            ? NotFound(new { message = $"'{slug}' icin tenant kaydi bulunamadi" })
            : Ok(new { tenant.Name, LogoUrl = LogoStorageService.ToPublicUrl(tenant.LogoUrl), tenant.PrimaryColorHex });
    }

    /// <summary>
    /// Yeni sirket kaydi. Basarili olursa yonetici e-postasina parola
    /// belirleme baglantisi gonderilir.
    /// </summary>
    [HttpPost]
    [RequirePublicSignup]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("registration")]
    public Task<IActionResult> Register(
        [FromBody] RegisterCompanyRequest request, [FromServices] PasswordLinkService links, CancellationToken ct)
        => CreateTenantAsync(this, _db, _provisioning, _logger, request, links, LinkDisplay.ShowIfNoEmail, new LinkActor(null, null), ct);

    /// <summary>
    /// Kiraci olusturmanin ortak yolu: anonim kayit (Register) ve platform yoneticisinin
    /// "Yeni kiraci" ucu (TenantsController.Create).
    /// </summary>
    internal static async Task<IActionResult> CreateTenantAsync(
        ControllerBase c, TenantDbContext db, TenantProvisioningService provisioning, ILogger logger,
        RegisterCompanyRequest request, PasswordLinkService links, LinkDisplay display, LinkActor actor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.CompanyName))
            return c.BadRequest(new { message = "Şirket adı zorunlu" });
        if (string.IsNullOrWhiteSpace(request.AdminEmail) || !EmailRegex().IsMatch(request.AdminEmail))
            return c.BadRequest(new { message = "Geçerli bir e-posta adresi girin" });

        var email = request.AdminEmail.Trim().ToLowerInvariant();

        // Ayni e-posta ile birden fazla tenant yonetimi bu surumde desteklenmiyor.
        if (await db.Tenants.AnyAsync(t => t.AdminEmail == email, ct))
        {
            return c.Conflict(new
            {
                message = "Bu e-posta ile kayıtlı bir şirket zaten var. " +
                          "Giris yapmayi deneyin ya da destek ile iletisime gecin.",
            });
        }

        if (request.CompanyName.Trim().Length > 150)
            return c.BadRequest(new { message = "Şirket adı en fazla 150 karakter olabilir" });

        var slug = string.IsNullOrWhiteSpace(request.Slug)
            ? await provisioning.BuildUniqueSlugAsync(request.CompanyName, ct)
            : request.Slug.Trim().ToLowerInvariant();
        // Otomatik uretilen ad ayrilmis bir ada denk gelirse ("Admin A.S." -> "admin-a-s"
        // degil ama "API" -> "api") kullaniciyi hataya dusurmek yerine tamamla.
        if (string.IsNullOrWhiteSpace(request.Slug) && ReservedSlugs.Contains(slug))
            slug = await provisioning.BuildUniqueSlugAsync($"{slug} sirket", ct);

        // GUVENLIK: Kullanicinin verdigi kisa ad (slug) hic dogrulanmiyordu -
        // bosluk/ozel karakter/64+ karakter ancak veritabani insert'unde 500 ile
        // patliyordu; "admin", "api" gibi ayrilmis adlar da alinabiliyordu.
        if (!SlugRegex().IsMatch(slug) || ReservedSlugs.Contains(slug))
            return c.BadRequest(new
            {
                message = "Kısa ad 3-40 karakter olmalı; yalnızca küçük harf, rakam ve tire " +
                          "içerebilir, tireyle başlayıp bitemez.",
            });

        if (await db.Tenants.AnyAsync(t => t.Slug == slug, ct))
            return c.Conflict(new { message = $"'{slug}' kisa adi kullanimda, baska bir ad secin" });

        try
        {
            var tenant = await provisioning.ProvisionAsync(
                request.CompanyName.Trim(), slug, email,
                request.AdminFullName?.Trim(), request.EmailDomain?.Trim(),
                request.TaxNumber?.Trim(), ct);

            // Parola: süreli davet linki (varsayılan) ya da eski yöntemde Keycloak e-postası. Bu adım
            // başarısız olsa da kayıt geçerlidir; platform yöneticisi linki yeniden üretebilir.
            PasswordLinkService.Issued? issued = null;
            try
            {
                issued = await links.IssueAsync(new PasswordLinkService.IssueRequest(
                    tenant, tenant.AdminUserId!, email, request.AdminFullName?.Trim(), PasswordInvitePurposes.Setup,
                    display, actor), ct);
                await provisioning.LogStepAsync(tenant.Id, ProvisioningStep.PasswordEmailSent, true,
                    $"{issued.Method}/{issued.EmailStatus}", ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Parola linki üretilemedi (kayıt geçerli)");
                await provisioning.LogStepAsync(tenant.Id, ProvisioningStep.PasswordEmailSent, false, ex.Message, ct);
            }

            return c.Ok(new
            {
                tenantId = tenant.Id,
                slug = tenant.Slug,
                companyName = tenant.Name,
                status = tenant.Status.ToString(),
                passwordLink = issued is null ? null : PasswordLinkView.From(issued),
                message = issued switch
                {
                    { Link: not null } => "Kaydınız oluşturuldu. Parolanızı aşağıdaki linkle belirleyin.",
                    { EmailStatus: "queued" or "keycloak" } => $"Kaydınız oluşturuldu. {email} adresine parola belirleme bağlantısı gönderildi.",
                    _ => "Kaydınız oluşturuldu. Parola belirleme linki platform yöneticisinden alınabilir.",
                },
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Kayıt başarısız: {Company}", request.CompanyName);
            return c.StatusCode(500, new
            {
                message = "Kayıt tamamlanamadı. Lütfen tekrar deneyin ya da destek ile " +
                          "iletişime geçin.",
            });
        }
    }
}

public record RegisterCompanyRequest(
    string CompanyName,
    string AdminEmail,
    string? AdminFullName,
    string? Slug,
    string? EmailDomain,
    string? TaxNumber);

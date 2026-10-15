using System.Security;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using TenantService.Data;
using TenantService.Domains;
using TenantService.Models;
using TenantService.Services;

namespace TenantService.Controllers;

/// <summary>
/// Herkese açık site bilgileri (anonim): SSS, gizlilik politikası, kullanım koşulları ve iletişim
/// sayfalarının içeriği; robots.txt ve sitemap.xml (gateway /robots.txt ve /sitemap.xml'i buraya
/// yönlendirir). İçerik Platform › Sistem ayarları'ndan gelir; boş metin = arayüzdeki varsayılan şablon.
/// Arama motoru görünürlüğü varsayılan KAPALI: robots.txt her şeyi engeller, sitemap boştur.
/// </summary>
[ApiController]
[Route("api/public")]
[AllowAnonymous]
public class PublicSiteController : ControllerBase
{
    /// <summary>Sitemap'teki herkese açık sayfalar (kayıt sayfası yalnızca kayıt açıkken).</summary>
    public static readonly string[] PublicPages = ["/giris", "/sss", "/gizlilik", "/kosullar", "/iletisim"];

    /// <summary>Dizinlenmemesi gereken yollar: oturumlu ekranlar, API, jetonlu bağlantılar.</summary>
    public static readonly string[] DisallowedPaths =
    [
        "/panel", "/api/", "/api-docs", "/auth/", "/grafana/", "/ml/", "/parola-belirle/", "/parola-sifirla",
        "/oturum-sona-erdi", "/tesekkurler", "/kiosk", "/onay-eposta", "/belge-dogrula", "/etik/",
        "/kariyer/*/basvuru/", "/kariyer/*/durum/", "/kariyer/*/teklif/",
    ];

    private readonly TenantDbContext _db;
    private readonly PlatformSettingsService _settings;

    public PublicSiteController(TenantDbContext db, PlatformSettingsService settings)
    {
        _db = db;
        _settings = settings;
    }

    [HttpGet("site")]
    [EnableRateLimiting("public-resolve")]
    public async Task<IActionResult> Site(CancellationToken ct)
    {
        Response.Headers.CacheControl = "public, max-age=60";
        return Ok(PlatformSettingsController.SiteView(await _settings.GetRowAsync(ct)));
    }

    [HttpGet("robots.txt")]
    [EnableRateLimiting("public-resolve")]
    public async Task<IActionResult> Robots(CancellationToken ct)
    {
        Response.Headers.CacheControl = "public, max-age=300";
        var indexing = await _settings.SearchIndexingAsync(ct);
        return Content(BuildRobots(indexing, await OriginAsync(ct)), "text/plain; charset=utf-8");
    }

    [HttpGet("sitemap.xml")]
    [EnableRateLimiting("public-resolve")]
    public async Task<IActionResult> Sitemap(CancellationToken ct)
    {
        Response.Headers.CacheControl = "public, max-age=300";
        var indexing = await _settings.SearchIndexingAsync(ct);
        var signup = await _settings.SignupEnabledAsync(ct);
        if (!indexing) Response.Headers["X-Robots-Tag"] = "noindex";
        return Content(BuildSitemap(indexing, signup, await OriginAsync(ct)), "application/xml; charset=utf-8");
    }

    public static string BuildRobots(bool indexing, string origin)
    {
        var sb = new StringBuilder();
        if (!indexing)
        {
            sb.Append("# Bu kurulum arama motorlarına kapalı (Platform › Sistem ayarları › Herkese açık site).\n");
            sb.Append("User-agent: *\nDisallow: /\n");
            return sb.ToString();
        }
        sb.Append("User-agent: *\n");
        foreach (var p in DisallowedPaths) sb.Append("Disallow: ").Append(p).Append('\n');
        sb.Append("Allow: /\n\n");
        sb.Append("Sitemap: ").Append(origin).Append("/sitemap.xml\n");
        return sb.ToString();
    }

    public static string BuildSitemap(bool indexing, bool signupEnabled, string origin)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n");
        if (indexing)
        {
            var pages = signupEnabled ? PublicPages.Append("/kayit") : PublicPages;
            foreach (var p in pages)
                sb.Append("  <url><loc>").Append(SecurityElement.Escape(origin + p)).Append("</loc></url>\n");
        }
        sb.Append("</urlset>\n");
        return sb.ToString();
    }

    /// <summary>
    /// Mutlak adreslerin kökeni: istek doğrulanmış bir özel alan adından geldiyse o (https), değilse
    /// kurulumun PUBLIC_ORIGIN'i. İstekteki Host başlığına doğrudan güvenilmez.
    /// </summary>
    private async Task<string> OriginAsync(CancellationToken ct)
    {
        var platform = (Environment.GetEnvironmentVariable("PUBLIC_ORIGIN") ?? Environment.GetEnvironmentVariable("PUBLIC_URL") ?? "http://localhost").TrimEnd('/');
        var host = DomainValidator.NormalizeHost(Request.Host.Host);
        if (host is null) return platform;
        var verified = await (from d in _db.CustomDomains
                              join t in _db.Tenants on d.TenantSlug equals t.Slug
                              where d.Domain == host && d.Status == CustomDomainStatus.Verified && t.Status == TenantStatus.Active
                              select d.Domain).AnyAsync(ct);
        return verified ? "https://" + host : platform;
    }
}

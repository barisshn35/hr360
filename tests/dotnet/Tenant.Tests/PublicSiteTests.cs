using TenantService.Controllers;
using TenantService.Models;
using TenantService.Services;
using Xunit;

namespace Tenant.Tests;

/// <summary>Herkese açık site: robots.txt, sitemap.xml, SSS kaydı ve site görünümü.</summary>
public class PublicSiteTests
{
    [Fact]
    public void Arama_motorlari_kapaliyken_robots_her_seyi_engeller_sitemap_bos()
    {
        var robots = PublicSiteController.BuildRobots(false, "https://hr.ornek.com");
        Assert.Contains("User-agent: *\nDisallow: /\n", robots);
        Assert.DoesNotContain("Sitemap:", robots);
        Assert.DoesNotContain("Allow:", robots);

        var sitemap = PublicSiteController.BuildSitemap(false, true, "https://hr.ornek.com");
        Assert.Contains("<urlset", sitemap);
        Assert.DoesNotContain("<url>", sitemap);
    }

    [Fact]
    public void Arama_motorlari_acikken_kisisel_yollar_engelli_sitemap_mutlak_adresli()
    {
        var robots = PublicSiteController.BuildRobots(true, "https://hr.ornek.com");
        foreach (var p in new[] { "/panel", "/api/", "/auth/", "/parola-belirle/", "/kariyer/*/basvuru/", "/kariyer/*/teklif/" })
            Assert.Contains($"Disallow: {p}\n", robots);
        Assert.Contains("Sitemap: https://hr.ornek.com/sitemap.xml", robots);
        Assert.DoesNotContain("Disallow: /\n", robots);

        var sitemap = PublicSiteController.BuildSitemap(true, true, "https://hr.ornek.com");
        foreach (var p in new[] { "/giris", "/sss", "/gizlilik", "/kosullar", "/iletisim", "/kayit" })
            Assert.Contains($"<loc>https://hr.ornek.com{p}</loc>", sitemap);
        Assert.DoesNotContain("/panel", sitemap);
    }

    [Fact]
    public void Kayit_kapaliyken_sitemapte_kayit_sayfasi_yok()
    {
        var sitemap = PublicSiteController.BuildSitemap(true, false, "https://hr.ornek.com");
        Assert.DoesNotContain("/kayit", sitemap);
        Assert.Contains("/giris", sitemap);
    }

    [Fact]
    public void Sitemap_adresi_xml_icin_kacislanir()
    {
        var sitemap = PublicSiteController.BuildSitemap(true, true, "https://a.com/?x=1&y=<2>");
        Assert.Contains("&amp;", sitemap);
        Assert.DoesNotContain("<2>", sitemap);
    }

    [Fact]
    public void Sss_kaydi_gidip_gelir_bos_ve_bozuk_kayit_bos_liste()
    {
        var items = new List<PlatformSettingsService.FaqItem> { new("Soru 1?", "Cevap **1**"), new("Soru 2?", "Cevap 2") };
        var json = PlatformSettingsService.SerializeFaq(items);
        Assert.NotNull(json);
        Assert.Equal(items, PlatformSettingsService.ParseFaq(json));
        Assert.Null(PlatformSettingsService.SerializeFaq([]));
        Assert.Empty(PlatformSettingsService.ParseFaq(null));
        Assert.Empty(PlatformSettingsService.ParseFaq("{bozuk"));
        Assert.Single(PlatformSettingsService.ParseFaq("[{\"q\":\"S\",\"a\":\"C\"},{\"q\":\"\",\"a\":\"yalniz cevap\"}]"));
    }

    [Fact]
    public void Site_gorunumu_varsayilan_olarak_arama_motorlarina_kapali()
    {
        var view = PlatformSettingsController.SiteView(new PlatformSetting());
        var prop = view.GetType().GetProperty("searchIndexing")!;
        Assert.False((bool)prop.GetValue(view)!);
        Assert.Null(view.GetType().GetProperty("privacyMd")!.GetValue(view));
    }
}

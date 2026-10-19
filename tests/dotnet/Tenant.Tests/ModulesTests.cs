using HR360.Platform;
using TenantService.Controllers;
using TenantService.Models;
using Xunit;

namespace Tenant.Tests;

/// <summary>İsteğe bağlı modüller (apps/shared-dotnet/Modules.cs) ve herkese açık sitedeki kurulum bilgisi.</summary>
public class ModulesTests
{
    private static readonly string[] AllIds = ["recruitment", "onboarding", "timeshift", "performance", "learning", "compensation", "ml", "expense"];

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("all")]
    [InlineData(" ALL ")]
    [InlineData("timeshift,all")]
    public void Bos_ya_da_all_hepsi_acik(string? raw) =>
        Assert.Equal(AllIds.OrderBy(x => x), Modules.ParseModules(raw).OrderBy(x => x));

    [Fact]
    public void Liste_bilinmeyeni_ve_boslugu_yok_sayar()
    {
        var set = Modules.ParseModules(" timeshift , Compensation,,bilinmeyen, expense ");
        Assert.Equal(["compensation", "expense", "timeshift"], set.OrderBy(x => x));
        Assert.Contains("TIMESHIFT", set);
    }

    [Fact]
    public void Yalniz_bilinmeyen_kimlik_hicbir_modul_acmaz() => Assert.Empty(Modules.ParseModules("foo,bar"));

    [Theory]
    [InlineData(null, "multi")]
    [InlineData("", "multi")]
    [InlineData("multi", "multi")]
    [InlineData(" Single ", "single")]
    [InlineData("tek", "multi")]
    public void Kiracilik_kipi(string? raw, string want) => Assert.Equal(want, Modules.ParseTenancy(raw));

    [Fact]
    public void Override_kapali_modulu_ve_cekirdegi_dogru_bildirir_sonra_geri_doner()
    {
        var before = Modules.EnabledList();
        using (Modules.Override("timeshift,compensation", "single"))
        {
            Assert.True(Modules.IsEnabled("timeshift"));
            Assert.False(Modules.IsEnabled(Modules.Onboarding));
            Assert.False(Modules.IsEnabled("ml"));
            Assert.True(Modules.IsEnabled("leave")); // çekirdek alan her zaman açık
            Assert.True(Modules.IsSingleTenant);
            Assert.Equal(["timeshift", "compensation"], Modules.EnabledList());
        }
        Assert.Equal(before, Modules.EnabledList());
    }

    [Fact]
    public void Ortam_yokken_hepsi_acik_ve_coklu_kiraci()
    {
        using (Modules.Override(null))
        {
            Assert.Equal(AllIds, Modules.EnabledList());
            Assert.Equal("multi", Modules.Tenancy);
            Assert.False(Modules.IsSingleTenant);
        }
    }

    private static object? Prop(object o, string name) => o.GetType().GetProperty(name)!.GetValue(o);

    [Fact]
    public void Site_gorunumu_acik_modulleri_ve_kiracilik_kipini_tasir()
    {
        using (Modules.Override("expense,timeshift", "single"))
        {
            var view = PlatformSettingsController.SiteView(new PlatformSetting());
            Assert.Equal(["timeshift", "expense"], (string[])Prop(view, "modules")!);
            Assert.Equal("single", Prop(view, "tenancy"));
            Assert.NotNull(view.GetType().GetProperty("searchIndexing")); // eski alanlar yerinde
        }
        using (Modules.Override("all"))
        {
            var view = PlatformSettingsController.SiteView(new PlatformSetting());
            Assert.Equal(AllIds, (string[])Prop(view, "modules")!);
            Assert.Equal("multi", Prop(view, "tenancy"));
        }
    }
}

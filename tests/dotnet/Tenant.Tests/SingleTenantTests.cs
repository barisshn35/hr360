using System.Security.Claims;
using HR360.Platform;
using Microsoft.AspNetCore.Mvc;
using TenantService.Controllers;
using TenantService.Models;
using TenantService.Services;
using Xunit;

namespace Tenant.Tests;

/// <summary>Tek şirket kipi (HR360_TENANCY=single): şirket tohumlama kararları, bakım yetkisi ve kipte kapalı uçlar.</summary>
public class SingleTenantTests
{
    private static ClaimsPrincipal UserWith(params string[] roles) =>
        new(new ClaimsIdentity(roles.Select(r => new Claim(ClaimTypes.Role, r)), "test"));

    [Fact]
    public void Sirket_yokken_tohumlanir() => Assert.Equal(SingleTenantRules.Decision.Seed, SingleTenantRules.Decide([]));

    [Fact]
    public void Yalniz_yarim_kalmis_kayit_varken_yeniden_tohumlanir() =>
        Assert.Equal(SingleTenantRules.Decision.Seed, SingleTenantRules.Decide([TenantStatus.Pending, TenantStatus.Pending]));

    [Theory]
    [InlineData(TenantStatus.Active)]
    [InlineData(TenantStatus.Suspended)]
    [InlineData(TenantStatus.Cancelled)]
    public void Herhangi_bir_sirket_varsa_hicbir_sey_yapilmaz(TenantStatus status) =>
        Assert.Equal(SingleTenantRules.Decision.AlreadyHasCompany, SingleTenantRules.Decide([TenantStatus.Pending, status]));

    [Theory]
    [InlineData(true, "Örnek A.Ş.", true)]
    [InlineData(true, "  ", false)]
    [InlineData(true, null, false)]
    [InlineData(false, "Örnek A.Ş.", false)]
    public void Tohumlayici_yalniz_tek_sirket_kipinde_ve_adla_kaydolur(bool single, string? name, bool want) =>
        Assert.Equal(want, SingleTenantRules.ShouldRegister(single, name));

    [Theory]
    [InlineData("'Örnek A.Ş.'", "Örnek A.Ş.")]
    [InlineData("\"Acme\"", "Acme")]
    [InlineData("  Acme  ", "Acme")]
    [InlineData("''", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Ortam_degeri_tirnak_ve_bosluktan_arindirilir(string? raw, string? want) =>
        Assert.Equal(want, SingleTenantRules.Clean(raw));

    [Theory]
    [InlineData("Örnek Gıda A.Ş.", "ornek-gida-a-s")]
    [InlineData("İSTANBUL Lojistik", "istanbul-lojistik")]
    [InlineData("AB", "ab-sirket")]
    [InlineData("!!!", "sirket")]
    public void Kisa_ad_sirket_adindan_uretilir(string name, string want) =>
        Assert.Equal(want, TenantProvisioningService.BaseSlug(name));

    [Fact]
    public void Ayrilmis_ad_algilanir()
    {
        Assert.Contains(TenantProvisioningService.BaseSlug("API"), RegistrationController.ReservedSlugs);
        Assert.DoesNotContain(TenantProvisioningService.BaseSlug("API sirket"), RegistrationController.ReservedSlugs);
    }

    [Fact]
    public void Bakim_rolu_yalniz_tek_sirket_kipinde_gecerli()
    {
        var yonetici = UserWith("tenant-admin", SingleTenantRules.PlatformManageRole);
        using (Modules.Override(null, "multi"))
        {
            Assert.False(SingleTenantRules.CanManagePlatform(yonetici));
            Assert.True(SingleTenantRules.CanManagePlatform(UserWith("platform-admin")));
            Assert.True(SingleTenantRules.CanManagePlatform(UserWith("ext-platform-manage")));
            Assert.False(SingleTenantRules.CanManagePlatform(UserWith("tenant-admin")));
        }
        using (Modules.Override(null, "single"))
        {
            Assert.True(SingleTenantRules.CanManagePlatform(yonetici));
            Assert.False(SingleTenantRules.CanManagePlatform(UserWith("tenant-admin")));
        }
    }

    [Fact]
    public async Task Tek_sirket_kipinde_yeni_sirket_409()
    {
        using var _ = Modules.Override(null, "single");
        var c = new TenantsController(null!, null!, null!);
        var res = await c.Create(new RegisterCompanyRequest("Yeni", "a@b.com", null, null, null, null), null!, null!, default);
        var conflict = Assert.IsType<ConflictObjectResult>(res);
        Assert.Contains("single_tenant_mode", System.Text.Json.JsonSerializer.Serialize(conflict.Value));
    }

    [Fact]
    public async Task Tek_sirket_kipinde_askiya_alma_409()
    {
        using var _ = Modules.Override(null, "single");
        var c = new TenantsController(null!, null!, null!);
        Assert.IsType<ConflictObjectResult>(await c.Suspend(Guid.NewGuid(), new SuspendRequest("x"), default));
    }

    [Fact]
    public async Task Tek_sirket_kipinde_kayit_acilamaz()
    {
        using var _ = Modules.Override(null, "single");
        var c = new PlatformSettingsController(null!, null!, null!, null!, null!, null!);
        var res = await c.PutGeneral(new PlatformSettingsController.GeneralRequest(true, null, null), default);
        Assert.IsType<ConflictObjectResult>(res);
    }
}

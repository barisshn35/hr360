using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using TenantService.Controllers;
using TenantService.Services;
using Xunit;

namespace Tenant.Tests;

/// <summary>Kurulum ayarlari: HR360_DEMO (demo verisi) ve PUBLIC_SIGNUP_ENABLED (herkese acik kayit).</summary>
public class ProductSettingsTests
{
    [Theory]
    [InlineData(null, true)]      // eski kurulum: degisken yok -> demo acik kalir
    [InlineData("", true)]
    [InlineData("  ", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("1", true)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData("0", false)]
    [InlineData("no", false)]
    [InlineData("hayir", false)]
    public void Demo_yalnizca_acikca_kapatilirsa_kapali(string? value, bool expected)
        => Assert.Equal(expected, ProductSettings.DemoEnabledFrom(value));

    [Theory]
    [InlineData(null, true, true)]
    [InlineData("tanimsiz", false, false)]
    [InlineData("evet", false, true)]
    [InlineData("off", true, false)]
    public void Bayrak_okuma(string? value, bool fallback, bool expected)
        => Assert.Equal(expected, ProductSettings.FlagFrom(value, fallback));

    [Fact]
    public void Kayit_uclari_isaretli_marka_ucu_degil()
    {
        var t = typeof(RegistrationController);
        Assert.NotNull(t.GetMethod(nameof(RegistrationController.Register))!.GetCustomAttribute<RequirePublicSignupAttribute>());
        Assert.NotNull(t.GetMethod(nameof(RegistrationController.SlugAvailable))!.GetCustomAttribute<RequirePublicSignupAttribute>());
        // notification-service e-posta basligi icin kullanir; kayit kapaliyken de calismali.
        Assert.Null(t.GetMethod(nameof(RegistrationController.Branding))!.GetCustomAttribute<RequirePublicSignupAttribute>());
    }

    [Fact]
    public async Task Kayit_kapaliyken_404_acikken_dokunmaz()
    {
        var old = Environment.GetEnvironmentVariable("PUBLIC_SIGNUP_ENABLED");
        try
        {
            var filter = new RequirePublicSignupAttribute();

            Environment.SetEnvironmentVariable("PUBLIC_SIGNUP_ENABLED", "false");
            var closed = NewContext();
            var ranClosed = false;
            await filter.OnResourceExecutionAsync(closed, () => { ranClosed = true; return Task.FromResult<ResourceExecutedContext>(null!); });
            Assert.IsType<NotFoundResult>(closed.Result);
            Assert.False(ranClosed);

            Environment.SetEnvironmentVariable("PUBLIC_SIGNUP_ENABLED", null);
            var open = NewContext();
            var ranOpen = false;
            await filter.OnResourceExecutionAsync(open, () => { ranOpen = true; return Task.FromResult<ResourceExecutedContext>(null!); });
            Assert.Null(open.Result);
            Assert.True(ranOpen);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PUBLIC_SIGNUP_ENABLED", old);
        }
    }

    private static ResourceExecutingContext NewContext() =>
        new(new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()),
            new List<IFilterMetadata>(), new List<IValueProviderFactory>());
}

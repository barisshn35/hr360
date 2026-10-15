using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace TenantService.Services;

/// <summary>
/// Kurulum duzeyindeki urun ayarlari (.env -> docker-compose -> ortam degiskeni).
/// <list type="bullet">
/// <item><c>HR360_DEMO</c>: demo sirketi ("demo") ve demo.admin. install.sh --demo ile true,
/// aksi halde false yazar. Degisken HIC YOKSA (bu ayardan onceki kurulumlar) demo acik kabul
/// edilir: mevcut kurulumlarin davranisi degismez.</item>
/// <item><c>PUBLIC_SIGNUP_ENABLED</c>: herkese acik sirket kaydi (/kayit). Varsayilan acik;
/// install.sh --no-signup ile false. Kapaliyken kayit uclari 404 doner, web kayit girisini gizler.</item>
/// </list>
/// </summary>
public static class ProductSettings
{
    public static bool DemoEnabled => DemoEnabledFrom(Environment.GetEnvironmentVariable("HR360_DEMO"));
    public static bool PublicSignupEnabled => FlagFrom(Environment.GetEnvironmentVariable("PUBLIC_SIGNUP_ENABLED"), true);

    /// <summary>Tanimsiz/bos = true (geriye uyumluluk); yalnizca acikca kapatilirsa false.</summary>
    public static bool DemoEnabledFrom(string? value) => FlagFrom(value, true);

    /// <summary>true/1/yes/evet/on -> true; false/0/no/hayir/off -> false; bos ya da taninmayan -> varsayilan.</summary>
    public static bool FlagFrom(string? value, bool fallback)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "true" or "1" or "yes" or "evet" or "e" or "on": return true;
            case "false" or "0" or "no" or "hayir" or "hayır" or "h" or "off": return false;
            default: return fallback;
        }
    }
}

/// <summary>
/// Herkese acik sirket kaydi kapaliyken (Sistem ayarlari; ilk deger PUBLIC_SIGNUP_ENABLED) isaretli uc yokmus gibi
/// 404 doner; ucun varligi bile disariya belli edilmez.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class RequirePublicSignupAttribute : Attribute, IAsyncResourceFilter
{
    // Ayar arayüzden (Platform yönetimi > Sistem ayarları) değişir: platform_settings, yoksa .env.
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        // DI yoksa (birim testi) .env değeri.
        var settings = context.HttpContext.RequestServices?.GetService<PlatformSettingsService>();
        var enabled = settings is null ? ProductSettings.PublicSignupEnabled : await settings.SignupEnabledAsync(context.HttpContext.RequestAborted);
        if (!enabled)
        {
            context.Result = new NotFoundResult();
            return;
        }
        await next();
    }
}

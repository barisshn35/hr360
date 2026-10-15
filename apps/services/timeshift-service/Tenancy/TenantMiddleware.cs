using System.Security.Claims;
using System.Text.Json;

namespace TimeShiftService.Tenancy;

/// <summary>
/// JWT'deki organization claim'inden tenant'i cozer ve istek kapsamindaki
/// TenantContext'e yazar. UseAuthentication'DAN SONRA cagrilmalidir.
///
/// Guvenlik notu: Tenant kimligi YALNIZCA token'dan gelir. Istemcinin
/// gonderdigi header/parametre asla dikkate alinmaz - aksi halde bir
/// kullanici baska tenant'in verisini isteyebilirdi.
/// </summary>
public class TenantMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TenantMiddleware> _logger;

    public TenantMiddleware(RequestDelegate next, ILogger<TenantMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, TenantContext tenantContext)
    {
        var user = context.User;

        if (user?.Identity?.IsAuthenticated == true)
        {
            tenantContext.IsPlatformAdmin = user.IsInRole("platform-admin");

            var raw = user.FindFirst("organization")?.Value;
            if (!string.IsNullOrWhiteSpace(raw))
            {
                tenantContext.TenantSlug = ParseOrganizationSlug(raw);
            }

            if (!tenantContext.IsResolved)
            {
                _logger.LogWarning(
                    "Kimlik dogrulandi ama tenant cozulemedi. Kullanici: {User}",
                    user.FindFirst(ClaimTypes.NameIdentifier)?.Value);
            }
        }

        await _next(context);
    }

    /// <summary>
    /// JWT'deki "organization" claim'ini tenant slug'ina cevirir.
    ///
    /// NOT: Bu claim onceden basit bir dizi ("[\"acme\"]") sanilip
    /// raw.Trim('[', ']', '"', ' ') ile parse ediliyordu - ama Keycloak
    /// (bu projede: 25.0.6) "organization" scope'unu JSON NESNESI olarak
    /// dolduruyor, orgutun ADIYLA anahtarlanmis: {"acme":{}}. Trim()
    /// yaklasimi bunun icin YANLIS sonuc uretiyordu (suslu parantezleri
    /// ve ic taraftaki ":{}" parcasini silmiyordu), bu yuzden HICBIR
    /// authenticated istek dogru tenant'a cozulemiyordu - canli JWT ile
    /// dogrulandi (hardcore test sirasinda bulundu). Simdi claim'i gercek
    /// JSON olarak parse ediyoruz; nesne ise ilk anahtari, dizi ise ilk
    /// elemani, duz string ise oldugu gibi kullaniyoruz. JSON parse
    /// basarisiz olursa (baska bir protokol haritalayicisi/surum farki
    /// ihtimaline karsi) eski davranisa (Trim) geri duseriz.
    /// </summary>
    private static string? ParseOrganizationSlug(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            switch (doc.RootElement.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var prop in doc.RootElement.EnumerateObject())
                        return prop.Name;
                    return null;
                case JsonValueKind.Array:
                    foreach (var el in doc.RootElement.EnumerateArray())
                        return el.GetString();
                    return null;
                case JsonValueKind.String:
                    return doc.RootElement.GetString();
                default:
                    return null;
            }
        }
        catch (JsonException)
        {
            return raw.Trim('[', ']', '"', ' ');
        }
    }
}

public static class TenantMiddlewareExtensions
{
    public static IApplicationBuilder UseTenantContext(this IApplicationBuilder app)
        => app.UseMiddleware<TenantMiddleware>().UseMiddleware<TenantStatusGate>();
}

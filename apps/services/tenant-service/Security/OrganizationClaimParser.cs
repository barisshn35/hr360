using System.Text.Json;

namespace TenantService.Security;

/// <summary>
/// JWT'deki "organization" claim'ini tenant slug'ina cevirir.
///
/// NOT: Bu claim onceden basit bir dizi ("[\"acme\"]") sanilip
/// raw.Trim('[', ']', '"', ' ') ile parse ediliyordu - ama Keycloak (bu
/// projede: 25.0.6) "organization" scope'unu JSON NESNESI olarak
/// dolduruyor, organizasyonun ADIYLA anahtarlanmis: {"acme":{}}. Trim()
/// yaklasimi bunun icin YANLIS sonuc uretiyordu (suslu parantezleri ve ic
/// taraftaki ":{}" parcasini silmiyordu), bu yuzden MyTenantController ve
/// TeamMembersController HICBIR ZAMAN dogru tenant'a cozulemiyordu - canli
/// JWT ile dogrulandi (hardcore test sirasinda bulundu: demo.admin girisi
/// "Kullanıcı bir şirkete bağlı değil" / "'{\"demo\":{}}' icin tenant kaydi
/// bulunamadi" hatasiyla panele hic ulasamiyordu). Ayni fix, tum diger
/// mikroservislerin TenantMiddleware.cs dosyalarina da uygulandi.
/// </summary>
public static class OrganizationClaimParser
{
    public static string? ParseSlug(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

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

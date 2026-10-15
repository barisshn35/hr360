using System.Security.Cryptography;
using System.Text;

namespace EngagementService.Security;

/// <summary>
/// Servisler arası iç uç anahtarı (X-Internal-Token) denetimi. Geçerli anahtar INTERNAL_SERVICE_TOKEN;
/// kesintisiz değiştirme penceresinde INTERNAL_SERVICE_TOKEN_PREVIOUS (eski anahtar, boş değilse) da
/// kabul edilir: tüm servisler yeni anahtara geçene kadar eski anahtarla gelen çağrılar reddedilmez
/// (scripts/rotate-internal-token.sh). Geçerli anahtar tanımlı değilse uç kapalıdır (her zaman false).
/// Karşılaştırma sabit zamanlıdır (SHA-256 özetleri; uzunluk farkı sızmaz) ve iki aday da her zaman denenir.
/// Her servisin kendi kopyası vardır (servisler ayrı derleme bağlamı).
/// </summary>
public static class InternalServiceToken
{
    public const string Header = "X-Internal-Token";

    /// <summary>Gelen anahtar geçerli ya da (pencere açıksa) önceki anahtarla eşleşiyor mu?</summary>
    public static bool Matches(string? given) => Matches(given,
        Environment.GetEnvironmentVariable("INTERNAL_SERVICE_TOKEN"),
        Environment.GetEnvironmentVariable("INTERNAL_SERVICE_TOKEN_PREVIOUS"));

    /// <summary>Saf karşılaştırma (testler için): geçerli boşsa false; önceki yalnızca doluysa sayılır.</summary>
    public static bool Matches(string? given, string? current, string? previous)
    {
        if (string.IsNullOrEmpty(current)) return false;
        var g = SHA256.HashData(Encoding.UTF8.GetBytes(given ?? ""));
        var okCurrent = CryptographicOperations.FixedTimeEquals(g, SHA256.HashData(Encoding.UTF8.GetBytes(current)));
        var okPrevious = !string.IsNullOrEmpty(previous)
            & CryptographicOperations.FixedTimeEquals(g, SHA256.HashData(Encoding.UTF8.GetBytes(previous ?? "")));
        return okCurrent | okPrevious;
    }
}

using System.Security.Cryptography;

namespace CompensationService.Auditing;

/// <summary>
/// Güvenlik dalgası 2B — indirme iz kodu. Kişisel veri içeren her indirmeye kısa, rastgele bir kod
/// verilir; kod denetim kaydına (audit_log."Changes"->>'traceCode') yazılır ve yanıt başlığında
/// (X-HR360-Trace) döner. Sızan bir dosya/çıktı üzerindeki kodla Veri koruma › İz kodu ekranından
/// kimin, ne zaman indirdiği bulunur. Aynı biçim governance-service'te de var (Infrastructure/Watermark.cs).
/// </summary>
public static class TraceCode
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // karışmayan 32 karakter

    /// <summary>"K7P2-MX9Q" biçiminde 40 bitlik kod.</summary>
    public static string New()
    {
        var b = RandomNumberGenerator.GetBytes(8);
        var c = b.Select(x => Alphabet[x & 31]).ToArray();
        return $"{new string(c, 0, 4)}-{new string(c, 4, 4)}";
    }
}

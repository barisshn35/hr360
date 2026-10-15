using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace RecruitmentService.Services;

/* ---------------------------------------------------------------- herkese açık form koruması */

public enum FormTokenStatus { Ok, Missing, Invalid, TooFast, Expired, Replayed }

/// <summary>
/// Herkese açık POST formları (etik bildirimi, iş başvurusu) için hafif bot koruması: form açılırken
/// GET ile HMAC imzalı zaman jetonu alınır; gönderimde imza, kapsam (kiracı/form), yaş (en az 3 sn,
/// en çok 2 saat) ve tek kullanım denetlenir. Üçüncü taraf CAPTCHA ve IP saklama yok. Anahtar
/// FORM_TOKEN_KEY (yoksa TENANT_SECRET_KEY) ortam değişkeninden türetilir; ikisi de yoksa süreç başına
/// rastgele (yeniden başlatmada açık formların jetonu geçersizleşir, kullanıcı yeniden dener).
/// Aynı sınıf governance-service'te de var (Infrastructure/DataProtection.cs, etik hattı).
/// </summary>
public sealed class FormGuard
{
    public static readonly TimeSpan MinAge = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(2);
    public static readonly FormGuard Shared = new(DeriveKey());

    private readonly byte[] _key;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _used = new();

    public FormGuard(byte[] key) => _key = key;

    private static byte[] DeriveKey()
    {
        var src = Environment.GetEnvironmentVariable("FORM_TOKEN_KEY") is { Length: > 0 } k ? k
            : Environment.GetEnvironmentVariable("TENANT_SECRET_KEY") is { Length: > 0 } t ? t : null;
        return src is null ? RandomNumberGenerator.GetBytes(32)
            : HMACSHA256.HashData(Encoding.UTF8.GetBytes(src), Encoding.UTF8.GetBytes("hr360-form-guard-v1"));
    }

    private string Sign(string scope, long ts, string nonce) =>
        Convert.ToBase64String(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{scope}|{ts}|{nonce}")))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>"zaman.nonce.imza" biçiminde jeton.</summary>
    public string Issue(string scope, DateTimeOffset? now = null)
    {
        var ts = (now ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        return $"{ts}.{nonce}.{Sign(scope, ts, nonce)}";
    }

    public FormTokenStatus Verify(string? token, string scope, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(token)) return FormTokenStatus.Missing;
        var parts = token.Split('.');
        if (parts.Length != 3 || parts[0].Length > 15 || parts[1].Length != 24 || !long.TryParse(parts[0], out var ts))
            return FormTokenStatus.Invalid;
        var expected = Encoding.ASCII.GetBytes(Sign(scope, ts, parts[1]));
        if (!CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(parts[2]))) return FormTokenStatus.Invalid;
        var t = now ?? DateTimeOffset.UtcNow;
        var age = t - DateTimeOffset.FromUnixTimeMilliseconds(ts);
        if (age < MinAge) return FormTokenStatus.TooFast;
        if (age > MaxAge) return FormTokenStatus.Expired;
        if (_used.Count > 20_000)
            foreach (var kv in _used.Where(kv => t - kv.Value > MaxAge).ToList()) _used.TryRemove(kv.Key, out _);
        return _used.TryAdd(parts[1], t) ? FormTokenStatus.Ok : FormTokenStatus.Replayed;
    }

    /// <summary>Kullanıcıya gösterilecek ileti (Türkçe; istemci "@server:" anahtarıyla çevirir).</summary>
    public static string Message(FormTokenStatus s) => s switch
    {
        FormTokenStatus.TooFast => "Form çok hızlı gönderildi; birkaç saniye bekleyip yeniden deneyin.",
        FormTokenStatus.Expired => "Formun süresi doldu; sayfayı yenileyip yeniden gönderin.",
        _ => "Form doğrulanamadı; sayfayı yenileyip yeniden gönderin.",
    };
}

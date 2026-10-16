using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace TenantService.Security;

/// <summary>
/// Guvenlik dalgasi 2A: iki adimli dogrulama politikasi kurallari (saf, birim testli).
///
/// Politika sirket basinadir (platform_tenants.MfaPolicy):
///   off        - zorunluluk yok (null da "off" sayilir; demo sirketi ve eski kayitlar),
///   privileged - IK (hr-admin), sirket yoneticisi (tenant-admin) ve yonetici (manager) rolleri,
///   all        - tum sirket kullanicilari.
/// Platform yoneticisi (platform-admin) kiraciya bagli olmadigi icin ayri ayardir:
/// MFA_REQUIRE_PLATFORM_ADMIN=true.
///
/// Uygulama: zorunlu olup ne TOTP ne passkey (WebAuthn) kimlik bilgisi olan kullaniciya
/// Keycloak'ta CONFIGURE_TOTP gerekli eylemi eklenir; bir sonraki giriste dogrulayici
/// kurmadan devam edemez. Passkey kurmus kullanici uyumlu sayilir (giris akisinda passkey,
/// OTP'ye alternatif ikinci adimdir). Kullanici dogrulayicisini silerse duzenli tur
/// (MfaPolicyHostedService) zorunlulugu yeniden ekler.
/// </summary>
public static class MfaPolicyRules
{
    public const string Off = "off";
    public const string Privileged = "privileged";
    public const string All = "all";

    /// <summary>Kayitla acilan yeni sirketlerin politikasi: herkes icin zorunlu; yonetici Guvenlik ekranindan gevsetebilir.</summary>
    public const string NewTenantDefault = All;

    /// <summary>Iki adimli dogrulamanin zorunlu oldugu roller ("privileged" politikasi).</summary>
    public static readonly IReadOnlyList<string> PrivilegedRoles = new[] { "tenant-admin", "hr-admin", "manager" };

    public const string ConfigureTotp = "CONFIGURE_TOTP";

    /// <summary>Gecersiz/bos deger "off" sayilir.</summary>
    public static string Normalize(string? policy) => (policy ?? "").Trim().ToLowerInvariant() switch
    {
        Privileged => Privileged,
        All => All,
        _ => Off,
    };

    public static bool IsValid(string? policy) => policy is Off or Privileged or All;

    /// <summary>Politika ve kullanicinin realm rollerine gore iki adimli dogrulama zorunlu mu?</summary>
    public static bool Requires(string? policy, IEnumerable<string> roles) => Normalize(policy) switch
    {
        All => true,
        Privileged => roles.Any(r => PrivilegedRoles.Contains(r, StringComparer.Ordinal)),
        _ => false,
    };

    /// <summary>Kullanicinin ikinci adimi (TOTP ya da passkey) var mi?</summary>
    public static bool HasSecondFactor(IEnumerable<string> credentialTypes) =>
        credentialTypes.Any(t => t == "otp" || t.StartsWith("webauthn", StringComparison.Ordinal));

    /// <summary>
    /// CONFIGURE_TOTP eklenmeli mi? Zorunlu + ikinci adim yok + zaten bekleyen kurulum yok.
    /// </summary>
    public static bool NeedsSetup(bool required, IEnumerable<string> credentialTypes, IEnumerable<string> requiredActions) =>
        required && !HasSecondFactor(credentialTypes) && !requiredActions.Contains(ConfigureTotp, StringComparer.Ordinal);
}

/// <summary>
/// Guvenlik dalgasi 2A: supheli giris tespiti kurallari (saf, birim testli). Keycloak'in
/// LOGIN / LOGIN_ERROR olaylarindan:
///   1) Kullanicinin daha once gorulmemis bir agdan (IPv4 /24, IPv6 /48) basarili girisi ->
///      kullaniciya "yeni bir agdan giris" bildirimi. Kullanicinin ILK gozlemi yalnizca
///      kaydedilir (baslangic; tum kullanicilara bir kez uyari gitmesin).
///   2) 10 dakikada 5 ve uzeri hatali giris (ayni kullanici ya da ayni ag) -> sirketin IK ve
///      sirket yoneticilerine bildirim + audit_log. Ayni konu icin 60 dakikada bir uyari.
/// KVKK: ham IP saklanmaz; ag oneki anahtarli ozetle (HMAC-SHA256) tutulur. Konum (GeoIP) ve
/// dis servis kullanilmaz. Keycloak olaylari tarayici/cihaz bilgisi tasimadigi icin "cihaz"
/// ayrimi yapilmaz; ag degisimi esas alinir.
/// </summary>
public static class LoginWatchRules
{
    public const int FailureThreshold = 5;
    public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan AlertCooldown = TimeSpan.FromMinutes(60);
    public static readonly TimeSpan NetworkRetention = TimeSpan.FromDays(180);

    /// <summary>Sayilan hata turleri: yanlis parola/OTP ve olmayan kullanici adi.</summary>
    public static readonly IReadOnlySet<string> CountedErrors =
        new HashSet<string>(StringComparer.Ordinal) { "invalid_user_credentials", "user_not_found" };

    /// <summary>IP'nin ag oneki (IPv4 /24, IPv6 /48); gecersizse null.</summary>
    public static string? NetworkPrefix(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip) || !IPAddress.TryParse(ip.Trim(), out var addr)) return null;
        if (addr.IsIPv4MappedToIPv6) addr = addr.MapToIPv4();
        var bytes = addr.GetAddressBytes();
        if (addr.AddressFamily == AddressFamily.InterNetwork)
        {
            bytes[3] = 0;
            return new IPAddress(bytes) + "/24";
        }
        for (var i = 6; i < bytes.Length; i++) bytes[i] = 0;
        return new IPAddress(bytes) + "/48";
    }

    /// <summary>Ag onekinin anahtarli ozeti (hex, 32 karakter); IP gecersizse null.</summary>
    public static string? NetworkHash(string? ip, byte[] key)
    {
        var prefix = NetworkPrefix(ip);
        if (prefix is null) return null;
        using var h = new HMACSHA256(key);
        return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes("login-net|" + prefix)))[..32].ToLowerInvariant();
    }

    /// <summary>
    /// Basarili giriste "yeni ag" uyarisi gerekir mi? Kullanicinin hic kayitli agi yoksa bu
    /// ilk gozlemdir (uyari yok); kayitli aglar arasinda yoksa uyari.
    /// </summary>
    public static bool IsNewNetworkAlert(int knownNetworks, bool networkKnown) => knownNetworks > 0 && !networkKnown;

    /// <summary>Olay sayilan bir hata mi?</summary>
    public static bool IsCountedFailure(string? type, string? error) =>
        type == "LOGIN_ERROR" && error is not null && CountedErrors.Contains(error);
}

/// <summary>
/// Kayan pencere sayaci (anahtar basina zaman damgalari) + uyari bekleme suresi. Bellekte
/// tutulur; servis yeniden baslarsa pencere sifirlanir (uyari tekrarini tenant_security_alerts
/// kontrolu onler). Tek is parcacigindan (LoginWatchHostedService) kullanilir.
/// </summary>
public sealed class FailureCounter
{
    private readonly Dictionary<string, List<DateTimeOffset>> _hits = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _alerted = new(StringComparer.Ordinal);
    private readonly TimeSpan _window;
    private readonly TimeSpan _cooldown;
    private readonly int _threshold;

    public FailureCounter(int threshold, TimeSpan window, TimeSpan cooldown)
    {
        _threshold = threshold; _window = window; _cooldown = cooldown;
    }

    /// <summary>Hatayi ekler; pencere icindeki sayiyi doner.</summary>
    public int Add(string key, DateTimeOffset at)
    {
        if (!_hits.TryGetValue(key, out var list)) _hits[key] = list = new List<DateTimeOffset>();
        list.Add(at);
        list.RemoveAll(t => at - t > _window);
        return list.Count;
    }

    /// <summary>
    /// Esik asildi ve bekleme suresi doldu mu? true donerse uyari zamani kaydedilir (ayni
    /// konu icin bekleme suresince tekrar true donmez).
    /// </summary>
    public bool ShouldAlert(string key, int count, DateTimeOffset at)
    {
        if (count < _threshold) return false;
        if (_alerted.TryGetValue(key, out var last) && at - last < _cooldown) return false;
        _alerted[key] = at;
        return true;
    }

    /// <summary>Penceresi dolmus anahtarlari bellekten atar.</summary>
    public void Prune(DateTimeOffset now)
    {
        foreach (var k in _hits.Where(kv => kv.Value.Count == 0 || now - kv.Value[^1] > _window).Select(kv => kv.Key).ToList())
            _hits.Remove(k);
        foreach (var k in _alerted.Where(kv => now - kv.Value > _cooldown).Select(kv => kv.Key).ToList())
            _alerted.Remove(k);
    }

    public int TrackedKeys => _hits.Count;
}

/// <summary>Guvenlik dalgasi 2A: platform yoneticisi sureli erisim izni kurallari.</summary>
public static class PlatformAccessRules
{
    public const int MaxHours = 4;
    public const int MinReasonLength = 10;
    public const int MaxReasonLength = 500;

    /// <summary>Gecerli izin talebi mi? Hata yoksa null, varsa Turkce ileti.</summary>
    public static string? Validate(string? tenantSlug, string? reason, int hours)
    {
        if (string.IsNullOrWhiteSpace(tenantSlug)) return "Şirket belirtilmedi.";
        var r = (reason ?? "").Trim();
        if (r.Length < MinReasonLength) return $"Gerekçe en az {MinReasonLength} karakter olmalı.";
        if (r.Length > MaxReasonLength) return $"Gerekçe en fazla {MaxReasonLength} karakter olabilir.";
        if (hours < 1 || hours > MaxHours) return $"Süre 1 ile {MaxHours} saat arasında olmalı.";
        return null;
    }

    private static readonly System.Text.RegularExpressions.Regex SlugRe = new("^[a-z0-9][a-z0-9-]{0,63}$");

    /// <summary>
    /// Platform yoneticisinin sectigi kiraci (X-HR360-Tenant basligi; servislerdeki PlatformAccessGate ile
    /// ayni bicim kurali). Gecersiz ya da bos ise null.
    /// </summary>
    public static string? TargetTenant(string? header)
    {
        var s = header?.Trim().ToLowerInvariant();
        return s is not null && SlugRe.IsMatch(s) ? s : null;
    }
}

using Microsoft.EntityFrameworkCore;
using TenantService.Data;
using TenantService.Models;

namespace TenantService.Services;

/// <summary>
/// Arayüzden değiştirilen platform ayarlarının (platform_settings) geçerli değerleri. Satırdaki boş
/// alan .env'deki ilk değere düşer. Değerler 15 sn önbelleklenir; kaydedince önbellek boşalır.
/// </summary>
public sealed class PlatformSettingsService
{
    public const int DefaultInviteTtlHours = 72;
    public const int MinInviteTtlHours = 1;
    public const int MaxInviteTtlHours = 720;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(15);
    private static PlatformSetting? _cached;
    private static DateTimeOffset _cachedAt = DateTimeOffset.MinValue;

    private readonly TenantDbContext _db;
    public PlatformSettingsService(TenantDbContext db) => _db = db;

    /// <summary>Geçerli SMTP: Mode = custom (arayüzden girildi) | env (kurulumda .env'e girildi) | self (sunucunun kendi
    /// posta servisi, install.sh --mail-mode=self) | mailpit (gerçek gönderim yok).</summary>
    public sealed record EffectiveSmtp(string Mode, string Host, int Port, string? User, string? PasswordEncrypted,
        string? PasswordPlain, string FromAddress, string FromName)
    {
        /// <summary>Gerçek gönderim yapılabiliyor mu (paketteki Mailpit değil).</summary>
        public bool Configured => Mode != "mailpit";
    }

    public static void Invalidate() => _cachedAt = DateTimeOffset.MinValue;

    public async Task<PlatformSetting> GetRowAsync(CancellationToken ct)
    {
        if (_cached is not null && DateTimeOffset.UtcNow - _cachedAt < CacheTtl) return _cached;
        var row = await _db.PlatformSettings.AsNoTracking().FirstOrDefaultAsync(p => p.Id == 1, ct) ?? new PlatformSetting();
        _cached = row;
        _cachedAt = DateTimeOffset.UtcNow;
        return row;
    }

    /// <summary>Kaydetmek için izlenen satır (yoksa eklenir).</summary>
    public async Task<PlatformSetting> GetTrackedRowAsync(CancellationToken ct)
    {
        var row = await _db.PlatformSettings.FirstOrDefaultAsync(p => p.Id == 1, ct);
        if (row is null)
        {
            row = new PlatformSetting { Id = 1 };
            _db.PlatformSettings.Add(row);
        }
        return row;
    }

    public async Task<bool> SignupEnabledAsync(CancellationToken ct) =>
        (await GetRowAsync(ct)).SignupEnabled ?? ProductSettings.PublicSignupEnabled;

    public async Task<string> DefaultPasswordMethodAsync(CancellationToken ct) =>
        (await GetRowAsync(ct)).PasswordMethod is { } m && PasswordSetupMethods.Valid(m) ? m : PasswordSetupMethods.Invite;

    public async Task<int> InviteTtlHoursAsync(CancellationToken ct) =>
        Math.Clamp((await GetRowAsync(ct)).InviteTtlHours ?? DefaultInviteTtlHours, MinInviteTtlHours, MaxInviteTtlHours);

    public async Task<EffectiveSmtp> SmtpAsync(CancellationToken ct) => FromRow(await GetRowAsync(ct));

    /// <summary>Arama motorlarında görünür mü (varsayılan kapalı: İK uygulaması dizinlenmesin).</summary>
    public async Task<bool> SearchIndexingAsync(CancellationToken ct) => (await GetRowAsync(ct)).SearchIndexing ?? false;

    public sealed record FaqItem(string Q, string A);

    public const int MaxLegalTextLength = 100_000;
    public const int MaxFaqItems = 50;
    public const int MaxFaqQuestionLength = 300;
    public const int MaxFaqAnswerLength = 5_000;

    private static readonly System.Text.Json.JsonSerializerOptions FaqJsonOptions = new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>SSS listesi; bozuk/boş kayıt = boş liste (arayüz varsayılan soruları gösterir).</summary>
    public static IReadOnlyList<FaqItem> ParseFaq(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return (System.Text.Json.JsonSerializer.Deserialize<List<FaqItem>>(json, FaqJsonOptions) ?? [])
                .Where(f => !string.IsNullOrWhiteSpace(f.Q) && !string.IsNullOrWhiteSpace(f.A)).ToList();
        }
        catch (System.Text.Json.JsonException) { return []; }
    }

    public static string? SerializeFaq(IReadOnlyList<FaqItem>? items) =>
        items is null || items.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(items, FaqJsonOptions);

    public static EffectiveSmtp FromRow(PlatformSetting row)
    {
        if (row.SmtpMode == "custom" && !string.IsNullOrWhiteSpace(row.SmtpHost))
            return new EffectiveSmtp("custom", row.SmtpHost!, row.SmtpPort ?? 587, row.SmtpUser, row.SmtpPasswordEncrypted, null,
                string.IsNullOrWhiteSpace(row.SmtpFromAddress) ? EnvFrom() : row.SmtpFromAddress!,
                string.IsNullOrWhiteSpace(row.SmtpFromName) ? EnvFromName() : row.SmtpFromName!);
        if (row.SmtpMode == "mailpit") return Mailpit();
        var host = Environment.GetEnvironmentVariable("SMTP_HOST")?.Trim();
        if (string.IsNullOrEmpty(host) || IsMailpitHost(host)) return Mailpit();
        return new EffectiveSmtp(IsSelfMailerHost(host) ? "self" : "env", host,
            int.TryParse(Environment.GetEnvironmentVariable("SMTP_PORT"), out var p) ? p : 587,
            Environment.GetEnvironmentVariable("SMTP_USER"), null, Environment.GetEnvironmentVariable("SMTP_PASSWORD"),
            EnvFrom(), EnvFromName());
    }

    public static bool IsMailpitHost(string host) =>
        host.Equals("mailpit", StringComparison.OrdinalIgnoreCase) || host.StartsWith("mailpit.", StringComparison.OrdinalIgnoreCase);

    /// <summary>Paketteki giden posta servisi (Postfix + DKIM; compose "mailer" profili).</summary>
    public static bool IsSelfMailerHost(string host) => host.Equals("mailer", StringComparison.OrdinalIgnoreCase);

    private static EffectiveSmtp Mailpit() => new("mailpit", "mailpit", 1025, null, null, null, EnvFrom(), EnvFromName());
    private static string EnvFrom() => Environment.GetEnvironmentVariable("SMTP_FROM_ADDRESS") is { Length: > 0 } f ? f : "noreply@hr360.local";
    private static string EnvFromName() => Environment.GetEnvironmentVariable("SMTP_FROM_NAME") is { Length: > 0 } n ? n : "HR360";

    /// <summary>Şirket için e-posta gönderilebiliyor mu (platform SMTP'si ya da şirketin kendi SMTP'si).</summary>
    public async Task<bool> EmailAvailableAsync(Tenant? tenant, CancellationToken ct) =>
        !string.IsNullOrEmpty(tenant?.SmtpHost) || (await SmtpAsync(ct)).Configured;

    /// <summary>
    /// Şirketin geçerli parola belirleme yöntemi. Eski yöntem (Keycloak e-postası) yalnızca platform SMTP'si
    /// gerçek gönderim yapıyorsa geçerlidir (Keycloak tek realm, platform SMTP'sini kullanır); değilse davet linki.
    /// </summary>
    public async Task<(string Selected, string Effective)> PasswordMethodAsync(Tenant? tenant, CancellationToken ct)
        => ResolveMethod(tenant?.PasswordSetupMethod, await DefaultPasswordMethodAsync(ct), (await SmtpAsync(ct)).Configured);

    /// <summary>Seçilen yöntem (şirket seçimi yoksa platform varsayılanı) ve geçerli olan (SMTP yoksa davet linki).</summary>
    public static (string Selected, string Effective) ResolveMethod(string? tenantChoice, string platformDefault, bool platformSmtpConfigured)
    {
        var selected = PasswordSetupMethods.Valid(tenantChoice) ? tenantChoice!
            : PasswordSetupMethods.Valid(platformDefault) ? platformDefault : PasswordSetupMethods.Invite;
        var effective = selected == PasswordSetupMethods.Keycloak && !platformSmtpConfigured ? PasswordSetupMethods.Invite : selected;
        return (selected, effective);
    }

    /// <summary>Keycloak realm'ine yazılacak SMTP ayarı (eski yöntem e-postaları bunu kullanır).</summary>
    public static Dictionary<string, string> RealmSmtp(EffectiveSmtp s, Func<string, string> decrypt)
    {
        var password = s.PasswordPlain ?? (s.PasswordEncrypted is { Length: > 0 } enc ? decrypt(enc) : null);
        var auth = !string.IsNullOrEmpty(s.User) && !string.IsNullOrEmpty(password);
        var d = new Dictionary<string, string>
        {
            ["host"] = s.Host,
            ["port"] = s.Port.ToString(),
            ["from"] = s.FromAddress,
            ["fromDisplayName"] = s.FromName,
            ["auth"] = auth ? "true" : "false",
            ["ssl"] = s.Port == 465 ? "true" : "false",
            ["starttls"] = s.Port is 587 or 2525 ? "true" : "false",
        };
        if (auth)
        {
            d["user"] = s.User!;
            d["password"] = password!;
        }
        return d;
    }
}

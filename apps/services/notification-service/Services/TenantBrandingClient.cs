using System.Collections.Concurrent;
using System.Text.Json;
using NotificationService.Security;

namespace NotificationService.Services;

/// <summary>
/// tenant-service'in ANONIM "api/registration/branding/{slug}" ucundan
/// kiracinin logo URL'ini ve ana rengini ceker - e-posta bildirim
/// basliginda kiracinin KENDI logosunu gostermek icin kullanilir
/// (employee-service/OrganizationDirectoryClient ile ayni cross-service
/// HTTP client deseni, ama bu uc anonim oldugu icin JWT pass-through yok).
///
/// EmailSenderWorker her turda (30sn) ayni tenant icin defalarca cagirabilir
/// (bircok bekleyen bildirim ayni sirkete ait olabilir) - bu yuzden sonuc
/// kisa sureli (5 dk) process-ici bellekte tutulur. Bu, ayri instance'lar
/// arasinda paylasilmaz ama amac zaten agir bir cache degil, ayni turdaki
/// tekrarli cagrilari azaltmak.
/// </summary>
public class TenantBrandingClient
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentDictionary<string, (DateTimeOffset ExpiresAt, TenantBranding? Value)> Cache = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly SmtpCredentialProtector _protector;
    private readonly ILogger<TenantBrandingClient> _logger;

    public TenantBrandingClient(HttpClient http, SmtpCredentialProtector protector, ILogger<TenantBrandingClient> logger)
    {
        _http = http;
        _protector = protector;
        _logger = logger;
        _baseUrl = (Environment.GetEnvironmentVariable("TENANT_SERVICE_URL")
            ?? "http://tenant-service:8080").TrimEnd('/');
    }

    public record TenantBranding(string Name, string? LogoUrl, string? PrimaryColorHex);

    /// <summary>Cozulmus (plaintext) parolayla kiracinin ozel SMTP ayarlari.</summary>
    public record TenantSmtp(string Host, int Port, string? User, string Password, string? FromAddress, string? FromName);

    private record SmtpConfigDto(
        string SmtpHost, int? SmtpPort, string? SmtpUser, string? SmtpPasswordEncrypted,
        string? SmtpFromAddress, string? SmtpFromName);

    private static readonly ConcurrentDictionary<string, (DateTimeOffset ExpiresAt, TenantSmtp? Value)> SmtpCache = new();

    /// <summary>
    /// Marka bilgisini dondurur; bulunamazsa veya cross-service cagri
    /// basarisiz olursa null doner - bu SESSIZ bir best-effort islem,
    /// e-posta gonderimini asla engellememeli (varsayilan HR360 markasiyla
    /// gonderilir).
    /// </summary>
    public async Task<TenantBranding?> GetBrandingAsync(string tenantSlug, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantSlug)) return null;

        if (Cache.TryGetValue(tenantSlug, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
            return cached.Value;

        try
        {
            var resp = await _http.GetAsync($"{_baseUrl}/api/registration/branding/{tenantSlug}", ct);
            if (!resp.IsSuccessStatusCode)
            {
                Cache[tenantSlug] = (DateTimeOffset.UtcNow.Add(CacheTtl), null);
                return null;
            }

            var branding = JsonSerializer.Deserialize<TenantBranding>(
                await resp.Content.ReadAsStringAsync(ct), JsonOpts);
            Cache[tenantSlug] = (DateTimeOffset.UtcNow.Add(CacheTtl), branding);
            return branding;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Tenant marka bilgisi alinamadi ({Slug}), e-posta varsayilan HR360 markasiyla gonderilecek",
                tenantSlug);
            return null;
        }
    }

    /// <summary>
    /// Kiracinin OZEL SMTP sunucusu ayarlanmissa (Ayarlar > Marka'dan)
    /// cozulmus parolayla dondurur; ayarlanmamissa/bulunamazsa/cagri
    /// basarisiz olursa null doner - bu durumda EmailSenderWorker platform
    /// varsayilan SMTP'sine duser (best-effort, e-posta gonderimini
    /// engellememeli). tenant-service'ten SIFRELI gelen parola burada
    /// SmtpCredentialProtector ile cozulur (iki serviste de ayni
    /// TENANT_SECRET_KEY paylasilir).
    /// </summary>
    private static (DateTimeOffset ExpiresAt, TenantSmtp? Value) _platformSmtp;
    private static readonly TimeSpan PlatformSmtpTtl = TimeSpan.FromSeconds(20);

    private record PlatformSmtpDto(string? Mode, string? SmtpHost, int? SmtpPort, string? SmtpUser, string? SmtpPasswordEncrypted,
        string? SmtpFromAddress, string? SmtpFromName);

    /// <summary>
    /// Arayüzden (Platform yönetimi > Sistem ayarları) girilen platform SMTP'si; yoksa null ve .env'deki
    /// kurulum ayarı (EmailOptions) kullanılır. 20 sn önbellek. "mailpit" seçildiyse paketteki test kutusu.
    /// </summary>
    public async Task<TenantSmtp?> GetPlatformSmtpAsync(CancellationToken ct)
    {
        if (_platformSmtp.ExpiresAt > DateTimeOffset.UtcNow) return _platformSmtp.Value;
        TenantSmtp? value = null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/internal/platform-smtp");
            var token = Environment.GetEnvironmentVariable("INTERNAL_SERVICE_TOKEN");
            if (!string.IsNullOrEmpty(token)) req.Headers.Add(InternalServiceToken.Header, token);
            using var resp = await _http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                var dto = JsonSerializer.Deserialize<PlatformSmtpDto>(await resp.Content.ReadAsStringAsync(ct), JsonOpts);
                if (dto?.Mode == "custom" && !string.IsNullOrWhiteSpace(dto.SmtpHost))
                    value = new TenantSmtp(dto.SmtpHost, dto.SmtpPort ?? 587, dto.SmtpUser,
                        string.IsNullOrEmpty(dto.SmtpPasswordEncrypted) ? "" : _protector.Decrypt(dto.SmtpPasswordEncrypted),
                        dto.SmtpFromAddress, dto.SmtpFromName);
                else if (dto?.Mode == "mailpit")
                    value = new TenantSmtp("mailpit", 1025, null, "", null, null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Platform SMTP ayarı alınamadı, kurulum ayarı kullanılacak: {Message}", ex.Message);
        }
        _platformSmtp = (DateTimeOffset.UtcNow.Add(PlatformSmtpTtl), value);
        return value;
    }

    public async Task<TenantSmtp?> GetSmtpConfigAsync(string tenantSlug, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantSlug)) return null;

        if (SmtpCache.TryGetValue(tenantSlug, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
            return cached.Value;

        try
        {
            var resp = await _http.GetAsync($"{_baseUrl}/internal/smtp-config/{tenantSlug}", ct);
            if (!resp.IsSuccessStatusCode)
            {
                // 404: ozel SMTP ayarlanmamis - bu bir hata degil, cok yaygin
                // bir durum (kiracilarin cogu platform varsayilanini kullanir).
                SmtpCache[tenantSlug] = (DateTimeOffset.UtcNow.Add(CacheTtl), null);
                return null;
            }

            var dto = JsonSerializer.Deserialize<SmtpConfigDto>(
                await resp.Content.ReadAsStringAsync(ct), JsonOpts);
            if (dto is null || string.IsNullOrEmpty(dto.SmtpPasswordEncrypted))
            {
                SmtpCache[tenantSlug] = (DateTimeOffset.UtcNow.Add(CacheTtl), null);
                return null;
            }

            var smtp = new TenantSmtp(
                Host: dto.SmtpHost,
                Port: dto.SmtpPort ?? 587,
                User: dto.SmtpUser,
                Password: _protector.Decrypt(dto.SmtpPasswordEncrypted),
                FromAddress: dto.SmtpFromAddress,
                FromName: dto.SmtpFromName);

            SmtpCache[tenantSlug] = (DateTimeOffset.UtcNow.Add(CacheTtl), smtp);
            return smtp;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Tenant ozel SMTP ayarlari alinamadi/cozulemedi ({Slug}), platform varsayilan SMTP'si kullanilacak",
                tenantSlug);
            return null;
        }
    }
}

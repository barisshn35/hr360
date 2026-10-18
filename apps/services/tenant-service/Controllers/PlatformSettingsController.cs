using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TenantService.Data;
using TenantService.Models;
using TenantService.Security;
using TenantService.Services;

namespace TenantService.Controllers;

/// <summary>
/// Platform yönetimi > Sistem ayarları: arayüzden değiştirilen uygulama içi ayarlar (platform_settings).
/// Yalnızca platform yöneticisi. Her değişiklik audit_log'a yazılır (parola yazılmaz). SMTP değişince
/// Keycloak realm'inin e-posta ayarı da eşitlenir (eski yöntem e-postaları onu kullanır).
/// Sunucu düzeyindeki ayarlar (TLS, Keycloak paneli portu) burada değil; yedek ve güncelleme PlatformOpsController.
/// </summary>
[ApiController]
[Route("api/platform/settings")]
[Authorize(Policy = "RequirePlatformAdmin")]
public partial class PlatformSettingsController : ControllerBase
{
    private static readonly HashSet<int> AllowedPorts = new() { 25, 465, 587, 1025, 2525 };

    private readonly TenantDbContext _db;
    private readonly PlatformSettingsService _settings;
    private readonly SmtpCredentialProtector _protector;
    private readonly SecurityNotifier _audit;
    private readonly KeycloakAdminClient _kc;
    private readonly ILogger<PlatformSettingsController> _log;

    public PlatformSettingsController(TenantDbContext db, PlatformSettingsService settings, SmtpCredentialProtector protector,
        SecurityNotifier audit, KeycloakAdminClient kc, ILogger<PlatformSettingsController> log)
    {
        _db = db; _settings = settings; _protector = protector; _audit = audit; _kc = kc; _log = log;
    }

    [GeneratedRegex(@"^[A-Za-z0-9.-]{1,253}$")]
    private static partial Regex HostRegex();

    private string? ActorName => User.FindFirst("preferred_username")?.Value ?? User.FindFirst("email")?.Value;
    private string? ActorId => User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        PlatformSettingsService.Invalidate();
        var row = await _settings.GetRowAsync(ct);
        var smtp = PlatformSettingsService.FromRow(row);
        var envHost = Environment.GetEnvironmentVariable("SMTP_HOST")?.Trim();
        var tenantsWithOwnSmtp = await _db.Tenants.CountAsync(t => t.SmtpHost != null, ct);
        return Ok(new
        {
            smtp = new
            {
                mode = smtp.Mode,
                configured = smtp.Configured,
                host = smtp.Mode == "mailpit" ? null : smtp.Host,
                port = smtp.Mode == "mailpit" ? (int?)null : smtp.Port,
                user = smtp.User,
                hasPassword = smtp.Mode == "custom" ? !string.IsNullOrEmpty(row.SmtpPasswordEncrypted) : !string.IsNullOrEmpty(smtp.PasswordPlain),
                fromAddress = smtp.FromAddress,
                fromName = smtp.FromName,
                // Kurulumda .env'e girilen gerçek sunucu (varsa): "kurulum ayarına dön" seçeneği için.
                installHost = envHost is { Length: > 0 } && !PlatformSettingsService.IsMailpitHost(envHost) ? envHost : null,
                // Kurulumda "sunucu kendisi göndersin" seçildiyse (installHost = mailer).
                installSelf = envHost is { Length: > 0 } && PlatformSettingsService.IsSelfMailerHost(envHost),
                tenantsWithOwnSmtp,
            },
            signupEnabled = await _settings.SignupEnabledAsync(ct),
            passwordMethod = await _settings.DefaultPasswordMethodAsync(ct),
            inviteTtlHours = await _settings.InviteTtlHoursAsync(ct),
            site = SiteView(row),
            updatedAt = row.UpdatedAt,
            updatedBy = row.UpdatedBy,
        });
    }

    public record SmtpRequest(string? Host, int? Port, string? User, string? Password, string? FromAddress, string? FromName);

    /// <summary>Platform e-posta sunucusu (custom). Password: null = değiştirme, "" = sil.</summary>
    [HttpPut("smtp")]
    public async Task<IActionResult> PutSmtp([FromBody] SmtpRequest r, CancellationToken ct)
    {
        var host = r.Host?.Trim() ?? "";
        var port = r.Port ?? 587;
        if (!HostRegex().IsMatch(host)) return BadRequest(new { field = "host", message = "Geçerli bir sunucu adı girin (örn. smtp.sirket.com)" });
        if (!AllowedPorts.Contains(port)) return BadRequest(new { field = "port", message = "Port 25, 465, 587, 1025 ya da 2525 olmalı" });
        var from = r.FromAddress?.Trim();
        if (string.IsNullOrEmpty(from) || from.Length > 320 || !System.Net.Mail.MailAddress.TryCreate(from, out _))
            return BadRequest(new { field = "fromAddress", message = "Geçerli bir gönderen adresi girin" });
        if (r.User is { Length: > 320 }) return BadRequest(new { field = "user", message = "Kullanıcı adı çok uzun" });
        if (r.Password is { Length: > 1000 }) return BadRequest(new { field = "password", message = "Parola çok uzun" });
        var fromName = string.IsNullOrWhiteSpace(r.FromName) ? "HR360" : r.FromName.Trim();
        if (fromName.Length > 120) return BadRequest(new { field = "fromName", message = "Gönderen adı en fazla 120 karakter" });

        var row = await _settings.GetTrackedRowAsync(ct);
        row.SmtpMode = "custom";
        row.SmtpHost = host;
        row.SmtpPort = port;
        row.SmtpUser = string.IsNullOrWhiteSpace(r.User) ? null : r.User.Trim();
        if (r.Password is not null)
            row.SmtpPasswordEncrypted = r.Password.Length == 0 ? null : _protector.Encrypt(r.Password);
        row.SmtpFromAddress = from;
        row.SmtpFromName = fromName;
        await SaveAsync(row, "PlatformSmtpChanged", new { mode = "custom", host, port, user = row.SmtpUser, from, passwordChanged = r.Password is not null }, ct);
        await SyncRealmAsync(ct);
        return await Get(ct);
    }

    public record SmtpModeRequest(string? Mode);

    /// <summary>mailpit: paketteki test kutusu (gerçek gönderim yok); install: kurulumda .env'e girilen sunucu.</summary>
    [HttpPut("smtp/mode")]
    public async Task<IActionResult> PutSmtpMode([FromBody] SmtpModeRequest r, CancellationToken ct)
    {
        if (r.Mode is not ("mailpit" or "install")) return BadRequest(new { message = "Geçersiz seçim" });
        var row = await _settings.GetTrackedRowAsync(ct);
        row.SmtpMode = r.Mode == "mailpit" ? "mailpit" : null;
        await SaveAsync(row, "PlatformSmtpChanged", new { mode = r.Mode }, ct);
        await SyncRealmAsync(ct);
        return await Get(ct);
    }

    public record SmtpTestRequest(string? To);

    /// <summary>Geçerli platform SMTP'siyle deneme e-postası gönderir (hemen; sonuç yanıtta). Parola yanıtta/günlükte yok.</summary>
    [HttpPost("smtp/test")]
    public async Task<IActionResult> TestSmtp([FromBody] SmtpTestRequest r, CancellationToken ct)
    {
        var to = r.To?.Trim();
        if (string.IsNullOrEmpty(to) || !System.Net.Mail.MailAddress.TryCreate(to, out _))
            return BadRequest(new { message = "Geçerli bir alıcı adresi girin" });
        PlatformSettingsService.Invalidate();
        var smtp = await _settings.SmtpAsync(ct);
        string? error = null;
        try
        {
            var message = new MimeKit.MimeMessage();
            message.From.Add(new MimeKit.MailboxAddress(smtp.FromName, smtp.FromAddress));
            message.To.Add(MimeKit.MailboxAddress.Parse(to));
            message.Subject = "HR360 deneme e-postası / test email";
            message.Body = new MimeKit.TextPart("plain")
            {
                Text = "Bu e-posta HR360 Sistem ayarlarından gönderilen bir denemedir; e-posta sunucusu çalışıyor.\n\n" +
                       "This is a test email sent from HR360 System settings; the mail server works.",
            };
            using var client = new MailKit.Net.Smtp.SmtpClient { Timeout = 15000 };
            await client.ConnectAsync(smtp.Host, smtp.Port, MailKit.Security.SecureSocketOptions.Auto, ct);
            var password = smtp.PasswordPlain ?? (smtp.PasswordEncrypted is { Length: > 0 } enc ? _protector.Decrypt(enc) : null);
            if (client.Capabilities.HasFlag(MailKit.Net.Smtp.SmtpCapabilities.Authentication)
                && !string.IsNullOrEmpty(smtp.User) && !string.IsNullOrEmpty(password))
                await client.AuthenticateAsync(smtp.User, password, ct);
            await client.SendAsync(message, ct);
            await client.DisconnectAsync(true, ct);
        }
        catch (MailKit.Security.AuthenticationException) { error = "Kimlik doğrulaması başarısız (kullanıcı adı ya da parola)."; }
        catch (Exception ex) when (ex is MailKit.ProtocolException or MailKit.Net.Smtp.SmtpCommandException or System.Net.Sockets.SocketException
                                       or IOException or TimeoutException or System.Security.Authentication.AuthenticationException)
        { error = "E-posta sunucusuna bağlanılamadı ya da gönderim reddedildi: " + ex.Message; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { error = "E-posta sunucusu zamanında yanıt vermedi."; }
        await _audit.AuditAsync(null, "PlatformSettings", "1", "PlatformSmtpTested", new { mode = smtp.Mode, ok = error is null }, ActorId, ActorName, ct);
        return Ok(new
        {
            ok = error is null,
            mode = smtp.Mode,
            message = error ?? (smtp.Configured
                ? $"Deneme e-postası {to} adresine gönderildi."
                : "Deneme e-postası paketteki test kutusuna (Mailpit) bırakıldı; gerçek alıcıya gitmez."),
        });
    }

    public record GeneralRequest(bool? SignupEnabled, string? PasswordMethod, int? InviteTtlHours);

    [HttpPut("general")]
    public async Task<IActionResult> PutGeneral([FromBody] GeneralRequest r, CancellationToken ct)
    {
        if (r.PasswordMethod is not null && !PasswordSetupMethods.Valid(r.PasswordMethod))
            return BadRequest(new { field = "passwordMethod", message = "Yöntem 'invite' ya da 'keycloak' olmalı" });
        if (r.InviteTtlHours is { } h && (h < PlatformSettingsService.MinInviteTtlHours || h > PlatformSettingsService.MaxInviteTtlHours))
            return BadRequest(new { field = "inviteTtlHours", message = "Link süresi 1-720 saat olmalı" });
        if (r.PasswordMethod == PasswordSetupMethods.Keycloak && !(await _settings.SmtpAsync(ct)).Configured)
            return BadRequest(new { field = "passwordMethod", message = "Keycloak e-postası yöntemi için önce e-posta sunucusunu ayarlayın" });
        var row = await _settings.GetTrackedRowAsync(ct);
        if (r.SignupEnabled is { } s) row.SignupEnabled = s;
        if (r.PasswordMethod is not null) row.PasswordMethod = r.PasswordMethod;
        if (r.InviteTtlHours is { } t) row.InviteTtlHours = t;
        await SaveAsync(row, "PlatformSettingsChanged", new { r.SignupEnabled, r.PasswordMethod, r.InviteTtlHours }, ct);
        return await Get(ct);
    }

    /// <summary>Herkese açık site bilgileri; boş metin = arayüzdeki varsayılan şablon.</summary>
    public static object SiteView(PlatformSetting row) => new
    {
        operatorName = row.SiteOperatorName,
        address = row.SiteAddress,
        email = row.SiteEmail,
        phone = row.SitePhone,
        kep = row.SiteKep,
        verbisNo = row.SiteVerbisNo,
        privacyMd = row.PrivacyPolicyMd,
        termsMd = row.TermsMd,
        faq = PlatformSettingsService.ParseFaq(row.FaqJson),
        searchIndexing = row.SearchIndexing ?? false,
    };

    public record SiteRequest(string? OperatorName, string? Address, string? Email, string? Phone, string? Kep, string? VerbisNo,
        string? PrivacyMd, string? TermsMd, List<PlatformSettingsService.FaqItem>? Faq, bool? SearchIndexing);

    [GeneratedRegex(@"^[0-9+()\-. ]{3,40}$")]
    private static partial Regex PhoneRegex();

    /// <summary>Yasal metinler, SSS, iletişim bilgileri ve arama motoru görünürlüğü (tamamı birlikte kaydedilir).</summary>
    [HttpPut("site")]
    public async Task<IActionResult> PutSite([FromBody] SiteRequest r, CancellationToken ct)
    {
        static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        var name = Clean(r.OperatorName);
        var address = Clean(r.Address);
        var email = Clean(r.Email);
        var phone = Clean(r.Phone);
        var kep = Clean(r.Kep);
        var verbis = Clean(r.VerbisNo);
        var privacy = Clean(r.PrivacyMd);
        var terms = Clean(r.TermsMd);
        if (name is { Length: > 200 }) return BadRequest(new { field = "operatorName", message = "İşletmeci adı en fazla 200 karakter" });
        if (address is { Length: > 500 }) return BadRequest(new { field = "address", message = "Adres en fazla 500 karakter" });
        if (email is not null && (email.Length > 320 || !System.Net.Mail.MailAddress.TryCreate(email, out _)))
            return BadRequest(new { field = "email", message = "Geçerli bir e-posta adresi girin" });
        if (phone is not null && !PhoneRegex().IsMatch(phone)) return BadRequest(new { field = "phone", message = "Geçerli bir telefon numarası girin" });
        if (kep is not null && (kep.Length > 320 || !System.Net.Mail.MailAddress.TryCreate(kep, out _)))
            return BadRequest(new { field = "kep", message = "Geçerli bir KEP adresi girin" });
        if (verbis is { Length: > 64 }) return BadRequest(new { field = "verbisNo", message = "VERBİS numarası en fazla 64 karakter" });
        if (privacy is { Length: > PlatformSettingsService.MaxLegalTextLength } || terms is { Length: > PlatformSettingsService.MaxLegalTextLength })
            return BadRequest(new { field = privacy is { Length: > PlatformSettingsService.MaxLegalTextLength } ? "privacyMd" : "termsMd", message = "Metin en fazla 100.000 karakter" });
        var faq = (r.Faq ?? []).Select(f => new PlatformSettingsService.FaqItem(f.Q?.Trim() ?? "", f.A?.Trim() ?? ""))
            .Where(f => f.Q.Length > 0 || f.A.Length > 0).ToList();
        if (faq.Count > PlatformSettingsService.MaxFaqItems) return BadRequest(new { field = "faq", message = "En fazla 50 soru eklenebilir" });
        if (faq.Any(f => f.Q.Length == 0 || f.A.Length == 0)) return BadRequest(new { field = "faq", message = "Her sorunun bir cevabı olmalı" });
        if (faq.Any(f => f.Q.Length > PlatformSettingsService.MaxFaqQuestionLength || f.A.Length > PlatformSettingsService.MaxFaqAnswerLength))
            return BadRequest(new { field = "faq", message = "Soru en fazla 300, cevap en fazla 5.000 karakter" });

        var row = await _settings.GetTrackedRowAsync(ct);
        row.SiteOperatorName = name;
        row.SiteAddress = address;
        row.SiteEmail = email;
        row.SitePhone = phone;
        row.SiteKep = kep;
        row.SiteVerbisNo = verbis;
        row.PrivacyPolicyMd = privacy;
        row.TermsMd = terms;
        row.FaqJson = PlatformSettingsService.SerializeFaq(faq);
        if (r.SearchIndexing is { } idx) row.SearchIndexing = idx;
        await SaveAsync(row, "PlatformSiteChanged", new
        {
            operatorName = name, email, phone, kep, verbisNo = verbis,
            privacyCustom = privacy is not null, termsCustom = terms is not null, faqCount = faq.Count, searchIndexing = row.SearchIndexing,
        }, ct);
        return await Get(ct);
    }

    private async Task SaveAsync(PlatformSetting row, string action, object changes, CancellationToken ct)
    {
        row.UpdatedAt = DateTimeOffset.UtcNow;
        row.UpdatedBy = ActorName;
        await _db.SaveChangesAsync(ct);
        PlatformSettingsService.Invalidate();
        await _audit.AuditAsync(null, "PlatformSettings", "1", action, changes, ActorId, ActorName, ct);
    }

    private async Task SyncRealmAsync(CancellationToken ct)
    {
        try
        {
            var smtp = await _settings.SmtpAsync(ct);
            await _kc.ApplyRealmMailSettingsAsync(false, PlatformSettingsService.RealmSmtp(smtp, _protector.Decrypt), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Kayıt geçerli; eşitleme açılışta (RealmMailSyncHostedService) yeniden denenir.
            _log.LogWarning("Keycloak e-posta ayarı eşitlenemedi: {Message}", ex.Message);
        }
    }
}

/// <summary>
/// Açılışta Keycloak realm'ini platform ayarlarıyla eşitler: Keycloak'ın kendi "parolamı unuttum" bağlantısı
/// kapalı (HR360 akışı kullanılır) ve realm SMTP'si = geçerli platform SMTP'si.
/// </summary>
public sealed class RealmMailSyncHostedService : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<RealmMailSyncHostedService> _log;
    public RealmMailSyncHostedService(IServiceProvider sp, ILogger<RealmMailSyncHostedService> log) { _sp = sp; _log = log; }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        HR360.Platform.LeaderGate.RunAsync("tenant/RealmMailSyncHostedService", stoppingToken, LeaderLoopAsync);

    private async Task LeaderLoopAsync(CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 20 && !ct.IsCancellationRequested; attempt++)
        {
            try
            {
                using var scope = _sp.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<PlatformSettingsService>();
                var kc = scope.ServiceProvider.GetRequiredService<KeycloakAdminClient>();
                var protector = scope.ServiceProvider.GetRequiredService<SmtpCredentialProtector>();
                var smtp = await settings.SmtpAsync(ct);
                await kc.ApplyRealmMailSettingsAsync(false, PlatformSettingsService.RealmSmtp(smtp, protector.Decrypt), ct);
                _log.LogInformation("Keycloak e-posta ayarı eşitlendi ({Mode})", smtp.Mode);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning("Keycloak e-posta ayarı eşitlenemedi (deneme {Attempt}): {Message}", attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 5 * attempt)), ct);
            }
        }
    }
}

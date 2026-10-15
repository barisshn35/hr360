using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TenantService.Data;
using TenantService.Models;

namespace TenantService.Services;

/// <summary>Link kime gösterilir.</summary>
public enum LinkDisplay
{
    /// <summary>İşlemi yapan yöneticiye bir kez gösterilir (e-posta açıksa ayrıca e-postayla gider).</summary>
    Show,
    /// <summary>E-posta açıksa yalnızca e-postayla gider; kapalıysa işlemi yapana gösterilir (ör. şirket kaydı).</summary>
    ShowIfNoEmail,
    /// <summary>Hiç gösterilmez: e-posta açıksa e-postayla gider, değilse link bekler (yönetici yeniden üretir).</summary>
    Never,
}

/// <summary>İşlemi yapan kişi (denetim kaydı); anonim istekte Id null.</summary>
public sealed record LinkActor(string? Id, string? Name);

/// <summary>
/// HR360'ın kendi parola belirleme/sıfırlama linkleri: tek kullanımlık, süreli (varsayılan 72 saat).
/// Token yalnızca üretildiği anda bellekte bulunur; veritabanında SHA-256 özeti tutulur. Aynı hesap için
/// yeni link açık linkleri iptal eder. Şirketin yöntemi "keycloak" (eski yöntem) ve platform SMTP'si
/// gerçekse Keycloak'ın kendi e-postası gönderilir; gönderilemezse davet linkine düşülür.
/// Oluşturma / kullanma / iptal audit_log'a yazılır (token yazılmaz).
/// </summary>
public sealed class PasswordLinkService
{
    public const int TokenBytes = 32;
    /// <summary>Keycloak execute-actions-email linkinin ömrü (KeycloakAdminClient: lifespan=86400).</summary>
    private static readonly TimeSpan KeycloakLinkLifetime = TimeSpan.FromHours(24);

    private static readonly string NotificationBase =
        (Environment.GetEnvironmentVariable("NOTIFICATION_SERVICE_URL") ?? "http://notification-service:8080").TrimEnd('/');

    private readonly TenantDbContext _db;
    private readonly PlatformSettingsService _settings;
    private readonly KeycloakAdminClient _kc;
    private readonly SecurityNotifier _audit;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<PasswordLinkService> _log;

    public PasswordLinkService(TenantDbContext db, PlatformSettingsService settings, KeycloakAdminClient kc,
        SecurityNotifier audit, IHttpClientFactory http, ILogger<PasswordLinkService> log)
    {
        _db = db; _settings = settings; _kc = kc; _audit = audit; _http = http; _log = log;
    }

    public sealed record IssueRequest(Tenant? Tenant, string KeycloakUserId, string Email, string? FullName,
        string Purpose, LinkDisplay Display, LinkActor Actor, string? Language = null, bool SelfService = false);

    /// <summary>
    /// Sonuç. Method: geçerli yöntem (invite | keycloak). Link yalnızca gösterilecekse dolu.
    /// EmailStatus: none (e-posta kapalı) | queued (HR360 linki e-postayla kuyrukta) | keycloak (Keycloak e-postası gitti) | failed.
    /// </summary>
    public sealed record Issued(Guid InviteId, string Method, string? Link, DateTimeOffset ExpiresAt, string EmailStatus,
        bool EmailFallback, string Purpose);

    public static LinkActor ActorOf(System.Security.Claims.ClaimsPrincipal u) =>
        new(u.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? u.FindFirst("sub")?.Value,
            u.FindFirst("preferred_username")?.Value ?? u.FindFirst("email")?.Value ?? u.FindFirst("name")?.Value);

    public static string Origin() =>
        (Environment.GetEnvironmentVariable("PUBLIC_ORIGIN") ?? Environment.GetEnvironmentVariable("PUBLIC_URL") ?? "http://localhost").TrimEnd('/');

    public static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(TokenBytes));

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>Token biçimi: 43 karakter base64url (32 bayt). Diğerleri veritabanına hiç sorulmaz.</summary>
    public static bool LooksLikeToken(string? token) =>
        token is { Length: 43 } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public static string LinkFor(string token) => $"{Origin()}/parola-belirle/{token}";

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public async Task<Issued> IssueAsync(IssueRequest r, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await RevokeOpenAsync(r.KeycloakUserId, r.Tenant?.Slug, r.Actor, "superseded", ct);

        var (_, method) = await _settings.PasswordMethodAsync(r.Tenant, ct);
        var emailAvailable = await _settings.EmailAvailableAsync(r.Tenant, ct);
        var fallback = false;

        // "Parolamı unuttum" her zaman HR360 linkiyle (Keycloak'ın kendi sıfırlama bağlantısı kapalı).
        if (method == PasswordSetupMethods.Keycloak && !r.SelfService)
        {
            try
            {
                await _kc.SendPasswordSetupEmailAsync(r.KeycloakUserId, ct);
                var kcInvite = new PasswordInvite
                {
                    TenantSlug = r.Tenant?.Slug, KeycloakUserId = r.KeycloakUserId, Email = r.Email, FullName = r.FullName,
                    Purpose = r.Purpose, Channel = PasswordInviteChannels.Keycloak, EmailStatus = "keycloak",
                    CreatedAt = now, ExpiresAt = now + KeycloakLinkLifetime, CreatedBy = r.Actor.Id, CreatedByName = r.Actor.Name,
                };
                _db.PasswordInvites.Add(kcInvite);
                await _db.SaveChangesAsync(ct);
                await AuditAsync(kcInvite, "PasswordLinkCreated", r.Actor, new { method, channel = kcInvite.Channel }, ct);
                return new Issued(kcInvite.Id, PasswordSetupMethods.Keycloak, null, kcInvite.ExpiresAt, "keycloak", false, r.Purpose);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Eski yöntem seçili ama Keycloak e-postası gitmedi: davet linkine düş, yöneticiye göster.
                _log.LogWarning("Keycloak parola e-postası gönderilemedi, davet linkine düşülüyor: {Message}", ex.Message);
                fallback = true;
            }
        }

        var ttl = TimeSpan.FromHours(await _settings.InviteTtlHoursAsync(ct));
        var showLink = fallback || r.Display == LinkDisplay.Show || (r.Display == LinkDisplay.ShowIfNoEmail && !emailAvailable);
        var sendEmail = emailAvailable && !fallback;
        // Hiç iletilemeyecek link üretilmez: yönetici sonra "yeniden üret" ile gösterir.
        var produce = showLink || sendEmail;
        var token = produce ? NewToken() : null;

        var invite = new PasswordInvite
        {
            TenantSlug = r.Tenant?.Slug, KeycloakUserId = r.KeycloakUserId, Email = r.Email, FullName = r.FullName,
            Purpose = r.Purpose, TokenHash = token is null ? null : Hash(token),
            Channel = showLink ? PasswordInviteChannels.Link : PasswordInviteChannels.Email,
            EmailStatus = fallback ? "failed" : "none",
            CreatedAt = now, ExpiresAt = now + ttl, CreatedBy = r.Actor.Id, CreatedByName = r.Actor.Name,
        };
        _db.PasswordInvites.Add(invite);
        await _db.SaveChangesAsync(ct);

        if (sendEmail && token is not null)
        {
            invite.EmailStatus = await SendEmailAsync(r, token, invite.ExpiresAt, ct) ? "queued" : "failed";
            if (invite.EmailStatus == "failed" && !showLink && r.Display != LinkDisplay.Never)
            {
                // E-posta kuyruğa alınamadı: link işlemi yapana gösterilir.
                showLink = true;
                invite.Channel = PasswordInviteChannels.Link;
                fallback = true;
            }
            await _db.SaveChangesAsync(ct);
        }

        await AuditAsync(invite, "PasswordLinkCreated", r.Actor,
            new { method = PasswordSetupMethods.Invite, channel = invite.Channel, emailStatus = invite.EmailStatus, expiresAt = invite.ExpiresAt, fallback }, ct);
        return new Issued(invite.Id, PasswordSetupMethods.Invite, showLink && token is not null ? LinkFor(token) : null,
            invite.ExpiresAt, invite.EmailStatus, fallback, r.Purpose);
    }

    /// <summary>Hesabın açık (kullanılmamış, iptal edilmemiş) linklerini iptal eder.</summary>
    public async Task<int> RevokeOpenAsync(string keycloakUserId, string? tenantSlug, LinkActor actor, string reason, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var open = await _db.PasswordInvites
            .Where(i => i.KeycloakUserId == keycloakUserId && i.UsedAt == null && i.RevokedAt == null && i.ExpiresAt > now)
            .ToListAsync(ct);
        foreach (var i in open) i.RevokedAt = now;
        if (open.Count == 0) return 0;
        await _db.SaveChangesAsync(ct);
        foreach (var i in open) await AuditAsync(i, "PasswordLinkRevoked", actor, new { reason }, ct);
        return open.Count;
    }

    public async Task<bool> RevokeAsync(PasswordInvite invite, LinkActor actor, CancellationToken ct)
    {
        if (invite.UsedAt is not null || invite.RevokedAt is not null) return false;
        invite.RevokedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        await AuditAsync(invite, "PasswordLinkRevoked", actor, new { reason = "manual" }, ct);
        return true;
    }

    public sealed record LinkState(string Status, PasswordInvite? Invite);

    /// <summary>Token'ın durumu: active | expired | used | revoked | invalid.</summary>
    public async Task<LinkState> LookupAsync(string? token, CancellationToken ct)
    {
        if (!LooksLikeToken(token)) return new("invalid", null);
        var hash = Hash(token!);
        var invite = await _db.PasswordInvites.FirstOrDefaultAsync(i => i.TokenHash == hash, ct);
        if (invite is null) return new("invalid", null);
        var status = invite.StatusAt(DateTimeOffset.UtcNow);
        return new(status == "pending" ? "invalid" : status, invite);
    }

    public sealed record ConsumeResult(bool Ok, string Status, string? ErrorCode = null, string? ErrorDescription = null);

    /// <summary>Linkle parolayı belirler. Başarıda link kullanılmış olur; parola politikası reddederse link açık kalır.</summary>
    public async Task<ConsumeResult> ConsumeAsync(string? token, string password, CancellationToken ct)
    {
        var state = await LookupAsync(token, ct);
        if (state.Status != "active" || state.Invite is null) return new(false, state.Status);
        var invite = state.Invite;

        var user = await _kc.GetUserBasicsAsync(invite.KeycloakUserId, ct);
        if (user is null || !user.Enabled) return new(false, "account_disabled");

        try
        {
            await _kc.ResetPasswordAsync(invite.KeycloakUserId, password, ct);
        }
        catch (PasswordPolicyException ex)
        {
            return new(false, "active", ex.Code, ex.Description);
        }

        // Aynı link iki kez kullanılmasın: koşullu güncelleme (eşzamanlı ikinci istek 0 satır görür).
        var now = DateTimeOffset.UtcNow;
        var rows = await _db.PasswordInvites
            .Where(i => i.Id == invite.Id && i.UsedAt == null && i.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.UsedAt, now), ct);
        if (rows == 0) return new(false, "used");

        try
        {
            // E-posta yalnızca link e-postayla iletildiyse doğrulanmış sayılır (ekranda gösterilen link e-posta sahipliğini kanıtlamaz).
            await _kc.CompletePasswordSetupAsync(invite.KeycloakUserId, invite.Channel == PasswordInviteChannels.Email, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning("Parola belirlendi ancak hesap gereksinimleri güncellenemedi: {Message}", ex.Message);
        }
        await AuditAsync(invite, "PasswordLinkUsed", new LinkActor(invite.KeycloakUserId, user.Email), new { purpose = invite.Purpose }, ct);
        return new(true, "used");
    }

    private async Task<bool> SendEmailAsync(IssueRequest r, string token, DateTimeOffset expiresAt, CancellationToken ct)
    {
        var internalToken = Environment.GetEnvironmentVariable("INTERNAL_SERVICE_TOKEN");
        if (string.IsNullOrEmpty(internalToken)) return false;
        var lang = r.Language is "en" ? "en" : "tr";
        var (subject, body, label) = EmailTexts(r.Purpose, lang, r.Tenant?.Name, r.FullName, expiresAt);
        try
        {
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            using var req = new HttpRequestMessage(HttpMethod.Post, NotificationBase + "/api/internal/chat/notify")
            {
                Content = JsonContent.Create(new
                {
                    tenantSlug = r.Tenant?.Slug ?? "platform",
                    recipientEmployeeId = Guid.Empty,
                    recipientEmail = r.Email,
                    subject,
                    body,
                    templateCode = r.Purpose == PasswordInvitePurposes.Reset ? "security.password-reset" : "security.password-setup",
                    language = lang,
                    actionUrl = LinkFor(token),
                    actionLabel = label,
                }),
            };
            req.Headers.Add(Security.InternalServiceToken.Header, internalToken);
            using var resp = await client.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode) return true;
            _log.LogWarning("Parola linki e-postası kuyruğa alınamadı (HTTP {Status})", (int)resp.StatusCode);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning("Parola linki e-postası kuyruğa alınamadı: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>E-posta metni (TR/EN). Link gövdeye yazılmaz; e-postadaki düğmede (ActionUrl) yer alır.</summary>
    public static (string Subject, string Body, string Label) EmailTexts(string purpose, string lang, string? company, string? fullName, DateTimeOffset expiresAt)
    {
        var tr = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");
        var until = TimeZoneInfo.ConvertTime(expiresAt, tr).ToString("dd.MM.yyyy HH:mm");
        var greet = string.IsNullOrWhiteSpace(fullName) ? null : fullName.Trim();
        if (lang == "en")
        {
            return purpose == PasswordInvitePurposes.Reset
                ? ("Reset your HR360 password",
                   $"{(greet is null ? "Hello," : $"Hello {greet},")}\n\nA password reset was requested for your HR360 account. Use the button below to choose a new password. The link can be used once and is valid until {until} (Turkey time).\n\nIf you did not request this, you can ignore this email; your password stays the same.",
                   "Choose a new password")
                : ($"Set your HR360 password{(company is null ? "" : $" — {company}")}",
                   $"{(greet is null ? "Hello," : $"Hello {greet},")}\n\nAn HR360 account has been created for you{(company is null ? "" : $" at {company}")}. Use the button below to set your password. The link can be used once and is valid until {until} (Turkey time).",
                   "Set my password");
        }
        return purpose == PasswordInvitePurposes.Reset
            ? ("HR360 parolanızı sıfırlayın",
               $"{(greet is null ? "Merhaba," : $"Merhaba {greet},")}\n\nHR360 hesabınız için parola sıfırlama istendi. Yeni parolanızı aşağıdaki düğmeyle belirleyin. Link bir kez kullanılabilir ve {until} tarihine kadar geçerlidir.\n\nBu isteği siz yapmadıysanız e-postayı yok sayabilirsiniz; parolanız değişmez.",
               "Yeni parola belirle")
            : ($"HR360 parolanızı belirleyin{(company is null ? "" : $" — {company}")}",
               $"{(greet is null ? "Merhaba," : $"Merhaba {greet},")}\n\n{(company is null ? "Sizin için" : $"{company} için")} bir HR360 hesabı açıldı. Parolanızı aşağıdaki düğmeyle belirleyin. Link bir kez kullanılabilir ve {until} tarihine kadar geçerlidir.",
               "Parolamı belirle");
    }

    private Task AuditAsync(PasswordInvite i, string action, LinkActor actor, object extra, CancellationToken ct) =>
        _audit.AuditAsync(i.TenantSlug, "PasswordInvite", i.Id.ToString(), action,
            new { email = i.Email, userId = i.KeycloakUserId, purpose = i.Purpose, details = extra },
            actor.Id, actor.Name, ct);

    /// <summary>E-postayı maskeler: "ay***@sirket.com".</summary>
    public static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 0) return "***";
        var local = email[..at];
        return (local.Length <= 2 ? local[..1] : local[..2]) + "***" + email[at..];
    }
}

/// <summary>Arayüze dönen link özeti. Link yalnızca bu yanıtta bulunur; sonra tekrar okunamaz.</summary>
public sealed record PasswordLinkView(Guid InviteId, string Method, string? Link, DateTimeOffset ExpiresAt, string EmailStatus,
    bool EmailFallback, string Purpose)
{
    public static PasswordLinkView From(PasswordLinkService.Issued i) =>
        new(i.InviteId, i.Method, i.Link, i.ExpiresAt, i.EmailStatus, i.EmailFallback, i.Purpose);
}


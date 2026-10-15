using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using TenantService.Data;
using TenantService.Models;
using TenantService.Services;

namespace TenantService.Controllers;

/// <summary>
/// Süreli parola linkinin herkese açık uçları (oturumsuz): linkin durumu, parolayı belirleme ve
/// "parolamı unuttum". Gateway: /api/tenant/public/password/... (düşük istek sınırı) + IP başına sınır.
/// </summary>
[ApiController]
[Route("api/public/password")]
[AllowAnonymous]
public class PublicPasswordController : ControllerBase
{
    public const int MaxPasswordLength = 256;

    private readonly PasswordLinkService _links;
    private readonly TenantDbContext _db;
    public PublicPasswordController(PasswordLinkService links, TenantDbContext db) { _links = links; _db = db; }

    /// <summary>Linkin durumu: active | expired | used | revoked | invalid. Aktifse maskeli e-posta ve şirket adı.</summary>
    [HttpGet("link/{token}")]
    [EnableRateLimiting("password-link")]
    public async Task<IActionResult> Status(string token, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var state = await _links.LookupAsync(token, ct);
        if (state.Status != "active" || state.Invite is null) return Ok(new { status = state.Status });
        var i = state.Invite;
        var company = i.TenantSlug is null ? null
            : await _db.Tenants.AsNoTracking().Where(t => t.Slug == i.TenantSlug).Select(t => t.Name).FirstOrDefaultAsync(ct);
        return Ok(new
        {
            status = state.Status,
            purpose = i.Purpose,
            email = PasswordLinkService.MaskEmail(i.Email),
            company,
            expiresAt = i.ExpiresAt,
        });
    }

    public record SetPasswordRequest(string? Password);

    /// <summary>Linkle parolayı belirler. Parola politikasına uymazsa 400 (link açık kalır); link geçersizse 410.</summary>
    [HttpPost("link/{token}")]
    [EnableRateLimiting("password-link")]
    public async Task<IActionResult> SetPassword(string token, [FromBody] SetPasswordRequest request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        if (string.IsNullOrEmpty(request.Password) || request.Password.Length > MaxPasswordLength)
            return BadRequest(new { status = "active", code = "invalidPassword", message = "Parola 1-256 karakter olmalı." });
        var result = await _links.ConsumeAsync(token, request.Password, ct);
        if (result.Ok) return Ok(new { status = "used" });
        if (result.Status == "active")
            return BadRequest(new { status = "active", code = result.ErrorCode, message = PolicyMessage(result.ErrorCode, result.ErrorDescription) });
        return StatusCode(StatusCodes.Status410Gone, new { status = result.Status });
    }

    public record ResetRequest(string? Email, string? Language);

    /// <summary>
    /// "Parolamı unuttum": hesap varsa ve e-posta gönderilebiliyorsa HR360 sıfırlama linki e-postayla gider.
    /// Yanıt hesabın varlığını belli etmez (her zaman 202). Kişi başına saatte en fazla 3 link.
    /// </summary>
    [HttpPost("reset-request")]
    [EnableRateLimiting("password-reset")]
    public async Task<IActionResult> RequestReset([FromBody] ResetRequest request,
        [FromServices] KeycloakAdminClient kc, [FromServices] SecurityNotifier notifier, [FromServices] PlatformSettingsService settings,
        [FromServices] ILogger<PublicPasswordController> log, CancellationToken ct)
    {
        var email = request.Email?.Trim().ToLowerInvariant();
        var accepted = Accepted(new { message = "Bu adrese kayıtlı bir hesap varsa sıfırlama linki gönderildi. E-posta gelmezse İK yöneticinize başvurun." });
        if (string.IsNullOrEmpty(email) || email.Length > 320 || !System.Net.Mail.MailAddress.TryCreate(email, out _)) return accepted;
        try
        {
            var userId = await kc.FindUserByEmailAsync(email, ct);
            if (userId is null) return accepted;
            var since = DateTimeOffset.UtcNow.AddHours(-1);
            var recent = await _db.PasswordInvites.CountAsync(i => i.KeycloakUserId == userId
                && i.Purpose == PasswordInvitePurposes.Reset && i.CreatedBy == null && i.CreatedAt > since, ct);
            if (recent >= 3) return accepted;
            var user = await kc.GetUserBasicsAsync(userId, ct);
            if (user is null || !user.Enabled || string.IsNullOrEmpty(user.Email)) return accepted;
            var slug = await notifier.TenantOfUserAsync(userId, ct);
            var tenant = slug is null ? null : await _db.Tenants.FirstOrDefaultAsync(t => t.Slug == slug, ct);
            if (!await settings.EmailAvailableAsync(tenant, ct)) return accepted;
            await _links.IssueAsync(new PasswordLinkService.IssueRequest(tenant, userId, user.Email,
                $"{user.FirstName} {user.LastName}".Trim(), PasswordInvitePurposes.Reset, LinkDisplay.Never,
                new LinkActor(null, null), request.Language is "en" ? "en" : user.Locale, SelfService: true), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("Parola sıfırlama isteği işlenemedi: {Message}", ex.Message);
        }
        return accepted;
    }

    /// <summary>Keycloak parola politikası hata kodunu anlaşılır Türkçe metne çevirir (arayüz İngilizceye çevirir).</summary>
    public static string PolicyMessage(string? code, string? description) => code switch
    {
        "invalidPasswordMinLengthMessage" => "Parola çok kısa. Daha uzun bir parola seçin.",
        "invalidPasswordMaxLengthMessage" => "Parola çok uzun.",
        "invalidPasswordMinDigitsMessage" => "Parola en az bir rakam içermeli.",
        "invalidPasswordMinUpperCaseCharsMessage" => "Parola en az bir büyük harf içermeli.",
        "invalidPasswordMinLowerCaseCharsMessage" => "Parola en az bir küçük harf içermeli.",
        "invalidPasswordMinSpecialCharsMessage" => "Parola en az bir özel karakter (ör. ! ? * #) içermeli.",
        "invalidPasswordNotUsernameMessage" or "invalidPasswordNotContainsUsernameMessage" => "Parola kullanıcı adınızı içeremez.",
        "invalidPasswordNotEmailMessage" => "Parola e-posta adresiniz olamaz.",
        "invalidPasswordHistoryMessage" => "Bu parolayı yakın zamanda kullandınız; farklı bir parola seçin.",
        "invalidPasswordBlacklistedMessage" => "Bu parola çok yaygın; daha güçlü bir parola seçin.",
        "invalidPasswordRegexPatternMessage" => "Parola şirketin parola kurallarına uymuyor.",
        _ => "Parola, parola kurallarına uymuyor.",
    };
}

/// <summary>
/// Şirket içi davet/parola linkleri: liste, iptal, yeniden üretme ve bir hesap için sıfırlama linki.
/// Yetki: İK yöneticisi ve üstü. İK yöneticisi şirket yöneticisinin (tenant-admin) linkini üretemez
/// (yetki yükseltme: linki gören, o hesabın parolasını belirleyebilir). Platform yöneticisi hesaplarına
/// buradan link üretilmez.
/// </summary>
[ApiController]
[Route("api/my-tenant/password-links")]
[Authorize(Policy = "RequireHrAdmin")]
public class PasswordLinksController : ControllerBase
{
    private readonly TenantDbContext _db;
    private readonly PasswordLinkService _links;
    private readonly KeycloakAdminClient _kc;

    public PasswordLinksController(TenantDbContext db, PasswordLinkService links, KeycloakAdminClient kc)
    {
        _db = db; _links = links; _kc = kc;
    }

    private async Task<Tenant?> MyTenantAsync(CancellationToken ct)
    {
        var slug = Security.OrganizationClaimParser.ParseSlug(User.FindFirst("organization")?.Value);
        return string.IsNullOrWhiteSpace(slug) ? null : await _db.Tenants.FirstOrDefaultAsync(t => t.Slug == slug, ct);
    }

    /// <summary>Son 30 günün linkleri ve hâlâ açık olanlar (durum, son kullanma, kim üretti). Token/link yok.</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var tenant = await MyTenantAsync(ct);
        if (tenant is null) return NotFound(new { message = "Kullanıcı bir şirkete bağlı değil" });
        var now = DateTimeOffset.UtcNow;
        var since = now.AddDays(-30);
        var rows = await _db.PasswordInvites.AsNoTracking()
            .Where(i => i.TenantSlug == tenant.Slug && (i.CreatedAt > since || (i.UsedAt == null && i.RevokedAt == null && i.ExpiresAt > now)))
            .OrderByDescending(i => i.CreatedAt).Take(300).ToListAsync(ct);
        return Ok(rows.Select(i => new
        {
            i.Id, i.Email, i.FullName, i.Purpose, i.Channel, i.EmailStatus, i.CreatedAt, i.ExpiresAt, i.UsedAt, i.RevokedAt,
            i.CreatedByName, status = i.StatusAt(now), i.KeycloakUserId,
        }));
    }

    [HttpPost("{id:guid}/revoke")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        var tenant = await MyTenantAsync(ct);
        if (tenant is null) return NotFound(new { message = "Kullanıcı bir şirkete bağlı değil" });
        var invite = await _db.PasswordInvites.FirstOrDefaultAsync(i => i.Id == id && i.TenantSlug == tenant.Slug, ct);
        if (invite is null) return NotFound(new { message = "Link bulunamadı" });
        if (await DeniedForTargetAsync(tenant, invite.KeycloakUserId, ct) is { } denied) return denied;
        return await _links.RevokeAsync(invite, PasswordLinkService.ActorOf(User), ct)
            ? Ok(new { status = "revoked" })
            : Conflict(new { message = "Link zaten kullanılmış ya da iptal edilmiş" });
    }

    /// <summary>Aynı hesap ve amaçla yeni link üretir (açık olanlar iptal edilir); link yanıtta bir kez döner.</summary>
    [HttpPost("{id:guid}/regenerate")]
    public async Task<IActionResult> Regenerate(Guid id, CancellationToken ct)
    {
        var tenant = await MyTenantAsync(ct);
        if (tenant is null) return NotFound(new { message = "Kullanıcı bir şirkete bağlı değil" });
        var invite = await _db.PasswordInvites.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id && i.TenantSlug == tenant.Slug, ct);
        if (invite is null) return NotFound(new { message = "Link bulunamadı" });
        if (await DeniedForTargetAsync(tenant, invite.KeycloakUserId, ct) is { } denied) return denied;
        var user = await _kc.GetUserBasicsAsync(invite.KeycloakUserId, ct);
        if (user is null) return NotFound(new { message = "Hesap bulunamadı" });
        if (!user.Enabled) return Conflict(new { message = "Hesap kapalı; önce hesabı açın" });
        var issued = await _links.IssueAsync(new PasswordLinkService.IssueRequest(tenant, invite.KeycloakUserId,
            user.Email ?? invite.Email, invite.FullName, invite.Purpose, LinkDisplay.Show, PasswordLinkService.ActorOf(User), user.Locale), ct);
        return Ok(PasswordLinkView.From(issued));
    }

    /// <summary>Şirketteki bir hesap için parola sıfırlama linki (yöneticiye bir kez gösterilir).</summary>
    [HttpPost("users/{keycloakUserId}/reset")]
    public async Task<IActionResult> ResetLink(string keycloakUserId, CancellationToken ct)
    {
        var tenant = await MyTenantAsync(ct);
        if (tenant?.KeycloakOrgId is null) return NotFound(new { message = "Kullanıcı bir şirkete bağlı değil" });
        if (!Guid.TryParse(keycloakUserId, out _)) return BadRequest(new { message = "Geçersiz kullanıcı kimliği" });
        if (!await _kc.IsOrganizationMemberAsync(tenant.KeycloakOrgId, keycloakUserId, ct))
            return NotFound(new { message = "Hesap bu şirkette bulunamadı" });
        if (await DeniedForTargetAsync(tenant, keycloakUserId, ct) is { } denied) return denied;
        var user = await _kc.GetUserBasicsAsync(keycloakUserId, ct);
        if (user is null || string.IsNullOrEmpty(user.Email)) return NotFound(new { message = "Hesap bulunamadı" });
        if (!user.Enabled) return Conflict(new { message = "Hesap kapalı; önce hesabı açın" });
        // Hiç parola belirlememiş hesap için ilk parola linki, diğerleri için sıfırlama.
        var purpose = (await _kc.GetCredentialTypesAsync(keycloakUserId, ct)).Contains("password")
            ? PasswordInvitePurposes.Reset : PasswordInvitePurposes.Setup;
        var issued = await _links.IssueAsync(new PasswordLinkService.IssueRequest(tenant, keycloakUserId, user.Email,
            $"{user.FirstName} {user.LastName}".Trim(), purpose, LinkDisplay.Show, PasswordLinkService.ActorOf(User), user.Locale), ct);
        return Ok(PasswordLinkView.From(issued));
    }

    /// <summary>
    /// Hedef hesap kısıtı: platform yöneticisi hesabına link yok; tenant-admin hesabına yalnızca tenant-admin
    /// (ya da platform yöneticisi) link üretebilir/iptal edebilir.
    /// </summary>
    private async Task<IActionResult?> DeniedForTargetAsync(Tenant tenant, string targetUserId, CancellationToken ct)
    {
        var roles = await _kc.GetUserRealmRolesAsync(targetUserId, ct);
        if (roles.Contains("platform-admin")) return StatusCode(403, new { message = "Platform yöneticisi hesabına buradan link üretilemez" });
        var callerIsTenantAdmin = User.IsInRole("tenant-admin") || User.IsInRole("platform-admin");
        if (roles.Contains("tenant-admin") && !callerIsTenantAdmin)
            return StatusCode(403, new { message = "Şirket yöneticisinin linkini yalnızca şirket yöneticisi üretebilir" });
        return null;
    }
}

/// <summary>Şirketin parola belirleme yöntemi (Ayarlar). Okuma: İK yöneticisi ve üstü; değiştirme: şirket yöneticisi.</summary>
[ApiController]
[Route("api/my-tenant/password-method")]
[Authorize(Policy = "RequireHrAdmin")]
public class PasswordMethodController : ControllerBase
{
    private readonly TenantDbContext _db;
    private readonly PlatformSettingsService _settings;
    private readonly SecurityNotifier _audit;

    public PasswordMethodController(TenantDbContext db, PlatformSettingsService settings, SecurityNotifier audit)
    {
        _db = db; _settings = settings; _audit = audit;
    }

    private async Task<Tenant?> MyTenantAsync(CancellationToken ct)
    {
        var slug = Security.OrganizationClaimParser.ParseSlug(User.FindFirst("organization")?.Value);
        return string.IsNullOrWhiteSpace(slug) ? null : await _db.Tenants.FirstOrDefaultAsync(t => t.Slug == slug, ct);
    }

    private async Task<object> ViewAsync(Tenant tenant, CancellationToken ct)
    {
        var (selected, effective) = await _settings.PasswordMethodAsync(tenant, ct);
        var smtp = await _settings.SmtpAsync(ct);
        return new
        {
            // null: platform varsayılanı izleniyor.
            choice = tenant.PasswordSetupMethod,
            selected,
            effective,
            platformDefault = await _settings.DefaultPasswordMethodAsync(ct),
            // Eski yöntem Keycloak'ın e-postasıdır; Keycloak platform SMTP'sini kullanır.
            keycloakAvailable = smtp.Configured,
            emailAvailable = smtp.Configured || !string.IsNullOrEmpty(tenant.SmtpHost),
            inviteTtlHours = await _settings.InviteTtlHoursAsync(ct),
        };
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var tenant = await MyTenantAsync(ct);
        return tenant is null ? NotFound(new { message = "Kullanıcı bir şirkete bağlı değil" }) : Ok(await ViewAsync(tenant, ct));
    }

    public record MethodRequest(string? Method);

    /// <summary>Method: "invite" | "keycloak" | null (platform varsayılanına dön).</summary>
    [HttpPut]
    [Authorize(Policy = "RequireTenantAdmin")]
    public async Task<IActionResult> Put([FromBody] MethodRequest r, CancellationToken ct)
    {
        var tenant = await MyTenantAsync(ct);
        if (tenant is null) return NotFound(new { message = "Kullanıcı bir şirkete bağlı değil" });
        if (r.Method is not null && !PasswordSetupMethods.Valid(r.Method))
            return BadRequest(new { message = "Yöntem 'invite' ya da 'keycloak' olmalı" });
        if (r.Method == PasswordSetupMethods.Keycloak && !(await _settings.SmtpAsync(ct)).Configured)
            return BadRequest(new { message = "Keycloak e-postası için platform e-posta sunucusu ayarlı olmalı" });
        var before = tenant.PasswordSetupMethod;
        tenant.PasswordSetupMethod = r.Method;
        await _db.SaveChangesAsync(ct);
        await _audit.AuditAsync(tenant.Slug, "Tenant", tenant.Id.ToString(), "PasswordMethodChanged",
            new { before, after = r.Method }, PasswordLinkService.ActorOf(User).Id, PasswordLinkService.ActorOf(User).Name, ct);
        return Ok(await ViewAsync(tenant, ct));
    }
}

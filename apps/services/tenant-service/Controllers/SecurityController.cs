using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TenantService.Data;
using TenantService.Security;
using TenantService.Services;

namespace TenantService.Controllers;

/// <summary>
/// Şirket güvenlik ayarları: kurumsal SSO (Google Workspace / Microsoft Entra ID,
/// Keycloak "identity brokering" ile) ve iki adımlı doğrulama (TOTP) zorunluluğu.
///
/// SSO NASIL ÇALIŞIR: Sağlayıcı realm'e eklenir ve şirketin Keycloak
/// organizasyonuna bağlanır. Giriş ekranında e-posta alan adı şirketinkiyle
/// eşleşen kullanıcı doğrudan şirketin sağlayıcısına yönlenir; dönüşte Keycloak
/// hesabı e-postayla eşleştirir. Sağlayıcı tarafında (Google Cloud Console /
/// Entra ID "App registrations") yanıttaki redirectUri kayıtlı olmalıdır.
/// </summary>
[ApiController]
[Route("api/security")]
[Authorize(Policy = "RequireTenantAdmin")]
public class SecurityController : ControllerBase
{
    private readonly TenantDbContext _db;
    private readonly KeycloakAdminClient _kc;
    public SecurityController(TenantDbContext db, KeycloakAdminClient kc) { _db = db; _kc = kc; }

    private async Task<(string Slug, string OrgId, string? Domain)?> TenantAsync()
    {
        var slug = TenantService.Security.OrganizationClaimParser.ParseSlug(User.FindFirst("organization")?.Value)?.Trim('[', ']', '"', ' ');
        if (string.IsNullOrWhiteSpace(slug)) return null;
        var t = await _db.Tenants.Where(x => x.Slug == slug).Select(x => new { x.Slug, x.KeycloakOrgId, x.EmailDomain }).FirstOrDefaultAsync();
        return t?.KeycloakOrgId is null ? null : (t.Slug, t.KeycloakOrgId, t.EmailDomain);
    }

    private static string PublicOrigin =>
        (Environment.GetEnvironmentVariable("PUBLIC_ORIGIN") ?? Environment.GetEnvironmentVariable("PUBLIC_URL") ?? "http://localhost").TrimEnd('/');

    private static string RedirectUri(string alias) => $"{PublicOrigin}/auth/realms/hr360/broker/{alias}/endpoint";

    [HttpGet("sso")]
    public async Task<IActionResult> GetSso(CancellationToken ct)
    {
        var t = await TenantAsync();
        if (t is null) return NotFound(new { message = "Şirket bulunamadı." });
        var idps = await _kc.ListOrganizationIdentityProvidersAsync(t.Value.OrgId, ct);
        return Ok(new
        {
            domain = t.Value.Domain,
            providers = idps.Select(i => new
            {
                alias = i.GetProperty("alias").GetString(),
                displayName = i.TryGetProperty("displayName", out var d) ? d.GetString() : null,
                providerId = i.TryGetProperty("providerId", out var p) ? p.GetString() : null,
                enabled = i.TryGetProperty("enabled", out var e) && e.GetBoolean(),
                redirectUri = RedirectUri(i.GetProperty("alias").GetString()!),
            }),
            supported = new[]
            {
                new { id = "google", label = "Google Workspace", redirectUri = RedirectUri($"{t.Value.Slug}-google") },
                new { id = "microsoft", label = "Microsoft Entra ID (Azure AD)", redirectUri = RedirectUri($"{t.Value.Slug}-microsoft") },
            },
        });
    }

    public record SsoInput(string Provider, string ClientId, string ClientSecret, string? DirectoryId, string? Domain);

    [HttpPost("sso")]
    public async Task<IActionResult> AddSso(SsoInput body, CancellationToken ct)
    {
        var t = await TenantAsync();
        if (t is null) return NotFound(new { message = "Şirket bulunamadı." });
        if (string.IsNullOrWhiteSpace(body.ClientId) || string.IsNullOrWhiteSpace(body.ClientSecret))
            return BadRequest(new { message = "İstemci kimliği ve gizli anahtar zorunlu." });
        var domain = (body.Domain ?? t.Value.Domain)?.Trim().ToLowerInvariant();
        var alias = $"{t.Value.Slug}-{body.Provider}";
        var config = new Dictionary<string, string>
        {
            ["clientId"] = body.ClientId.Trim(), ["clientSecret"] = body.ClientSecret.Trim(), ["syncMode"] = "IMPORT",
            ["guiOrder"] = "1",
        };
        if (!string.IsNullOrEmpty(domain))
        {
            config["kc.org.domain"] = domain;
            config["kc.org.broker.redirect.mode.email-matches"] = "true";
        }
        string providerId, display;
        switch (body.Provider)
        {
            case "google":
                providerId = "google"; display = "Google ile giriş";
                if (!string.IsNullOrEmpty(domain)) config["hostedDomain"] = domain;
                config["defaultScope"] = "openid profile email";
                break;
            case "microsoft":
                if (string.IsNullOrWhiteSpace(body.DirectoryId) || !Guid.TryParse(body.DirectoryId, out _))
                    return BadRequest(new { message = "Microsoft için Dizin (kiracı) kimliği GUID olarak girilmeli." });
                providerId = "oidc"; display = "Microsoft ile giriş";
                var b = $"https://login.microsoftonline.com/{body.DirectoryId!.Trim()}";
                config["authorizationUrl"] = $"{b}/oauth2/v2.0/authorize";
                config["tokenUrl"] = $"{b}/oauth2/v2.0/token";
                config["logoutUrl"] = $"{b}/oauth2/v2.0/logout";
                config["issuer"] = $"{b}/v2.0";
                config["jwksUrl"] = $"{b}/discovery/v2.0/keys";
                config["useJwksUrl"] = "true";
                config["validateSignature"] = "true";
                config["clientAuthMethod"] = "client_secret_post";
                config["defaultScope"] = "openid profile email";
                config["pkceEnabled"] = "true";
                config["pkceMethod"] = "S256";
                break;
            default:
                return BadRequest(new { message = "Sağlayıcı google veya microsoft olmalı." });
        }
        try
        {
            await _kc.CreateOrganizationIdentityProviderAsync(t.Value.OrgId, alias, display, providerId, config, ct);
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(502, new { message = ex.Message });
        }
        return Ok(new { alias, redirectUri = RedirectUri(alias) });
    }

    [HttpDelete("sso/{alias}")]
    public async Task<IActionResult> RemoveSso(string alias, CancellationToken ct)
    {
        var t = await TenantAsync();
        if (t is null) return NotFound();
        if (!alias.StartsWith(t.Value.Slug + "-", StringComparison.Ordinal)) return Forbid();
        await _kc.DeleteIdentityProviderAsync(t.Value.OrgId, alias, ct);
        return NoContent();
    }

    [HttpGet("mfa")]
    public async Task<IActionResult> GetMfa([FromServices] MfaPolicyService mfa, CancellationToken ct)
    {
        var t = await TenantAsync();
        if (t is null) return NotFound();
        var policy = MfaPolicyRules.Normalize(await _db.Tenants.Where(x => x.Slug == t.Value.Slug).Select(x => x.MfaPolicy).FirstOrDefaultAsync(ct));
        var ids = await _kc.ListOrganizationMemberIdsAsync(t.Value.OrgId, ct);
        var rows = await mfa.MembersAsync(t.Value.OrgId, policy, ct);
        // Passkey (WebAuthn) de ikinci adim sayilir; "Etkin" = TOTP ya da passkey.
        var withOtp = rows.Count(r => r.HasOtp || r.HasPasskey);
        var pending = rows.Count(r => !r.HasOtp && !r.HasPasskey && r.PendingSetup);
        return Ok(new
        {
            policy,
            privilegedRoles = MfaPolicyRules.PrivilegedRoles,
            platformAdminRequired = MfaPolicyService.PlatformAdminRequired,
            members = ids.Count, withOtp, pendingSetup = pending, without = ids.Count - withOtp - pending,
            requiredWithout = rows.Count(r => r.Required && !r.HasOtp && !r.HasPasskey),
            users = rows.Select(r => new
            {
                userId = r.UserId, username = r.Username, hasOtp = r.HasOtp, hasPasskey = r.HasPasskey,
                pendingSetup = r.PendingSetup && !r.HasOtp && !r.HasPasskey, privileged = r.Privileged, required = r.Required,
            }),
        });
    }

    public record MfaPolicyInput(string Policy);

    /// <summary>
    /// Guvenlik dalgasi 2A: iki adimli dogrulama politikasi (off | privileged | all). Hemen
    /// uygulanir ve tenant-service duzenli olarak yeniden uygular (sonradan role atananlar,
    /// dogrulayicisini silenler).
    /// </summary>
    [HttpPut("mfa/policy")]
    public async Task<IActionResult> SetMfaPolicy(MfaPolicyInput body, [FromServices] MfaPolicyService mfa, CancellationToken ct)
    {
        var t = await TenantAsync();
        if (t is null) return NotFound();
        if (!MfaPolicyRules.IsValid(body.Policy))
            return BadRequest(new { message = "Politika off, privileged ya da all olmalı." });
        var tenant = await _db.Tenants.FirstAsync(x => x.Slug == t.Value.Slug, ct);
        tenant.MfaPolicy = body.Policy == MfaPolicyRules.Off ? null : body.Policy;
        await _db.SaveChangesAsync(ct);
        var required = await mfa.ApplyTenantAsync(t.Value.Slug, ct);
        return Ok(new { policy = MfaPolicyRules.Normalize(tenant.MfaPolicy), required });
    }

    /// <summary>
    /// Ikinci adimi (TOTP ya da passkey) olmayan tum sirket kullanicilarina bir sonraki giriste
    /// kurulum zorunlulugu ekler (tek seferlik; kalici kural icin mfa/policy "all").
    /// </summary>
    [HttpPost("mfa/enforce")]
    public async Task<IActionResult> EnforceMfa(CancellationToken ct)
    {
        var t = await TenantAsync();
        if (t is null) return NotFound();
        var ids = await _kc.ListOrganizationMemberIdsAsync(t.Value.OrgId, ct);
        var changed = 0;
        foreach (var id in ids)
        {
            var st = await _kc.GetUserMfaStateAsync(id, ct);
            if (st is null || !MfaPolicyRules.NeedsSetup(true, st.CredentialTypes, st.RequiredActions)) continue;
            await _kc.RequireOtpSetupAsync(id, ct);
            changed++;
        }
        return Ok(new { required = changed, members = ids.Count });
    }

    /* ------------------------------------------------- Güvenlik dalgası 2A: şüpheli giriş */

    /// <summary>Son 90 gunun supheli giris uyarilari (yeni ag, art arda hatali giris). IP tutulmaz.</summary>
    [HttpGet("login-alerts")]
    public async Task<IActionResult> LoginAlerts(CancellationToken ct)
    {
        var t = await TenantAsync();
        if (t is null) return NotFound();
        var since = DateTimeOffset.UtcNow.AddDays(-90);
        var rows = await _db.Database.SqlQuery<LoginAlertRow>($"""
            SELECT "Id", "Kind", "Username", "Count", "CreatedAt" FROM tenant_security_alerts
            WHERE "TenantSlug" = {t.Value.Slug} AND "CreatedAt" >= {since} ORDER BY "CreatedAt" DESC LIMIT 100
            """).ToListAsync(ct);
        return Ok(rows);
    }

    public sealed class LoginAlertRow
    {
        public Guid Id { get; set; }
        public string Kind { get; set; } = "";
        public string? Username { get; set; }
        public int Count { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }

    /* ------------------------------------------------------------ G22 IP kısıtı */

    private string? CallerIp => Request.Headers["X-Real-IP"].FirstOrDefault() ?? HttpContext.Connection.RemoteIpAddress?.ToString();

    [HttpGet("ip-allowlist")]
    public async Task<IActionResult> GetIpAllowlist(CancellationToken ct)
    {
        var t = await TenantAsync();
        if (t is null) return NotFound();
        var raw = await _db.Tenants.Where(x => x.Slug == t.Value.Slug).Select(x => x.IpAllowlist).FirstOrDefaultAsync(ct);
        return Ok(new { entries = TenantService.Tenancy.TenantStatusGate.ParseList(raw).Select(n => n.ToString()), yourIp = CallerIp });
    }

    public record IpAllowlistInput(List<string> Entries);

    /// <summary>
    /// Boş liste kısıtı kaldırır. Yöneticinin kendini dışarıda bırakmaması için şu anki
    /// adresi listede olmalıdır. Değişiklik servislerde en geç 30 sn içinde geçerli olur.
    /// </summary>
    [HttpPut("ip-allowlist")]
    public async Task<IActionResult> SetIpAllowlist(IpAllowlistInput body, CancellationToken ct)
    {
        var t = await TenantAsync();
        if (t is null) return NotFound();
        var entries = (body.Entries ?? new()).Select(e => e.Trim()).Where(e => e != "").Distinct().ToList();
        if (entries.Count > 50) return BadRequest(new { message = "En fazla 50 adres/aralık girilebilir." });
        var parsed = new List<System.Net.IPNetwork>();
        foreach (var e in entries)
        {
            var list = TenantService.Tenancy.TenantStatusGate.ParseList(e);
            if (list.Count != 1) return BadRequest(new { message = $"Geçersiz adres ya da aralık: {e}" });
            if (list[0].PrefixLength < (list[0].BaseAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 8 : 16))
                return BadRequest(new { message = $"Aralık çok geniş: {e}" });
            parsed.Add(list[0]);
        }
        if (parsed.Count > 0 && (CallerIp is null || !TenantService.Tenancy.TenantStatusGate.Allowed(parsed, CallerIp)))
            return BadRequest(new { message = $"Şu anki adresiniz ({CallerIp ?? "bilinmiyor"}) listede yok; kendinizi dışarıda bırakırsınız.", code = "self_lockout" });
        var tenant = await _db.Tenants.FirstAsync(x => x.Slug == t.Value.Slug, ct);
        tenant.IpAllowlist = parsed.Count == 0 ? null : string.Join(",", parsed.Select(n => n.ToString()));
        await _db.SaveChangesAsync(ct);
        return Ok(new { entries = parsed.Select(n => n.ToString()) });
    }

    /* ------------------------------------------------------------ G22 oturumlar */

    [HttpGet("sessions")]
    public async Task<IActionResult> Sessions(CancellationToken ct)
    {
        var t = await TenantAsync();
        if (t is null) return NotFound();
        var ids = await _kc.ListOrganizationMemberIdsAsync(t.Value.OrgId, ct);
        var rows = new List<object>();
        foreach (var id in ids.Take(500))
        {
            var sessions = await _kc.ListUserSessionsAsync(id, ct);
            if (sessions.Count == 0) continue;
            var (_, _, username) = await _kc.GetOtpStatusAsync(id, ct);
            rows.Add(new { userId = id, username, sessions = sessions.Select(s => new { s.Id, s.IpAddress, s.Start, s.LastAccess }) });
        }
        return Ok(rows);
    }

    /// <summary>Kullanıcının tüm oturumlarını kapatır (kayıp cihaz, ayrılan çalışan).</summary>
    [HttpPost("sessions/{userId}/logout")]
    public async Task<IActionResult> LogoutUser(string userId, CancellationToken ct)
    {
        var t = await TenantAsync();
        if (t is null) return NotFound();
        if (!await _kc.IsOrganizationMemberAsync(t.Value.OrgId, userId, ct)) return NotFound();
        await _kc.LogoutUserAsync(userId, ct);
        return NoContent();
    }

    /* ------------------------------------------------------------ G22 passkey */

    [HttpGet("passkeys")]
    public async Task<IActionResult> Passkeys(CancellationToken ct)
    {
        var t = await TenantAsync();
        if (t is null) return NotFound();
        var ids = await _kc.ListOrganizationMemberIdsAsync(t.Value.OrgId, ct);
        var with = 0;
        foreach (var id in ids.Take(500))
            if ((await _kc.GetCredentialTypesAsync(id, ct)).Any(x => x.StartsWith("webauthn", StringComparison.Ordinal))) with++;
        return Ok(new { members = ids.Count, withPasskey = with });
    }
}

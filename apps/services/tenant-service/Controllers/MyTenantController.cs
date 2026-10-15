using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TenantService.Data;
using TenantService.Models;
using TenantService.Security;
using TenantService.Services;

namespace TenantService.Controllers;

/// <summary>
/// Oturum acan kullanicinin kendi tenant bilgisi. Frontend acilista bunu
/// cagirip sirket adini ve calisan sayisini gosterir.
/// </summary>
[ApiController]
[Route("api/my-tenant")]
[Authorize]
public class MyTenantController : ControllerBase
{
    private readonly TenantDbContext _db;
    private readonly SmtpCredentialProtector _protector;
    private readonly LogoStorageService _logos;
    private readonly KeycloakAdminClient _keycloak;
    public MyTenantController(TenantDbContext db, SmtpCredentialProtector protector, LogoStorageService logos, KeycloakAdminClient keycloak)
    {
        _db = db;
        _protector = protector;
        _logos = logos;
        _keycloak = keycloak;
    }

    public record LocaleInput(string Language);

    /// <summary>
    /// Oturumdaki kullanıcının dilini Keycloak'ta saklar (giriş ekranı ve Keycloak
    /// e-postaları: davet, parola sıfırlama). Kiracı bağımsızdır; her kullanıcı kendi
    /// dilini değiştirebilir.
    /// </summary>
    [HttpPut("me/locale")]
    public async Task<IActionResult> SetMyLocale(LocaleInput body, CancellationToken ct)
    {
        if (body.Language is not ("tr" or "en")) return BadRequest(new { message = "Dil 'tr' ya da 'en' olmalı." });
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(userId)) return BadRequest(new { message = "Kullanıcı kimliği jetonda yok." });
        try
        {
            return await _keycloak.SetUserLocaleAsync(userId, body.Language, ct) ? NoContent() : NotFound();
        }
        catch (InvalidOperationException)
        {
            return StatusCode(502, new { message = "Keycloak'a ulaşılamadı; dil tercihi kaydedilemedi." });
        }
    }

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        // organization claim'i Keycloak tarafindan JWT'ye eklenir; degeri
        // tenant slug'idir. Claim yoksa kullanici hicbir tenant'a bagli degildir.
        var slug = TenantService.Security.OrganizationClaimParser.ParseSlug(User.FindFirst("organization")?.Value);

        // Platform yoneticisi kiraciya bagli degildir: Kiracilar ekraninda sectigi sirket X-HR360-Tenant
        // basligiyla gelir. Salt okunur ozet + etkin erisim izni (bitis zamani) doner; izin yoksa
        // platformAccess bos kalir. Kabuk bunu her sayfada ister (onceden 404 donuyordu).
        if (string.IsNullOrWhiteSpace(slug) && User.IsInRole("platform-admin"))
            return await PlatformTenantAsync();

        if (string.IsNullOrWhiteSpace(slug))
            return NotFound(new { message = "Kullanıcı bir şirkete bağlı değil" });

        // Claim dizi olarak gelebilir: ["acme"] -> acme
        slug = slug.Trim('[', ']', '"', ' ');

        var tenant = await _db.Tenants
            .Where(t => t.Slug == slug)
            .Select(t => new
            {
                t.Id, t.Name, t.Slug, t.Status, t.CreatedAt,
                t.LogoUrl, t.PrimaryColorHex,
                // SMTP sifresi ASLA donmez - sadece "ayarlanmis mi" bilgisi.
                HasCustomSmtp = t.SmtpHost != null,
                t.SmtpFromAddress, t.SmtpFromName,
            })
            .FirstOrDefaultAsync();

        if (tenant is null)
            return NotFound(new { message = $"'{slug}' icin tenant kaydi bulunamadi" });

        // Calisan sayisi paylasilan veritabanindan dogrudan sayilir.
        var employeeCount = await _db.Database
            .SqlQuery<int>($@"SELECT COUNT(*)::int AS ""Value"" FROM employee_employees
                               WHERE ""TenantSlug"" = {slug}")
            .FirstAsync();

        return Ok(new
        {
            tenant.Id, tenant.Name, tenant.Slug, tenant.Status, tenant.CreatedAt,
            LogoUrl = LogoStorageService.ToPublicUrl(tenant.LogoUrl), tenant.PrimaryColorHex,
            tenant.HasCustomSmtp, tenant.SmtpFromAddress, tenant.SmtpFromName,
            EmployeeCount = employeeCount,
        });
    }

    private async Task<IActionResult> PlatformTenantAsync()
    {
        var target = PlatformAccessRules.TargetTenant(Request.Headers["X-HR360-Tenant"].FirstOrDefault());
        if (target is null) return NotFound(new { message = "Kullanıcı bir şirkete bağlı değil" });
        var t = await _db.Tenants.AsNoTracking().Where(x => x.Slug == target)
            .Select(x => new { x.Id, x.Name, x.Slug, x.Status, x.CreatedAt, x.LogoUrl, x.PrimaryColorHex })
            .FirstOrDefaultAsync();
        if (t is null) return NotFound(new { message = "Şirket bulunamadı" });
        var sub = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;
        var now = DateTimeOffset.UtcNow;
        var grant = sub is null ? null : await _db.PlatformAccessGrants.AsNoTracking()
            .Where(g => g.TenantSlug == t.Slug && g.GrantedToUserId == sub && g.RevokedAt == null && g.ExpiresAt > now)
            .OrderByDescending(g => g.ExpiresAt).Select(g => new { grantId = g.Id, g.ExpiresAt }).FirstOrDefaultAsync();
        return Ok(new
        {
            t.Id, t.Name, t.Slug, t.Status, t.CreatedAt,
            LogoUrl = LogoStorageService.ToPublicUrl(t.LogoUrl), t.PrimaryColorHex,
            ReadOnly = true,
            PlatformAccess = grant,
        });
    }

    /// <summary>
    /// Beyaz etiketleme ayarlari (sirket adi degisikligi haric - o ayri,
    /// dogrulama gerektirmeyen bir alan).
    /// LogoUrl buradan degil, ayri bir dosya yukleme ucundan set edilir.
    /// </summary>
    public record UpdateBrandingRequest(
        string? SmtpHost, int? SmtpPort, string? SmtpUser, string? SmtpPassword,
        string? SmtpFromAddress, string? SmtpFromName, string? PrimaryColorHex);

    private static readonly System.Text.RegularExpressions.Regex HexColorPattern =
        new(@"^#[0-9A-Fa-f]{6}$");

    private static readonly HashSet<int> AllowedSmtpPorts = new() { 25, 465, 587, 2525 };

    /// <summary>
    /// GUVENLIK: Ozel SMTP sunucusu/portu hic dogrulanmiyordu. notification-service bu
    /// adrese baglandigi icin bir kiraci yoneticisi ic agdaki servisleri (postgres,
    /// keycloak, minio...) hedef gosterip port taramasi yapabiliyordu (SSRF). Yalnizca
    /// standart SMTP portlari ve genel (internete acik) adresler kabul edilir.
    /// </summary>
    private static async Task<string?> ValidateSmtpTargetAsync(string host, int port)
    {
        if (!AllowedSmtpPorts.Contains(port))
            return "SMTP portu 25, 465, 587 ya da 2525 olmalı";
        if (host.Length > 253 || !System.Text.RegularExpressions.Regex.IsMatch(host, @"^[A-Za-z0-9.-]+$") || !host.Contains('.'))
            return "SMTP sunucusu tam bir alan adı olmalı (örn. smtp.sirket.com)";
        System.Net.IPAddress[] addresses;
        try { addresses = await System.Net.Dns.GetHostAddressesAsync(host); }
        catch (Exception) { return "SMTP sunucusunun adı çözümlenemedi"; }
        if (addresses.Length == 0) return "SMTP sunucusunun adı çözümlenemedi";
        foreach (var ip in addresses)
        {
            var a = ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
            if (System.Net.IPAddress.IsLoopback(a) || a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6UniqueLocal)
                return "SMTP sunucusu iç ağ adresine işaret edemez";
            if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                var b = a.GetAddressBytes();
                var isPrivate = b[0] == 10 || b[0] == 127 || b[0] == 0
                    || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                    || (b[0] == 192 && b[1] == 168)
                    || (b[0] == 169 && b[1] == 254)
                    || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
                if (isPrivate) return "SMTP sunucusu iç ağ adresine işaret edemez";
            }
        }
        return null;
    }

    [HttpPut("branding")]
    [Authorize(Policy = "RequireTenantAdmin")]
    public async Task<IActionResult> UpdateBranding([FromBody] UpdateBrandingRequest request)
    {
        var slug = TenantService.Security.OrganizationClaimParser.ParseSlug(User.FindFirst("organization")?.Value);
        if (string.IsNullOrWhiteSpace(slug))
            return NotFound(new { message = "Kullanıcı bir şirkete bağlı değil" });

        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Slug == slug);
        if (tenant is null)
            return NotFound(new { message = $"'{slug}' icin tenant kaydi bulunamadi" });

        // Kismi guncelleme: PrimaryColorHex gonderilmemisse mevcut ayar
        // korunur. Bos string ("") gonderilirse renk KALDIRILIR
        // (platformun varsayilan zumrut rengine donulur).
        if (request.PrimaryColorHex is not null)
        {
            if (request.PrimaryColorHex.Length == 0)
            {
                tenant.PrimaryColorHex = null;
            }
            else if (!HexColorPattern.IsMatch(request.PrimaryColorHex))
            {
                return BadRequest(new { message = "Renk '#c0392b' formatinda 6 haneli hex olmali" });
            }
            else
            {
                tenant.PrimaryColorHex = request.PrimaryColorHex;
            }
        }

        // Kismi guncelleme: SmtpHost gonderilmemisse (null) mevcut ayar
        // korunur. Bos string ("") gonderilirse ozel SMTP KALDIRILIR
        // (platformun varsayilan gonderim borusuna donulur).
        if (request.SmtpHost is not null)
        {
            if (request.SmtpHost.Length == 0)
            {
                tenant.SmtpHost = null;
                tenant.SmtpPort = null;
                tenant.SmtpUser = null;
                tenant.SmtpPasswordEncrypted = null;
                tenant.SmtpFromAddress = null;
                tenant.SmtpFromName = null;
            }
            else
            {
                if (await ValidateSmtpTargetAsync(request.SmtpHost.Trim(), request.SmtpPort ?? 587) is { } smtpError)
                    return BadRequest(new { message = smtpError });
                tenant.SmtpHost = request.SmtpHost.Trim();
                tenant.SmtpPort = request.SmtpPort ?? 587;
                tenant.SmtpUser = request.SmtpUser;
                if (!string.IsNullOrEmpty(request.SmtpPassword))
                    tenant.SmtpPasswordEncrypted = _protector.Encrypt(request.SmtpPassword);
                tenant.SmtpFromAddress = request.SmtpFromAddress;
                tenant.SmtpFromName = request.SmtpFromName;
            }
        }

        await _db.SaveChangesAsync();
        return Ok(new
        {
            message = "Marka ayarları güncellendi",
            hasCustomSmtp = tenant.SmtpHost != null,
            primaryColorHex = tenant.PrimaryColorHex,
        });
    }

    /// <summary>
    /// Dalga 12 (madde 91): kiracının özel SMTP sunucusuna bağlantı testi — bağlanır, TLS anlaşması ve
    /// (kullanıcı adı varsa) kimlik doğrulama yapar, e-posta GÖNDERMEZ. Ayar kaydedilirken uygulanan SSRF
    /// denetimi burada da yinelenir (DNS sonradan değişmiş olabilir). Parola yanıtta ve günlükte yer almaz.
    /// scripts/integration-check.sh kullanır. Özel SMTP yoksa {configured:false} (platform varsayılanı kullanılır).
    /// </summary>
    [HttpPost("smtp-test")]
    [Authorize(Policy = "RequireTenantAdmin")]
    public async Task<IActionResult> SmtpTest(CancellationToken ct)
    {
        var slug = TenantService.Security.OrganizationClaimParser.ParseSlug(User.FindFirst("organization")?.Value);
        if (string.IsNullOrWhiteSpace(slug))
            return NotFound(new { message = "Kullanıcı bir şirkete bağlı değil" });
        var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Slug == slug, ct);
        if (tenant is null) return NotFound(new { message = "Şirket bulunamadı" });
        if (string.IsNullOrEmpty(tenant.SmtpHost)) return Ok(new { configured = false, ok = (bool?)null, message = "Özel SMTP sunucusu yok; platform varsayılanı kullanılır." });
        var port = tenant.SmtpPort ?? 587;
        if (await ValidateSmtpTargetAsync(tenant.SmtpHost, port) is { } targetError)
            return Ok(new { configured = true, ok = false, message = targetError });
        string? error = null;
        try
        {
            using var client = new MailKit.Net.Smtp.SmtpClient { Timeout = 15000 };
            await client.ConnectAsync(tenant.SmtpHost, port, MailKit.Security.SecureSocketOptions.Auto, ct);
            if (!string.IsNullOrEmpty(tenant.SmtpUser) && !string.IsNullOrEmpty(tenant.SmtpPasswordEncrypted))
                await client.AuthenticateAsync(tenant.SmtpUser, _protector.Decrypt(tenant.SmtpPasswordEncrypted), ct);
            await client.DisconnectAsync(true, ct);
        }
        catch (MailKit.Security.AuthenticationException) { error = "SMTP kimlik doğrulaması başarısız (kullanıcı adı ya da parola)."; }
        catch (Exception ex) when (ex is MailKit.ProtocolException or MailKit.Net.Smtp.SmtpCommandException or System.Net.Sockets.SocketException
                                       or IOException or TimeoutException or System.Security.Authentication.AuthenticationException)
        { error = "SMTP sunucusuna bağlanılamadı: " + ex.Message; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { error = "SMTP sunucusu zamanında yanıt vermedi."; }
        return Ok(new { configured = true, ok = error is null, message = error ?? "SMTP bağlantısı ve kimlik doğrulama başarılı." });
    }

    public record RenameRequest(string Name);

    [HttpPut("name")]
    [Authorize(Policy = "RequireTenantAdmin")]
    public async Task<IActionResult> Rename([FromBody] RenameRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 200)
            return BadRequest(new { message = "Şirket adı 1-200 karakter olmalı" });

        var slug = TenantService.Security.OrganizationClaimParser.ParseSlug(User.FindFirst("organization")?.Value);
        if (string.IsNullOrWhiteSpace(slug))
            return NotFound(new { message = "Kullanıcı bir şirkete bağlı değil" });

        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Slug == slug);
        if (tenant is null)
            return NotFound(new { message = $"'{slug}' icin tenant kaydi bulunamadi" });

        tenant.Name = request.Name.Trim();
        await _db.SaveChangesAsync();
        return Ok(new { message = "Şirket adı güncellendi", name = tenant.Name });
    }

    /// <summary>
    /// Logo yukler. multipart/form-data ile tek dosya
    /// bekler - form alan adi "file". Basariliysa yeni URL'i doner ve
    /// Tenant.LogoUrl'i gunceller.
    /// </summary>
    [HttpPost("logo")]
    [Authorize(Policy = "RequireTenantAdmin")]
    [RequestSizeLimit(2 * 1024 * 1024 + 1024)] // 2MB dosya + kucuk form-data payi
    public async Task<IActionResult> UploadLogo(IFormFile? file, CancellationToken ct)
    {
        var slug = TenantService.Security.OrganizationClaimParser.ParseSlug(User.FindFirst("organization")?.Value);
        if (string.IsNullOrWhiteSpace(slug))
            return NotFound(new { message = "Kullanıcı bir şirkete bağlı değil" });

        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Slug == slug);
        if (tenant is null)
            return NotFound(new { message = $"'{slug}' icin tenant kaydi bulunamadi" });

        if (file is null || file.Length == 0)
            return BadRequest(new { message = "Dosya bulunamadı" });

        await using var stream = file.OpenReadStream();
        var result = await _logos.UploadAsync(tenant.Id, file.ContentType, file.FileName, stream, file.Length, ct);
        if (!result.Success)
            return BadRequest(new { message = result.Error });

        tenant.LogoUrl = result.Url;
        await _db.SaveChangesAsync();
        return Ok(new { message = "Logo yüklendi", logoUrl = LogoStorageService.ToPublicUrl(tenant.LogoUrl) });
    }

    [HttpDelete("logo")]
    [Authorize(Policy = "RequireTenantAdmin")]
    public async Task<IActionResult> DeleteLogo(CancellationToken ct)
    {
        var slug = TenantService.Security.OrganizationClaimParser.ParseSlug(User.FindFirst("organization")?.Value);
        if (string.IsNullOrWhiteSpace(slug))
            return NotFound(new { message = "Kullanıcı bir şirkete bağlı değil" });

        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Slug == slug);
        if (tenant is null)
            return NotFound(new { message = $"'{slug}' icin tenant kaydi bulunamadi" });

        await _logos.DeleteAsync(tenant.Id, ct);
        tenant.LogoUrl = null;
        await _db.SaveChangesAsync();
        return Ok(new { message = "Logo kaldırıldı" });
    }

    /* ------------------------------------------------------------ G22 kendi oturumlarım */

    private string? MyUserId => User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;

    [HttpGet("me/sessions")]
    public async Task<IActionResult> MySessions(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(MyUserId)) return BadRequest(new { message = "Kullanıcı kimliği jetonda yok." });
        var current = User.FindFirst("sid")?.Value ?? User.FindFirst("session_state")?.Value;
        var sessions = await _keycloak.ListUserSessionsAsync(MyUserId, ct);
        var types = await _keycloak.GetCredentialTypesAsync(MyUserId, ct);
        return Ok(new
        {
            sessions = sessions.OrderByDescending(s => s.LastAccess).Select(s => new { s.Id, s.IpAddress, s.Start, s.LastAccess, current = s.Id == current }),
            hasOtp = types.Contains("otp"),
            hasPasskey = types.Any(x => x.StartsWith("webauthn", StringComparison.Ordinal)),
        });
    }

    [HttpDelete("me/sessions/{sessionId}")]
    public async Task<IActionResult> EndMySession(string sessionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(MyUserId)) return BadRequest();
        // Yalnızca kendi oturumu kapatılabilir.
        var mine = await _keycloak.ListUserSessionsAsync(MyUserId, ct);
        if (mine.All(s => s.Id != sessionId)) return NotFound();
        await _keycloak.DeleteSessionAsync(sessionId, ct);
        return NoContent();
    }

    /// <summary>Bu oturum dışındaki tüm oturumları kapatır.</summary>
    [HttpPost("me/sessions/logout-others")]
    public async Task<IActionResult> EndOtherSessions(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(MyUserId)) return BadRequest();
        var current = User.FindFirst("sid")?.Value ?? User.FindFirst("session_state")?.Value;
        var n = 0;
        foreach (var s in await _keycloak.ListUserSessionsAsync(MyUserId, ct))
        {
            if (s.Id == current) continue;
            await _keycloak.DeleteSessionAsync(s.Id, ct);
            n++;
        }
        return Ok(new { closed = n });
    }
}

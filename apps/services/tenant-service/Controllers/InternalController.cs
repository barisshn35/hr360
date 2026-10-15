using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TenantService.Data;

namespace TenantService.Controllers;

/// <summary>
/// Servisler-arasi (service-to-service) uclar - "/api/" altinda DEGIL bilerek:
/// deploy/nginx/nginx.conf'ta "/internal/" icin hicbir location tanimli
/// degil, dolayisiyla bu uclar gateway (internet) uzerinden ERISILEMEZ,
/// sadece Docker'in ic agindaki diger container'lar TENANT_SERVICE_URL ile
/// dogrudan cagirabilir. "/api/registration/branding/{slug}" (herkese acik
/// logo/renk) ile KARISTIRILMASIN - o bilincli olarak internetten de
/// erisilebilir, bu controller'daki uclar ise SADECE ic ag icindir ve bu
/// yuzden SMTP sifresi gibi daha hassas alanlari (sifreli halde) donebilir.
/// </summary>
[ApiController]
[Route("internal")]
[AllowAnonymous]
public class InternalController : ControllerBase
{
    private readonly TenantDbContext _db;
    private readonly TenantService.Services.KeycloakAdminClient _keycloak;
    private readonly ILogger<InternalController> _log;

    public InternalController(TenantDbContext db, TenantService.Services.KeycloakAdminClient keycloak, ILogger<InternalController> log)
    {
        _db = db;
        _keycloak = keycloak;
        _log = log;
    }

    /// <summary>X-Internal-Token (INTERNAL_SERVICE_TOKEN) sabit zamanli karsilastirma; tanimsizsa uc kapali.</summary>
    private bool InternalTokenValid() =>
        Security.InternalServiceToken.Matches(Request.Headers[Security.InternalServiceToken.Header].FirstOrDefault());

    /// <summary>
    /// Arayüzden girilen platform SMTP'si (Sistem ayarları). notification-service e-posta işçisi kullanır;
    /// parola şifreli döner. Arayüzde özel sunucu yoksa 204: .env'deki (kurulum) ayar geçerli.
    /// </summary>
    [HttpGet("platform-smtp")]
    public async Task<IActionResult> PlatformSmtp([FromServices] TenantService.Services.PlatformSettingsService settings, CancellationToken ct)
    {
        if (!InternalTokenValid()) return NotFound();
        var row = await settings.GetRowAsync(ct);
        var smtp = TenantService.Services.PlatformSettingsService.FromRow(row);
        if (smtp.Mode != "custom") return Ok(new { mode = smtp.Mode });
        return Ok(new
        {
            mode = smtp.Mode, smtpHost = smtp.Host, smtpPort = smtp.Port, smtpUser = smtp.User,
            smtpPasswordEncrypted = smtp.PasswordEncrypted, smtpFromAddress = smtp.FromAddress, smtpFromName = smtp.FromName,
        });
    }

    public record StorageDeleteRequest(string TenantSlug, List<string> Keys);

    /// <summary>
    /// Dalga 10 (KVKK imha doğrulaması): governance'in kuyruğa aldığı dosya anahtarlarını nesne deposundan
    /// siler ve yokluğunu doğrular (ObjectStorageEraser). Yalnızca kişisel dosya kovalarına dokunur.
    /// Anahtarlar günlüğe yazılmaz (kişi adı içerebilir).
    /// </summary>
    [HttpPost("storage/delete")]
    public async Task<IActionResult> DeleteStorage([FromBody] StorageDeleteRequest request, [FromServices] TenantService.Services.ObjectStorageEraser eraser, CancellationToken ct)
    {
        if (!InternalTokenValid()) return NotFound();
        if (string.IsNullOrWhiteSpace(request.TenantSlug) || request.Keys is null || request.Keys.Count == 0)
            return BadRequest(new { message = "Kiracı ve en az bir anahtar zorunlu" });
        if (request.Keys.Count > 500) return BadRequest(new { message = "Tek istekte en fazla 500 anahtar" });
        var results = await eraser.DeleteAndVerifyAsync(request.Keys, ct);
        _log.LogInformation("KVKK dosya imhasi: kiraci {Tenant}, {Count} anahtar ({Summary})", request.TenantSlug.ToLowerInvariant(), results.Count,
            string.Join(", ", results.GroupBy(r => r.Status).Select(g => $"{g.Key}={g.Count()}")));
        return Ok(results);
    }

    public record DisableUserRequest(string TenantSlug, string KeycloakUserId, string? Reason);

    /// <summary>
    /// Dalga 5c (G15): isten ayrilan calisanin hesabini kapatir ve TUM oturumlarini sonlandirir
    /// (engagement-service offboarding tamamlanirken cagirir). Hedef, cagiran kiracinin Keycloak
    /// organizasyonunun uyesi olmali; degilse 404 (baska kiracinin hesabi kapatilamaz). Sirket
    /// yoneticisi (kiracinin kurucu yonetici hesabi) ve platform yoneticileri bu yolla kapatilamaz.
    ///
    /// NOT: Kapatma, kullanicinin TAM temsiliyle yapilir (SuspendUserForTenantAsync) - yalnizca
    /// {enabled:false} gondermek (SetUserEnabledAsync) Keycloak 25'te e-posta/ad niteliklerini siliyor.
    /// </summary>
    [HttpPost("users/disable")]
    public async Task<IActionResult> DisableUser([FromBody] DisableUserRequest request, CancellationToken ct)
    {
        if (!InternalTokenValid()) return NotFound();
        if (string.IsNullOrWhiteSpace(request.TenantSlug) || string.IsNullOrWhiteSpace(request.KeycloakUserId))
            return BadRequest(new { message = "Kiracı ve kullanıcı kimliği zorunlu" });
        if (!Guid.TryParse(request.KeycloakUserId, out _))
            return BadRequest(new { message = "Geçersiz kullanıcı kimliği" });

        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Slug == request.TenantSlug.ToLowerInvariant(), ct);
        if (tenant?.KeycloakOrgId is null) return NotFound(new { message = "Şirket bulunamadı" });
        if (!await _keycloak.IsOrganizationMemberAsync(tenant.KeycloakOrgId, request.KeycloakUserId, ct))
            return NotFound(new { message = "Kullanıcı bu şirkette bulunamadı" });
        if (string.Equals(tenant.AdminUserId, request.KeycloakUserId, StringComparison.OrdinalIgnoreCase))
            return Conflict(new { message = "Şirketin kurucu yönetici hesabı bu yolla kapatılamaz" });
        var roles = await _keycloak.GetUserRealmRolesAsync(request.KeycloakUserId, ct);
        if (roles.Contains("platform-admin"))
            return Conflict(new { message = "Platform yöneticisi hesabı bu yolla kapatılamaz" });

        var disabledNow = await _keycloak.SuspendUserForTenantAsync(request.KeycloakUserId, ct);
        _log.LogInformation("Ayrilan calisan hesabi kapatildi: kiraci {Tenant}, kullanici {User}, yeni kapatildi={Now}",
            tenant.Slug, request.KeycloakUserId, disabledNow);
        return Ok(new { disabled = true, alreadyDisabled = !disabledNow, sessionsEnded = true });
    }

    /// <summary>
    /// Kiracinin kendi SMTP sunucusu ayarlanmissa (Ayarlar > Marka)
    /// dondurur - notification-service bunu EmailSenderWorker'da
    /// kullanir. Sifre SIFRELI (SmtpPasswordEncrypted) doner; cozme islemi
    /// notification-service tarafinda, ayni TENANT_SECRET_KEY paylasilarak
    /// yapilir (bkz. SmtpCredentialProtector, iki serviste de birebir ayni).
    /// Kiracinin ozel SMTP'si yoksa 404 doner - cagiran platform
    /// varsayilanina duser.
    /// </summary>
    [HttpGet("smtp-config/{slug}")]
    public async Task<IActionResult> SmtpConfig(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return BadRequest(new { message = "Slug bos olamaz" });

        var tenant = await _db.Tenants
            .Where(t => t.Slug == slug.ToLowerInvariant() && t.SmtpHost != null)
            .Select(t => new
            {
                t.SmtpHost,
                t.SmtpPort,
                t.SmtpUser,
                t.SmtpPasswordEncrypted,
                t.SmtpFromAddress,
                t.SmtpFromName,
            })
            .FirstOrDefaultAsync();

        return tenant is null ? NotFound() : Ok(tenant);
    }
}

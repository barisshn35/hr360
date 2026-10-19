using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TenantService.Data;
using TenantService.Models;
using TenantService.Services;

namespace TenantService.Controllers;

/// <summary>
/// Platform yonetimi. Yalnizca platform-admin - tum tenant'lari gorur.
/// Tenant'larin kendi verisi diger servislerde, izole halde durur.
/// </summary>
[ApiController]
[Route("api/tenants")]
[Authorize(Policy = "RequirePlatformAdmin")]
public class TenantsController : ControllerBase
{
    private readonly TenantDbContext _db;
    private readonly KeycloakAdminClient _keycloak;
    private readonly ILogger<TenantsController> _logger;

    public TenantsController(TenantDbContext db, KeycloakAdminClient keycloak, ILogger<TenantsController> logger)
    {
        _db = db;
        _keycloak = keycloak;
        _logger = logger;
    }

    /// <summary>
    /// Platform yoneticisi yeni kiraci (sirket) olusturur: herkese acik kayitla ayni akis
    /// (Keycloak organizasyonu + yonetici kullanicisi + parola belirleme e-postasi). Herkese acik kayit kapaliyken (PUBLIC_SIGNUP_ENABLED=false) ve demo
    /// verisi olmayan uretim kurulumunda ilk sirket bu uctan olusturulur.
    /// </summary>
    [HttpPost]
    public Task<IActionResult> Create(
        [FromBody] RegisterCompanyRequest request,
        [FromServices] TenantProvisioningService provisioning, [FromServices] PasswordLinkService links, CancellationToken ct)
        => HR360.Platform.Modules.IsSingleTenant
            ? Task.FromResult<IActionResult>(SingleTenantConflict("Tek şirket kipinde yeni şirket oluşturulamaz. Çok şirketli kipe Bakım ekranından geçilebilir."))
            : RegistrationController.CreateTenantAsync(this, _db, provisioning, _logger, request, links, LinkDisplay.Show,
                PasswordLinkService.ActorOf(User), ct);

    /// <summary>Tek şirket kipinde izin verilmeyen işlem (409, code=single_tenant_mode).</summary>
    public static ObjectResult SingleTenantConflict(string message) =>
        new ConflictObjectResult(new { code = "single_tenant_mode", message });

    /// <summary>
    /// Şirket yöneticisinin parola linkini yeniden üretir (ilk kurulumda link kaybolduysa). Link yanıtta bir
    /// kez döner; açık linkler iptal olur. Yönetici parolasını zaten belirlediyse sıfırlama linki üretilir.
    /// </summary>
    [HttpPost("{id:guid}/admin-link")]
    public async Task<IActionResult> AdminLink(Guid id, [FromServices] PasswordLinkService links,
        [FromServices] KeycloakAdminClient kc, CancellationToken ct)
    {
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null) return NotFound(new { message = "Şirket bulunamadı" });
        if (string.IsNullOrEmpty(tenant.AdminUserId)) return Conflict(new { message = "Şirketin yönetici hesabı yok" });
        var user = await kc.GetUserBasicsAsync(tenant.AdminUserId, ct);
        if (user is null) return NotFound(new { message = "Yönetici hesabı bulunamadı" });
        if (!user.Enabled) return Conflict(new { message = "Yönetici hesabı kapalı" });
        var purpose = (await kc.GetCredentialTypesAsync(tenant.AdminUserId, ct)).Contains("password")
            ? PasswordInvitePurposes.Reset : PasswordInvitePurposes.Setup;
        var issued = await links.IssueAsync(new PasswordLinkService.IssueRequest(tenant, tenant.AdminUserId,
            user.Email ?? tenant.AdminEmail, tenant.AdminFullName, purpose, LinkDisplay.Show, PasswordLinkService.ActorOf(User), user.Locale), ct);
        return Ok(PasswordLinkView.From(issued));
    }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] TenantStatus? status)
    {
        var q = _db.Tenants.AsQueryable();
        if (status.HasValue) q = q.Where(t => t.Status == status.Value);
        var tenants = await q.OrderByDescending(t => t.CreatedAt).ToListAsync();

        // Calisan sayilari tum kiracilar icin tek gruplu sorguyla (N+1 olmadan)
        // paylasilan veritabanindaki employee_employees'ten sayilir.
        var counts = await _db.Database
            .SqlQuery<TenantEmployeeCount>(
                $@"SELECT ""TenantSlug"" AS ""Slug"", COUNT(*)::int AS ""Count""
                   FROM employee_employees GROUP BY ""TenantSlug""")
            .ToDictionaryAsync(c => c.Slug, c => c.Count);

        // NOT: Onceki halinde bu uc ham Tenant entity'sini donuyordu - bu,
        // frontend'in ihtiyaci olmayan SmtpPasswordEncrypted/SmtpHost/
        // SmtpUser/AdminUserId gibi ic alanlari da (parola SIFRELI olsa
        // da) platform-admin API yanitina sizdiriyordu. MyTenantController
        // ZATEN bu alanlari gizleyip sadece HasCustomSmtp donuyordu - ayni
        // guvenli deseni burada da uyguluyoruz.
        return Ok(tenants.Select(t => Safe(t, counts.GetValueOrDefault(t.Slug, 0))));
    }

    /// <summary>
    /// Platform paneline donen guvenli kiraci sekli: SMTP parolasi/kullanicisi,
    /// Keycloak kimlikleri ve askiya alma kaydi gibi ic alanlar disari cikmaz.
    /// Tum uclar (liste, detay, askiya alma) bunu kullanir.
    /// </summary>
    private static Dictionary<string, object?> Safe(Tenant t, int? employeeCount = null)
    {
        var d = new Dictionary<string, object?>
        {
            ["id"] = t.Id, ["name"] = t.Name, ["slug"] = t.Slug, ["status"] = t.Status,
            ["createdAt"] = t.CreatedAt,
            ["emailDomain"] = t.EmailDomain, ["taxNumber"] = t.TaxNumber, ["adminEmail"] = t.AdminEmail,
            ["logoUrl"] = LogoStorageService.ToPublicUrl(t.LogoUrl), ["primaryColorHex"] = t.PrimaryColorHex,
            ["suspendedAt"] = t.SuspendedAt, ["suspensionReason"] = t.SuspendReason,
            ["hasCustomSmtp"] = t.SmtpHost != null,
            ["smtpFromAddress"] = t.SmtpFromAddress, ["smtpFromName"] = t.SmtpFromName,
        };
        if (employeeCount.HasValue) d["employeeCount"] = employeeCount.Value;
        return d;
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == id);
        if (tenant is null) return NotFound();

        var logs = await _db.ProvisioningLogs
            .Where(l => l.TenantId == id)
            .OrderBy(l => l.OccurredAt)
            .ToListAsync();

        var employeeCount = await _db.Database
            .SqlQuery<int>($@"SELECT COUNT(*)::int AS ""Value"" FROM employee_employees
                               WHERE ""TenantSlug"" = {tenant.Slug}")
            .FirstAsync();

        // NOT: Onceki halinde bu uc { tenant: {...}, employeeCount,
        // provisioningLog } seklinde IC ICE (nested) donuyordu - ama
        // frontend'in TenantDetail tipi (api/tenant.ts) Tenant'i DUZ
        // (flat) genisletiyor ve TenantsPage.tsx alanlari dogrudan
        // "detail.data.name", "detail.data.status", "detail.data.adminEmail"
        // vb. OKUYORDU (detail.data.tenant.xxx DEGIL). Sonuc: platform-admin
        // panelinde bir kiraciya tiklayip detay actiginda "Kiraci detayi"
        // basligindan sonra TUM alanlar (durum, plan, yonetici e-postasi,
        // askiya alma bilgisi...) undefined/bos gorunuyordu (hardcore test
        // sirasinda bulundu, 2. tur). GetAll ile ayni guvenli/duz sekle
        // (SmtpPasswordEncrypted gibi ic alanlar olmadan) getiriliyor.
        var result = Safe(tenant, employeeCount);
        // Zaman cizelgesi web istemcisinin bekledigi sekilde (step/status/message/startedAt).
        // Onceden ham kayit (success/detail/occurredAt) donuyordu; panelde her adim
        // mesajsiz ve "bekliyor" olarak gorunuyordu.
        result["provisioningLog"] = logs.Select(l => new
        {
            l.Id,
            Step = l.Step.ToString(),
            Status = l.Success ? "Succeeded" : "Failed",
            Message = l.Detail,
            StartedAt = l.OccurredAt,
            CompletedAt = (DateTimeOffset?)null,
        }).ToList();
        return Ok(result);
    }

    private record TenantEmployeeCount(string Slug, int Count);

    /// <summary>Kiracinin Keycloak organizasyonundaki tum uyeler (+ kayitli yonetici).</summary>
    private async Task<(int Affected, int Failed)> ForEachTenantUserAsync(
        Tenant tenant, Func<string, Task<bool>> action, CancellationToken ct)
    {
        var ids = new HashSet<string>();
        if (!string.IsNullOrEmpty(tenant.KeycloakOrgId))
            ids.UnionWith(await _keycloak.ListOrganizationMemberIdsAsync(tenant.KeycloakOrgId, ct));
        if (!string.IsNullOrEmpty(tenant.AdminUserId)) ids.Add(tenant.AdminUserId);
        int affected = 0, failed = 0;
        foreach (var id in ids)
        {
            try { if (await action(id)) affected++; }
            catch (Exception ex)
            {
                failed++;
                _logger.LogError(ex, "Kiraci {Slug} kullanicisi {User} guncellenemedi", tenant.Slug, id);
            }
        }
        return (affected, failed);
    }

    /// <summary>
    /// Kiraciyi askiya alir: organizasyonun tum uyelerinin hesaplari kapatilir ve
    /// oturumlari sonlandirilir; servisler askidaki kiracinin isteklerini 403 ile reddeder.
    /// </summary>
    [HttpPost("{id}/suspend")]
    public async Task<IActionResult> Suspend(
        Guid id, [FromBody] SuspendRequest request, CancellationToken ct)
    {
        // Tek şirket kipinde tek şirketi askıya almak herkesi (yöneticisi dahil) dışarıda bırakır.
        if (HR360.Platform.Modules.IsSingleTenant)
            return SingleTenantConflict("Tek şirket kipinde şirket askıya alınamaz.");
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null) return NotFound();
        if (tenant.Status == TenantStatus.Suspended)
            return BadRequest(new { message = "Tenant zaten askıda" });

        tenant.Status = TenantStatus.Suspended;
        tenant.SuspendedAt = DateTimeOffset.UtcNow;
        tenant.SuspendReason = request.Reason;

        // GUVENLIK: Onceden yalnizca kiraci yoneticisinin hesabi kapatiliyordu; hicbir
        // servis kiraci durumunu kontrol etmedigi icin askidaki sirketin diger tum
        // kullanicilari calismaya devam ediyordu. Artik organizasyonun tum uyeleri
        // kapatilir ve oturumlari sonlandirilir. Mevcut erisim jetonlari en fazla
        // omurleri kadar (realm ayari, varsayilan 5 dk) gecerli kalir.
        var disabled = new List<string>();
        var (affected, failed) = await ForEachTenantUserAsync(tenant, async uid =>
        {
            var changed = await _keycloak.SuspendUserForTenantAsync(uid, ct);
            if (changed) disabled.Add(uid);
            return changed;
        }, ct);
        tenant.SuspendedUserIdsJson = System.Text.Json.JsonSerializer.Serialize(disabled);

        await _db.SaveChangesAsync(ct);
        return Ok(new { tenant = Safe(tenant), usersDisabled = affected, usersFailed = failed });
    }

    [HttpPost("{id}/reactivate")]
    public async Task<IActionResult> Reactivate(Guid id, CancellationToken ct)
    {
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null) return NotFound();

        if (tenant.Status != TenantStatus.Suspended)
            return BadRequest(new { message = "Tenant askıda değil" });

        tenant.Status = TenantStatus.Active;
        tenant.SuspendedAt = null;
        tenant.SuspendReason = null;

        // Yalnizca askiya alma nedeniyle kapatilan hesaplar geri acilir; baska bir
        // nedenle kapatilmis hesaplar kapali kalir.
        var toEnable = string.IsNullOrEmpty(tenant.SuspendedUserIdsJson)
            ? new List<string>()
            : System.Text.Json.JsonSerializer.Deserialize<List<string>>(tenant.SuspendedUserIdsJson) ?? new();
        int affected = 0, failed = 0;
        var remaining = new List<string>();
        foreach (var uid in toEnable)
        {
            try { if (await _keycloak.EnableUserAsync(uid, ct)) affected++; }
            catch (Exception ex)
            {
                failed++; remaining.Add(uid);
                _logger.LogError(ex, "Kiraci {Slug} kullanicisi {User} acilamadi", tenant.Slug, uid);
            }
        }
        // Acilamayanlar kayitta kalir; islem tekrar denenebilir.
        tenant.SuspendedUserIdsJson = remaining.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(remaining);

        await _db.SaveChangesAsync(ct);
        return Ok(new { tenant = Safe(tenant), usersEnabled = affected, usersFailed = failed });
    }
}

public record SuspendRequest(string? Reason);

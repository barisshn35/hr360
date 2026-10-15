using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TenantService.Data;
using TenantService.Models;
using TenantService.Security;
using TenantService.Services;

namespace TenantService.Controllers;

/// <summary>
/// Guvenlik dalgasi 2A: platform yoneticisinin kiraci verisine "cam kirma" (break-glass)
/// erisimi. Platform yoneticisi bir kiracinin verisini okumadan once gerekceli ve sureli
/// (en fazla 4 saat) izin acar; servislerin kiraci kapisi (Tenancy/PlatformAccessGate) izin
/// olmadan platform yoneticisinin kiraci verisi isteklerini 403 ile reddeder, izin varken
/// istekleri yalnizca o kiraciyla sinirlar ve her erisimi audit_log'a yazar.
///
/// Kiracinin sirket yoneticileri izin acildiginda bildirim alir; Guvenlik ekraninda etkin ve
/// gecmis izinleri gorur ve etkin izni erken kapatabilir.
///
/// Platform seviyesindeki uclar (kiraci listesi, faturalar, kayit) izin gerektirmez.
/// </summary>
[ApiController]
[Route("api/platform-access")]
[Authorize]
public class PlatformAccessController : ControllerBase
{
    private readonly TenantDbContext _db;
    private readonly SecurityNotifier _notifier;
    public PlatformAccessController(TenantDbContext db, SecurityNotifier notifier) { _db = db; _notifier = notifier; }

    private bool IsPlatformAdmin => User.IsInRole("platform-admin");
    private bool IsTenantAdmin => User.IsInRole("tenant-admin");
    private string? Sub => User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;
    private string? DisplayName => User.FindFirst("name")?.Value ?? User.FindFirst("preferred_username")?.Value;
    private string? CallerTenant => OrganizationClaimParser.ParseSlug(User.FindFirst("organization")?.Value);

    private static object Dto(PlatformAccessGrant g, DateTimeOffset now) => new
    {
        g.Id, g.TenantSlug, g.GrantedToUserId, g.GrantedToName, g.Reason, g.CreatedAt, g.ExpiresAt,
        g.RevokedAt, g.RevokedByName, active = g.IsActive(now),
    };

    public record GrantInput(string TenantSlug, string Reason, int Hours);

    /// <summary>Platform yoneticisi: kiraciya sureli erisim izni acar.</summary>
    [HttpPost("grants")]
    public async Task<IActionResult> Create(GrantInput body, CancellationToken ct)
    {
        if (!IsPlatformAdmin || Sub is null) return Forbid();
        var error = PlatformAccessRules.Validate(body.TenantSlug, body.Reason, body.Hours);
        if (error is not null) return BadRequest(new { message = error });
        var slug = body.TenantSlug.Trim();
        var tenant = await _db.Tenants.AsNoTracking().Where(t => t.Slug == slug).Select(t => new { t.Slug, t.Name }).FirstOrDefaultAsync(ct);
        if (tenant is null) return NotFound(new { message = "Şirket bulunamadı." });

        var now = DateTimeOffset.UtcNow;
        var grant = new PlatformAccessGrant
        {
            TenantSlug = tenant.Slug,
            GrantedToUserId = Sub,
            GrantedToName = DisplayName,
            Reason = body.Reason.Trim(),
            CreatedAt = now,
            ExpiresAt = now.AddHours(body.Hours),
        };
        _db.PlatformAccessGrants.Add(grant);
        await _db.SaveChangesAsync(ct);
        await _notifier.AuditAsync(tenant.Slug, "PlatformAccessGrant", grant.Id.ToString(), "PlatformAccessGranted",
            new { hours = body.Hours, expiresAt = grant.ExpiresAt, reason = grant.Reason }, Sub, DisplayName, ct);

        // Sirket yoneticilerine bildirim: calisan kaydi olanlara uygulama ici, calisan kaydi olmayan
        // yonetici hesaplarina (ornegin yalnizca yonetim icin acilmis hesap) e-posta.
        var admins = await _notifier.TenantUsersWithRolesAsync(tenant.Slug, new[] { "tenant-admin" }, ct);
        var recipients = await _notifier.EmployeesOfUsersAsync(tenant.Slug, admins, ct);
        const string subject = "Platform yöneticisi erişim izni açtı";
        var text = $"{DisplayName ?? "Platform yöneticisi"}, şirket verilerinize {grant.ExpiresAt.ToOffset(TimeSpan.FromHours(3)):dd.MM.yyyy HH:mm} saatine kadar erişim izni açtı. Gerekçe: {grant.Reason}. İzni Güvenlik ekranından kapatabilirsiniz.";
        await _notifier.NotifyAsync(tenant.Slug, recipients, subject, text, "security.platform_access", ct);
        var emails = await _notifier.EmailsOfUsersWithoutEmployeeAsync(tenant.Slug, admins, ct);
        await _notifier.NotifyEmailAsync(tenant.Slug, emails, subject, text, "security.platform_access", ct);
        return Ok(Dto(grant, now));
    }

    /// <summary>Platform yoneticisi: kendi etkin izinleri.</summary>
    [HttpGet("grants/mine")]
    public async Task<IActionResult> Mine(CancellationToken ct)
    {
        if (!IsPlatformAdmin || Sub is null) return Forbid();
        var now = DateTimeOffset.UtcNow;
        var rows = await _db.PlatformAccessGrants.AsNoTracking()
            .Where(g => g.GrantedToUserId == Sub && g.RevokedAt == null && g.ExpiresAt > now)
            .OrderByDescending(g => g.ExpiresAt).ToListAsync(ct);
        return Ok(rows.Select(g => Dto(g, now)));
    }

    /// <summary>Sirket yoneticisi: kendi sirketine acilmis etkin ve son 90 gunun izinleri.</summary>
    [HttpGet("grants")]
    public async Task<IActionResult> ForMyTenant(CancellationToken ct)
    {
        var slug = CallerTenant;
        if (!IsTenantAdmin || string.IsNullOrEmpty(slug)) return Forbid();
        var now = DateTimeOffset.UtcNow;
        var since = now.AddDays(-90);
        var rows = await _db.PlatformAccessGrants.AsNoTracking()
            .Where(g => g.TenantSlug == slug && (g.CreatedAt >= since || (g.RevokedAt == null && g.ExpiresAt > now)))
            .OrderByDescending(g => g.CreatedAt).Take(200).ToListAsync(ct);
        return Ok(rows.Select(g => Dto(g, now)));
    }

    /// <summary>
    /// Izni erken kapatir: izni acan platform yoneticisi ya da o sirketin sirket yoneticisi.
    /// Servislerdeki izin onbellegi en gec 15 sn icinde yenilenir.
    /// </summary>
    [HttpPost("grants/{id:guid}/revoke")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        var g = await _db.PlatformAccessGrants.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (g is null) return NotFound();
        var mayRevoke = (IsPlatformAdmin && g.GrantedToUserId == Sub) || (IsTenantAdmin && CallerTenant == g.TenantSlug);
        if (!mayRevoke) return NotFound();
        var now = DateTimeOffset.UtcNow;
        if (g.IsActive(now))
        {
            g.RevokedAt = now;
            g.RevokedByUserId = Sub;
            g.RevokedByName = DisplayName;
            await _db.SaveChangesAsync(ct);
            await _notifier.AuditAsync(g.TenantSlug, "PlatformAccessGrant", g.Id.ToString(), "PlatformAccessRevoked",
                new { by = IsPlatformAdmin && g.GrantedToUserId == Sub ? "platform" : "tenant" }, Sub, DisplayName, ct);
        }
        return Ok(Dto(g, now));
    }
}

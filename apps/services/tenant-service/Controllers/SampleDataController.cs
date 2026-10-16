using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TenantService.Services;

namespace TenantService.Controllers;

/// <summary>
/// Örnek veri (yenilik haritası #35): şirket yöneticisi kendi şirketini, platform yöneticisi herhangi bir
/// şirketi tek tıkla örnek veriyle doldurur ve temizler. Doldurma yalnızca boş şirkette (en fazla
/// <see cref="SampleDataService.MaxRealEmployees"/> gerçek çalışan) yapılır. Her işlem audit_log'a yazılır.
/// </summary>
[ApiController]
[Authorize]
public class SampleDataController : ControllerBase
{
    private readonly SampleDataService _svc;
    private readonly SecurityNotifier _audit;
    public SampleDataController(SampleDataService svc, SecurityNotifier audit) { _svc = svc; _audit = audit; }

    private string? MySlug => TenantService.Security.OrganizationClaimParser.ParseSlug(User.FindFirst("organization")?.Value);
    private string? ActorName => User.FindFirst("preferred_username")?.Value ?? User.FindFirst("email")?.Value;
    private string? ActorId => User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;

    private async Task<IActionResult> Run(string? slug, string action, Func<string, Task<SampleDataService.Status>> fn)
    {
        if (string.IsNullOrWhiteSpace(slug)) return NotFound(new { message = "Kullanıcı bir şirkete bağlı değil" });
        try
        {
            var st = await fn(slug);
            if (action != "status") await _audit.AuditAsync(slug, "SampleData", slug, $"SampleData:{action}", new { st.SampleEmployees }, ActorId, ActorName, HttpContext.RequestAborted);
            return Ok(st);
        }
        catch (SampleDataException e) { return BadRequest(new { message = e.Message }); }
    }

    [HttpGet("api/my-tenant/sample-data")]
    [Authorize(Policy = "RequireTenantAdmin")]
    public Task<IActionResult> MyStatus(CancellationToken ct) => Run(MySlug, "status", s => _svc.StatusAsync(s, ct));

    [HttpPost("api/my-tenant/sample-data")]
    [Authorize(Policy = "RequireTenantAdmin")]
    public Task<IActionResult> MyFill(CancellationToken ct) => Run(MySlug, "fill", s => _svc.FillAsync(s, ActorName, ct));

    [HttpDelete("api/my-tenant/sample-data")]
    [Authorize(Policy = "RequireTenantAdmin")]
    public Task<IActionResult> MyRemove(CancellationToken ct) => Run(MySlug, "remove", s => _svc.RemoveAsync(s, ct));

    [HttpGet("api/tenants/by-slug/{slug}/sample-data")]
    [Authorize(Policy = "RequirePlatformAdmin")]
    public Task<IActionResult> Status(string slug, CancellationToken ct) => Run(slug, "status", s => _svc.StatusAsync(s, ct));

    [HttpPost("api/tenants/by-slug/{slug}/sample-data")]
    [Authorize(Policy = "RequirePlatformAdmin")]
    public Task<IActionResult> Fill(string slug, CancellationToken ct) => Run(slug, "fill", s => _svc.FillAsync(s, ActorName, ct));

    [HttpDelete("api/tenants/by-slug/{slug}/sample-data")]
    [Authorize(Policy = "RequirePlatformAdmin")]
    public Task<IActionResult> Remove(string slug, CancellationToken ct) => Run(slug, "remove", s => _svc.RemoveAsync(s, ct));
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrganizationService.Data;
using OrganizationService.Infrastructure;
using OrganizationService.Models;
using OrganizationService.Tenancy;

namespace OrganizationService.Controllers;

/// <summary>
/// Matris organizasyon bağları (noktalı çizgi raporlama). Okuma kiracıdaki her oturum açmış
/// kullanıcıya açık (departman listesi gibi kişisel veri içermez); ekleme/silme İK ve kiracı
/// yöneticisine (RequireHrAdmin, departman yönetimiyle aynı). Yazımlar AuditInterceptor ile
/// audit_log'a düşer.
/// </summary>
[ApiController]
[Route("api/department-links")]
[Authorize]
public class DepartmentLinksController : ControllerBase
{
    private readonly OrganizationDbContext _db;
    private readonly ITenantContext _tenant;

    public DepartmentLinksController(OrganizationDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    /// <summary>Bağlar; <c>companyId</c> verilirse en az bir ucu o şirkette olanlar.</summary>
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] Guid? companyId, CancellationToken ct)
    {
        var query = _db.DepartmentLinks.AsNoTracking();
        if (companyId.HasValue)
        {
            var ids = _db.Departments.Where(d => d.CompanyId == companyId.Value).Select(d => d.Id);
            query = query.Where(l => ids.Contains(l.FromDepartmentId) || ids.Contains(l.ToDepartmentId));
        }
        var items = await query.OrderBy(l => l.CreatedAt).ThenBy(l => l.Id).Take(5000).ToListAsync(ct);
        return Ok(items);
    }

    [Authorize(Policy = "RequireHrAdmin")]
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateDepartmentLinkRequest request, CancellationToken ct)
    {
        var pair = new[] { request.FromDepartmentId, request.ToDepartmentId };
        var depts = await _db.Departments.AsNoTracking().Where(d => pair.Contains(d.Id))
            .Select(d => new DepartmentLinkRules.DeptInfo(d.Id, d.TenantSlug)).ToListAsync(ct);
        var existing = await _db.DepartmentLinks.AsNoTracking()
            .Where(l => l.FromDepartmentId == request.FromDepartmentId && l.ToDepartmentId == request.ToDepartmentId)
            .Select(l => new DepartmentLinkRules.LinkInfo(l.FromDepartmentId, l.ToDepartmentId, l.Kind)).ToListAsync(ct);

        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        var error = DepartmentLinkRules.Validate(request.FromDepartmentId, request.ToDepartmentId, request.Kind, note,
            depts.FirstOrDefault(d => d.Id == request.FromDepartmentId),
            depts.FirstOrDefault(d => d.Id == request.ToDepartmentId),
            _tenant.TenantSlug, existing);
        if (error is not null)
            return error == "Bu bağ zaten var." ? Conflict(new { message = error }) : BadRequest(new { message = error });

        var from = depts.First(d => d.Id == request.FromDepartmentId);
        var link = new DepartmentLink
        {
            // Platform yöneticisinin isteğinde kiracı boş olabilir; bağ departmanların kiracısına yazılır.
            TenantSlug = from.TenantSlug,
            FromDepartmentId = request.FromDepartmentId,
            ToDepartmentId = request.ToDepartmentId,
            Kind = DepartmentLinkRules.NormalizeKind(request.Kind)!,
            Note = note,
        };
        _db.DepartmentLinks.Add(link);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Eşzamanlı aynı istek: benzersiz indeks yakalar.
            return Conflict(new { message = "Bu bağ zaten var." });
        }
        return Created($"/api/department-links/{link.Id}", link);
    }

    [Authorize(Policy = "RequireHrAdmin")]
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var link = await _db.DepartmentLinks.FirstOrDefaultAsync(l => l.Id == id, ct);
        if (link is null) return NotFound();
        _db.DepartmentLinks.Remove(link);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}

public record CreateDepartmentLinkRequest(Guid FromDepartmentId, Guid ToDepartmentId, string? Kind, string? Note);

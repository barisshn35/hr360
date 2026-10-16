using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrganizationService.Data;
using OrganizationService.Models;
using OrganizationService.Tenancy;

namespace OrganizationService.Controllers;

/// <summary>
/// Örnek veri (yenilik haritası #35): örnek departmanlar. YALNIZCA tenant-service çağırır
/// (X-Internal-Token; gateway /api/*/internal/ yollarını dışarıya kapatır). Aynı adda departman
/// varsa yenisi açılmaz, mevcut kullanılır (ve temizlikte silinmez: yalnızca bu uçla açılanların
/// kimlikleri tenant-service'te tutulur). Silme, departmanda başka çalışan ya da alt departman
/// kalmadıysa yapılır.
/// </summary>
[ApiController]
[AllowAnonymous]
public class InternalSampleDataController : ControllerBase
{
    private readonly OrganizationDbContext _db;
    private readonly TenantContext _tenant;
    public InternalSampleDataController(OrganizationDbContext db, TenantContext tenant) { _db = db; _tenant = tenant; }

    public record CreateRequest(string TenantSlug, string CompanyName, List<string> Names);
    public record HeadsRequest(string TenantSlug, Dictionary<Guid, Guid> Heads);
    public record RemoveRequest(string TenantSlug, List<Guid> DepartmentIds, List<Guid>? EmployeeIds = null);

    private IActionResult? Enter(string? slug)
    {
        if (!Security.InternalServiceToken.Matches(Request.Headers[Security.InternalServiceToken.Header].FirstOrDefault())) return NotFound();
        if (string.IsNullOrWhiteSpace(slug)) return BadRequest(new { message = "Kiracı belirtilmedi" });
        _tenant.TenantSlug = slug.Trim();
        _tenant.IsPlatformAdmin = false;
        return null;
    }

    /// <summary>Departmanları açar; ad → (kimlik, bu çağrıda mı açıldı).</summary>
    [HttpPost("/api/internal/organization/sample")]
    public async Task<IActionResult> Create([FromBody] CreateRequest body, CancellationToken ct)
    {
        if (Enter(body.TenantSlug) is { } err) return err;
        if (body.Names is not { Count: > 0 and <= 20 }) return BadRequest(new { message = "1-20 departman" });
        var company = await _db.Companies.OrderBy(c => c.Id).FirstOrDefaultAsync(ct);
        if (company is null)
        {
            company = new Company { Name = string.IsNullOrWhiteSpace(body.CompanyName) ? "Şirket" : body.CompanyName.Trim() };
            _db.Companies.Add(company);
        }
        var existing = await _db.Departments.ToListAsync(ct);
        var result = new Dictionary<string, object>();
        foreach (var n in body.Names.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct())
        {
            var d = existing.FirstOrDefault(x => string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase));
            if (d is not null) { result[n] = new { id = d.Id, created = false }; continue; }
            d = new Department { Name = n, CompanyId = company.Id };
            _db.Departments.Add(d);
            result[n] = new { id = d.Id, created = true };
        }
        await _db.SaveChangesAsync(ct);
        return Ok(result);
    }

    /// <summary>Departman başlarını atar (yalnızca başı boş olanlar).</summary>
    [HttpPost("/api/internal/organization/sample/heads")]
    public async Task<IActionResult> Heads([FromBody] HeadsRequest body, CancellationToken ct)
    {
        if (Enter(body.TenantSlug) is { } err) return err;
        var ids = body.Heads.Keys.ToList();
        foreach (var d in await _db.Departments.Where(d => ids.Contains(d.Id) && d.HeadEmployeeId == null).ToListAsync(ct))
            d.HeadEmployeeId = body.Heads[d.Id];
        await _db.SaveChangesAsync(ct);
        return Ok(new { ok = true });
    }

    [HttpPost("/api/internal/organization/sample/remove")]
    public async Task<IActionResult> Remove([FromBody] RemoveRequest body, CancellationToken ct)
    {
        if (Enter(body.TenantSlug) is { } err) return err;
        var ids = body.DepartmentIds ?? [];
        // Silinen örnek kişiler hiçbir departmanın başı olarak kalmasın (şirket sonradan atamış olabilir).
        if (body.EmployeeIds is { Count: > 0 } gone)
        {
            foreach (var d in await _db.Departments.Where(d => d.HeadEmployeeId != null && gone.Contains(d.HeadEmployeeId.Value)).ToListAsync(ct))
                d.HeadEmployeeId = null;
            await _db.SaveChangesAsync(ct);
        }
        var inUse = await _db.Database.SqlQuery<Guid>($"""
            SELECT DISTINCT "DepartmentId" AS "Value" FROM employee_assignments WHERE "TenantSlug" = {body.TenantSlug} AND "DepartmentId" = ANY({ids.ToArray()})
            """).ToListAsync(ct);
        var parents = await _db.Departments.Where(d => d.ParentDepartmentId != null && ids.Contains(d.ParentDepartmentId.Value) && !ids.Contains(d.Id))
            .Select(d => d.ParentDepartmentId!.Value).ToListAsync(ct);
        var removed = new List<Guid>();
        // Tek tek: ekip/pozisyon gibi bir kayıt bağlıysa o departman kalır, diğerleri silinir.
        foreach (var d in await _db.Departments.Where(d => ids.Contains(d.Id) && !inUse.Contains(d.Id) && !parents.Contains(d.Id)).ToListAsync(ct))
        {
            _db.Departments.Remove(d);
            try { await _db.SaveChangesAsync(ct); removed.Add(d.Id); }
            catch (DbUpdateException) { _db.Entry(d).State = EntityState.Detached; }
        }
        return Ok(new { removed, kept = ids.Except(removed) });
    }
}

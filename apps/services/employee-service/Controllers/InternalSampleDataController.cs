using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using EmployeeService.Data;
using EmployeeService.Models;
using EmployeeService.Tenancy;

namespace EmployeeService.Controllers;

/// <summary>
/// Örnek veri (yenilik haritası #35): boş bir şirketi denemek için örnek çalışanlar. YALNIZCA
/// tenant-service çağırır (X-Internal-Token; gateway /api/*/internal/ yollarını dışarıya kapatır).
/// Örnek kişilerin e-postası ayrılmış ".invalid" alanındadır (RFC 2606; hiçbir e-posta gidemez) ve
/// temizlik yalnızca bu alandaki kayıtları siler. Olay (Kafka) yayımlanmaz: örnek kişiler için işe
/// giriş planı, hoş geldin e-postası vb. tetiklenmez. Giriş hesabı açılmaz.
/// </summary>
[ApiController]
[AllowAnonymous]
public class InternalSampleDataController : ControllerBase
{
    public const string Domain = "@ornek.invalid";
    private readonly EmployeeDbContext _db;
    private readonly TenantContext _tenant;
    public InternalSampleDataController(EmployeeDbContext db, TenantContext tenant) { _db = db; _tenant = tenant; }

    private bool Authorized() => Security.InternalServiceToken.Matches(Request.Headers[Security.InternalServiceToken.Header].FirstOrDefault());

    public record SamplePerson(string FirstName, string LastName, string Email, string? PositionTitle, Guid? DepartmentId, DateOnly HireDate);
    public record SampleRequest(string TenantSlug, List<SamplePerson> People);
    public record TenantRequest(string TenantSlug);

    private void Scope(string slug) { _tenant.TenantSlug = slug.Trim(); _tenant.IsPlatformAdmin = false; }

    /// <summary>Şirketteki gerçek (örnek olmayan) çalışan sayısı ve örnek çalışanlar.</summary>
    [HttpPost("/api/internal/employees/sample/status")]
    public async Task<IActionResult> Status([FromBody] TenantRequest body, CancellationToken ct)
    {
        if (!Authorized()) return NotFound();
        Scope(body.TenantSlug);
        var all = await _db.Employees.AsNoTracking().Select(e => new { e.Id, e.Email }).ToListAsync(ct);
        return Ok(new
        {
            real = all.Count(e => !e.Email.EndsWith(Domain)),
            sampleIds = all.Where(e => e.Email.EndsWith(Domain)).Select(e => e.Id),
        });
    }

    [HttpPost("/api/internal/employees/sample")]
    public async Task<IActionResult> Create([FromBody] SampleRequest body, CancellationToken ct)
    {
        if (!Authorized()) return NotFound();
        if (string.IsNullOrWhiteSpace(body.TenantSlug) || body.People is not { Count: > 0 and <= 100 }) return BadRequest(new { message = "1-100 kişi" });
        if (body.People.Any(p => !p.Email.EndsWith(Domain, StringComparison.OrdinalIgnoreCase)))
            return BadRequest(new { message = $"Örnek kişilerin e-postası {Domain} ile bitmeli" });
        Scope(body.TenantSlug);
        var existing = (await _db.Employees.Where(e => e.Email.EndsWith(Domain)).Select(e => new { e.Id, e.Email }).ToListAsync(ct))
            .ToDictionary(e => e.Email.ToLowerInvariant(), e => e.Id);
        var ids = new Dictionary<string, Guid>();
        foreach (var p in body.People)
        {
            var email = p.Email.Trim().ToLowerInvariant();
            if (existing.TryGetValue(email, out var have)) { ids[email] = have; continue; }
            var e = new Employee { FirstName = p.FirstName.Trim(), LastName = p.LastName.Trim(), Email = email, HireDate = p.HireDate };
            _db.Employees.Add(e);
            if (p.DepartmentId is { } d)
                _db.Assignments.Add(new Assignment { EmployeeId = e.Id, DepartmentId = d, PositionTitle = p.PositionTitle, EffectiveFrom = p.HireDate });
            ids[email] = e.Id;
        }
        await _db.SaveChangesAsync(ct);
        return Ok(new { ids });
    }

    /// <summary>Örnek çalışanları ve atamalarını siler; silinen kimlikleri döner (diğer servisler temizlesin diye).</summary>
    [HttpPost("/api/internal/employees/sample/remove")]
    public async Task<IActionResult> Remove([FromBody] TenantRequest body, CancellationToken ct)
    {
        if (!Authorized()) return NotFound();
        Scope(body.TenantSlug);
        var people = await _db.Employees.Where(e => e.Email.EndsWith(Domain)).ToListAsync(ct);
        var ids = people.Select(e => e.Id).ToList();
        _db.Assignments.RemoveRange(await _db.Assignments.Where(a => ids.Contains(a.EmployeeId)).ToListAsync(ct));
        _db.Photos.RemoveRange(await _db.Photos.Where(p => ids.Contains(p.EmployeeId)).ToListAsync(ct));
        _db.Employees.RemoveRange(people);
        await _db.SaveChangesAsync(ct);
        return Ok(new { removed = ids });
    }
}

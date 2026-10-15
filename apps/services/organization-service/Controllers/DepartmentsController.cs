using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrganizationService.Data;
using OrganizationService.Infrastructure;
using OrganizationService.Models;
using OrganizationService.Tenancy;

namespace OrganizationService.Controllers;

[ApiController]
[Route("api/departments")]
[Authorize]
public class DepartmentsController : ControllerBase
{
    private readonly OrganizationDbContext _db;
    private readonly RefCache _cache;
    private readonly ITenantContext _tenant;

    public DepartmentsController(OrganizationDbContext db, RefCache cache, ITenantContext tenant)
    {
        _db = db;
        _cache = cache;
        _tenant = tenant;
    }

    internal const string CacheName = "departments";

    /// <remarks>
    /// Sayfalama (G24): <c>page</c> verilmezse eski biçim (düz dizi, tüm departmanlar; sayı
    /// <c>X-Total-Count</c> başlığında). <c>page</c>/<c>pageSize</c> (en fazla 200) ile
    /// <c>{ items, total, page, pageSize }</c>. <c>q</c>: ad (Türkçe harf katlamalı), <c>sort</c>=name|createdAt,
    /// <c>dir</c>=asc|desc. Departman listesi kişisel veri içermediğinden kiracı başına 60 sn önbelleğe
    /// alınır (yazma işlemleri önbelleği hemen eskitir).
    /// </remarks>
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] Guid? companyId, [FromQuery] int? page = null,
        [FromQuery] int? pageSize = null, [FromQuery] string? q = null, [FromQuery] string? sort = null,
        [FromQuery] string? dir = null)
    {
        var desc = Paging.Desc(dir);
        var byCreated = string.Equals(sort, "createdAt", StringComparison.OrdinalIgnoreCase);
        var (p, size) = Paging.Normalize(page ?? 1, pageSize);
        var key = $"{companyId}|{(page is null ? "all" : $"{p}/{size}")}|{q?.Trim()}|{byCreated}|{desc}";
        var (items, total) = await _cache.GetOrSetAsync(CacheName, RefCache.Scope(_tenant), key, async () =>
        {
            var query = _db.Departments.AsNoTracking().AsQueryable();
            if (companyId.HasValue) query = query.Where(d => d.CompanyId == companyId.Value);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var like = Paging.FoldLike(q);
                query = query.Where(d => EF.Functions.Like(OrganizationDbContext.Fold(d.Name), like, "\\"));
            }
            IOrderedQueryable<Department> ordered = byCreated
                ? (desc ? query.OrderByDescending(d => d.CreatedAt) : query.OrderBy(d => d.CreatedAt))
                : (desc ? query.OrderByDescending(d => d.Name) : query.OrderBy(d => d.Name));
            ordered = ordered.ThenBy(d => d.Id);
            if (page is null)
            {
                var all = await ordered.ToListAsync();
                return (all, all.Count);
            }
            var count = await ordered.CountAsync();
            var slice = count == 0 ? new List<Department>() : await ordered.Skip((p - 1) * size).Take(size).ToListAsync();
            return (slice, count);
        });
        Paging.SetTotal(this, total);
        return page is null ? Ok(items) : Ok(new PagedResult<Department>(items, total, p, size));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var department = await _db.Departments.FirstOrDefaultAsync(d => d.Id == id);
        return department is null ? NotFound() : Ok(department);
    }

    [Authorize(Policy = "RequireHrAdmin")]
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateDepartmentRequest request)
    {
        var companyExists = await _db.Companies.AnyAsync(c => c.Id == request.CompanyId);
        if (!companyExists) return BadRequest("Company not found");

        var department = new Department
        {
            Name = request.Name,
            CompanyId = request.CompanyId,
            ParentDepartmentId = request.ParentDepartmentId
        };
        _db.Departments.Add(department);
        await _db.SaveChangesAsync();
        _cache.Bump(CacheName, department.TenantSlug);
        return CreatedAtAction(nameof(GetAll), new { companyId = department.CompanyId }, department);
    }

    /// <summary>
    /// Departman adini gunceller. CompanyId/ParentDepartmentId bu uctan
    /// DEGISTIRILEMEZ - departmani baska bir sirkete/ust departmana
    /// tasimak farkli bir islem, sadece isim degisikligiyle karistirilmamali.
    /// </summary>
    [Authorize(Policy = "RequireHrAdmin")]
    [HttpPut("{id}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateDepartmentRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { message = "Departman adi bos olamaz" });

        var department = await _db.Departments.FirstOrDefaultAsync(d => d.Id == id);
        if (department is null) return NotFound();

        department.Name = request.Name.Trim();
        department.HeadEmployeeId = request.HeadEmployeeId;
        await _db.SaveChangesAsync();
        _cache.Bump(CacheName, department.TenantSlug);
        return Ok(department);
    }

    /// <summary>
    /// Departmani siler.
    ///
    /// GUVENLIK: bu uc silmeyi REDDEDER eger departmanin altinda ekip,
    /// alt departman ya da atanmis calisan varsa. Sebep: veritabaninda
    /// organization_teams.DepartmentId ON DELETE CASCADE - departmani
    /// silmek EKIPLERI DE SESSIZCE goturur, kullaniciya sormadan.
    /// ParentDepartmentId ve employee_assignments.DepartmentId'nin ise
    /// hic FK kisiti yok - silinirse bu alanlar YETIM kalir, hicbir hata
    /// vermez ama veri tutarsizligi yaratir.
    ///
    /// Once ilgili kayitlarin tasinmasi/kaldirilmasi istenir.
    /// </summary>
    [Authorize(Policy = "RequireHrAdmin")]
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var department = await _db.Departments.FirstOrDefaultAsync(d => d.Id == id);
        if (department is null) return NotFound();

        var childDepartmentCount = await _db.Departments.CountAsync(d => d.ParentDepartmentId == id);
        // Yalnızca AKTİF ekipler engeller; pasif ekipler (ve üyelik geçmişleri) FK ON DELETE CASCADE ile
        // departmanla birlikte silinir.
        var teamCount = await _db.Teams.CountAsync(t => t.DepartmentId == id && t.IsActive);

        // employee_assignments employee-service'in tablosu; aynı fiziksel veritabanında
        // yalnızca OKUNUR (salt sayım). Yalnızca bugün geçerli/gelecekteki atamalar engeller;
        // kapanmış (EffectiveTo geçmişte) atamalar engellemez. Bu sütunda FK yoktur: geçmiş
        // atama satırları değiştirilmez, departman kimliğini tarihçe olarak korur.
        var assignedEmployeeCount = await _db.Database
            .SqlQuery<int>($@"SELECT COUNT(*)::int AS ""Value"" FROM employee_assignments
                               WHERE ""DepartmentId"" = {id}
                                 AND (""EffectiveTo"" IS NULL OR ""EffectiveTo"" >= CURRENT_DATE)")
            .FirstAsync();

        var blockers = new List<string>();
        if (childDepartmentCount > 0) blockers.Add($"{childDepartmentCount} alt departman");
        if (teamCount > 0) blockers.Add($"{teamCount} aktif ekip");
        if (assignedEmployeeCount > 0) blockers.Add($"{assignedEmployeeCount} aktif çalışan ataması");

        if (blockers.Count > 0)
        {
            return Conflict(new
            {
                message = "Departman silinemedi: önce şu kayıtların taşınması ya da " +
                          $"kaldırılması gerekiyor: {string.Join(", ", blockers)}.",
                childDepartmentCount,
                teamCount,
                assignedEmployeeCount,
            });
        }

        _db.Departments.Remove(department);
        await _db.SaveChangesAsync();
        _cache.Bump(CacheName, department.TenantSlug);
        return NoContent();
    }
}

public record CreateDepartmentRequest(string Name, Guid CompanyId, Guid? ParentDepartmentId);
public record UpdateDepartmentRequest(string Name, Guid? HeadEmployeeId = null);

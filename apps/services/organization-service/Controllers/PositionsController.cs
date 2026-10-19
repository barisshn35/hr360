using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrganizationService.Data;
using OrganizationService.Models;
using OrganizationService.Tenancy;

namespace OrganizationService.Controllers;

/// <summary>
/// Norm kadro ve pozisyonlar (yenilik haritası #12). İK tanımlar; yönetici başı olduğu departmanların
/// kadrosunu (bütçe hariç) görür. Dolu kadro = departmanda o unvanla atanmış aktif çalışanlar.
/// "Kadro dışı": hiçbir aktif pozisyonla eşleşmeyen unvanla çalışanlar.
/// </summary>
[ApiController]
[Route("api/positions")]
[Authorize(Policy = "RequireManagerOrAbove")]
public class PositionsController : ControllerBase
{
    private readonly OrganizationDbContext _db;
    private readonly ITenantContext _tenant;
    public PositionsController(OrganizationDbContext db, ITenantContext tenant) { _db = db; _tenant = tenant; }

    private bool IsHr => User.IsInRole("hr-admin") || User.IsInRole("tenant-admin") || User.IsInRole("platform-admin") || User.IsInRole("ext-organization-manage");
    private string? Sub => User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;

    public sealed class Holder
    {
        public Guid EmployeeId { get; set; }
        public string Name { get; set; } = "";
        public Guid DepartmentId { get; set; }
        public string? PositionTitle { get; set; }
    }

    private Task<List<Holder>> HoldersAsync(CancellationToken ct) => _db.Database.SqlQueryRaw<Holder>("""
        SELECT e."Id" AS "EmployeeId", e."FirstName" || ' ' || e."LastName" AS "Name", a."DepartmentId", a."PositionTitle"
        FROM employee_employees e
        JOIN LATERAL (SELECT x."DepartmentId", x."PositionTitle" FROM employee_assignments x
            WHERE x."EmployeeId" = e."Id" AND x."EffectiveFrom" <= current_date AND (x."EffectiveTo" IS NULL OR x."EffectiveTo" >= current_date)
            ORDER BY x."EffectiveFrom" DESC LIMIT 1) a ON true
        WHERE e."TenantSlug" = {0} AND e."Status" <> 'Terminated'
        """, _tenant.TenantSlug ?? "").ToListAsync(ct);

    /// <summary>Kişinin görebileceği departmanlar: İK tümü, yönetici başı olduğu departmanlar.</summary>
    private async Task<HashSet<Guid>?> ScopeAsync(CancellationToken ct)
    {
        if (IsHr) return null;
        var me = await _db.Database.SqlQueryRaw<Guid>("SELECT \"Id\" AS \"Value\" FROM employee_employees WHERE \"TenantSlug\" = {0} AND \"KeycloakUserId\" = {1}",
            _tenant.TenantSlug ?? "", Sub ?? "").ToListAsync(ct);
        if (me.Count == 0) return new();
        return (await _db.Departments.AsNoTracking().Where(d => d.HeadEmployeeId == me[0]).Select(d => d.Id).ToListAsync(ct)).ToHashSet();
    }

    /// <summary>Unvan karşılaştırması: büyük/küçük harf, i/ı ve İ/I farkı ve fazla boşluk yok sayılır.</summary>
    public static string Key(string? s) => System.Text.RegularExpressions.Regex.Replace(
        (s ?? "").Trim().ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD).Replace("\u0307", "").Replace('ı', 'i'), @"\s+", " ");

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] Guid? departmentId, CancellationToken ct)
    {
        var scope = await ScopeAsync(ct);
        var depts = (await _db.Departments.AsNoTracking().ToListAsync(ct)).Where(d => scope is null || scope.Contains(d.Id)).ToDictionary(d => d.Id);
        if (departmentId is { } dep && !depts.ContainsKey(dep)) return Forbid();
        var positions = (await _db.Positions.AsNoTracking().Where(p => p.IsActive).ToListAsync(ct))
            .Where(p => depts.ContainsKey(p.DepartmentId) && (departmentId is null || p.DepartmentId == departmentId)).ToList();
        var holders = (await HoldersAsync(ct)).Where(h => depts.ContainsKey(h.DepartmentId) && (departmentId is null || h.DepartmentId == departmentId)).ToList();
        var keys = positions.Select(p => (p.DepartmentId, Key(p.Title))).ToHashSet();
        var rows = positions.OrderBy(p => depts[p.DepartmentId].Name).ThenBy(p => p.Title).Select(p =>
        {
            var hs = holders.Where(h => h.DepartmentId == p.DepartmentId && Key(h.PositionTitle) == Key(p.Title)).ToList();
            return new
            {
                p.Id, p.DepartmentId, department = depts[p.DepartmentId].Name, p.Title, p.Headcount, filled = hs.Count, open = Math.Max(0, p.Headcount - hs.Count),
                over = Math.Max(0, hs.Count - p.Headcount), p.Grade, p.Notes,
                budgetMonthly = IsHr ? p.BudgetMonthly : null, currency = IsHr ? p.Currency : null,
                holders = hs.Select(h => new { h.EmployeeId, h.Name }),
            };
        }).ToList();
        var outside = holders.Where(h => !keys.Contains((h.DepartmentId, Key(h.PositionTitle))))
            .Select(h => new { h.EmployeeId, h.Name, h.DepartmentId, department = depts[h.DepartmentId].Name, positionTitle = h.PositionTitle }).ToList();
        return Ok(new
        {
            positions = rows,
            outside,
            summary = new
            {
                approved = rows.Sum(r => r.Headcount), filled = rows.Sum(r => r.filled), open = rows.Sum(r => r.open), over = rows.Sum(r => r.over), outside = outside.Count,
                budgetMonthly = IsHr ? positions.Where(p => p.BudgetMonthly is not null).GroupBy(p => p.Currency).Select(g => new { currency = g.Key, amount = g.Sum(p => p.BudgetMonthly!.Value) }) : null,
            },
        });
    }

    public sealed record PositionInput(Guid DepartmentId, string Title, int Headcount, string? Grade, decimal? BudgetMonthly, string? Currency, string? Notes);

    private async Task<IActionResult?> ValidateAsync(PositionInput b, Guid? exceptId, CancellationToken ct)
    {
        var title = (b.Title ?? "").Trim();
        if (title.Length is < 2 or > 120) return BadRequest(new { message = "Unvan 2-120 karakter olmalı" });
        if (b.Headcount is < 0 or > 10000) return BadRequest(new { message = "Kadro 0-10000 olmalı" });
        if (b.BudgetMonthly is < 0) return BadRequest(new { message = "Bütçe negatif olamaz" });
        if (b.Notes is { Length: > 500 } || b.Grade is { Length: > 40 }) return BadRequest(new { message = "Not ya da kademe çok uzun" });
        if (!await _db.Departments.AnyAsync(d => d.Id == b.DepartmentId, ct)) return BadRequest(new { message = "Departman bulunamadı" });
        var key = Key(title);
        var dupes = await _db.Positions.Where(p => p.IsActive && p.DepartmentId == b.DepartmentId && p.Id != exceptId).Select(p => p.Title).ToListAsync(ct);
        if (dupes.Any(t => Key(t) == key)) return Conflict(new { message = "Bu departmanda aynı unvanla pozisyon var" });
        return null;
    }

    [HttpPost]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Create(PositionInput b, CancellationToken ct)
    {
        if (await ValidateAsync(b, null, ct) is { } err) return err;
        var p = new Position
        {
            DepartmentId = b.DepartmentId, Title = b.Title.Trim(), Headcount = b.Headcount, Grade = b.Grade?.Trim(), BudgetMonthly = b.BudgetMonthly,
            Currency = string.IsNullOrWhiteSpace(b.Currency) ? "TRY" : b.Currency.Trim().ToUpperInvariant(), Notes = b.Notes?.Trim()
        };
        _db.Positions.Add(p);
        await _db.SaveChangesAsync(ct);
        return Ok(p);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Update(Guid id, PositionInput b, CancellationToken ct)
    {
        var p = await _db.Positions.FirstOrDefaultAsync(x => x.Id == id && x.IsActive, ct);
        if (p is null) return NotFound();
        if (await ValidateAsync(b, id, ct) is { } err) return err;
        (p.DepartmentId, p.Title, p.Headcount, p.Grade, p.BudgetMonthly, p.Notes, p.UpdatedAt) = (b.DepartmentId, b.Title.Trim(), b.Headcount, b.Grade?.Trim(), b.BudgetMonthly, b.Notes?.Trim(), DateTimeOffset.UtcNow);
        if (!string.IsNullOrWhiteSpace(b.Currency)) p.Currency = b.Currency.Trim().ToUpperInvariant();
        await _db.SaveChangesAsync(ct);
        return Ok(p);
    }

    /// <summary>Pozisyon kaldırılır (geçmiş için pasifleştirilir).</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var p = await _db.Positions.FirstOrDefaultAsync(x => x.Id == id && x.IsActive, ct);
        if (p is null) return NotFound();
        p.IsActive = false; p.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}

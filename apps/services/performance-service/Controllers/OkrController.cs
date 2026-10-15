using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PerformanceService.Data;
using PerformanceService.Models;
using PerformanceService.Security;
using PerformanceService.Services;
using PerformanceService.Tenancy;

namespace PerformanceService.Controllers;

/// <summary>
/// Dalga 11 / 80: OKR hizalama ağacı. Şirket amaçlarını İK, departman amaçlarını İK ya da departman
/// başı tanımlar; kişisel hedefler (performance_goals) bir amaca bağlanır (yönetici+). İlerleme
/// yukarı doğru ağırlıklı ortalamayla toplanır (OkrMath).
/// Görünürlük: amaçlar herkese açık. Yönetici+ tüm kişisel hedefleri görür; çalışan yalnızca kendi
/// hedeflerini görür, diğerleri sayı olarak yansır ve küçük grup ilerlemesi gizlenir (KVKK).
/// </summary>
[ApiController]
[Route("api/okr")]
[Authorize]
public class OkrController : ControllerBase
{
    private readonly PerformanceDbContext _db;
    private readonly PerfPeople _people;
    private readonly ITenantContext _tenant;

    public OkrController(PerformanceDbContext db, PerfPeople people, ITenantContext tenant) { _db = db; _people = people; _tenant = tenant; }

    public sealed class DeptRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public Guid? HeadEmployeeId { get; set; }
    }

    private Task<List<DeptRow>> DepartmentsAsync(CancellationToken ct) =>
        _db.Database.SqlQueryRaw<DeptRow>(
            "SELECT \"Id\", \"Name\", \"HeadEmployeeId\" FROM organization_departments WHERE \"TenantSlug\" = {0}",
            _tenant.TenantSlug ?? "").ToListAsync(ct);

    [HttpGet("tree")]
    public async Task<IActionResult> Tree([FromQuery] Guid cycleId, CancellationToken ct)
    {
        var cycle = await _db.Cycles.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cycleId, ct);
        if (cycle is null) return NotFound(new { message = "Dönem bulunamadı" });
        var privileged = User.IsManagerOrAbove();
        var me = await _people.MeAsync(ct);
        var objectives = await _db.Objectives.AsNoTracking().Where(o => o.CycleId == cycleId).ToListAsync(ct);
        var goals = await _db.Goals.AsNoTracking().Where(g => g.CycleId == cycleId && g.Status != GoalStatus.Draft).ToListAsync(ct);
        var depts = (await DepartmentsAsync(ct)).ToDictionary(d => d.Id);
        var people = privileged ? (await _people.ActiveAsync(ct)).ToDictionary(p => p.Id, p => p.FullName) : new Dictionary<Guid, string>();
        if (me is not null) people[me.Id] = me.FullName;

        var nodes = objectives.Select(o => new OkrNode { Id = o.Id, Kind = o.Level.ToString(), ParentId = o.ParentId, Weight = o.Weight })
            .Concat(goals.Where(g => g.ParentObjectiveId != null).Select(g => new OkrNode
            {
                Id = g.Id, Kind = "Goal", ParentId = g.ParentObjectiveId, Weight = g.Weight, EmployeeId = g.EmployeeId,
                OwnProgress = OkrMath.GoalProgress(g),
            }))
            .ToList();
        var roots = OkrMath.Build(nodes);
        var hidden = OkrMath.Suppressed(roots, me?.Id, privileged);
        var objById = objectives.ToDictionary(o => o.Id);
        var goalById = goals.ToDictionary(g => g.Id);
        var canEditCompany = User.IsHr();

        object View(OkrNode n)
        {
            if (n.Kind == "Goal")
            {
                var g = goalById[n.Id];
                return new
                {
                    n.Id, kind = "Goal", title = g.Title, n.Weight, progress = n.Progress, status = g.Status.ToString(),
                    employeeId = g.EmployeeId, employeeName = people.GetValueOrDefault(g.EmployeeId), children = Array.Empty<object>(),
                };
            }
            var o = objById[n.Id];
            var visibleGoals = n.Children.Where(c => c.Kind != "Goal" || privileged || c.EmployeeId == me?.Id).ToList();
            var dept = o.DepartmentId is { } d ? depts.GetValueOrDefault(d) : null;
            return new
            {
                n.Id, kind = n.Kind, title = o.Title, o.Description, n.Weight, o.ParentId, o.DepartmentId, departmentName = dept?.Name,
                progress = hidden.Contains(n.Id) ? null : n.Progress,
                progressHidden = hidden.Contains(n.Id),
                people = n.Employees.Count,
                hiddenGoals = n.Children.Count - visibleGoals.Count,
                canEdit = canEditCompany || (n.Kind == "Department" && me is not null && dept?.HeadEmployeeId == me.Id),
                children = visibleGoals.OrderBy(c => c.Kind == "Goal").ThenByDescending(c => c.Weight).Select(View).ToList(),
            };
        }

        var unaligned = goals.Where(g => g.ParentObjectiveId is null || !objById.ContainsKey(g.ParentObjectiveId.Value))
            .Where(g => privileged || g.EmployeeId == me?.Id)
            .OrderBy(g => people.GetValueOrDefault(g.EmployeeId)).ThenBy(g => g.Title)
            .Take(500)
            .Select(g => new
            {
                g.Id, g.Title, g.Weight, g.EmployeeId, employeeName = people.GetValueOrDefault(g.EmployeeId),
                progress = OkrMath.GoalProgress(g),
                status = g.Status.ToString(),
            }).ToList();

        var headOf = me is null ? new List<object>() : depts.Values.Where(d => canEditCompany || d.HeadEmployeeId == me.Id)
            .OrderBy(d => d.Name).Select(d => (object)new { d.Id, d.Name }).ToList();
        return Ok(new
        {
            cycle = new { cycle.Id, cycle.Name, status = cycle.Status.ToString() },
            roots = roots.Where(r => r.Kind != "Goal").OrderBy(r => r.Kind == "Department").ThenByDescending(r => r.Weight).Select(View),
            unaligned,
            canEditCompany,
            canAlign = privileged && cycle.Status != CycleStatus.Closed,
            editableDepartments = cycle.Status == CycleStatus.Closed ? new List<object>() : headOf,
            minGroup = OkrMath.MinGroup,
            objectives = objectives.Select(o => new { o.Id, o.Title, level = o.Level.ToString(), o.DepartmentId }),
        });
    }

    public record ObjectiveInput(Guid CycleId, string? Level, Guid? DepartmentId, Guid? ParentId, string? Title, string? Description, int Weight = 100);

    private async Task<IActionResult?> CanEditAsync(ObjectiveLevel level, Guid? departmentId, CancellationToken ct)
    {
        if (User.IsHr()) return null;
        if (level == ObjectiveLevel.Company) return StatusCode(403, new { message = "Şirket amaçlarını yalnızca İK tanımlar" });
        var me = await _people.MeAsync(ct);
        var dept = (await DepartmentsAsync(ct)).FirstOrDefault(d => d.Id == departmentId);
        if (me is null || dept?.HeadEmployeeId != me.Id)
            return StatusCode(403, new { message = "Departman amaçlarını İK ya da departman başı tanımlar" });
        return null;
    }

    private async Task<string?> ValidateAsync(Objective o, CancellationToken ct)
    {
        var title = o.Title.Trim();
        if (title.Length is < 3 or > 200) return "Amaç başlığı 3-200 karakter olmalı";
        if (o.Description is { Length: > 2000 }) return "Açıklama en fazla 2000 karakter olabilir";
        if (o.Weight is < 1 or > 100) return "Ağırlık 1-100 arasında olmalı";
        if (o.Level == ObjectiveLevel.Department)
        {
            if (o.DepartmentId is not { } d) return "Departman seçin";
            if (!(await DepartmentsAsync(ct)).Any(x => x.Id == d)) return "Departman bulunamadı";
        }
        string? parentKind = null;
        if (o.ParentId is { } pid)
        {
            var parent = await _db.Objectives.AsNoTracking().FirstOrDefaultAsync(x => x.Id == pid, ct);
            if (parent is null || parent.CycleId != o.CycleId) return "Üst amaç bulunamadı ya da başka bir döneme ait";
            parentKind = parent.Level.ToString();
        }
        return OkrMath.ValidateParent(o.Level.ToString(), parentKind);
    }

    private async Task<IActionResult?> OpenCycleAsync(Guid cycleId, CancellationToken ct)
    {
        var cycle = await _db.Cycles.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cycleId, ct);
        if (cycle is null) return NotFound(new { message = "Dönem bulunamadı" });
        if (cycle.Status == CycleStatus.Closed) return Conflict(new { message = "Kapanmış dönemin amaçları değiştirilemez" });
        return null;
    }

    [HttpPost("objectives")]
    public async Task<IActionResult> Create([FromBody] ObjectiveInput body, CancellationToken ct)
    {
        if (!Enum.TryParse<ObjectiveLevel>(body.Level, true, out var level)) return BadRequest(new { message = "Seviye Company ya da Department olmalı" });
        if (await OpenCycleAsync(body.CycleId, ct) is { } cErr) return cErr;
        if (await CanEditAsync(level, body.DepartmentId, ct) is { } aErr) return aErr;
        var o = new Objective
        {
            CycleId = body.CycleId, Level = level, DepartmentId = level == ObjectiveLevel.Department ? body.DepartmentId : null,
            ParentId = body.ParentId, Title = (body.Title ?? "").Trim(), Weight = body.Weight,
            Description = string.IsNullOrWhiteSpace(body.Description) ? null : body.Description.Trim(), CreatedBy = PerfPeople.DisplayName(User),
        };
        if (await ValidateAsync(o, ct) is { } error) return BadRequest(new { message = error });
        _db.Objectives.Add(o);
        await _db.SaveChangesAsync(ct);
        return Ok(new { o.Id, o.Title, level = o.Level.ToString(), o.DepartmentId, o.ParentId, o.Weight });
    }

    [HttpPut("objectives/{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] ObjectiveInput body, CancellationToken ct)
    {
        var o = await _db.Objectives.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (o is null) return NotFound(new { message = "Amaç bulunamadı" });
        if (await OpenCycleAsync(o.CycleId, ct) is { } cErr) return cErr;
        if (await CanEditAsync(o.Level, o.DepartmentId, ct) is { } aErr) return aErr;
        if (body.ParentId == o.Id) return BadRequest(new { message = "Amaç kendisine bağlanamaz" });
        o.Title = (body.Title ?? "").Trim();
        o.Description = string.IsNullOrWhiteSpace(body.Description) ? null : body.Description.Trim();
        o.Weight = body.Weight;
        o.ParentId = body.ParentId;
        if (await ValidateAsync(o, ct) is { } error) return BadRequest(new { message = error });
        await _db.SaveChangesAsync(ct);
        return Ok(new { o.Id, o.Title, level = o.Level.ToString(), o.DepartmentId, o.ParentId, o.Weight });
    }

    /// <summary>Amacı siler; bağlı alt amaçlar ve hedefler bağlantısız kalır (veritabanı ON DELETE SET NULL).</summary>
    [HttpDelete("objectives/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var o = await _db.Objectives.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (o is null) return NotFound(new { message = "Amaç bulunamadı" });
        if (await OpenCycleAsync(o.CycleId, ct) is { } cErr) return cErr;
        if (await CanEditAsync(o.Level, o.DepartmentId, ct) is { } aErr) return aErr;
        await _db.Objectives.Where(x => x.Id == id).ExecuteDeleteAsync(ct);
        await _people.AuditAsync("Objective", id.ToString(), "Deleted", new { o.Title, level = o.Level.ToString(), o.CycleId });
        return NoContent();
    }

    public record AlignInput(Guid? ParentObjectiveId);

    /// <summary>Kişisel hedefi bir amaca bağlar (ya da bağlantıyı kaldırır). Hedefleri yönetici+ yönetir.</summary>
    [HttpPut("goals/{goalId:guid}/parent")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> Align(Guid goalId, [FromBody] AlignInput body, CancellationToken ct)
    {
        var g = await _db.Goals.FirstOrDefaultAsync(x => x.Id == goalId, ct);
        if (g is null) return NotFound(new { message = "Hedef bulunamadı" });
        if (await OpenCycleAsync(g.CycleId, ct) is { } cErr) return cErr;
        if (body.ParentObjectiveId is { } pid)
        {
            var parent = await _db.Objectives.AsNoTracking().FirstOrDefaultAsync(x => x.Id == pid, ct);
            if (parent is null || parent.CycleId != g.CycleId) return BadRequest(new { message = "Üst amaç bulunamadı ya da başka bir döneme ait" });
        }
        g.ParentObjectiveId = body.ParentObjectiveId;
        await _db.SaveChangesAsync(ct);
        return Ok(new { g.Id, g.ParentObjectiveId });
    }
}

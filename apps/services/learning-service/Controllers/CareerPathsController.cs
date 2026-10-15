using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LearningService.Data;
using LearningService.Models;
using LearningService.Services;

namespace LearningService.Controllers;

/// <summary>
/// Dalga 11 (madde 83) kariyer yolları: rol basamakları (pozisyon unvanı) ve basamak başına beklenen
/// yetkinlik seviyeleri. Tanımlar herkese açıktır (kişisel veri değildir); İK düzenler. Kişinin ilerleme
/// görünümü (açık, hazırlık, açığı kapatan eğitimler) yalnızca kendisine, departman başkanına ve İK'ya açıktır;
/// başkası görüntülediğinde denetim kaydı yazılır.
/// KVKK: hazırlık oranı yalnızca bilgi amaçlıdır; terfi ya da aday seçimi otomatik yapılmaz.
/// </summary>
[ApiController]
[Route("api/career-paths")]
[Authorize]
public class CareerPathsController : ControllerBase
{
    public const string Notice =
        "Hazırlık oranı yalnızca bilgi amaçlıdır: terfi, atama ya da aday seçimi otomatik yapılmaz. Kararı çalışan, yöneticisi ve İK birlikte verir.";

    private readonly LearningDbContext _db;
    private readonly LearningDirectory _dir;

    public CareerPathsController(LearningDbContext db, LearningDirectory dir) { _db = db; _dir = dir; }

    private bool IsHr => User.IsHr();

    private Task<List<CareerPath>> LoadAsync(bool includeInactive, CancellationToken ct)
    {
        var q = _db.CareerPaths.AsNoTracking().Include(p => p.Steps).ThenInclude(s => s.Requirements).AsQueryable();
        if (!includeInactive) q = q.Where(p => p.IsActive);
        return q.OrderBy(p => p.Name).AsSplitQuery().ToListAsync(ct);
    }

    private static object View(CareerPath p, IReadOnlyDictionary<Guid, Competency> comps) => new
    {
        p.Id, p.Name, p.Description, p.IsActive, p.UpdatedAt,
        steps = p.Steps.OrderBy(s => s.StepOrder).Select(s => new
        {
            s.Id, s.StepOrder, s.PositionTitle, s.Description, s.MinMonths,
            requirements = s.Requirements.Where(r => comps.ContainsKey(r.CompetencyId))
                .OrderBy(r => comps[r.CompetencyId].Name)
                .Select(r => new { r.CompetencyId, competency = comps[r.CompetencyId].Name, isActive = comps[r.CompetencyId].IsActive, r.RequiredLevel }),
        }),
    };

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] bool includeInactive = false, CancellationToken ct = default)
    {
        var paths = await LoadAsync(includeInactive && IsHr, ct);
        var comps = await _db.Competencies.AsNoTracking().ToDictionaryAsync(c => c.Id, ct);
        return Ok(paths.Select(p => View(p, comps)));
    }

    public record PathInput(string? Name, string? Description, bool? IsActive, List<CareerStepInput>? Steps);

    private async Task<string?> ValidateAsync(PathInput b, CancellationToken ct)
    {
        var known = (await _db.Competencies.AsNoTracking().Select(c => c.Id).ToListAsync(ct)).ToHashSet();
        return CareerMath.Validate(b.Name, b.Description, b.Steps, known);
    }

    private static void Fill(CareerPath p, PathInput b)
    {
        p.Name = b.Name!.Trim();
        p.Description = string.IsNullOrWhiteSpace(b.Description) ? null : b.Description.Trim();
        if (b.IsActive is not null) p.IsActive = b.IsActive.Value;
        p.UpdatedAt = DateTimeOffset.UtcNow;
        p.Steps = b.Steps!.Select((s, i) => new CareerStep
        {
            PathId = p.Id, StepOrder = i + 1, PositionTitle = s.PositionTitle!.Trim(),
            Description = string.IsNullOrWhiteSpace(s.Description) ? null : s.Description.Trim(), MinMonths = s.MinMonths,
            Requirements = (s.Requirements ?? new()).Select(r => new CareerStepRequirement { CompetencyId = r.CompetencyId, RequiredLevel = r.RequiredLevel }).ToList(),
        }).ToList();
        foreach (var s in p.Steps) foreach (var r in s.Requirements) r.StepId = s.Id;
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] PathInput body, CancellationToken ct)
    {
        if (!IsHr) return Forbid();
        var err = await ValidateAsync(body, ct);
        if (err is not null) return BadRequest(new { message = err });
        var name = body.Name!.Trim();
        if (await _db.CareerPaths.AnyAsync(p => p.Name.ToLower() == name.ToLower(), ct))
            return Conflict(new { message = "Bu adla bir kariyer yolu zaten var" });
        var p = new CareerPath { Name = name };
        Fill(p, body);
        _db.CareerPaths.Add(p);
        await _db.SaveChangesAsync(ct);
        var comps = await _db.Competencies.AsNoTracking().ToDictionaryAsync(c => c.Id, ct);
        return Ok(View(p, comps));
    }

    /// <summary>Yolu basamaklarıyla birlikte değiştirir (basamaklar ve beklentiler yeniden yazılır).</summary>
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] PathInput body, CancellationToken ct)
    {
        if (!IsHr) return Forbid();
        var err = await ValidateAsync(body, ct);
        if (err is not null) return BadRequest(new { message = err });
        var p = await _db.CareerPaths.Include(x => x.Steps).ThenInclude(s => s.Requirements).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return NotFound(new { message = "Kariyer yolu bulunamadı" });
        var name = body.Name!.Trim();
        if (await _db.CareerPaths.AnyAsync(x => x.Id != id && x.Name.ToLower() == name.ToLower(), ct))
            return Conflict(new { message = "Bu adla bir kariyer yolu zaten var" });
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        _db.CareerSteps.RemoveRange(p.Steps);
        await _db.SaveChangesAsync(ct);
        Fill(p, body);
        foreach (var s in p.Steps) _db.CareerSteps.Add(s);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        var comps = await _db.Competencies.AsNoTracking().ToDictionaryAsync(c => c.Id, ct);
        return Ok(View(p, comps));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!IsHr) return Forbid();
        var p = await _db.CareerPaths.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return NotFound();
        _db.CareerPaths.Remove(p); // basamaklar ve beklentiler FK ile silinir
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    /* ------------------------------------------------------------ ilerleme */

    private async Task<(PersonRow? Target, PersonRow? Me, IActionResult? Error)> TargetAsync(string employee, CancellationToken ct)
    {
        var me = await _dir.MeAsync(ct);
        Guid id;
        if (employee == "me")
        {
            if (me is null) return (null, null, NotFound(new { message = "Bu hesaba bağlı çalışan kaydı bulunamadı" }));
            id = me.Id;
        }
        else if (!Guid.TryParse(employee, out id)) return (null, null, NotFound());
        var target = await _dir.FindAsync(id, ct);
        if (target is null || !LearningDirectory.CanSee(target, me?.Id, IsHr)) return (null, null, NotFound());
        return (target, me, null);
    }

    /// <summary>
    /// Kişinin kariyer yollarındaki yeri: güncel unvanıyla eşleşen yollar (güncel basamak + sonraki basamağın
    /// açığı ve hazırlık), istenirse belirli bir yol/basamak (pathId, stepId). Sonraki basamağın açığını kapatan
    /// eğitimler mevcut yetkinlik önerisiyle (CompetencyMath.Recommend) hesaplanır — yalnızca öneri.
    /// </summary>
    [HttpGet("progress/{employee}")]
    public async Task<IActionResult> Progress(string employee, [FromQuery] Guid? pathId, [FromQuery] Guid? stepId, CancellationToken ct)
    {
        var (target, me, error) = await TargetAsync(employee, ct);
        if (error is not null) return error;
        var paths = await LoadAsync(false, ct);
        var comps = await _db.Competencies.AsNoTracking().ToDictionaryAsync(c => c.Id, ct);
        var active = comps.Values.Where(c => c.IsActive).Select(c => c.Id).ToHashSet();
        var latest = CompetencyMath.Latest(await _db.Assessments.AsNoTracking().Where(a => a.EmployeeId == target!.Id).ToListAsync(ct));
        var current = latest.ToDictionary(kv => kv.Key, kv => kv.Value.Level);

        var courses = await _db.Courses.AsNoTracking().Where(c => c.IsActive).ToDictionaryAsync(c => c.Id, ct);
        var tags = (await _db.CourseCompetencies.AsNoTracking().ToListAsync(ct)).Where(t => courses.ContainsKey(t.CourseId)).ToList();
        var enrollments = await _db.Enrollments.AsNoTracking().Where(e => e.EmployeeId == target!.Id).ToDictionaryAsync(e => e.CourseId, ct);

        object StepView(CareerStep s, bool withCourses)
        {
            var gaps = CareerMath.StepGaps(s, current, active);
            return new
            {
                s.Id, s.StepOrder, s.PositionTitle, s.Description, s.MinMonths,
                readiness = CareerMath.Readiness(gaps),
                totalGap = gaps.Sum(g => g.Gap),
                items = gaps.Select(g => new { g.CompetencyId, competency = comps[g.CompetencyId].Name, g.Required, g.Current, g.Gap }),
                courses = !withCourses ? null : CompetencyMath.Recommend(gaps, tags).Take(8).Select(r => new
                {
                    r.CourseId, title = courses[r.CourseId].Title, isMandatory = courses[r.CourseId].IsMandatory,
                    durationHours = courses[r.CourseId].DurationHours,
                    enrollmentStatus = enrollments.TryGetValue(r.CourseId, out var e) ? e.Status.ToString() : null,
                    closes = r.Closes.Select(c => new { c.CompetencyId, competency = comps[c.CompetencyId].Name, c.From, c.To }),
                }),
            };
        }

        var result = new List<object>();
        foreach (var p in paths)
        {
            var ordered = p.Steps.OrderBy(s => s.StepOrder).ToList();
            var idx = CareerMath.CurrentIndex(ordered, target!.PositionTitle);
            var selected = p.Id == pathId && stepId is not null ? ordered.FirstOrDefault(s => s.Id == stepId) : null;
            if (idx is null && selected is null && p.Id != pathId) continue;
            var next = selected ?? (idx is { } i ? (i + 1 < ordered.Count ? ordered[i + 1] : null) : ordered.FirstOrDefault());
            result.Add(new
            {
                pathId = p.Id, name = p.Name, description = p.Description,
                currentStepId = idx is { } j ? ordered[j].Id : (Guid?)null,
                currentStep = idx is { } k ? StepView(ordered[k], false) : null,
                target = next is null ? null : StepView(next, true),
                steps = ordered.Select(s => new { s.Id, s.StepOrder, s.PositionTitle, readiness = CareerMath.Readiness(CareerMath.StepGaps(s, current, active)) }),
            });
        }
        if (me?.Id != target!.Id)
            await _dir.AuditAsync("CareerProgress", target.Id.ToString(), "SensitiveViewed", new { field = "careerReadiness", paths = result.Count });
        return Ok(new
        {
            notice = Notice,
            employeeId = target.Id, name = target.FullName, positionTitle = target.PositionTitle,
            matched = result,
            available = paths.Select(p => new { p.Id, p.Name }),
        });
    }
}

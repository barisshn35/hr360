using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LearningService.Data;
using LearningService.Models;
using LearningService.Services;

namespace LearningService.Controllers;

/// <summary>
/// Y19 yetkinlik matrisi: yetkinlik tanımları, rol profilleri (pozisyon/departman için beklenen
/// seviye), değerlendirmeler (öz/yönetici/İK — son kayıt geçerli), açık analizi ve eğitim önerisi.
/// Görünürlük: çalışan kendini, yönetici başı olduğu departmanı, İK herkesi görür.
/// KVKK: öneri yalnızca bir öneridir; otomatik kayıt ya da kişi hakkında otomatik karar yoktur.
/// </summary>
[ApiController]
[Route("api/competencies")]
[Authorize]
public class CompetenciesController : ControllerBase
{
    public const string RecommendationNotice =
        "Bu liste yalnızca bir öneridir: otomatik eğitim kaydı yapılmaz ve kişi hakkında otomatik karar verilmez. Kararı çalışan ve yöneticisi birlikte verir.";

    private readonly LearningDbContext _db;
    private readonly LearningDirectory _dir;

    public CompetenciesController(LearningDbContext db, LearningDirectory dir) { _db = db; _dir = dir; }

    private bool IsHr => User.IsHr();

    /* ------------------------------------------------------------ tanımlar */

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] bool includeInactive = false, CancellationToken ct = default)
    {
        var q = _db.Competencies.AsNoTracking();
        if (!includeInactive || !IsHr) q = q.Where(c => c.IsActive);
        return Ok(await q.OrderBy(c => c.Category).ThenBy(c => c.Name).ToListAsync(ct));
    }

    public record CompetencyInput(string Name, string? Description, string? Category, bool? IsActive);

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CompetencyInput body, CancellationToken ct)
    {
        if (!IsHr) return Forbid();
        var err = Validate(body);
        if (err is not null) return BadRequest(new { message = err });
        var name = body.Name.Trim();
        if (await _db.Competencies.AnyAsync(c => c.Name.ToLower() == name.ToLower(), ct))
            return Conflict(new { message = "Bu adla bir yetkinlik zaten var" });
        var c = new Competency { Name = name, Description = Clean(body.Description), Category = Clean(body.Category), IsActive = body.IsActive ?? true };
        _db.Competencies.Add(c);
        await _db.SaveChangesAsync(ct);
        return Ok(c);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] CompetencyInput body, CancellationToken ct)
    {
        if (!IsHr) return Forbid();
        var err = Validate(body);
        if (err is not null) return BadRequest(new { message = err });
        var c = await _db.Competencies.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();
        var name = body.Name.Trim();
        if (await _db.Competencies.AnyAsync(x => x.Id != id && x.Name.ToLower() == name.ToLower(), ct))
            return Conflict(new { message = "Bu adla bir yetkinlik zaten var" });
        c.Name = name; c.Description = Clean(body.Description); c.Category = Clean(body.Category);
        if (body.IsActive is not null) c.IsActive = body.IsActive.Value;
        await _db.SaveChangesAsync(ct);
        return Ok(c);
    }

    /// <summary>Kullanılmış (değerlendirilmiş) yetkinlik silinmez, pasifleştirilir — geçmiş kaybolmasın.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!IsHr) return Forbid();
        var c = await _db.Competencies.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();
        if (await _db.Assessments.AnyAsync(a => a.CompetencyId == id, ct))
        {
            c.IsActive = false;
            await _db.SaveChangesAsync(ct);
            return Ok(new { deactivated = true });
        }
        _db.Competencies.Remove(c);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static string? Validate(CompetencyInput b)
    {
        if (string.IsNullOrWhiteSpace(b.Name) || b.Name.Trim().Length < 2 || b.Name.Length > 150)
            return "Yetkinlik adı 2-150 karakter olmalı";
        if (b.Description is { Length: > 1000 }) return "Açıklama en fazla 1000 karakter olabilir";
        if (b.Category is { Length: > 80 }) return "Kategori en fazla 80 karakter olabilir";
        return null;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /* ------------------------------------------------------------ rol profilleri */

    [HttpGet("role-profiles")]
    public async Task<IActionResult> RoleProfiles(CancellationToken ct) =>
        Ok(await _db.RoleProfiles.AsNoTracking().OrderBy(r => r.PositionTitle).ThenBy(r => r.DepartmentId).ToListAsync(ct));

    public record RoleProfileInput(Guid CompetencyId, string? PositionTitle, Guid? DepartmentId, int RequiredLevel);

    /// <summary>Hedef (pozisyon ya da departman) + yetkinlik için beklenen seviyeyi ekler/günceller.</summary>
    [HttpPut("role-profiles")]
    public async Task<IActionResult> UpsertRoleProfile([FromBody] RoleProfileInput body, CancellationToken ct)
    {
        if (!IsHr) return Forbid();
        var title = Clean(body.PositionTitle);
        if ((title is null) == (body.DepartmentId is null))
            return BadRequest(new { message = "Pozisyon unvanı YA DA departman seçin (ikisi birden değil)" });
        if (title is { Length: > 150 }) return BadRequest(new { message = "Unvan en fazla 150 karakter olabilir" });
        if (body.RequiredLevel is < 1 or > 5) return BadRequest(new { message = "Beklenen seviye 1-5 arasında olmalı" });
        if (!await _db.Competencies.AnyAsync(c => c.Id == body.CompetencyId, ct)) return BadRequest(new { message = "Yetkinlik bulunamadı" });
        if (body.DepartmentId is not null && !(await _dir.DepartmentsAsync(ct)).Any(d => d.Id == body.DepartmentId))
            return BadRequest(new { message = "Departman bulunamadı" });

        var all = await _db.RoleProfiles.Where(r => r.CompetencyId == body.CompetencyId).ToListAsync(ct);
        var row = all.FirstOrDefault(r => body.DepartmentId is not null
            ? r.DepartmentId == body.DepartmentId
            : r.PositionTitle is not null && CompetencyMath.Normalize(r.PositionTitle) == CompetencyMath.Normalize(title));
        if (row is null)
        {
            row = new RoleProfileEntry { CompetencyId = body.CompetencyId, PositionTitle = title, DepartmentId = body.DepartmentId };
            _db.RoleProfiles.Add(row);
        }
        row.RequiredLevel = body.RequiredLevel;
        await _db.SaveChangesAsync(ct);
        return Ok(row);
    }

    [HttpDelete("role-profiles/{id:guid}")]
    public async Task<IActionResult> DeleteRoleProfile(Guid id, CancellationToken ct)
    {
        if (!IsHr) return Forbid();
        var r = await _db.RoleProfiles.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return NotFound();
        _db.RoleProfiles.Remove(r);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Rol profili ekranı için departmanlar ve kullanılan pozisyon unvanları (İK).</summary>
    [HttpGet("org-options")]
    public async Task<IActionResult> OrgOptions(CancellationToken ct)
    {
        if (!IsHr) return Forbid();
        var people = await _dir.ActiveAsync(ct);
        var depts = await _dir.DepartmentsAsync(ct);
        return Ok(new
        {
            departments = depts.Select(d => new { d.Id, d.Name }),
            positions = people.Select(p => p.PositionTitle).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t),
        });
    }

    /* ------------------------------------------------------------ değerlendirme */

    public record AssessmentInput(Guid EmployeeId, Guid CompetencyId, int Level, string? Note);

    /// <summary>
    /// Öz değerlendirme (kendisi), yönetici değerlendirmesi (çalışanın departman başkanı) ya da İK.
    /// Kaynak çağırandan belirlenir; istemci seçemez.
    /// </summary>
    [HttpPost("assessments")]
    public async Task<IActionResult> Assess([FromBody] AssessmentInput body, CancellationToken ct)
    {
        if (body.Level is < 1 or > 5) return BadRequest(new { message = "Seviye 1-5 arasında olmalı" });
        if (body.Note is { Length: > 500 }) return BadRequest(new { message = "Not en fazla 500 karakter olabilir" });
        var comp = await _db.Competencies.FirstOrDefaultAsync(c => c.Id == body.CompetencyId, ct);
        if (comp is null || !comp.IsActive) return BadRequest(new { message = "Yetkinlik bulunamadı" });
        var target = await _dir.FindAsync(body.EmployeeId, ct);
        if (target is null) return NotFound(new { message = "Çalışan bulunamadı" });
        var me = await _dir.MeAsync(ct);

        string source;
        if (me is not null && me.Id == target.Id) source = AssessmentSource.Self;
        else if (me is not null && target.HeadId == me.Id) source = AssessmentSource.Manager;
        else if (IsHr) source = AssessmentSource.Hr;
        else return StatusCode(403, new { message = "Yalnızca kendinizi ya da departmanınızdaki çalışanları değerlendirebilirsiniz" });

        var a = new CompetencyAssessment
        {
            EmployeeId = target.Id, CompetencyId = comp.Id, Level = body.Level, Source = source,
            AssessedByEmployeeId = me?.Id, AssessedByName = me?.FullName ?? User.DisplayName(),
            Note = Clean(body.Note), AssessedAt = DateTimeOffset.UtcNow,
        };
        _db.Assessments.Add(a);
        await _db.SaveChangesAsync(ct);
        return Ok(a);
    }

    [HttpGet("assessments")]
    public async Task<IActionResult> Assessments([FromQuery] Guid employeeId, CancellationToken ct)
    {
        var target = await _dir.FindAsync(employeeId, ct);
        if (target is null) return NotFound();
        var me = await _dir.MeAsync(ct);
        if (!LearningDirectory.CanSee(target, me?.Id, IsHr)) return NotFound();
        return Ok(await _db.Assessments.AsNoTracking().Where(a => a.EmployeeId == employeeId)
            .OrderByDescending(a => a.AssessedAt).Take(500).ToListAsync(ct));
    }

    /* ------------------------------------------------------------ açık analizi */

    private async Task<object> GapViewAsync(PersonRow p, List<Competency> comps, List<RoleProfileEntry> profiles, CancellationToken ct)
    {
        var req = CompetencyMath.Requirements(profiles, p.PositionTitle, p.DepartmentId);
        var latest = CompetencyMath.Latest(await _db.Assessments.AsNoTracking().Where(a => a.EmployeeId == p.Id).ToListAsync(ct));
        var activeIds = comps.Select(c => c.Id).ToHashSet();
        foreach (var k in req.Keys.Where(k => !activeIds.Contains(k)).ToList()) req.Remove(k);
        var gaps = CompetencyMath.Gaps(req, latest.ToDictionary(kv => kv.Key, kv => kv.Value.Level));
        var names = comps.ToDictionary(c => c.Id);
        return new
        {
            employeeId = p.Id,
            name = p.FullName,
            positionTitle = p.PositionTitle,
            departmentId = p.DepartmentId,
            department = p.DepartmentName,
            totalGap = gaps.Sum(g => g.Gap),
            items = gaps.Select(g => new
            {
                competencyId = g.CompetencyId,
                competency = names[g.CompetencyId].Name,
                category = names[g.CompetencyId].Category,
                required = g.Required,
                current = g.Current,
                gap = g.Gap,
                basis = g.Basis,
                assessedAt = latest.TryGetValue(g.CompetencyId, out var la) ? la.AssessedAt : (DateTimeOffset?)null,
                source = latest.TryGetValue(g.CompetencyId, out var lb) ? lb.Source : null,
                assessedBy = latest.TryGetValue(g.CompetencyId, out var lc) ? lc.AssessedByName : null,
            }),
            // Rol profilinde olmayan ama değerlendirilmiş yetkinlikler (bilgi amaçlı).
            extra = latest.Where(kv => !req.ContainsKey(kv.Key) && names.ContainsKey(kv.Key)).Select(kv => new
            {
                competencyId = kv.Key, competency = names[kv.Key].Name, current = kv.Value.Level, kv.Value.AssessedAt, kv.Value.Source,
            }),
        };
    }

    /// <summary>Bir çalışanın açık analizi (kendisi, departman başkanı, İK). "me" kendi kaydı.</summary>
    [HttpGet("gaps/{employee}")]
    public async Task<IActionResult> Gaps(string employee, CancellationToken ct)
    {
        var me = await _dir.MeAsync(ct);
        Guid id;
        if (employee == "me")
        {
            if (me is null) return NotFound(new { message = "Bu hesaba bağlı çalışan kaydı bulunamadı" });
            id = me.Id;
        }
        else if (!Guid.TryParse(employee, out id)) return NotFound();
        var target = await _dir.FindAsync(id, ct);
        if (target is null || !LearningDirectory.CanSee(target, me?.Id, IsHr)) return NotFound();
        var comps = await _db.Competencies.AsNoTracking().Where(c => c.IsActive).ToListAsync(ct);
        var profiles = await _db.RoleProfiles.AsNoTracking().ToListAsync(ct);
        if (me?.Id != target.Id)
            await _dir.AuditAsync("CompetencyGap", target.Id.ToString(), "SensitiveViewed", new { field = "competencyGaps" });
        return Ok(await GapViewAsync(target, comps, profiles, ct));
    }

    /// <summary>
    /// Ekip ısı haritası: satır çalışan, sütun yetkinlik, hücre beklenen/güncel/açık. Yönetici yalnızca
    /// başı olduğu departman(lar)ı; İK istediği departmanı ya da (boşsa) herkesi görür.
    /// </summary>
    [HttpGet("team")]
    public async Task<IActionResult> Team([FromQuery] Guid? departmentId, CancellationToken ct)
    {
        var me = await _dir.MeAsync(ct);
        var depts = await _dir.DepartmentsAsync(ct);
        List<Guid> allowed;
        if (IsHr) allowed = departmentId is null ? depts.Select(d => d.Id).ToList() : new() { departmentId.Value };
        else
        {
            var headed = me is null ? new List<Guid>() : depts.Where(d => d.HeadEmployeeId == me.Id).Select(d => d.Id).ToList();
            if (headed.Count == 0) return StatusCode(403, new { message = "Ekip görünümü departman yöneticilerine ve İK'ya açık" });
            if (departmentId is not null && !headed.Contains(departmentId.Value))
                return StatusCode(403, new { message = "Yalnızca yönettiğiniz departmanı görebilirsiniz" });
            allowed = departmentId is null ? headed : new() { departmentId.Value };
        }
        var people = (await _dir.ActiveAsync(ct))
            .Where(p => p.DepartmentId is not null && allowed.Contains(p.DepartmentId.Value))
            .Where(p => IsHr || p.Id != me?.Id)
            .OrderBy(p => p.DepartmentName).ThenBy(p => p.LastName).ToList();
        var comps = await _db.Competencies.AsNoTracking().Where(c => c.IsActive).ToListAsync(ct);
        var profiles = await _db.RoleProfiles.AsNoTracking().ToListAsync(ct);
        var ids = people.Select(p => p.Id).ToList();
        var assessments = await _db.Assessments.AsNoTracking().Where(a => ids.Contains(a.EmployeeId)).ToListAsync(ct);
        var byEmp = assessments.GroupBy(a => a.EmployeeId).ToDictionary(g => g.Key, g => CompetencyMath.Latest(g));
        var activeIds = comps.Select(c => c.Id).ToHashSet();

        var rows = people.Select(p =>
        {
            var req = CompetencyMath.Requirements(profiles, p.PositionTitle, p.DepartmentId);
            foreach (var k in req.Keys.Where(k => !activeIds.Contains(k)).ToList()) req.Remove(k);
            var latest = byEmp.TryGetValue(p.Id, out var l) ? l : new Dictionary<Guid, CompetencyAssessment>();
            var gaps = CompetencyMath.Gaps(req, latest.ToDictionary(kv => kv.Key, kv => kv.Value.Level));
            return new
            {
                employeeId = p.Id, name = p.FullName, positionTitle = p.PositionTitle, department = p.DepartmentName,
                totalGap = gaps.Sum(g => g.Gap),
                cells = gaps.Select(g => new { competencyId = g.CompetencyId, g.Required, g.Current, g.Gap }),
            };
        }).ToList();
        var usedComps = rows.SelectMany(r => r.cells.Select(c => c.competencyId)).ToHashSet();
        await _dir.AuditAsync("CompetencyTeam", string.Join(',', allowed.Take(5)), "SensitiveViewed", new { field = "competencyHeatmap", employees = rows.Count });
        return Ok(new
        {
            departments = depts.Where(d => IsHr || allowed.Contains(d.Id)).Select(d => new { d.Id, d.Name }),
            competencies = comps.Where(c => usedComps.Contains(c.Id)).OrderBy(c => c.Category).ThenBy(c => c.Name)
                .Select(c => new { c.Id, c.Name, c.Category }),
            rows,
        });
    }

    /* ------------------------------------------------------------ öneri */

    /// <summary>
    /// Açığı kapatan eğitimler, kapattığı açık büyüklüğüne göre sıralı. YALNIZCA ÖNERİ: kayıt açılmaz,
    /// otomatik karar verilmez (yanıtta "notice" alanı arayüzde gösterilir).
    /// </summary>
    [HttpGet("recommendations/{employee}")]
    public async Task<IActionResult> Recommendations(string employee, CancellationToken ct)
    {
        var me = await _dir.MeAsync(ct);
        Guid id;
        if (employee == "me")
        {
            if (me is null) return NotFound(new { message = "Bu hesaba bağlı çalışan kaydı bulunamadı" });
            id = me.Id;
        }
        else if (!Guid.TryParse(employee, out id)) return NotFound();
        var target = await _dir.FindAsync(id, ct);
        if (target is null || !LearningDirectory.CanSee(target, me?.Id, IsHr)) return NotFound();

        var comps = await _db.Competencies.AsNoTracking().Where(c => c.IsActive).ToListAsync(ct);
        var activeIds = comps.Select(c => c.Id).ToHashSet();
        var req = CompetencyMath.Requirements(await _db.RoleProfiles.AsNoTracking().ToListAsync(ct), target.PositionTitle, target.DepartmentId);
        foreach (var k in req.Keys.Where(k => !activeIds.Contains(k)).ToList()) req.Remove(k);
        var latest = CompetencyMath.Latest(await _db.Assessments.AsNoTracking().Where(a => a.EmployeeId == id).ToListAsync(ct));
        var gaps = CompetencyMath.Gaps(req, latest.ToDictionary(kv => kv.Key, kv => kv.Value.Level));

        var courses = await _db.Courses.AsNoTracking().Where(c => c.IsActive).ToDictionaryAsync(c => c.Id, ct);
        var tags = (await _db.CourseCompetencies.AsNoTracking().ToListAsync(ct)).Where(t => courses.ContainsKey(t.CourseId));
        var recs = CompetencyMath.Recommend(gaps, tags);
        var enrollments = await _db.Enrollments.AsNoTracking().Where(e => e.EmployeeId == id).ToDictionaryAsync(e => e.CourseId, ct);
        var names = comps.ToDictionary(c => c.Id, c => c.Name);

        return Ok(new
        {
            notice = RecommendationNotice,
            employeeId = id,
            items = recs.Select(r => new
            {
                courseId = r.CourseId,
                title = courses[r.CourseId].Title,
                category = courses[r.CourseId].Category.ToString(),
                durationHours = courses[r.CourseId].DurationHours,
                isMandatory = courses[r.CourseId].IsMandatory,
                maxGap = r.MaxGap,
                totalClosed = r.TotalClosed,
                enrollmentStatus = enrollments.TryGetValue(r.CourseId, out var e) ? e.Status.ToString() : null,
                closes = r.Closes.Select(c => new { competencyId = c.CompetencyId, competency = names[c.CompetencyId], c.Gap, c.From, c.To }),
            }),
        });
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TimeShiftService.Data;
using TimeShiftService.Models;
using TimeShiftService.Services;
using TimeShiftService.Tenancy;

namespace TimeShiftService.Controllers;

/// <summary>
/// G6: vardiya tercihleri. Çalışan kendi tercihini girer; planlayıcı (yönetici/İK) atama yaparken
/// görür ve bir atamanın tercihlerle uyuşmazlığını <c>POST check</c> ile denetler. Tercih bağlayıcı
/// değildir — uyuşmazlık uyarı olarak döner.
/// </summary>
[ApiController]
[Route("api/shift-preferences")]
[Authorize]
public class ShiftPreferencesController : ControllerBase
{
    private readonly TimeShiftDbContext _db;
    private readonly EmployeeDirectoryClient _employees;
    private readonly ITenantContext _tenant;

    public ShiftPreferencesController(TimeShiftDbContext db, EmployeeDirectoryClient employees, ITenantContext tenant)
    {
        _db = db; _employees = employees; _tenant = tenant;
    }

    private bool IsPlanner => User.IsInRole("manager") || User.IsInRole("hr-admin") || User.IsInRole("tenant-admin")
        || User.IsInRole("platform-admin") || User.IsInRole("ext-timeshift-manage");

    private static readonly string[] ShiftTypes = { "Day", "Night" };

    public record PreferenceInput(int[]? PreferredDays, int[]? UnavailableDays, string[]? PreferredShiftTypes,
        string[]? AvoidShiftTypes, int? MaxNightsPerWeek, string? Note);

    private static object Dto(ShiftPreference? p, Guid employeeId) => p is null
        ? new { employeeId, preferredDays = Array.Empty<int>(), unavailableDays = Array.Empty<int>(), preferredShiftTypes = Array.Empty<string>(),
                avoidShiftTypes = Array.Empty<string>(), maxNightsPerWeek = (int?)null, note = (string?)null, updatedAt = (DateTimeOffset?)null }
        : new { employeeId = p.EmployeeId, preferredDays = p.PreferredDays, unavailableDays = p.UnavailableDays, preferredShiftTypes = p.PreferredShiftTypes,
                avoidShiftTypes = p.AvoidShiftTypes, maxNightsPerWeek = p.MaxNightsPerWeek, note = p.Note, updatedAt = (DateTimeOffset?)p.UpdatedAt };

    [HttpGet("me")]
    public async Task<IActionResult> Mine(CancellationToken ct)
    {
        var me = await _employees.FindMyEmployeeIdAsync(ct);
        if (me is null) return NotFound(new { message = "Çalışan kaydınız bulunamadı" });
        var p = await _db.ShiftPreferences.AsNoTracking().FirstOrDefaultAsync(x => x.EmployeeId == me, ct);
        return Ok(Dto(p, me.Value));
    }

    [HttpPut("me")]
    public async Task<IActionResult> SaveMine([FromBody] PreferenceInput b, CancellationToken ct)
    {
        var me = await _employees.FindMyEmployeeIdAsync(ct);
        if (me is null) return NotFound(new { message = "Çalışan kaydınız bulunamadı" });
        var pref = (b.PreferredDays ?? Array.Empty<int>()).Distinct().OrderBy(x => x).ToArray();
        var unav = (b.UnavailableDays ?? Array.Empty<int>()).Distinct().OrderBy(x => x).ToArray();
        if (pref.Concat(unav).Any(d => d is < 1 or > 7)) return BadRequest(new { message = "Gün numarası 1 (Pazartesi) ile 7 (Pazar) arasında olmalı" });
        if (pref.Intersect(unav).Any()) return BadRequest(new { message = "Bir gün hem tercih edilen hem müsait olmayan olamaz" });
        var ptypes = (b.PreferredShiftTypes ?? Array.Empty<string>()).Distinct().ToArray();
        var atypes = (b.AvoidShiftTypes ?? Array.Empty<string>()).Distinct().ToArray();
        if (ptypes.Concat(atypes).Any(t => !ShiftTypes.Contains(t))) return BadRequest(new { message = "Vardiya türü Day ya da Night olmalı" });
        if (ptypes.Intersect(atypes).Any()) return BadRequest(new { message = "Bir vardiya türü hem tercih edilip hem istenmeyemez" });
        if (b.MaxNightsPerWeek is < 0 or > 7) return BadRequest(new { message = "Haftalık gece vardiyası 0-7 arasında olmalı" });
        if (b.Note is { Length: > 300 }) return BadRequest(new { message = "Not en fazla 300 karakter olabilir" });

        var p = await _db.ShiftPreferences.FirstOrDefaultAsync(x => x.EmployeeId == me, ct);
        if (p is null) { p = new ShiftPreference { EmployeeId = me.Value }; _db.ShiftPreferences.Add(p); }
        p.PreferredDays = pref;
        p.UnavailableDays = unav;
        p.PreferredShiftTypes = ptypes;
        p.AvoidShiftTypes = atypes;
        p.MaxNightsPerWeek = b.MaxNightsPerWeek;
        p.Note = string.IsNullOrWhiteSpace(b.Note) ? null : b.Note.Trim();
        p.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(Dto(p, me.Value));
    }

    /// <summary>Planlayıcı: tercihler (employeeIds verilmezse tümü).</summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? employeeIds, CancellationToken ct)
    {
        if (!IsPlanner) return Forbid();
        var q = _db.ShiftPreferences.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(employeeIds))
        {
            var ids = employeeIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => Guid.TryParse(x, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList();
            q = q.Where(p => ids.Contains(p.EmployeeId));
        }
        return Ok((await q.ToListAsync(ct)).Select(p => Dto(p, p.EmployeeId)));
    }

    public record CheckInput(Guid EmployeeId, Guid ShiftId, DateOnly Date);

    /// <summary>Bir atamanın (çalışan + vardiya + gün) tercihlerle uyuşmazlıkları ve çalışma süresi kural uyarıları.</summary>
    [HttpPost("check")]
    public async Task<IActionResult> Check([FromBody] CheckInput b, CancellationToken ct)
    {
        if (!IsPlanner) return Forbid();
        var shift = await _db.Shifts.AsNoTracking().FirstOrDefaultAsync(s => s.Id == b.ShiftId, ct);
        if (shift is null) return NotFound(new { message = "Vardiya bulunamadı" });
        var pref = await _db.ShiftPreferences.AsNoTracking().FirstOrDefaultAsync(p => p.EmployeeId == b.EmployeeId, ct);
        var monday = b.Date.AddDays(1 - PreferenceRules.IsoDay(b.Date));
        var week = await _db.ShiftAssignments.AsNoTracking().Include(a => a.Shift)
            .Where(a => a.EmployeeId == b.EmployeeId && a.Date >= monday && a.Date <= monday.AddDays(6) && a.Date != b.Date)
            .ToListAsync(ct);
        var nights = week.Count(a => a.Shift is not null && PreferenceRules.ShiftType(a.Shift.StartTime, a.Shift.EndTime, a.Shift.IsNightShift) == "Night");
        var conflicts = PreferenceRules.Check(pref, b.Date, shift.StartTime, shift.EndTime, shift.IsNightShift, nights);
        // Madde 66: çalışma süresi kuralları (uyarı; atamayı engellemez).
        var rules = await WorkRuleCheck.ForAssignmentAsync(_db, b.EmployeeId, b.Date, shift.StartTime, shift.EndTime, shift.BreakMinutes,
            await WorkRuleCheck.RulesAsync(_db, ct), ct);
        return Ok(new
        {
            conflicts = conflicts.Select(c => new { c.Code, c.Message }),
            ruleWarnings = rules.Select(w => new { w.Code, w.Message }),
            shiftType = PreferenceRules.ShiftType(shift.StartTime, shift.EndTime, shift.IsNightShift),
            preference = Dto(pref, b.EmployeeId),
        });
    }
}

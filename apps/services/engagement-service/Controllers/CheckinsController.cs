using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using EngagementService.Data;
using EngagementService.Infrastructure;
using EngagementService.Models;

namespace EngagementService.Controllers;

/* ======================================================================
 * Haftalık check-in (yenilik haritası #9). Çalışan haftada bir kısa not
 * yazar; yöneticisi (departman başı / ekip lideri) ekibinin notlarını ve
 * ruh hâli eğilimini görür, engeli sıradaki 1:1 gündemine ekler.
 * ==================================================================== */
[Route("api/checkins")]
[Authorize]
public class CheckinsController : AppController
{
    private readonly EngagementDbContext _db;
    public CheckinsController(EngagementDbContext db) => _db = db;

    /// <summary>Haftanın pazartesisi (Türkiye günü).</summary>
    public static DateOnly WeekOf(DateTime utc)
    {
        var d = DateOnly.FromDateTime(utc.AddHours(3));
        return d.AddDays(-(((int)d.DayOfWeek + 6) % 7));
    }

    private static object Dto(WeeklyCheckin c, string? name = null) => new
    {
        c.Id, c.EmployeeId, name, c.WeekStart, c.Progress, c.Blockers, c.NextWeek, c.Mood, c.AddedToOneOnOne, c.UpdatedAt,
    };

    [HttpGet("me")]
    public async Task<IActionResult> Mine([FromQuery] int weeks = 8, CancellationToken ct = default)
    {
        var me = await MyPersonAsync(ct);
        if (me is null) return Ok(new { week = WeekOf(DateTime.UtcNow), items = Array.Empty<object>() });
        var since = WeekOf(DateTime.UtcNow).AddDays(-7 * Math.Clamp(weeks, 1, 52));
        var items = await _db.WeeklyCheckins.AsNoTracking().Where(c => c.EmployeeId == me.Id && c.WeekStart >= since).OrderByDescending(c => c.WeekStart).ToListAsync(ct);
        return Ok(new { week = WeekOf(DateTime.UtcNow), items = items.Select(c => Dto(c)) });
    }

    public sealed record CheckinInput(string? Progress, string? Blockers, string? NextWeek, int Mood);

    /// <summary>Bu haftanın notunu yazar ya da günceller.</summary>
    [HttpPut("me")]
    public async Task<IActionResult> Save(CheckinInput body, CancellationToken ct)
    {
        if (body.Mood is < 1 or > 5) return BadRequest(new { message = "Ruh hâli 1-5 arasında olmalı" });
        string? T(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        var (p, b, n) = (T(body.Progress), T(body.Blockers), T(body.NextWeek));
        if (new[] { p, b, n }.Any(x => x is { Length: > 1000 })) return BadRequest(new { message = "Her alan en fazla 1000 karakter olabilir" });
        if (p is null && b is null && n is null) return BadRequest(new { message = "En az bir alanı doldurun" });
        var me = await MyPersonAsync(ct);
        if (me is null) return BadRequest(new { message = "Check-in için çalışan kaydınız olmalı" });
        var week = WeekOf(DateTime.UtcNow);
        var c = await _db.WeeklyCheckins.FirstOrDefaultAsync(x => x.EmployeeId == me.Id && x.WeekStart == week, ct);
        if (c is null) { c = new WeeklyCheckin { EmployeeId = me.Id, WeekStart = week }; _db.WeeklyCheckins.Add(c); }
        (c.Progress, c.Blockers, c.NextWeek, c.Mood, c.UpdatedAt) = (p, b, n, (short)body.Mood, DateTime.UtcNow);
        await _db.SaveChangesAsync(ct);
        return Ok(Dto(c));
    }

    /// <summary>Yönetici: ekibinin son haftaları (kim yazdı, kim yazmadı, ruh hâli eğilimi).</summary>
    [HttpGet("team")]
    public async Task<IActionResult> Team([FromQuery] int weeks = 4, CancellationToken ct = default)
    {
        var me = await MyPersonAsync(ct);
        if (me is null) return Ok(new { weeks = Array.Empty<DateOnly>(), people = Array.Empty<object>() });
        var team = await People.TeamOfAsync(Tenant, me.Id, ct);
        var ids = team.Select(t => t.Id).ToList();
        var thisWeek = WeekOf(DateTime.UtcNow);
        var ws = Enumerable.Range(0, Math.Clamp(weeks, 1, 12)).Select(i => thisWeek.AddDays(-7 * i)).ToList();
        var since = ws.Last();
        var rows = await _db.WeeklyCheckins.AsNoTracking().Where(c => ids.Contains(c.EmployeeId) && c.WeekStart >= since).ToListAsync(ct);
        return Ok(new
        {
            weeks = ws,
            people = team.Select(t => new
            {
                employeeId = t.Id, t.Name, t.Position,
                checkins = ws.Select(w => rows.FirstOrDefault(r => r.EmployeeId == t.Id && r.WeekStart == w)).Select(c => c is null ? null : Dto(c, t.Name)),
            }),
        });
    }

    /// <summary>Yönetici: check-in'deki engeli ilgili çalışanla planlı sıradaki 1:1'in gündemine ekler.</summary>
    [HttpPost("{id:guid}/to-one-on-one")]
    public async Task<IActionResult> ToOneOnOne(Guid id, CancellationToken ct)
    {
        var c = await _db.WeeklyCheckins.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();
        var me = await MyPersonAsync(ct);
        if (me is null || !(await People.TeamOfAsync(Tenant, me.Id, ct)).Any(t => t.Id == c.EmployeeId)) return StatusCode(403, new { message = "Yalnızca ekibinizdeki kişinin check-in'i" });
        if (string.IsNullOrWhiteSpace(c.Blockers)) return BadRequest(new { message = "Bu check-in'de engel yazılmamış" });
        var next = await _db.OneOnOnes.Where(o => o.EmployeeId == c.EmployeeId && o.ManagerUserId == Me.UserId && o.Status == "Planned" && o.ScheduledAt >= DateTime.UtcNow.AddHours(-1))
            .OrderBy(o => o.ScheduledAt).FirstOrDefaultAsync(ct);
        if (next is null) return BadRequest(new { message = "Bu kişiyle planlı bir 1:1 yok; önce 1:1 planlayın" });
        next.Agenda = [.. next.Agenda, new AgendaItem { Text = $"Check-in ({c.WeekStart:dd.MM}) engeli: {c.Blockers}", By = Me.Name }];
        c.AddedToOneOnOne = true;
        await _db.SaveChangesAsync(ct);
        return Ok(new { oneOnOneId = next.Id, scheduledAt = next.ScheduledAt });
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TimeShiftService.Data;
using TimeShiftService.Models;
using TimeShiftService.Services;

namespace TimeShiftService.Controllers;

/// <summary>
/// Madde 72: puantaj dönemi kilidi (ay). İK ayı kapatınca o ayın giriş-çıkış, puantaj düzeltmesi ve fazla
/// mesai talebi/kararı reddedilir; bordro (compensation-service) dönemi hesaplarken kilidi denetler ve
/// kapanmamış puantaj dönemini uyarı olarak bildirir. Onay bekleyen fazla mesai varken dönem kapatılamaz.
/// Kapatma/yeniden açma denetim kaydına (AuditInterceptor) yazılır; yeniden açma gerekçe ister.
/// </summary>
[ApiController]
[Route("api/timesheet-periods")]
[Authorize(Policy = "RequireHrAdmin")]
public class TimesheetPeriodsController : ControllerBase
{
    private readonly TimeShiftDbContext _db;
    public TimesheetPeriodsController(TimeShiftDbContext db) => _db = db;

    private string UserName => User.FindFirst("name")?.Value ?? User.FindFirst("preferred_username")?.Value ?? "";

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int year, CancellationToken ct)
    {
        if (year is < 2000 or > 2100) return BadRequest(new { message = "Geçersiz yıl" });
        var periods = await _db.TimesheetPeriods.AsNoTracking().Where(p => p.Year == year).ToListAsync(ct);
        var from = new DateOnly(year, 1, 1);
        var to = new DateOnly(year, 12, 31);
        var entries = await _db.TimeEntries.AsNoTracking().Where(e => e.Date >= from && e.Date <= to)
            .GroupBy(e => e.Date.Month).Select(g => new { Month = g.Key, Count = g.Count(), MissingOut = g.Count(e => e.ClockIn != null && e.ClockOut == null) })
            .ToListAsync(ct);
        var overtime = await _db.OvertimeRequests.AsNoTracking().Where(o => o.Date >= from && o.Date <= to
                && (o.Status == OvertimeStatus.Approved || o.Status == OvertimeStatus.Pending))
            .GroupBy(o => new { o.Date.Month, o.Status }).Select(g => new { g.Key.Month, g.Key.Status, Hours = g.Sum(o => o.Hours), Count = g.Count() })
            .ToListAsync(ct);
        return Ok(Enumerable.Range(1, 12).Select(m =>
        {
            var p = periods.FirstOrDefault(x => x.Month == m);
            var e = entries.FirstOrDefault(x => x.Month == m);
            return new
            {
                year, month = m, status = p?.Status ?? PeriodLock.Open, closedAt = p?.ClosedAt, closedBy = p?.ClosedBy,
                reopenedAt = p?.ReopenedAt, reopenReason = p?.ReopenReason,
                entries = e?.Count ?? 0, missingOut = e?.MissingOut ?? 0,
                approvedOvertimeHours = overtime.Where(o => o.Month == m && o.Status == OvertimeStatus.Approved).Sum(o => o.Hours),
                pendingOvertime = overtime.Where(o => o.Month == m && o.Status == OvertimeStatus.Pending).Sum(o => o.Count),
            };
        }));
    }

    [HttpPost("{year:int}/{month:int}/close")]
    public async Task<IActionResult> Close(int year, int month, CancellationToken ct)
    {
        if (PeriodLock.ValidateMonth(year, month, ClockCore.WorkDate(DateTimeOffset.UtcNow)) is { } bad) return BadRequest(new { message = bad });
        var from = new DateOnly(year, month, 1);
        var to = from.AddMonths(1).AddDays(-1);
        var pending = await _db.OvertimeRequests.CountAsync(o => o.Date >= from && o.Date <= to && o.Status == OvertimeStatus.Pending, ct);
        if (pending > 0)
            return Conflict(new { message = $"Bu dönemde onay bekleyen {pending} fazla mesai talebi var; önce sonuçlandırın", code = "pending_overtime" });
        var p = await _db.TimesheetPeriods.FirstOrDefaultAsync(x => x.Year == year && x.Month == month, ct);
        if (p is null) { p = new TimesheetPeriod { Year = year, Month = month }; _db.TimesheetPeriods.Add(p); }
        if (p.Status == PeriodLock.Closed) return Conflict(new { message = "Dönem zaten kapalı" });
        p.Status = PeriodLock.Closed;
        p.ClosedAt = DateTimeOffset.UtcNow;
        p.ClosedBy = UserName;
        await _db.SaveChangesAsync(ct);
        var missingOut = await _db.TimeEntries.CountAsync(e => e.Date >= from && e.Date <= to && e.ClockIn != null && e.ClockOut == null, ct);
        return Ok(new { p.Year, p.Month, p.Status, p.ClosedAt, missingOut });
    }

    public record ReopenInput(string? Reason);

    [HttpPost("{year:int}/{month:int}/reopen")]
    public async Task<IActionResult> Reopen(int year, int month, [FromBody] ReopenInput b, CancellationToken ct)
    {
        var reason = b.Reason?.Trim() ?? "";
        if (reason.Length is < 5 or > 300) return BadRequest(new { message = "Yeniden açma gerekçesi 5-300 karakter olmalı" });
        var p = await _db.TimesheetPeriods.FirstOrDefaultAsync(x => x.Year == year && x.Month == month, ct);
        if (p is null || p.Status != PeriodLock.Closed) return Conflict(new { message = "Dönem kapalı değil" });
        p.Status = PeriodLock.Open;
        p.ReopenedAt = DateTimeOffset.UtcNow;
        p.ReopenedBy = UserName;
        p.ReopenReason = reason;
        await _db.SaveChangesAsync(ct);
        return Ok(new { p.Year, p.Month, p.Status, p.ReopenedAt });
    }
}

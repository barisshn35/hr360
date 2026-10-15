using Microsoft.EntityFrameworkCore;
using TimeShiftService.Data;
using TimeShiftService.Models;

namespace TimeShiftService.Services;

/// <summary>
/// Madde 66: bir atamanın (çalışan + gün + vardiya saatleri) çalışma süresi kurallarına göre uyarıları.
/// Çalışanın o günkü mevcut ataması yerine geçeceği varsayılır (atama ucu aynı günü günceller).
/// </summary>
public static class WorkRuleCheck
{
    public static async Task<TimesheetSettings> SettingsAsync(TimeShiftDbContext db, CancellationToken ct) =>
        await db.TimesheetSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new TimesheetSettings();

    public static async Task<WorkRuleSettings> RulesAsync(TimeShiftDbContext db, CancellationToken ct) =>
        WorkRuleSettings.From(await SettingsAsync(db, ct));

    /// <summary>Çalışanın [from, to] aralığındaki atamaları vardiya aralığı olarak.</summary>
    public static async Task<List<ShiftInterval>> ScheduleAsync(TimeShiftDbContext db, Guid employeeId, DateOnly from, DateOnly to,
        DateOnly? replaceDate, CancellationToken ct) =>
        (await db.ShiftAssignments.AsNoTracking().Include(a => a.Shift)
            .Where(a => a.EmployeeId == employeeId && a.Date >= from && a.Date <= to && a.Shift != null)
            .ToListAsync(ct))
        .Where(a => replaceDate is null || a.Date != replaceDate.Value)
        .Select(a => ShiftInterval.Of(a.Id, a.Date, a.Shift!.StartTime, a.Shift.EndTime, a.Shift.BreakMinutes))
        .ToList();

    public static async Task<List<RuleWarning>> ForAssignmentAsync(TimeShiftDbContext db, Guid employeeId, DateOnly date,
        TimeOnly start, TimeOnly end, int breakMinutes, WorkRuleSettings rules, CancellationToken ct, string who = "Çalışan")
    {
        var list = await ScheduleAsync(db, employeeId, date.AddDays(-14), date.AddDays(14), date, ct);
        var n = ShiftInterval.Of(null, date, start, end, breakMinutes);
        list.Add(n);
        return WorkRules.Check(list, new[] { n }, rules, who);
    }
}

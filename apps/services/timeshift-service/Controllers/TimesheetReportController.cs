using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TimeShiftService.Data;
using TimeShiftService.Models;
using TimeShiftService.Services;
using TimeShiftService.Tenancy;

namespace TimeShiftService.Controllers;

/// <summary>
/// G7: puantaj — giriş-çıkış hareketlerinden (TimeClockPunch; yoksa web giriş-çıkış kaydı) ve
/// atanmış vardiyadan (yoksa kiracının varsayılan mesaisi, ör. 09:00–18:00, hafta içi) kişi/gün
/// bazında geç kalma, erken çıkış, çalışılan süre ve olası fazla mesai. BİLGİLENDİRME amaçlıdır:
/// otomatik kesinti/yaptırım yoktur; fazla mesai yalnızca mevcut talep/onay akışıyla (OvertimeController)
/// bordroya girer. Görünürlük: İK herkes, yönetici başı olduğu departman(lar), çalışan yalnızca kendisi.
/// </summary>
[ApiController]
[Route("api/timesheet-report")]
[Authorize]
public class TimesheetReportController : ControllerBase
{
    private const int MaxDays = 31;
    private readonly TimeShiftDbContext _db;
    private readonly EmployeeDirectoryClient _employees;
    private readonly ITenantContext _tenant;

    public TimesheetReportController(TimeShiftDbContext db, EmployeeDirectoryClient employees, ITenantContext tenant)
    {
        _db = db; _employees = employees; _tenant = tenant;
    }

    private string Tenant => _tenant.TenantSlug ?? "";
    private bool IsHr => User.IsInRole("hr-admin") || User.IsInRole("tenant-admin") || User.IsInRole("platform-admin")
        || User.IsInRole("ext-timeshift-manage");

    private async Task<TimesheetSettings> SettingsAsync(CancellationToken ct) =>
        await _db.TimesheetSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new TimesheetSettings();

    [HttpGet("settings")]
    public async Task<IActionResult> GetSettings(CancellationToken ct)
    {
        var s = await SettingsAsync(ct);
        return Ok(SettingsDto(s));
    }

    private static object SettingsDto(TimesheetSettings s) => new
    {
        s.LateGraceMinutes, s.DefaultStart, s.DefaultEnd, s.DefaultBreakMinutes,
        s.MinRestHours, s.WeeklyMaxHours, s.DailyMaxHours, s.NightMaxHours, s.MaxConsecutiveDays,
        s.GeoOutsidePolicy, s.GeoStoreRaw, s.GeoRawRetentionDays,
    };

    /// <summary>Dalga 9 alanları isteğe bağlıdır: verilmezse mevcut değer korunur (eski istemciler bozulmaz).</summary>
    public record SettingsInput(int LateGraceMinutes, TimeOnly DefaultStart, TimeOnly DefaultEnd, int DefaultBreakMinutes,
        decimal? MinRestHours = null, decimal? WeeklyMaxHours = null, decimal? DailyMaxHours = null, decimal? NightMaxHours = null,
        int? MaxConsecutiveDays = null, string? GeoOutsidePolicy = null, bool? GeoStoreRaw = null, int? GeoRawRetentionDays = null);

    [HttpPut("settings")]
    public async Task<IActionResult> PutSettings([FromBody] SettingsInput b, CancellationToken ct)
    {
        if (!IsHr) return Forbid();
        if (b.LateGraceMinutes is < 0 or > 60) return BadRequest(new { message = "Tolerans 0-60 dakika olmalı" });
        if (b.DefaultBreakMinutes is < 0 or > 180) return BadRequest(new { message = "Mola 0-180 dakika olmalı" });
        if (b.DefaultEnd <= b.DefaultStart) return BadRequest(new { message = "Mesai bitişi başlangıçtan sonra olmalı" });
        // Madde 66: sektörel istisnalar için değiştirilebilir; makul aralık dışı değerler reddedilir.
        if (b.MinRestHours is < 8 or > 24) return BadRequest(new { message = "Dinlenme süresi 8-24 saat olmalı" });
        if (b.WeeklyMaxHours is < 1 or > 72) return BadRequest(new { message = "Haftalık süre 1-72 saat olmalı" });
        if (b.DailyMaxHours is < 1 or > 16) return BadRequest(new { message = "Günlük süre 1-16 saat olmalı" });
        if (b.NightMaxHours is < 1 or > 16) return BadRequest(new { message = "Gece çalışması süresi 1-16 saat olmalı" });
        if (b.MaxConsecutiveDays is < 1 or > 14) return BadRequest(new { message = "Ardışık gün sınırı 1-14 olmalı" });
        if (b.GeoOutsidePolicy is not (null or "Block" or "Flag")) return BadRequest(new { message = "Konum politikası Block ya da Flag olmalı" });
        if (b.GeoRawRetentionDays is < 1 or > 90) return BadRequest(new { message = "Ham konum saklama süresi 1-90 gün olmalı" });
        var s = await _db.TimesheetSettings.FirstOrDefaultAsync(ct);
        if (s is null) { s = new TimesheetSettings(); _db.TimesheetSettings.Add(s); }
        s.LateGraceMinutes = b.LateGraceMinutes;
        s.DefaultStart = b.DefaultStart;
        s.DefaultEnd = b.DefaultEnd;
        s.DefaultBreakMinutes = b.DefaultBreakMinutes;
        if (b.MinRestHours is { } rest) s.MinRestHours = rest;
        if (b.WeeklyMaxHours is { } wk) s.WeeklyMaxHours = wk;
        if (b.DailyMaxHours is { } dy) s.DailyMaxHours = dy;
        if (b.NightMaxHours is { } nt) s.NightMaxHours = nt;
        if (b.MaxConsecutiveDays is { } cd) s.MaxConsecutiveDays = cd;
        if (b.GeoOutsidePolicy is { } gp) s.GeoOutsidePolicy = gp;
        if (b.GeoStoreRaw is { } raw) s.GeoStoreRaw = raw;
        if (b.GeoRawRetentionDays is { } rd) s.GeoRawRetentionDays = rd;
        s.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        // Ham konum saklama kapatıldıysa mevcut ham koordinatlar hemen silinir.
        if (!s.GeoStoreRaw)
            await _db.TimeClockPunches.Where(p => p.RawLatitude != null || p.RawLongitude != null)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.RawLatitude, (double?)null).SetProperty(p => p.RawLongitude, (double?)null)
                    .SetProperty(p => p.RawExpiresAt, (DateTimeOffset?)null), ct);
        return Ok(SettingsDto(s));
    }

    private static DateTime Local(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, ClockCore.BusinessZone).DateTime;

    /// <summary>Günlük / haftalık rapor. <c>from</c>-<c>to</c> en çok 31 gün; bugünden sonrası hesaplanmaz.</summary>
    [HttpGet]
    public async Task<IActionResult> Report([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] Guid? employeeId,
        [FromQuery] Guid? departmentId, CancellationToken ct)
    {
        var today = ClockCore.WorkDate(DateTimeOffset.UtcNow);
        var f = from ?? today.AddDays(-6);
        var t = to ?? today;
        if (t > today) t = today;
        if (t < f) return BadRequest(new { message = "Bitiş tarihi başlangıçtan önce olamaz" });
        if (t.DayNumber - f.DayNumber + 1 > MaxDays) return BadRequest(new { message = $"Rapor en çok {MaxDays} gün için alınabilir" });

        var me = await _employees.FindMyEmployeeIdAsync(ct);
        var people = (await TsOps.PeopleAsync(_db, Tenant, ct)).Where(p => p.Status != "Terminated").ToList();
        List<TsOps.PersonRow> scope;
        string scopeName;
        if (IsHr) { scope = people; scopeName = "all"; }
        else if (User.IsInRole("manager") && me is not null)
        {
            scope = people.Where(p => p.HeadId == me || p.Id == me).ToList();
            scopeName = "department";
        }
        else
        {
            if (me is null) return Forbid();
            scope = people.Where(p => p.Id == me).ToList();
            scopeName = "self";
        }
        if (employeeId is { } eid)
        {
            if (!scope.Any(p => p.Id == eid)) return StatusCode(403, new { message = "Bu çalışanın puantajını görme yetkiniz yok" });
            scope = scope.Where(p => p.Id == eid).ToList();
        }
        if (departmentId is { } did) scope = scope.Where(p => p.DepartmentId == did).ToList();
        var ids = scope.Select(p => p.Id).ToList();

        var settings = await SettingsAsync(ct);
        var assignments = await _db.ShiftAssignments.AsNoTracking().Include(a => a.Shift)
            .Where(a => ids.Contains(a.EmployeeId) && a.Date >= f.AddDays(-1) && a.Date <= t).ToListAsync(ct);
        var overrides = await _db.ShiftOverrides.AsNoTracking()
            .Where(o => ids.Contains(o.EmployeeId) && o.Date >= f && o.Date <= t).ToListAsync(ct);
        var fromUtc = new DateTimeOffset(f.AddDays(-1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var toUtc = new DateTimeOffset(t.AddDays(2).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var punches = await _db.TimeClockPunches.AsNoTracking()
            .Where(p => ids.Contains(p.EmployeeId) && p.At >= fromUtc && p.At < toUtc).ToListAsync(ct);
        var entries = await _db.TimeEntries.AsNoTracking()
            .Where(e => ids.Contains(e.EmployeeId) && e.Date >= f && e.Date <= t).ToListAsync(ct);
        var otReqs = await _db.OvertimeRequests.AsNoTracking()
            .Where(o => ids.Contains(o.EmployeeId) && o.Date >= f && o.Date <= t && o.Status != OvertimeStatus.Cancelled).ToListAsync(ct);

        var rows = new List<object>();
        var totals = new List<object>();
        foreach (var p in scope.OrderBy(p => p.FullName, StringComparer.Create(new System.Globalization.CultureInfo("tr-TR"), false)))
        {
            int late = 0, early = 0, worked = 0, overtime = 0, lateDays = 0, absent = 0;
            var myPunches = punches.Where(x => x.EmployeeId == p.Id).Select(x => (In: x.Kind == PunchKind.In, At: Local(x.At))).ToList();
            for (var d = f; d <= t; d = d.AddDays(1))
            {
                var a = assignments.FirstOrDefault(x => x.EmployeeId == p.Id && x.Date == d && x.Shift is not null);
                var ov = overrides.FirstOrDefault(x => x.EmployeeId == p.Id && x.Date == d);
                DateTime? ps = null, pe = null;
                var brk = 0;
                string source;
                if (a is not null)
                {
                    var iv = ShiftInterval.Of(a.Id, d, a.Shift!.StartTime, a.Shift.EndTime, a.Shift.BreakMinutes);
                    (ps, pe, brk, source) = (iv.Start, iv.End, a.Shift.BreakMinutes, "Shift");
                }
                else if (ov is { Type: ShiftOverrideType.Manual, StartTime: { } os, EndTime: { } oe })
                {
                    var iv = ShiftInterval.Of(null, d, os, oe, settings.DefaultBreakMinutes);
                    (ps, pe, brk, source) = (iv.Start, iv.End, settings.DefaultBreakMinutes, "Override");
                }
                else if (ov is { Type: ShiftOverrideType.Leave or ShiftOverrideType.Holiday }) source = ov.Type.ToString();
                else if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) source = "Weekend";
                else
                {
                    ps = d.ToDateTime(settings.DefaultStart);
                    pe = d.ToDateTime(settings.DefaultEnd);
                    brk = settings.DefaultBreakMinutes;
                    source = "Default";
                }

                // Pencere: planlı günde başlangıçtan 4 saat önce – bitişten 8 saat sonra; plansız günde takvim günü.
                var winStart = ps?.AddHours(-4) ?? d.ToDateTime(TimeOnly.MinValue);
                var winEnd = pe?.AddHours(8) ?? d.AddDays(1).ToDateTime(TimeOnly.MinValue);
                var next = assignments.FirstOrDefault(x => x.EmployeeId == p.Id && x.Date == d.AddDays(1) && x.Shift is not null);
                if (next is not null)
                {
                    var nStart = d.AddDays(1).ToDateTime(next.Shift!.StartTime).AddHours(-4);
                    if (nStart < winEnd && nStart > (pe ?? winStart)) winEnd = nStart;
                }
                var dayPunches = myPunches.Where(x => x.At >= winStart && x.At < winEnd).ToList();
                if (dayPunches.Count == 0 && entries.FirstOrDefault(e => e.EmployeeId == p.Id && e.Date == d) is { ClockIn: { } ci } en)
                {
                    dayPunches.Add((true, Local(ci)));
                    if (en.ClockOut is { } co) dayPunches.Add((false, Local(co)));
                }
                var r = AttendanceCalc.Compute(ps, pe, brk, dayPunches, settings.LateGraceMinutes);
                var status = r.Status;
                if (ps is null && dayPunches.Count == 0) status = source is "Leave" or "Holiday" ? source : "Off";
                if (status == "Absent" && d == today) status = "NotYet";
                if (status is "Off" or "Leave" or "Holiday" && dayPunches.Count == 0)
                {
                    rows.Add(new { employeeId = p.Id, name = p.FullName, date = d, source, status });
                    continue;
                }
                late += r.LateMinutes; early += r.EarlyLeaveMinutes; worked += r.WorkedMinutes; overtime += r.OvertimeMinutes;
                if (r.LateMinutes > 0) lateDays++;
                if (status == "Absent") absent++;
                var req = otReqs.FirstOrDefault(o => o.EmployeeId == p.Id && o.Date == d);
                rows.Add(new
                {
                    employeeId = p.Id, name = p.FullName, date = d, source, status,
                    plannedStart = ps, plannedEnd = pe, firstIn = r.FirstIn, lastOut = r.LastOut,
                    lateMinutes = r.LateMinutes, earlyLeaveMinutes = r.EarlyLeaveMinutes, workedMinutes = r.WorkedMinutes,
                    expectedMinutes = r.ExpectedMinutes, overtimeMinutes = r.OvertimeMinutes, suggestedOvertimeHours = r.SuggestedOvertimeHours,
                    overtimeRequest = req is null ? null : new { req.Id, req.Status, req.Hours },
                });
            }
            totals.Add(new { employeeId = p.Id, name = p.FullName, department = p.Department, lateMinutes = late, lateDays, earlyLeaveMinutes = early,
                workedMinutes = worked, overtimeMinutes = overtime, absentDays = absent });
        }

        return Ok(new
        {
            from = f, to = t, scope = scopeName, graceMinutes = settings.LateGraceMinutes,
            defaultStart = settings.DefaultStart, defaultEnd = settings.DefaultEnd,
            note = "Bilgilendirme amaçlıdır; otomatik kesinti yapılmaz. Fazla mesai yalnızca talep ve onayla bordroya girer.",
            totals, rows,
        });
    }
}

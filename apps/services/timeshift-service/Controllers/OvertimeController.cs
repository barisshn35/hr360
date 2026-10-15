using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TimeShiftService.Data;
using TimeShiftService.Models;
using TimeShiftService.Services;

namespace TimeShiftService.Controllers;

/// <summary>
/// Fazla mesai talebi ve onayı. Yasal sınır (İş Kanunu m.41): yılda 270 saat; günlük
/// çalışma 11 saati aşamayacağı için tek günde en fazla 4 saat fazla mesai girilebilir.
/// Yalnızca onaylanan saatler bordroya (compensation-service) girer.
/// </summary>
[ApiController]
[Route("api/overtime")]
[Authorize]
public class OvertimeController : ControllerBase
{
    private static readonly System.Globalization.CultureInfo Tr = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
    public const decimal AnnualLimitHours = 270m;
    public const decimal DailyLimitHours = 4m;
    private const int WorkflowTypeOvertime = 5;

    private readonly TimeShiftDbContext _db;
    private readonly EmployeeDirectoryClient _employees;
    private readonly ApprovalStarter _approvals;

    public OvertimeController(TimeShiftDbContext db, EmployeeDirectoryClient employees, ApprovalStarter approvals)
    {
        _db = db; _employees = employees; _approvals = approvals;
    }

    private bool IsTimekeeper => User.IsInRole("manager") || User.IsInRole("hr-admin")
        || User.IsInRole("tenant-admin") || User.IsInRole("platform-admin") || User.IsInRole("ext-timeshift-manage");
    private bool IsHr => User.IsInRole("hr-admin") || User.IsInRole("tenant-admin") || User.IsInRole("platform-admin");

    /// <summary>Yıl içinde onaylı + bekleyen saat (iptal/ret hariç).</summary>
    private Task<decimal> UsedHoursAsync(Guid employeeId, int year, Guid? exclude, CancellationToken ct) =>
        _db.OvertimeRequests.Where(o => o.EmployeeId == employeeId && o.Date.Year == year && o.Id != exclude
                && (o.Status == OvertimeStatus.Approved || o.Status == OvertimeStatus.Pending))
            .SumAsync(o => o.Hours, ct);

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] Guid? employeeId, [FromQuery] int? year, [FromQuery] string? status,
        [FromQuery] bool mine, CancellationToken ct)
    {
        // mine=true: "Taleplerim" — İK/yönetici için de yalnızca kendi kayıtları (çalışan kaydı yoksa boş).
        if (mine)
        {
            var me = await _employees.FindMyEmployeeIdAsync(ct);
            if (me is null) return Ok(Array.Empty<OvertimeRequest>());
            employeeId = me;
        }
        else if (!IsTimekeeper)
        {
            var me = await _employees.FindMyEmployeeIdAsync(ct);
            if (me is null) return Forbid();
            if (employeeId.HasValue && employeeId != me) return Forbid();
            employeeId = me;
        }
        var q = _db.OvertimeRequests.AsNoTracking();
        if (employeeId.HasValue) q = q.Where(o => o.EmployeeId == employeeId);
        if (year.HasValue) q = q.Where(o => o.Date.Year == year);
        if (Enum.TryParse<OvertimeStatus>(status, true, out var st)) q = q.Where(o => o.Status == st);
        return Ok(await q.OrderByDescending(o => o.Date).ThenByDescending(o => o.CreatedAt).Take(500).ToListAsync(ct));
    }

    /// <summary>Yıllık kullanım: onaylı, bekleyen ve kalan saat (270 saat sınırına göre).</summary>
    [HttpGet("summary")]
    public async Task<IActionResult> Summary([FromQuery] Guid? employeeId, [FromQuery] int? year, CancellationToken ct)
    {
        var y = year ?? DateTime.UtcNow.Year;
        var me = await _employees.FindMyEmployeeIdAsync(ct);
        var who = employeeId ?? me;
        // Çalışan kaydı olmayan hesap (ör. yalnızca yönetici): boş özet.
        if (who is null) return Ok(new { employeeId = (Guid?)null, year = y, approvedHours = 0m, pendingHours = 0m, limitHours = AnnualLimitHours, remainingHours = AnnualLimitHours });
        if (who != me && !IsTimekeeper) return Forbid();
        var rows = await _db.OvertimeRequests.AsNoTracking().Where(o => o.EmployeeId == who && o.Date.Year == y).ToListAsync(ct);
        var approved = rows.Where(o => o.Status == OvertimeStatus.Approved).Sum(o => o.Hours);
        var pending = rows.Where(o => o.Status == OvertimeStatus.Pending).Sum(o => o.Hours);
        return Ok(new { employeeId = who, year = y, approvedHours = approved, pendingHours = pending,
            limitHours = AnnualLimitHours, remainingHours = Math.Max(0, AnnualLimitHours - approved - pending) });
    }

    public record CreateOvertimeInput(Guid? EmployeeId, DateOnly Date, decimal Hours, string? Reason);

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateOvertimeInput body, CancellationToken ct)
    {
        var me = await _employees.FindMyEmployeeIdAsync(ct);
        var employeeId = body.EmployeeId ?? me;
        if (employeeId is null) return BadRequest(new { message = "Hesabınıza bağlı çalışan kaydı yok", code = "no_employee_record" });
        var onBehalf = employeeId != me;
        if (onBehalf && !IsTimekeeper)
            return StatusCode(403, new { message = "Yalnızca kendi adınıza fazla mesai talebi oluşturabilirsiniz" });

        var hours = Math.Round(body.Hours * 2, MidpointRounding.AwayFromZero) / 2; // yarım saat adımı
        if (hours is <= 0 or > DailyLimitHours)
            return BadRequest(new { message = $"Fazla mesai günde 0,5 ile {DailyLimitHours} saat arasında olmalı (günlük çalışma 11 saati aşamaz)" });
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (body.Date < today.AddDays(-60) || body.Date > today.AddDays(60))
            return BadRequest(new { message = "Tarih bugünden en fazla 60 gün önce ya da sonra olabilir" });
        if (body.Reason is { Length: > 500 }) return BadRequest(new { message = "Gerekçe en fazla 500 karakter olabilir" });
        // Madde 72: kapatılmış puantaj dönemine fazla mesai girilemez (bordroya aktarılan veri değişmesin).
        if (await PeriodLock.CheckAsync(_db, body.Date, ct) is { } locked) return Conflict(new { message = locked, code = "period_locked" });
        if (await _db.OvertimeRequests.AnyAsync(o => o.EmployeeId == employeeId && o.Date == body.Date
                && (o.Status == OvertimeStatus.Pending || o.Status == OvertimeStatus.Approved), ct))
            return Conflict(new { message = "Bu gün için zaten bir fazla mesai talebi var" });

        var used = await UsedHoursAsync(employeeId.Value, body.Date.Year, null, ct);
        if (used + hours > AnnualLimitHours)
            return BadRequest(new { message = $"Yıllık fazla mesai sınırı (270 saat) aşılıyor: bu yıl {used.ToString("0.#", Tr)} saat kullanıldı ya da onay bekliyor", code = "annual_limit" });

        var o = new OvertimeRequest
        {
            EmployeeId = employeeId.Value, Date = body.Date, Hours = hours,
            Reason = string.IsNullOrWhiteSpace(body.Reason) ? null : body.Reason.Trim(),
            CreatedBy = User.FindFirst("preferred_username")?.Value,
        };
        // Yönetici/İK'nın başkası adına girdiği fazla mesai zaten onaylanmış sayılır (onaycı kendisi).
        if (onBehalf) { o.Status = OvertimeStatus.Approved; o.DecidedAt = DateTimeOffset.UtcNow; }
        _db.OvertimeRequests.Add(o);
        await _db.SaveChangesAsync(ct);
        if (!onBehalf)
        {
            var payload = System.Text.Json.JsonSerializer.Serialize(new { overtimeRequestId = o.Id, date = o.Date.ToString("yyyy-MM-dd"), hours = o.Hours });
            o.WorkflowRequestId = await _approvals.StartAsync(_db, o.EmployeeId, WorkflowTypeOvertime,
                $"{o.Hours.ToString("0.#", Tr)} saat fazla mesai ({o.Date:dd.MM.yyyy})", payload, ct);
            if (o.WorkflowRequestId is not null) await _db.SaveChangesAsync(ct);
        }
        return Ok(o);
    }

    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var o = await _db.OvertimeRequests.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (o is null) return NotFound();
        var me = await _employees.FindMyEmployeeIdAsync(ct);
        if (o.EmployeeId != me && !IsHr) return Forbid();
        if (o.Status != OvertimeStatus.Pending) return Conflict(new { message = "Yalnızca bekleyen talep iptal edilebilir" });
        o.Status = OvertimeStatus.Cancelled;
        o.DecidedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        // İzin iptaliyle aynı: açık onay akışı kapatılır, yoksa onaycının kutusunda "Beklemede" kalır.
        // İç uç talep sahibi adına çağrılır; İK başkasının talebini iptal ettiğinde de çalışır.
        if (o.WorkflowRequestId is { } wf) await _approvals.CancelAsync(o.TenantSlug, wf, o.EmployeeId, ct);
        return Ok(o);
    }

    public record DecideInput(bool Approve);

    /// <summary>
    /// Onay akışı açılamamışsa (bölüm başı yok) İK panelden karar verir. Akışı olan talep
    /// Onay kutusundan karara bağlanır; kimse kendi talebine karar veremez.
    /// </summary>
    [HttpPost("{id:guid}/decide")]
    public async Task<IActionResult> Decide(Guid id, [FromBody] DecideInput body, CancellationToken ct)
    {
        if (!IsHr) return Forbid();
        var o = await _db.OvertimeRequests.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (o is null) return NotFound();
        if (o.WorkflowRequestId is not null) return Conflict(new { message = "Bu talep onay akışında; Onay kutusundan karar verin" });
        if (o.EmployeeId == await _employees.FindMyEmployeeIdAsync(ct)) return StatusCode(403, new { message = "Kendi talebinize karar veremezsiniz" });
        if (o.Status != OvertimeStatus.Pending) return Conflict(new { message = "Talep zaten karara bağlanmış" });
        o.Status = body.Approve ? OvertimeStatus.Approved : OvertimeStatus.Rejected;
        o.DecidedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(o);
    }
}

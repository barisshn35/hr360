using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TimeShiftService.Data;
using TimeShiftService.Models;
using TimeShiftService.Services;
using TimeShiftService.Tenancy;

namespace TimeShiftService.Controllers;

[ApiController]
[Route("api/shifts")]
[Authorize]
public class ShiftsController : ControllerBase
{
    private readonly TimeShiftDbContext _db;
    private readonly EmployeeDirectoryClient _employees;
    private readonly ITenantContext _tenant;

    public ShiftsController(TimeShiftDbContext db, EmployeeDirectoryClient employees, ITenantContext tenant)
    {
        _db = db; _employees = employees; _tenant = tenant;
    }

    private string Tenant => _tenant.TenantSlug ?? "";
    /// <summary>Kiracının tamamını planlayan roller (optimizer ve takas ile aynı).</summary>
    private bool IsHr => User.IsInRole("hr-admin") || User.IsInRole("tenant-admin") || User.IsInRole("platform-admin")
        || User.IsInRole("ext-timeshift-manage");
    private static DateOnly Today => ClockCore.WorkDate(DateTimeOffset.UtcNow);
    private const string ScopeMessage = "Yalnızca başı olduğunuz bölümün çalışanlarını planlayabilirsiniz";

    /// <summary>
    /// Planlama yetkisi (atama, atama kaldırma, kural denetimi): İK tümü; yönetici yalnızca başı olduğu
    /// bölümün çalışanları ve kendisi (optimizer apply ile aynı). Çalışan kiracıda yoksa 400.
    /// </summary>
    private async Task<IActionResult?> PlanGuardAsync(Guid employeeId, CancellationToken ct)
    {
        var person = await TsOps.PersonAsync(_db, Tenant, employeeId, ct);
        if (person is null) return BadRequest(new { message = "Çalışan bulunamadı" });
        if (IsHr) return null;
        var me = await _employees.FindMyEmployeeIdAsync(ct);
        return ShiftScope.CanPlan(me, person) ? null : StatusCode(403, new { message = ScopeMessage });
    }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] Guid? departmentId)
    {
        var q = _db.Shifts.AsQueryable();
        if (departmentId.HasValue) q = q.Where(s => s.DepartmentId == departmentId.Value);
        return Ok(await q.OrderBy(s => s.StartTime).ToListAsync());
    }

    [HttpPost]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Create([FromBody] CreateShiftRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return BadRequest(new { message = "Vardiya adı zorunlu" });
        if (request.StartTime == request.EndTime) return BadRequest(new { message = "Başlangıç ve bitiş saati aynı olamaz" });
        if (request.BreakMinutes is < 0 or > 240) return BadRequest(new { message = "Mola 0–240 dakika olmalı" });
        var span = request.EndTime > request.StartTime
            ? request.EndTime - request.StartTime
            : TimeSpan.FromHours(24) - (request.StartTime - request.EndTime);
        if (request.BreakMinutes >= span.TotalMinutes) return BadRequest(new { message = "Mola vardiya süresinden kısa olmalı" });
        var shift = new Shift
        {
            Name = request.Name.Trim(),
            StartTime = request.StartTime,
            EndTime = request.EndTime,
            BreakMinutes = request.BreakMinutes,
            DepartmentId = request.DepartmentId,
            IsNightShift = request.EndTime < request.StartTime
        };
        _db.Shifts.Add(shift);
        await _db.SaveChangesAsync();
        return Created($"/api/shifts/{shift.Id}", shift);
    }

    /// <summary>
    /// Vardiya tanımını siler. Atama kaydı varsa (geçmiş puantaj ve takas talepleri ona bağlı)
    /// silinmez: ilişki veritabanında cascade olduğundan sessiz veri kaybını önlemek için 409.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var shift = await _db.Shifts.FirstOrDefaultAsync(s => s.Id == id);
        if (shift is null) return NotFound();
        var used = await _db.ShiftAssignments.CountAsync(a => a.ShiftId == id);
        if (used > 0)
            return Conflict(new { message = $"Bu vardiyaya {used} atama bağlı; vardiya tanımı silinemez" });
        _db.Shifts.Remove(shift);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>
    /// Tek atama (çalışan + gün). Geçmiş güne atama yapılamaz (bugün serbest); yönetici yalnızca başı
    /// olduğu bölümün çalışanlarına atar. Aynı güne var olan atama vardiya değişikliği olarak güncellenir.
    /// </summary>
    [HttpPost("{id}/assign")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> Assign(Guid id, [FromBody] AssignShiftRequest request, CancellationToken ct)
    {
        if (!await _db.Shifts.AnyAsync(s => s.Id == id, ct)) return NotFound(new { message = "Vardiya bulunamadı" });
        if (ShiftScope.IsPast(request.Date, Today)) return BadRequest(new { message = "Geçmiş güne atama yapılamaz" });
        if (await PlanGuardAsync(request.EmployeeId, ct) is { } deny) return deny;

        var existing = await _db.ShiftAssignments.FirstOrDefaultAsync(a =>
            a.EmployeeId == request.EmployeeId && a.Date == request.Date, ct);
        if (existing is not null)
        {
            existing.ShiftId = id;
            await _db.SaveChangesAsync(ct);
            return Ok(existing);
        }

        var assignment = new ShiftAssignment
        {
            EmployeeId = request.EmployeeId,
            ShiftId = id,
            Date = request.Date
        };
        _db.ShiftAssignments.Add(assignment);
        await _db.SaveChangesAsync(ct);
        return Created($"/api/shifts/{id}", assignment);
    }

    /// <summary>
    /// Hatalı atamanın geri alınması. Yetki atama ile aynıdır; geçmiş atama (puantaja bağlı) kaldırılamaz,
    /// bekleyen bir takas talebine konu olan atama da kaldırılamaz (409). Silme denetim kaydına yazılır.
    /// </summary>
    [HttpDelete("assignments/{assignmentId:guid}")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> Unassign(Guid assignmentId, CancellationToken ct)
    {
        var a = await _db.ShiftAssignments.FirstOrDefaultAsync(x => x.Id == assignmentId, ct);
        if (a is null) return NotFound(new { message = "Vardiya ataması bulunamadı" });
        if (!IsHr && !ShiftScope.CanPlan(await _employees.FindMyEmployeeIdAsync(ct), await TsOps.PersonAsync(_db, Tenant, a.EmployeeId, ct)))
            return StatusCode(403, new { message = ScopeMessage });
        if (ShiftScope.IsPast(a.Date, Today)) return BadRequest(new { message = "Geçmiş atama kaldırılamaz" });
        if (await _db.ShiftSwapRequests.AnyAsync(s => (s.Status == SwapStatus.PendingPeer || s.Status == SwapStatus.PendingApproval)
                && (s.RequesterAssignmentId == a.Id || s.TargetAssignmentId == a.Id), ct))
            return Conflict(new { message = "Bu atama için bekleyen bir takas talebi var; önce talep sonuçlanmalı" });
        _db.ShiftAssignments.Remove(a);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// Madde 66: atama/taşıma öncesi çalışma süresi kural uyarıları (11 saat dinlenme, günlük/haftalık
    /// süre, gece 7,5 saat, ardışık gün). Vardiya tanımı (ShiftId) ya da saatler (Start/End/Break) verilir.
    /// Uyarıdır; atamayı engellemez.
    /// </summary>
    [HttpPost("rule-check")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> RuleCheck([FromBody] RuleCheckRequest b, CancellationToken ct)
    {
        if (await PlanGuardAsync(b.EmployeeId, ct) is { } deny) return deny;
        TimeOnly start, end;
        int brk;
        if (b.ShiftId is { } sid)
        {
            var shift = await _db.Shifts.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sid, ct);
            if (shift is null) return NotFound(new { message = "Vardiya bulunamadı" });
            (start, end, brk) = (shift.StartTime, shift.EndTime, shift.BreakMinutes);
        }
        else if (b.StartTime is { } st && b.EndTime is { } en && st != en)
            (start, end, brk) = (st, en, Math.Clamp(b.BreakMinutes ?? 0, 0, 240));
        else return BadRequest(new { message = "Vardiya ya da başlangıç-bitiş saati gerekli" });
        var warnings = await WorkRuleCheck.ForAssignmentAsync(_db, b.EmployeeId, b.Date, start, end, brk, await WorkRuleCheck.RulesAsync(_db, ct), ct);
        return Ok(new { warnings = warnings.Select(w => new { w.Code, w.Message }) });
    }

    /// <summary>
    /// Vardiya çizelgesi (KVKK: veri en aza). İK tümünü; yönetici başı olduğu bölümleri ve kendisini;
    /// çalışan kendisini ve ekip arkadaşlarını (aynı vardiya ekibi, ekipsizse aynı departman) görür.
    /// Kapsam dışındaki bir kişi istenirse 403. Yanıtta yalnızca atama + vardiya tanımı vardır.
    /// </summary>
    [HttpGet("roster")]
    public async Task<IActionResult> GetRoster(
        [FromQuery] DateOnly from, [FromQuery] DateOnly to, [FromQuery] Guid? employeeId, CancellationToken ct)
    {
        if (to < from) return BadRequest(new { message = "Bitiş tarihi başlangıçtan önce olamaz" });
        var q = _db.ShiftAssignments.AsNoTracking().Include(a => a.Shift)
            .Where(a => a.Date >= from && a.Date <= to);
        if (!IsHr)
        {
            var me = await _employees.FindMyEmployeeIdAsync(ct);
            if (me is null) return employeeId.HasValue ? StatusCode(403, new { message = "Bu kişinin vardiyalarını göremezsiniz" }) : Ok(Array.Empty<object>());
            var allowed = User.IsInRole("manager")
                ? await ShiftScope.ManagedAsync(_db, Tenant, me.Value, ct)
                : new HashSet<Guid>();
            allowed.UnionWith(await ShiftScope.PeersAsync(_db, Tenant, me.Value, from, to, ct));
            allowed.Add(me.Value);
            if (employeeId is { } e && !allowed.Contains(e))
                return StatusCode(403, new { message = "Bu kişinin vardiyalarını göremezsiniz" });
            var ids = allowed.ToList();
            q = q.Where(a => ids.Contains(a.EmployeeId));
        }
        if (employeeId.HasValue) q = q.Where(a => a.EmployeeId == employeeId.Value);
        return Ok(await q.OrderBy(a => a.Date).ToListAsync(ct));
    }
}

public record CreateShiftRequest(
    string Name, TimeOnly StartTime, TimeOnly EndTime, int BreakMinutes, Guid? DepartmentId);
public record AssignShiftRequest(Guid EmployeeId, DateOnly Date);
public record RuleCheckRequest(Guid EmployeeId, DateOnly Date, Guid? ShiftId, TimeOnly? StartTime, TimeOnly? EndTime, int? BreakMinutes);

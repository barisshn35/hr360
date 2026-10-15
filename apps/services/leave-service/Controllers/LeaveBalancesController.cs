using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LeaveService.Data;
using LeaveService.Models;
using LeaveService.Services;
using LeaveService.Infrastructure;

namespace LeaveService.Controllers;

[ApiController]
[Route("api/leave-balances")]
[Authorize]
public class LeaveBalancesController : ControllerBase
{
    private readonly LeaveDbContext _db;
    private readonly ApprovalWorkflowClient _approvals;
    public LeaveBalancesController(LeaveDbContext db, ApprovalWorkflowClient approvals)
    {
        _db = db;
        _approvals = approvals;
    }

    /// <remarks>Sayfalama (G24): <c>page</c> verilmezse eski biçim (düz dizi, en fazla 2000; toplam
    /// <c>X-Total-Count</c> başlığında); <c>page</c>/<c>pageSize</c> ile <c>{ items, total, page, pageSize }</c>.
    /// Ek filtre: <c>type</c>.</remarks>
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] Guid? employeeId, [FromQuery] int? year, CancellationToken ct,
        [FromQuery] int? page = null, [FromQuery] int? pageSize = null, [FromQuery] LeaveType? type = null)
    {
        // GUVENLIK: Onceden her calisan tum kiracinin izin bakiyelerini (kimin ne
        // kadar hastalik/ucretsiz izin kullandigi dahil) listeleyebiliyordu. Yonetici
        // ve IK disindakiler yalnizca kendi bakiyesini gorur.
        var isManager = User.IsInRole("manager") || User.IsInRole("hr-admin")
            || User.IsInRole("tenant-admin") || User.IsInRole("platform-admin");
        if (!isManager)
        {
            var me = await _approvals.FindMyEmployeeIdAsync(ct);
            if (me is null) return Forbid();
            if (employeeId.HasValue && employeeId.Value != me.Value) return Forbid();
            employeeId = me;
        }

        var q = _db.LeaveBalances.AsNoTracking().AsQueryable();
        if (employeeId.HasValue) q = q.Where(b => b.EmployeeId == employeeId.Value);
        if (year.HasValue) q = q.Where(b => b.Year == year.Value);
        if (type.HasValue) q = q.Where(b => b.Type == type.Value);
        return await Paging.ListAsync(this, q.OrderBy(b => b.Type).ThenBy(b => b.EmployeeId).ThenBy(b => b.Year).ThenBy(b => b.Id),
            page, pageSize, ct);
    }

    /// <summary>Yeni yil bakiyesi tanimlar veya mevcut hak edisi gunceller.</summary>
    [HttpPost]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Upsert([FromBody] UpsertBalanceRequest request)
    {
        if (request.EntitledDays is < 0 or > 365)
            return BadRequest("Hak edilen gün 0-365 arasında olmalı");
        if (request.Year is < 2000 or > 2100)
            return BadRequest("Geçersiz yıl");
        var existing = await _db.LeaveBalances.FirstOrDefaultAsync(b =>
            b.EmployeeId == request.EmployeeId && b.Year == request.Year && b.Type == request.Type);

        if (existing is null)
        {
            existing = new LeaveBalance
            {
                EmployeeId = request.EmployeeId,
                Year = request.Year,
                Type = request.Type,
                EntitledDays = request.EntitledDays
            };
            _db.LeaveBalances.Add(existing);
        }
        else
        {
            existing.EntitledDays = request.EntitledDays;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await _db.SaveChangesAsync();
        return Ok(existing);
    }

    private sealed class EmpRow
    {
        public Guid Id { get; set; }
        public DateOnly HireDate { get; set; }
        public DateOnly? BirthDate { get; set; }
    }

    private async Task<List<(Guid Id, DateOnly Hire, int Days, int ServiceYears, bool AgeRule)>> StatutoryAsync(int year, CancellationToken ct)
    {
        var rows = await _db.Database.SqlQueryRaw<EmpRow>("""
            SELECT e."Id", e."HireDate", p."BirthDate" FROM employee_employees e
            LEFT JOIN engagement_profiles p ON p."EmployeeId" = e."Id" AND p."TenantSlug" = e."TenantSlug"
            WHERE e."TenantSlug" = {0} AND e."Status" <> 'Terminated'
            """, _db.CurrentTenantSlug ?? "").ToListAsync(ct);
        return rows.Select(r =>
        {
            var (days, years, ageRule) = LeaveEntitlement.Statutory(r.HireDate, r.BirthDate, year);
            return (r.Id, r.HireDate, days, years, ageRule);
        }).ToList();
    }

    /// <summary>
    /// Yasal yıllık izin hakkı ön izlemesi (İş Kanunu m.53, kıdeme ve yaşa göre). Doğum tarihi
    /// yanıtta yer almaz; yalnızca yaş kuralının uygulandığı belirtilir.
    /// </summary>
    [HttpGet("statutory")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Statutory([FromQuery] int year, CancellationToken ct)
    {
        if (year is < 2000 or > 2100) return BadRequest(new { message = "Geçersiz yıl" });
        var list = await StatutoryAsync(year, ct);
        var balances = await _db.LeaveBalances.AsNoTracking().Where(b => b.Year == year && b.Type == LeaveType.Annual).ToListAsync(ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return Ok(list.Select(x =>
        {
            var b = balances.FirstOrDefault(y => y.EmployeeId == x.Id);
            var proposed = ProposedEntitled(b, x.Days);
            return new
            {
                employeeId = x.Id, x.ServiceYears, anniversary = LeaveEntitlement.Anniversary(x.Hire, year), statutoryDays = x.Days,
                ageRule = x.AgeRule, currentEntitled = b?.EntitledDays, carriedOver = b?.CarriedOverDays ?? 0,
                // Dalga 9 (madde 71): hak yıl dönümünde doğar; uygulanınca bakiye ne olur (değişmeyecekse null).
                accrued = LeaveEntitlement.Accrued(x.Hire, year, today),
                proposedEntitled = proposed,
            };
        }));
    }

    /// <summary>
    /// Yasal hakkın bakiyeye yansıması: bakiye yoksa yasal gün; varsa bu yılın kendi hakkı (devir hariç)
    /// yasal günün altındaysa yükseltilir — asla düşürülmez. Değişiklik yoksa null.
    /// </summary>
    public static decimal? ProposedEntitled(LeaveBalance? b, int statutoryDays)
    {
        if (statutoryDays <= 0) return null;
        if (b is null) return statutoryDays;
        if (b.EntitledDays + b.CarriedOutDays - b.CarriedOverDays < statutoryDays)
            return statutoryDays + b.CarriedOverDays - b.CarriedOutDays;
        return null;
    }

    /// <summary>
    /// Yasal hakkı bakiyeye yazar: bakiye yoksa açar, yasal günün altındaysa yükseltir (asla düşürmez).
    /// Dalga 9: İK ön izlemede onayladığı kişileri (EmployeeIds) gönderir; OnlyAccrued ile yalnızca yıl
    /// dönümü gelmiş (hak doğmuş) kişiler uygulanır. Her bakiye değişikliği denetim kaydına yazılır.
    /// </summary>
    [HttpPost("statutory/apply")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> ApplyStatutory([FromBody] ApplyStatutoryRequest body, CancellationToken ct)
    {
        if (body.Year is < 2000 or > 2100) return BadRequest(new { message = "Geçersiz yıl" });
        if (body.EmployeeIds is { Length: > 5000 }) return BadRequest(new { message = "En çok 5000 çalışan" });
        var list = await StatutoryAsync(body.Year, ct);
        var balances = await _db.LeaveBalances.Where(b => b.Year == body.Year && b.Type == LeaveType.Annual).ToListAsync(ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var selected = body.EmployeeIds is { Length: > 0 } ids ? ids.ToHashSet() : null;
        var changed = 0;
        var skippedNotAccrued = 0;
        foreach (var x in list.Where(x => x.Days > 0))
        {
            if (selected is not null && !selected.Contains(x.Id)) continue;
            if (body.OnlyAccrued == true && !LeaveEntitlement.Accrued(x.Hire, body.Year, today)) { skippedNotAccrued++; continue; }
            var b = balances.FirstOrDefault(y => y.EmployeeId == x.Id);
            // Devreden/devredilen günler korunur; yalnızca bu yılın kendi hakkı yasal güne çıkarılır.
            if (ProposedEntitled(b, x.Days) is not { } target) continue;
            if (b is null)
                _db.LeaveBalances.Add(new LeaveBalance { EmployeeId = x.Id, Year = body.Year, Type = LeaveType.Annual, EntitledDays = target });
            else
            {
                b.EntitledDays = target;
                b.UpdatedAt = DateTimeOffset.UtcNow;
            }
            changed++;
        }
        await _db.SaveChangesAsync(ct);
        return Ok(new { changed, skippedNotAccrued });
    }

    /// <summary>
    /// Kullanılmayan yıllık izni sonraki yıla devreder (yasal olarak yıllık izin yanmaz; isteğe
    /// bağlı üst sınır). Tekrar çalıştırılabilir: yalnızca fark aktarılır.
    /// </summary>
    [HttpPost("carry-over")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> CarryOver([FromBody] CarryOverRequest body, CancellationToken ct)
    {
        if (body.FromYear is < 2000 or > 2099) return BadRequest(new { message = "Geçersiz yıl" });
        if (body.MaxDays is < 0 or > 365) return BadRequest(new { message = "Üst sınır 0-365 gün olmalı" });
        var from = await _db.LeaveBalances.Where(b => b.Year == body.FromYear && b.Type == LeaveType.Annual).ToListAsync(ct);
        var next = await _db.LeaveBalances.Where(b => b.Year == body.FromYear + 1 && b.Type == LeaveType.Annual).ToListAsync(ct);
        var moved = 0m;
        var people = 0;
        foreach (var b in from)
        {
            var unused = Math.Max(0, b.EntitledDays - b.UsedDays - b.PendingDays + b.CarriedOutDays);
            var carry = body.MaxDays is { } max ? Math.Min(unused, max) : unused;
            var delta = carry - b.CarriedOutDays;
            if (delta == 0) continue;
            var n = next.FirstOrDefault(x => x.EmployeeId == b.EmployeeId);
            if (n is null)
            {
                n = new LeaveBalance { EmployeeId = b.EmployeeId, Year = body.FromYear + 1, Type = LeaveType.Annual };
                _db.LeaveBalances.Add(n);
                next.Add(n);
            }
            n.EntitledDays += delta; n.CarriedOverDays += delta; n.UpdatedAt = DateTimeOffset.UtcNow;
            b.CarriedOutDays += delta; b.EntitledDays -= delta; b.UpdatedAt = DateTimeOffset.UtcNow;
            moved += delta; people++;
        }
        await _db.SaveChangesAsync(ct);
        return Ok(new { employees = people, days = moved });
    }
}

public record UpsertBalanceRequest(Guid EmployeeId, int Year, LeaveType Type, decimal EntitledDays);
public record ApplyStatutoryRequest(int Year, Guid[]? EmployeeIds = null, bool? OnlyAccrued = null);
public record CarryOverRequest(int FromYear, decimal? MaxDays);

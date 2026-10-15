using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PerformanceService.Data;
using PerformanceService.Models;
using PerformanceService.Services;

namespace PerformanceService.Controllers;

[ApiController]
[Route("api/review-cycles")]
[Authorize]
public class CyclesController : ControllerBase
{
    private readonly PerformanceDbContext _db;
    private readonly SnapshotService _snapshots;

    public CyclesController(PerformanceDbContext db, SnapshotService snapshots)
    {
        _db = db;
        _snapshots = snapshots;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] int? year, [FromQuery] CycleStatus? status)
    {
        var q = _db.Cycles.AsQueryable();
        if (year.HasValue) q = q.Where(c => c.Year == year.Value);
        if (status.HasValue) q = q.Where(c => c.Status == status.Value);
        return Ok(await q.OrderByDescending(c => c.Year).ThenBy(c => c.Period).ToListAsync());
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var c = await _db.Cycles.Include(x => x.Goals).FirstOrDefaultAsync(x => x.Id == id);
        return c is null ? NotFound() : Ok(c);
    }

    [HttpPost]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Create([FromBody] CreateCycleRequest request)
    {
        var cycle = new ReviewCycle
        {
            Name = request.Name,
            Year = request.Year,
            Period = request.Period,
            StartDate = request.StartDate,
            EndDate = request.EndDate
        };
        _db.Cycles.Add(cycle);
        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id = cycle.Id }, cycle);
    }

    [HttpPost("{id}/status")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> ChangeStatus(Guid id, [FromBody] ChangeCycleStatusRequest request)
    {
        var c = await _db.Cycles.FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return NotFound();

        // Kapali donem yeniden acilamaz: resmi sonuclar sabitlendi,
        // sonradan gelen degerlendirmeyle degismemeli.
        if (c.Status == CycleStatus.Closed && request.Status != CycleStatus.Closed)
            return BadRequest(new
            {
                message = "Kapanmış dönem yeniden açılamaz. Yeni bir dönem oluşturun.",
            });

        var closing = request.Status == CycleStatus.Closed && c.Status != CycleStatus.Closed;

        c.Status = request.Status;
        await _db.SaveChangesAsync();

        if (!closing) return Ok(c);

        // Donem kapaniyor: herkesin nihai puanini hesaplayip sabitle.
        // Bu anlik goruntuler "resmi sonuc" sayilir; sonraki hesaplarda
        // bunlar kullanilir, yeniden hesaplanmaz.
        var captured = await _snapshots.CaptureCycleAsync(id, SnapshotSource.CycleClosed);

        return Ok(new
        {
            cycle = c,
            closed = true,
            finalizedEmployeeCount = captured,
            message = $"Dönem kapatıldı, {captured} çalışanın nihai puanı sabitlendi.",
        });
    }

    /// <summary>
    /// Donem kapanmadan once durum kontrolu: kimin degerlendirmesi eksik,
    /// kimin puani gecici kalacak. Kapanis geri alinamadigi icin yonetici
    /// once bunu gormeli.
    /// </summary>
    [HttpGet("{id}/readiness")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> Readiness(Guid id)
    {
        var cycle = await _db.Cycles.FirstOrDefaultAsync(c => c.Id == id);
        if (cycle is null) return NotFound();

        var config = await _snapshots.ActiveConfigAsync();

        var reviews = await _db.Reviews.Where(r => r.CycleId == id).ToListAsync();
        var goals = await _db.Goals.Where(g => g.CycleId == id).ToListAsync();

        var employees = reviews.Select(r => r.EmployeeId)
            .Concat(goals.Select(g => g.EmployeeId))
            .Distinct().ToList();

        var rows = employees.Select(empId =>
        {
            var own = reviews.Where(r => r.EmployeeId == empId).ToList();
            var submitted = own.Count(r => r.SubmittedAt != null);
            var pending = own.Count(r => r.SubmittedAt == null);
            var onlySelf = submitted > 0 &&
                own.Where(r => r.SubmittedAt != null).All(r => r.Type == ReviewType.Self);

            var willBeProvisional = submitted < config.MinReviewsForValidScore
                                    || (!config.AllowSelfOnlyScore && onlySelf);

            return new
            {
                employeeId = empId,
                submittedReviews = submitted,
                pendingReviews = pending,
                goalCount = goals.Count(g => g.EmployeeId == empId),
                willBeProvisional,
                reason = willBeProvisional
                    ? (onlySelf && !config.AllowSelfOnlyScore
                        ? "Yalnızca öz değerlendirme var."
                        : $"En az {config.MinReviewsForValidScore} değerlendirme gerekiyor.")
                    : null,
            };
        }).ToList();

        return Ok(new
        {
            cycleId = id,
            cycleName = cycle.Name,
            status = cycle.Status.ToString(),
            employeeCount = rows.Count,
            readyCount = rows.Count(r => !r.willBeProvisional),
            provisionalCount = rows.Count(r => r.willBeProvisional),
            pendingReviewTotal = rows.Sum(r => r.pendingReviews),
            employees = rows.OrderByDescending(r => r.willBeProvisional),
        });
    }
}

public record CreateCycleRequest(
    string Name, int Year, CyclePeriod Period, DateOnly StartDate, DateOnly EndDate);
public record ChangeCycleStatusRequest(CycleStatus Status);

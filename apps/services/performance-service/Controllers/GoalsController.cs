using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PerformanceService.Data;
using PerformanceService.Infrastructure;
using PerformanceService.Models;
using PerformanceService.Security;
using PerformanceService.Services;

namespace PerformanceService.Controllers;

[ApiController]
[Route("api/goals")]
[Authorize]
public class GoalsController : ControllerBase
{
    private readonly PerformanceDbContext _db;
    private readonly DirectoryClient _directory;

    public GoalsController(PerformanceDbContext db, DirectoryClient directory)
    {
        _db = db;
        _directory = directory;
    }

    /// <summary>
    /// Hedefleri listeler.
    ///
    /// GUVENLIK: onceki surumde bu uc herkese acikti - bir calisan
    /// employeeId parametresini degistirerek BASKA HERHANGI BIR calisanin
    /// hedeflerini gorebiliyordu, hatta employeeId hic vermeden TUM
    /// sirketin hedeflerini cekebiliyordu. Artik:
    ///   - Yonetici ve ustu: istedigi employeeId'yi (ya da hicbirini)
    ///     sorgulayabilir.
    ///   - Calisan: yalnizca KENDI employeeId'sini sorgulayabilir; farkli
    ///     bir ID verirse ya da hic vermezse 403 doner.
    /// </summary>
    /// <remarks>
    /// Sayfalama (G24): <c>page</c> verilmezse eski biçim (düz dizi, en yeni önce; sayı
    /// <c>X-Total-Count</c> başlığında). <c>page</c>/<c>pageSize</c> (en fazla 200) ile
    /// <c>{ items, total, page, pageSize }</c>. Ek filtreler: <c>status</c>, <c>q</c> (hedef başlığı).
    /// Sıralama: <c>sort</c>=createdAt|title|weight|status|dueDate, <c>dir</c>=asc|desc (varsayılan createdAt desc).
    /// </remarks>
    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] Guid? employeeId, [FromQuery] Guid? cycleId, CancellationToken ct,
        [FromQuery] int? page = null, [FromQuery] int? pageSize = null, [FromQuery] GoalStatus? status = null,
        [FromQuery] string? q = null, [FromQuery] string? sort = null, [FromQuery] string? dir = null)
    {
        var isManager = User.IsManagerOrAbove();

        if (!isManager)
        {
            var me = await _directory.FindMeAsync(ct);

            if (me is null)
                return Forbid();

            if (employeeId.HasValue && employeeId.Value != me.Id)
                return Forbid();

            employeeId = me.Id;   // employeeId verilmemisse de kendine sabitlenir
        }

        var query = _db.Goals.AsNoTracking().AsQueryable();
        if (employeeId.HasValue) query = query.Where(g => g.EmployeeId == employeeId.Value);
        if (cycleId.HasValue) query = query.Where(g => g.CycleId == cycleId.Value);
        if (status.HasValue) query = query.Where(g => g.Status == status.Value);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var like = Paging.FoldLike(q);
            query = query.Where(g => EF.Functions.Like(PerformanceDbContext.Fold(g.Title), like, "\\"));
        }

        var desc = sort is null || Paging.Desc(dir);
        IOrderedQueryable<Goal> ordered = (sort ?? "createdAt").ToLowerInvariant() switch
        {
            "title" => desc ? query.OrderByDescending(g => g.Title) : query.OrderBy(g => g.Title),
            "weight" => desc ? query.OrderByDescending(g => g.Weight) : query.OrderBy(g => g.Weight),
            "status" => desc ? query.OrderByDescending(g => g.Status) : query.OrderBy(g => g.Status),
            "duedate" => desc ? query.OrderByDescending(g => g.DueDate) : query.OrderBy(g => g.DueDate),
            _ => desc ? query.OrderByDescending(g => g.CreatedAt) : query.OrderBy(g => g.CreatedAt),
        };
        return await Paging.ListAsync(this, ordered.ThenBy(g => g.Id), page, pageSize, ct);
    }

    /// <summary>
    /// GUVENLIK: Onceden herkes herkese hedef ekleyebiliyor, herhangi bir hedefin
    /// ilerlemesini/durumunu (orn. kendi hedefini "Achieved") degistirebiliyordu -
    /// hedefler nihai puana girdigi icin calisan kendi puanini sisirebiliyordu.
    /// Hedefleri ve ilerlemeyi yonetici+ yonetir (web istemcisi de boyle calisir).
    /// </summary>
    [HttpPost]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> Create([FromBody] CreateGoalRequest request)
    {
        var cycle = await _db.Cycles.FirstOrDefaultAsync(c => c.Id == request.CycleId);
        if (cycle is null) return BadRequest("Değerlendirme dönemi bulunamadı");
        if (cycle.Status == CycleStatus.Closed) return BadRequest("Kapalı döneme hedef eklenemez");
        if (DueDateError(request.DueDate, cycle) is { } dueError) return BadRequest(new { message = dueError });

        var goal = new Goal
        {
            CycleId = request.CycleId,
            EmployeeId = request.EmployeeId,
            Title = request.Title,
            Description = request.Description,
            Weight = request.Weight,
            TargetValue = request.TargetValue,
            Unit = request.Unit,
            DueDate = request.DueDate,
            Status = GoalStatus.Active
        };
        _db.Goals.Add(goal);
        await _db.SaveChangesAsync();
        return Created($"/api/goals/{goal.Id}", goal);
    }

    /// <summary>
    /// Hedefi düzenler (başlık, açıklama, ağırlık, hedef değer, birim). Kapalı dönemin
    /// hedefi düzenlenemez. Gerçekleşen değer ve durum /progress ucundan değişir.
    /// </summary>
    [HttpPut("{id}")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateGoalRequest request, CancellationToken ct)
    {
        var goal = await _db.Goals.FirstOrDefaultAsync(g => g.Id == id, ct);
        if (goal is null) return NotFound();
        var cycle = await _db.Cycles.AsNoTracking().FirstOrDefaultAsync(c => c.Id == goal.CycleId, ct);
        if (cycle?.Status == CycleStatus.Closed)
            return Conflict(new { message = "Kapanmış dönemin hedefleri düzenlenemez" });
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Trim().Length > 200)
            return BadRequest(new { message = "Hedef başlığı zorunlu ve en fazla 200 karakter olabilir" });
        if (request.Weight is < 1 or > 100)
            return BadRequest(new { message = "Ağırlık 1-100 arasında olmalı" });
        if (request.TargetValue is < 0)
            return BadRequest(new { message = "Hedef değer negatif olamaz" });
        if (cycle is not null && DueDateError(request.DueDate, cycle) is { } dueError)
            return BadRequest(new { message = dueError });

        goal.Title = request.Title.Trim();
        goal.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        goal.Weight = request.Weight;
        goal.TargetValue = request.TargetValue;
        goal.Unit = string.IsNullOrWhiteSpace(request.Unit) ? null : request.Unit.Trim();
        goal.DueDate = request.DueDate;
        await _db.SaveChangesAsync(ct);
        return Ok(goal);
    }

    /// <summary>
    /// Son tarih boş bırakılabilir; verildiyse dönem başlangıcından önce ve (tanımlıysa)
    /// dönem bitişinden sonra olamaz. Hata yoksa null döner.
    /// </summary>
    public static string? DueDateError(DateOnly? dueDate, ReviewCycle cycle)
    {
        if (dueDate is not { } due) return null;
        if (cycle.EndDate != default && due > cycle.EndDate)
            return $"Son tarih dönem bitişinden ({cycle.EndDate:dd.MM.yyyy}) sonra olamaz";
        if (cycle.StartDate != default && due < cycle.StartDate)
            return $"Son tarih dönem başlangıcından ({cycle.StartDate:dd.MM.yyyy}) önce olamaz";
        return null;
    }

    /// <summary>Hedefi siler. Kapalı dönemin hedefi (nihai puana girmiş) silinemez.</summary>
    [HttpDelete("{id}")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var goal = await _db.Goals.FirstOrDefaultAsync(g => g.Id == id, ct);
        if (goal is null) return NotFound();
        var cycle = await _db.Cycles.AsNoTracking().FirstOrDefaultAsync(c => c.Id == goal.CycleId, ct);
        if (cycle?.Status == CycleStatus.Closed)
            return Conflict(new { message = "Kapanmış dönemin hedefleri silinemez" });
        _db.Goals.Remove(goal);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("{id}/progress")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> UpdateProgress(Guid id, [FromBody] UpdateProgressRequest request)
    {
        var goal = await _db.Goals.FirstOrDefaultAsync(g => g.Id == id);
        if (goal is null) return NotFound();
        var cycle = await _db.Cycles.FirstOrDefaultAsync(c => c.Id == goal.CycleId);
        if (cycle?.Status == CycleStatus.Closed)
            return BadRequest(new { message = "Kapanmış dönemin hedefleri güncellenemez" });
        if (request.CurrentValue is < 0)
            return BadRequest(new { message = "Gerçekleşen değer negatif olamaz" });
        if (request.Status.HasValue && !Enum.IsDefined(request.Status.Value))
            return BadRequest(new { message = "Geçerli bir durum seçin" });

        // Yalnizca durum degistiren istekte deger gonderilmez; mevcut deger korunur
        // (onceden eksik alan 0 sayilip gerceklesen deger sifirlaniyordu).
        if (request.CurrentValue.HasValue) goal.CurrentValue = request.CurrentValue.Value;
        if (request.Status.HasValue) goal.Status = request.Status.Value;
        await _db.SaveChangesAsync();
        return Ok(goal);
    }
}

public record CreateGoalRequest(
    Guid CycleId, Guid EmployeeId, string Title, string? Description,
    int Weight, decimal? TargetValue, string? Unit, DateOnly? DueDate = null);
/// <remarks>DueDate gönderilmezse (null) son tarih temizlenir; web istemcisi her düzenlemede mevcut değeri gönderir.</remarks>
public record UpdateGoalRequest(string Title, string? Description, int Weight, decimal? TargetValue, string? Unit, DateOnly? DueDate = null);
public record UpdateProgressRequest(decimal? CurrentValue, GoalStatus? Status);

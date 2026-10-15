using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PerformanceService.Data;
using PerformanceService.Models;
using PerformanceService.Security;
using PerformanceService.Services;

namespace PerformanceService.Controllers;

/// <summary>
/// Calisanlara yazilan geri bildirimler.
///
/// Tasarim tercihi: geri bildirim ANONIM DEGIL. Calisan kimin yazdigini
/// gorur. Anonimlik hesap sorulabilirligi zayiflatir ve kotuye kullanima
/// acik hale getirir. Gizli kalmasi gereken notlar icin
/// visibleToEmployee=false kullanilir; o zaman calisana hic gosterilmez.
/// </summary>
[ApiController]
[Route("api/feedback")]
[Authorize]
public class FeedbackController : ControllerBase
{
    private readonly PerformanceDbContext _db;
    private readonly DirectoryClient _directory;
    public FeedbackController(PerformanceDbContext db, DirectoryClient directory)
    {
        _db = db;
        _directory = directory;
    }

    /// <summary>
    /// Bir calisana gelen geri bildirimler.
    /// Calisan kendi kaydini goruyorsa yalnizca gorunur olanlar donduruler.
    /// </summary>
    [HttpGet("received/{employeeId}")]
    public async Task<IActionResult> Received(
        Guid employeeId,
        [FromQuery] bool asManager = false,
        [FromQuery] FeedbackReason? reason = null,
        [FromQuery] DateTimeOffset? since = null,
        CancellationToken ct = default)
    {
        // GUVENLIK: Onceden herhangi bir calisan, herhangi bir meslektasinin aldigi
        // geri bildirimleri okuyabiliyordu. Kisi yalnizca kendisine gelenleri (gizli
        // notlar haric); yonetici+ herkesinkini gorur.
        var isManager = User.IsManagerOrAbove();
        if (!isManager)
        {
            var me = await _directory.FindMeAsync(ct);
            if (me is null || me.Id != employeeId) return Forbid();
        }

        var q = _db.Feedback.Where(f => f.ToEmployeeId == employeeId);

        // Yonetici gorunumu degilse gizli notlar filtrelenir.
        if (!asManager || !isManager)
        {
            q = q.Where(f => f.VisibleToEmployee);
        }

        if (reason.HasValue) q = q.Where(f => f.Reason == reason.Value);
        if (since.HasValue) q = q.Where(f => f.CreatedAt >= since.Value);

        var list = await q.OrderByDescending(f => f.CreatedAt).ToListAsync();

        return Ok(list.Select(f => new
        {
            f.Id,
            f.FromEmployeeId,          // kimden geldigi acikca gorunur
            f.ToEmployeeId,
            f.CycleId,
            f.MetricId,
            reason = f.Reason.ToString(),
            f.ReasonDetail,
            sentiment = f.Sentiment.ToString(),
            f.Body,
            f.VisibleToEmployee,
            f.CreatedAt,
            f.ReadAt,
        }));
    }

    /// <summary>
    /// Bir kisinin yazdigi geri bildirimler.
    ///
    /// GUVENLIK: Onceden yalnizca [Authorize] - her calisan, bir yoneticinin
    /// YAZDIGI tum notlari, calisana GOSTERILMEYEN (VisibleToEmployee=false) gizli
    /// yonetici notlari dahil okuyabiliyordu (canli dogrulandi: Ayse, Mehmet'in
    /// kendisi hakkindaki "PIP dusunuluyor" notunu okudu). Yalnizca yazan kisi ve IK.
    /// </summary>
    [HttpGet("sent/{employeeId}")]
    public async Task<IActionResult> Sent(Guid employeeId, CancellationToken ct)
    {
        if (!User.IsHr())
        {
            var me = await _directory.FindMeAsync(ct);
            if (me is null || me.Id != employeeId) return Forbid();
        }
        return Ok(await _db.Feedback
            .Where(f => f.FromEmployeeId == employeeId)
            .OrderByDescending(f => f.CreatedAt)
            .ToListAsync(ct));
    }

    [HttpPost]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> Create([FromBody] CreateFeedbackRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Body))
            return BadRequest(new { message = "Geri bildirim metni bos olamaz" });
        if (request.Body.Length > 5000)
            return BadRequest(new { message = "Geri bildirim en fazla 5000 karakter olabilir" });

        // GUVENLIK: Yazar (FromEmployeeId) istemciden geliyordu - bir yonetici notu
        // baska birinin (orn. ust yoneticinin) adina yazabiliyordu. Yazar her zaman
        // token sahibidir.
        var me = await _directory.FindMeAsync(ct);
        if (me is null)
            return StatusCode(StatusCodes.Status403Forbidden,
                new { message = "Geri bildirim yazmak için çalışan kaydına bağlı bir hesap gerekli" });
        if (request.FromEmployeeId != Guid.Empty && request.FromEmployeeId != me.Id)
            return StatusCode(StatusCodes.Status403Forbidden,
                new { message = "Geri bildirim yalnızca kendi adınıza yazılabilir" });

        if (me.Id == request.ToEmployeeId)
            return BadRequest(new { message = "Kisi kendine geri bildirim yazamaz" });

        // Sebep aciklamasi, yapici/olumsuz geri bildirimde zorunlu:
        // "gelistirilmeli" deyip gerekce yazmamak ise yaramaz.
        if (request.Sentiment == FeedbackSentiment.Constructive &&
            string.IsNullOrWhiteSpace(request.ReasonDetail))
        {
            return BadRequest(new
            {
                message = "Yapıcı geri bildirimde gerekçe (reasonDetail) zorunludur",
            });
        }

        var feedback = new Feedback
        {
            FromEmployeeId = me.Id,
            ToEmployeeId = request.ToEmployeeId,
            CycleId = request.CycleId,
            MetricId = request.MetricId,
            Reason = request.Reason,
            ReasonDetail = request.ReasonDetail,
            Sentiment = request.Sentiment,
            Body = request.Body,
            VisibleToEmployee = request.VisibleToEmployee,
        };

        _db.Feedback.Add(feedback);
        await _db.SaveChangesAsync();
        return Created($"/api/feedback/{feedback.Id}", feedback);
    }

    [HttpPost("{id}/mark-read")]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken ct)
    {
        var f = await _db.Feedback.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (f is null) return NotFound();
        if (!f.VisibleToEmployee) return Forbid();
        // Yalnizca alici okundu isaretleyebilir.
        var me = await _directory.FindMeAsync(ct);
        if (me is null || me.Id != f.ToEmployeeId) return NotFound();

        f.ReadAt ??= DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(f);
    }

    /// <summary>
    /// Geri bildirim ozeti: hangi sebeple ne kadar geri bildirim veriliyor, ton
    /// dagilimi nasil. Yoneticiye ekibin geri bildirim kulturu hakkinda fikir verir.
    ///
    /// NOT: Onceden yalnizca yonetici+ - ama "Geri bildirim" ekrani calisana KENDI
    /// ozetini gosteriyor ve 403 aliyordu. Calisan kendi ozetini gorebilir (gizli
    /// notlar sayilmaz); tum kiraci/baskasinin ozeti yonetici+'ya ozel.
    /// </summary>
    [HttpGet("summary")]
    public async Task<IActionResult> Summary(
        [FromQuery] Guid? employeeId,
        [FromQuery] DateTimeOffset? since,
        CancellationToken ct = default)
    {
        var isManager = User.IsManagerOrAbove();
        if (!isManager)
        {
            var me = await _directory.FindMeAsync(ct);
            if (me is null || employeeId != me.Id) return Forbid();
        }
        var q = _db.Feedback.AsQueryable();
        if (!isManager) q = q.Where(f => f.VisibleToEmployee);
        if (employeeId.HasValue) q = q.Where(f => f.ToEmployeeId == employeeId.Value);
        if (since.HasValue) q = q.Where(f => f.CreatedAt >= since.Value);

        var items = await q.ToListAsync();

        return Ok(new
        {
            total = items.Count,
            unread = items.Count(f => f.ReadAt is null && f.VisibleToEmployee),
            bySentiment = items.GroupBy(f => f.Sentiment)
                .Select(g => new { sentiment = g.Key.ToString(), count = g.Count() }),
            byReason = items.GroupBy(f => f.Reason)
                .Select(g => new { reason = g.Key.ToString(), count = g.Count() })
                .OrderByDescending(x => x.count),
        });
    }
}

public record CreateFeedbackRequest(
    Guid FromEmployeeId,
    Guid ToEmployeeId,
    string Body,
    FeedbackReason Reason,
    string? ReasonDetail,
    FeedbackSentiment Sentiment,
    Guid? CycleId,
    Guid? MetricId,
    bool VisibleToEmployee = true);

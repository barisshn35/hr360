using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LeaveService.Data;
using LeaveService.Models;
using LeaveService.Services;

namespace LeaveService.Controllers;

/// <summary>
/// Resmi tatil takvimi. Herkes okuyabilir (izin formundaki gun onizlemesi icin);
/// yalnizca IK ekler/siler.
/// </summary>
[ApiController]
[Route("api/public-holidays")]
[Authorize]
public class PublicHolidaysController : ControllerBase
{
    private readonly LeaveDbContext _db;
    public PublicHolidaysController(LeaveDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] int? year, CancellationToken ct)
    {
        var q = _db.PublicHolidays.AsQueryable();
        if (year.HasValue) q = q.Where(h => h.Date.Year == year.Value);
        return Ok(await q.OrderBy(h => h.Date).ToListAsync(ct));
    }

    [HttpPost]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Create([FromBody] CreatePublicHolidayRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 120)
            return BadRequest(new { message = "Tatil adı zorunlu ve en fazla 120 karakter olabilir" });
        if (request.Date.Year is < 2000 or > 2100)
            return BadRequest(new { message = "Geçersiz tarih" });
        if (await _db.PublicHolidays.AnyAsync(h => h.Date == request.Date, ct))
            return Conflict(new { message = "Bu tarih zaten tatil olarak kayıtlı" });

        var h = new PublicHoliday { Date = request.Date, Name = request.Name.Trim(), IsHalfDay = request.IsHalfDay ?? false };
        _db.PublicHolidays.Add(h);
        await _db.SaveChangesAsync(ct);
        return Created($"/api/public-holidays/{h.Id}", h);
    }

    /// <summary>
    /// Verilen yilin Turkiye resmi tatillerini ekler (zaten kayitli tarihler atlanir). Dini bayramlar
    /// 2025-2030 icin gomulu tablodan (Services/TurkishHolidays, kaynak Diyanet); arifeler ve 28 Ekim
    /// yarim gun olarak eklenir (halfDays=false ile atlanir). Ayni gune denk gelen iki tatil tek kayittir.
    /// </summary>
    [HttpPost("seed-tr")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> SeedTurkish([FromQuery] int year, CancellationToken ct, [FromQuery] bool halfDays = true)
    {
        if (year is < 2000 or > 2100) return BadRequest(new { message = "Geçersiz yıl" });
        var (added, skipped, half) = await SeedAsync(_db, year, halfDays, ct);
        var hasReligious = TurkishHolidays.HasReligious(year);
        return Ok(new
        {
            added,
            skipped,
            halfDays = half,
            religiousIncluded = hasReligious,
            message = hasReligious
                ? $"{added} tatil eklendi."
                : $"{added} sabit tarihli tatil eklendi. {year} için Ramazan ve Kurban Bayramı tarihlerini elle girin.",
        });
    }

    /// <summary>Tohumlama çekirdeği (uç ve yıllık otomatik yükleme ortak): eklenen, atlanan, eklenen yarım gün.</summary>
    internal static async Task<(int Added, int Skipped, int Half)> SeedAsync(LeaveDbContext db, int year, bool halfDays, CancellationToken ct)
    {
        var days = TurkishHolidays.For(year, halfDays);
        var existingDates = (await db.PublicHolidays
            .Where(h => h.Date.Year == year).Select(h => h.Date).ToListAsync(ct)).ToHashSet();
        var added = 0;
        var half = 0;
        foreach (var d in days)
        {
            if (existingDates.Contains(d.Date)) continue;
            db.PublicHolidays.Add(new PublicHoliday { Date = d.Date, Name = d.Name, IsHalfDay = d.IsHalfDay });
            added++;
            if (d.IsHalfDay) half++;
        }
        await db.SaveChangesAsync(ct);
        return (added, days.Count - added, half);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var h = await _db.PublicHolidays.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (h is null) return NotFound();
        _db.PublicHolidays.Remove(h);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}

public record CreatePublicHolidayRequest(DateOnly Date, string Name, bool? IsHalfDay = null);

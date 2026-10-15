using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TimeShiftService.Data;
using TimeShiftService.Models;

namespace TimeShiftService.Controllers;

/// <summary>
/// Sirketin kendi vardiya donguleri (pattern) - "3 gece / 3 off / 3 gunduz"
/// gibi tekrarlanan diziler. Hic vardiya duzeni olmayan sirketler bu
/// modulu hic kullanmaz - Shift/ShiftAssignment (elle atama) yeterlidir.
/// </summary>
[ApiController]
[Route("api/shift-patterns")]
[Authorize]
public class ShiftPatternsController : ControllerBase
{
    private readonly TimeShiftDbContext _db;
    public ShiftPatternsController(TimeShiftDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var patterns = await _db.ShiftPatterns
            .Include(p => p.Days.OrderBy(d => d.DayIndex))
            .OrderBy(p => p.Name)
            .ToListAsync();
        return Ok(patterns);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var pattern = await _db.ShiftPatterns
            .Include(p => p.Days.OrderBy(d => d.DayIndex))
            .FirstOrDefaultAsync(p => p.Id == id);
        return pattern is null ? NotFound() : Ok(pattern);
    }

    public record PatternDayInput(PatternDayType Type, TimeOnly? StartTime, TimeOnly? EndTime);
    public record CreatePatternRequest(string Name, List<PatternDayInput> Days);

    private const int MaxPatternDays = 62;

    [HttpPost]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> Create([FromBody] CreatePatternRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { message = "Desen adı boş olamaz" });
        if (request.Days is null || request.Days.Count == 0)
            return BadRequest(new { message = "Desen en az bir gün içermeli" });
        if (request.Days.Count > MaxPatternDays)
            return BadRequest(new { message = $"Desen en fazla {MaxPatternDays} gün olabilir" });

        var invalid = request.Days.FirstOrDefault(d =>
            d.Type != PatternDayType.Off && (d.StartTime is null || d.EndTime is null));
        if (invalid is not null)
            return BadRequest(new { message = "Gündüz/gece günlerinde başlangıç-bitiş saati zorunlu" });

        var pattern = new ShiftPattern { Name = request.Name.Trim() };
        for (var i = 0; i < request.Days.Count; i++)
        {
            var d = request.Days[i];
            pattern.Days.Add(new ShiftPatternDay
            {
                DayIndex = i,
                Type = d.Type,
                StartTime = d.Type == PatternDayType.Off ? null : d.StartTime,
                EndTime = d.Type == PatternDayType.Off ? null : d.EndTime,
            });
        }

        _db.ShiftPatterns.Add(pattern);
        await _db.SaveChangesAsync();
        return Created($"/api/shift-patterns/{pattern.Id}", pattern);
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> Delete(Guid id)
    {
        if (await _db.ShiftTeams.AnyAsync(t => t.ShiftPatternId == id))
            return Conflict(new { message = "Bu deseni kullanan ekipler var - önce onları taşıyın veya silin" });

        var pattern = await _db.ShiftPatterns.FindAsync(id);
        if (pattern is null) return NotFound();

        _db.ShiftPatterns.Remove(pattern);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    public record UpdatePatternRequest(string? Name, bool? IsActive, List<PatternDayInput>? Days);

    /// <summary>
    /// Name/IsActive her zaman guncellenebilir. Days (gunlerin kendisi)
    /// SADECE hicbir ekip bu deseni kullanmiyorsa degistirilebilir - aksi
    /// halde kullanimdaki bir desenin gecmis/gelecek takvimini SESSIZCE
    /// bozar. Desen degismesi gerekiyorsa yeni bir desen olusturup
    /// ekipleri ona tasimak (ya da eskisini IsActive=false yapmak) dogru
    /// yol - bu yuzden "versiyon" yerine 409 tercih edildi.
    /// </summary>
    [HttpPut("{id}")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdatePatternRequest request)
    {
        var pattern = await _db.ShiftPatterns.Include(p => p.Days).FirstOrDefaultAsync(p => p.Id == id);
        if (pattern is null) return NotFound();

        if (!string.IsNullOrWhiteSpace(request.Name)) pattern.Name = request.Name.Trim();
        if (request.IsActive.HasValue) pattern.IsActive = request.IsActive.Value;

        if (request.Days is not null)
        {
            if (await _db.ShiftTeams.AnyAsync(t => t.ShiftPatternId == id))
                return Conflict(new
                {
                    message = "Bu deseni kullanan ekipler var - günler değiştirilemez. " +
                              "Yeni bir desen oluşturup ekipleri ona taşıyın.",
                });

            if (request.Days.Count == 0)
                return BadRequest(new { message = "Desen en az bir gün içermeli" });
            if (request.Days.Count > MaxPatternDays)
                return BadRequest(new { message = $"Desen en fazla {MaxPatternDays} gün olabilir" });

            var invalid = request.Days.FirstOrDefault(d =>
                d.Type != PatternDayType.Off && (d.StartTime is null || d.EndTime is null));
            if (invalid is not null)
                return BadRequest(new { message = "Gündüz/gece günlerinde başlangıç-bitiş saati zorunlu" });

            _db.ShiftPatternDays.RemoveRange(pattern.Days);
            pattern.Days.Clear();
            for (var i = 0; i < request.Days.Count; i++)
            {
                var d = request.Days[i];
                pattern.Days.Add(new ShiftPatternDay
                {
                    DayIndex = i,
                    Type = d.Type,
                    StartTime = d.Type == PatternDayType.Off ? null : d.StartTime,
                    EndTime = d.Type == PatternDayType.Off ? null : d.EndTime,
                });
            }
        }

        await _db.SaveChangesAsync();
        return Ok(pattern);
    }
}

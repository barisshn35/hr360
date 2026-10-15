using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TimeShiftService.Data;
using TimeShiftService.Models;

namespace TimeShiftService.Controllers;

/// <summary>
/// Holiday (resmi tatil) ve Manual (vardiya degisimi) istisnalarinin
/// elle yonetimi. Leave tipi BURADAN yonetilmez - LeaveEventConsumer
/// tarafindan otomatik olusturulur/guncellenir (IsSystemManaged=true),
/// bu uclardan silinemez/degistirilemez.
/// </summary>
[ApiController]
[Route("api/shift-overrides")]
[Authorize]
public class ShiftOverridesController : ControllerBase
{
    private readonly TimeShiftDbContext _db;
    public ShiftOverridesController(TimeShiftDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] DateOnly from, [FromQuery] DateOnly to)
    {
        var overrides = await _db.ShiftOverrides
            .Where(o => o.Date >= from && o.Date <= to)
            .OrderBy(o => o.Date)
            .ToListAsync();
        return Ok(overrides);
    }

    /// <summary>
    /// Hedef: employeeId (bir kisi), teamId (bir ekibin TUM aktif uyeleri)
    /// ya da hicbiri (tenant'taki TUM calisanlar - employee-service'e
    /// cross-service call gerektirmemek icin, bu durumda YALNIZCA su an
    /// timeshift-service'in bildigi calisanlar - yani herhangi bir
    /// ShiftTeamMember veya TimeEntry kaydinda gecen kisiler - kapsanir).
    /// Ayni employeeId+Date icin var olan bir override (Leave HARIC)
    /// UZERINE YAZILIR.
    /// </summary>
    public record CreateOverrideRequest(
        Guid? EmployeeId, Guid? TeamId, DateOnly From, DateOnly To,
        ShiftOverrideType Type, string? Note, TimeOnly? StartTime, TimeOnly? EndTime);

    [HttpPost]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> Create([FromBody] CreateOverrideRequest request)
    {
        if (request.Type == ShiftOverrideType.Leave)
            return BadRequest(new { message = "Leave override'ları izin onayıyla otomatik oluşur, elle eklenemez" });
        if (request.To < request.From)
            return BadRequest(new { message = "Bitiş tarihi başlangıçtan önce olamaz" });
        // NOT: Aralik sinirsizdi - 0001-01-01..9999-12-31 (calisan bos = tum calisanlar)
        // tek istekte milyarlarca satir uretmeye calisip servisi kilitliyordu.
        if (request.To.DayNumber - request.From.DayNumber > 366)
            return BadRequest("Tek seferde en fazla 1 yıllık aralık girilebilir");
        if (request.Type == ShiftOverrideType.Manual && (request.StartTime is null || request.EndTime is null))
            return BadRequest(new { message = "Manuel değişimde başlangıç-bitiş saati zorunlu" });

        List<Guid> targetEmployeeIds;
        if (request.EmployeeId.HasValue)
        {
            targetEmployeeIds = new List<Guid> { request.EmployeeId.Value };
        }
        else if (request.TeamId.HasValue)
        {
            targetEmployeeIds = await _db.ShiftTeamMembers
                .Where(m => m.ShiftTeamId == request.TeamId.Value && m.EffectiveTo == null)
                .Select(m => m.EmployeeId)
                .ToListAsync();
        }
        else
        {
            targetEmployeeIds = await _db.ShiftTeamMembers
                .Where(m => m.EffectiveTo == null)
                .Select(m => m.EmployeeId)
                .Distinct()
                .ToListAsync();
        }

        if (targetEmployeeIds.Count == 0)
            return BadRequest(new { message = "Hedeflenen çalışan bulunamadı" });

        var created = new List<ShiftOverride>();
        for (var date = request.From; date <= request.To; date = date.AddDays(1))
        {
            foreach (var employeeId in targetEmployeeIds)
            {
                var existing = await _db.ShiftOverrides.FirstOrDefaultAsync(o =>
                    o.EmployeeId == employeeId && o.Date == date);

                // Sistem yonetimli (Leave) override'in ustune YAZILMAZ -
                // izin onceligi vardir, elle tatil/manuel eklense bile.
                if (existing is not null && existing.IsSystemManaged) continue;

                if (existing is not null)
                {
                    existing.Type = request.Type;
                    existing.Note = request.Note;
                    existing.StartTime = request.Type == ShiftOverrideType.Manual ? request.StartTime : null;
                    existing.EndTime = request.Type == ShiftOverrideType.Manual ? request.EndTime : null;
                    created.Add(existing);
                }
                else
                {
                    var over = new ShiftOverride
                    {
                        EmployeeId = employeeId,
                        Date = date,
                        Type = request.Type,
                        Note = request.Note,
                        StartTime = request.Type == ShiftOverrideType.Manual ? request.StartTime : null,
                        EndTime = request.Type == ShiftOverrideType.Manual ? request.EndTime : null,
                    };
                    _db.ShiftOverrides.Add(over);
                    created.Add(over);
                }
            }
        }

        await _db.SaveChangesAsync();
        return Ok(created);
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var over = await _db.ShiftOverrides.FirstOrDefaultAsync(o => o.Id == id);
        if (over is null) return NotFound();

        if (over.IsSystemManaged)
            return Conflict(new { message = "Bu, izin onayıyla otomatik oluşmuş bir kayıt - elle silinemez" });

        _db.ShiftOverrides.Remove(over);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}

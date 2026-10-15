using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LeaveService.Data;
using LeaveService.Models;
using LeaveService.Services;

namespace LeaveService.Controllers;

/// <summary>
/// Dalga 9: şirketin izin ayarları — saatlik izinde günlük çalışma saati, ekip çakışma uyarısı eşiği,
/// yıllık izin devrinde önerilen üst sınır. Herkes okur (izin formu günlük saati ve uyarı eşiğini
/// gösterir); yalnızca İK değiştirir. Değişiklik denetim kaydına yazılır (AuditInterceptor).
/// </summary>
[ApiController]
[Route("api/leave-settings")]
[Authorize]
public class LeaveSettingsController : ControllerBase
{
    private readonly LeaveDbContext _db;
    public LeaveSettingsController(LeaveDbContext db) => _db = db;

    private static object Dto(LeaveSettings s) => new
    {
        dayHours = LeaveEntitlement.EffectiveDayHours(s.DayHours), customDayHours = s.DayHours,
        s.ConflictWarnEnabled, s.ConflictThresholdPercent, s.CarryOverMaxDays,
    };

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct) =>
        Ok(Dto(await _db.LeaveSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new LeaveSettings()));

    public record SettingsInput(decimal? DayHours, bool ConflictWarnEnabled, int ConflictThresholdPercent, decimal? CarryOverMaxDays);

    [HttpPut]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Put([FromBody] SettingsInput b, CancellationToken ct)
    {
        if (b.DayHours is { } h && (h < 1 || h > 12 || h * 2 != Math.Floor(h * 2)))
            return BadRequest(new { message = "Günlük çalışma saati 1-12 arasında, 0,5 adımlarla olmalı" });
        if (b.ConflictThresholdPercent is < 1 or > 100) return BadRequest(new { message = "Çakışma eşiği %1-100 olmalı" });
        if (b.CarryOverMaxDays is < 0 or > 365) return BadRequest(new { message = "Devir üst sınırı 0-365 gün olmalı" });
        var s = await _db.LeaveSettings.FirstOrDefaultAsync(ct);
        if (s is null) { s = new LeaveSettings(); _db.LeaveSettings.Add(s); }
        s.DayHours = b.DayHours;
        s.ConflictWarnEnabled = b.ConflictWarnEnabled;
        s.ConflictThresholdPercent = b.ConflictThresholdPercent;
        s.CarryOverMaxDays = b.CarryOverMaxDays;
        s.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(Dto(s));
    }
}

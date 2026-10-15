using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PerformanceService.Data;
using PerformanceService.Models;
using PerformanceService.Security;
using PerformanceService.Services;

namespace PerformanceService.Controllers;

/// <summary>
/// G12 dönem şablonları: bir dönemin yapılandırmasını (ad, bölümler/sorular, ağırlıklar, ölçek)
/// şablon olarak saklar; yeni dönem şablondan açılır. Yazma İK'ya, okuma yöneticilere açık.
/// </summary>
[ApiController]
[Route("api/review-cycles")]
[Authorize]
public class CycleTemplatesController : ControllerBase
{
    private readonly PerformanceDbContext _db;
    private readonly SnapshotService _snapshots;

    public CycleTemplatesController(PerformanceDbContext db, SnapshotService snapshots)
    {
        _db = db; _snapshots = snapshots;
    }

    private static object View(CycleTemplate t) => new
    {
        t.Id, t.Name, t.Description, period = t.Period.ToString(), t.DurationDays, config = CycleConfig.Parse(t.ConfigJson), t.CreatedBy, t.CreatedAt,
    };

    [HttpGet("templates")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok((await _db.CycleTemplates.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct)).Select(View));

    public record TemplateInput(string Name, string? Description, CyclePeriod Period, int DurationDays, CycleConfig Config);

    private static string? Validate(string? name, string? description, int durationDays, CycleConfig? config)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length < 2 || name.Length > 150) return "Şablon adı 2-150 karakter olmalı";
        if (description is { Length: > 500 }) return "Açıklama en fazla 500 karakter olabilir";
        if (durationDays is < 1 or > 400) return "Süre 1-400 gün olmalı";
        if (config is null) return "Şablon yapılandırması eksik";
        return config.Validate();
    }

    [HttpPost("templates")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Create([FromBody] TemplateInput body, CancellationToken ct)
    {
        if (!Enum.IsDefined(body.Period)) return BadRequest(new { message = "Geçersiz dönem türü" });
        var err = Validate(body.Name, body.Description, body.DurationDays, body.Config);
        if (err is not null) return BadRequest(new { message = err });
        var name = body.Name.Trim();
        if (await _db.CycleTemplates.AnyAsync(t => t.Name.ToLower() == name.ToLower(), ct))
            return Conflict(new { message = "Bu adla bir şablon zaten var" });
        var t = new CycleTemplate
        {
            Name = name, Description = body.Description?.Trim(), Period = body.Period, DurationDays = body.DurationDays,
            ConfigJson = body.Config.ToJson(), CreatedBy = PerfPeople.DisplayName(User),
        };
        _db.CycleTemplates.Add(t);
        await _db.SaveChangesAsync(ct);
        return Ok(View(t));
    }

    [HttpDelete("templates/{id:guid}")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var t = await _db.CycleTemplates.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return NotFound();
        _db.CycleTemplates.Remove(t);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// Dönemin yapılandırmasından varsayılan şablon içeriği: dönemde kayıtlı yapılandırma varsa o;
    /// yoksa etkin metrikler kategori bölümleri olarak, kategori ağırlıkları %100'e ölçeklenerek.
    /// </summary>
    private async Task<CycleConfig> ConfigOfAsync(ReviewCycle cycle, CancellationToken ct)
    {
        var existing = CycleConfig.Parse(cycle.ConfigJson);
        if (existing is not null && existing.Validate() is null) return existing;
        var scoring = await _snapshots.ActiveConfigAsync(ct);
        var metrics = await _db.Metrics.AsNoTracking().Where(m => m.IsActive).OrderBy(m => m.SortOrder).ToListAsync(ct);
        var groups = metrics.GroupBy(m => m.Category).Where(g => scoring.WeightFor(g.Key) > 0).ToList();
        var cfg = new CycleConfig { GoalWeightPercent = scoring.GoalWeightPercent };
        if (groups.Count == 0) return cfg;
        var raw = groups.Select(g => scoring.WeightFor(g.Key)).ToList();
        var total = raw.Sum();
        var weights = raw.Select(w => (int)Math.Floor(100 * w / total)).ToList();
        // En büyük kalan yöntemiyle toplamı 100'e tamamla.
        var remainders = raw.Select((w, i) => (i, r: 100 * w / total - weights[i])).OrderByDescending(x => x.r).ToList();
        for (var k = 0; weights.Sum() < 100; k++) weights[remainders[k % remainders.Count].i]++;
        cfg.Sections = groups.Select((g, i) => new CycleConfig.SectionConfig
        {
            Title = g.Key.ToString(), Weight = weights[i],
            Questions = g.Select(m => new CycleConfig.QuestionConfig { Text = m.Name.Length > 500 ? m.Name[..500] : m.Name }).Take(50).ToList(),
        }).ToList();
        return cfg;
    }

    public record SaveAsTemplateInput(string Name, string? Description);

    [HttpPost("{id:guid}/save-as-template")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> SaveAsTemplate(Guid id, [FromBody] SaveAsTemplateInput body, CancellationToken ct)
    {
        var cycle = await _db.Cycles.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (cycle is null) return NotFound();
        var config = await ConfigOfAsync(cycle, ct);
        var duration = cycle.EndDate.DayNumber - cycle.StartDate.DayNumber + 1;
        var err = Validate(body.Name, body.Description, duration, config);
        if (err is not null) return BadRequest(new { message = err });
        var name = body.Name.Trim();
        if (await _db.CycleTemplates.AnyAsync(t => t.Name.ToLower() == name.ToLower(), ct))
            return Conflict(new { message = "Bu adla bir şablon zaten var" });
        var t = new CycleTemplate
        {
            Name = name, Description = body.Description?.Trim(), Period = cycle.Period, DurationDays = duration,
            ConfigJson = config.ToJson(), CreatedBy = PerfPeople.DisplayName(User),
        };
        _db.CycleTemplates.Add(t);
        await _db.SaveChangesAsync(ct);
        return Ok(View(t));
    }

    public record FromTemplateInput(Guid TemplateId, string? Name, int Year, CyclePeriod? Period, DateOnly StartDate, DateOnly? EndDate);

    /// <summary>Şablondan yeni (taslak) dönem: yapılandırma kopyalanır, bitiş = başlangıç + süre.</summary>
    [HttpPost("from-template")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> FromTemplate([FromBody] FromTemplateInput body, CancellationToken ct)
    {
        var t = await _db.CycleTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == body.TemplateId, ct);
        if (t is null) return NotFound(new { message = "Şablon bulunamadı" });
        if (body.Year is < 2000 or > 2100) return BadRequest(new { message = "Geçersiz yıl" });
        if (body.Period is { } p && !Enum.IsDefined(p)) return BadRequest(new { message = "Geçersiz dönem türü" });
        var end = body.EndDate ?? body.StartDate.AddDays(t.DurationDays - 1);
        if (end <= body.StartDate) return BadRequest(new { message = "Bitiş tarihi başlangıçtan sonra olmalı" });
        var name = string.IsNullOrWhiteSpace(body.Name) ? $"{t.Name} {body.Year}" : body.Name.Trim();
        if (name.Length > 200) return BadRequest(new { message = "Dönem adı en fazla 200 karakter olabilir" });
        var cycle = new ReviewCycle
        {
            Name = name, Year = body.Year, Period = body.Period ?? t.Period, StartDate = body.StartDate, EndDate = end,
            TemplateId = t.Id, ConfigJson = t.ConfigJson,
        };
        _db.Cycles.Add(cycle);
        await _db.SaveChangesAsync(ct);
        return Ok(cycle);
    }
}

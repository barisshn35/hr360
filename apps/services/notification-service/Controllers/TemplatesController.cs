using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NotificationService.Data;
using NotificationService.Models;

namespace NotificationService.Controllers;

[ApiController]
[Route("api/notification-templates")]
[Authorize(Policy = "RequireHrAdmin")]
public class TemplatesController : ControllerBase
{
    private readonly NotificationDbContext _db;
    public TemplatesController(NotificationDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> GetAll()
        // NOT: "Sil" yalnizca IsActive=false yapiyor; liste bunu filtrelemedigi icin
        // silinen sablonlar arayuzde hic kaybolmuyordu.
        => Ok(await _db.Templates.Where(t => t.IsActive).OrderBy(t => t.Code).ToListAsync());

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateTemplateRequest request)
    {
        var locale = request.Locale ?? "tr";
        if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.BodyTemplate))
            return BadRequest("Şablon kodu ve gövdesi zorunlu");
        var existing = await _db.Templates.FirstOrDefaultAsync(t =>
            t.Code == request.Code && t.Channel == request.Channel && t.Locale == locale);
        if (existing is { IsActive: true })
            return Conflict("Bu kod/kanal/dil kombinasyonu icin sablon zaten var");
        if (existing is not null)
        {
            // Silinmis (pasif) sablonla ayni kod yeniden eklenince benzersiz indekse
            // carpip 500 vermek yerine eski kayit yeniden etkinlestirilir.
            existing.SubjectTemplate = request.SubjectTemplate;
            existing.BodyTemplate = request.BodyTemplate;
            existing.IsActive = true;
            await _db.SaveChangesAsync();
            return Ok(existing);
        }

        var tpl = new NotificationTemplate
        {
            Code = request.Code,
            Channel = request.Channel,
            Locale = locale,
            SubjectTemplate = request.SubjectTemplate,
            BodyTemplate = request.BodyTemplate
        };
        _db.Templates.Add(tpl);
        await _db.SaveChangesAsync();
        return Created($"/api/notification-templates/{tpl.Id}", tpl);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Deactivate(Guid id)
    {
        var tpl = await _db.Templates.FirstOrDefaultAsync(t => t.Id == id);
        if (tpl is null) return NotFound();
        tpl.IsActive = false;
        await _db.SaveChangesAsync();
        return NoContent();
    }
}

public record CreateTemplateRequest(
    string Code, NotificationChannel Channel, string? Locale,
    string? SubjectTemplate, string BodyTemplate);

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OnboardingService.Data;
using OnboardingService.Models;
using OnboardingService.Services;

namespace OnboardingService.Controllers;

/// <summary>
/// G14: rol (unvan/departman) bazli gorev sablonlari ve kiraci ayarlari (ilk gun
/// karsilama sablonu, zimmet hatirlatma sorumlusu). Okuma: ise alismayi yonetenler;
/// yazma: IK.
/// </summary>
[ApiController]
[Route("api/onboarding-config")]
[Authorize(Policy = "RequireOnboardingManage")]
public class OnboardingConfigController : ControllerBase
{
    private readonly OnboardingDbContext _db;
    public OnboardingConfigController(OnboardingDbContext db) => _db = db;

    private bool IsHr => User.IsInRole("hr-admin") || User.IsInRole("tenant-admin") || User.IsInRole("platform-admin")
        || User.IsInRole("ext-onboarding-manage");

    public record ItemInput(string Title, TaskCategory Category, string OwnerRole, int OffsetDays);
    public record TemplateInput(string Name, string? PositionTitle, Guid? DepartmentId, bool IsActive, List<ItemInput> Items);

    [HttpGet("templates")]
    public async Task<IActionResult> Templates(CancellationToken ct) =>
        Ok(await _db.TaskTemplates.AsNoTracking().Include(t => t.Items.OrderBy(i => i.Order))
            .OrderBy(t => t.Name).ToListAsync(ct));

    private static string? Validate(TemplateInput b)
    {
        if (string.IsNullOrWhiteSpace(b.Name) || b.Name.Length > 120) return "Şablon adı 1-120 karakter olmalı";
        if (b.Items is null || b.Items.Count == 0) return "En az bir görev gerekli";
        if (b.Items.Count > 60) return "Bir şablonda en fazla 60 görev olabilir";
        foreach (var i in b.Items)
        {
            if (string.IsNullOrWhiteSpace(i.Title) || i.Title.Length > 200) return "Görev başlığı 1-200 karakter olmalı";
            if (!OwnerRoles.All.Contains(i.OwnerRole)) return "Görev sahibi HR, Manager, IT, Buddy ya da Employee olmalı";
            if (i.OffsetDays is < -60 or > 365) return "Gün farkı -60 ile 365 arasında olmalı";
        }
        return null;
    }

    private static void Fill(TaskTemplate t, TemplateInput b)
    {
        t.Name = b.Name.Trim();
        t.PositionTitle = string.IsNullOrWhiteSpace(b.PositionTitle) ? null : b.PositionTitle.Trim();
        t.DepartmentId = b.DepartmentId;
        t.IsActive = b.IsActive;
    }

    [HttpPost("templates")]
    public async Task<IActionResult> Create([FromBody] TemplateInput b, CancellationToken ct)
    {
        if (!IsHr) return Forbid();
        if (Validate(b) is { } err) return BadRequest(new { message = err });
        var t = new TaskTemplate { Name = b.Name.Trim() };
        Fill(t, b);
        var order = 1;
        foreach (var i in b.Items)
            t.Items.Add(new TaskTemplateItem { Title = i.Title.Trim(), Category = i.Category, OwnerRole = i.OwnerRole, OffsetDays = i.OffsetDays, Order = order++ });
        _db.TaskTemplates.Add(t);
        await _db.SaveChangesAsync(ct);
        return Ok(t);
    }

    [HttpPut("templates/{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] TemplateInput b, CancellationToken ct)
    {
        if (!IsHr) return Forbid();
        if (Validate(b) is { } err) return BadRequest(new { message = err });
        var t = await _db.TaskTemplates.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return NotFound();
        Fill(t, b);
        _db.TaskTemplateItems.RemoveRange(t.Items);
        var order = 1;
        foreach (var i in b.Items)
            _db.TaskTemplateItems.Add(new TaskTemplateItem { TemplateId = t.Id, Title = i.Title.Trim(), Category = i.Category, OwnerRole = i.OwnerRole, OffsetDays = i.OffsetDays, Order = order++ });
        await _db.SaveChangesAsync(ct);
        return Ok(await _db.TaskTemplates.AsNoTracking().Include(x => x.Items.OrderBy(i => i.Order)).FirstAsync(x => x.Id == id, ct));
    }

    [HttpDelete("templates/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!IsHr) return Forbid();
        var t = await _db.TaskTemplates.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return NotFound();
        _db.TaskTemplates.Remove(t);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    public record SettingsInput(string? WelcomeSubject, string? WelcomeBody, Guid? HrContactEmployeeId, int? ReminderDaysBefore);

    [HttpGet("settings")]
    public async Task<IActionResult> GetSettings(CancellationToken ct)
    {
        var s = await _db.Settings.AsNoTracking().FirstOrDefaultAsync(ct);
        return Ok(new
        {
            welcomeSubject = s?.WelcomeSubject, welcomeBody = s?.WelcomeBody,
            hrContactEmployeeId = s?.HrContactEmployeeId, reminderDaysBefore = s?.ReminderDaysBefore ?? 3,
            defaultSubject = WelcomeTemplate.DefaultSubject, defaultBody = WelcomeTemplate.DefaultBody,
            placeholders = new[] { "{ad}", "{baslangic}", "{yonetici}", "{buddy}", "{konum}" },
        });
    }

    [HttpPut("settings")]
    public async Task<IActionResult> PutSettings([FromBody] SettingsInput b, CancellationToken ct)
    {
        if (!IsHr) return Forbid();
        if (b.WelcomeSubject is { Length: > 200 }) return BadRequest(new { message = "Konu en fazla 200 karakter olabilir" });
        if (b.WelcomeBody is { Length: > 4000 }) return BadRequest(new { message = "İleti en fazla 4000 karakter olabilir" });
        if (b.ReminderDaysBefore is < 0 or > 60) return BadRequest(new { message = "Hatırlatma 0-60 gün önce olabilir" });
        var s = await _db.Settings.FirstOrDefaultAsync(ct);
        if (s is null) { s = new OnboardingSettings(); _db.Settings.Add(s); }
        s.WelcomeSubject = string.IsNullOrWhiteSpace(b.WelcomeSubject) ? null : b.WelcomeSubject.Trim();
        s.WelcomeBody = string.IsNullOrWhiteSpace(b.WelcomeBody) ? null : b.WelcomeBody.Trim();
        s.HrContactEmployeeId = b.HrContactEmployeeId;
        s.ReminderDaysBefore = b.ReminderDaysBefore ?? s.ReminderDaysBefore;
        s.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { s.WelcomeSubject, s.WelcomeBody, s.HrContactEmployeeId, s.ReminderDaysBefore });
    }
}

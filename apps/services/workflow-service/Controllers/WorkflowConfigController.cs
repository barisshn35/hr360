using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WorkflowService.Data;
using WorkflowService.Models;
using WorkflowService.Services;

namespace WorkflowService.Controllers;

/// <summary>
/// Akış tanımları (görsel akış tasarımcısı, Y22) ve vekâlet (Y23).
/// </summary>
[ApiController]
[Route("api/workflows")]
[Authorize]
public class WorkflowConfigController : ControllerBase
{
    private readonly WorkflowDbContext _db;
    private readonly EmployeeDirectoryClient _employees;
    private readonly WorkflowRouting _routing;

    public WorkflowConfigController(WorkflowDbContext db, EmployeeDirectoryClient employees, WorkflowRouting routing)
    {
        _db = db; _employees = employees; _routing = routing;
    }

    private bool IsHr => User.IsInRole("hr-admin") || User.IsInRole("tenant-admin") || User.IsInRole("platform-admin");

    // ------------------------------------------------------------------ akış tanımları

    public record DefinitionInput(WorkflowType Type, string Name, bool IsActive, List<DefinitionStep> Steps, List<string>? HiddenFields);

    private static object Dto(WorkflowDefinition d) => new
    {
        d.Id, type = d.Type.ToString(), d.Name, d.IsActive, d.UpdatedAt,
        steps = JsonSerializer.Deserialize<List<DefinitionStep>>(d.StepsJson, WorkflowRouting.Json),
        hiddenFields = JsonSerializer.Deserialize<List<string>>(d.HiddenFieldsJson) ?? new(),
    };

    [HttpGet("definitions")]
    public async Task<IActionResult> Definitions(CancellationToken ct)
    {
        if (!IsHr) return Forbid();
        var rows = await _db.Definitions.AsNoTracking().OrderBy(d => d.Type).ThenBy(d => d.Name).ToListAsync(ct);
        return Ok(rows.Select(Dto));
    }

    /// <summary>
    /// Tanım doğrulama ve KVKK uyarıları: belirli bir kişiye giden adım, talep içeriğini o kişiyle
    /// paylaşır; hastalık izni gibi hassas türlerde gerekçe alanının gizlenmesi önerilir.
    /// </summary>
    private static (string? Error, List<string> Warnings) Check(DefinitionInput b)
    {
        var w = new List<string>();
        if (string.IsNullOrWhiteSpace(b.Name) || b.Name.Length > 120) return ("Ad gerekli (en fazla 120 karakter)", w);
        if (b.Steps is null || b.Steps.Count is 0 or > 10) return ("1-10 adım tanımlayın", w);
        for (var i = 0; i < b.Steps.Count; i++)
        {
            var s = b.Steps[i];
            if (s.Approver == ApproverKind.Employee && (s.EmployeeId is null || s.EmployeeId == Guid.Empty))
                return ($"Adım {i + 1}: onaycı kişi seçilmeli", w);
            if (s.ConditionField is not null && (s.ConditionValue is null || s.ConditionOp is not (">" or ">=" or "<" or "<=")))
                return ($"Adım {i + 1}: koşul eksik", w);
            if (s.SlaHours is < 1 or > 24 * 30) return ($"Adım {i + 1}: süre 1 saat ile 30 gün arasında olmalı", w);
            if (s.Approver == ApproverKind.Employee)
                w.Add($"Adım {i + 1} belirli bir kişiye gidiyor: talep içeriği bu kişiyle de paylaşılır. Gerekli değilse bölüm başı adımını kullanın.");
        }
        if (b.Steps.All(s => s.ConditionField is not null))
            w.Add("Tüm adımlar koşullu: hiçbir koşul sağlanmazsa talep hizmetin varsayılan onaycısına (bölüm başı) gider.");
        var hidden = b.HiddenFields ?? new();
        if (b.Type == WorkflowType.LeaveRequest && !hidden.Contains("reason"))
            w.Add("İzin taleplerinde gerekçe alanı sağlık bilgisi içerebilir; onaycılardan gizlemeniz önerilir.");
        if (b.Steps.Count > 3)
            w.Add("3'ten fazla adım: talep içeriği daha çok kişiye açılır ve karar süresi uzar.");
        return (null, w);
    }

    [HttpPost("definitions/validate")]
    public IActionResult Validate([FromBody] DefinitionInput b)
    {
        if (!IsHr) return Forbid();
        var (err, warnings) = Check(b);
        return Ok(new { valid = err is null, error = err, warnings });
    }

    [HttpPost("definitions")]
    public async Task<IActionResult> Create([FromBody] DefinitionInput b, CancellationToken ct) => await SaveAsync(null, b, ct);

    [HttpPut("definitions/{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] DefinitionInput b, CancellationToken ct) => await SaveAsync(id, b, ct);

    private async Task<IActionResult> SaveAsync(Guid? id, DefinitionInput b, CancellationToken ct)
    {
        if (!IsHr) return Forbid();
        var (err, warnings) = Check(b);
        if (err is not null) return BadRequest(new { message = err });
        WorkflowDefinition? d;
        if (id is null) { d = new WorkflowDefinition(); _db.Definitions.Add(d); }
        else if ((d = await _db.Definitions.FirstOrDefaultAsync(x => x.Id == id, ct)) is null) return NotFound();
        d.Type = b.Type; d.Name = b.Name.Trim(); d.IsActive = b.IsActive;
        d.StepsJson = JsonSerializer.Serialize(b.Steps, WorkflowRouting.Json);
        d.HiddenFieldsJson = JsonSerializer.Serialize((b.HiddenFields ?? new()).Where(f => f is { Length: > 0 and < 40 }).Distinct().ToList());
        d.UpdatedAt = DateTimeOffset.UtcNow;
        // Bir türde yalnızca bir etkin tanım olur.
        if (d.IsActive)
            await _db.Definitions.Where(x => x.Type == b.Type && x.Id != d.Id && x.IsActive)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.IsActive, false), ct);
        await _db.SaveChangesAsync(ct);
        return Ok(new { definition = Dto(d), warnings });
    }

    [HttpDelete("definitions/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!IsHr) return Forbid();
        var d = await _db.Definitions.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (d is null) return NotFound();
        _db.Definitions.Remove(d);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    public record PreviewInput(WorkflowType Type, Guid RequesterEmployeeId, decimal? Days, decimal? Amount, decimal? Hours);

    /// <summary>Tasarımcıda "bu kişi için kim onaylar?" ön izlemesi.</summary>
    [HttpPost("definitions/preview")]
    public async Task<IActionResult> Preview([FromBody] PreviewInput b, CancellationToken ct)
    {
        if (!IsHr) return Forbid();
        var payload = JsonSerializer.Serialize(new { days = b.Days, amount = b.Amount, hours = b.Hours });
        var chain = await _routing.ResolveAsync(_db.CurrentTenantSlug ?? "", b.Type, b.RequesterEmployeeId, payload, ct);
        return Ok(new { usesDefinition = chain is not null, approvers = chain?.Select(c => new { employeeId = c.Approver, c.SlaHours }) ?? Enumerable.Empty<object>() });
    }

    /// <summary>Onaycıya gösterilmeyecek alanlar (sohbet ve web, talep türüne göre).</summary>
    [HttpGet("definitions/hidden-fields")]
    public async Task<IActionResult> HiddenFields([FromQuery] WorkflowType type, CancellationToken ct)
    {
        var d = await _db.Definitions.AsNoTracking().Where(x => x.Type == type && x.IsActive).FirstOrDefaultAsync(ct);
        return Ok(d is null ? new List<string>() : JsonSerializer.Deserialize<List<string>>(d.HiddenFieldsJson) ?? new());
    }

    // ------------------------------------------------------------------ onay ayarları

    public record SettingsInput(Guid? HrApproverEmployeeId);

    /// <summary>
    /// İK onaycısı: üst onaycısı bulunamayan talepler (departman başının kendi izni gibi) ve
    /// süre aşımında iletilecek üst yönetici yoksa talepler bu kişiye gider.
    /// </summary>
    [HttpGet("settings")]
    public async Task<IActionResult> GetSettings(CancellationToken ct)
    {
        if (!IsHr) return Forbid();
        if (string.IsNullOrEmpty(_db.CurrentTenantSlug)) return Ok(new { hrApproverEmployeeId = (Guid?)null, updatedAt = (DateTimeOffset?)null });
        var s = await _db.Settings.AsNoTracking().FirstOrDefaultAsync(ct);
        return Ok(new { hrApproverEmployeeId = s?.HrApproverEmployeeId, updatedAt = s?.UpdatedAt });
    }

    [HttpPut("settings")]
    public async Task<IActionResult> SaveSettings([FromBody] SettingsInput b, CancellationToken ct)
    {
        if (!IsHr) return Forbid();
        var tenant = _db.CurrentTenantSlug;
        if (string.IsNullOrEmpty(tenant)) return BadRequest(new { message = "Kiracı belirtilmedi" });
        if (b.HrApproverEmployeeId is { } id
            && !(await _routing.ActiveLoginEmployeesAsync(tenant, new[] { id }, ct)).Contains(id))
            return BadRequest(new { message = "İK onaycısı HR360 giriş hesabı olan, aktif bir çalışan olmalı" });
        var s = await _db.Settings.FirstOrDefaultAsync(ct);
        if (s is null) { s = new WorkflowSettings(); _db.Settings.Add(s); }
        s.HrApproverEmployeeId = b.HrApproverEmployeeId;
        s.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { hrApproverEmployeeId = s.HrApproverEmployeeId, updatedAt = s.UpdatedAt });
    }

    // ------------------------------------------------------------------ vekâlet

    public record DelegationInput(Guid? FromEmployeeId, Guid ToEmployeeId, DateOnly StartDate, DateOnly EndDate, string? Reason);

    [HttpGet("delegations")]
    public async Task<IActionResult> Delegations([FromQuery] bool all, CancellationToken ct)
    {
        var q = _db.Delegations.AsNoTracking();
        if (!(all && IsHr))
        {
            var me = await _employees.FindMyEmployeeIdAsync(ct);
            if (me is null) return Ok(Array.Empty<object>());
            q = q.Where(d => d.FromEmployeeId == me || d.ToEmployeeId == me);
        }
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
        var rows = await q.OrderByDescending(d => d.StartDate).Take(200).ToListAsync(ct);
        return Ok(rows.Select(d => new
        {
            d.Id, d.FromEmployeeId, d.ToEmployeeId, d.StartDate, d.EndDate, d.Reason, d.CreatedAt, d.RevokedAt,
            active = d.RevokedAt == null && d.StartDate <= today && d.EndDate >= today,
        }));
    }

    [HttpPost("delegations")]
    public async Task<IActionResult> CreateDelegation([FromBody] DelegationInput b, CancellationToken ct)
    {
        var me = await _employees.FindMyEmployeeIdAsync(ct);
        var from = b.FromEmployeeId ?? me;
        if (from is null) return Forbid();
        if (from != me && !IsHr) return StatusCode(403, new { message = "Yalnızca kendi onaylarınız için vekil atayabilirsiniz" });
        if (b.ToEmployeeId == Guid.Empty || b.ToEmployeeId == from) return BadRequest(new { message = "Vekil siz olamazsınız" });
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
        if (b.EndDate < b.StartDate || b.EndDate < today) return BadRequest(new { message = "Bitiş tarihi başlangıçtan ve bugünden önce olamaz" });
        if (b.EndDate.DayNumber - b.StartDate.DayNumber > 180) return BadRequest(new { message = "Vekâlet en fazla 180 gün olabilir" });
        if (b.Reason is { Length: > 200 }) return BadRequest(new { message = "Açıklama en fazla 200 karakter olabilir" });
        if (await _db.Delegations.AnyAsync(d => d.FromEmployeeId == from && d.RevokedAt == null && d.StartDate <= b.EndDate && d.EndDate >= b.StartDate, ct))
            return Conflict(new { message = "Bu tarihlerle çakışan bir vekâlet var" });
        // Zincirleme vekâlet yok: vekilin kendisi aynı günlerde başkasına vekâlet vermişse reddedilir.
        if (await _db.Delegations.AnyAsync(d => d.FromEmployeeId == b.ToEmployeeId && d.RevokedAt == null && d.StartDate <= b.EndDate && d.EndDate >= b.StartDate, ct))
            return Conflict(new { message = "Vekil bu tarihlerde kendisi vekâlet vermiş" });
        var del = new Delegation
        {
            FromEmployeeId = from.Value, ToEmployeeId = b.ToEmployeeId, StartDate = b.StartDate, EndDate = b.EndDate,
            Reason = string.IsNullOrWhiteSpace(b.Reason) ? null : b.Reason.Trim(), CreatedBy = User.FindFirst("preferred_username")?.Value,
        };
        _db.Delegations.Add(del);
        await _db.SaveChangesAsync(ct);
        // Vekâlet bugün başlıyorsa sırası gelmiş bekleyen adımlar hemen vekile geçer.
        var moved = del.StartDate <= today ? await ApplyToPendingAsync(del, ct) : 0;
        return Ok(new { del.Id, moved });
    }

    /// <summary>Sırası gelmiş, vekili olmayan bekleyen adımları vekile aktarır (bildirim gider).</summary>
    private async Task<int> ApplyToPendingAsync(Delegation del, CancellationToken ct)
    {
        var steps = await _db.ApprovalSteps.Include(s => s.WorkflowRequest).ThenInclude(w => w!.Steps)
            .Where(s => s.ApproverEmployeeId == del.FromEmployeeId && s.Decision == StepDecision.Pending && s.DelegatedToEmployeeId == null
                && s.WorkflowRequest!.Status == WorkflowStatus.Pending)
            .ToListAsync(ct);
        var n = 0;
        foreach (var s in steps)
        {
            var wf = s.WorkflowRequest!;
            if (wf.RequesterEmployeeId == del.ToEmployeeId) continue;
            if (wf.Steps.Any(p => p.Order < s.Order && p.Decision == StepDecision.Pending)) continue; // sırası gelmemiş
            await _routing.AssignAsync(wf, s, wf.TenantSlug, ct);
            n++;
        }
        await _db.SaveChangesAsync(ct);
        return n;
    }

    [HttpDelete("delegations/{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        var d = await _db.Delegations.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (d is null) return NotFound();
        var me = await _employees.FindMyEmployeeIdAsync(ct);
        if (d.FromEmployeeId != me && !IsHr) return Forbid();
        if (d.RevokedAt is null)
        {
            d.RevokedAt = DateTimeOffset.UtcNow;
            await DelegationSweep.ReturnStepsAsync(_db, d.Id, ct);
            await _db.SaveChangesAsync(ct);
        }
        return NoContent();
    }
}

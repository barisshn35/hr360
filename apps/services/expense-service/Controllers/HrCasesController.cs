using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ExpenseService.Data;
using ExpenseService.Models;
using ExpenseService.Services;

namespace ExpenseService.Controllers;

[ApiController]
[Route("api/hr-cases")]
[Authorize]
public class HrCasesController : ControllerBase
{
    private readonly ExpenseDbContext _db;
    private readonly ApprovalWorkflowClient _employees;
    public HrCasesController(ExpenseDbContext db, ApprovalWorkflowClient employees)
    {
        _db = db;
        _employees = employees;
    }

    /// <summary>Tum vakalari gorebilen roller: IK + acikca "case:manage" verilenler.</summary>
    private bool IsCaseAdmin => User.IsInRole("hr-admin") || User.IsInRole("tenant-admin")
        || User.IsInRole("platform-admin") || User.IsInRole("ext-case-manage");

    /// <summary>
    /// GUVENLIK: IK vakalari (sikayet, bordro, taciz iddiasi) onceden yalnizca
    /// [Authorize] ile korunuyordu - her calisan kiracidaki TUM vakalari okuyabiliyordu
    /// (canli dogrulandi: Ayse, Mehmet'in "gizli sikayet" vakasini okudu). Artik IK
    /// disindakiler yalnizca kendi actiklari ya da kendilerine atanan vakalari gorur -
    /// bir yonetici kendisi hakkindaki bir sikayeti de gormez.
    /// </summary>
    private async Task<IQueryable<HrCase>?> ScopedAsync(CancellationToken ct)
    {
        var q = _db.Cases.AsQueryable();
        if (IsCaseAdmin) return q;
        var me = await _employees.FindMyEmployeeIdAsync(ct);
        if (me is null) return null;
        return q.Where(c => c.EmployeeId == me.Value || c.AssignedToEmployeeId == me.Value);
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] Guid? employeeId, [FromQuery] CaseStatus? status, [FromQuery] CasePriority? priority,
        CancellationToken ct)
    {
        var q = await ScopedAsync(ct);
        if (q is null) return Forbid();
        if (employeeId.HasValue) q = q.Where(c => c.EmployeeId == employeeId.Value);
        if (status.HasValue) q = q.Where(c => c.Status == status.Value);
        if (priority.HasValue) q = q.Where(c => c.Priority == priority.Value);
        return Ok(await q.OrderByDescending(c => c.Priority).ThenBy(c => c.CreatedAt).ToListAsync());
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var q = await ScopedAsync(ct);
        if (q is null) return Forbid();
        var c = await q.FirstOrDefaultAsync(x => x.Id == id, ct);
        return c is null ? NotFound() : Ok(c);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCaseRequest request, CancellationToken ct)
    {
        // Baskasi adina vaka acmak yalnizca IK'ya acik.
        if (!IsCaseAdmin)
        {
            var me = await _employees.FindMyEmployeeIdAsync(ct);
            if (me is null) return Forbid();
            if (request.EmployeeId != me.Value)
                return StatusCode(StatusCodes.Status403Forbidden,
                    new { message = "Yalnızca kendi adınıza vaka açabilirsiniz" });
        }
        var (error, hrCase) = await CreateCoreAsync(_db, request, ct);
        if (error is not null) return error;
        return CreatedAtAction(nameof(GetById), new { id = hrCase!.Id }, hrCase);
    }

    /// <summary>
    /// Vaka açmanın ortak çekirdeği: web ucu ve sohbet botunun iç ucu (InternalChatController)
    /// aynı doğrulamadan geçer. Yetki (kimin adına) kontrolü çağırandadır.
    /// </summary>
    [NonAction]
    public static async Task<(IActionResult? Error, HrCase? Case)> CreateCoreAsync(
        ExpenseDbContext db, CreateCaseRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Subject) || request.Subject.Length > 200)
            return (new BadRequestObjectResult("Konu zorunlu ve en fazla 200 karakter olabilir"), null);

        var hrCase = new HrCase
        {
            EmployeeId = request.EmployeeId,
            Subject = request.Subject,
            Description = request.Description,
            Category = request.Category,
            Priority = request.Priority
        };
        db.Cases.Add(hrCase);
        await db.SaveChangesAsync(ct);
        return (null, hrCase);
    }

    [HttpPost("{id}/assign")]
    [Authorize(Policy = "RequireCaseManage")]
    public async Task<IActionResult> Assign(Guid id, [FromBody] AssignCaseRequest request)
    {
        var c = await _db.Cases.FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return NotFound();
        // Atama yalnizca IK'ya acik: onceden herhangi bir yonetici vakayi (kendisi
        // hakkindaki bir sikayeti dahil) kendisine atayip okuyabilirdi.
        if (!IsCaseAdmin) return Forbid();
        if (request.AssignedToEmployeeId == c.EmployeeId)
            return BadRequest("Vaka, açan kişiye atanamaz");

        c.AssignedToEmployeeId = request.AssignedToEmployeeId;
        if (c.Status == CaseStatus.Open) c.Status = CaseStatus.InProgress;
        await _db.SaveChangesAsync();
        return Ok(c);
    }

    [HttpPost("{id}/resolve")]
    // NOT: Politika kaldirildi - yetki asagida: IK ya da vakanin atandigi kisi (atanan
    // duz bir calisan olsa bile). Politika, atanan kisiyi yeni kurala ulasmadan eliyordu.
    public async Task<IActionResult> Resolve(Guid id, [FromBody] ResolveCaseRequest request)
    {
        var c = await _db.Cases.FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return NotFound();
        if (c.Status is CaseStatus.Resolved or CaseStatus.Closed)
            return BadRequest("Vaka zaten sonuclanmis");

        // Yalnizca IK ya da vakanin atandigi kisi sonuclandirabilir; hic kimse
        // kendi actigi vakayi kapatamaz (yonetici kendisi hakkindaki sikayeti dahil).
        var me = await _employees.FindMyEmployeeIdAsync(HttpContext.RequestAborted);
        if (!IsCaseAdmin && (me is null || c.AssignedToEmployeeId != me.Value)) return NotFound();
        if (me is not null && me.Value == c.EmployeeId)
            return StatusCode(StatusCodes.Status403Forbidden,
                new { message = "Kendi açtığınız vakayı sonuçlandıramazsınız" });

        c.Status = CaseStatus.Resolved;
        c.Resolution = request.Resolution;
        c.ResolvedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(c);
    }
}

public record CreateCaseRequest(
    Guid EmployeeId, string Subject, string? Description,
    CaseCategory Category, CasePriority Priority);
public record AssignCaseRequest(Guid AssignedToEmployeeId);
public record ResolveCaseRequest(string Resolution);

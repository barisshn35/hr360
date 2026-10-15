using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ExpenseService.Data;
using ExpenseService.Models;
using ExpenseService.Services;

namespace ExpenseService.Controllers;

/// <summary>
/// Sohbet botunun (governance-service, Slack/Teams) bu servisin verisini değiştirdiği iç uçlar.
/// Bot artık expense_* tablolarına doğrudan yazmaz: kurallar web uçlarıyla aynı çekirdekten
/// (<see cref="ExpenseClaimsController.CreateCoreAsync"/>, <see cref="HrCasesController.CreateCoreAsync"/>)
/// geçer, denetim kaydı bu serviste (AuditInterceptor) yazılır — işlemi yapan çalışan
/// "employee:&lt;id&gt;", kullanıcı adı "Sohbet (&lt;platform&gt;)" olarak.
/// Çalışan kimliği botun doğrulanmış sohbet hesabından gelir. Gateway /api/*/internal/
/// yollarını dışarıya kapatır; INTERNAL_SERVICE_TOKEN tanımlı değilse uçlar kapalıdır (404).
/// </summary>
[ApiController]
[AllowAnonymous]
public class InternalChatController : ControllerBase
{
    private readonly ExpenseDbContext _db;
    private readonly FxService _fx;
    private readonly Tenancy.TenantContext _tenant;

    public InternalChatController(ExpenseDbContext db, FxService fx, Tenancy.TenantContext tenant)
    {
        _db = db;
        _fx = fx;
        _tenant = tenant;
    }

    /// <summary>Sohbetten taslak masraf beyanı (fiş okuma / elle öneri) — tek kalemli, Draft.</summary>
    [HttpPost("/api/internal/chat/expense-draft")]
    public async Task<IActionResult> ExpenseDraft([FromBody] ChatExpenseDraftRequest request, CancellationToken ct)
    {
        if (!TokenOk(Request)) return NotFound();
        if (Prepare(request.TenantSlug, request.EmployeeId, request.Platform) is { } bad) return bad;
        if (!TryCategory(request.Category, ExpenseCategory.Other, out ExpenseCategory category))
            return BadRequest(new { message = "Geçersiz masraf kategorisi" });
        var date = request.Date ?? DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
        var title = string.IsNullOrWhiteSpace(request.Title) ? $"Fiş {date:dd.MM.yyyy}" : request.Title;
        var core = new CreateClaimRequest(request.EmployeeId, title, string.IsNullOrWhiteSpace(request.Currency) ? "TRY" : request.Currency,
            new List<ExpenseItemInput> { new(category, request.Amount, date, request.Description, null) });
        var (error, claim) = await ExpenseClaimsController.CreateCoreAsync(_db, _fx, _tenant.TenantSlug, core, ct);
        if (error is not null) return WithMessage(error);
        return Ok(new { id = claim!.Id, totalAmount = claim.TotalAmount, status = claim.Status.ToString() });
    }

    /// <summary>Sohbetten İK vakası (yalnızca kendi adına; İK görür).</summary>
    [HttpPost("/api/internal/chat/hr-case")]
    public async Task<IActionResult> HrCase([FromBody] ChatHrCaseRequest request, CancellationToken ct)
    {
        if (!TokenOk(Request)) return NotFound();
        if (Prepare(request.TenantSlug, request.EmployeeId, request.Platform) is { } bad) return bad;
        if (!TryCategory(request.Category, CaseCategory.Other, out CaseCategory category))
            return BadRequest(new { message = "Geçersiz vaka kategorisi" });
        var core = new CreateCaseRequest(request.EmployeeId, (request.Subject ?? "").Trim(), request.Description, category, CasePriority.Normal);
        var (error, hrCase) = await HrCasesController.CreateCoreAsync(_db, core, ct);
        if (error is not null) return WithMessage(error);
        return Ok(new { id = hrCase!.Id, status = hrCase.Status.ToString() });
    }

    // ------------------------------------------------------------------ ortak

    /// <summary>X-Internal-Token sabit zamanlı karşılaştırılır; anahtar tanımlı değilse her istek reddedilir.</summary>
    public static bool TokenOk(HttpRequest request) =>
        Security.InternalServiceToken.Matches(request.Headers[Security.InternalServiceToken.Header].FirstOrDefault());

    /// <summary>
    /// Kiracıyı (EF kiracı filtreleri ve TenantSlug damgası için) ve denetim kaydındaki
    /// işlemi yapanı (sohbetteki çalışan) ayarlar.
    /// </summary>
    private IActionResult? Prepare(string? tenantSlug, Guid employeeId, string? platform)
    {
        if (string.IsNullOrWhiteSpace(tenantSlug)) return BadRequest(new { message = "Kiracı belirtilmedi" });
        if (employeeId == Guid.Empty) return BadRequest(new { message = "Çalışan belirtilmedi" });
        _tenant.TenantSlug = tenantSlug.Trim();
        _tenant.IsPlatformAdmin = false;
        HttpContext.User = ChatActor(employeeId, platform);
        return null;
    }

    public static ClaimsPrincipal ChatActor(Guid employeeId, string? platform)
    {
        var p = string.IsNullOrWhiteSpace(platform) ? "chat" : platform.Trim();
        if (p.Length > 20) p = p[..20];
        return new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "employee:" + employeeId),
            new Claim("name", $"Sohbet ({p})"),
        }, "internal-chat"));
    }

    /// <summary>Yalnızca adlandırılmış enum değerleri (sayısal "5" gibi değerler kabul edilmez).</summary>
    public static bool TryCategory<T>(string? value, T fallback, out T result) where T : struct, Enum
    {
        result = fallback;
        if (string.IsNullOrWhiteSpace(value)) return true;
        var v = value.Trim();
        if (!char.IsLetter(v[0]) || !Enum.TryParse(v, true, out T parsed) || !Enum.IsDefined(parsed)) return false;
        result = parsed;
        return true;
    }

    /// <summary>Çekirdeğin düz metin hata gövdesini bota gösterilebilir { message } biçimine çevirir.</summary>
    public static IActionResult WithMessage(IActionResult error) => error is ObjectResult { Value: string s } o
        ? new ObjectResult(new { message = s }) { StatusCode = o.StatusCode }
        : error;
}

public record ChatExpenseDraftRequest(
    string? TenantSlug, Guid EmployeeId, string? Title, string? Currency, decimal Amount, DateOnly? Date,
    string? Category, string? Description = null, string? Platform = null);
public record ChatHrCaseRequest(
    string? TenantSlug, Guid EmployeeId, string? Subject, string? Description, string? Category = null, string? Platform = null);

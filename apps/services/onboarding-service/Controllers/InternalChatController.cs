using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OnboardingService.Data;
using OnboardingService.Models;

namespace OnboardingService.Controllers;

/// <summary>
/// Sohbet botunun (governance-service, Slack/Teams) bu servisin verisini değiştirdiği iç uçlar.
/// Bot artık onboarding_* tablolarına doğrudan yazmaz: görev tamamlama web ucuyla aynı
/// çekirdekten (<see cref="OnboardingPlansController.SetTaskStatusCoreAsync"/>) geçer (atanan kişi
/// ya da — hukuki görevler hariç — planın sahibi; iptal edilmiş plan değişmez; tüm görevler
/// bitince plan kapanır). Denetim kaydını bu servisin AuditInterceptor'ı yazar; işlemi yapan
/// "employee:&lt;id&gt;", kullanıcı adı "Sohbet (&lt;platform&gt;)". Gateway /api/*/internal/ yollarını
/// dışarıya kapatır; INTERNAL_SERVICE_TOKEN tanımlı değilse uç kapalıdır (404).
/// </summary>
[ApiController]
[AllowAnonymous]
public class InternalChatController : ControllerBase
{
    private readonly OnboardingDbContext _db;
    private readonly Tenancy.TenantContext _tenant;

    public InternalChatController(OnboardingDbContext db, Tenancy.TenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    /// <summary>Sohbetteki "✓ görev" düğmesi: çalışan, kendisinin yapabileceği bir görevi tamamlar.</summary>
    [HttpPost("/api/internal/chat/task-done")]
    public async Task<IActionResult> TaskDone([FromBody] ChatTaskDoneRequest request, CancellationToken ct)
    {
        if (!TokenOk(Request)) return NotFound();
        if (string.IsNullOrWhiteSpace(request.TenantSlug)) return BadRequest(new { message = "Kiracı belirtilmedi" });
        if (request.EmployeeId == Guid.Empty) return BadRequest(new { message = "Çalışan belirtilmedi" });
        if (request.TaskId == Guid.Empty) return BadRequest(new { message = "Görev belirtilmedi" });
        _tenant.TenantSlug = request.TenantSlug.Trim();
        _tenant.IsPlatformAdmin = false;
        HttpContext.User = ChatActor(request.EmployeeId, request.Platform);

        // Sohbetteki çalışan yönetici yetkisiyle davranmaz: yalnızca kendi görevleri.
        var (error, task, planCompleted) = await OnboardingPlansController.SetTaskStatusCoreAsync(_db, null, request.TaskId,
            OnboardingTaskStatus.Done, canManage: false, _ => Task.FromResult<Guid?>(request.EmployeeId), failIfAlreadyDone: true, ct);
        if (error is not null) return WithMessage(error);
        return Ok(new { id = task!.Id, title = task.Title, status = task.Status.ToString(), planId = task.PlanId, planCompleted });
    }

    /// <summary>X-Internal-Token sabit zamanlı karşılaştırılır; anahtar tanımlı değilse her istek reddedilir.</summary>
    public static bool TokenOk(HttpRequest request) =>
        Security.InternalServiceToken.Matches(request.Headers[Security.InternalServiceToken.Header].FirstOrDefault());

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

    /// <summary>Çekirdeğin hata sonuçlarını bota gösterilebilir { message } biçimine çevirir.</summary>
    public static IActionResult WithMessage(IActionResult error) => error switch
    {
        NotFoundResult => new NotFoundObjectResult(new { message = "Bu görev zaten tamam ya da size ait değil" }),
        ObjectResult { Value: string s } o => new ObjectResult(new { message = s }) { StatusCode = o.StatusCode },
        _ => error,
    };
}

public record ChatTaskDoneRequest(string? TenantSlug, Guid EmployeeId, Guid TaskId, string? Platform = null);

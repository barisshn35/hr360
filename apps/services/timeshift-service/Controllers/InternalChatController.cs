using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TimeShiftService.Data;
using TimeShiftService.Models;
using TimeShiftService.Services;
using TimeShiftService.Tenancy;

namespace TimeShiftService.Controllers;

/// <summary>
/// Sohbet botunun (governance-service, Slack/Teams) vardiya/PDKS işlemleri için iç uçlar.
/// Kurallar web uçlarıyla aynı çekirdekten geçer (ClockCore, ShiftSwapsController çekirdekleri);
/// çalışan, botun doğrulanmış sohbet hesabından gelir. Gateway /api/*/internal/ yollarını dışarıya
/// kapatır; INTERNAL_SERVICE_TOKEN tanımlı değilse ya da X-Internal-Token uymazsa uç yok sayılır (404).
/// Denetim: alan değişiklikleri AuditInterceptor ile "employee:&lt;id&gt;" / "Sohbet (platform)" adına,
/// takas kararları ayrıca botun eski eylem adlarıyla (PeerAccepted, Approved…) yazılır.
/// </summary>
[ApiController]
[AllowAnonymous]
public class InternalChatController : ControllerBase
{
    private readonly TimeShiftDbContext _db;
    private readonly EmployeeDirectoryClient _employees;
    private readonly TenantContext _tenant;

    public InternalChatController(TimeShiftDbContext db, EmployeeDirectoryClient employees, TenantContext tenant)
    {
        _db = db; _employees = employees; _tenant = tenant;
    }

    public record PunchBody(string? TenantSlug, Guid EmployeeId, string? Kind, string? Platform);
    public record SwapRespondBody(string? TenantSlug, Guid EmployeeId, Guid SwapId, bool Accept, string? Platform);
    public record SwapDecideBody(string? TenantSlug, Guid EmployeeId, Guid SwapId, bool Approve, string? Reason, string? Platform);

    internal static bool TokenOk(string? expected, string? given) =>
        Security.InternalServiceToken.Matches(given, expected, null);

    private static string PlatformOf(string? p) =>
        string.IsNullOrWhiteSpace(p) ? "chat" : new string(p.Trim().Where(char.IsLetterOrDigit).Take(20).ToArray()) is { Length: > 0 } s ? s : "chat";

    /// <summary>Anahtar + kiracı + çalışan denetimi; kiracı bağlamını ve denetim kimliğini (çalışan, sohbet) ayarlar.</summary>
    private IActionResult? Gate(string? tenantSlug, Guid employeeId, string? platform)
    {
        if (!Security.InternalServiceToken.Matches(Request.Headers[Security.InternalServiceToken.Header].FirstOrDefault()))
            return NotFound();
        if (string.IsNullOrWhiteSpace(tenantSlug)) return BadRequest(new { message = "Kiracı belirtilmedi" });
        if (employeeId == Guid.Empty) return BadRequest(new { message = "Çalışan belirtilmedi" });
        _tenant.TenantSlug = tenantSlug.Trim();
        _tenant.IsPlatformAdmin = false;
        // AuditInterceptor eyleyeni HttpContext.User'dan okur: satırlar "system" yerine çalışana yazılsın.
        HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("sub", $"employee:{employeeId}"),
            new Claim("name", $"Sohbet ({PlatformOf(platform)})"),
        }, "internal-chat"));
        return null;
    }

    /// <summary>
    /// Sohbetten giriş/çıkış: web/QR/terminal ile aynı ClockCore yolu (açık kayıt, günde bir giriş,
    /// çalışılan/fazla mesai dakikası). Konum denetimi uygulanmaz: OnSite NULL, yöntem "Chat".
    /// </summary>
    [HttpPost("/api/internal/chat/punch")]
    public async Task<IActionResult> Punch([FromBody] PunchBody b, CancellationToken ct)
    {
        if (Gate(b.TenantSlug, b.EmployeeId, b.Platform) is { } stop) return stop;
        var kind = (b.Kind ?? "").Trim().ToLowerInvariant();
        if (kind is not ("in" or "out")) return BadRequest(new { message = "Geçersiz işlem" });
        var (err, punch, entry) = await ClockCore.PunchAsync(_db, b.EmployeeId, null, kind, TimeEntrySource.Chat, null, ct);
        if (err is not null)
            return Conflict(new
            {
                message = err,
                code = err switch
                {
                    ClockCore.ErrOpenEntry => "open_entry",
                    ClockCore.ErrAlreadyIn => "already_in",
                    ClockCore.ErrNoOpenEntry => "no_open_entry",
                    _ => "clock_rule",
                },
            });
        return Ok(new
        {
            kind = punch!.Kind == PunchKind.In ? "in" : "out",
            at = punch.At,
            entryId = entry?.Id,
            clockIn = entry?.ClockIn,
            workedMinutes = entry?.WorkedMinutes ?? 0,
            overtimeMinutes = entry?.OvertimeMinutes ?? 0,
        });
    }

    private ShiftSwapsController Swaps() => new(_db, _employees, _tenant);

    /// <summary>Takas talebine hedef kişinin yanıtı (web Respond ile aynı çekirdek).</summary>
    [HttpPost("/api/internal/chat/swap-respond")]
    public async Task<IActionResult> SwapRespond([FromBody] SwapRespondBody b, CancellationToken ct)
    {
        if (Gate(b.TenantSlug, b.EmployeeId, b.Platform) is { } stop) return stop;
        var r = await Swaps().RespondCoreAsync(b.EmployeeId, b.SwapId, b.Accept, PlatformOf(b.Platform), ct);
        return r is NotFoundResult ? NotFound(new { message = "Bu talep artık yanıtınızı beklemiyor", code = "not_found" }) : r;
    }

    /// <summary>
    /// Yönetici onayı/reddi (web Decide ile aynı çekirdek ve kurallar). Yetki: karar veren (botun
    /// doğruladığı çalışan) talep edenin bölüm başı olmalı ve taraflardan biri olmamalı.
    /// Kural ihlalinde talep reddedilir ve 400 { code: "rule_violation", message: gerekçe } döner.
    /// </summary>
    [HttpPost("/api/internal/chat/swap-decide")]
    public async Task<IActionResult> SwapDecide([FromBody] SwapDecideBody b, CancellationToken ct)
    {
        if (Gate(b.TenantSlug, b.EmployeeId, b.Platform) is { } stop) return stop;
        var swaps = Swaps();
        string? decidedBy = null;
        var r = await swaps.DecideCoreAsync(b.SwapId, new ShiftSwapsController.DecideInput(b.Approve, b.Reason),
            async s =>
            {
                if (!await swaps.CanDecideAsHeadAsync(s, b.EmployeeId, ct)) return false;
                decidedBy = (await TsOps.PersonAsync(_db, _tenant.TenantSlug!, b.EmployeeId, ct))?.FullName;
                return true;
            },
            () => string.IsNullOrWhiteSpace(decidedBy) ? "Sohbet" : decidedBy!,
            (b.EmployeeId, PlatformOf(b.Platform)), ct);
        return r is NotFoundResult ? NotFound(new { message = "Takas talebi bulunamadı", code = "not_found" }) : r;
    }
}

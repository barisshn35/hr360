using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using EngagementService.Infrastructure;
using EngagementService.Infrastructure.Badges;

namespace EngagementService.Controllers;

/// <summary>
/// Rozet koleksiyonu (yenilik haritası #29). Kişi kendi koleksiyonunu ilerlemesiyle görür; başkasının
/// profilinde yalnızca kazanılmış rozetler (ilerleme ve ölçüt sayıları gösterilmez).
/// </summary>
[Route("api/badges")]
[Authorize]
public class BadgesController : AppController
{
    private readonly BadgeService _badges;
    public BadgesController(BadgeService badges) => _badges = badges;

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] Guid? employeeId, CancellationToken ct)
    {
        var me = (await MyPersonAsync(ct))?.Id;
        var target = employeeId ?? me;
        if (target is null) return Ok(new { mine = false, badges = Array.Empty<object>() });
        var mine = target == me;
        var awards = await _badges.SyncAsync(Tenant, target, ct);
        if (!awards.TryGetValue(target.Value, out var got)) return NotFound();
        if (!mine)
            return Ok(new
            {
                mine,
                badges = BadgeCatalog.All.Where(b => got.ContainsKey(b.Id)).Select(b => new { b.Id, b.Group, earned = true, earnedAt = got[b.Id] }),
            });
        var metrics = (await _badges.MetricsAsync(Tenant, target, ct))[target.Value].M;
        return Ok(new
        {
            mine,
            badges = BadgeCatalog.Evaluate(metrics).Select(b => new
            {
                b.Id, b.Group, b.Earned, earnedAt = got.TryGetValue(b.Id, out var at) ? at : (DateTime?)null, value = b.Value, target = b.Target,
            }),
        });
    }
}

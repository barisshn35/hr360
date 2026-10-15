using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using GovernanceService.Data;
using GovernanceService.Infrastructure;

namespace GovernanceService.Controllers;

/// <summary>
/// Yedekten geri yükleme sonrası (scripts/restore.sh): tüm kiracıların etkin saklama politikaları
/// hemen yeniden çalıştırılır; yedekten sonra imha edilmiş kayıtlar yeniden silinir/anonimleşir.
/// İmha tutanağına "Restore" tetikleyicisiyle yazılır. INTERNAL_SERVICE_TOKEN ile korunur;
/// gateway /api/*/internal/ yollarını dışarıya kapatır.
/// </summary>
[ApiController]
[Route("api/internal/retention")]
[AllowAnonymous]
public class InternalRetentionController : ControllerBase
{
    private readonly GovernanceDbContext _db;
    private readonly Sql _sql;
    private readonly Tenancy.TenantContext _tenant;
    public InternalRetentionController(GovernanceDbContext db, Sql sql, Tenancy.TenantContext tenant) { _db = db; _sql = sql; _tenant = tenant; }

    [HttpPost("run")]
    public async Task<IActionResult> Run(CancellationToken ct)
    {
        if (!Security.InternalServiceToken.Matches(Request.Headers[Security.InternalServiceToken.Header].FirstOrDefault()))
            return NotFound();
        _tenant.IsPlatformAdmin = true;
        var policies = await _db.RetentionPolicies.IgnoreQueryFilters().Where(p => p.IsEnabled).ToListAsync(ct);
        var total = 0;
        var details = new List<(Models.DestructionLog? Log, Retention.RunResult Run)>();
        foreach (var p in policies)
        {
            var run = await Retention.RunDetailedAsync(_sql, p, ct);
            p.LastAffected = run.Affected;
            p.LastRunAt = DateTime.UtcNow;
            total += p.LastAffected;
            details.Add((Retention.Log(_db, p.TenantSlug, p.Category, p.Action, p.LastAffected, p.RetentionMonths, "Restore", "Sistem (yedekten geri yükleme sonrası)"), run));
        }
        await _db.SaveChangesAsync(ct);
        foreach (var (log, run) in details) await Retention.SaveDetailsAsync(_sql, log, run, ct);
        return Ok(new { policies = policies.Count, affected = total });
    }
}

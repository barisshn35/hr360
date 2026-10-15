using Microsoft.EntityFrameworkCore;
using WorkflowService.Data;
using WorkflowService.Models;
using WorkflowService.Tenancy;

namespace WorkflowService.Services;

/// <summary>Vekâlet bitince/geri alınınca vekile geçmiş bekleyen adımlar asıl onaycıya döner.</summary>
public static class DelegationSweep
{
    public static async Task<int> ReturnStepsAsync(WorkflowDbContext db, Guid delegationId, CancellationToken ct)
    {
        var steps = await db.ApprovalSteps.IgnoreQueryFilters()
            .Where(s => s.DelegationId == delegationId && s.Decision == StepDecision.Pending).ToListAsync(ct);
        foreach (var s in steps) { s.DelegatedToEmployeeId = null; s.DelegationId = null; }
        return steps.Count;
    }
}

/// <summary>
/// Arka plan işleri (5 dakikada bir, tüm kiracılar):
///  - Süresi dolan vekâletlerde adımları asıl onaycıya döndürür (vekilin erişimi kapanır).
///  - SLA'sı aşılan bekleyen adımı onaycının üst yöneticisine (yoksa İK onaycısına) iletir (G10); bir adım bir kez iletilir.
/// </summary>
public class WorkflowJobsWorker : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(int.TryParse(Environment.GetEnvironmentVariable("WORKFLOW_JOBS_SECONDS"), out var s) ? Math.Max(5, s) : 300);
    private readonly IServiceProvider _sp;
    private readonly ILogger<WorkflowJobsWorker> _log;
    public WorkflowJobsWorker(IServiceProvider sp, ILogger<WorkflowJobsWorker> log) { _sp = sp; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), ct);
        while (!ct.IsCancellationRequested)
        {
            try { await RunOnceAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "Onay akışı arka plan işi başarısız"); }
            await Task.Delay(Every, ct);
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().IsPlatformAdmin = true;
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        var routing = scope.ServiceProvider.GetRequiredService<WorkflowRouting>();
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));

        // 1) Biten vekâletler
        var ended = await db.Delegations.Where(d => d.RevokedAt == null && d.EndDate < today).Select(d => d.Id).ToListAsync(ct);
        foreach (var id in ended) await DelegationSweep.ReturnStepsAsync(db, id, ct);
        // Bugün başlayan vekâletler: sırası gelmiş bekleyen adımlar vekile geçer.
        var starting = await db.Delegations.Where(d => d.RevokedAt == null && d.StartDate <= today && d.EndDate >= today).ToListAsync(ct);
        foreach (var del in starting)
        {
            var steps = await db.ApprovalSteps.Include(s => s.WorkflowRequest).ThenInclude(w => w!.Steps)
                .Where(s => s.TenantSlug == del.TenantSlug && s.ApproverEmployeeId == del.FromEmployeeId && s.Decision == StepDecision.Pending
                    && s.DelegatedToEmployeeId == null && s.WorkflowRequest!.Status == WorkflowStatus.Pending)
                .ToListAsync(ct);
            foreach (var st in steps.Where(st => st.WorkflowRequest!.RequesterEmployeeId != del.ToEmployeeId
                         && !st.WorkflowRequest.Steps.Any(p => p.Order < st.Order && p.Decision == StepDecision.Pending)))
                await routing.AssignAsync(st.WorkflowRequest!, st, st.TenantSlug, ct);
        }
        await db.SaveChangesAsync(ct);

        // 2) Süre aşımı: üst yöneticiye ilet
        var now = DateTimeOffset.UtcNow;
        var overdue = await db.WorkflowRequests.Include(w => w.Steps)
            .Where(w => w.Status == WorkflowStatus.Pending && w.SlaDueAt != null && w.SlaDueAt < now)
            .Take(200).ToListAsync(ct);
        foreach (var wf in overdue)
        {
            var step = wf.Steps.Where(s => s.Decision == StepDecision.Pending).OrderBy(s => s.Order).FirstOrDefault();
            if (step is null || step.EscalatedAt is not null) continue;
            var upper = await routing.HeadOfAsync(wf.TenantSlug, step.ApproverEmployeeId, parent: false, ct);
            if (upper is null || upper == step.ApproverEmployeeId || upper == wf.RequesterEmployeeId)
                upper = await routing.HeadOfAsync(wf.TenantSlug, step.ApproverEmployeeId, parent: true, ct);
            // Üst yönetici yoksa (ör. onaycı en üst departmanın başı) İK onaycısına iletilir.
            if (upper is null || upper == step.ApproverEmployeeId || upper == wf.RequesterEmployeeId)
                upper = await routing.HrApproverAsync(wf.TenantSlug, ct);
            if (upper is null || upper == step.ApproverEmployeeId || upper == wf.RequesterEmployeeId
                || upper == step.DelegatedToEmployeeId)
            {
                step.EscalatedAt = now; // iletilecek kimse yok; bir daha denenmez
                continue;
            }
            await routing.AssignAsync(wf, step, wf.TenantSlug, ct, escalateTo: upper);
            _log.LogInformation("Akış {Wf} süre aşımıyla üst yöneticiye iletildi", wf.Id);
        }
        await db.SaveChangesAsync(ct);
    }
}

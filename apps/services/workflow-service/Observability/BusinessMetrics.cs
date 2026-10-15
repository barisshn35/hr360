using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Prometheus;
using WorkflowService.Models;

namespace WorkflowService.Observability;

/// <summary>
/// G25: Is metrikleri (Prometheus). Yalnizca dusuk kardinaliteli etiketler (tur, islem, asama);
/// kisi duzeyinde etiket YOK. Kiraci etiketi varsayilan olarak kapali (paylasilan
/// Grafana'da sirket bazli bilgi sizmasin); METRICS_TENANT_LABEL=true ile acilir.
/// Sayaclar EF kaydi BASARIYLA tamamlaninca artar (BusinessMetricsInterceptor) -
/// tum yollar (HTTP, Kafka tuketicisi, IK elle sonuclandirma) tek noktadan sayilir.
/// </summary>
public static class BusinessMetrics
{
    public static readonly bool TenantLabel =
        string.Equals(Environment.GetEnvironmentVariable("METRICS_TENANT_LABEL"), "true", StringComparison.OrdinalIgnoreCase);

    internal static string[] Labels(params string[] names) => TenantLabel ? [.. names, "tenant"] : names;
    internal static string[] Values(string? tenant, params string[] values) => TenantLabel ? [.. values, tenant ?? ""] : values;

    /// <summary>Status alani gercekten degistiyse (eski, yeni) doner.</summary>
    private static (T? From, T To)? StatusChange<T>(EntityEntry entry, string property) where T : struct
    {
        var prop = entry.Property(property);
        if (entry.State == EntityState.Added) return (null, (T)prop.CurrentValue!);
        if (entry.State != EntityState.Modified || !prop.IsModified || Equals(prop.OriginalValue, prop.CurrentValue)) return null;
        return ((T?)prop.OriginalValue, (T)prop.CurrentValue!);
    }

    public static readonly Counter Requests = Metrics.CreateCounter(
        "hr360_workflow_requests_total", "Onay akislari (islem: created/approved/rejected/cancelled; tur: akis turu)",
        Labels("action", "type"));

    public static readonly Counter StepDecisions = Metrics.CreateCounter(
        "hr360_workflow_step_decisions_total", "Onay adimi kararlari (karar: Approved/Rejected/Delegated; tur: akis turu)",
        Labels("decision", "type"));

    public static readonly Histogram ApprovalDuration = Metrics.CreateHistogram(
        "hr360_workflow_approval_duration_seconds", "Akisin acilisindan sonuclanmasina kadar gecen sure (saniye)",
        new HistogramConfiguration
        {
            LabelNames = Labels("outcome", "type"),
            // 1 dk .. 30 gun
            Buckets = new double[] { 60, 300, 900, 1800, 3600, 4 * 3600, 8 * 3600, 24 * 3600, 2 * 86400, 3 * 86400, 7 * 86400, 14 * 86400, 30 * 86400 },
        });

    internal static void Observe(EntityEntry entry, List<Action> pending)
    {
        switch (entry.Entity)
        {
            case WorkflowRequest wf:
            {
                var change = StatusChange<WorkflowStatus>(entry, nameof(WorkflowRequest.Status));
                if (change is null) return;
                var type = wf.Type.ToString();
                var tenant = wf.TenantSlug;
                if (entry.State == EntityState.Added)
                {
                    pending.Add(() => Requests.WithLabels(Values(tenant, "created", type)).Inc());
                    return;
                }
                var action = change.Value.To switch
                {
                    WorkflowStatus.Approved => "approved",
                    WorkflowStatus.Rejected => "rejected",
                    WorkflowStatus.Cancelled => "cancelled",
                    _ => null,
                };
                if (action is null) return;
                var seconds = ((wf.CompletedAt ?? DateTimeOffset.UtcNow) - wf.CreatedAt).TotalSeconds;
                pending.Add(() =>
                {
                    Requests.WithLabels(Values(tenant, action, type)).Inc();
                    if (action != "cancelled" && seconds >= 0)
                        ApprovalDuration.WithLabels(Values(tenant, action, type)).Observe(seconds);
                });
                return;
            }
            case ApprovalStep step when entry.State == EntityState.Modified:
            {
                var change = StatusChange<StepDecision>(entry, nameof(ApprovalStep.Decision));
                if (change is null || change.Value.To == StepDecision.Pending) return;
                var decision = change.Value.To.ToString();
                var type = step.WorkflowRequest?.Type.ToString() ?? "unknown";
                var tenant = step.TenantSlug;
                pending.Add(() => StepDecisions.WithLabels(Values(tenant, decision, type)).Inc());
                return;
            }
        }
    }
}

/// <summary>
/// SaveChanges oncesi gozlemleri toplar, BASARILI kayittan sonra sayaclara yansitir
/// (basarisiz/geri alinan kayitlar sayilmaz). Tum servislerde ayni iskelet.
/// </summary>
public sealed class BusinessMetricsInterceptor : SaveChangesInterceptor
{
    private readonly ConditionalWeakTable<DbContext, List<Action>> _pending = new();

    private void Collect(DbContext? ctx)
    {
        if (ctx is null) return;
        try
        {
            var list = new List<Action>();
            foreach (var entry in ctx.ChangeTracker.Entries())
                BusinessMetrics.Observe(entry, list);
            if (list.Count > 0) _pending.AddOrUpdate(ctx, list);
            else _pending.Remove(ctx);
        }
        catch
        {
            // Metrik toplama is akisini asla bozmamali.
        }
    }

    private void Flush(DbContext? ctx)
    {
        if (ctx is null || !_pending.TryGetValue(ctx, out var list)) return;
        _pending.Remove(ctx);
        foreach (var a in list)
        {
            try { a(); } catch { /* yoksay */ }
        }
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Collect(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Collect(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Flush(eventData.Context);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Flush(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        if (eventData.Context is not null) _pending.Remove(eventData.Context);
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null) _pending.Remove(eventData.Context);
        return Task.CompletedTask;
    }
}

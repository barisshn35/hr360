using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Prometheus;
using LeaveService.Models;
using LeaveService.Tenancy;

namespace LeaveService.Observability;

/// <summary>
/// G25: Is metrikleri (Prometheus). Yalnizca dusuk kardinaliteli etiketler (tur, islem);
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

    public static readonly Counter LeaveRequests = Metrics.CreateCounter(
        "hr360_leave_requests_total", "Izin talepleri (islem: created/approved/rejected/cancelled; tur: izin turu)",
        Labels("action", "type"));

    /// <summary>Kaydedilmek uzere olan degisikliklerden metrik gozlemleri toplar.</summary>
    internal static void Observe(EntityEntry entry, List<Action> pending)
    {
        if (entry.Entity is not LeaveRequest leave) return;
        var tenant = (entry.Entity as ITenantOwned)?.TenantSlug;
        var type = leave.Type.ToString();
        if (entry.State == EntityState.Added)
        {
            if (leave.Status != LeaveRequestStatus.Draft)
                pending.Add(() => LeaveRequests.WithLabels(Values(tenant, "created", type)).Inc());
            return;
        }
        if (entry.State != EntityState.Modified) return;
        var prop = entry.Property(nameof(LeaveRequest.Status));
        if (!prop.IsModified || Equals(prop.OriginalValue, prop.CurrentValue)) return;
        var action = leave.Status switch
        {
            LeaveRequestStatus.Approved => "approved",
            LeaveRequestStatus.Rejected => "rejected",
            LeaveRequestStatus.Cancelled => "cancelled",
            LeaveRequestStatus.Submitted => "created",
            _ => null,
        };
        if (action is not null)
            pending.Add(() => LeaveRequests.WithLabels(Values(tenant, action, type)).Inc());
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

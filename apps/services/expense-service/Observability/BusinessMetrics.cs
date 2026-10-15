using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Prometheus;
using ExpenseService.Models;

namespace ExpenseService.Observability;

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

    public static readonly Counter ExpenseClaims = Metrics.CreateCounter(
        "hr360_expense_claims_total",
        "Masraf beyanlari (islem: submitted/approved/rejected/paid; kategori: kalem kategorisi - cok kategorili beyan her kategoride bir kez sayilir)",
        Labels("action", "category"));

    internal static void Observe(EntityEntry entry, List<Action> pending)
    {
        if (entry.Entity is not ExpenseClaim claim) return;
        var change = StatusChange<ClaimStatus>(entry, nameof(ExpenseClaim.Status));
        if (change is null) return;
        var action = change.Value.To switch
        {
            ClaimStatus.Submitted => "submitted",
            ClaimStatus.Approved => "approved",
            ClaimStatus.Rejected => "rejected",
            ClaimStatus.Paid => "paid",
            _ => null,
        };
        if (action is null) return;
        var categories = claim.Items.Select(i => i.Category.ToString()).Distinct().ToList();
        if (categories.Count == 0) categories.Add("unknown");
        var tenant = claim.TenantSlug;
        pending.Add(() =>
        {
            foreach (var c in categories) ExpenseClaims.WithLabels(Values(tenant, action, c)).Inc();
        });
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

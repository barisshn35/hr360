using Microsoft.EntityFrameworkCore;
using NotificationService.Data;
using NotificationService.Models;

namespace NotificationService.Preferences;

/// <summary>Gönderim hattı için tercihleri toplu okur (kiracı + çalışan anahtarlı).</summary>
public static class PreferenceStore
{
    public static EffectivePrefs Compose(NotificationPreference? p, IEnumerable<CategoryPreference> cats) =>
        new(cats.ToDictionary(c => c.Category, c => new ChannelPrefs(c.InApp, c.Email, c.Push, c.Chat)),
            p is null ? QuietHours.Off : new QuietHours(p.QuietHoursEnabled, p.QuietStart, p.QuietEnd, p.QuietDays),
            p?.DigestEnabled ?? false, p?.DigestHour ?? 18);

    /// <summary>Kayıtlı tercihi olmayan kişi sözlükte yer almaz (varsayılan: her şey açık, hemen gönder).</summary>
    public static async Task<Dictionary<(string Tenant, Guid Employee), EffectivePrefs>> LoadAsync(
        NotificationDbContext db, IEnumerable<(string Tenant, Guid Employee)> keys, CancellationToken ct)
    {
        var wanted = keys.Distinct().ToList();
        var ids = wanted.Select(k => k.Employee).Distinct().ToList();
        var result = new Dictionary<(string, Guid), EffectivePrefs>();
        if (ids.Count == 0) return result;
        var general = await db.Preferences.IgnoreQueryFilters().AsNoTracking().Where(p => ids.Contains(p.EmployeeId)).ToListAsync(ct);
        var cats = await db.CategoryPreferences.IgnoreQueryFilters().AsNoTracking().Where(c => ids.Contains(c.EmployeeId)).ToListAsync(ct);
        foreach (var k in wanted)
        {
            var g = general.FirstOrDefault(p => p.TenantSlug == k.Tenant && p.EmployeeId == k.Employee);
            var c = cats.Where(x => x.TenantSlug == k.Tenant && x.EmployeeId == k.Employee).ToList();
            if (g is null && c.Count == 0) continue;
            result[k] = Compose(g, c);
        }
        return result;
    }
}

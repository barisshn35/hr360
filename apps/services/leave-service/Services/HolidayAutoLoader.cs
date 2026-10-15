using Microsoft.EntityFrameworkCore;
using LeaveService.Data;
using LeaveService.Tenancy;

namespace LeaveService.Services;

/// <summary>
/// Madde 72: Türkiye resmî tatillerinin yıllık otomatik yüklenmesi. Günde bir kez (açılıştan 2 dk sonra da)
/// çalışır: Türkiye takvimini yüklemiş (bu ya da geçen yıl 29 Ekim kaydı olan) her şirket için BU YILIN ve
/// GELECEK YILIN eksik tatilleri gömülü tablodan (TurkishHolidays) eklenir — yarım gün arifeler ve 28 Ekim
/// dahil. Yalnızca eksik tarihler eklenir; mevcut (şirketin düzenlediği) kayıtlar değiştirilmez, silinmez.
/// Her şirket × yıl, veri sürümü başına bir kez tamamlanır (leave_holiday_autoloads): şirketin sonradan
/// sildiği tatil ertesi gün yeniden eklenmez. Yarım gün desteğinden önce yüklenmiş şirketlerde (sürüm yok)
/// eksik arifeler ve 28 Ekim bir kez tamamlanır. İnternetten veri alınmaz. Kapatmak için LEAVE_HOLIDAY_AUTOLOAD=false.
/// </summary>
public sealed class HolidayAutoLoader : BackgroundService
{
    /// <summary>Gömülü tablo/kural sürümü: artırılırsa eksikler her şirkette bir kez daha tamamlanır (2 = yarım günler).</summary>
    public const int DataVersion = 2;

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<HolidayAutoLoader> _log;

    public HolidayAutoLoader(IServiceScopeFactory scopes, ILogger<HolidayAutoLoader> log)
    {
        _scopes = scopes; _log = log;
    }

    public static bool Enabled =>
        !string.Equals(Environment.GetEnvironmentVariable("LEAVE_HOLIDAY_AUTOLOAD"), "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>Tamamlanacak yıllar (saf): bu yıl ve gelecek yıl.</summary>
    public static int[] TargetYears(DateOnly today) => new[] { today.Year, today.Year + 1 };

    /// <summary>Şirket × yıl için yükleme gerekli mi (saf): hiç yüklenmemiş ya da eski sürümle yüklenmiş.</summary>
    public static bool NeedsLoad(int? appliedVersion) => appliedVersion is null || appliedVersion < DataVersion;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled) return;
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(DateOnly.FromDateTime(DateTime.UtcNow), stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning("Resmî tatil otomatik yüklemesi yapılamadı: {Error}", ex.Message);
            }
            try { await Task.Delay(TimeSpan.FromHours(24), stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }

    public async Task RunOnceAsync(DateOnly today, CancellationToken ct)
    {
        List<string> tenants;
        using (var scope = _scopes.CreateScope())
        {
            var t = scope.ServiceProvider.GetRequiredService<TenantContext>();
            t.IsPlatformAdmin = true;
            var db = scope.ServiceProvider.GetRequiredService<LeaveDbContext>();
            var oct29 = new DateOnly(today.Year, 10, 29);
            var oct29Prev = new DateOnly(today.Year - 1, 10, 29);
            tenants = (await db.PublicHolidays.AsNoTracking().Where(h => h.Date == oct29 || h.Date == oct29Prev)
                .Select(h => h.TenantSlug).Distinct().ToListAsync(ct)).Where(s => !string.IsNullOrEmpty(s)).ToList();
        }
        foreach (var tenant in tenants)
        {
            foreach (var year in TargetYears(today))
            {
                using var scope = _scopes.CreateScope();
                var t = scope.ServiceProvider.GetRequiredService<TenantContext>();
                t.TenantSlug = tenant;
                t.IsPlatformAdmin = false;
                var db = scope.ServiceProvider.GetRequiredService<LeaveDbContext>();
                var applied = await db.Database.SqlQueryRaw<int?>(
                    "SELECT \"Version\" AS \"Value\" FROM leave_holiday_autoloads WHERE \"TenantSlug\" = {0} AND \"Year\" = {1}",
                    tenant, year).FirstOrDefaultAsync(ct);
                if (!NeedsLoad(applied)) continue;
                var (added, _, half) = await Controllers.PublicHolidaysController.SeedAsync(db, year, true, ct);
                await db.Database.ExecuteSqlRawAsync("""
                    INSERT INTO leave_holiday_autoloads ("Id","TenantSlug","Year","Version","Added","AppliedAt")
                    VALUES ({0},{1},{2},{3},{4},now())
                    ON CONFLICT ("TenantSlug","Year") DO UPDATE SET "Version" = EXCLUDED."Version", "Added" = EXCLUDED."Added", "AppliedAt" = now()
                    """, new object[] { Guid.NewGuid(), tenant, year, DataVersion, added }, ct);
                if (added > 0)
                    _log.LogInformation("{Tenant}: {Year} resmî tatilleri otomatik tamamlandı ({Added} gün, {Half} yarım gün)", tenant, year, added, half);
            }
        }
    }
}

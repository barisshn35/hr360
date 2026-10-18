using Microsoft.EntityFrameworkCore;
using RecruitmentService.Controllers;
using RecruitmentService.Data;
using RecruitmentService.Models;

namespace RecruitmentService.Services;

/// <summary>
/// Dalga 11 (74): durum bağlantısı GET uçları için ayrı, daha geniş sınır (bağlantı sık yenilenebilir).
/// RECRUITMENT_STATUS_RATE_LIMIT (varsayılan 60) / RECRUITMENT_STATUS_RATE_WINDOW_SECONDS (varsayılan 600).
/// nginx'teki hr360_public bölgesi (IP başına 2 istek/sn) ayrıca geçerlidir.
/// </summary>
public sealed class StatusRateLimiter : PublicRateLimiter
{
    public StatusRateLimiter() : base(
        int.TryParse(Environment.GetEnvironmentVariable("RECRUITMENT_STATUS_RATE_LIMIT"), out var l) && l > 0 ? l : 60,
        TimeSpan.FromSeconds(int.TryParse(Environment.GetEnvironmentVariable("RECRUITMENT_STATUS_RATE_WINDOW_SECONDS"), out var w) && w > 0 ? w : 600))
    { }
}

public static class RecruitmentW11
{
    /// <summary>Genel adres: PUBLIC_ORIGIN, yoksa isteğin adresi (gateway arkasında X-Forwarded-*).</summary>
    public static string PublicOrigin(HttpContext? http)
    {
        var env = Environment.GetEnvironmentVariable("PUBLIC_ORIGIN");
        if (!string.IsNullOrWhiteSpace(env)) return env.TrimEnd('/');
        if (http is null) return "";
        var proto = http.Request.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? http.Request.Scheme;
        var host = http.Request.Headers["X-Forwarded-Host"].FirstOrDefault() ?? http.Request.Host.Value;
        return $"{proto}://{host}".TrimEnd('/');
    }

    public static async Task<RecruitmentProgramSettings> SettingsAsync(RecruitmentDbContext db, CancellationToken ct) =>
        await db.ProgramSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new RecruitmentProgramSettings();

    public static string StatusPath(string tenant, string token) => $"/kariyer/{tenant}/durum/{token}";

    /// <summary>
    /// Yeni durum bağlantısı üretir; başvurunun etkin bağlantıları "Reissued" olarak iptal edilir
    /// (aynı anda tek geçerli bağlantı). Kaydetmez; jetonu döner (yalnızca bir kez gösterilir).
    /// </summary>
    public static async Task<(StatusLink Link, string Token)> IssueStatusLinkAsync(RecruitmentDbContext db, Guid applicationId, int days, string? userId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var active = await db.StatusLinks.Where(l => l.ApplicationId == applicationId && l.RevokedAt == null && l.ExpiresAt > now).ToListAsync(ct);
        foreach (var l in active) { l.RevokedAt = now; l.RevokedBy = "Reissued"; }
        var token = PublicCareerController.NewToken();
        var link = new StatusLink
        {
            ApplicationId = applicationId,
            TokenHash = PublicCareerController.Hash(token),
            ExpiresAt = now.AddDays(Math.Clamp(days, 1, StatusLinkRules.MaxDays)),
            CreatedByUserId = userId,
        };
        db.StatusLinks.Add(link);
        return (link, token);
    }
}

/// <summary>
/// Dalga 11 (73): öneri ödülü hak ediş turu. İşe alınan önerilerde deneme süresi dolunca durumu
/// "Eligible" yapar (İK panosunda "onay bekliyor" sayılır); onay/ödeme İK kararıdır. Beklenen yarışlar (başvuru/aday bu
/// arada silindi, tablo henüz yok) hata olarak günlüğe düşmez.
/// </summary>
public class ReferralRewardWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<ReferralRewardWorker> _log;

    public ReferralRewardWorker(IServiceProvider services, ILogger<ReferralRewardWorker> log)
    {
        _services = services;
        _log = log;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        HR360.Platform.LeaderGate.RunAsync("recruitment/ReferralRewardWorker", stoppingToken, LeaderLoopAsync);

    private async Task LeaderLoopAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(90), stoppingToken); } catch (OperationCanceledException) { return; }
        var hours = int.TryParse(Environment.GetEnvironmentVariable("RECRUITMENT_REFERRAL_INTERVAL_HOURS"), out var h) && h > 0 ? h : 6;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                var tenant = scope.ServiceProvider.GetRequiredService<Tenancy.TenantContext>();
                tenant.IsPlatformAdmin = true; // tüm kiracılar; satırlar kendi TenantSlug'ıyla güncellenir
                var db = scope.ServiceProvider.GetRequiredService<RecruitmentDbContext>();
                var changed = await ReferralRewardEvaluator.RunAsync(db, null, stoppingToken);
                if (changed > 0) _log.LogInformation("Öneri ödülü turu: {Count} öneri güncellendi", changed);
            }
            catch (OperationCanceledException) { break; }
            catch (Npgsql.PostgresException ex) when (ex.SqlState == "42P01")
            {
                _log.LogInformation("Öneri tabloları henüz yok (migration uygulanmamış); tur atlandı");
            }
            catch (DbUpdateConcurrencyException)
            {
                _log.LogInformation("Öneri ödülü turu: kayıt bu arada değişti/silindi; sonraki turda yeniden denenecek");
            }
            catch (Exception ex) { _log.LogWarning(ex, "Öneri ödülü turu tamamlanamadı; sonraki turda yeniden denenecek"); }
            try { await Task.Delay(TimeSpan.FromHours(hours), stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
}

public static class ReferralRewardEvaluator
{
    private sealed class EmpStatus
    {
        public string Email { get; set; } = "";
        public string Status { get; set; } = "";
        public DateOnly? HireDate { get; set; }
    }

    /// <summary>Kiracının (null = tüm kiracılar; çağıran tenant filtresini ayarlar) önerilerini değerlendirir.</summary>
    public static async Task<int> RunAsync(RecruitmentDbContext db, string? tenant, CancellationToken ct)
    {
        var open = new[] { ReferralRewardStatus.None, ReferralRewardStatus.Waiting, ReferralRewardStatus.Eligible };
        var rows = await db.Referrals.Where(r => open.Contains(r.RewardStatus) && r.ApplicationId != null)
            .Where(r => tenant == null || r.TenantSlug == tenant)
            .OrderBy(r => r.CreatedAt).Take(2000).ToListAsync(ct);
        if (rows.Count == 0) return 0;
        var appIds = rows.Select(r => r.ApplicationId!.Value).ToList();
        var apps = await db.Applications.AsNoTracking().Include(a => a.Candidate).Where(a => appIds.Contains(a.Id))
            .Select(a => new { a.Id, a.Status, a.StatusChangedAt, Email = a.Candidate!.Email }).ToListAsync(ct);
        var hiredEvents = await db.StageEvents.AsNoTracking().Where(e => appIds.Contains(e.ApplicationId) && e.ToStatus == "Hired")
            .GroupBy(e => e.ApplicationId).Select(g => new { g.Key, At = g.Max(e => e.ChangedAt) }).ToListAsync(ct);
        // Deneme süresi işe BAŞLAMA tarihinden sayılır (teklif kabulünden değil): kabul edilen teklifin başlama tarihi.
        var offerStarts = await db.Offers.AsNoTracking().Where(o => appIds.Contains(o.ApplicationId) && o.Status == OfferStatus.Accepted)
            .GroupBy(o => o.ApplicationId).Select(g => new { g.Key, Start = g.Max(o => o.StartDate) }).ToListAsync(ct);
        var settings = await db.ProgramSettings.AsNoTracking().ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var changed = 0;
        foreach (var r in rows)
        {
            var app = apps.FirstOrDefault(a => a.Id == r.ApplicationId);
            if (app is null) continue; // başvuru bu arada silindi (FK boşalacak)
            var hiredAt = hiredEvents.FirstOrDefault(e => e.Key == app.Id)?.At ?? (app.Status == ApplicationStatus.Hired ? app.StatusChangedAt : null);
            var s = settings.FirstOrDefault(x => x.TenantSlug == r.TenantSlug) ?? new RecruitmentProgramSettings();
            bool? employed = null;
            DateOnly? hireDate = null;
            if (app.Status == ApplicationStatus.Hired && !string.IsNullOrWhiteSpace(app.Email))
            {
                var emp = await db.Database.SqlQueryRaw<EmpStatus>(
                    "SELECT \"Email\", \"Status\", \"HireDate\" FROM employee_employees WHERE \"TenantSlug\" = {0} AND lower(\"Email\") = lower({1}) ORDER BY \"CreatedAt\" DESC LIMIT 1",
                    r.TenantSlug, app.Email).FirstOrDefaultAsync(ct);
                if (emp is not null) { employed = emp.Status != "Terminated"; hireDate = emp.HireDate; }
            }
            hiredAt = ReferralRules.ProbationStart(hireDate, offerStarts.FirstOrDefault(o => o.Key == app.Id)?.Start, hiredAt);
            var (next, hired, eligible) = ReferralRules.Step(r.RewardStatus, app.Status, hiredAt, s.ReferralProbationDays, now, employed);
            if (next == r.RewardStatus && hired == r.HiredAt && eligible == r.RewardEligibleAt) continue;
            r.RewardStatus = next;
            r.HiredAt = hired;
            r.RewardEligibleAt = eligible;
            if (next == ReferralRewardStatus.Eligible && r.RewardAmount is null)
            {
                r.RewardAmount = s.ReferralRewardAmount;
                r.RewardCurrency = s.ReferralRewardCurrency;
            }
            changed++;
        }
        if (changed > 0) await db.SaveChangesAsync(ct);
        return changed;
    }
}

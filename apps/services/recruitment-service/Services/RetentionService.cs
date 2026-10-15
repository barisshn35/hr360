using Microsoft.EntityFrameworkCore;
using RecruitmentService.Data;
using RecruitmentService.Models;
using RecruitmentService.Tenancy;

namespace RecruitmentService.Services;

/// <summary>
/// Y16 / KVKK: aday verisinin saklama süresi sonunda imhası.
///  * RECRUITMENT_RETENTION_DAYS (varsayılan 180): açık rıza yoksa başvuru kapandıktan sonra.
///  * RECRUITMENT_POOL_MONTHS (varsayılan 24): aday havuzu açık rızası varsa.
/// İmha yöntemi geri döndürülemez anonimleştirmedir (governance "RejectedCandidates" politikasıyla
/// aynı biçim: Anonim Aday / anon-…@anonim.invalid); aşama istatistikleri korunur.
/// </summary>
public static class RetentionService
{
    public static int RetentionDays =>
        int.TryParse(Environment.GetEnvironmentVariable("RECRUITMENT_RETENTION_DAYS"), out var d) && d > 0 ? d : 180;

    public static int PoolMonths =>
        int.TryParse(Environment.GetEnvironmentVariable("RECRUITMENT_POOL_MONTHS"), out var m) && m > 0 ? m : 24;

    /// <summary>Saklama süresi dolan adayları anonimleştirir; kiracı başına sayıyı döner.</summary>
    public static async Task<Dictionary<string, int>> RunAsync(RecruitmentDbContext db, string trigger, string actor, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        // Süresi geçen gönderilmiş teklifler.
        var expired = await db.Offers.Where(o => o.Status == OfferStatus.Sent && o.ExpiresAt < today).ToListAsync(ct);
        foreach (var o in expired) o.Status = OfferStatus.Expired;

        var candidates = await db.Candidates.AsNoTracking()
            .Where(c => c.AnonymizedAt == null)
            .Include(c => c.Applications).ThenInclude(a => a.JobPosting)
            .ToListAsync(ct);
        var result = new Dictionary<string, int>();
        foreach (var c in candidates)
        {
            var due = RetentionRules.DueAt(c.CreatedAt, c.TalentPoolConsent, c.TalentPoolConsentAt,
                c.Applications.Select(a => new RetentionRules.AppSnapshot(a.Status, a.AppliedAt, a.StatusChangedAt,
                    a.JobPosting?.Status == JobPostingStatus.Closed ? a.JobPosting.ClosedAt ?? a.JobPosting.CreatedAt : null)).ToList(),
                RetentionDays, PoolMonths);
            if (due is null || due > now) continue;
            await db.EnqueueResumeDeletionAsync(c.TenantSlug, c.Id, "RecruitmentCandidates", ct);
            await AnonymizeAsync(db, c.Id, ct);
            result[c.TenantSlug] = result.GetValueOrDefault(c.TenantSlug) + 1;
        }
        await db.SaveChangesAsync(ct);
        foreach (var (tenant, n) in result)
            await db.DestructionLogAsync(tenant, n, Math.Max(1, RetentionDays / 30), trigger, actor, ct);
        return result;
    }

    /// <summary>
    /// Adayın kimlik/iletişim/serbest metin alanlarını geri döndürülemez biçimde siler. Toplu SQL ile
    /// yapılır: EF değişiklik izleme üzerinden yapılsaydı denetim kaydı (audit_log, değiştirilemez)
    /// eski değerleri — yani silinen kişisel veriyi — kalıcı olarak saklardı.
    /// </summary>
    /// <summary>ScoresJson'dan kanıt notlarını çıkaran ifade (puanlar kalır). Süslü parantez içermez (format dizesinde kullanılır).</summary>
    internal const string ScoresWithoutEvidence =
        "coalesce((SELECT jsonb_agg(e - 'evidence' - 'Evidence') FROM jsonb_array_elements(\"ScoresJson\"::jsonb) e)::text, '[]')";

    /// <summary>Adaya ait önerilerdeki serbest metni temizler. Aday silinmeden ÖNCE de çağrılır: FK "SET NULL" notu bırakırdı.</summary>
    internal const string ReferralScrubSql =
        "UPDATE recruitment_referrals SET \"Note\" = NULL, \"Relationship\" = NULL WHERE \"CandidateId\" = {0} AND (\"Note\" IS NOT NULL OR \"Relationship\" IS NOT NULL)";

    public static async Task AnonymizeAsync(RecruitmentDbContext db, Guid candidateId, CancellationToken ct)
    {
        const string apps = "SELECT \"Id\" FROM recruitment_applications WHERE \"CandidateId\" = {0}";
        await db.Database.ExecuteSqlRawAsync("""
            UPDATE recruitment_candidates SET "FirstName" = 'Anonim', "LastName" = 'Aday', "Email" = 'anon-' || "Id" || '@anonim.invalid',
                "Phone" = NULL, "NormalizedEmail" = NULL, "NormalizedPhone" = NULL, "ResumeStorageKey" = NULL, "ResumeText" = NULL,
                "Skills" = '{{}}', "Source" = NULL, "TalentPoolConsent" = false, "AnonymizedAt" = now()
            WHERE "Id" = {0}
            """, new object[] { candidateId }, ct);
        await db.Database.ExecuteSqlRawAsync("""
            UPDATE recruitment_applications SET "CoverNote" = NULL, "Notes" = NULL, "SelfServiceTokenHash" = NULL, "DuplicateReason" = NULL
            WHERE "CandidateId" = {0}
            """, new object[] { candidateId }, ct);
        await db.Database.ExecuteSqlRawAsync(
            $"UPDATE recruitment_interviews SET \"Notes\" = NULL, \"Location\" = NULL, \"MeetingUrl\" = NULL WHERE \"ApplicationId\" IN ({apps})",
            new object[] { candidateId }, ct);
        // Dalga 11: ölçüt kanıt notları (ScoresJson içindeki "evidence") da serbest metindir; puanlar kalır.
        await db.Database.ExecuteSqlRawAsync(
            $"UPDATE recruitment_scorecards SET \"Notes\" = NULL, \"ScoresJson\" = {ScoresWithoutEvidence} WHERE \"InterviewId\" IN (SELECT \"Id\" FROM recruitment_interviews WHERE \"ApplicationId\" IN ({apps}))",
            new object[] { candidateId }, ct);
        // Dalga 11: durum bağlantıları (jeton özeti) silinir; çalışan önerisindeki serbest metin (öneren notu, yakınlık) temizlenir.
        await db.Database.ExecuteSqlRawAsync(
            $"DELETE FROM recruitment_status_links WHERE \"ApplicationId\" IN ({apps})", new object[] { candidateId }, ct);
        await db.Database.ExecuteSqlRawAsync(ReferralScrubSql, new object[] { candidateId }, ct);
        await db.Database.ExecuteSqlRawAsync(
            $"UPDATE recruitment_offers SET \"LetterText\" = '(anonimleştirildi)', \"Benefits\" = NULL, \"DecisionNote\" = NULL, \"SignedLetterHtml\" = NULL, \"SignTokenHash\" = NULL WHERE \"ApplicationId\" IN ({apps})",
            new object[] { candidateId }, ct);
    }
}

/// <summary>Periyodik imha işçisi: RECRUITMENT_RETENTION_INTERVAL_MINUTES (varsayılan 360).</summary>
public class RetentionWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<RetentionWorker> _log;

    public RetentionWorker(IServiceProvider services, ILogger<RetentionWorker> log)
    {
        _services = services;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minutes = int.TryParse(Environment.GetEnvironmentVariable("RECRUITMENT_RETENTION_INTERVAL_MINUTES"), out var m) && m > 0 ? m : 360;
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                var tenant = scope.ServiceProvider.GetRequiredService<TenantContext>();
                tenant.IsPlatformAdmin = true; // sistem işi: tüm kiracılar
                var db = scope.ServiceProvider.GetRequiredService<RecruitmentDbContext>();
                var done = await RetentionService.RunAsync(db, "Periodic", "recruitment-service", stoppingToken);
                if (done.Count > 0) _log.LogInformation("Aday verisi imhası: {Counts}", string.Join(", ", done.Select(kv => $"{kv.Key}={kv.Value}")));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogError(ex, "Aday verisi imha turu başarısız"); }
            try { await Task.Delay(TimeSpan.FromMinutes(minutes), stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
}

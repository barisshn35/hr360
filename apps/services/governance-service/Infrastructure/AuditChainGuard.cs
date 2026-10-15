using Prometheus;
using GovernanceService.Infrastructure.Chat;
using GovernanceService.Tenancy;

namespace GovernanceService.Infrastructure;

/// <summary>Bir kiracının denetim zinciri doğrulama sonucu.</summary>
public sealed record AuditChainResult(long Rows, long Tampered, long Broken, long Gaps, long Unchained,
    long? FirstProblemSeq, long? FromSeq, long? ToSeq, string? Head)
{
    public bool Ok => Tampered == 0 && Broken == 0 && Gaps == 0 && Unchained == 0;
}

/// <summary>
/// G21 + güvenlik dalgası 1: denetim kaydı zincirinin doğrulanması ve gecelik çapa (anchor).
///
/// Zincir doğrulaması tek başına satırın sonradan değiştirildiğini yakalar; ancak veritabanında
/// tam yetkili biri tüm zinciri baştan hesaplarsa ya da sondan satır silerse zincir yine "tutarlı"
/// görünür. Bu yüzden her gece her kiracının zincir başı (son sıra no + özet) governance_audit_anchors
/// tablosuna ve servis günlüğüne (Loki/SIEM, veritabanı dışı kopya) yazılır; sonraki turda önceki
/// çapanın satırı aynı özetle hâlâ yerinde mi ve zincir geriye gitmiş mi diye bakılır. Saklama
/// süresi dolan en eski satırların silinmesi zincirin başını kısaltır; bu hata sayılmaz.
/// Sorun bulunursa hr360_audit_chain_problems ölçümü 0'dan büyük olur ve Prometheus alarmı çalar.
/// </summary>
public sealed class AuditChainGuard : BackgroundService
{
    public static readonly Gauge Problems = Metrics.CreateGauge("hr360_audit_chain_problems",
        "Son gecelik denetimde zinciri bozuk ya da çapası tutmayan kiracı sayısı.");
    public static readonly Gauge LastRun = Metrics.CreateGauge("hr360_audit_chain_last_check_timestamp_seconds",
        "Son gecelik denetim zinciri kontrolünün zamanı (Unix saniye).");

    private static readonly TimeSpan Interval = TimeSpan.FromHours(
        double.TryParse(EnvVar.Or("AUDIT_CHAIN_CHECK_HOURS", "24"), out var h) ? Math.Clamp(h, 0.01, 168) : 24);

    private readonly IServiceProvider _sp;
    private readonly ILogger<AuditChainGuard> _log;
    public AuditChainGuard(IServiceProvider sp, ILogger<AuditChainGuard> log) { _sp = sp; _log = log; }

    public static async Task<AuditChainResult> VerifyAsync(Sql db, string tenant, CancellationToken ct)
    {
        var rows = await db.QueryAsync("""
            WITH c AS (
                SELECT "ChainSeq", "Hash", "PrevHash",
                       audit_row_hash("PrevHash", "ChainSeq", "TenantSlug", "Service", "EntityType", "EntityId", "Action", "Changes",
                                      "UserId", "UserName", "CorrelationId", "IpAddress", "OccurredAt") AS calc,
                       lag("Hash") OVER (ORDER BY "ChainSeq") AS prev_hash,
                       lag("ChainSeq") OVER (ORDER BY "ChainSeq") AS prev_seq
                FROM audit_log WHERE coalesce("TenantSlug", '') = $1 AND "ChainSeq" IS NOT NULL)
            SELECT count(*),
                   count(*) FILTER (WHERE calc <> "Hash"),
                   count(*) FILTER (WHERE prev_hash IS NOT NULL AND "PrevHash" <> prev_hash),
                   count(*) FILTER (WHERE prev_seq IS NOT NULL AND "ChainSeq" <> prev_seq + 1),
                   min("ChainSeq") FILTER (WHERE calc <> "Hash" OR (prev_hash IS NOT NULL AND "PrevHash" <> prev_hash) OR (prev_seq IS NOT NULL AND "ChainSeq" <> prev_seq + 1)),
                   min("ChainSeq"), max("ChainSeq"),
                   (SELECT "Hash" FROM c ORDER BY "ChainSeq" DESC LIMIT 1),
                   (SELECT count(*) FROM audit_log WHERE coalesce("TenantSlug", '') = $1 AND "ChainSeq" IS NULL)
            FROM c
            """, r => new AuditChainResult(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(8),
                r.IsDBNull(4) ? null : r.GetInt64(4), r.IsDBNull(5) ? null : r.GetInt64(5), r.IsDBNull(6) ? null : r.GetInt64(6), r.Str(7)),
            ct, tenant);
        return rows[0];
    }

    /// <summary>Önceki çapa ile bugünkü zinciri karşılaştırır; sorun yoksa null döner.</summary>
    public static async Task<string?> CheckAnchorAsync(Sql db, string tenant, AuditChainResult now, CancellationToken ct)
    {
        var prev = (await db.QueryAsync("""
            SELECT "ToSeq", "HeadHash" FROM governance_audit_anchors
             WHERE "TenantSlug" = $1 AND "ToSeq" IS NOT NULL ORDER BY "CheckedAt" DESC LIMIT 1
            """, r => (Seq: r.GetInt64(0), Hash: r.Str(1)), ct, tenant)).FirstOrDefault();
        if (prev.Hash is null) return null;
        if (now.ToSeq is null || now.ToSeq < prev.Seq) return $"Zincir geriye gitti: önceki çapa {prev.Seq}, şimdiki son {now.ToSeq?.ToString() ?? "yok"}";
        if (now.FromSeq > prev.Seq) return null; // çapa satırı saklama süresiyle silinmiş (zincirin başı kısaldı)
        var hash = await db.ScalarAsync("""SELECT "Hash" FROM audit_log WHERE coalesce("TenantSlug", '') = $1 AND "ChainSeq" = $2""", ct, tenant, prev.Seq);
        return Equals(hash as string, prev.Hash) ? null : $"Çapa satırı ({prev.Seq}) değişmiş ya da silinmiş";
    }

    public static async Task<bool> RunOnceAsync(Sql db, ILogger log, CancellationToken ct)
    {
        var tenants = await db.QueryAsync("""
            SELECT DISTINCT coalesce("TenantSlug", '') FROM audit_log
            UNION SELECT DISTINCT "TenantSlug" FROM governance_audit_anchors
            """, r => r.GetString(0), ct);
        var problems = 0;
        foreach (var t in tenants)
        {
            var res = await VerifyAsync(db, t, ct);
            var anchorProblem = await CheckAnchorAsync(db, t, res, ct);
            var problem = !res.Ok
                ? $"Zincir bozuk: değişmiş {res.Tampered}, kopuk {res.Broken}, boşluk {res.Gaps}, zincirsiz {res.Unchained}, ilk sorunlu sıra {res.FirstProblemSeq}"
                : anchorProblem;
            if (problem is not null) problems++;
            await db.ExecuteAsync("""
                INSERT INTO governance_audit_anchors ("Id","TenantSlug","CheckedAt","Ok","Rows","FromSeq","ToSeq","HeadHash","Problem")
                VALUES (gen_random_uuid(), $1, now(), $2, $3, $4, $5, $6, $7)
                """, ct, t, problem is null, res.Rows, res.FromSeq, res.ToSeq, res.Head, problem);
            // Veritabanı dışında da bir kopya kalsın diye çapa günlüğe yazılır (Loki/SIEM).
            if (problem is null)
                log.LogInformation("Denetim zinciri çapası: kiracı={Tenant} sıra={Seq} özet={Head} satır={Rows}", t == "" ? "(platform)" : t, res.ToSeq, res.Head, res.Rows);
            else
                log.LogError("Denetim zinciri SORUNU: kiracı={Tenant} {Problem}", t == "" ? "(platform)" : t, problem);
        }
        // 400 günden eski doğrulama kayıtları tutulmaz; kiracı başına son çapa her zaman kalır.
        await db.ExecuteAsync("""
            DELETE FROM governance_audit_anchors a WHERE "CheckedAt" < now() - interval '400 days'
               AND EXISTS (SELECT 1 FROM governance_audit_anchors b WHERE b."TenantSlug" = a."TenantSlug" AND b."CheckedAt" > a."CheckedAt")
            """, ct);
        Problems.Set(problems);
        LastRun.SetToCurrentTimeUtc();
        return problems == 0;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromMinutes(2), ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = _sp.CreateScope();
                scope.ServiceProvider.GetRequiredService<TenantContext>().IsPlatformAdmin = true;
                await RunOnceAsync(scope.ServiceProvider.GetRequiredService<Sql>(), _log, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Denetim zinciri gecelik kontrolü hata verdi");
            }
            await Task.Delay(Interval, ct);
        }
    }
}

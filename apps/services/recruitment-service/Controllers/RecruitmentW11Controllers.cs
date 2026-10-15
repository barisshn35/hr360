using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RecruitmentService.Data;
using RecruitmentService.Models;
using RecruitmentService.Services;
using RecruitmentService.Tenancy;

namespace RecruitmentService.Controllers;

/// <summary>
/// Dalga 11 (74): İK tarafı — başvuru için oturumsuz durum bağlantısı üretme, listeleme ve iptal.
/// Bağlantı yalnızca kaba aşamayı ve sonraki adımı gösterir (ad, iletişim, not, puan yok); bu yüzden
/// e-postayla gönderilebilir. Jetonun yalnızca özeti saklanır; yeni bağlantı eskisini geçersiz kılar.
/// </summary>
[ApiController]
[Route("api/applications/{applicationId:guid}/status-links")]
[Authorize(Policy = "RequireManagerOrAbove")]
public class StatusLinksController : ControllerBase
{
    private readonly RecruitmentDbContext _db;
    private readonly ITenantContext _tenant;

    public StatusLinksController(RecruitmentDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    private string Tenant => _tenant.TenantSlug ?? "";

    private static object View(StatusLink l, DateTimeOffset now) => new
    {
        l.Id, l.CreatedAt, l.ExpiresAt, l.RevokedAt, l.RevokedBy, l.LastViewedAt, l.ViewCount,
        active = StatusLinkRules.IsActive(l.RevokedAt, l.ExpiresAt, now),
    };

    [HttpGet]
    public async Task<IActionResult> List(Guid applicationId, CancellationToken ct)
    {
        if (!await _db.Applications.AnyAsync(a => a.Id == applicationId, ct)) return NotFound(new { message = "Başvuru bulunamadı" });
        var now = DateTimeOffset.UtcNow;
        var rows = await _db.StatusLinks.AsNoTracking().Where(l => l.ApplicationId == applicationId).OrderByDescending(l => l.CreatedAt).Take(20).ToListAsync(ct);
        return Ok(rows.Select(l => View(l, now)));
    }

    public record IssueBody(int? Days, bool SendEmail);

    [HttpPost]
    public async Task<IActionResult> Issue(Guid applicationId, [FromBody] IssueBody b, CancellationToken ct)
    {
        var app = await _db.Applications.Include(a => a.Candidate).Include(a => a.JobPosting).FirstOrDefaultAsync(a => a.Id == applicationId, ct);
        if (app?.Candidate is null) return NotFound(new { message = "Başvuru bulunamadı" });
        if (app.Candidate.AnonymizedAt is not null) return Conflict(new { message = "Anonimleştirilmiş aday için bağlantı üretilemez" });
        var days = b.Days ?? StatusLinkRules.DefaultDays;
        if (days is < 1 or > StatusLinkRules.MaxDays) return BadRequest(new { message = $"Geçerlilik 1-{StatusLinkRules.MaxDays} gün olmalı" });
        var (link, token) = await RecruitmentW11.IssueStatusLinkAsync(_db, app.Id, days, RecruitmentSql.UserId(User), ct);
        await _db.SaveChangesAsync(ct);
        var path = RecruitmentW11.StatusPath(Tenant, token);
        if (b.SendEmail)
        {
            var company = (await _db.TenantAsync(Tenant, ct))?.Name ?? Tenant;
            await _db.EmailCandidateAsync(Tenant, app.Candidate.Email, $"Başvuru durumu — {company}",
                $"Merhaba {app.Candidate.FirstName}, \"{app.JobPosting?.Title}\" başvurunuzun genel durumunu ve sonraki adımı şu bağlantıdan izleyebilirsiniz "
                + $"(kişisel veri göstermez, {link.ExpiresAt:dd.MM.yyyy} tarihine kadar geçerlidir; sayfadan iptal edebilirsiniz): "
                + RecruitmentW11.PublicOrigin(HttpContext) + path,
                "recruitment.status.link", ct);
        }
        await _db.AuditAsync(HttpContext, Tenant, "Application", app.Id.ToString(), "StatusLinkIssued", new { days, emailed = b.SendEmail }, ct);
        return Ok(new { link = View(link, DateTimeOffset.UtcNow), path, emailed = b.SendEmail });
    }

    [HttpPost("revoke")]
    public async Task<IActionResult> Revoke(Guid applicationId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var rows = await _db.StatusLinks.Where(l => l.ApplicationId == applicationId && l.RevokedAt == null && l.ExpiresAt > now).ToListAsync(ct);
        foreach (var l in rows) { l.RevokedAt = now; l.RevokedBy = "Hr"; }
        await _db.SaveChangesAsync(ct);
        if (rows.Count > 0)
            await _db.AuditAsync(HttpContext, Tenant, "Application", applicationId.ToString(), "StatusLinkRevoked", new { count = rows.Count }, ct);
        return Ok(new { revoked = rows.Count });
    }
}

/// <summary>
/// Dalga 11 (74, 75): oturumsuz uçlar — aday durum bağlantısı ve ilan ayrıntısı (Google for Jobs
/// JSON-LD). nginx'teki /api/recruitment/public/ sınırı (hr360_public) ve serviste IP başına bellek
/// sınırı geçerlidir; IP veritabanına yazılmaz.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/public/{tenantSlug}")]
public class PublicRecruitmentW11Controller : ControllerBase
{
    private readonly RecruitmentDbContext _db;
    private readonly TenantContext _tenant;
    private readonly StatusRateLimiter _limiter;

    public PublicRecruitmentW11Controller(RecruitmentDbContext db, TenantContext tenant, StatusRateLimiter limiter)
    {
        _db = db;
        _tenant = tenant;
        _limiter = limiter;
    }

    private static readonly Regex SlugRx = new("^[a-z0-9][a-z0-9-]{1,63}$", RegexOptions.Compiled);

    private async Task<RecruitmentSql.TenantRow?> ResolveAsync(string slug, CancellationToken ct)
    {
        if (!SlugRx.IsMatch(slug)) return null;
        _tenant.TenantSlug = slug;
        _tenant.IsPlatformAdmin = false;
        var t = await _db.TenantAsync(slug, ct);
        return t is not null && string.Equals(t.Status, "Active", StringComparison.OrdinalIgnoreCase) ? t : null;
    }

    private bool Limited()
    {
        var key = PublicRateLimiter.ClientKey(HttpContext);
        Request.Headers.Remove("X-Real-IP");
        Request.Headers.Remove("X-Forwarded-For");
        return !_limiter.TryAcquire(key);
    }

    private IActionResult TooMany() => StatusCode(StatusCodes.Status429TooManyRequests,
        new { message = "Çok fazla istek gönderildi. Lütfen birkaç dakika sonra tekrar deneyin." });

    private static readonly object Invalid = new { message = "Bağlantı geçersiz, süresi dolmuş ya da iptal edilmiş" };

    private async Task<StatusLink?> LinkAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length is < 20 or > 100) return null;
        var hash = PublicCareerController.Hash(token);
        var l = await _db.StatusLinks.FirstOrDefaultAsync(x => x.TokenHash == hash, ct);
        return l is not null && StatusLinkRules.IsActive(l.RevokedAt, l.ExpiresAt, DateTimeOffset.UtcNow) ? l : null;
    }

    /// <summary>Kaba aşama + sonraki adım. Ad, iletişim, not, puan, ücret gösterilmez.</summary>
    [HttpGet("status/{token}")]
    public async Task<IActionResult> Status(string tenantSlug, string token, CancellationToken ct)
    {
        if (Limited()) return TooMany();
        var t = await ResolveAsync(tenantSlug, ct);
        if (t is null) return NotFound(new { message = "Kariyer sayfası bulunamadı" });
        var l = await LinkAsync(token, ct);
        if (l is null) return NotFound(Invalid);
        var a = await _db.Applications.AsNoTracking().Include(x => x.JobPosting).FirstOrDefaultAsync(x => x.Id == l.ApplicationId, ct);
        if (a is null) return NotFound(Invalid);
        var now = DateTimeOffset.UtcNow;
        var hasInterview = await _db.Interviews.AnyAsync(i => i.ApplicationId == a.Id && i.Result == InterviewResult.Pending && i.ScheduledAt > now, ct);
        var offer = await _db.Offers.AsNoTracking().Where(o => o.ApplicationId == a.Id).OrderByDescending(o => o.CreatedAt)
            .Select(o => (OfferStatus?)o.Status).FirstOrDefaultAsync(ct);
        var (code, label, next) = StatusLinkRules.View(a.Status, a.JobPosting?.Status == JobPostingStatus.Closed, hasInterview ? now : null, offer);
        l.LastViewedAt = now;
        l.ViewCount++;
        await _db.SaveChangesAsync(ct);
        return Ok(new
        {
            company = t.Name, posting = a.JobPosting?.Title, status = code, statusLabel = label, nextStep = next,
            appliedAt = a.AppliedAt, updatedAt = a.StatusChangedAt ?? a.AppliedAt, expiresAt = l.ExpiresAt,
        });
    }

    /// <summary>Aday bağlantıyı kendisi iptal eder (ör. yanlışlıkla paylaştıysa).</summary>
    [HttpPost("status/{token}/revoke")]
    public async Task<IActionResult> Revoke(string tenantSlug, string token, CancellationToken ct)
    {
        if (Limited()) return TooMany();
        if (await ResolveAsync(tenantSlug, ct) is null) return NotFound(new { message = "Kariyer sayfası bulunamadı" });
        var l = await LinkAsync(token, ct);
        if (l is null) return NotFound(Invalid);
        l.RevokedAt = DateTimeOffset.UtcNow;
        l.RevokedBy = "Candidate";
        await _db.SaveChangesAsync(ct);
        await _db.AuditAsync(HttpContext, tenantSlug, "Application", l.ApplicationId.ToString(), "StatusLinkRevoked", new { by = "candidate" }, ct);
        return Ok(new { revoked = true });
    }

    /// <summary>İlan ayrıntısı + schema.org JobPosting (Google for Jobs). Ücret yalnızca kiracı açarsa.</summary>
    [HttpGet("jobs/{id:guid}")]
    public async Task<IActionResult> Job(string tenantSlug, Guid id, CancellationToken ct)
    {
        var t = await ResolveAsync(tenantSlug, ct);
        if (t is null) return NotFound(new { message = "Kariyer sayfası bulunamadı" });
        var p = await _db.JobPostings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.Status == JobPostingStatus.Published, ct);
        if (p is null) return NotFound(new { message = "İlan bulunamadı ya da başvuruya kapalı" });
        var settings = await RecruitmentW11.SettingsAsync(_db, ct);
        var origin = RecruitmentW11.PublicOrigin(HttpContext);
        var dept = await _db.DepartmentNameAsync(tenantSlug, p.DepartmentId, ct);
        var expired = p.ValidThrough is { } vt && vt < DateTimeOffset.UtcNow;
        var showSalary = settings.PublishSalaryInJobPostings && (p.SalaryMin is > 0 || p.SalaryMax is > 0);
        JsonObject? jsonLd = expired ? null : JobPostingJsonLd.Build(p, t.Name, null, settings.PublishSalaryInJobPostings,
            string.IsNullOrEmpty(origin) ? null : $"{origin}/kariyer/{tenantSlug}/ilan/{p.Id}");
        return Ok(new
        {
            company = t.Name,
            job = new
            {
                p.Id, p.Title, p.Description, p.EmploymentType, department = dept, p.PublishedAt, p.ValidThrough,
                p.Location, p.Region, country = string.IsNullOrWhiteSpace(p.Country) ? "TR" : p.Country, p.RemoteAllowed,
                salary = showSalary ? new { min = p.SalaryMin, max = p.SalaryMax, currency = p.SalaryCurrency ?? "TRY", period = p.SalaryPeriod ?? "MONTH" } : null,
            },
            expired,
            jsonLd,
        });
    }
}

/// <summary>Dalga 11 (75): ilanın herkese açık yapılandırılmış veri alanları (İK).</summary>
[ApiController]
[Route("api/job-postings/{id:guid}/career-details")]
[Authorize(Policy = "RequireHrAdmin")]
public class JobPostingCareerController : ControllerBase
{
    private readonly RecruitmentDbContext _db;
    public JobPostingCareerController(RecruitmentDbContext db) => _db = db;

    public record Body(string? Location, string? Region, string? Country, bool RemoteAllowed, DateTimeOffset? ValidThrough,
        decimal? SalaryMin, decimal? SalaryMax, string? SalaryCurrency, string? SalaryPeriod);

    [HttpPut]
    public async Task<IActionResult> Put(Guid id, [FromBody] Body b, CancellationToken ct)
    {
        var p = await _db.JobPostings.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return NotFound(new { message = "İlan bulunamadı" });
        var error = JobPostingJsonLd.Validate(b.SalaryMin, b.SalaryMax, b.SalaryCurrency, b.SalaryPeriod, b.Country);
        if (error is not null) return BadRequest(new { message = error });
        if (b.Location is { Length: > 120 } || b.Region is { Length: > 120 }) return BadRequest(new { message = "Konum en fazla 120 karakter olabilir" });
        if (b.ValidThrough is { } vt && vt < DateTimeOffset.UtcNow.AddDays(-1)) return BadRequest(new { message = "Son başvuru tarihi geçmişte olamaz" });
        static string? T(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        p.Location = T(b.Location);
        p.Region = T(b.Region);
        p.Country = T(b.Country)?.ToUpperInvariant();
        p.RemoteAllowed = b.RemoteAllowed;
        p.ValidThrough = b.ValidThrough;
        p.SalaryMin = b.SalaryMin is > 0 ? Math.Round(b.SalaryMin.Value, 2) : null;
        p.SalaryMax = b.SalaryMax is > 0 ? Math.Round(b.SalaryMax.Value, 2) : null;
        p.SalaryCurrency = T(b.SalaryCurrency)?.ToUpperInvariant();
        p.SalaryPeriod = T(b.SalaryPeriod)?.ToUpperInvariant();
        await _db.SaveChangesAsync(ct);
        return Ok(new { p.Id, p.Location, p.Region, p.Country, p.RemoteAllowed, p.ValidThrough, p.SalaryMin, p.SalaryMax, p.SalaryCurrency, p.SalaryPeriod });
    }
}

/// <summary>
/// Dalga 11 (78): işe alım hunisi analizi (İK). Aşama geçmişi veritabanı tetikleyicisiyle tutulur.
/// KVKK: n &lt; 5 gruplar gizlenir (kişi düzeyinde çıkarım); kişi adı/kimliği dönmez.
/// </summary>
[ApiController]
[Route("api/recruitment-analytics")]
[Authorize(Policy = "RequireHrAdmin")]
public class RecruitmentAnalyticsController : ControllerBase
{
    private readonly RecruitmentDbContext _db;
    public RecruitmentAnalyticsController(RecruitmentDbContext db) => _db = db;

    [HttpGet("funnel")]
    public async Task<IActionResult> Funnel([FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, [FromQuery] Guid? jobPostingId,
        [FromQuery] Guid? departmentId, CancellationToken ct)
    {
        var start = from ?? DateTimeOffset.UtcNow.AddDays(-365);
        var end = to ?? DateTimeOffset.UtcNow;
        if (end < start || (end - start).TotalDays > 3 * 366) return BadRequest(new { message = "Tarih aralığı geçersiz (en fazla 3 yıl)" });
        var q = _db.Applications.AsNoTracking().Where(a => a.AppliedAt >= start && a.AppliedAt <= end);
        if (jobPostingId is { } pid) q = q.Where(a => a.JobPostingId == pid);
        if (departmentId is { } did) q = q.Where(a => a.JobPosting!.DepartmentId == did);
        var apps = await q.Select(a => new { a.Id, a.AppliedAt, a.Status, a.Channel, Source = a.Candidate!.Source }).ToListAsync(ct);
        var ids = apps.Select(a => a.Id).ToList();
        var events = await _db.StageEvents.AsNoTracking().Where(e => ids.Contains(e.ApplicationId))
            .Select(e => new { e.ApplicationId, e.FromStatus, e.ToStatus, e.ChangedAt }).ToListAsync(ct);
        var byApp = events.GroupBy(e => e.ApplicationId).ToDictionary(g => g.Key, g => g.Select(e => (e.FromStatus, e.ToStatus, e.ChangedAt)).ToList());
        var offers = await _db.Offers.AsNoTracking().Where(o => ids.Contains(o.ApplicationId)).Select(o => new FunnelAnalytics.OfferRow(o.Status)).ToListAsync(ct);
        var rows = apps.Select(a => new FunnelAnalytics.App(a.Id, a.AppliedAt, a.Status, SourceLabel(a.Channel, a.Source),
            byApp.TryGetValue(a.Id, out var ev) ? ev : new List<(string?, string, DateTimeOffset)>())).ToList();
        var result = FunnelAnalytics.Compute(rows, offers, DateTimeOffset.UtcNow);
        return Ok(new { from = start, to = end, result.Applications, result.Stages, result.TimeToHire, result.Sources, result.Offers, result.MinGroup });
    }

    /// <summary>Kaynak anahtarı: Career / Referral / Other ya da elle girilen kayıtta adayın kaynağı (serbest metin).</summary>
    public static string SourceLabel(string? channel, string? source) => channel switch
    {
        "Career" => "Career",
        "Referral" => "Referral",
        _ => string.IsNullOrWhiteSpace(source) ? "Other" : source.Trim().Length > 40 ? source.Trim()[..40] : source.Trim(),
    };
}

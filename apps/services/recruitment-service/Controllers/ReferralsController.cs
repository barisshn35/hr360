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
/// Dalga 11 (73): çalışan aday önerisi programı.
///  * Her çalışan yayındaki bir ilana aday önerebilir; adayın önerilmeyi kabul ettiğini onay kutusuyla
///    beyan etmesi zorunludur. Adaya KVKK m.10 aydınlatma e-postası (öneren adı yazılmadan) ve yalnızca
///    kaba durumu gösteren durum bağlantısı gider.
///  * Öneren yalnızca kaba durumu (değerlendiriliyor / işe alındı / kapandı) ve ödül durumunu görür.
///  * Ödül: işe girişten sonra deneme süresi dolunca hak edilir (ReferralRewardWorker); onay ve ödeme
///    İK kararıdır, her karar denetim kaydına yazılır.
/// </summary>
[ApiController]
[Route("api/referrals")]
[Authorize]
public class ReferralsController : ControllerBase
{
    private readonly RecruitmentDbContext _db;
    private readonly ITenantContext _tenant;

    public ReferralsController(RecruitmentDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    private string Tenant => _tenant.TenantSlug ?? "";
    private bool IsHr => RecruitmentSql.IsHr(User);
    private static readonly Regex EmailRx = new(@"^[^\s@]+@[^\s@]+\.[^\s@]{2,}$", RegexOptions.Compiled);
    private static readonly string[] Relationships = { "FormerColleague", "Friend", "Network", "Other" };
    private static readonly string[] Currencies = { "TRY", "USD", "EUR", "GBP" };

    // ------------------------------------------------------------------ ayarlar

    private static object SettingsView(RecruitmentProgramSettings s) => new
    {
        s.ReferralEnabled, s.ReferralRewardAmount, s.ReferralRewardCurrency, s.ReferralProbationDays, s.ReferralRewardNote,
        s.PublishSalaryInJobPostings,
    };

    [HttpGet("settings")]
    public async Task<IActionResult> GetSettings(CancellationToken ct) =>
        Ok(SettingsView(await RecruitmentW11.SettingsAsync(_db, ct)));

    public record SettingsBody(bool ReferralEnabled, decimal? ReferralRewardAmount, string? ReferralRewardCurrency, int ReferralProbationDays,
        string? ReferralRewardNote, bool PublishSalaryInJobPostings);

    [HttpPut("settings")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> PutSettings([FromBody] SettingsBody b, CancellationToken ct)
    {
        if (b.ReferralRewardAmount is < 0 or > 10_000_000) return BadRequest(new { message = "Ödül tutarı geçersiz" });
        var cur = (b.ReferralRewardCurrency ?? "TRY").ToUpperInvariant();
        if (!Currencies.Contains(cur)) return BadRequest(new { message = "Para birimi TRY, USD, EUR ya da GBP olmalı" });
        if (b.ReferralProbationDays is < 0 or > 365) return BadRequest(new { message = "Deneme süresi 0-365 gün olmalı" });
        if (b.ReferralRewardNote is { Length: > 500 }) return BadRequest(new { message = "Not en fazla 500 karakter olabilir" });
        var s = await _db.ProgramSettings.FirstOrDefaultAsync(ct);
        if (s is null) { s = new RecruitmentProgramSettings(); _db.ProgramSettings.Add(s); }
        s.ReferralEnabled = b.ReferralEnabled;
        s.ReferralRewardAmount = b.ReferralRewardAmount is null ? null : Math.Round(b.ReferralRewardAmount.Value, 2);
        s.ReferralRewardCurrency = cur;
        s.ReferralProbationDays = b.ReferralProbationDays;
        s.ReferralRewardNote = string.IsNullOrWhiteSpace(b.ReferralRewardNote) ? null : b.ReferralRewardNote.Trim();
        s.PublishSalaryInJobPostings = b.PublishSalaryInJobPostings;
        s.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(SettingsView(s));
    }

    // ------------------------------------------------------------------ öneri

    public record ReferralInput(Guid JobPostingId, string? FirstName, string? LastName, string? Email, string? Phone,
        string? Relationship, string? Note, bool CandidateConsent);

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ReferralInput b, CancellationToken ct)
    {
        var me = await _db.MyEmployeeIdAsync(Tenant, RecruitmentSql.UserId(User), ct);
        if (me is null) return StatusCode(403, new { message = "Öneri yapmak için çalışan kaydınız olmalı" });
        var settings = await RecruitmentW11.SettingsAsync(_db, ct);
        if (!settings.ReferralEnabled) return Conflict(new { message = "Çalışan önerisi programı şu anda kapalı" });
        if (!b.CandidateConsent)
            return BadRequest(new { message = "Adayın önerilmeyi ve bilgilerinin İK ile paylaşılmasını kabul ettiğini onaylamalısınız" });

        var first = b.FirstName?.Trim() ?? "";
        var last = b.LastName?.Trim() ?? "";
        var email = b.Email?.Trim() ?? "";
        var phone = string.IsNullOrWhiteSpace(b.Phone) ? null : b.Phone.Trim();
        if (first.Length is < 2 or > 100 || last.Length is < 2 or > 100) return BadRequest(new { message = "Ad ve soyad 2-100 karakter olmalı" });
        if (email.Length > 200 || !EmailRx.IsMatch(email)) return BadRequest(new { message = "Geçerli bir e-posta adresi girin" });
        if (phone is not null && (phone.Length > 30 || DuplicateDetector.NormalizePhone(phone) is null))
            return BadRequest(new { message = "Telefon numarası geçersiz" });
        if (b.Note is { Length: > 1000 }) return BadRequest(new { message = "Not en fazla 1000 karakter olabilir" });
        if (b.Relationship is { Length: > 0 } rel && !Relationships.Contains(rel)) return BadRequest(new { message = "Tanışıklık türü geçersiz" });

        var posting = await _db.JobPostings.FirstOrDefaultAsync(p => p.Id == b.JobPostingId && p.Status == JobPostingStatus.Published, ct);
        if (posting is null) return NotFound(new { message = "İlan bulunamadı ya da başvuruya kapalı" });

        var myEmail = await _db.Database.SqlQueryRaw<string>(
            "SELECT \"Email\" AS \"Value\" FROM employee_employees WHERE \"TenantSlug\" = {0} AND \"Id\" = {1}", Tenant, me.Value).FirstOrDefaultAsync(ct);
        if (myEmail is not null && DuplicateDetector.NormalizeEmail(myEmail) == DuplicateDetector.NormalizeEmail(email))
            return BadRequest(new { message = "Kendinizi öneremezsiniz; ilana kariyer sayfasından başvurabilirsiniz" });

        var ne = DuplicateDetector.NormalizeEmail(email);
        var np = DuplicateDetector.NormalizePhone(phone);
        var pool = await _db.Candidates
            .Where(c => c.AnonymizedAt == null && (c.NormalizedEmail == ne || (np != null && c.NormalizedPhone == np)))
            .Select(c => new DuplicateDetector.Candidate(c.Id, c.FirstName, c.LastName, c.NormalizedEmail, c.NormalizedPhone))
            .ToListAsync(ct);
        var match = DuplicateDetector.FindBest(pool, first, last, email, phone);

        Candidate candidate;
        var owns = false;
        if (match is { Strength: DuplicateDetector.Strength.Strong })
        {
            candidate = await _db.Candidates.FirstAsync(c => c.Id == match.CandidateId, ct);
            // KVKK: önerene mevcut başvurunun ayrıntısı gösterilmez; yalnızca öneri yapılamadığı söylenir.
            if (await _db.Applications.AnyAsync(a => a.JobPostingId == posting.Id && a.CandidateId == candidate.Id, ct))
                return Conflict(new { message = "Bu aday için bu ilana öneri yapılamıyor (aday zaten süreçte olabilir)" });
        }
        else
        {
            candidate = new Candidate
            {
                FirstName = first, LastName = last, Email = email, Phone = phone, Source = "Çalışan önerisi",
            };
            candidate.Normalize();
            _db.Candidates.Add(candidate);
            owns = true;
        }

        var app = new Application
        {
            JobPostingId = posting.Id, CandidateId = candidate.Id, Channel = "Referral", OwnsCandidate = owns,
            PrivacyNoticeVersion = PublicCareerController.NoticeVersion,
            DuplicateReason = owns ? null : "Çalışan önerisi mevcut adaya bağlandı",
        };
        _db.Applications.Add(app);
        // FK ilişkileri EF modelinde tanımlı olmadığından (öneri/bağlantı) önce aday + başvuru yazılır.
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return Conflict(new { message = "Bu aday için bu ilana öneri yapılamıyor (aday zaten süreçte olabilir)" });
        }
        var now = DateTimeOffset.UtcNow;
        var referral = new Referral
        {
            JobPostingId = posting.Id, ReferrerEmployeeId = me.Value, CandidateId = candidate.Id, ApplicationId = app.Id,
            Relationship = string.IsNullOrWhiteSpace(b.Relationship) ? null : b.Relationship,
            Note = string.IsNullOrWhiteSpace(b.Note) ? null : b.Note.Trim(),
            CandidateConsentConfirmed = true, ConsentConfirmedAt = now,
        };
        _db.Referrals.Add(referral);
        var (_, token) = await RecruitmentW11.IssueStatusLinkAsync(_db, app.Id, StatusLinkRules.DefaultDays, RecruitmentSql.UserId(User), ct);
        await _db.SaveChangesAsync(ct);

        // KVKK m.10: adaya aydınlatma. Öneren kişinin adı yazılmaz (veri en aza indirme). Durum bağlantısı
        // kişisel veri göstermez; iptal edilebilir ve süresi dolar.
        var company = (await _db.TenantAsync(Tenant, ct))?.Name ?? Tenant;
        var url = RecruitmentW11.PublicOrigin(HttpContext) + RecruitmentW11.StatusPath(Tenant, token);
        await _db.EmailCandidateAsync(Tenant, email, $"Kişisel verileriniz hakkında bilgilendirme — {company}",
            $"Merhaba {first}, {company} çalışanlarından biri sizi \"{posting.Title}\" pozisyonu için önerdi ve önerilmeyi kabul ettiğinizi beyan etti.\n\n"
            + "6698 sayılı KVKK m.10 uyarınca bilgilendirme: ad-soyad, e-posta ve (varsa) telefon bilgileriniz ile öneri notu, yalnızca bu pozisyon için "
            + "işe alım sürecinin yürütülmesi amacıyla, sözleşmenin kurulması (m.5/2-c) ve meşru menfaat (m.5/2-f) hukuki sebeplerine dayanılarak işlenir; "
            + $"yurt dışına aktarılmaz. Süreç sonuçlandıktan {RetentionService.RetentionDays} gün sonra silinir; açık rızanız olmadan aday havuzunda saklanmaz.\n\n"
            + "Önerilmeyi kabul etmediyseniz ya da verilerinizin silinmesini istiyorsanız bu e-postayı yanıtlayarak ya da İnsan Kaynakları ile iletişime geçerek bildirebilirsiniz (KVKK m.11).\n\n"
            + $"Başvurunuzun genel durumunu şu bağlantıdan izleyebilirsiniz (kişisel veri göstermez, {StatusLinkRules.DefaultDays} gün geçerlidir): {url}",
            "recruitment.referral.notice", ct);
        referral.NoticeSentAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        return Ok(new { referral.Id, status = "InReview", statusLabel = "Değerlendiriliyor", linkedToExisting = !owns });
    }

    /// <summary>Önerenin kendi önerileri: yalnızca kaba durum ve ödül durumu.</summary>
    [HttpGet("mine")]
    public async Task<IActionResult> Mine(CancellationToken ct)
    {
        var me = await _db.MyEmployeeIdAsync(Tenant, RecruitmentSql.UserId(User), ct);
        if (me is null) return Ok(new { items = Array.Empty<object>(), settings = SettingsView(await RecruitmentW11.SettingsAsync(_db, ct)) });
        var rows = await _db.Referrals.AsNoTracking().Where(r => r.ReferrerEmployeeId == me)
            .OrderByDescending(r => r.CreatedAt).Take(200).ToListAsync(ct);
        var appIds = rows.Where(r => r.ApplicationId != null).Select(r => r.ApplicationId!.Value).ToList();
        var apps = await _db.Applications.AsNoTracking().Include(a => a.Candidate).Include(a => a.JobPosting)
            .Where(a => appIds.Contains(a.Id)).ToListAsync(ct);
        var postingIds = rows.Select(r => r.JobPostingId).Distinct().ToList();
        var postings = await _db.JobPostings.AsNoTracking().Where(p => postingIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Title, p.Status }).ToListAsync(ct);
        return Ok(new
        {
            settings = SettingsView(await RecruitmentW11.SettingsAsync(_db, ct)),
            items = rows.Select(r =>
            {
                var a = apps.FirstOrDefault(x => x.Id == r.ApplicationId);
                var p = postings.FirstOrDefault(x => x.Id == r.JobPostingId);
                var (code, label) = ReferralRules.CoarseStatus(a?.Status, p is null || p.Status == JobPostingStatus.Closed);
                return new
                {
                    r.Id, r.JobPostingId, posting = p?.Title,
                    // Öneren adı zaten biliyor; aday silinmiş/anonimleşmişse gösterilmez.
                    candidateName = a?.Candidate is { AnonymizedAt: null } c ? $"{c.FirstName} {c.LastName}" : null,
                    status = code, statusLabel = label,
                    rewardStatus = r.RewardStatus.ToString(), rewardLabel = ReferralRules.RewardLabel(r.RewardStatus),
                    r.RewardEligibleAt, r.CreatedAt,
                };
            }),
        });
    }

    // ------------------------------------------------------------------ İK panosu

    [HttpGet]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> List([FromQuery] string? rewardStatus, CancellationToken ct)
    {
        var q = _db.Referrals.AsNoTracking().AsQueryable();
        if (Enum.TryParse<ReferralRewardStatus>(rewardStatus, out var rs)) q = q.Where(r => r.RewardStatus == rs);
        var rows = await q.OrderByDescending(r => r.CreatedAt).Take(500).ToListAsync(ct);
        var all = await _db.Referrals.AsNoTracking().Select(r => new { r.ReferrerEmployeeId, r.ApplicationId, r.RewardStatus, r.RewardAmount }).ToListAsync(ct);
        var appIds = all.Where(r => r.ApplicationId != null).Select(r => r.ApplicationId!.Value).ToList();
        var apps = await _db.Applications.AsNoTracking().Include(a => a.Candidate).Where(a => appIds.Contains(a.Id))
            .Select(a => new { a.Id, a.Status, a.JobPostingId, Name = a.Candidate!.AnonymizedAt == null ? a.Candidate.FirstName + " " + a.Candidate.LastName : null })
            .ToListAsync(ct);
        var postingIds = rows.Select(r => r.JobPostingId).Distinct().ToList();
        var postings = await _db.JobPostings.AsNoTracking().Where(p => postingIds.Contains(p.Id)).Select(p => new { p.Id, p.Title, p.Status }).ToListAsync(ct);
        var people = (await _db.PeopleAsync(Tenant, rows.Select(r => r.ReferrerEmployeeId).Distinct().ToList(), ct))
            .ToDictionary(p => p.Id, p => $"{p.FirstName} {p.LastName}");

        var hired = all.Count(r => apps.FirstOrDefault(a => a.Id == r.ApplicationId)?.Status == ApplicationStatus.Hired);
        await _db.AuditAsync(HttpContext, Tenant, "Referral", "list", "Viewed", new { count = rows.Count }, ct);
        return Ok(new
        {
            summary = new
            {
                total = all.Count,
                inReview = all.Count(r => apps.FirstOrDefault(a => a.Id == r.ApplicationId) is { } a && !PipelineRules.IsTerminal(a.Status)),
                hired,
                hireRate = all.Count == 0 ? (double?)null : Math.Round(100.0 * hired / all.Count, 1),
                awaitingApproval = all.Count(r => r.RewardStatus == ReferralRewardStatus.Eligible),
                waiting = all.Count(r => r.RewardStatus == ReferralRewardStatus.Waiting),
                paidTotal = all.Where(r => r.RewardStatus == ReferralRewardStatus.Paid).Sum(r => r.RewardAmount ?? 0),
                referrers = all.Select(r => r.ReferrerEmployeeId).Distinct().Count(),
            },
            items = rows.Select(r =>
            {
                var a = apps.FirstOrDefault(x => x.Id == r.ApplicationId);
                var p = postings.FirstOrDefault(x => x.Id == r.JobPostingId);
                var (code, label) = ReferralRules.CoarseStatus(a?.Status, p is null || p.Status == JobPostingStatus.Closed);
                return new
                {
                    r.Id, r.JobPostingId, posting = p?.Title, r.ApplicationId, candidateName = a?.Name,
                    applicationStatus = a?.Status.ToString(), status = code, statusLabel = label,
                    r.ReferrerEmployeeId, referrer = people.GetValueOrDefault(r.ReferrerEmployeeId),
                    r.Relationship, r.Note, r.CandidateConsentConfirmed, r.ConsentConfirmedAt, r.NoticeSentAt,
                    rewardStatus = r.RewardStatus.ToString(), rewardLabel = ReferralRules.RewardLabel(r.RewardStatus),
                    r.HiredAt, r.RewardEligibleAt, r.RewardAmount, r.RewardCurrency, r.RewardDecidedAt, r.RewardNote, r.CreatedAt,
                };
            }),
        });
    }

    public record RewardBody(string? Action, decimal? Amount, string? Note);

    /// <summary>İK ödül kararı: approve | pay | forfeit | not-eligible (insan onayı; denetlenir).</summary>
    [HttpPost("{id:guid}/reward")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Reward(Guid id, [FromBody] RewardBody b, CancellationToken ct)
    {
        var r = await _db.Referrals.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return NotFound(new { message = "Öneri bulunamadı" });
        var (next, error) = ReferralRules.Decide(r.RewardStatus, b.Action);
        if (error is not null) return Conflict(new { message = error });
        if (b.Amount is < 0 or > 10_000_000) return BadRequest(new { message = "Ödül tutarı geçersiz" });
        if (b.Note is { Length: > 500 }) return BadRequest(new { message = "Not en fazla 500 karakter olabilir" });
        var from = r.RewardStatus;
        r.RewardStatus = next!.Value;
        if (b.Amount is { } amt) r.RewardAmount = Math.Round(amt, 2);
        r.RewardCurrency ??= (await RecruitmentW11.SettingsAsync(_db, ct)).ReferralRewardCurrency;
        r.RewardNote = string.IsNullOrWhiteSpace(b.Note) ? r.RewardNote : b.Note.Trim();
        r.RewardDecidedAt = DateTimeOffset.UtcNow;
        r.RewardDecidedByUserId = RecruitmentSql.UserId(User);
        await _db.SaveChangesAsync(ct);
        await _db.AuditAsync(HttpContext, Tenant, "Referral", r.Id.ToString(), "RewardDecided", new { from = from.ToString(), to = r.RewardStatus.ToString() }, ct);
        if (r.RewardStatus is ReferralRewardStatus.Approved or ReferralRewardStatus.Paid)
            await _db.NotifyAsync(Tenant, r.ReferrerEmployeeId, "Öneri ödülü",
                r.RewardStatus == ReferralRewardStatus.Paid ? "Çalışan önerinizin ödülü ödendi olarak işaretlendi." : "Çalışan önerinizin ödülü onaylandı.",
                "recruitment.referral.reward", ct);
        return Ok(new { r.Id, rewardStatus = r.RewardStatus.ToString(), rewardLabel = ReferralRules.RewardLabel(r.RewardStatus), r.RewardAmount, r.RewardCurrency });
    }

    /// <summary>Ödül hak ediş turunu bu kiracı için hemen çalıştırır (işçi 6 saatte bir çalışır).</summary>
    [HttpPost("evaluate")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Evaluate(CancellationToken ct) =>
        Ok(new { changed = await ReferralRewardEvaluator.RunAsync(_db, Tenant, ct) });
}

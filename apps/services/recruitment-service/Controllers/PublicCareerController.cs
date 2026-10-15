using System.Security.Cryptography;
using System.Text;
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
/// Y16: herkese açık kariyer sayfası ve aday öz-hizmeti (oturum gerekmez).
///
/// KVKK tasarımı:
///  * Aydınlatma metni BİLGİLENDİRMEDİR (onay kutusu yok); aday havuzunda saklama için AYRI ve
///    isteğe bağlı açık rıza alınır (m.5/1). Rıza yoksa veri, başvuru kapandıktan
///    RECRUITMENT_RETENTION_DAYS gün sonra; rıza varsa RECRUITMENT_POOL_MONTHS ay sonra imha edilir.
///  * Aday, başvuru sonrası bir kez gösterilen kişisel bağlantıyla durumunu görür ve verisinin
///    silinmesini ister (m.11). Bağlantının yalnızca SHA-256 özeti saklanır.
///  * POST uçları IP başına bellekte sınırlandırılır; IP adresi veritabanına (denetim dahil) yazılmaz.
///  * Mevcut adaya bağlanan başvurularda (e-posta/telefon eşleşmesi) bağlantı yalnızca o başvurunun
///    verisini gösterir/siler: başkasının e-postasını bilen biri mevcut kaydı göremez/silemez.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/public/{tenantSlug}")]
public class PublicCareerController : ControllerBase
{
    public const string NoticeVersion = "2026-10-v1";

    private readonly RecruitmentDbContext _db;
    private readonly TenantContext _tenant;
    private readonly PublicRateLimiter _limiter;

    public PublicCareerController(RecruitmentDbContext db, TenantContext tenant, PublicRateLimiter limiter)
    {
        _db = db;
        _tenant = tenant;
        _limiter = limiter;
    }

    private static readonly Regex SlugRx = new("^[a-z0-9][a-z0-9-]{1,63}$", RegexOptions.Compiled);
    private static readonly Regex EmailRx = new(@"^[^\s@]+@[^\s@]+\.[^\s@]{2,}$", RegexOptions.Compiled);

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string PrivacyNotice(string company, int days, int poolMonths) => $"""
        {company} ("Şirket") olarak, 6698 sayılı Kişisel Verilerin Korunması Kanunu ("KVKK") m.10 uyarınca veri sorumlusu sıfatıyla sizi bilgilendiririz.

        İşlenen veriler: ad-soyad, e-posta, (isteğe bağlı) telefon, ön yazı ve özgeçmiş metni ile işe alım sürecindeki değerlendirme kayıtları.
        Amaç: başvurduğunuz pozisyon için işe alım sürecinin yürütülmesi, sizinle iletişim kurulması ve değerlendirilmeniz.
        Hukuki sebep: bir sözleşmenin kurulmasıyla doğrudan ilgili olması (m.5/2-c) ve Şirketin meşru menfaati (m.5/2-f).
        Aktarım: verileriniz yurt dışına aktarılmaz; yalnızca işe alım sürecinde görevli Şirket çalışanlarınca görülür.
        Saklama süresi: başvurunuz sonuçlandıktan (ya da ilan kapandıktan) {days} gün sonra verileriniz silinir/anonimleştirilir.
        Aday havuzu: yalnızca ayrıca AÇIK RIZA verirseniz verileriniz başka pozisyonlarda değerlendirilmek üzere {poolMonths} ay saklanır; rıza vermemeniz başvurunuzu etkilemez ve rızanızı dilediğiniz an geri alabilirsiniz.
        Haklarınız (m.11): verilerinizin işlenip işlenmediğini öğrenme, bilgi talep etme, düzeltilmesini ve silinmesini isteme. Başvuru sonrası size gösterilen kişisel bağlantıdan başvurunuzu görüntüleyebilir ve verilerinizin silinmesini isteyebilirsiniz.
        Lütfen sağlık, din, siyasi görüş, sendika üyeliği gibi özel nitelikli kişisel verilerinizi başvurunuzda paylaşmayınız.
        """;

    /// <summary>Kiracıyı yoldan çözer (tenant filtresi bu kiracıya kilitlenir). Bulunamazsa ya da etkin değilse null.</summary>
    private async Task<RecruitmentSql.TenantRow?> ResolveAsync(string slug, CancellationToken ct)
    {
        if (!SlugRx.IsMatch(slug)) return null;
        _tenant.TenantSlug = slug;
        _tenant.IsPlatformAdmin = false;
        var t = await _db.TenantAsync(slug, ct);
        return t is not null && string.Equals(t.Status, "Active", StringComparison.OrdinalIgnoreCase) ? t : null;
    }

    private IActionResult NotFoundPage() => NotFound(new { message = "Kariyer sayfası bulunamadı" });

    /// <summary>Sınır kontrolü; IP yalnızca bellekte anahtar olur ve denetim kaydına geçmesin diye başlıklardan çıkarılır.</summary>
    private bool RateLimited()
    {
        var key = PublicRateLimiter.ClientKey(HttpContext);
        Request.Headers.Remove("X-Real-IP");
        Request.Headers.Remove("X-Forwarded-For");
        return !_limiter.TryAcquire(key);
    }

    private IActionResult TooMany() => StatusCode(StatusCodes.Status429TooManyRequests,
        new { message = "Çok fazla istek gönderildi. Lütfen birkaç dakika sonra tekrar deneyin." });

    public record PublicJob(Guid Id, string Title, string? Description, EmploymentType EmploymentType, string? Department, DateTimeOffset? PublishedAt);

    [HttpGet("jobs")]
    public async Task<IActionResult> Jobs(string tenantSlug, CancellationToken ct)
    {
        var t = await ResolveAsync(tenantSlug, ct);
        if (t is null) return NotFoundPage();
        var depts = await _db.DepartmentNamesAsync(tenantSlug, ct);
        var jobs = await _db.JobPostings.AsNoTracking()
            .Where(p => p.Status == JobPostingStatus.Published)
            .OrderByDescending(p => p.PublishedAt)
            .Select(p => new { p.Id, p.Title, p.Description, p.EmploymentType, p.DepartmentId, p.PublishedAt })
            .ToListAsync(ct);
        return Ok(new
        {
            company = t.Name,
            privacyNotice = new { version = NoticeVersion, text = PrivacyNotice(t.Name, RetentionService.RetentionDays, RetentionService.PoolMonths) },
            retentionDays = RetentionService.RetentionDays,
            poolMonths = RetentionService.PoolMonths,
            jobs = jobs.Select(j => new PublicJob(j.Id, j.Title, j.Description, j.EmploymentType, depts.GetValueOrDefault(j.DepartmentId), j.PublishedAt)),
        });
    }

    /// <summary>
    /// Bot koruması (güvenlik dalgası 2B): başvuru formu açılırken alınan imzalı zaman jetonu. Gönderim en
    /// erken 3 sn, en geç 2 saat sonra ve bir kez kabul edilir; "Website" görünmez tuzak alanıyla birlikte.
    /// </summary>
    [HttpGet("form-token")]
    public async Task<IActionResult> FormToken(string tenantSlug, CancellationToken ct)
    {
        var t = await ResolveAsync(tenantSlug, ct);
        if (t is null) return NotFoundPage();
        return Ok(new { token = FormGuard.Shared.Issue("career:" + tenantSlug), minSeconds = (int)FormGuard.MinAge.TotalSeconds });
    }

    public record ApplyRequest(
        string? FirstName, string? LastName, string? Email, string? Phone, string? CoverNote, string? ResumeText,
        bool TalentPoolConsent, string? PrivacyNoticeVersion, string? Website, string? FormToken = null);

    [HttpPost("jobs/{id:guid}/apply")]
    public async Task<IActionResult> Apply(string tenantSlug, Guid id, [FromBody] ApplyRequest req, CancellationToken ct)
    {
        if (RateLimited()) return TooMany();
        var t = await ResolveAsync(tenantSlug, ct);
        if (t is null) return NotFoundPage();

        var first = req.FirstName?.Trim() ?? "";
        var last = req.LastName?.Trim() ?? "";
        var email = req.Email?.Trim() ?? "";
        var phone = string.IsNullOrWhiteSpace(req.Phone) ? null : req.Phone.Trim();
        if (first.Length is < 2 or > 100 || last.Length is < 2 or > 100)
            return BadRequest(new { message = "Ad ve soyad 2-100 karakter olmalı" });
        if (email.Length > 200 || !EmailRx.IsMatch(email)) return BadRequest(new { message = "Geçerli bir e-posta adresi girin" });
        if (phone is not null && (phone.Length > 30 || DuplicateDetector.NormalizePhone(phone) is null))
            return BadRequest(new { message = "Telefon numarası geçersiz" });
        if (req.CoverNote is { Length: > 3000 }) return BadRequest(new { message = "Ön yazı en fazla 3000 karakter olabilir" });
        if (req.ResumeText is { Length: > 20000 }) return BadRequest(new { message = "Özgeçmiş metni en fazla 20.000 karakter olabilir" });

        var posting = await _db.JobPostings.FirstOrDefaultAsync(p => p.Id == id && p.Status == JobPostingStatus.Published, ct);
        if (posting is null) return NotFound(new { message = "İlan bulunamadı ya da başvuruya kapalı" });

        // Bot tuzağı: görünmez alan doldurulduysa kayıt yapılmadan başarı döner.
        if (!string.IsNullOrEmpty(req.Website)) return Ok(new { received = true });
        var guard = FormGuard.Shared.Verify(req.FormToken, "career:" + tenantSlug);
        if (guard != FormTokenStatus.Ok)
            return BadRequest(new { message = FormGuard.Message(guard), code = guard == FormTokenStatus.TooFast ? "form_too_fast" : "form_token" });

        var ne = DuplicateDetector.NormalizeEmail(email);
        var np = DuplicateDetector.NormalizePhone(phone);
        var pool = await _db.Candidates
            .Where(c => c.AnonymizedAt == null && (c.NormalizedEmail == ne || (np != null && c.NormalizedPhone == np)))
            .Select(c => new DuplicateDetector.Candidate(c.Id, c.FirstName, c.LastName, c.NormalizedEmail, c.NormalizedPhone))
            .ToListAsync(ct);
        var match = DuplicateDetector.FindBest(pool, first, last, email, phone);

        Candidate candidate;
        var owns = false;
        string? duplicateReason = null;
        if (match is { Strength: DuplicateDetector.Strength.Strong })
        {
            candidate = await _db.Candidates.FirstAsync(c => c.Id == match.CandidateId, ct);
            duplicateReason = $"Mevcut adaya bağlandı: {DuplicateDetector.Describe(match.Reason)}";
            if (await _db.Applications.AnyAsync(a => a.JobPostingId == id && a.CandidateId == candidate.Id, ct))
                return Conflict(new { message = "Bu ilana daha önce başvurdunuz" });
        }
        else
        {
            if (match is { Strength: DuplicateDetector.Strength.Possible })
                duplicateReason = $"Olası tekrar: {DuplicateDetector.Describe(match.Reason)}";
            candidate = new Candidate
            {
                FirstName = first, LastName = last, Email = email, Phone = phone, Source = "Kariyer sayfası",
                ResumeText = string.IsNullOrWhiteSpace(req.ResumeText) ? null : req.ResumeText.Trim(),
                TalentPoolConsent = req.TalentPoolConsent,
                TalentPoolConsentAt = req.TalentPoolConsent ? DateTimeOffset.UtcNow : null,
            };
            candidate.Normalize();
            _db.Candidates.Add(candidate);
            owns = true;
        }

        var token = NewToken();
        var app = new Application
        {
            JobPostingId = id,
            CandidateId = candidate.Id,
            Channel = "Career",
            CoverNote = string.IsNullOrWhiteSpace(req.CoverNote) ? null : req.CoverNote.Trim(),
            SelfServiceTokenHash = Hash(token),
            OwnsCandidate = owns,
            PrivacyNoticeVersion = string.IsNullOrWhiteSpace(req.PrivacyNoticeVersion) ? NoticeVersion : req.PrivacyNoticeVersion[..Math.Min(40, req.PrivacyNoticeVersion.Length)],
            DuplicateReason = duplicateReason,
        };
        _db.Applications.Add(app);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return Conflict(new { message = "Bu e-posta adresiyle bir başvuru zaten var" });
        }

        // Onay e-postası: bağlantı (jeton) e-postaya YAZILMAZ — bildirim kayıtlarında düz metin kalmasın.
        await _db.EmailCandidateAsync(tenantSlug, email, "Başvurunuz alındı",
            $"Merhaba {first}, {t.Name} bünyesindeki \"{posting.Title}\" ilanına başvurunuz alındı. Başvurunuzun durumunu ve "
            + "kişisel verilerinizi, başvuru sonrasında ekranda gösterilen kişisel bağlantıdan görüntüleyebilir ve silinmesini isteyebilirsiniz.",
            "recruitment.applied", ct);

        return Ok(new
        {
            applicationId = app.Id,
            token,
            selfServicePath = $"/kariyer/{tenantSlug}/basvuru/{token}",
            linkedToExisting = !owns,
            talentPoolConsent = owns ? req.TalentPoolConsent : candidate.TalentPoolConsent,
            message = owns || !req.TalentPoolConsent
                ? "Başvurunuz alındı"
                : "Başvurunuz alındı. Daha önceki kaydınızla eşleştiği için aday havuzu tercihiniz değiştirilmedi; bunun için İK ile iletişime geçebilirsiniz.",
        });
    }

    private async Task<Application?> ByTokenAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length is < 20 or > 100) return null;
        var hash = Hash(token);
        return await _db.Applications.Include(a => a.Candidate).Include(a => a.JobPosting)
            .FirstOrDefaultAsync(a => a.SelfServiceTokenHash == hash, ct);
    }

    private static string Mask(string? s, bool email = false)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (email && s.IndexOf('@') is var at and > 0) return s[0] + new string('*', Math.Max(2, at - 1)) + s[at..];
        return s.Length <= 4 ? "****" : new string('*', s.Length - 4) + s[^4..];
    }

    private static (string Code, string Label) PublicStatus(Application a) => a.Status switch
    {
        ApplicationStatus.Applied when a.JobPosting?.Status == JobPostingStatus.Closed => ("Closed", "İlan kapandı"),
        ApplicationStatus.Applied => ("Received", "Başvurunuz alındı"),
        ApplicationStatus.Screening or ApplicationStatus.Interview when a.JobPosting?.Status == JobPostingStatus.Closed => ("Closed", "İlan kapandı"),
        ApplicationStatus.Screening or ApplicationStatus.Interview => ("InReview", "Değerlendiriliyor"),
        ApplicationStatus.Offer => ("Offer", "Teklif aşamasında"),
        ApplicationStatus.Hired => ("Positive", "Olumlu sonuçlandı"),
        ApplicationStatus.Rejected => ("Negative", "Olumsuz sonuçlandı"),
        ApplicationStatus.Withdrawn => ("Withdrawn", "Geri çekildi"),
        _ => ("InReview", "Değerlendiriliyor"),
    };

    [HttpGet("self-service/{token}")]
    public async Task<IActionResult> SelfService(string tenantSlug, string token, CancellationToken ct)
    {
        // Dalga 11 (74): GET de sınırlanır (jeton tahmini denemelerine karşı; ayrı ve daha geniş sayaç).
        var getLimiter = HttpContext.RequestServices.GetRequiredService<StatusRateLimiter>();
        if (!getLimiter.TryAcquire(PublicRateLimiter.ClientKey(HttpContext))) return TooMany();
        var t = await ResolveAsync(tenantSlug, ct);
        if (t is null) return NotFoundPage();
        var a = await ByTokenAsync(token, ct);
        if (a?.Candidate is null) return NotFound(new { message = "Bağlantı geçersiz ya da verileriniz silinmiş" });
        var c = a.Candidate;
        var (code, label) = PublicStatus(a);
        var now = DateTimeOffset.UtcNow;
        var interviews = await _db.Interviews.AsNoTracking()
            .Where(i => i.ApplicationId == a.Id && i.Result == InterviewResult.Pending && i.ScheduledAt > now)
            .OrderBy(i => i.ScheduledAt)
            .Select(i => new { i.ScheduledAt, i.DurationMinutes, i.Location, i.MeetingUrl, i.Type })
            .ToListAsync(ct);
        var offer = await _db.Offers.AsNoTracking()
            .Where(o => o.ApplicationId == a.Id && (o.Status == OfferStatus.Sent || o.Status == OfferStatus.Accepted || o.Status == OfferStatus.Declined))
            .OrderByDescending(o => o.CreatedAt).FirstOrDefaultAsync(ct);
        await _db.AuditAsync(HttpContext, tenantSlug, "Application", a.Id.ToString(), "SelfServiceViewed", new { channel = "candidate-link" }, ct);
        return Ok(new
        {
            company = t.Name,
            posting = a.JobPosting?.Title,
            status = code,
            statusLabel = label,
            nextStep = StatusLinkRules.View(a.Status, a.JobPosting?.Status == JobPostingStatus.Closed,
                interviews.Count > 0 ? interviews[0].ScheduledAt : null, offer?.Status).NextStep,
            appliedAt = a.AppliedAt,
            ownsCandidate = a.OwnsCandidate,
            data = new
            {
                firstName = c.FirstName,
                lastName = a.OwnsCandidate ? c.LastName : Mask(c.LastName),
                email = a.OwnsCandidate ? c.Email : Mask(c.Email, email: true),
                phone = a.OwnsCandidate ? c.Phone : (c.Phone is null ? null : Mask(c.Phone)),
                coverNote = a.CoverNote,
                resumeText = a.OwnsCandidate ? c.ResumeText : null,
            },
            talentPoolConsent = c.TalentPoolConsent,
            retention = c.TalentPoolConsent
                ? $"Açık rızanız ile verileriniz aday havuzunda {RetentionService.PoolMonths} ay saklanır, ardından silinir."
                : $"Başvurunuz sonuçlandıktan (ya da ilan kapandıktan) {RetentionService.RetentionDays} gün sonra verileriniz silinir.",
            interviews,
            offer = offer is null ? null : new
            {
                offer.Id, status = offer.Status.ToString(), offer.PositionTitle, offer.StartDate, offer.ExpiresAt,
                letterText = offer.SalaryLetterText,
            },
            privacyNotice = new { version = a.PrivacyNoticeVersion ?? NoticeVersion, text = PrivacyNotice(t.Name, RetentionService.RetentionDays, RetentionService.PoolMonths) },
        });
    }

    public record ConsentRequest(bool TalentPool);

    /// <summary>Aday havuzu rızası: geri alma her zaman serbest; vermek yalnızca kaydın sahibi olan başvuruda.</summary>
    [HttpPost("self-service/{token}/consent")]
    public async Task<IActionResult> Consent(string tenantSlug, string token, [FromBody] ConsentRequest req, CancellationToken ct)
    {
        if (RateLimited()) return TooMany();
        if (await ResolveAsync(tenantSlug, ct) is null) return NotFoundPage();
        var a = await ByTokenAsync(token, ct);
        if (a?.Candidate is null) return NotFound(new { message = "Bağlantı geçersiz ya da verileriniz silinmiş" });
        if (req.TalentPool && !a.OwnsCandidate)
            return StatusCode(403, new { message = "Bu başvuru önceki bir kaydınıza bağlandığı için havuz rızası buradan verilemez; İK ile iletişime geçin." });
        a.Candidate.TalentPoolConsent = req.TalentPool;
        a.Candidate.TalentPoolConsentAt = req.TalentPool ? DateTimeOffset.UtcNow : null;
        await _db.SaveChangesAsync(ct);
        return Ok(new { talentPoolConsent = req.TalentPool });
    }

    public record RespondRequest(bool Accept);

    /// <summary>Adaya gönderilmiş teklifi kabul/ret.</summary>
    [HttpPost("self-service/{token}/offer/respond")]
    public async Task<IActionResult> RespondOffer(string tenantSlug, string token, [FromBody] RespondRequest req, CancellationToken ct)
    {
        if (RateLimited()) return TooMany();
        if (await ResolveAsync(tenantSlug, ct) is null) return NotFoundPage();
        var a = await ByTokenAsync(token, ct);
        if (a is null) return NotFound(new { message = "Bağlantı geçersiz ya da verileriniz silinmiş" });
        var offer = await _db.Offers.Where(o => o.ApplicationId == a.Id && o.Status == OfferStatus.Sent)
            .OrderByDescending(o => o.CreatedAt).FirstOrDefaultAsync(ct);
        if (offer is null) return NotFound(new { message = "Yanıt bekleyen bir teklif yok" });
        var (error, code) = await OffersController.RespondCoreAsync(_db, offer, a, req.Accept, ct);
        if (error is not null) return StatusCode(code, new { message = error });
        await _db.SaveChangesAsync(ct);
        return Ok(new { status = offer.Status.ToString() });
    }

    /// <summary>
    /// KVKK m.11 silme talebi. Kaydın sahibi olan başvuruda aday kaydı (tüm başvuruları, mülakat,
    /// puan kartı ve teklifleriyle) silinir; mevcut kayda bağlanmış başvuruda yalnızca bu başvuru silinir.
    /// İşe alınmış (çalışan olmuş) adayın kaydı silinmez: veriler artık çalışan kaydı kapsamındadır.
    /// Toplu silme kullanılır: EF ile silinseydi denetim kaydı silinen kişisel veriyi saklardı.
    /// </summary>
    [HttpDelete("self-service/{token}")]
    public async Task<IActionResult> Delete(string tenantSlug, string token, CancellationToken ct)
    {
        if (RateLimited()) return TooMany();
        if (await ResolveAsync(tenantSlug, ct) is null) return NotFoundPage();
        var a = await ByTokenAsync(token, ct);
        if (a is null) return NotFound(new { message = "Bağlantı geçersiz ya da verileriniz zaten silinmiş" });
        var candidateId = a.CandidateId;
        var scopeApps = a.OwnsCandidate
            ? await _db.Applications.Where(x => x.CandidateId == candidateId).Select(x => new { x.Id, x.Status }).ToListAsync(ct)
            : new[] { new { a.Id, a.Status } }.ToList();
        var ids = scopeApps.Select(x => x.Id).ToList();
        if (scopeApps.Any(x => x.Status == ApplicationStatus.Hired)
            || await _db.Offers.AnyAsync(o => ids.Contains(o.ApplicationId) && o.Status == OfferStatus.Accepted, ct))
            return Conflict(new { message = "İşe alım süreciniz tamamlandığı için verileriniz artık çalışan kaydı kapsamında işleniyor. Talebiniz için İK ile iletişime geçin." });

        int affected;
        if (a.OwnsCandidate)
            await _db.EnqueueResumeDeletionAsync(tenantSlug, candidateId, "RecruitmentCandidates", ct);
        if (a.OwnsCandidate)
            await _db.Database.ExecuteSqlRawAsync(RetentionService.ReferralScrubSql, new object[] { candidateId }, ct); // öneri notu FK "SET NULL" ile kalmasın
        if (a.OwnsCandidate)
            affected = await _db.Candidates.Where(c => c.Id == candidateId).ExecuteDeleteAsync(ct); // FK: başvurular, mülakatlar, puan kartları, teklifler
        else
            affected = await _db.Applications.Where(x => x.Id == a.Id).ExecuteDeleteAsync(ct);

        await _db.AuditAsync(HttpContext, tenantSlug, a.OwnsCandidate ? "Candidate" : "Application",
            a.OwnsCandidate ? candidateId.ToString() : a.Id.ToString(), "Deleted",
            new { reason = "KVKK m.11 — aday öz-hizmet silme talebi", applications = ids.Count }, ct);
        await _db.DestructionLogAsync(tenantSlug, affected, 0, "DataSubjectRequest", "candidate", ct, action: "Delete");
        return Ok(new { deleted = true, scope = a.OwnsCandidate ? "candidate" : "application" });
    }
}

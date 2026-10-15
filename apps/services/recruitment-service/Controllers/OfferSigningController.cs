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
/// Dalga 11 (madde 76): adayın iş teklifini oturumsuz, jetonlu bağlantıdan basit elektronik imzayla
/// (e-postaya tek kullanımlık kod) kabul etmesi. Kod/imza/kanıt governance TEK imza motorundadır
/// (DocumentType "OfferLetter", SignerKind "Candidate"); burada imzalı belge (HTML) ve özetleri saklanır.
///
/// Jeton: teklife özel imza jetonu (İK gönderirken/yenilerken üretilir, yalnızca SHA-256 özeti saklanır,
/// İK iptal edebilir) ya da kariyer başvurusunun öz-hizmet jetonu (aynı başvurunun en son teklifi).
/// KVKK: IP adresi saklanmaz/gönderilmez; POST uçları IP başına bellekte sınırlanır (gateway hr360_public bölgesi de).
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/public/{tenantSlug}/offer-sign/{token}")]
public class OfferSigningController : ControllerBase
{
    private readonly RecruitmentDbContext _db;
    private readonly TenantContext _tenant;
    private readonly PublicRateLimiter _limiter;
    private readonly GovernanceSignatureClient _gov;

    public OfferSigningController(RecruitmentDbContext db, TenantContext tenant, PublicRateLimiter limiter, GovernanceSignatureClient gov)
    {
        _db = db;
        _tenant = tenant;
        _limiter = limiter;
        _gov = gov;
    }

    private static readonly Regex SlugRx = new("^[a-z0-9][a-z0-9-]{1,63}$", RegexOptions.Compiled);

    private string? Lang => Request.Headers["X-HR360-Lang"].FirstOrDefault() is { Length: > 0 } l ? l : null;

    private async Task<RecruitmentSql.TenantRow?> ResolveAsync(string slug, CancellationToken ct)
    {
        if (!SlugRx.IsMatch(slug)) return null;
        _tenant.TenantSlug = slug;
        _tenant.IsPlatformAdmin = false;
        var t = await _db.TenantAsync(slug, ct);
        return t is not null && string.Equals(t.Status, "Active", StringComparison.OrdinalIgnoreCase) ? t : null;
    }

    private bool RateLimited()
    {
        var key = PublicRateLimiter.ClientKey(HttpContext);
        Request.Headers.Remove("X-Real-IP");
        Request.Headers.Remove("X-Forwarded-For");
        return !_limiter.TryAcquire(key);
    }

    private IActionResult TooMany() => StatusCode(StatusCodes.Status429TooManyRequests,
        new { message = "Çok fazla istek gönderildi. Lütfen birkaç dakika sonra tekrar deneyin." });

    private IActionResult Invalid() => NotFound(new { message = "Bağlantı geçersiz, iptal edilmiş ya da süresi dolmuş" });

    /// <summary>Jetonu teklife çözer: önce teklif imza jetonu, sonra öz-hizmet jetonu (o başvurunun en son gönderilmiş teklifi).</summary>
    private async Task<(Offer? Offer, Application? App)> ByTokenAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length is < 20 or > 100) return (null, null);
        var hash = PublicCareerController.Hash(token);
        var offer = await _db.Offers.FirstOrDefaultAsync(o => o.SignTokenHash == hash, ct);
        if (offer is null)
        {
            var appId = await _db.Applications.Where(a => a.SelfServiceTokenHash == hash).Select(a => (Guid?)a.Id).FirstOrDefaultAsync(ct);
            if (appId is null) return (null, null);
            offer = await _db.Offers.Where(o => o.ApplicationId == appId
                    && (o.Status == OfferStatus.Sent || o.Status == OfferStatus.Accepted || o.Status == OfferStatus.Declined || o.Status == OfferStatus.Expired))
                .OrderByDescending(o => o.CreatedAt).FirstOrDefaultAsync(ct);
            if (offer is null) return (null, null);
        }
        var app = await _db.Applications.Include(a => a.Candidate).FirstOrDefaultAsync(a => a.Id == offer.ApplicationId, ct);
        if (app?.Candidate is null || app.Candidate.AnonymizedAt is not null) return (null, null);
        // Henüz gönderilmemiş/geri çekilmiş teklif adaya gösterilmez.
        if (offer.Status is OfferStatus.PendingApproval or OfferStatus.Approved or OfferStatus.Rejected or OfferStatus.Withdrawn) return (null, null);
        return (offer, app);
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    [HttpGet]
    public async Task<IActionResult> Get(string tenantSlug, string token, CancellationToken ct)
    {
        var t = await ResolveAsync(tenantSlug, ct);
        if (t is null) return NotFound(new { message = "Kariyer sayfası bulunamadı" });
        var (o, app) = await ByTokenAsync(token, ct);
        if (o is null) return Invalid();
        await _db.AuditAsync(HttpContext, tenantSlug, "Offer", o.Id.ToString(), "SignPageViewed", new { channel = "candidate-link" }, ct);
        var reason = OfferSignature.CanSign(o, Today);
        return Ok(new
        {
            company = t.Name,
            offerId = o.Id,
            positionTitle = o.PositionTitle,
            o.StartDate, o.ExpiresAt,
            status = o.Status.ToString(),
            letterText = o.SalaryLetterText,
            letterSha256 = OfferSignature.LetterHash(o.SalaryLetterText),
            canSign = reason is null,
            reason,
            canDecline = o.Status == OfferStatus.Sent && o.SignatureEvidenceId is null,
            emailMasked = OfferSignature.MaskEmail(app!.Candidate!.Email),
            signed = o.SignatureEvidenceId is null ? null : new { o.SignedAt, evidenceId = o.SignatureEvidenceId, o.LetterSha256, o.SignedDocumentSha256 },
            disclaimer = "Basit elektronik imza — 5070 sayılı Kanun kapsamında nitelikli (güvenli) elektronik imza değildir.",
        });
    }

    /// <summary>Kod iste: adayın kayıtlı e-postasına tek kullanımlık kod (governance motoru; 10 dk, saatte 5, 30 sn bekleme).</summary>
    [HttpPost("otp")]
    public async Task<IActionResult> Otp(string tenantSlug, string token, CancellationToken ct)
    {
        if (RateLimited()) return TooMany();
        if (await ResolveAsync(tenantSlug, ct) is null) return NotFound(new { message = "Kariyer sayfası bulunamadı" });
        var (o, app) = await ByTokenAsync(token, ct);
        if (o is null) return Invalid();
        if (OfferSignature.CanSign(o, Today) is { } reason) return Conflict(new { message = reason });
        var r = await _gov.RequestOtpAsync(tenantSlug, o.Id, app!.CandidateId, app.Candidate!.Email, OfferSignature.Title(o), Lang, ct);
        if (!r.Ok) return StatusCode(r.Status, new { message = r.Message, code = r.Code });
        return Ok(new { r.Value!.OtpId, channel = "Email", r.Value.ExpiresAt, r.Value.MaxAttempts, r.Value.SendsLeft, emailMasked = OfferSignature.MaskEmail(app.Candidate.Email) });
    }

    public record SignBody(Guid? OtpId, string? Code, bool Confirm);

    /// <summary>İmzala ve kabul et: kod doğrulanır, kanıt yazılır, teklif "Kabul edildi" ve başvuru "İşe alındı" olur.</summary>
    [HttpPost("sign")]
    public async Task<IActionResult> Sign(string tenantSlug, string token, [FromBody] SignBody body, CancellationToken ct)
    {
        if (RateLimited()) return TooMany();
        var t = await ResolveAsync(tenantSlug, ct);
        if (t is null) return NotFound(new { message = "Kariyer sayfası bulunamadı" });
        if (!body.Confirm) return BadRequest(new { message = "İmzalamak için mektubu okuduğunuzu ve kabul ettiğinizi onaylayın", code = "confirm_required" });
        var (o, app) = await ByTokenAsync(token, ct);
        if (o is null) return Invalid();
        if (OfferSignature.CanSign(o, Today) is { } reason) return Conflict(new { message = reason });
        var letterHash = OfferSignature.LetterHash(o.SalaryLetterText);
        // Mektup, imza başladıktan sonra değişmişse (beklenmez: gönderilmiş teklif düzenlenemez) imza reddedilir.
        if (o.LetterSha256 is { } prev && prev != letterHash) return Conflict(new { message = "Teklif mektubu değişmiş; sayfayı yenileyin" });

        var r = await _gov.SignAsync(tenantSlug, o.Id, app!.CandidateId, body.OtpId, body.Code, letterHash, OfferSignature.Title(o), Lang, ct);
        if (!r.Ok) return StatusCode(r.Status, new { message = r.Message, code = r.Code, attemptsLeft = r.AttemptsLeft });
        var e = r.Value!;

        o.LetterSha256 = letterHash;
        o.SignatureEvidenceId = e.Id;
        o.SignedAt = e.SignedAt;
        o.SignedSalaryLetterHtml = OfferSignature.RenderSignedHtml(t.Name, o, letterHash, e.Id, e.EvidenceSha256, e.SignedAt, e.Method, e.Disclaimer);
        o.SignedDocumentSha256 = OfferSignature.Sha256Hex(o.SignedSalaryLetterHtml);
        var (error, _) = await OffersController.RespondCoreAsync(_db, o, app, accept: true, ct);
        await _db.SaveChangesAsync(ct);
        await _db.AuditAsync(HttpContext, tenantSlug, "Offer", o.Id.ToString(), "SignedByCandidate",
            new { evidenceId = e.Id, method = e.Method, letterSha256 = letterHash, documentSha256 = o.SignedDocumentSha256, accepted = error is null }, ct);
        await _db.EmailCandidateAsync(tenantSlug, app.Candidate!.Email, $"İş teklifi imzalandı — {t.Name}",
            $"Merhaba {app.Candidate.FirstName}, \"{o.PositionTitle}\" iş teklifini elektronik olarak imzalayıp kabul ettiniz. "
            + "İmzalı belgeyi aynı bağlantıdan indirebilirsiniz. Bu işlemi siz yapmadıysanız lütfen İnsan Kaynakları ile hemen iletişime geçin.",
            "recruitment.offer.signed", ct);
        if (error is not null) return Conflict(new { message = error, signed = true });
        return Ok(new
        {
            status = o.Status.ToString(), o.SignedAt, evidenceId = e.Id, method = e.Method, documentSha256 = letterHash,
            evidenceSha256 = e.EvidenceSha256, ipPrefix = (string?)null, e.IntegrityOk, o.SignedDocumentSha256,
        });
    }

    /// <summary>Teklifi reddet (imza gerekmez).</summary>
    [HttpPost("decline")]
    public async Task<IActionResult> Decline(string tenantSlug, string token, CancellationToken ct)
    {
        if (RateLimited()) return TooMany();
        if (await ResolveAsync(tenantSlug, ct) is null) return NotFound(new { message = "Kariyer sayfası bulunamadı" });
        var (o, app) = await ByTokenAsync(token, ct);
        if (o is null) return Invalid();
        if (o.SignatureEvidenceId is not null) return Conflict(new { message = "Teklif zaten imzalanmış" });
        var (error, code) = await OffersController.RespondCoreAsync(_db, o, app!, accept: false, ct);
        await _db.SaveChangesAsync(ct);
        if (error is not null) return StatusCode(code, new { message = error });
        await _db.AuditAsync(HttpContext, tenantSlug, "Offer", o.Id.ToString(), "DeclinedByCandidate", new { channel = "candidate-link" }, ct);
        return Ok(new { status = o.Status.ToString() });
    }

    /// <summary>İmzalı belge (HTML) — yalnızca imzadan sonra.</summary>
    [HttpGet("document")]
    public async Task<IActionResult> Document(string tenantSlug, string token, CancellationToken ct)
    {
        if (await ResolveAsync(tenantSlug, ct) is null) return NotFound(new { message = "Kariyer sayfası bulunamadı" });
        var (o, _) = await ByTokenAsync(token, ct);
        if (o is null) return Invalid();
        if (o.SignedSalaryLetterHtml is null) return NotFound(new { message = "İmzalı belge yok" });
        await _db.AuditAsync(HttpContext, tenantSlug, "Offer", o.Id.ToString(), "SignedDocumentDownloaded", new { channel = "candidate-link" }, ct);
        return Ok(new { fileName = $"is-teklifi-{o.Id.ToString()[..8]}.html", html = o.SignedSalaryLetterHtml, sha256 = o.SignedDocumentSha256 });
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RecruitmentService.Data;
using RecruitmentService.Models;
using RecruitmentService.Services;
using RecruitmentService.Tenancy;

namespace RecruitmentService.Controllers;

/// <summary>
/// Adayın mülakat saatini kendisi seçmesi (yenilik haritası #10). İşe alım sorumlusu 2-10 uygun saat
/// önerir ve adaya tek kullanımlık bağlantı gider; aday bağlantıdan bir saat seçince mülakat kurulur.
/// Seçim anında görüşmecilerin o saatte başka mülakatı varsa o saat artık gösterilmez/kabul edilmez.
/// </summary>
[ApiController]
public class InterviewBookingController : ControllerBase
{
    private readonly RecruitmentDbContext _db;
    private readonly TenantContext _tenant;
    private readonly PublicRateLimiter _limiter;
    public InterviewBookingController(RecruitmentDbContext db, TenantContext tenant, PublicRateLimiter limiter) { _db = db; _tenant = tenant; _limiter = limiter; }

    public sealed record ProposalInput(InterviewType Type, int DurationMinutes, List<Guid> InterviewerIds, string? Location, string? MeetingUrl,
        List<DateTimeOffset> Slots, bool SendEmail = true, int ValidDays = 7);

    private static string Origin => (Environment.GetEnvironmentVariable("PUBLIC_ORIGIN") ?? "http://localhost").TrimEnd('/');

    /// <summary>Görüşmecilerin bu aralıkla kesişen beklemedeki mülakatı var mı?</summary>
    private async Task<bool> BusyAsync(DateTimeOffset at, int duration, List<Guid> interviewers, CancellationToken ct)
    {
        var from = at.AddHours(-8);
        var to = at.AddMinutes(duration);
        var list = await _db.Interviews.AsNoTracking().Where(i => i.Result == InterviewResult.Pending && i.ScheduledAt < to && i.ScheduledAt > from).ToListAsync(ct);
        return list.Any(i => i.ScheduledAt.AddMinutes(i.DurationMinutes) > at && i.AllInterviewers().Intersect(interviewers).Any());
    }

    [HttpPost("api/applications/{id:guid}/interview-proposals")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> Create(Guid id, ProposalInput body, CancellationToken ct)
    {
        var app = await _db.Applications.Include(a => a.Candidate).Include(a => a.JobPosting).FirstOrDefaultAsync(a => a.Id == id, ct);
        if (app is null) return NotFound();
        if (PipelineRules.IsTerminal(app.Status)) return BadRequest(new { message = "Sonuçlanmış başvuru için mülakat önerilemez" });
        var interviewers = body.InterviewerIds.Where(g => g != Guid.Empty).Distinct().ToList();
        if (interviewers.Count is 0 or > 6) return BadRequest(new { message = "1-6 görüşmeci seçin" });
        if (body.DurationMinutes is < 15 or > 480) return BadRequest(new { message = "Süre 15-480 dakika olmalı" });
        var slots = body.Slots.Distinct().OrderBy(s => s).ToList();
        if (slots.Count is < 2 or > 10) return BadRequest(new { message = "2-10 farklı saat önerin" });
        if (slots.Any(s => s < DateTimeOffset.UtcNow.AddHours(1))) return BadRequest(new { message = "Önerilen saatler en az bir saat sonrası olmalı" });
        if (body.Location is { Length: > 300 } || body.MeetingUrl is { Length: > 500 }) return BadRequest(new { message = "Yer/bağlantı çok uzun" });
        if (!string.IsNullOrWhiteSpace(body.MeetingUrl) && !(Uri.TryCreate(body.MeetingUrl.Trim(), UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp)))
            return BadRequest(new { message = "Toplantı bağlantısı http(s) ile başlayan geçerli bir adres olmalı" });
        if ((await _db.PeopleAsync(app.TenantSlug, interviewers, ct)).Count != interviewers.Count) return BadRequest(new { message = "Görüşmecilerden biri bulunamadı" });
        if (app.Candidate is null or { AnonymizedAt: not null }) return BadRequest(new { message = "Adayın iletişim bilgisi yok" });

        // Önceki açık öneriler geçersiz olur (adayın elinde tek geçerli bağlantı kalsın).
        await _db.InterviewProposals.Where(p => p.ApplicationId == id && p.Status == "Open").ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, "Cancelled"), ct);
        var token = PublicCareerController.NewToken();
        var proposal = new InterviewProposal
        {
            ApplicationId = id, TokenHash = PublicCareerController.Hash(token), Type = body.Type, DurationMinutes = body.DurationMinutes,
            InterviewerIds = interviewers, Location = string.IsNullOrWhiteSpace(body.Location) ? null : body.Location.Trim(),
            MeetingUrl = string.IsNullOrWhiteSpace(body.MeetingUrl) ? null : body.MeetingUrl.Trim(), Slots = slots,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(Math.Clamp(body.ValidDays, 1, 30)),
            CreatedByName = User.FindFirst("name")?.Value ?? User.FindFirst("preferred_username")?.Value,
        };
        _db.InterviewProposals.Add(proposal);
        await _db.SaveChangesAsync(ct);
        var link = $"{Origin}/kariyer/{app.TenantSlug}/mulakat/{token}";
        if (body.SendEmail)
        {
            var company = (await _db.TenantAsync(app.TenantSlug, ct))?.Name ?? "Şirketimiz";
            await _db.EmailCandidateAsync(app.TenantSlug, app.Candidate.Email, $"Mülakat saatinizi seçin — {company}",
                $"Merhaba {app.Candidate.FirstName},\n\n{company} bünyesindeki \"{app.JobPosting?.Title}\" pozisyonu başvurunuz için sizi mülakata davet ediyoruz. "
                + $"Aşağıdaki bağlantıdan size uygun saati seçebilirsiniz ({InterviewTexts.When(proposal.ExpiresAt)} tarihine kadar geçerli):\n\n{link}\n\nSaygılarımızla,\n{company} İnsan Kaynakları",
                "recruitment.interview.booking", ct);
        }
        return Ok(new { proposal.Id, link, proposal.ExpiresAt, slots });
    }

    [HttpGet("api/applications/{id:guid}/interview-proposals")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> List(Guid id, CancellationToken ct) =>
        Ok(await _db.InterviewProposals.AsNoTracking().Where(p => p.ApplicationId == id).OrderByDescending(p => p.CreatedAt)
            .Select(p => new { p.Id, p.Status, p.Slots, p.ExpiresAt, p.ChosenAt, p.InterviewId, p.DurationMinutes, p.Type, p.CreatedAt, p.CreatedByName }).ToListAsync(ct));

    [HttpPost("api/applications/{id:guid}/interview-proposals/{proposalId:guid}/cancel")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> Cancel(Guid id, Guid proposalId, CancellationToken ct)
        => await _db.InterviewProposals.Where(p => p.Id == proposalId && p.ApplicationId == id && p.Status == "Open")
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, "Cancelled"), ct) == 1 ? Ok(new { ok = true }) : BadRequest(new { message = "Açık öneri bulunamadı" });

    // ------------------------------------------------------------------ aday (herkese açık)

    private async Task<(InterviewProposal? P, Application? App, string? Company)> ResolveAsync(string tenantSlug, string token, CancellationToken ct)
    {
        if (token.Length is < 20 or > 100 || !System.Text.RegularExpressions.Regex.IsMatch(tenantSlug, "^[a-z0-9][a-z0-9-]{1,63}$")) return (null, null, null);
        _tenant.TenantSlug = tenantSlug;
        _tenant.IsPlatformAdmin = false;
        var hash = PublicCareerController.Hash(token);
        var p = await _db.InterviewProposals.FirstOrDefaultAsync(x => x.TokenHash == hash, ct);
        if (p is null) return (null, null, null);
        var app = await _db.Applications.Include(a => a.Candidate).Include(a => a.JobPosting).FirstOrDefaultAsync(a => a.Id == p.ApplicationId, ct);
        return (p, app, (await _db.TenantAsync(tenantSlug, ct))?.Name);
    }

    private bool Limited()
    {
        var key = PublicRateLimiter.ClientKey(HttpContext);
        Request.Headers.Remove("X-Real-IP");
        Request.Headers.Remove("X-Forwarded-For");
        return !_limiter.TryAcquire(key);
    }

    [HttpGet("api/public/{tenantSlug}/interview-booking/{token}")]
    [AllowAnonymous]
    public async Task<IActionResult> Get(string tenantSlug, string token, CancellationToken ct)
    {
        if (Limited()) return StatusCode(429, new { message = "Çok fazla istek gönderildi. Lütfen birkaç dakika sonra tekrar deneyin." });
        var (p, app, company) = await ResolveAsync(tenantSlug, token, ct);
        if (p is null || app is null) return NotFound(new { message = "Bağlantı geçersiz" });
        var expired = p.Status == "Open" && p.ExpiresAt < DateTimeOffset.UtcNow;
        var free = new List<DateTimeOffset>();
        if (p.Status == "Open" && !expired)
            foreach (var s in p.Slots.Where(s => s > DateTimeOffset.UtcNow.AddMinutes(30)))
                if (!await BusyAsync(s, p.DurationMinutes, p.InterviewerIds, ct)) free.Add(s);
        var chosen = p.InterviewId is { } iid ? await _db.Interviews.AsNoTracking().FirstOrDefaultAsync(i => i.Id == iid, ct) : null;
        return Ok(new
        {
            company, position = app.JobPosting?.Title, firstName = app.Candidate?.FirstName, status = expired ? "Expired" : p.Status,
            p.DurationMinutes, type = p.Type.ToString(), location = p.Location, online = p.MeetingUrl is not null, slots = free, p.ExpiresAt,
            chosen = chosen is null ? null : new { chosen.ScheduledAt, chosen.Location, chosen.MeetingUrl },
        });
    }

    public sealed record BookInput(DateTimeOffset Slot);

    [HttpPost("api/public/{tenantSlug}/interview-booking/{token}")]
    [AllowAnonymous]
    public async Task<IActionResult> Book(string tenantSlug, string token, BookInput body, CancellationToken ct)
    {
        if (Limited()) return StatusCode(429, new { message = "Çok fazla istek gönderildi. Lütfen birkaç dakika sonra tekrar deneyin." });
        var (p, app, company) = await ResolveAsync(tenantSlug, token, ct);
        if (p is null || app is null) return NotFound(new { message = "Bağlantı geçersiz" });
        if (p.Status != "Open") return Conflict(new { message = p.Status == "Booked" ? "Bu bağlantıyla zaten bir saat seçildi" : "Bu bağlantı artık geçerli değil" });
        if (p.ExpiresAt < DateTimeOffset.UtcNow) return Conflict(new { message = "Bağlantının süresi doldu; İnsan Kaynakları ile iletişime geçin" });
        if (PipelineRules.IsTerminal(app.Status)) return Conflict(new { message = "Başvuru sonuçlandığı için mülakat planlanamaz" });
        var slot = p.Slots.FirstOrDefault(s => Math.Abs((s - body.Slot).TotalSeconds) < 1);
        if (slot == default) return BadRequest(new { message = "Seçilen saat önerilenlerden değil" });
        if (slot < DateTimeOffset.UtcNow.AddMinutes(30) || await BusyAsync(slot, p.DurationMinutes, p.InterviewerIds, ct))
            return Conflict(new { message = "Bu saat artık uygun değil; lütfen başka bir saat seçin", code = "slot_taken" });

        var interview = new Interview
        {
            ApplicationId = app.Id, Type = p.Type, ScheduledAt = slot, InterviewerEmployeeId = p.InterviewerIds[0], InterviewerIds = p.InterviewerIds,
            DurationMinutes = p.DurationMinutes, Location = p.Location, MeetingUrl = p.MeetingUrl, CandidateNotifiedAt = DateTimeOffset.UtcNow,
        };
        _db.Interviews.Add(interview);
        if (app.Status is ApplicationStatus.Applied or ApplicationStatus.Screening) { app.Status = ApplicationStatus.Interview; app.StatusChangedAt = DateTimeOffset.UtcNow; }
        p.Status = "Booked"; p.ChosenAt = DateTimeOffset.UtcNow; p.InterviewId = interview.Id;
        await _db.SaveChangesAsync(ct);

        var when = InterviewTexts.When(slot);
        var where = InterviewTexts.Where(p.Location, p.MeetingUrl);
        foreach (var e in p.InterviewerIds)
            await _db.NotifyAsync(app.TenantSlug, e, "Aday mülakat saatini seçti",
                $"\"{app.JobPosting?.Title}\" pozisyonu adayı {when} tarihini seçti ({p.DurationMinutes} dk, {where}). Puan kartını Mülakatlarım ekranından doldurabilirsiniz.",
                "recruitment.interview.scheduled", ct);
        if (app.Candidate is { AnonymizedAt: null } c)
            await _db.EmailCandidateAsync(app.TenantSlug, c.Email, $"Mülakat onayı — {company}",
                InterviewTexts.Invitation(c.FirstName, company ?? "Şirketimiz", app.JobPosting?.Title ?? "", when, p.DurationMinutes, where), "recruitment.interview.invite", ct);
        return Ok(new { scheduledAt = slot, location = p.Location, meetingUrl = p.MeetingUrl });
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RecruitmentService.Data;
using RecruitmentService.Models;
using RecruitmentService.Services;
using RecruitmentService.Tenancy;

namespace RecruitmentService.Controllers;

[ApiController]
[Route("api/applications")]
[Authorize(Policy = "RequireManagerOrAbove")]
public class ApplicationsController : ControllerBase
{
    private readonly RecruitmentDbContext _db;
    private readonly ITenantContext _tenant;
    public ApplicationsController(RecruitmentDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] Guid? jobPostingId, [FromQuery] ApplicationStatus? status)
    {
        var q = _db.Applications.Include(a => a.Candidate).AsQueryable();
        if (jobPostingId.HasValue) q = q.Where(a => a.JobPostingId == jobPostingId.Value);
        if (status.HasValue) q = q.Where(a => a.Status == status.Value);
        return Ok(await q.OrderByDescending(a => a.AppliedAt).ToListAsync());
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var a = await _db.Applications
            .Include(x => x.Candidate).Include(x => x.JobPosting).Include(x => x.Interviews)
            .FirstOrDefaultAsync(x => x.Id == id);
        return a is null ? NotFound() : Ok(a);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateApplicationRequest request)
    {
        var posting = await _db.JobPostings.FirstOrDefaultAsync(p => p.Id == request.JobPostingId);
        if (posting is null) return BadRequest("Ilan bulunamadi");
        if (posting.Status != JobPostingStatus.Published)
            return BadRequest("Yalnizca yayindaki ilanlara basvuru alinabilir");

        if (!await _db.Candidates.AnyAsync(c => c.Id == request.CandidateId))
            return BadRequest("Aday bulunamadi");

        if (await _db.Applications.AnyAsync(a =>
                a.JobPostingId == request.JobPostingId && a.CandidateId == request.CandidateId))
            return Conflict("Bu aday bu ilana zaten basvurmus");

        var app = new Application
        {
            JobPostingId = request.JobPostingId,
            CandidateId = request.CandidateId,
            Notes = request.Notes
        };
        _db.Applications.Add(app);
        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id = app.Id }, app);
    }

    /// <summary>Basvuruyu ise alim hunisinde bir sonraki asamaya tasir.</summary>
    [HttpPost("{id}/status")]
    public async Task<IActionResult> ChangeStatus(Guid id, [FromBody] ChangeStatusRequest request)
    {
        var app = await _db.Applications.FirstOrDefaultAsync(a => a.Id == id);
        if (app is null) return NotFound();

        // NOT: Onceden herhangi bir gecis serbestti (Basvuru -> Ise alindi, kapali ilana
        // ise alim). Ise alim yalnizca Teklif asamasindan ve acik bir ilanda yapilir;
        // asamalar arasi geri tasima (yeniden degerlendirme) serbest birakildi.
        // G13: kural kanban tasima ucuyla ortak (PipelineRules).
        var posting = await _db.JobPostings.AsNoTracking().FirstOrDefaultAsync(p => p.Id == app.JobPostingId);
        var error = PipelineRules.CheckMove(app.Status, request.Status, posting is null || posting.Status == JobPostingStatus.Closed);
        if (error is not null) return BadRequest(error);

        app.Status = request.Status;
        app.StatusChangedAt = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(request.Notes)) app.Notes = request.Notes;

        await _db.SaveChangesAsync();
        return Ok(app);
    }

    /// <summary>G13: kanban panosu — sıralı aşamalar ve ilan başvurularının kartları.</summary>
    [HttpGet("pipeline")]
    public async Task<IActionResult> Pipeline([FromQuery] Guid jobPostingId, CancellationToken ct)
    {
        var apps = await _db.Applications.AsNoTracking().Include(a => a.Candidate).Include(a => a.Interviews)
            .Where(a => a.JobPostingId == jobPostingId)
            .OrderBy(a => a.StatusChangedAt ?? a.AppliedAt)
            .ToListAsync(ct);
        var ids = apps.Select(a => a.Id).ToList();
        var ivIds = apps.SelectMany(a => a.Interviews).Select(i => i.Id).ToList();
        // Kör değerlendirme (GET /interviews/{id}/scorecards ile aynı kural): çağıran bir mülakatın panelindeyse
        // ve panel tamamlanmadıysa o başvurunun ortalaması gösterilmez (averageHidden).
        var cards = await _db.Scorecards.AsNoTracking().Where(s => ivIds.Contains(s.InterviewId))
            .Select(s => new { s.InterviewId, s.InterviewerEmployeeId, s.OverallScore }).ToListAsync(ct);
        var me = await _db.MyEmployeeIdAsync(_tenant.TenantSlug, RecruitmentSql.UserId(User), ct);
        var blindInterviews = apps.SelectMany(a => a.Interviews)
            .Where(i => ScorecardRules.IsBlind(i.AllInterviewers().ToList(),
                cards.Where(c => c.InterviewId == i.Id).Select(c => c.InterviewerEmployeeId).ToHashSet(), me))
            .Select(i => i.Id).ToHashSet();
        var scores = cards.Where(c => c.OverallScore != null).Select(c => new { c.InterviewId, c.OverallScore }).ToList();
        var offers = await _db.Offers.AsNoTracking().Where(o => ids.Contains(o.ApplicationId))
            .OrderByDescending(o => o.CreatedAt).Select(o => new { o.ApplicationId, o.Status }).ToListAsync(ct);
        return Ok(new
        {
            stages = PipelineRules.Stages.Select(s => new { key = s.ToString(), terminal = PipelineRules.IsTerminal(s) }),
            cards = apps.Select(a =>
            {
                var ivs = a.Interviews.Select(i => i.Id).ToHashSet();
                var hidden = ivs.Overlaps(blindInterviews);
                var sc = hidden ? new List<decimal>() : scores.Where(s => ivs.Contains(s.InterviewId)).Select(s => s.OverallScore!.Value).ToList();
                return new
                {
                    a.Id, a.CandidateId, candidateName = a.Candidate is null ? null : $"{a.Candidate.FirstName} {a.Candidate.LastName}",
                    status = a.Status.ToString(), a.AppliedAt, a.StatusChangedAt, a.Channel, a.DuplicateReason,
                    interviewCount = a.Interviews.Count(i => i.Result != InterviewResult.Cancelled),
                    nextInterviewAt = a.Interviews.Where(i => i.Result == InterviewResult.Pending && i.ScheduledAt > DateTimeOffset.UtcNow)
                        .OrderBy(i => i.ScheduledAt).Select(i => (DateTimeOffset?)i.ScheduledAt).FirstOrDefault(),
                    averageScore = sc.Count == 0 ? (decimal?)null : Math.Round(sc.Average(), 2),
                    averageHidden = hidden,
                    offerStatus = offers.FirstOrDefault(o => o.ApplicationId == a.Id)?.Status.ToString(),
                };
            }),
        });
    }

    public record MoveRequest(ApplicationStatus Status, string? Notes);

    /// <summary>G13: kanban sürükle-bırak — ChangeStatus ile aynı kurallar, hata gövdesi JSON.</summary>
    [HttpPost("{id}/move")]
    public async Task<IActionResult> Move(Guid id, [FromBody] MoveRequest request, CancellationToken ct)
    {
        var app = await _db.Applications.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (app is null) return NotFound(new { message = "Başvuru bulunamadı" });
        var posting = await _db.JobPostings.AsNoTracking().FirstOrDefaultAsync(p => p.Id == app.JobPostingId, ct);
        var error = PipelineRules.CheckMove(app.Status, request.Status, posting is null || posting.Status == JobPostingStatus.Closed);
        if (error is not null) return BadRequest(new { message = error });
        var from = app.Status;
        app.Status = request.Status;
        app.StatusChangedAt = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(request.Notes)) app.Notes = request.Notes.Trim();
        await _db.SaveChangesAsync(ct);
        return Ok(new { app.Id, from = from.ToString(), status = app.Status.ToString(), app.StatusChangedAt });
    }

    /// <summary>
    /// Y17: mülakat planlama. Birden çok görüşmeci, süre, yer/çevrim içi bağlantı. Görüşmecilerden
    /// biri aynı zaman aralığında başka bir mülakattaysa 409. Görüşmecilere uygulama içi bildirim,
    /// adaya (isteğe bağlı) e-posta daveti; kopyalanabilir davet metni de döner.
    /// </summary>
    [HttpPost("{id}/interviews")]
    public async Task<IActionResult> ScheduleInterview(
        Guid id, [FromBody] ScheduleInterviewRequest request, CancellationToken ct)
    {
        var app = await _db.Applications.Include(a => a.Candidate).Include(a => a.JobPosting).FirstOrDefaultAsync(a => a.Id == id, ct);
        if (app is null) return NotFound();
        if (PipelineRules.IsTerminal(app.Status)) return BadRequest(new { message = "Sonuçlanmış başvuru için mülakat planlanamaz" });

        var interviewers = (request.InterviewerEmployeeIds ?? new())
            .Prepend(request.InterviewerEmployeeId ?? Guid.Empty)
            .Where(g => g != Guid.Empty).Distinct().ToList();
        if (interviewers.Count is 0 or > 6) return BadRequest(new { message = "1-6 görüşmeci seçin" });
        var duration = request.DurationMinutes ?? 60;
        if (duration is < 15 or > 480) return BadRequest(new { message = "Süre 15-480 dakika olmalı" });
        if (request.ScheduledAt < DateTimeOffset.UtcNow.AddMinutes(-5)) return BadRequest(new { message = "Geçmiş bir zamana mülakat planlanamaz" });
        if (request.Location is { Length: > 300 } || request.MeetingUrl is { Length: > 500 })
            return BadRequest(new { message = "Yer/bağlantı çok uzun" });
        if (!string.IsNullOrWhiteSpace(request.MeetingUrl)
            && !(Uri.TryCreate(request.MeetingUrl.Trim(), UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp)))
            return BadRequest(new { message = "Toplantı bağlantısı http(s) ile başlayan geçerli bir adres olmalı" });

        var tenant = app.TenantSlug;
        var people = await _db.PeopleAsync(tenant, interviewers, ct);
        if (people.Count != interviewers.Count) return BadRequest(new { message = "Görüşmecilerden biri bulunamadı" });

        if (await BusyConflictAsync(request.ScheduledAt, duration, interviewers, people, null, ct) is { } busy) return busy;

        var interview = new Interview
        {
            ApplicationId = id,
            Type = request.Type,
            ScheduledAt = request.ScheduledAt,
            InterviewerEmployeeId = interviewers[0],
            InterviewerIds = interviewers,
            DurationMinutes = duration,
            Location = string.IsNullOrWhiteSpace(request.Location) ? null : request.Location.Trim(),
            MeetingUrl = string.IsNullOrWhiteSpace(request.MeetingUrl) ? null : request.MeetingUrl.Trim(),
        };
        _db.Interviews.Add(interview);

        if (app.Status == ApplicationStatus.Applied || app.Status == ApplicationStatus.Screening)
        {
            app.Status = ApplicationStatus.Interview;
            app.StatusChangedAt = DateTimeOffset.UtcNow;
        }

        var company = (await _db.TenantAsync(tenant, ct))?.Name ?? "Şirketimiz";
        var when = InterviewTexts.When(request.ScheduledAt);
        var where = InterviewTexts.Where(interview.Location, interview.MeetingUrl);
        var invitation = InterviewTexts.Invitation(app.Candidate?.FirstName ?? "", company, app.JobPosting?.Title ?? "", when, duration, where);
        if (request.NotifyCandidate && app.Candidate is { AnonymizedAt: null })
            interview.CandidateNotifiedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);

        // KVKK: görüşmeci bildiriminde aday adı yok; ayrıntı İK360'ta.
        foreach (var p in interviewers)
            await _db.NotifyAsync(tenant, p, "Mülakat planlandı",
                $"{when} tarihinde \"{app.JobPosting?.Title}\" pozisyonu için bir mülakata görüşmeci olarak atandınız ({duration} dk, {where}). Puan kartını Mülakatlarım ekranından doldurabilirsiniz.",
                "recruitment.interview.scheduled", ct);
        if (interview.CandidateNotifiedAt is not null && app.Candidate is not null)
            await _db.EmailCandidateAsync(tenant, app.Candidate.Email, $"Mülakat daveti — {company}", invitation, "recruitment.interview.invite", ct);

        return CreatedAtAction(nameof(GetById), new { id }, new
        {
            interview.Id, interview.ApplicationId, interview.Type, interview.ScheduledAt, interview.DurationMinutes, interview.Location,
            interview.MeetingUrl, interview.InterviewerEmployeeId, interview.InterviewerIds, interview.Result,
            candidateNotified = interview.CandidateNotifiedAt is not null,
            invitationText = invitation,
        });
    }

    /// <summary>
    /// Çakışma: aynı görüşmecinin (birincil ya da panel) beklemedeki bir mülakatıyla zaman aralığı kesişiyor mu?
    /// <paramref name="exceptId"/> yeniden planlamada mülakatın kendisini dışarıda bırakır.
    /// </summary>
    private async Task<IActionResult?> BusyConflictAsync(DateTimeOffset at, int duration, List<Guid> interviewers,
        List<RecruitmentSql.PersonRow> people, Guid? exceptId, CancellationToken ct)
    {
        var windowStart = at.AddHours(-8);
        var windowEnd = at.AddMinutes(duration);
        var near = await _db.Interviews.AsNoTracking()
            .Where(i => i.Result == InterviewResult.Pending && i.ScheduledAt > windowStart && i.ScheduledAt < windowEnd && i.Id != exceptId)
            .ToListAsync(ct);
        var conflicts = near
            .Where(i => ScorecardRules.Overlaps(at, duration, i.ScheduledAt, i.DurationMinutes))
            .SelectMany(i => i.AllInterviewers().Where(interviewers.Contains).Select(p => new { interviewerId = p, interviewId = i.Id, i.ScheduledAt, i.DurationMinutes }))
            .ToList();
        if (conflicts.Count == 0) return null;
        var names = people.Where(p => conflicts.Any(c => c.interviewerId == p.Id)).Select(p => $"{p.FirstName} {p.LastName}");
        return Conflict(new { message = $"Görüşmecinin bu saatte başka bir mülakatı var: {string.Join(", ", names)}", code = "interviewer_busy", conflicts });
    }

    /// <summary>
    /// Planlanan (beklemedeki) mülakatı iptal eder. Görüşmecilere uygulama içi bildirim gider; aday daha
    /// önce e-postayla davet edildiyse ve istenirse adaya da iptal e-postası gönderilir.
    /// </summary>
    [HttpPost("{id}/interviews/{interviewId}/cancel")]
    public async Task<IActionResult> CancelInterview(Guid id, Guid interviewId, [FromBody] CancelInterviewRequest? request, CancellationToken ct)
    {
        var iv = await _db.Interviews.Include(i => i.Application!).ThenInclude(a => a.Candidate)
            .Include(i => i.Application!).ThenInclude(a => a.JobPosting)
            .FirstOrDefaultAsync(i => i.Id == interviewId && i.ApplicationId == id, ct);
        if (iv is null) return NotFound(new { message = "Mülakat bulunamadı" });
        if (iv.Result != InterviewResult.Pending) return BadRequest(new { message = "Yalnızca sonuçlanmamış mülakat iptal edilebilir" });
        if (request?.Reason is { Length: > 300 }) return BadRequest(new { message = "Gerekçe en fazla 300 karakter olabilir" });

        iv.Result = InterviewResult.Cancelled;
        iv.Notes = string.IsNullOrWhiteSpace(request?.Reason) ? iv.Notes : request!.Reason!.Trim();
        await _db.SaveChangesAsync(ct);

        var tenant = iv.TenantSlug;
        var when = InterviewTexts.When(iv.ScheduledAt);
        var posting = iv.Application?.JobPosting?.Title ?? "";
        // KVKK: görüşmeci bildiriminde aday adı yok.
        foreach (var p in iv.AllInterviewers())
            await _db.NotifyAsync(tenant, p, "Mülakat iptal edildi",
                $"{when} tarihindeki \"{posting}\" pozisyonu mülakatı iptal edildi.", "recruitment.interview.cancelled", ct);
        var candidate = iv.Application?.Candidate;
        if (request?.NotifyCandidate == true && iv.CandidateNotifiedAt is not null && candidate is { AnonymizedAt: null })
        {
            var company = (await _db.TenantAsync(tenant, ct))?.Name ?? "Şirketimiz";
            await _db.EmailCandidateAsync(tenant, candidate.Email, $"Mülakat iptali — {company}",
                InterviewTexts.Cancellation(candidate.FirstName, company, posting, when), "recruitment.interview.cancelled", ct);
        }
        return Ok(new { iv.Id, iv.ApplicationId, iv.Result });
    }

    /// <summary>Beklemedeki mülakatın zamanını/süresini değiştirir (çakışma denetimiyle); görüşmecilere bildirim.</summary>
    [HttpPost("{id}/interviews/{interviewId}/reschedule")]
    public async Task<IActionResult> RescheduleInterview(Guid id, Guid interviewId, [FromBody] RescheduleInterviewRequest request, CancellationToken ct)
    {
        var iv = await _db.Interviews.Include(i => i.Application!).ThenInclude(a => a.Candidate)
            .Include(i => i.Application!).ThenInclude(a => a.JobPosting)
            .FirstOrDefaultAsync(i => i.Id == interviewId && i.ApplicationId == id, ct);
        if (iv is null) return NotFound(new { message = "Mülakat bulunamadı" });
        if (iv.Result != InterviewResult.Pending) return BadRequest(new { message = "Yalnızca sonuçlanmamış mülakat yeniden planlanabilir" });
        var duration = request.DurationMinutes ?? iv.DurationMinutes;
        if (duration is < 15 or > 480) return BadRequest(new { message = "Süre 15-480 dakika olmalı" });
        if (request.ScheduledAt < DateTimeOffset.UtcNow.AddMinutes(-5)) return BadRequest(new { message = "Geçmiş bir zamana mülakat planlanamaz" });

        var interviewers = iv.AllInterviewers().ToList();
        var people = await _db.PeopleAsync(iv.TenantSlug, interviewers, ct);
        if (await BusyConflictAsync(request.ScheduledAt, duration, interviewers, people, iv.Id, ct) is { } busy) return busy;

        var oldWhen = InterviewTexts.When(iv.ScheduledAt);
        iv.ScheduledAt = request.ScheduledAt;
        iv.DurationMinutes = duration;
        await _db.SaveChangesAsync(ct);

        var tenant = iv.TenantSlug;
        var when = InterviewTexts.When(iv.ScheduledAt);
        var where = InterviewTexts.Where(iv.Location, iv.MeetingUrl);
        var posting = iv.Application?.JobPosting?.Title ?? "";
        foreach (var p in interviewers)
            await _db.NotifyAsync(tenant, p, "Mülakat yeniden planlandı",
                $"\"{posting}\" pozisyonu mülakatı {oldWhen} yerine {when} tarihine alındı ({duration} dk, {where}).", "recruitment.interview.rescheduled", ct);
        var candidate = iv.Application?.Candidate;
        string? invitation = null;
        if (candidate is not null)
        {
            var company = (await _db.TenantAsync(tenant, ct))?.Name ?? "Şirketimiz";
            invitation = InterviewTexts.Invitation(candidate.FirstName, company, posting, when, duration, where);
            if (request.NotifyCandidate && candidate.AnonymizedAt is null)
            {
                iv.CandidateNotifiedAt = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(ct);
                await _db.EmailCandidateAsync(tenant, candidate.Email, $"Mülakat zamanı değişti — {company}", invitation, "recruitment.interview.invite", ct);
            }
        }
        return Ok(new
        {
            iv.Id, iv.ApplicationId, iv.Type, iv.ScheduledAt, iv.DurationMinutes, iv.Location, iv.MeetingUrl,
            iv.InterviewerEmployeeId, iv.InterviewerIds, iv.Result, candidateNotified = request.NotifyCandidate && iv.CandidateNotifiedAt is not null,
            invitationText = invitation,
        });
    }

    [HttpPost("{id}/interviews/{interviewId}/result")]
    public async Task<IActionResult> RecordResult(
        Guid id, Guid interviewId, [FromBody] InterviewResultRequest request)
    {
        var interview = await _db.Interviews
            .FirstOrDefaultAsync(i => i.Id == interviewId && i.ApplicationId == id);
        if (interview is null) return NotFound();
        if (interview.Result == InterviewResult.Cancelled || request.Result == InterviewResult.Cancelled)
            return BadRequest(new { message = "İptal edilen mülakata sonuç girilemez; iptal için iptal işlemini kullanın" });

        interview.Result = request.Result;
        interview.Score = request.Score;
        interview.Notes = request.Notes;
        await _db.SaveChangesAsync();
        // Y17: özel nitelikli veri uyarısı (engellemez).
        return Ok(new
        {
            interview.Id, interview.ApplicationId, interview.Type, interview.ScheduledAt, interview.InterviewerEmployeeId,
            interview.Result, interview.Score, interview.Notes,
            warnings = SensitiveNoteDetector.Detect(request.Notes),
        });
    }
}

public static class InterviewTexts
{
    private static readonly System.Globalization.CultureInfo Tr = new("tr-TR");
    private static readonly TimeZoneInfo Istanbul = Find();

    private static TimeZoneInfo Find()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"); }
        catch (Exception) { return TimeZoneInfo.CreateCustomTimeZone("TRT", TimeSpan.FromHours(3), "TRT", "TRT"); }
    }

    public static string When(DateTimeOffset at) =>
        TimeZoneInfo.ConvertTime(at, Istanbul).ToString("dd.MM.yyyy HH:mm", Tr);

    public static string Where(string? location, string? url) =>
        url is not null && location is not null ? $"{location} / çevrim içi: {url}"
        : url is not null ? $"çevrim içi: {url}"
        : location ?? "yer daha sonra bildirilecek";

    public static string Invitation(string firstName, string company, string posting, string when, int minutes, string where) =>
        $"Merhaba {firstName},\n\n{company} bünyesindeki \"{posting}\" pozisyonu başvurunuz için sizi mülakata davet ediyoruz.\n\n"
        + $"Tarih ve saat: {when} (Türkiye saati)\nSüre: yaklaşık {minutes} dakika\nYer: {where}\n\n"
        + "Bu zaman size uygun değilse lütfen İnsan Kaynakları ile iletişime geçin.\n\nSaygılarımızla,\n"
        + $"{company} İnsan Kaynakları";

    public static string Cancellation(string firstName, string company, string posting, string when) =>
        $"Merhaba {firstName},\n\n{company} bünyesindeki \"{posting}\" pozisyonu için {when} (Türkiye saati) tarihinde planlanan mülakatınız iptal edilmiştir. "
        + "Yeni bir zaman planlanırsa size ayrıca bilgi verilecektir.\n\nSaygılarımızla,\n"
        + $"{company} İnsan Kaynakları";
}

public record CreateApplicationRequest(Guid JobPostingId, Guid CandidateId, string? Notes);
public record ChangeStatusRequest(ApplicationStatus Status, string? Notes);
public record ScheduleInterviewRequest(
    InterviewType Type, DateTimeOffset ScheduledAt, Guid? InterviewerEmployeeId,
    List<Guid>? InterviewerEmployeeIds = null, int? DurationMinutes = null, string? Location = null,
    string? MeetingUrl = null, bool NotifyCandidate = false);
public record CancelInterviewRequest(string? Reason, bool NotifyCandidate = false);
public record RescheduleInterviewRequest(DateTimeOffset ScheduledAt, int? DurationMinutes = null, bool NotifyCandidate = false);
public record InterviewResultRequest(InterviewResult Result, int? Score, string? Notes);

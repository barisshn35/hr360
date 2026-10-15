using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RecruitmentService.Data;
using RecruitmentService.Models;
using RecruitmentService.Services;
using RecruitmentService.Tenancy;

namespace RecruitmentService.Controllers;

/// <summary>
/// Y17: yapılandırılmış mülakat değerlendirmesi. Görüşmeci (yönetici olmayan bir çalışan da olabilir)
/// yalnızca kendisine atanmış mülakatları görür ve puan kartını doldurur; tüm puan kartlarını
/// yönetici+ / İK görür. Notlarda özel nitelikli veri ifadesi varsa uyarı döner (kayıt engellenmez).
/// </summary>
[ApiController]
[Route("api/interviews")]
[Authorize]
public class InterviewsController : ControllerBase
{
    private readonly RecruitmentDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IAuthorizationService _authz;

    public InterviewsController(RecruitmentDbContext db, ITenantContext tenant, IAuthorizationService authz)
    {
        _db = db;
        _tenant = tenant;
        _authz = authz;
    }

    private async Task<bool> IsManagerPlusAsync() => (await _authz.AuthorizeAsync(User, "RequireManagerOrAbove")).Succeeded;
    private Task<Guid?> MeAsync(CancellationToken ct) => _db.MyEmployeeIdAsync(_tenant.TenantSlug, RecruitmentSql.UserId(User), ct);

    private async Task<List<ScorecardCriterion>> CriteriaAsync(Guid postingId, CancellationToken ct)
    {
        var t = await _db.ScorecardTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.JobPostingId == postingId, ct);
        return t?.Criteria is { Count: > 0 } c ? c : ScorecardRules.DefaultCriteria();
    }

    /// <summary>Bana atanmış mülakatlar (yaklaşan + son 30 gün).</summary>
    [HttpGet("mine")]
    public async Task<IActionResult> Mine(CancellationToken ct)
    {
        var me = await MeAsync(ct);
        if (me is null) return Ok(Array.Empty<object>());
        var since = DateTimeOffset.UtcNow.AddDays(-30);
        var rows = await _db.Interviews.AsNoTracking().Include(i => i.Application!).ThenInclude(a => a.Candidate)
            .Include(i => i.Application!).ThenInclude(a => a.JobPosting)
            .Where(i => (i.InterviewerEmployeeId == me || i.InterviewerIds.Contains(me.Value)) && i.ScheduledAt > since)
            .OrderBy(i => i.ScheduledAt).ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToList();
        var mine = await _db.Scorecards.AsNoTracking().Where(s => ids.Contains(s.InterviewId) && s.InterviewerEmployeeId == me)
            .Select(s => new { s.InterviewId, s.OverallScore }).ToListAsync(ct);
        return Ok(rows.Select(i => new
        {
            i.Id, i.ApplicationId, i.Type, i.ScheduledAt, i.DurationMinutes, i.Location, i.MeetingUrl, i.Result,
            candidateName = i.Application?.Candidate is { } c ? $"{c.FirstName} {c.LastName}" : null,
            posting = i.Application?.JobPosting?.Title,
            jobPostingId = i.Application?.JobPostingId,
            myScorecard = mine.FirstOrDefault(m => m.InterviewId == i.Id) is { } m ? new { submitted = true, m.OverallScore } : null,
        }));
    }

    private async Task<(Interview? Interview, Guid? Me, bool ManagerPlus, IActionResult? Error)> LoadAsync(Guid id, CancellationToken ct)
    {
        var iv = await _db.Interviews.Include(i => i.Application).FirstOrDefaultAsync(i => i.Id == id, ct);
        if (iv is null) return (null, null, false, NotFound(new { message = "Mülakat bulunamadı" }));
        var me = await MeAsync(ct);
        var mgr = await IsManagerPlusAsync();
        var isInterviewer = me is not null && iv.AllInterviewers().Contains(me.Value);
        if (!isInterviewer && !mgr) return (null, null, false, Forbid());
        return (iv, isInterviewer ? me : null, mgr, null);
    }

    /// <summary>Puan kartı formu: ilan şablonu + varsa benim kaydım.</summary>
    [HttpGet("{id:guid}/scorecard-form")]
    public async Task<IActionResult> Form(Guid id, CancellationToken ct)
    {
        var (iv, me, _, err) = await LoadAsync(id, ct);
        if (err is not null) return err;
        var criteria = await CriteriaAsync(iv!.Application!.JobPostingId, ct);
        var mine = me is null ? null : await _db.Scorecards.AsNoTracking().FirstOrDefaultAsync(s => s.InterviewId == id && s.InterviewerEmployeeId == me, ct);
        var submittedBy = await _db.Scorecards.AsNoTracking().Where(s => s.InterviewId == id).Select(s => s.InterviewerEmployeeId).ToListAsync(ct);
        var panel = iv.AllInterviewers().ToList();
        var locked = me is not null && panel.Count > 1 && submittedBy.Contains(me.Value) && panel.All(submittedBy.Contains);
        return Ok(new
        {
            criteria, canSubmit = me is not null && !locked, locked,
            panelSize = panel.Count, submittedCount = submittedBy.Distinct().Count(),
            mine = mine is null ? null : new { mine.Scores, mine.OverallScore, mine.Recommendation, mine.Notes, mine.SubmittedAt },
            hint = "Değerlendirmeyi işle ilgili yetkinliklere dayandırın. Sağlık, hamilelik, din, siyasi görüş, sendika, etnik köken, engellilik, medeni hal, çocuk, yaş gibi özel nitelikli ya da ilgisiz kişisel veri yazmayın.",
        });
    }

    public record SubmitScorecard(List<CriterionScore>? Scores, string? Recommendation, string? Notes);

    private static readonly string[] Recommendations = { "StrongNo", "No", "Yes", "StrongYes" };

    [HttpPost("{id:guid}/scorecard")]
    public async Task<IActionResult> Submit(Guid id, [FromBody] SubmitScorecard body, CancellationToken ct)
    {
        var (iv, me, _, err) = await LoadAsync(id, ct);
        if (err is not null) return err;
        if (me is null) return StatusCode(403, new { message = "Puan kartını yalnızca mülakatın görüşmecileri doldurabilir" });
        if (iv!.Result == InterviewResult.Cancelled) return BadRequest(new { message = "İptal edilen mülakat için puan kartı doldurulamaz" });
        if (body.Notes is { Length: > 4000 }) return BadRequest(new { message = "Not en fazla 4000 karakter olabilir" });
        if (body.Recommendation is { Length: > 0 } r && !Recommendations.Contains(r)) return BadRequest(new { message = "Öneri geçersiz" });
        var criteria = await CriteriaAsync(iv!.Application!.JobPostingId, ct);
        var scores = (body.Scores ?? new()).Select(x => x with { Evidence = string.IsNullOrWhiteSpace(x.Evidence) ? null : x.Evidence.Trim() }).ToList();
        if (scores.Any(x => x.Evidence is { Length: > 1000 })) return BadRequest(new { message = "Kanıt notu en fazla 1000 karakter olabilir" });
        var (overall, error) = ScorecardRules.Weighted(criteria, scores);
        if (error is not null) return BadRequest(new { message = error });
        if (overall is null) return BadRequest(new { message = "En az bir ölçütü puanlayın" });
        // Dalga 11 (77): tüm görüşmeciler gönderince kartlar birbirine açılır; bundan sonra değiştirilemez
        // (başkalarının puanını gördükten sonra kendi puanını uydurma riskine karşı).
        var submittedBy = await _db.Scorecards.AsNoTracking().Where(s => s.InterviewId == id).Select(s => s.InterviewerEmployeeId).ToListAsync(ct);
        var panel = iv.AllInterviewers().ToList();
        if (panel.Count > 1 && submittedBy.Contains(me.Value) && panel.All(submittedBy.Contains))
            return Conflict(new { message = "Tüm görüşmeciler puan kartını gönderdi; değerlendirmeler artık değiştirilemez" });

        var sc = await _db.Scorecards.FirstOrDefaultAsync(s => s.InterviewId == id && s.InterviewerEmployeeId == me, ct);
        if (sc is null)
        {
            sc = new Scorecard { InterviewId = id, InterviewerEmployeeId = me.Value };
            _db.Scorecards.Add(sc);
        }
        sc.Scores = scores;
        sc.OverallScore = overall;
        sc.Recommendation = string.IsNullOrWhiteSpace(body.Recommendation) ? null : body.Recommendation;
        sc.Notes = string.IsNullOrWhiteSpace(body.Notes) ? null : body.Notes.Trim();
        sc.SubmittedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new
        {
            sc.Id, sc.InterviewId, sc.InterviewerEmployeeId, sc.Scores, sc.OverallScore, sc.Recommendation, sc.Notes, sc.SubmittedAt,
            warnings = SensitiveNoteDetector.Detect(string.Join("\n", new[] { body.Notes }.Concat(scores.Select(x => x.Evidence)).Where(x => !string.IsNullOrEmpty(x)))),
        });
    }

    /// <summary>Mülakatın tüm puan kartları ve ortalama (yönetici+ / İK).</summary>
    [HttpGet("{id:guid}/scorecards")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> List(Guid id, CancellationToken ct)
    {
        var iv = await _db.Interviews.AsNoTracking().Include(i => i.Application).FirstOrDefaultAsync(i => i.Id == id, ct);
        if (iv is null) return NotFound(new { message = "Mülakat bulunamadı" });
        var cards = await _db.Scorecards.AsNoTracking().Where(s => s.InterviewId == id).OrderBy(s => s.SubmittedAt).ToListAsync(ct);
        var criteria = await CriteriaAsync(iv.Application!.JobPostingId, ct);
        // Dalga 11 (77): kör değerlendirme — görüşmeci (yönetici de olsa) tüm panel gönderene kadar
        // başkalarının kartını göremez; yalnızca kendi kartı ve kaç kişinin gönderdiği görünür.
        var me = await MeAsync(ct);
        var panel = iv.AllInterviewers().ToList();
        var submittedIds = cards.Select(c => c.InterviewerEmployeeId).ToHashSet();
        var allSubmitted = panel.Count > 0 && panel.All(submittedIds.Contains);
        var blind = ScorecardRules.IsBlind(panel, submittedIds, me);
        if (blind) cards = cards.Where(c => c.InterviewerEmployeeId == me).ToList();
        var people = (await _db.PeopleAsync(iv.TenantSlug, cards.Select(c => c.InterviewerEmployeeId).Distinct().ToList(), ct))
            .ToDictionary(p => p.Id, p => $"{p.FirstName} {p.LastName}");
        var consistency = blind ? null : ScorecardConsistency.Compute(criteria, cards.Select(c => ((IReadOnlyList<CriterionScore>)c.Scores, c.Recommendation)).ToList());
        return Ok(new
        {
            blind,
            allSubmitted,
            submittedCount = submittedIds.Count,
            panelSize = panel.Count,
            consistency,
            criteria,
            average = blind || cards.Count(c => c.OverallScore != null) == 0 ? (decimal?)null
                : Math.Round(cards.Where(c => c.OverallScore != null).Average(c => c.OverallScore!.Value), 2),
            pending = panel.Where(p => !submittedIds.Contains(p)).ToList(),
            scorecards = cards.Select(c => new
            {
                c.Id, c.InterviewerEmployeeId, interviewer = people.GetValueOrDefault(c.InterviewerEmployeeId),
                c.Scores, c.OverallScore, c.Recommendation, c.Notes, c.SubmittedAt,
                warnings = SensitiveNoteDetector.Detect(string.Join("\n", new[] { c.Notes }.Concat(c.Scores.Select(x => x.Evidence)).Where(x => !string.IsNullOrEmpty(x)))),
            }),
        });
    }

    public record NotesCheck(string? Text);

    /// <summary>Not yazılırken canlı uyarı (kayıt yapmaz).</summary>
    [HttpPost("notes-check")]
    public IActionResult Check([FromBody] NotesCheck body) =>
        Ok(new { warnings = SensitiveNoteDetector.Detect(body.Text?.Length > 10000 ? body.Text[..10000] : body.Text) });

    // ------------------------------------------------------------- şablon (ilan başına)

    [HttpGet("/api/job-postings/{postingId:guid}/scorecard-template")]
    public async Task<IActionResult> GetTemplate(Guid postingId, CancellationToken ct)
    {
        if (!await _db.JobPostings.AnyAsync(p => p.Id == postingId, ct)) return NotFound(new { message = "İlan bulunamadı" });
        var t = await _db.ScorecardTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.JobPostingId == postingId, ct);
        return Ok(new { criteria = t?.Criteria is { Count: > 0 } c ? c : ScorecardRules.DefaultCriteria(), isDefault = t is null, t?.UpdatedAt });
    }

    public record TemplateBody(List<ScorecardCriterion>? Criteria);

    [HttpPut("/api/job-postings/{postingId:guid}/scorecard-template")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> PutTemplate(Guid postingId, [FromBody] TemplateBody body, CancellationToken ct)
    {
        if (!await _db.JobPostings.AnyAsync(p => p.Id == postingId, ct)) return NotFound(new { message = "İlan bulunamadı" });
        var criteria = (body.Criteria ?? new()).Select(c => c with { Key = (c.Key ?? "").Trim(), Label = (c.Label ?? "").Trim() }).ToList();
        var error = ScorecardRules.Validate(criteria);
        if (error is not null) return BadRequest(new { message = error });
        var t = await _db.ScorecardTemplates.FirstOrDefaultAsync(x => x.JobPostingId == postingId, ct);
        if (t is null)
        {
            t = new ScorecardTemplate { JobPostingId = postingId };
            _db.ScorecardTemplates.Add(t);
        }
        t.Criteria = criteria;
        t.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { criteria = t.Criteria, isDefault = false, t.UpdatedAt });
    }
}

using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PerformanceService.Data;
using PerformanceService.Models;
using PerformanceService.Security;
using PerformanceService.Services;
using PerformanceService.Tenancy;

namespace PerformanceService.Controllers;

/// <summary>
/// Dalga 11 / 81: anonim 360 derece geri bildirim.
///  * İK (herkes için) ya da departman başı (yönettiği çalışan için) talep açar ve en az 5 değerlendiren seçer.
///  * Katılım (kim davet edildi / yanıtladı mı) performance_f360_participants'ta; yanıtlar
///    performance_f360_responses'ta değerlendiren kimliği, ilişki türü ve zaman damgası OLMADAN tutulur.
///    Yanıt ham SQL ile yazılır: EF ile yazılsaydı denetim kaydı (audit_log) yanıtı kullanıcıyla eşleştirirdi.
///  * Sonuçlar (yetkinlik ortalaması, dağılım, yorumlar) yalnızca kapanmış ve ≥5 yanıtlı talepte gösterilir
///    (F360Rules); görüntüleme denetim kaydına yazılır. Kişi sonuçlarını İK paylaşırsa görür.
///  * KVKK: yorumlarda özel nitelikli veri yazılmaması için arayüzde uyarı (PiiHint) gösterilir; otomatik karar yok.
/// </summary>
[ApiController]
[Route("api/feedback360")]
[Authorize]
public class Feedback360Controller : ControllerBase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly PerformanceDbContext _db;
    private readonly PerfPeople _people;
    private readonly ITenantContext _tenant;

    public Feedback360Controller(PerformanceDbContext db, PerfPeople people, ITenantContext tenant)
    {
        _db = db; _people = people; _tenant = tenant;
    }

    private bool IsHr => User.IsHr();

    /// <summary>Talebi yönetebilir mi: İK, talebi açan ya da kişinin departman başı.</summary>
    private async Task<bool> CanManageAsync(F360Request r, CancellationToken ct)
    {
        if (IsHr) return true;
        var me = await _people.MeAsync(ct);
        if (me is null || me.Id == r.SubjectEmployeeId) return false;
        if (r.CreatedByEmployeeId == me.Id) return true;
        var subject = await _people.FindAsync(r.SubjectEmployeeId, ct);
        return subject?.HeadId == me.Id;
    }

    // ------------------------------------------------------------ değerlendiren

    /// <summary>Bana gelen davetler (açık talepler + yanıtladıklarım).</summary>
    [HttpGet("mine")]
    public async Task<IActionResult> Mine(CancellationToken ct)
    {
        var me = await _people.MeAsync(ct);
        if (me is null) return Ok(Array.Empty<object>());
        var rows = await (from p in _db.F360Participants.AsNoTracking()
                          join r in _db.F360Requests.AsNoTracking() on p.RequestId equals r.Id
                          where p.ReviewerEmployeeId == me.Id && r.Status == "Open"
                          orderby r.CreatedAt descending
                          select new { r, p.Submitted, p.Relationship }).Take(100).ToListAsync(ct);
        var names = (await _people.ActiveAsync(ct)).ToDictionary(x => x.Id, x => x.FullName);
        return Ok(rows.Select(x => new
        {
            x.r.Id, x.r.Title, subjectName = names.GetValueOrDefault(x.r.SubjectEmployeeId), x.r.DueDate, competencies = x.r.Competencies,
            submitted = x.Submitted, relationship = x.Relationship,
        }));
    }

    public record RespondInput(Dictionary<string, int>? Ratings, string? Comment);

    [HttpPost("{id:guid}/respond")]
    public async Task<IActionResult> Respond(Guid id, [FromBody] RespondInput body, CancellationToken ct)
    {
        var me = await _people.MeAsync(ct);
        if (me is null) return StatusCode(403, new { message = "Çalışan kaydınız bulunamadı" });
        var r = await _db.F360Requests.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return NotFound(new { message = "360 talebi bulunamadı" });
        if (r.Status != "Open") return Conflict(new { message = "Talep kapanmış; yanıt verilemez" });
        var ratings = body.Ratings ?? new();
        var comment = string.IsNullOrWhiteSpace(body.Comment) ? null : body.Comment.Trim();
        var error = F360Rules.ValidateRatings(r.Competencies, ratings, comment);
        if (error is not null) return BadRequest(new { message = error });
        var tenant = _tenant.TenantSlug ?? "";

        // Önce katılım işaretlenir (çift yanıtı önler), sonra kimliksiz yanıt ayrı komutla yazılır.
        // İkisi de ham SQL: denetim kaydına (kullanıcı + zaman) yanıt içeriği düşmez.
        var marked = await _db.Database.ExecuteSqlRawAsync(
            "UPDATE performance_f360_participants SET \"Submitted\" = true WHERE \"TenantSlug\" = {0} AND \"RequestId\" = {1} AND \"ReviewerEmployeeId\" = {2} AND NOT \"Submitted\"",
            new object[] { tenant, id, me.Id }, ct);
        if (marked == 0)
        {
            var invited = await _db.F360Participants.AnyAsync(p => p.RequestId == id && p.ReviewerEmployeeId == me.Id, ct);
            return invited ? Conflict(new { message = "Bu talebi zaten yanıtladınız" }) : StatusCode(403, new { message = "Bu talebe davetli değilsiniz" });
        }
        try
        {
            // Yorum yoksa boş metin gönderilir ve NULLIF ile NULL yazılır: EF ham SQL'i DBNull/null parametreye
            // tür eşlemesi bulamaz (InvalidOperationException).
            await _db.Database.ExecuteSqlRawAsync(
                "INSERT INTO performance_f360_responses (\"Id\",\"TenantSlug\",\"RequestId\",\"RatingsJson\",\"Comment\") VALUES ({0},{1},{2},{3}::jsonb,NULLIF({4}, ''))",
                new object[] { Guid.NewGuid(), tenant, id, JsonSerializer.Serialize(ratings, Json), comment ?? "" }, ct);
        }
        catch (Exception e) when (e is Npgsql.PostgresException or InvalidOperationException or DbUpdateException)
        {
            // Yanıt yazılamadı: katılım işareti geri alınır, kişi yeniden deneyebilir (500 yerine 409).
            await _db.Database.ExecuteSqlRawAsync(
                "UPDATE performance_f360_participants SET \"Submitted\" = false WHERE \"TenantSlug\" = {0} AND \"RequestId\" = {1} AND \"ReviewerEmployeeId\" = {2}",
                new object[] { tenant, id, me.Id }, CancellationToken.None);
            return Conflict(new { message = "Yanıtınız kaydedilemedi; lütfen yeniden deneyin" });
        }
        return Ok(new { submitted = true });
    }

    // ------------------------------------------------------------ yönetim

    public record ReviewerInput(Guid EmployeeId, string? Relationship);
    public record CreateInput(Guid SubjectEmployeeId, string? Title, Guid? CycleId, DateOnly? DueDate,
        List<F360Competency>? Competencies, List<ReviewerInput>? Reviewers);

    [HttpPost]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> Create([FromBody] CreateInput body, CancellationToken ct)
    {
        var subject = await _people.FindAsync(body.SubjectEmployeeId, ct);
        if (subject is null) return NotFound(new { message = "Çalışan bulunamadı" });
        var me = await _people.MeAsync(ct);
        if (me?.Id == subject.Id) return StatusCode(403, new { message = "Kendiniz için 360 talebi açamazsınız" });
        if (!IsHr && (me is null || subject.HeadId != me.Id))
            return StatusCode(403, new { message = "Yalnızca yönettiğiniz departmandaki çalışanlar için 360 talebi açabilirsiniz" });
        var title = body.Title?.Trim() ?? "";
        if (title.Length is < 3 or > 200) return BadRequest(new { message = "Başlık 3-200 karakter olmalı" });
        if (body.DueDate is { } due && due < DateOnly.FromDateTime(DateTime.UtcNow)) return BadRequest(new { message = "Son tarih geçmişte olamaz" });
        if (body.CycleId is { } cid && !await _db.Cycles.AnyAsync(c => c.Id == cid, ct)) return NotFound(new { message = "Dönem bulunamadı" });
        var comps = (body.Competencies is { Count: > 0 } c0 ? c0 : F360Rules.DefaultCompetencies())
            .Select(c => new F360Competency((c.Key ?? "").Trim(), (c.Label ?? "").Trim())).ToList();
        var cErr = F360Rules.ValidateCompetencies(comps);
        if (cErr is not null) return BadRequest(new { message = cErr });
        var reviewers = (body.Reviewers ?? new()).Select(r => (r.EmployeeId, r.Relationship ?? "Peer")).ToList();
        var rErr = F360Rules.ValidateReviewers(subject.Id, reviewers);
        if (rErr is not null) return BadRequest(new { message = rErr });
        var active = (await _people.ActiveAsync(ct)).Select(p => p.Id).ToHashSet();
        if (reviewers.Any(r => !active.Contains(r.EmployeeId))) return BadRequest(new { message = "Değerlendirenlerden biri bulunamadı" });

        var req = new F360Request
        {
            SubjectEmployeeId = subject.Id, Title = title, CycleId = body.CycleId, DueDate = body.DueDate, Competencies = comps,
            CreatedByEmployeeId = me?.Id, CreatedBy = PerfPeople.DisplayName(User),
        };
        _db.F360Requests.Add(req);
        foreach (var (eid, rel) in reviewers)
            _db.F360Participants.Add(new F360Participant { RequestId = req.Id, ReviewerEmployeeId = eid, Relationship = rel });
        await _db.SaveChangesAsync(ct);
        return Ok(new { req.Id, reviewers = reviewers.Count });
    }

    /// <summary>Yönettiğim talepler (İK: hepsi; yönetici: açtıkları ve departmanındakiler).</summary>
    [HttpGet("managed")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> Managed(CancellationToken ct)
    {
        var me = await _people.MeAsync(ct);
        var people = await _people.ActiveAsync(ct);
        var names = people.ToDictionary(x => x.Id, x => x.FullName);
        var q = _db.F360Requests.AsNoTracking().AsQueryable();
        if (!IsHr)
        {
            if (me is null) return Ok(Array.Empty<object>());
            var mine = people.Where(p => p.HeadId == me.Id && p.Id != me.Id).Select(p => p.Id).ToList();
            q = q.Where(r => r.SubjectEmployeeId != me.Id && (r.CreatedByEmployeeId == me.Id || mine.Contains(r.SubjectEmployeeId)));
        }
        var rows = await q.OrderByDescending(r => r.CreatedAt).Take(200).ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToList();
        var counts = await _db.F360Participants.AsNoTracking().Where(p => ids.Contains(p.RequestId))
            .GroupBy(p => p.RequestId).Select(g => new { g.Key, invited = g.Count(), submitted = g.Count(p => p.Submitted) })
            .ToDictionaryAsync(x => x.Key, ct);
        return Ok(rows.Select(r => new
        {
            r.Id, r.Title, r.SubjectEmployeeId, subjectName = names.GetValueOrDefault(r.SubjectEmployeeId), r.Status, r.DueDate,
            r.ReleasedToSubject, r.CreatedBy, r.CreatedAt, r.ClosedAt, r.MinResponses,
            invited = counts.GetValueOrDefault(r.Id)?.invited ?? 0, submitted = counts.GetValueOrDefault(r.Id)?.submitted ?? 0,
        }));
    }

    /// <summary>Talep ayrıntısı ve katılım (kim yanıtladı — hatırlatma için; yanıt içeriğiyle ilişkisi yok).</summary>
    [HttpGet("{id:guid}")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var r = await _db.F360Requests.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return NotFound(new { message = "360 talebi bulunamadı" });
        if (!await CanManageAsync(r, ct)) return Forbid();
        var parts = await _db.F360Participants.AsNoTracking().Where(p => p.RequestId == id).ToListAsync(ct);
        var names = (await _people.ActiveAsync(ct)).ToDictionary(x => x.Id, x => x.FullName);
        return Ok(new
        {
            r.Id, r.Title, r.SubjectEmployeeId, subjectName = names.GetValueOrDefault(r.SubjectEmployeeId), r.Status, r.DueDate,
            r.ReleasedToSubject, r.CreatedBy, r.CreatedAt, r.ClosedAt, r.MinResponses, competencies = r.Competencies, canRelease = IsHr,
            participants = parts.OrderBy(p => names.GetValueOrDefault(p.ReviewerEmployeeId)).Select(p => new
            {
                p.ReviewerEmployeeId, name = names.GetValueOrDefault(p.ReviewerEmployeeId), p.Relationship, p.Submitted,
            }),
        });
    }

    [HttpPost("{id:guid}/close")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> Close(Guid id, CancellationToken ct)
    {
        var r = await _db.F360Requests.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return NotFound(new { message = "360 talebi bulunamadı" });
        if (!await CanManageAsync(r, ct)) return Forbid();
        if (r.Status != "Open") return Conflict(new { message = "Talep zaten kapalı" });
        r.Status = "Closed";
        r.ClosedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { r.Status, r.ClosedAt });
    }

    public record ReleaseInput(bool Released);

    /// <summary>İK, kapanmış talebin sonuçlarını kişiyle paylaşır (varsayılan: paylaşılmaz).</summary>
    [HttpPost("{id:guid}/release")]
    public async Task<IActionResult> Release(Guid id, [FromBody] ReleaseInput body, CancellationToken ct)
    {
        if (!IsHr) return StatusCode(403, new { message = "Sonuçları yalnızca İK paylaşabilir" });
        var r = await _db.F360Requests.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return NotFound(new { message = "360 talebi bulunamadı" });
        if (r.Status != "Closed") return Conflict(new { message = "Önce talebi kapatın" });
        r.ReleasedToSubject = body.Released;
        await _db.SaveChangesAsync(ct);
        await _people.AuditAsync("Feedback360", id.ToString(), body.Released ? "Published" : "Unpublished", new { subjectEmployeeId = r.SubjectEmployeeId });
        return Ok(new { r.ReleasedToSubject });
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var r = await _db.F360Requests.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return NotFound(new { message = "360 talebi bulunamadı" });
        if (!await CanManageAsync(r, ct)) return Forbid();
        // Toplu silme (FK cascade): yanıtlar EF denetim kaydına düşmez.
        await _db.F360Requests.Where(x => x.Id == id).ExecuteDeleteAsync(ct);
        await _people.AuditAsync("Feedback360", id.ToString(), "Deleted", new { subjectEmployeeId = r.SubjectEmployeeId, r.Title });
        return NoContent();
    }

    // ------------------------------------------------------------ sonuçlar

    [HttpGet("{id:guid}/results")]
    public async Task<IActionResult> Results(Guid id, CancellationToken ct)
    {
        var r = await _db.F360Requests.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return NotFound(new { message = "360 talebi bulunamadı" });
        var me = await _people.MeAsync(ct);
        var isSubject = me?.Id == r.SubjectEmployeeId;
        if (isSubject ? !r.ReleasedToSubject : !(User.IsManagerOrAbove() && await CanManageAsync(r, ct))) return Forbid();
        var rows = await _db.F360Responses.AsNoTracking().Where(x => x.RequestId == id).Select(x => new { x.RatingsJson, x.Comment }).ToListAsync(ct);
        var responses = rows.Select(x => ((IReadOnlyDictionary<string, int>)(JsonSerializer.Deserialize<Dictionary<string, int>>(x.RatingsJson, Json) ?? new()), x.Comment)).ToList();
        var result = F360Rules.Aggregate(r.Competencies, responses, r.MinResponses, r.Status == "Closed");
        if (!result.Hidden)
            await _people.AuditAsync("Feedback360", id.ToString(), "SensitiveViewed", new { field = "feedback360Results", subjectEmployeeId = r.SubjectEmployeeId, self = isSubject });
        var names = await _people.FindAsync(r.SubjectEmployeeId, ct);
        return Ok(new
        {
            r.Id, r.Title, subjectName = names?.FullName, r.Status, closed = r.Status == "Closed",
            hidden = result.Hidden, responses = result.Responses, threshold = result.Threshold,
            competencies = result.Competencies, comments = result.Comments,
            notice = "Sonuçlar anonimdir: değerlendiren kimliği yanıtlarla birlikte saklanmaz. 5'ten az yanıt varsa sonuç gösterilmez. Bu sonuçlar gelişim içindir; otomatik karar üretmez.",
        });
    }

    /// <summary>Kişinin kendisiyle paylaşılan 360 sonuçları.</summary>
    [HttpGet("my-results")]
    public async Task<IActionResult> MyResults(CancellationToken ct)
    {
        var me = await _people.MeAsync(ct);
        if (me is null) return Ok(Array.Empty<object>());
        var rows = await _db.F360Requests.AsNoTracking().Where(r => r.SubjectEmployeeId == me.Id && r.ReleasedToSubject && r.Status == "Closed")
            .OrderByDescending(r => r.ClosedAt).Select(r => new { r.Id, r.Title, r.ClosedAt }).ToListAsync(ct);
        return Ok(rows);
    }
}

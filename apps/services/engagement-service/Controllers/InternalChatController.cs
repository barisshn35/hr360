using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using EngagementService.Data;
using EngagementService.Infrastructure;
using EngagementService.Models;
using EngagementService.Tenancy;

namespace EngagementService.Controllers;

/* ======================================================================
 * Sohbet botunun (governance-service, Slack/Teams) engagement verisine
 * yazdığı işlemler. Bot artık engagement tablolarına doğrudan SQL yazmaz;
 * bu iç uçları çağırır, böylece kurallar (rozet, kendine takdir, çakışma,
 * anonimlik) ve denetim kaydı tek yerde kalır. Çalışan kimliği botun
 * doğruladığı sohbet hesabından gelir. Gateway /api/<servis>/internal/
 * yollarını dışarıya kapatır; X-Internal-Token (INTERNAL_SERVICE_TOKEN)
 * tutmazsa ya da tanımlı değilse uç yokmuş gibi 404 döner.
 *
 * Denetim: varlık değişiklikleri AuditInterceptor ile yazılır; istek süresince
 * kullanıcı, botun bildirdiği çalışan olarak ayarlanır ve UserName'e
 * "· Sohbet (<platform>)" eklenir (işlemin sohbetten geldiği görünür).
 * ==================================================================== */
[AllowAnonymous]
public class InternalChatController : AppController
{
    private readonly EngagementDbContext _db;
    private readonly TenantContext _tenant;
    public InternalChatController(EngagementDbContext db, TenantContext tenant) { _db = db; _tenant = tenant; }

    /// <summary>Sabit zamanlı anahtar karşılaştırması; beklenen anahtar boşsa her zaman false.</summary>
    public static bool TokenMatches(string? expected, string? given) =>
        Security.InternalServiceToken.Matches(given, expected, null);

    /// <summary>Anahtar ve kiracıyı denetler; hata varsa döndürülecek yanıtı verir.</summary>
    private IActionResult? Enter(string? tenantSlug)
    {
        if (!Security.InternalServiceToken.Matches(Request.Headers[Security.InternalServiceToken.Header].FirstOrDefault()))
            return NotFound();
        if (string.IsNullOrWhiteSpace(tenantSlug)) return BadRequest(new { message = "Kiracı belirtilmedi", code = "tenant" });
        _tenant.TenantSlug = tenantSlug.Trim();
        _tenant.IsPlatformAdmin = false;
        return null;
    }

    public static string ChatUserName(string name, string? platform) =>
        $"{name} · Sohbet{(string.IsNullOrWhiteSpace(platform) ? "" : $" ({platform.Trim()[..Math.Min(platform.Trim().Length, 20)]})")}";

    /// <summary>Denetim satırlarının (AuditInterceptor) kullanıcısı: botun doğruladığı çalışan.</summary>
    private void ActAs(string userId, string userName) =>
        HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", userId), new Claim("name", userName) }, "internal-chat"));

    private async Task<(Person? Person, Actor? Actor)> EmployeeAsync(Guid employeeId, string? platform, CancellationToken ct)
    {
        var p = employeeId == Guid.Empty ? null : await People.FindAsync(Tenant, employeeId, ct);
        if (p is null) return (null, null);
        var actor = new Actor(p.UserId ?? $"employee:{p.Id}", p.Id, p.Name);
        ActAs(actor.UserId, ChatUserName(p.Name, platform));
        return (p, actor);
    }

    private static IActionResult EmployeeMissing() =>
        new NotFoundObjectResult(new { message = "Çalışan kaydı bulunamadı.", code = "employee" });

    /* ------------------------------------------------------------- takdir */

    public record ChatKudosInput(string? TenantSlug, Guid EmployeeId, Guid ToEmployeeId, string? Message, string? Badge, string? Platform);

    /// <summary>"teşekkür @kişi mesaj" → takdir duvarı (web ile aynı kurallar; rozet varsayılanı "thanks").</summary>
    [HttpPost("/api/internal/chat/kudos")]
    public async Task<IActionResult> CreateKudos([FromBody] ChatKudosInput body, [FromServices] Notifier notify, CancellationToken ct)
    {
        if (Enter(body.TenantSlug) is { } fail) return fail;
        var (_, from) = await EmployeeAsync(body.EmployeeId, body.Platform, ct);
        if (from is null) return EmployeeMissing();
        var (error, k) = await KudosController.CreateCoreAsync(_db, notify, People, Tenant, from,
            new KudosController.CreateKudos(body.ToEmployeeId, string.IsNullOrWhiteSpace(body.Badge) ? "thanks" : body.Badge.Trim(), (body.Message ?? "").Trim()), ct);
        if (error is not null) return error.ToResult();
        return Ok(new { id = k!.Id, fromName = k.FromName, toEmployeeId = k.ToEmployeeId, toName = k.ToName, badge = k.Badge,
            badgeLabel = KudosController.Badges[k.Badge] });
    }

    /* ------------------------------------------------------------- masa */

    public record ChatDeskBookInput(string? TenantSlug, Guid EmployeeId, Guid DeskId, DateOnly Date, int? StartMinute, int? EndMinute, string? Platform);

    /// <summary>Sohbetten masa (yalnızca Kind=Desk) ayırma; varsayılan 09:00–18:00. Masa ayıran o gün ofiste görünür.</summary>
    [HttpPost("/api/internal/chat/desk-book")]
    public async Task<IActionResult> DeskBook([FromBody] ChatDeskBookInput body, [FromServices] AppCache cache, CancellationToken ct)
    {
        if (Enter(body.TenantSlug) is { } fail) return fail;
        var (_, actor) = await EmployeeAsync(body.EmployeeId, body.Platform, ct);
        if (actor is null) return EmployeeMissing();
        var (error, b, desk) = await WorkplaceController.BookCoreAsync(_db, actor,
            new WorkplaceController.BookingInput(body.DeskId, body.Date, body.StartMinute ?? 540, body.EndMinute ?? 1080, null), "Desk", ct);
        if (error is not null) return error.ToResult();
        await cache.BumpAsync("presence", Tenant);
        return Ok(new { id = b!.Id, deskId = desk!.Id, code = desk.Code, name = desk.Name, date = b.Date, startMinute = b.StartMinute, endMinute = b.EndMinute });
    }

    public record ChatDeskCancelInput(string? TenantSlug, Guid EmployeeId, Guid BookingId, string? Platform);

    /// <summary>Çalışanın kendi rezervasyonunu iptal eder (başkasınınki "bulunamadı" sayılır).</summary>
    [HttpPost("/api/internal/chat/desk-cancel")]
    public async Task<IActionResult> DeskCancel([FromBody] ChatDeskCancelInput body, CancellationToken ct)
    {
        if (Enter(body.TenantSlug) is { } fail) return fail;
        var (_, actor) = await EmployeeAsync(body.EmployeeId, body.Platform, ct);
        if (actor is null) return EmployeeMissing();
        var status = await WorkplaceController.CancelCoreAsync(_db, body.BookingId,
            b => b.EmployeeId == actor.EmployeeId || b.UserId == actor.UserId, ct);
        return status == StatusCodes.Status204NoContent
            ? Ok(new { ok = true })
            : NotFound(new { message = "Rezervasyon bulunamadı.", code = "not_found" });
    }

    /* ------------------------------------------------------------- nabız anketi */

    /// <summary>Sohbet nabzının tek sorusu (bot yanıtları bu kimlikle gönderir).</summary>
    public const string PulseQuestionId = "pulse";

    public record ChatPulseSurveyInput(string? TenantSlug, string? Question, DateTime? SendAt, DateTime? ClosesAt, string? CreatedBy);

    /// <summary>İK'nın sohbet nabzı: tek soruluk (Scale 1–5), anonim, hemen açık "Pulse" anketi.</summary>
    [HttpPost("/api/internal/chat/pulse-survey")]
    public async Task<IActionResult> PulseSurvey([FromBody] ChatPulseSurveyInput body, CancellationToken ct)
    {
        if (Enter(body.TenantSlug) is { } fail) return fail;
        var q = (body.Question ?? "").Trim();
        if (q.Length is < 5 or > 300) return BadRequest(new { message = "Soru 5–300 karakter olmalı.", code = "question_length" });
        var send = body.SendAt is { } s ? DateTime.SpecifyKind(s, DateTimeKind.Utc) : DateTime.UtcNow;
        var survey = BuildPulseSurvey(q, send, body.ClosesAt is { } c ? DateTime.SpecifyKind(c, DateTimeKind.Utc) : null, body.CreatedBy);
        if (SurveysController.ValidateSurvey(survey.Title, survey.Questions) is { } invalid) return invalid.ToResult();
        ActAs("chat-bot", ChatUserName(string.IsNullOrWhiteSpace(body.CreatedBy) ? "İK" : body.CreatedBy.Trim(), null));
        _db.Surveys.Add(survey);
        await _db.SaveChangesAsync(ct);
        return Ok(new { id = survey.Id });
    }

    public static Survey BuildPulseSurvey(string question, DateTime sendAtUtc, DateTime? closesAtUtc, string? createdBy) => new()
    {
        Title = $"Sohbet nabzı — {sendAtUtc.AddHours(3):dd.MM.yyyy}",
        Description = "Sohbet botuyla gönderilen tek soruluk anonim nabız anketi.",
        Kind = "Pulse", IsAnonymous = true, Status = "Open", ClosesAt = closesAtUtc,
        CreatedByName = string.IsNullOrWhiteSpace(createdBy) ? null : createdBy.Trim()[..Math.Min(createdBy.Trim().Length, 200)],
        Questions = new() { new SurveyQuestion { Id = PulseQuestionId, Text = question, Type = "Scale", Options = new(), Required = true } },
    };

    public record ChatPulseCloseInput(string? TenantSlug, Guid SurveyId);

    /// <summary>Süresi dolan / İK'nın kapattığı sohbet nabzının anketini yanıta kapatır (yalnızca Pulse türü).</summary>
    [HttpPost("/api/internal/chat/pulse-close")]
    public async Task<IActionResult> PulseClose([FromBody] ChatPulseCloseInput body, CancellationToken ct)
    {
        if (Enter(body.TenantSlug) is { } fail) return fail;
        var s = await _db.Surveys.FirstOrDefaultAsync(x => x.Id == body.SurveyId && x.Kind == "Pulse", ct);
        if (s is null) return NotFound(new { message = "Anket bulunamadı.", code = "not_found" });
        if (s.Status != "Closed")
        {
            ActAs("chat-bot", "Sohbet botu (nabız kapanışı)");
            s.Status = "Closed";
            await _db.SaveChangesAsync(ct);
        }
        return Ok(new { id = s.Id, status = s.Status });
    }

    public record ChatPulseAnswerInput(string? TenantSlug, Guid SurveyId, int? Score, List<SurveyAnswer>? Answers);

    /// <summary>
    /// Sohbetten nabız yanıtı. Anonimlik (botun önceki davranışıyla birebir): çalışan kimliği bu uca
    /// GÖNDERİLMEZ; yanıt rastgele bir anahtarla ("chat-" + 128 bit), departmansız (küçük grup kırılımına
    /// girmez) ve gün hassasiyetinde zamanla saklanır. Tekrarı bot kendi "yanıtladı" tablosuyla önler.
    /// SurveyResponse denetime yazılmaz (AuditInterceptor SkipTypes).
    /// </summary>
    [HttpPost("/api/internal/chat/pulse-answer")]
    public async Task<IActionResult> PulseAnswer([FromBody] ChatPulseAnswerInput body, CancellationToken ct)
    {
        if (Enter(body.TenantSlug) is { } fail) return fail;
        var s = await _db.Surveys.AsNoTracking().FirstOrDefaultAsync(x => x.Id == body.SurveyId, ct);
        if (s is null) return NotFound(new { message = "Anket bulunamadı.", code = "not_found" });
        if (!s.IsAnonymous) return BadRequest(new { message = "Sohbetten yalnızca anonim anketler yanıtlanabilir.", code = "not_anonymous" });
        if (SurveysController.CheckOpen(s, DateTime.UtcNow) is { } closed) return closed.ToResult();
        var given = body.Answers ?? (body.Score is { } score
            ? new List<SurveyAnswer> { new() { QuestionId = s.Questions.FirstOrDefault()?.Id ?? PulseQuestionId, Score = score } }
            : new List<SurveyAnswer>());
        var (invalid, answers) = SurveysController.ValidateAnswers(s, given);
        if (invalid is not null) return invalid.ToResult();
        _db.SurveyResponses.Add(AnonymousResponse(s.Id, answers, DateTime.UtcNow));
        await _db.SaveChangesAsync(ct);
        return Ok(new { ok = true });
    }

    /// <summary>Kimliksiz yanıt: rastgele anahtar, departman yok, gün hassasiyetinde tarih.</summary>
    public static SurveyResponse AnonymousResponse(Guid surveyId, List<SurveyAnswer> answers, DateTime nowUtc) => new()
    {
        SurveyId = surveyId,
        RespondentKey = "chat-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
        DepartmentName = null,
        Answers = answers,
        SubmittedAt = DateTime.SpecifyKind(nowUtc.Date, DateTimeKind.Utc),
    };

    /* ------------------------------------------------------------- çıkış anketi */

    public record ChatExitInterviewInput(string? TenantSlug, Guid CaseId, Guid EmployeeId, ExitInterview? Answers, string? Platform);

    /// <summary>
    /// Ayrılan çalışanın sohbetten doldurduğu çıkış anketi: yalnızca kendi AÇIK ayrılış kaydına ve
    /// İK henüz görüşme girmediyse yazılır (İK'nın girdiği görüşmenin üzerine yazılmaz → 409 "exists").
    /// </summary>
    [HttpPost("/api/internal/chat/exit-interview")]
    public async Task<IActionResult> SaveExitInterview([FromBody] ChatExitInterviewInput body, [FromServices] Sql sql, CancellationToken ct)
    {
        if (Enter(body.TenantSlug) is { } fail) return fail;
        if (OffboardingController.ValidateSelfInterview(body.Answers) is { } invalid) return invalid.ToResult();
        var c = await _db.OffboardingCases.FirstOrDefaultAsync(x => x.Id == body.CaseId && x.EmployeeId == body.EmployeeId, ct);
        if (c is null || c.Status != "Open") return NotFound(new { message = "Açık ayrılış kaydı bulunamadı.", code = "not_found" });
        if (c.ExitInterview is not null) return Conflict(new { message = "Çıkış görüşmesi zaten kayıtlı.", code = "exists" });
        var (_, actor) = await EmployeeAsync(body.EmployeeId, body.Platform, ct);
        var userId = actor?.UserId ?? $"employee:{body.EmployeeId}";
        var userName = ChatUserName(actor?.Name ?? c.EmployeeName, body.Platform);
        if (actor is null) ActAs(userId, userName);
        var a = body.Answers!;
        OffboardingController.ApplyInterview(_db, c, new ExitInterview
        {
            PrimaryReason = a.PrimaryReason!.Trim(), ManagerScore = a.ManagerScore, CultureScore = a.CultureScore, GrowthScore = a.GrowthScore,
            CompensationScore = a.CompensationScore, WouldRecommend = a.WouldRecommend, Comments = string.IsNullOrWhiteSpace(a.Comments) ? null : a.Comments.Trim(),
        }, "Çalışan (sohbet anketi)");
        await _db.SaveChangesAsync(ct);
        // Botun yazdığı eylem adıyla ayrıca bir satır (yanıt içerikleri yazılmaz).
        try
        {
            await sql.ExecuteAsync(
                "INSERT INTO audit_log (\"TenantSlug\",\"Service\",\"EntityType\",\"EntityId\",\"Action\",\"Changes\",\"UserId\",\"UserName\",\"CorrelationId\",\"IpAddress\",\"OccurredAt\") " +
                "VALUES ($1,'engagement-service','OffboardingCase',$2,'ExitSurveyCompleted',$3::jsonb,$4,$5,$6,NULL,now())", CancellationToken.None,
                Tenant, c.Id.ToString(), "{\"source\":\"chat\"}", userId, userName,
                Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? HttpContext.TraceIdentifier);
        }
        catch (Exception) { /* denetim yazılamazsa iş akışı bozulmaz */ }
        return Ok(new { ok = true, caseId = c.Id });
    }
}

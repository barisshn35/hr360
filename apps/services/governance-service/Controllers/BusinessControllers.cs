using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using GovernanceService.Data;
using GovernanceService.Infrastructure;
using GovernanceService.Models;
using GovernanceService.Tenancy;

namespace GovernanceService.Controllers;

/* ======================================================================
 * Takvim aboneliği (.ics): kişiye özel, gizli bağlantılı akış. Outlook /
 * Google Takvim / Apple Takvim "URL ile abone ol" ile eklenir; izinler,
 * resmî tatiller, 1:1'ler ve masa rezervasyonları görünür.
 * ==================================================================== */
[Route("api/calendar")]
[Authorize]
public class CalendarController : AppController
{
    private readonly GovernanceDbContext _db;
    public CalendarController(GovernanceDbContext db) => _db = db;

    private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant();

    [HttpGet("feed")]
    public async Task<IActionResult> Feed(CancellationToken ct)
    {
        var f = await _db.CalendarFeeds.FirstOrDefaultAsync(x => x.UserId == Me.UserId, ct);
        if (f is null)
        {
            var me = await MyPersonAsync(ct);
            f = new CalendarFeed { UserId = Me.UserId, EmployeeId = me?.Id, Token = NewToken() };
            _db.CalendarFeeds.Add(f);
            await _db.SaveChangesAsync(ct);
        }
        return Ok(new { path = $"/api/governance/calendar/ics/{f.Token}.ics", f.CreatedAt });
    }

    [HttpPost("feed/rotate")]
    public async Task<IActionResult> Rotate(CancellationToken ct)
    {
        var f = await _db.CalendarFeeds.FirstOrDefaultAsync(x => x.UserId == Me.UserId, ct);
        if (f is null) return await Feed(ct);
        f.Token = NewToken();
        f.CreatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { path = $"/api/governance/calendar/ics/{f.Token}.ics", f.CreatedAt });
    }

    [HttpGet("ics/{token}.ics")]
    [AllowAnonymous]
    public async Task<IActionResult> Ics(string token, CancellationToken ct)
    {
        var feed = (await Db.QueryAsync("SELECT \"TenantSlug\", \"UserId\", \"EmployeeId\" FROM governance_calendar_feeds WHERE \"Token\" = $1",
            r => (Tenant: r.GetString(0), User: r.GetString(1), Emp: r.GuidOrNull(2)), ct, token)).FirstOrDefault();
        if (feed.Tenant is null) return NotFound();
        var tenant = feed.Tenant;
        var from = DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-3);
        var to = from.AddMonths(15);
        var sb = new StringBuilder("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//HR360//Takvim//TR\r\nCALSCALE:GREGORIAN\r\nX-WR-CALNAME:HR360\r\nX-WR-TIMEZONE:Europe/Istanbul\r\n");
        void AllDay(string uid, DateOnly start, DateOnly endInclusive, string summary, string? desc = null)
        {
            sb.Append($"BEGIN:VEVENT\r\nUID:{uid}@hr360\r\nDTSTAMP:{DateTime.UtcNow:yyyyMMddTHHmmssZ}\r\nDTSTART;VALUE=DATE:{start:yyyyMMdd}\r\nDTEND;VALUE=DATE:{endInclusive.AddDays(1):yyyyMMdd}\r\nSUMMARY:{Esc(summary)}\r\n");
            if (desc is not null) sb.Append($"DESCRIPTION:{Esc(desc)}\r\n");
            sb.Append("TRANSP:TRANSPARENT\r\nEND:VEVENT\r\n");
        }
        void Timed(string uid, DateTime startUtc, DateTime endUtc, string summary)
            => sb.Append($"BEGIN:VEVENT\r\nUID:{uid}@hr360\r\nDTSTAMP:{DateTime.UtcNow:yyyyMMddTHHmmssZ}\r\nDTSTART:{startUtc:yyyyMMddTHHmmssZ}\r\nDTEND:{endUtc:yyyyMMddTHHmmssZ}\r\nSUMMARY:{Esc(summary)}\r\nEND:VEVENT\r\n");

        foreach (var h in await Db.QueryAsync("SELECT \"Id\", \"Date\", \"Name\" FROM leave_public_holidays WHERE \"TenantSlug\" = $1 AND \"Date\" BETWEEN $2 AND $3",
                     r => (Id: r.GetGuid(0), Date: r.GetFieldValue<DateOnly>(1), Name: r.GetString(2)), ct, tenant, from, to))
            AllDay(h.Id.ToString(), h.Date, h.Date, $"🇹🇷 {h.Name}");

        if (feed.Emp is { } emp)
        {
            foreach (var l in await Db.QueryAsync("""
                SELECT "Id", "StartDate", "EndDate", "Type", "Status", "Days" FROM leave_requests
                WHERE "TenantSlug" = $1 AND "EmployeeId" = $2 AND "Status" IN ('Approved','Submitted') AND "EndDate" >= $3
                """, r => (Id: r.GetGuid(0), S: r.GetFieldValue<DateOnly>(1), E: r.GetFieldValue<DateOnly>(2), T: r.GetString(3), St: r.GetString(4), D: r.GetDecimal(5)), ct, tenant, emp, from))
                AllDay(l.Id.ToString(), l.S, l.E, $"{(l.St == "Approved" ? "" : "(onay bekliyor) ")}İzin — {LeaveLabel(l.T)}", $"{l.D:0.#} gün");

            // Yöneticiyse: başı olduğu departmandaki ekip izinleri
            foreach (var l in await Db.QueryAsync("""
                SELECT l."Id", l."StartDate", l."EndDate", e."FirstName" || ' ' || e."LastName" FROM leave_requests l
                JOIN employee_employees e ON e."Id" = l."EmployeeId"
                JOIN employee_assignments a ON a."EmployeeId" = e."Id" AND a."EffectiveTo" IS NULL
                JOIN organization_departments d ON d."Id" = a."DepartmentId" AND d."HeadEmployeeId" = $2
                WHERE l."TenantSlug" = $1 AND l."Status" = 'Approved' AND l."EmployeeId" <> $2 AND l."EndDate" >= $3
                """, r => (Id: r.GetGuid(0), S: r.GetFieldValue<DateOnly>(1), E: r.GetFieldValue<DateOnly>(2), N: r.GetString(3)), ct, tenant, emp, from))
                AllDay("team-" + l.Id, l.S, l.E, $"Ekip izni: {l.N}");

            foreach (var b in await Db.QueryAsync("""
                SELECT b."Id", b."Date", b."StartMinute", b."EndMinute", d."Name" FROM engagement_desk_bookings b JOIN engagement_desks d ON d."Id" = b."DeskId"
                WHERE b."TenantSlug" = $1 AND b."UserId" = $2 AND b."Date" >= $3
                """, r => (Id: r.GetGuid(0), D: r.GetFieldValue<DateOnly>(1), S: r.GetInt32(2), E: r.GetInt32(3), N: r.GetString(4)), ct, tenant, feed.User, from))
            {
                var start = b.D.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddMinutes(b.S - 180);
                Timed(b.Id.ToString(), start, start.AddMinutes(b.E - b.S), $"Rezervasyon: {b.N}");
            }
        }
        foreach (var o in await Db.QueryAsync("""
            SELECT "Id", "ScheduledAt", "ManagerName", "EmployeeName" FROM engagement_one_on_ones
            WHERE "TenantSlug" = $1 AND ("ManagerUserId" = $2 OR "EmployeeUserId" = $2) AND "Status" = 'Planned'
            """, r => (Id: r.GetGuid(0), At: r.GetFieldValue<DateTime>(1), M: r.GetString(2), E: r.GetString(3)), ct, tenant, feed.User))
            Timed(o.Id.ToString(), o.At, o.At.AddMinutes(30), $"1:1 — {o.M} / {o.E}");

        sb.Append("END:VCALENDAR\r\n");
        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/calendar; charset=utf-8");
    }

    private static string LeaveLabel(string t) => t switch
    {
        "Annual" => "Yıllık", "Sick" => "Hastalık", "Unpaid" => "Ücretsiz", "Maternity" => "Doğum", "Paternity" => "Babalık",
        "Marriage" => "Evlilik", "Bereavement" => "Vefat", _ => t,
    };

    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace(",", "\\,").Replace(";", "\\;").Replace("\n", "\\n");
}

/* ======================================================================
 * İK asistanı + doğal dilde rapor + bilgi bankası.
 * Asistan: kişisel veriye dair sık soruları (izin bakiyem, bekleyen
 * taleplerim, sonraki resmî tatil…) doğrudan veriden, politika sorularını
 * bilgi bankasından (İK'nın yazdığı maddeler) yanıtlar. Yöneticinin
 * analitik soruları doğal dilde rapor motoruna (NlReport) gider.
 * Kural tabanlıdır; bilmediğini uydurmaz.
 * ==================================================================== */
[Route("api/insights")]
[Authorize]
public class InsightsController : AppController
{
    private readonly GovernanceDbContext _db;
    public InsightsController(GovernanceDbContext db) => _db = db;

    public record AskInput(string Question);
    /// <summary>G3: isteğe bağlı süzgeçler (departman, tarih aralığı, geçen yılla karşılaştırma).</summary>
    public record ReportInput(string Question, Guid? DepartmentId, DateOnly? From, DateOnly? To, bool? Compare);

    [HttpGet("examples")]
    public IActionResult ExampleQuestions() => Ok(NlReport.ExamplesFor(Lang));

    [HttpPost("report")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> Report(ReportInput body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Question) || body.Question.Length > 300) return BadRequest(new { message = L("Soru 1–300 karakter olmalı.", "The question must be 1–300 characters.") });
        var result = await NlReport.RunAsync(Db, Tenant, body.Question, Me.IsHr, ct, Lang,
            new NlReport.Options(body.DepartmentId, body.From, body.To, body.Compare, AllowSalary: Me.IsHr));
        // Ücret dağılımı toplu da olsa hassas: görüntüleme erişim kaydına yazılır.
        if (result.Metric == "salary" && result.Understood)
            await ComplianceAudit.WriteAsync(Db, Tenant, "Report", "salary-distribution", "SensitiveViewed", new { field = "salaryDistribution", groupBy = result.GroupBy }, Me.UserId, Me.Name, ct);
        return Ok(result);
    }

    /// <summary>Rapor süzgeci için departman listesi (yalnızca ad).</summary>
    [HttpGet("departments")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> Departments(CancellationToken ct) =>
        Ok(await Db.QueryAsync("SELECT \"Id\", \"Name\" FROM organization_departments WHERE \"TenantSlug\" = $1 ORDER BY 2", r => new { id = r.GetGuid(0), name = r.GetString(1) }, ct, Tenant));

    [HttpPost("assistant")]
    public async Task<IActionResult> Assistant(AskInput body, CancellationToken ct)
    {
        var raw = body.Question?.Trim() ?? "";
        if (raw.Length is 0 or > 500) return BadRequest(new { message = L("Mesaj 1–500 karakter olmalı.", "The message must be 1–500 characters.") });
        var assistant = HttpContext.RequestServices.GetRequiredService<HrAssistant>();
        var a = await assistant.AskAsync(new AssistantAsker(Tenant, await MyPersonAsync(ct), Me.UserId, Me.IsManager, Me.IsHr, En), raw, ct);
        var links = a.Links.Select(l => new { label = l.Label, path = l.Path });
        return a.Source switch
        {
            "report" => Ok(new { reply = a.Reply, source = a.Source, report = a.Report, links }),
            "llm" or "kb" => Ok(new { reply = a.Reply, source = a.Source, related = a.Related ?? Array.Empty<string>(), links }),
            _ => Ok(new { reply = a.Reply, source = a.Source, links }),
        };
    }

    /* ---------------------------------------------------- bilgi bankası */

    [HttpGet("kb")]
    public async Task<IActionResult> Kb(CancellationToken ct) => Ok(await _db.KbArticles.AsNoTracking().OrderBy(a => a.Title).ToListAsync(ct));

    public record KbInput(string Title, string Body, List<string>? Tags);

    [HttpPost("kb")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> CreateKb(KbInput body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Title) || string.IsNullOrWhiteSpace(body.Body)) return BadRequest(new { message = L("Başlık ve içerik zorunlu.", "Title and content are required.") });
        var a = new KbArticle { Title = body.Title.Trim(), Body = body.Body.Trim(), Tags = body.Tags ?? new() };
        _db.KbArticles.Add(a);
        await _db.SaveChangesAsync(ct);
        return Ok(a);
    }

    [HttpPut("kb/{id:guid}")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> UpdateKb(Guid id, KbInput body, CancellationToken ct)
    {
        var a = await _db.KbArticles.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null) return NotFound();
        a.Title = body.Title.Trim(); a.Body = body.Body.Trim(); a.Tags = body.Tags ?? new(); a.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(a);
    }

    [HttpDelete("kb/{id:guid}")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> DeleteKb(Guid id, CancellationToken ct)
    {
        var a = await _db.KbArticles.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null) return NotFound();
        _db.KbArticles.Remove(a);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("kb/samples")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> KbSamples(CancellationToken ct)
    {
        var have = await _db.KbArticles.Select(a => a.Title).ToListAsync(ct);
        var samples = new (string T, string B, string[] Tags)[]
        {
            ("Yıllık izin politikası", "1–5 yıl kıdemde 14, 5–15 yılda 20, 15 yıl ve üzerinde 26 iş günü yıllık izin hakkı vardır (İş K. m.53). 18 yaş altı ve 50 yaş üstü için en az 20 gündür. İzin talebi en az 1 hafta önceden yöneticiye iletilir; bölünerek kullanılabilir, bir parçası 10 günden az olamaz.", new[] { "izin", "yillik izin", "tatil" }),
            ("Uzaktan çalışma", "Haftada en fazla 2 gün uzaktan çalışılabilir. Çalışma yerinizi Ofis → Kim nerede ekranından günlük bildirmeniz rica edilir. Çekirdek saatler 10:00–16:00'dır.", new[] { "uzaktan", "hibrit", "evden", "remote" }),
            ("Masraf politikası", "Masraflar harcamadan sonraki 30 gün içinde fişiyle beyan edilir. Yemek limiti kişi başı günlük 750 ₺, şehir içi ulaşımda taksi yalnızca 22:00 sonrası kabul edilir. Onaylanan masraflar takip eden ayın maaşıyla ödenir.", new[] { "masraf", "harcama", "fis", "limit" }),
            ("Maaş ödeme günü", "Maaşlar her ayın son iş günü hesabınıza yatar. Bordronuz e-posta ile gönderilir.", new[] { "maas", "bordro", "odeme" }),
            ("Hastalık raporu", "Rapor alındığında aynı gün yöneticinize ve İK'ya bildirin; e-Rapor SGK sisteminden otomatik düşer. 2 güne kadar raporlar için ücret kesintisi yapılmaz.", new[] { "rapor", "hastalik", "saglik" }),
            ("KVKK başvurusu nasıl yapılır?", "Kişisel verilerinize erişmek, düzeltmek veya silinmesini istemek için Profil → Gizlilik (KVKK) ekranından başvuru oluşturabilirsiniz. Başvurular en geç 30 gün içinde yanıtlanır. Verilerinizi aynı ekrandan JSON olarak indirebilirsiniz.", new[] { "kvkk", "kisisel veri", "gizlilik" }),
        };
        var added = 0;
        foreach (var s in samples.Where(s => !have.Contains(s.T)))
        {
            _db.KbArticles.Add(new KbArticle { Title = s.T, Body = s.B, Tags = s.Tags.ToList() });
            added++;
        }
        await _db.SaveChangesAsync(ct);
        return Ok(new { added });
    }
}

/* ======================================================================
 * Teklif → işe alım sagası. recruitment (başvuru "Hired") → employee
 * (çalışan kaydı) → onboarding (işe giriş planı). Her adım idempotenttir:
 * önce sonucun var olup olmadığına bakılır, yoksa sahibi olan servisin
 * API'si kullanıcının kendi jetonuyla çağrılır. İzleme ekranı takılan
 * başvuruları (ör. çalışan kaydı açılmamış) gösterir.
 * ==================================================================== */
[Route("api/sagas/offer-to-hire")]
[Authorize(Policy = "RequireHrAdmin")]
public class SagaController : AppController
{
    private readonly IHttpClientFactory _http;
    public SagaController(IHttpClientFactory http) => _http = http;

    private sealed record Row(Guid ApplicationId, string Status, DateTime? ChangedAt, string Candidate, string Email, string? Phone, string Posting,
        Guid? EmployeeId, Guid? PlanId, string? PlanStatus);

    private async Task<List<Row>> LoadAsync(Guid? applicationId, CancellationToken ct) => await Db.QueryAsync("""
        SELECT a."Id", a."Status", coalesce(a."StatusChangedAt", a."AppliedAt"), c."FirstName" || ' ' || c."LastName", c."Email", c."Phone", p."Title",
               e."Id", o."Id", o."Status"
        FROM recruitment_applications a
        JOIN recruitment_candidates c ON c."Id" = a."CandidateId"
        JOIN recruitment_job_postings p ON p."Id" = a."JobPostingId"
        LEFT JOIN employee_employees e ON e."TenantSlug" = a."TenantSlug" AND lower(e."Email") = lower(c."Email")
        LEFT JOIN LATERAL (SELECT x."Id", x."Status" FROM onboarding_plans x WHERE x."EmployeeId" = e."Id" ORDER BY x."CreatedAt" DESC LIMIT 1) o ON true
        WHERE a."TenantSlug" = $1 AND a."Status" IN ('Offer','Hired') AND ($2::uuid IS NULL OR a."Id" = $2)
        ORDER BY 3 DESC
        """, r => new Row(r.GetGuid(0), r.GetString(1), r.Ts(2), r.GetString(3), r.GetString(4), r.Str(5), r.GetString(6), r.GuidOrNull(7), r.GuidOrNull(8), r.Str(9)),
        ct, Tenant, applicationId);

    private static object Shape(Row r)
    {
        var stage = r.Status == "Offer" ? "Offer" : r.EmployeeId is null ? "AwaitingEmployee" : r.PlanId is null ? "AwaitingOnboarding" : "Completed";
        var days = r.ChangedAt is null ? 0 : (int)(DateTime.UtcNow - r.ChangedAt.Value).TotalDays;
        return new
        {
            r.ApplicationId, candidate = r.Candidate, email = r.Email, posting = r.Posting, applicationStatus = r.Status, stage,
            steps = new[]
            {
                new { key = "offer", label = "Teklif", done = true },
                new { key = "hired", label = "İşe alındı", done = r.Status == "Hired" },
                new { key = "employee", label = "Çalışan kaydı", done = r.EmployeeId != null },
                new { key = "onboarding", label = "Onboarding planı", done = r.PlanId != null },
            },
            r.EmployeeId, onboardingPlanId = r.PlanId, onboardingStatus = r.PlanStatus, daysInStage = days,
            stuck = stage is "AwaitingEmployee" or "AwaitingOnboarding" && days >= 2,
        };
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var rows = (await LoadAsync(null, ct)).Select(Shape).ToList();
        return Ok(rows);
    }

    public record AdvanceInput(DateOnly? StartDate);

    [HttpPost("{applicationId:guid}/advance")]
    public async Task<IActionResult> Advance(Guid applicationId, AdvanceInput body, CancellationToken ct)
    {
        var row = (await LoadAsync(applicationId, ct)).FirstOrDefault();
        if (row is null) return NotFound();
        if (row.Status != "Hired") return BadRequest(new { message = "Önce başvuru İşe alım ekranında \"İşe alındı\" yapılmalı." });
        var log = new List<string>();
        var start = body.StartDate ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(14));
        var client = _http.CreateClient();
        var auth = Request.Headers.Authorization.ToString();
        var empId = row.EmployeeId;

        if (empId is null)
        {
            var parts = row.Candidate.Split(' ', 2);
            var req = new HttpRequestMessage(HttpMethod.Post, (Environment.GetEnvironmentVariable("EMPLOYEE_SERVICE_URL") ?? "http://employee-service:8080") + "/api/employees")
            { Content = JsonContent.Create(new { firstName = parts[0], lastName = parts.Length > 1 ? parts[1] : "-", email = row.Email, phone = row.Phone, hireDate = start }) };
            req.Headers.TryAddWithoutValidation("Authorization", auth);
            var res = await client.SendAsync(req, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) return StatusCode(502, new { message = $"Çalışan kaydı açılamadı: {Trim(text)}", log });
            empId = System.Text.Json.JsonDocument.Parse(text).RootElement.GetProperty("id").GetGuid();
            log.Add("Çalışan kaydı oluşturuldu");
        }
        else log.Add("Çalışan kaydı zaten var");

        if (row.PlanId is null)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, (Environment.GetEnvironmentVariable("ONBOARDING_SERVICE_URL") ?? "http://onboarding-service:8080") + "/api/onboarding-plans")
            { Content = JsonContent.Create(new { employeeId = empId, startDate = start, templateName = "Standart işe giriş", useDefaultTasks = true }) };
            req.Headers.TryAddWithoutValidation("Authorization", auth);
            var res = await client.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return StatusCode(502, new { message = $"Onboarding planı açılamadı: {Trim(await res.Content.ReadAsStringAsync(ct))}", log });
            log.Add("Onboarding planı oluşturuldu");
        }
        else log.Add("Onboarding planı zaten var");

        var after = (await LoadAsync(applicationId, ct)).First();
        return Ok(new { log, saga = Shape(after) });
    }

    private static string Trim(string s) => s.Length > 200 ? s[..200] : s;
}

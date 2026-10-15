using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using GovernanceService.Infrastructure;

namespace GovernanceService.Controllers;

/* ======================================================================
 * Y7 Disiplin süreci: vaka → savunma istemi (en az 2 iş günü) → çalışanın
 * yazılı savunması → tutanak → karar → kapanış.
 * Görünürlük: İK (tümü), çalışanın departman başı (salt okunur) ve çalışanın
 * kendisi (yalnızca savunma istemi, kendi savunması ve karar — tutanak ve
 * tanık adları gösterilmez). İK/yönetici görüntülemeleri erişim kaydına yazılır.
 * KVKK: adli sicil / mahkûmiyet bilgisi tutulmaz (arayüz + sunucu uyarısı).
 * Saklama: "DisciplinaryCases" kategorisi — kapanıştan N ay sonra silinir.
 * ==================================================================== */
[Route("api/disciplinary")]
[Authorize]
public class DisciplinaryController : AppController
{
    private readonly Notifier _notifier;
    public DisciplinaryController(Notifier notifier) => _notifier = notifier;

    public sealed record Case(Guid Id, Guid EmployeeId, DateOnly IncidentDate, string Category, string Description, string Status,
        string? DefenceNotice, DateTime? DefenceRequestedAt, DateOnly? DefenceDeadline, string? DefenceText, DateTime? DefenceSubmittedAt,
        string? MinutesText, string? Witnesses, string? Decision, string? DecisionNote, string? DecidedBy, DateTime? DecidedAt,
        DateTime? ClosedAt, string CreatedBy, DateTime CreatedAt);

    private const string Cols = """
        "Id","EmployeeId","IncidentDate","Category","Description","Status","DefenceNotice","DefenceRequestedAt","DefenceDeadline",
        "DefenceText","DefenceSubmittedAt","MinutesText","Witnesses","Decision","DecisionNote","DecidedBy","DecidedAt","ClosedAt","CreatedBy","CreatedAt"
        """;
    private static Case Map(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetFieldValue<DateOnly>(2), r.GetString(3), r.GetString(4), r.GetString(5),
        r.Str(6), r.Ts(7), r.Date(8), r.Str(9), r.Ts(10), r.Str(11), r.Str(12), r.Str(13), r.Str(14), r.Str(15), r.Ts(16), r.Ts(17), r.GetString(18), r.GetFieldValue<DateTime>(19));

    private async Task<Case?> FindAsync(Guid id, CancellationToken ct) =>
        (await Db.QueryAsync($"SELECT {Cols} FROM governance_disciplinary_cases WHERE \"TenantSlug\" = $1 AND \"Id\" = $2", Map, ct, Tenant, id)).FirstOrDefault();

    private static object Full(Case c, Person? p, DateOnly today) => new
    {
        c.Id, c.EmployeeId, employeeName = p?.Name, department = p?.Department, c.IncidentDate, c.Category,
        categoryLabel = Disciplinary.Categories.GetValueOrDefault(c.Category, c.Category), c.Description, c.Status,
        c.DefenceNotice, c.DefenceRequestedAt, c.DefenceDeadline, c.DefenceText, c.DefenceSubmittedAt,
        defenceOverdue = c.Status == "DefenceRequested" && c.DefenceDeadline is { } d && today > d,
        c.MinutesText, c.Witnesses, c.Decision, decisionLabel = c.Decision is null ? null : Disciplinary.Decisions.GetValueOrDefault(c.Decision, c.Decision),
        c.DecisionNote, c.DecidedBy, c.DecidedAt, c.ClosedAt, c.CreatedBy, c.CreatedAt,
    };

    /// <summary>İK mı, yoksa çalışanın departman başı mı? (Kişi kendi vakasında yönetici sayılmaz.)</summary>
    private async Task<(bool Allowed, Person? Subject)> CanViewAsync(Case c, CancellationToken ct)
    {
        var subject = await People.FindAsync(Tenant, c.EmployeeId, ct);
        if (Me.IsHr) return (true, subject);
        if (!Me.IsManager || subject is null) return (false, subject);
        var me = await MyPersonAsync(ct);
        return (me is not null && me.Id != subject.Id && subject.DepartmentHeadId == me.Id, subject);
    }

    [HttpGet("meta")]
    public async Task<IActionResult> Meta(CancellationToken ct)
    {
        var today = BusinessCalendar.TodayTr;
        var holidays = await BusinessCalendar.HolidaysAsync(Db, Tenant, today, ct);
        return Ok(new
        {
            categories = Disciplinary.Categories.Select(c => new { value = c.Key, label = c.Value }),
            decisions = Disciplinary.Decisions.Select(c => new { value = c.Key, label = c.Value }),
            minDefenceDeadline = Disciplinary.MinDeadline(today, holidays),
            minDefenceBusinessDays = Disciplinary.MinDefenceBusinessDays,
            canManage = Me.IsHr,
        });
    }

    /// <summary>İK: tüm vakalar; yönetici: başı olduğu departmandaki çalışanların vakaları.</summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? status, CancellationToken ct)
    {
        if (!Me.IsManager) return StatusCode(403, new { message = L("Bu liste İK ve yöneticilere açıktır.", "HR and managers only.") });
        var people = await People.ListAsync(Tenant, ct, includeTerminated: true);
        HashSet<Guid>? scope = null;
        if (!Me.IsHr)
        {
            var me = await MyPersonAsync(ct);
            scope = me is null ? new HashSet<Guid>() : people.Where(p => p.DepartmentHeadId == me.Id && p.Id != me.Id).Select(p => p.Id).ToHashSet();
        }
        var rows = await Db.QueryAsync($"""
            SELECT {Cols} FROM governance_disciplinary_cases WHERE "TenantSlug" = $1 AND ($2::uuid[] IS NULL OR "EmployeeId" = ANY($2))
              AND ($3::text IS NULL OR "Status" = $3)
            ORDER BY "Status" = 'Closed', "CreatedAt" DESC LIMIT 500
            """, Map, ct, Tenant, scope?.ToArray(), status);
        var today = BusinessCalendar.TodayTr;
        // Liste özet içerir (açıklama/tutanak yok); ayrıntı açıldığında erişim kaydı yazılır.
        return Ok(rows.Select(c =>
        {
            var p = people.FirstOrDefault(x => x.Id == c.EmployeeId);
            return new
            {
                c.Id, c.EmployeeId, employeeName = p?.Name, department = p?.Department, c.IncidentDate, c.Category,
                categoryLabel = Disciplinary.Categories.GetValueOrDefault(c.Category, c.Category), c.Status, c.DefenceDeadline,
                defenceOverdue = c.Status == "DefenceRequested" && c.DefenceDeadline is { } d && today > d,
                c.Decision, decisionLabel = c.Decision is null ? null : Disciplinary.Decisions.GetValueOrDefault(c.Decision, c.Decision), c.CreatedAt,
            };
        }));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var c = await FindAsync(id, ct);
        if (c is null) return NotFound();
        var (ok, subject) = await CanViewAsync(c, ct);
        if (!ok) return NotFound();
        await ComplianceAudit.WriteAsync(Db, Tenant, "DisciplinaryCase", c.EmployeeId.ToString(), "SensitiveViewed", new { field = "disciplinaryCase", caseId = id }, Me.UserId, Me.Name, ct);
        return Ok(Full(c, subject, BusinessCalendar.TodayTr));
    }

    public record CaseInput(Guid EmployeeId, DateOnly IncidentDate, string Category, string Description);

    [HttpPost]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Create([FromBody] CaseInput b, CancellationToken ct)
    {
        if (!Disciplinary.Categories.ContainsKey(b.Category ?? "")) return BadRequest(new { message = L("Geçersiz kategori.", "Invalid category.") });
        var description = b.Description?.Trim() ?? "";
        if (description.Length is < 10 or > 10_000) return BadRequest(new { message = L("Açıklama 10-10000 karakter olmalı.", "Description must be 10-10000 characters.") });
        if (b.IncidentDate > BusinessCalendar.TodayTr) return BadRequest(new { message = L("Olay tarihi ileri bir tarih olamaz.", "Incident date cannot be in the future.") });
        var p = await People.FindAsync(Tenant, b.EmployeeId, ct);
        if (p is null) return BadRequest(new { message = L("Çalışan bulunamadı.", "Employee not found.") });
        var me = await MyPersonAsync(ct);
        if (me?.Id == p.Id) return StatusCode(403, new { message = L("Kendiniz hakkında disiplin vakası açamazsınız.", "You cannot open a case about yourself.") });
        var id = Guid.NewGuid();
        await Db.ExecuteAsync("""
            INSERT INTO governance_disciplinary_cases ("Id","TenantSlug","EmployeeId","IncidentDate","Category","Description","Status","CreatedBy","CreatedAt","UpdatedAt")
            VALUES ($1,$2,$3,$4,$5,$6,'Open',$7,now(),now())
            """, ct, id, Tenant, p.Id, b.IncidentDate, b.Category, description, Me.Name);
        await ComplianceAudit.WriteAsync(Db, Tenant, "DisciplinaryCase", p.Id.ToString(), "Created", new { caseId = id, category = b.Category }, Me.UserId, Me.Name, ct);
        return Ok(new { id, warnings = Disciplinary.CriminalRecordWarnings(description) });
    }

    public record DefenceRequestInput(DateOnly? Deadline, string? NoticeText);

    /// <summary>Savunma istemi: süre en az 2 iş günü; yazı otomatik üretilir (İK düzenleyebilir). Çalışana bildirim gider.</summary>
    [HttpPost("{id:guid}/defence-request")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> RequestDefence(Guid id, [FromBody] DefenceRequestInput b, CancellationToken ct)
    {
        var c = await FindAsync(id, ct);
        if (c is null) return NotFound();
        if (c.Status is not ("Open" or "DefenceRequested"))
            return Conflict(new { message = L("Savunma istemi yalnızca açık vakada gönderilebilir.", "Defence can only be requested for open cases.") });
        var today = BusinessCalendar.TodayTr;
        var holidays = await BusinessCalendar.HolidaysAsync(Db, Tenant, today, ct);
        var min = Disciplinary.MinDeadline(today, holidays);
        var deadline = b.Deadline ?? min;
        if (deadline < min)
            return BadRequest(new { message = L($"Savunma süresi en az {Disciplinary.MinDefenceBusinessDays} iş günü olmalı (en erken {min:dd.MM.yyyy}).", $"Deadline must be at least {Disciplinary.MinDefenceBusinessDays} business days (earliest {min:yyyy-MM-dd})."), minDeadline = min });
        if (deadline > today.AddDays(60)) return BadRequest(new { message = L("Savunma süresi en fazla 60 gün olabilir.", "Deadline too far.") });
        var p = await People.FindAsync(Tenant, c.EmployeeId, ct);
        var company = (await Db.QueryAsync("SELECT \"Name\" FROM platform_tenants WHERE \"Slug\" = $1", r => r.GetString(0), ct, Tenant)).FirstOrDefault();
        var notice = string.IsNullOrWhiteSpace(b.NoticeText)
            ? Disciplinary.DefenceNotice(p?.Name ?? "Çalışan", company, c.IncidentDate, Disciplinary.Categories.GetValueOrDefault(c.Category, c.Category), c.Description, deadline)
            : b.NoticeText.Trim();
        if (notice.Length > 20_000) return BadRequest(new { message = L("Yazı çok uzun.", "Notice too long.") });
        await Db.ExecuteAsync("""
            UPDATE governance_disciplinary_cases SET "Status" = 'DefenceRequested', "DefenceNotice" = $3, "DefenceDeadline" = $4,
                "DefenceRequestedAt" = now(), "UpdatedAt" = now() WHERE "TenantSlug" = $1 AND "Id" = $2
            """, ct, Tenant, id, notice, deadline);
        await ComplianceAudit.WriteAsync(Db, Tenant, "DisciplinaryCase", c.EmployeeId.ToString(), "DefenceRequested", new { caseId = id, deadline }, Me.UserId, Me.Name, ct);
        await _notifier.LocalizedAsync(Tenant, c.EmployeeId, "Yazılı savunmanız isteniyor", "Your written statement of defence is requested",
            $"Son gün: {deadline:dd.MM.yyyy}. Ayrıntılar Günlük iş › Savunmalarım sayfasında.",
            $"Deadline: {deadline:dd.MM.yyyy}. Details are on Daily work › My statements.", "disciplinary.defence", ct);
        return Ok(new { id, deadline, notice, warnings = Disciplinary.CriminalRecordWarnings(notice) });
    }

    public record MinutesInput(string? MinutesText, string? Witnesses);

    /// <summary>Tutanak ve tanık adları (çalışana gösterilmez).</summary>
    [HttpPut("{id:guid}/minutes")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Minutes(Guid id, [FromBody] MinutesInput b, CancellationToken ct)
    {
        var c = await FindAsync(id, ct);
        if (c is null) return NotFound();
        if (c.Status == "Closed") return Conflict(new { message = L("Kapatılmış vaka değiştirilemez.", "Closed case cannot be changed.") });
        var minutes = string.IsNullOrWhiteSpace(b.MinutesText) ? null : b.MinutesText.Trim();
        var witnesses = string.IsNullOrWhiteSpace(b.Witnesses) ? null : b.Witnesses.Trim();
        if (minutes is { Length: > 20_000 } || witnesses is { Length: > 1000 }) return BadRequest(new { message = L("Metin çok uzun.", "Text too long.") });
        await Db.ExecuteAsync("""
            UPDATE governance_disciplinary_cases SET "MinutesText" = $3, "Witnesses" = $4, "UpdatedAt" = now() WHERE "TenantSlug" = $1 AND "Id" = $2
            """, ct, Tenant, id, minutes, witnesses);
        await ComplianceAudit.WriteAsync(Db, Tenant, "DisciplinaryCase", c.EmployeeId.ToString(), "Updated", new { caseId = id, field = "minutes" }, Me.UserId, Me.Name, ct);
        return Ok(new { id, warnings = Disciplinary.CriminalRecordWarnings(minutes) });
    }

    public record DecisionInput(string Decision, string? Note);

    /// <summary>
    /// Karar. "İşlem yok" dışındaki kararlar için savunma istenmiş ve savunma alınmış ya da süresi
    /// dolmuş olmalıdır (İş Kanunu m.19: savunma alınmadan fesih yapılamaz).
    /// </summary>
    [HttpPost("{id:guid}/decision")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Decide(Guid id, [FromBody] DecisionInput b, CancellationToken ct)
    {
        var c = await FindAsync(id, ct);
        if (c is null) return NotFound();
        if (!Disciplinary.Decisions.ContainsKey(b.Decision ?? "")) return BadRequest(new { message = L("Geçersiz karar.", "Invalid decision.") });
        if (c.Status is "Decided" or "Closed") return Conflict(new { message = L("Vaka zaten karara bağlanmış.", "Already decided.") });
        var today = BusinessCalendar.TodayTr;
        var defenceDone = c.Status == "DefenceReceived" || (c.Status == "DefenceRequested" && c.DefenceDeadline is { } d && today > d);
        if (b.Decision != "NoAction" && !defenceDone)
            return Conflict(new { message = L("Karar için önce yazılı savunma istenmeli ve savunma alınmalı ya da süresi dolmalıdır.", "Defence must be received or its deadline passed before a decision.") });
        var note = string.IsNullOrWhiteSpace(b.Note) ? null : b.Note.Trim()[..Math.Min(b.Note.Trim().Length, 5000)];
        await Db.ExecuteAsync("""
            UPDATE governance_disciplinary_cases SET "Status" = 'Decided', "Decision" = $3, "DecisionNote" = $4, "DecidedBy" = $5, "DecidedAt" = now(), "UpdatedAt" = now()
            WHERE "TenantSlug" = $1 AND "Id" = $2
            """, ct, Tenant, id, b.Decision, note, Me.Name);
        await ComplianceAudit.WriteAsync(Db, Tenant, "DisciplinaryCase", c.EmployeeId.ToString(), "Decided", new { caseId = id, decision = b.Decision }, Me.UserId, Me.Name, ct);
        if (c.DefenceRequestedAt is not null)
            await _notifier.LocalizedAsync(Tenant, c.EmployeeId, "Disiplin süreciyle ilgili karar verildi", "A decision has been made in the disciplinary process",
                "Ayrıntılar Günlük iş › Savunmalarım sayfasında.", "Details are on Daily work › My statements.", "disciplinary.decision", ct);
        return Ok(new { id, status = "Decided", warnings = Disciplinary.CriminalRecordWarnings(note) });
    }

    [HttpPost("{id:guid}/close")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Close(Guid id, CancellationToken ct)
    {
        var c = await FindAsync(id, ct);
        if (c is null) return NotFound();
        if (c.Status == "Closed") return Conflict(new { message = L("Vaka zaten kapalı.", "Already closed.") });
        await Db.ExecuteAsync("""
            UPDATE governance_disciplinary_cases SET "Status" = 'Closed', "ClosedAt" = now(), "UpdatedAt" = now(),
                "Decision" = coalesce("Decision", 'NoAction'), "DecidedAt" = coalesce("DecidedAt", now()), "DecidedBy" = coalesce("DecidedBy", $3)
            WHERE "TenantSlug" = $1 AND "Id" = $2
            """, ct, Tenant, id, Me.Name);
        await ComplianceAudit.WriteAsync(Db, Tenant, "DisciplinaryCase", c.EmployeeId.ToString(), "Closed", new { caseId = id }, Me.UserId, Me.Name, ct);
        return Ok(new { id, status = "Closed" });
    }

    /* -------------------------------------------------------------- çalışanın kendi görünümü */

    private static object EmployeeView(Case c, DateOnly today) => new
    {
        c.Id, c.IncidentDate, c.Category, categoryLabel = Disciplinary.Categories.GetValueOrDefault(c.Category, c.Category), c.Status,
        c.DefenceNotice, c.DefenceRequestedAt, c.DefenceDeadline, c.DefenceText, c.DefenceSubmittedAt,
        canSubmitDefence = c.Status == "DefenceRequested" && c.DefenceDeadline is { } d && today <= d,
        decision = c.Status is "Decided" or "Closed" ? c.Decision : null,
        decisionLabel = c.Status is "Decided" or "Closed" && c.Decision is { } x ? Disciplinary.Decisions.GetValueOrDefault(x, x) : null,
        decisionNote = c.Status is "Decided" or "Closed" ? c.DecisionNote : null,
        decidedAt = c.Status is "Decided" or "Closed" ? c.DecidedAt : null,
    };

    /// <summary>Çalışanın kendi vakaları — yalnızca savunma istendikten sonra görünür; tutanak/tanık yok.</summary>
    [HttpGet("mine")]
    public async Task<IActionResult> Mine(CancellationToken ct)
    {
        var me = await MyPersonAsync(ct);
        if (me is null) return Ok(Array.Empty<object>());
        var rows = await Db.QueryAsync($"""
            SELECT {Cols} FROM governance_disciplinary_cases WHERE "TenantSlug" = $1 AND "EmployeeId" = $2 AND "DefenceRequestedAt" IS NOT NULL
            ORDER BY "CreatedAt" DESC
            """, Map, ct, Tenant, me.Id);
        var today = BusinessCalendar.TodayTr;
        return Ok(rows.Select(c => EmployeeView(c, today)));
    }

    public record DefenceInput(string Text);

    [HttpPost("mine/{id:guid}/defence")]
    public async Task<IActionResult> SubmitDefence(Guid id, [FromBody] DefenceInput b, CancellationToken ct)
    {
        var me = await MyPersonAsync(ct);
        var c = await FindAsync(id, ct);
        if (me is null || c is null || c.EmployeeId != me.Id || c.DefenceRequestedAt is null) return NotFound();
        if (c.Status != "DefenceRequested") return Conflict(new { message = L("Bu vaka için savunma beklenmiyor.", "No defence is expected for this case.") });
        if (c.DefenceDeadline is { } d && BusinessCalendar.TodayTr > d)
            return Conflict(new { message = L("Savunma süresi doldu; İK ile görüşün.", "The defence deadline has passed; contact HR.") });
        var text = b.Text?.Trim() ?? "";
        if (text.Length is < 10 or > 20_000) return BadRequest(new { message = L("Savunma 10-20000 karakter olmalı.", "Defence must be 10-20000 characters.") });
        await Db.ExecuteAsync("""
            UPDATE governance_disciplinary_cases SET "Status" = 'DefenceReceived', "DefenceText" = $3, "DefenceSubmittedAt" = now(), "UpdatedAt" = now()
            WHERE "TenantSlug" = $1 AND "Id" = $2
            """, ct, Tenant, id, text);
        await ComplianceAudit.WriteAsync(Db, Tenant, "DisciplinaryCase", me.Id.ToString(), "DefenceSubmitted", new { caseId = id }, Me.UserId, Me.Name, ct);
        var updated = await FindAsync(id, ct);
        return Ok(new { view = EmployeeView(updated!, BusinessCalendar.TodayTr), warnings = Disciplinary.CriminalRecordWarnings(text) });
    }
}

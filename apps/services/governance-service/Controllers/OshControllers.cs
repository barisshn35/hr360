using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using GovernanceService.Infrastructure;

namespace GovernanceService.Controllers;

/* ======================================================================
 * Y6 İş sağlığı ve güvenliği (6331 s. Kanun).
 *  (a) iş kazası / ramak kala kayıtları — SGK bildirimi 3 iş günü (5510 m.13)
 *  (b) periyodik muayeneler — sağlık notları ÖZEL NİTELİKLİ veri: AES-256-GCM
 *      şifreli, YALNIZCA işyeri hekimi ("osh-physician" / "ext-osh-physician"
 *      rolü) okur/yazar; her okuma erişim kaydına yazılır. İK ve yönetici
 *      yalnızca uygun/uygun değil ve tarihleri görür.
 *  (c) İSG eğitimleri — geçerlilik süresi ve yenileme hatırlatma listesi.
 * Yönetim: İK yöneticisi, İSG uzmanı ("osh-specialist") ve işyeri hekimi.
 * ==================================================================== */
[Route("api/osh")]
[Authorize]
public class OshController : AppController
{
    private IActionResult Forbidden(string? msg = null) => StatusCode(403, new { message = msg ?? L("Bu işlem için İSG yetkisi gerekir.", "OSH permission required.") });

    [HttpGet("me")]
    public IActionResult MeInfo() => Ok(new { canManage = Osh.CanManage(Me), isPhysician = Osh.IsPhysician(Me), isSpecialist = Osh.IsSpecialist(Me), isManager = Me.IsManager });

    /* ================================================================ (a) olaylar */

    public sealed record Incident(Guid Id, string Kind, DateOnly OccurredOn, string? OccurredTime, string Location, string Description,
        Guid? InjuredEmployeeId, int LostDays, string? RootCause, string? CorrectiveActions, DateOnly? SgkNotifiedOn, string? SgkReference,
        string Status, string CreatedBy, DateTime CreatedAt);

    private const string IncCols = "\"Id\",\"Kind\",\"OccurredOn\",\"OccurredTime\",\"Location\",\"Description\",\"InjuredEmployeeId\",\"LostDays\",\"RootCause\",\"CorrectiveActions\",\"SgkNotifiedOn\",\"SgkReference\",\"Status\",\"CreatedBy\",\"CreatedAt\"";
    private static Incident MapInc(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetFieldValue<DateOnly>(2), r.Str(3), r.GetString(4), r.GetString(5),
        r.GuidOrNull(6), r.GetInt32(7), r.Str(8), r.Str(9), r.Date(10), r.Str(11), r.GetString(12), r.GetString(13), r.GetFieldValue<DateTime>(14));

    private async Task<object> IncDto(Incident i, IReadOnlyList<Person> people, CancellationToken ct)
    {
        var holidays = await BusinessCalendar.HolidaysAsync(Db, Tenant, i.OccurredOn, ct);
        var deadline = i.Kind == "Accident" ? Osh.SgkDeadline(i.OccurredOn, holidays) : (DateOnly?)null;
        var today = BusinessCalendar.TodayTr;
        return new
        {
            i.Id, i.Kind, i.OccurredOn, i.OccurredTime, i.Location, i.Description, i.InjuredEmployeeId,
            injuredName = i.InjuredEmployeeId is { } e ? people.FirstOrDefault(p => p.Id == e)?.Name : null,
            i.LostDays, i.RootCause, i.CorrectiveActions, i.SgkNotifiedOn, i.SgkReference, i.Status, i.CreatedBy, i.CreatedAt,
            sgkDeadline = deadline,
            sgkOverdue = deadline is { } d && i.SgkNotifiedOn is null && today > d,
            sgkLate = deadline is { } d2 && i.SgkNotifiedOn is { } n && n > d2,
            sgkDaysLeft = deadline is { } d3 && i.SgkNotifiedOn is null ? d3.DayNumber - today.DayNumber : (int?)null,
        };
    }

    [HttpGet("incidents")]
    public async Task<IActionResult> Incidents(CancellationToken ct)
    {
        if (!Osh.CanManage(Me)) return Forbidden();
        var rows = await Db.QueryAsync($"SELECT {IncCols} FROM governance_osh_incidents WHERE \"TenantSlug\" = $1 ORDER BY \"OccurredOn\" DESC, \"CreatedAt\" DESC LIMIT 500", MapInc, ct, Tenant);
        var people = await People.ListAsync(Tenant, ct, includeTerminated: true);
        var list = new List<object>();
        foreach (var r in rows) list.Add(await IncDto(r, people, ct));
        return Ok(list);
    }

    public record IncidentInput(string Kind, DateOnly OccurredOn, string? OccurredTime, string? Location, string Description, Guid? InjuredEmployeeId,
        int? LostDays, string? RootCause, string? CorrectiveActions, DateOnly? SgkNotifiedOn, string? SgkReference, string? Status);

    private string? ValidateIncident(IncidentInput b)
    {
        if (!Osh.Kinds.Contains(b.Kind)) return L("Geçersiz olay türü.", "Invalid kind.");
        if (b.OccurredOn > BusinessCalendar.TodayTr) return L("Olay tarihi ileri bir tarih olamaz.", "Date cannot be in the future.");
        if ((b.Description?.Trim().Length ?? 0) is < 5 or > 5000) return L("Açıklama 5-5000 karakter olmalı.", "Description must be 5-5000 characters.");
        if (b.LostDays is < 0 or > 3650) return L("Kayıp gün geçersiz.", "Invalid lost days.");
        if (b.Status is not (null or "Open" or "Closed")) return L("Geçersiz durum.", "Invalid status.");
        if (b.SgkNotifiedOn is { } n && n < b.OccurredOn) return L("SGK bildirim tarihi olay tarihinden önce olamaz.", "Notification date before incident.");
        return null;
    }

    private static string? Clean(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(s.Trim().Length, max)];

    [HttpPost("incidents")]
    public async Task<IActionResult> CreateIncident([FromBody] IncidentInput b, CancellationToken ct)
    {
        if (!Osh.CanManage(Me)) return Forbidden();
        if (ValidateIncident(b) is { } err) return BadRequest(new { message = err });
        if (b.InjuredEmployeeId is { } e && await People.FindAsync(Tenant, e, ct) is null) return BadRequest(new { message = L("Çalışan bulunamadı.", "Employee not found.") });
        var id = Guid.NewGuid();
        await Db.ExecuteAsync("""
            INSERT INTO governance_osh_incidents ("Id","TenantSlug","Kind","OccurredOn","OccurredTime","Location","Description","InjuredEmployeeId","LostDays",
                "RootCause","CorrectiveActions","SgkNotifiedOn","SgkReference","Status","CreatedBy","CreatedAt","UpdatedAt")
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,now(),now())
            """, ct, id, Tenant, b.Kind, b.OccurredOn, Clean(b.OccurredTime, 5), Clean(b.Location, 200) ?? "", b.Description.Trim(), b.InjuredEmployeeId,
            b.LostDays ?? 0, Clean(b.RootCause, 5000), Clean(b.CorrectiveActions, 5000), b.SgkNotifiedOn, Clean(b.SgkReference, 100), b.Status ?? "Open", Me.Name);
        var row = (await Db.QueryAsync($"SELECT {IncCols} FROM governance_osh_incidents WHERE \"TenantSlug\" = $1 AND \"Id\" = $2", MapInc, ct, Tenant, id)).First();
        return Ok(await IncDto(row, await People.ListAsync(Tenant, ct, includeTerminated: true), ct));
    }

    [HttpPut("incidents/{id:guid}")]
    public async Task<IActionResult> UpdateIncident(Guid id, [FromBody] IncidentInput b, CancellationToken ct)
    {
        if (!Osh.CanManage(Me)) return Forbidden();
        if (ValidateIncident(b) is { } err) return BadRequest(new { message = err });
        var n = await Db.ExecuteAsync("""
            UPDATE governance_osh_incidents SET "Kind" = $3, "OccurredOn" = $4, "OccurredTime" = $5, "Location" = $6, "Description" = $7,
                "InjuredEmployeeId" = $8, "LostDays" = $9, "RootCause" = $10, "CorrectiveActions" = $11, "SgkNotifiedOn" = $12,
                "SgkReference" = $13, "Status" = $14, "UpdatedAt" = now()
            WHERE "TenantSlug" = $1 AND "Id" = $2
            """, ct, Tenant, id, b.Kind, b.OccurredOn, Clean(b.OccurredTime, 5), Clean(b.Location, 200) ?? "", b.Description.Trim(), b.InjuredEmployeeId,
            b.LostDays ?? 0, Clean(b.RootCause, 5000), Clean(b.CorrectiveActions, 5000), b.SgkNotifiedOn, Clean(b.SgkReference, 100), b.Status ?? "Open");
        if (n == 0) return NotFound();
        var row = (await Db.QueryAsync($"SELECT {IncCols} FROM governance_osh_incidents WHERE \"TenantSlug\" = $1 AND \"Id\" = $2", MapInc, ct, Tenant, id)).First();
        return Ok(await IncDto(row, await People.ListAsync(Tenant, ct, includeTerminated: true), ct));
    }

    [HttpDelete("incidents/{id:guid}")]
    public async Task<IActionResult> DeleteIncident(Guid id, CancellationToken ct)
    {
        if (!Osh.CanManage(Me)) return Forbidden();
        var n = await Db.ExecuteAsync("DELETE FROM governance_osh_incidents WHERE \"TenantSlug\" = $1 AND \"Id\" = $2", ct, Tenant, id);
        if (n > 0) await ComplianceAudit.WriteAsync(Db, Tenant, "OshIncident", id.ToString(), "Deleted", new { }, Me.UserId, Me.Name, ct);
        return n == 0 ? NotFound() : NoContent();
    }

    /* ================================================================ (b) muayeneler */

    public sealed record Exam(Guid Id, Guid EmployeeId, string ExamType, DateOnly ExamDate, DateOnly? NextDueDate, string Result, bool HasNotes, string RecordedBy, DateTime UpdatedAt);
    private const string ExamCols = "\"Id\",\"EmployeeId\",\"ExamType\",\"ExamDate\",\"NextDueDate\",\"Result\",\"NotesEnc\" IS NOT NULL,\"RecordedBy\",\"UpdatedAt\"";
    private static Exam MapExam(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetFieldValue<DateOnly>(3), r.Date(4),
        r.GetString(5), r.GetBoolean(6), r.GetString(7), r.GetFieldValue<DateTime>(8));

    /// <summary>Kimin muayene kayıtlarını görebilirim? null = herkes (İSG yetkilileri).</summary>
    private async Task<HashSet<Guid>?> ExamScopeAsync(CancellationToken ct)
    {
        if (Osh.CanManage(Me)) return null;
        var me = await MyPersonAsync(ct);
        var ids = new HashSet<Guid>();
        if (me is null) return ids;
        ids.Add(me.Id);
        if (Me.IsManager) foreach (var p in await People.TeamOfAsync(Tenant, me.Id, ct)) ids.Add(p.Id);
        return ids;
    }

    /// <summary>
    /// Muayene listesi. Sağlık notunun VARLIĞI bile yalnızca hekime gösterilir; diğerleri sonuç ve
    /// tarihleri görür. ?latest=true her çalışanın en son muayenesi.
    /// </summary>
    [HttpGet("exams")]
    public async Task<IActionResult> Exams([FromQuery] bool latest, CancellationToken ct)
    {
        var scope = await ExamScopeAsync(ct);
        var rows = await Db.QueryAsync($"""
            SELECT {ExamCols} FROM governance_osh_exams WHERE "TenantSlug" = $1 AND ($2::uuid[] IS NULL OR "EmployeeId" = ANY($2))
            ORDER BY "ExamDate" DESC LIMIT 2000
            """, MapExam, ct, Tenant, scope?.ToArray());
        if (latest) rows = rows.GroupBy(r => r.EmployeeId).Select(g => g.First()).ToList();
        var people = await People.ListAsync(Tenant, ct, includeTerminated: true);
        var today = BusinessCalendar.TodayTr;
        var physician = Osh.IsPhysician(Me);
        var latestIds = rows.GroupBy(r => r.EmployeeId).Select(g => g.OrderByDescending(x => x.ExamDate).First().Id).ToHashSet();
        return Ok(rows.Select(r => new
        {
            r.Id, r.EmployeeId, employeeName = people.FirstOrDefault(p => p.Id == r.EmployeeId)?.Name, r.ExamType, r.ExamDate, r.NextDueDate, r.Result,
            hasNotes = physician ? r.HasNotes : (bool?)null, r.RecordedBy, r.UpdatedAt,
            isLatest = latestIds.Contains(r.Id),
            dueState = latestIds.Contains(r.Id) ? Osh.DueState(r.NextDueDate, today) : "None",
        }));
    }

    public record ExamInput(Guid EmployeeId, string? ExamType, DateOnly ExamDate, DateOnly? NextDueDate, string? HazardClass, string Result, string? Notes);

    [HttpPost("exams")]
    public async Task<IActionResult> CreateExam([FromBody] ExamInput b, CancellationToken ct)
    {
        if (!Osh.CanManage(Me)) return Forbidden();
        if (!string.IsNullOrWhiteSpace(b.Notes) && !Osh.IsPhysician(Me))
            return Forbidden(L("Sağlık notlarını yalnızca işyeri hekimi girebilir.", "Only the occupational physician can enter health notes."));
        if (!Osh.Results.Contains(b.Result)) return BadRequest(new { message = L("Geçersiz sonuç.", "Invalid result.") });
        var type = b.ExamType ?? "Periodic";
        if (!Osh.ExamTypes.Contains(type)) return BadRequest(new { message = L("Geçersiz muayene türü.", "Invalid exam type.") });
        if (b.ExamDate > BusinessCalendar.TodayTr) return BadRequest(new { message = L("Muayene tarihi ileri bir tarih olamaz.", "Exam date cannot be in the future.") });
        var next = b.NextDueDate ?? Osh.NextExam(b.ExamDate, b.HazardClass);
        if (next <= b.ExamDate) return BadRequest(new { message = L("Sonraki muayene tarihi muayeneden sonra olmalı.", "Next due must be after exam date.") });
        if (b.Notes is { Length: > 10_000 }) return BadRequest(new { message = L("Not en fazla 10000 karakter olabilir.", "Notes too long.") });
        if (await People.FindAsync(Tenant, b.EmployeeId, ct) is null) return BadRequest(new { message = L("Çalışan bulunamadı.", "Employee not found.") });
        var id = Guid.NewGuid();
        await Db.ExecuteAsync("""
            INSERT INTO governance_osh_exams ("Id","TenantSlug","EmployeeId","ExamType","ExamDate","NextDueDate","Result","NotesEnc","RecordedBy","CreatedAt","UpdatedAt")
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,now(),now())
            """, ct, id, Tenant, b.EmployeeId, type, b.ExamDate, next, b.Result,
            string.IsNullOrWhiteSpace(b.Notes) ? null : SecretBox.Protect(b.Notes.Trim()), Me.Name);
        return Ok(new { id, nextDueDate = next });
    }

    [HttpDelete("exams/{id:guid}")]
    public async Task<IActionResult> DeleteExam(Guid id, CancellationToken ct)
    {
        if (!Osh.CanManage(Me)) return Forbidden();
        var n = await Db.ExecuteAsync("DELETE FROM governance_osh_exams WHERE \"TenantSlug\" = $1 AND \"Id\" = $2", ct, Tenant, id);
        if (n > 0) await ComplianceAudit.WriteAsync(Db, Tenant, "OshExam", id.ToString(), "Deleted", new { }, Me.UserId, Me.Name, ct);
        return n == 0 ? NotFound() : NoContent();
    }

    public record NotesInput(string? Notes);

    /// <summary>Sağlık notu (yalnızca işyeri hekimi). Boş gönderilirse silinir.</summary>
    [HttpPut("exams/{id:guid}/notes")]
    public async Task<IActionResult> SetNotes(Guid id, [FromBody] NotesInput b, CancellationToken ct)
    {
        if (!Osh.IsPhysician(Me)) return Forbidden(L("Sağlık notlarını yalnızca işyeri hekimi girebilir.", "Only the occupational physician can enter health notes."));
        if (b.Notes is { Length: > 10_000 }) return BadRequest(new { message = L("Not en fazla 10000 karakter olabilir.", "Notes too long.") });
        var n = await Db.ExecuteAsync("UPDATE governance_osh_exams SET \"NotesEnc\" = $3, \"UpdatedAt\" = now() WHERE \"TenantSlug\" = $1 AND \"Id\" = $2",
            ct, Tenant, id, string.IsNullOrWhiteSpace(b.Notes) ? null : SecretBox.Protect(b.Notes.Trim()));
        if (n == 0) return NotFound();
        await ComplianceAudit.WriteAsync(Db, Tenant, "OshExam", id.ToString(), "Updated", new { field = "healthNotes" }, Me.UserId, Me.Name, ct);
        return Ok(new { saved = true });
    }

    /// <summary>Sağlık notu okuma: yalnızca işyeri hekimi; her okuma erişim kaydına yazılır.</summary>
    [HttpGet("exams/{id:guid}/notes")]
    public async Task<IActionResult> Notes(Guid id, CancellationToken ct)
    {
        if (!Osh.IsPhysician(Me)) return Forbidden(L("Sağlık notlarını yalnızca işyeri hekimi görebilir.", "Only the occupational physician can view health notes."));
        var row = (await Db.QueryAsync("SELECT \"EmployeeId\", \"NotesEnc\" FROM governance_osh_exams WHERE \"TenantSlug\" = $1 AND \"Id\" = $2",
            r => (Emp: r.GetGuid(0), Enc: r.Str(1)), ct, Tenant, id)).FirstOrDefault();
        if (row.Emp == Guid.Empty) return NotFound();
        await ComplianceAudit.WriteAsync(Db, Tenant, "OshExam", row.Emp.ToString(), "SensitiveViewed", new { field = "healthNotes", exam = id }, Me.UserId, Me.Name, ct);
        return Ok(new { notes = SecretBox.Unprotect(row.Enc) });
    }

    /* ================================================================ (c) eğitimler */

    public sealed record Training(Guid Id, string Topic, DateOnly TrainingDate, decimal DurationHours, int? ValidityMonths, DateOnly? ExpiresOn,
        string? Trainer, Guid[] ParticipantIds, string CreatedBy);
    private const string TrCols = "\"Id\",\"Topic\",\"TrainingDate\",\"DurationHours\",\"ValidityMonths\",\"ExpiresOn\",\"Trainer\",\"ParticipantIds\",\"CreatedBy\"";
    private static Training MapTr(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetFieldValue<DateOnly>(2), r.GetDecimal(3), r.IsDBNull(4) ? null : r.GetInt32(4),
        r.Date(5), r.Str(6), r.GetFieldValue<Guid[]>(7), r.GetString(8));

    [HttpGet("trainings")]
    public async Task<IActionResult> Trainings(CancellationToken ct)
    {
        Guid? only = null;
        if (!Osh.CanManage(Me))
        {
            var me = await MyPersonAsync(ct);
            if (me is null) return Ok(Array.Empty<object>());
            only = me.Id;
        }
        var rows = await Db.QueryAsync($"""
            SELECT {TrCols} FROM governance_osh_trainings WHERE "TenantSlug" = $1 AND ($2::uuid IS NULL OR $2 = ANY("ParticipantIds"))
            ORDER BY "TrainingDate" DESC LIMIT 1000
            """, MapTr, ct, Tenant, only);
        var people = await People.ListAsync(Tenant, ct, includeTerminated: true);
        return Ok(rows.Select(t => new
        {
            t.Id, t.Topic, t.TrainingDate, t.DurationHours, t.ValidityMonths, t.ExpiresOn, t.Trainer, t.CreatedBy,
            participantCount = t.ParticipantIds.Length,
            // Çalışan kendi kaydında diğer katılımcıları görmez.
            participants = t.ParticipantIds.Where(_ => only is null).Select(id => new { id, name = people.FirstOrDefault(p => p.Id == id)?.Name ?? "—" }).ToArray(),
        }));
    }

    public record TrainingInput(string Topic, DateOnly TrainingDate, decimal DurationHours, int? ValidityMonths, string? Trainer, Guid[] ParticipantIds);

    [HttpPost("trainings")]
    public async Task<IActionResult> CreateTraining([FromBody] TrainingInput b, CancellationToken ct)
    {
        if (!Osh.CanManage(Me)) return Forbidden();
        var topic = b.Topic?.Trim() ?? "";
        if (topic.Length is < 3 or > 200) return BadRequest(new { message = L("Konu 3-200 karakter olmalı.", "Topic must be 3-200 characters.") });
        if (b.DurationHours is <= 0 or > 500) return BadRequest(new { message = L("Süre (saat) geçersiz.", "Invalid duration.") });
        if (b.ValidityMonths is < 1 or > 120) return BadRequest(new { message = L("Geçerlilik 1-120 ay olmalı.", "Validity must be 1-120 months.") });
        var ids = b.ParticipantIds?.Distinct().ToArray() ?? Array.Empty<Guid>();
        if (ids.Length == 0) return BadRequest(new { message = L("En az bir katılımcı seçin.", "Select at least one participant.") });
        var people = (await People.ListAsync(Tenant, ct)).Select(p => p.Id).ToHashSet();
        if (ids.Any(i => !people.Contains(i))) return BadRequest(new { message = L("Katılımcılardan biri bulunamadı.", "Unknown participant.") });
        var id = Guid.NewGuid();
        var expires = b.ValidityMonths is { } m ? b.TrainingDate.AddMonths(m) : (DateOnly?)null;
        await Db.ExecuteAsync("""
            INSERT INTO governance_osh_trainings ("Id","TenantSlug","Topic","TrainingDate","DurationHours","ValidityMonths","ExpiresOn","Trainer","ParticipantIds","CreatedBy","CreatedAt")
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,now())
            """, ct, id, Tenant, topic, b.TrainingDate, b.DurationHours, b.ValidityMonths, expires, Clean(b.Trainer, 200), ids, Me.Name);
        return Ok(new { id, expiresOn = expires });
    }

    [HttpDelete("trainings/{id:guid}")]
    public async Task<IActionResult> DeleteTraining(Guid id, CancellationToken ct)
    {
        if (!Osh.CanManage(Me)) return Forbidden();
        var n = await Db.ExecuteAsync("DELETE FROM governance_osh_trainings WHERE \"TenantSlug\" = $1 AND \"Id\" = $2", ct, Tenant, id);
        return n == 0 ? NotFound() : NoContent();
    }

    /// <summary>
    /// Hatırlatma listesi: süresi geçmiş/30 gün içinde dolacak periyodik muayeneler ve
    /// süresi geçmiş/60 gün içinde dolacak eğitimler (kişi + konu başına en son eğitim).
    /// </summary>
    [HttpGet("reminders")]
    public async Task<IActionResult> Reminders(CancellationToken ct)
    {
        if (!Osh.CanManage(Me)) return Forbidden();
        var today = BusinessCalendar.TodayTr;
        var people = await People.ListAsync(Tenant, ct);
        var active = people.ToDictionary(p => p.Id, p => p.Name);
        var exams = (await Db.QueryAsync($"SELECT {ExamCols} FROM governance_osh_exams WHERE \"TenantSlug\" = $1 ORDER BY \"ExamDate\" DESC", MapExam, ct, Tenant))
            .GroupBy(e => e.EmployeeId).Select(g => g.First())
            .Where(e => active.ContainsKey(e.EmployeeId) && Osh.DueState(e.NextDueDate, today) is "Overdue" or "DueSoon")
            .Select(e => new { e.EmployeeId, employeeName = active[e.EmployeeId], e.NextDueDate, e.Result, state = Osh.DueState(e.NextDueDate, today) })
            .OrderBy(e => e.NextDueDate).ToList();
        var trainings = await Db.QueryAsync($"SELECT {TrCols} FROM governance_osh_trainings WHERE \"TenantSlug\" = $1 AND \"ExpiresOn\" IS NOT NULL", MapTr, ct, Tenant);
        var expiring = trainings
            .SelectMany(t => t.ParticipantIds.Select(p => (Employee: p, Topic: t.Topic.Trim().ToLowerInvariant(), t)))
            .GroupBy(x => (x.Employee, x.Topic))
            .Select(g => g.OrderByDescending(x => x.t.ExpiresOn).First())
            .Where(x => active.ContainsKey(x.Employee) && Osh.DueState(x.t.ExpiresOn, today, 60) is "Overdue" or "DueSoon")
            .Select(x => new { employeeId = x.Employee, employeeName = active[x.Employee], topic = x.t.Topic, expiresOn = x.t.ExpiresOn, state = Osh.DueState(x.t.ExpiresOn, today, 60) })
            .OrderBy(x => x.expiresOn).ToList();
        return Ok(new { exams, trainings = expiring });
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using EngagementService.Infrastructure;

namespace EngagementService.Controllers;

/* ======================================================================
 * İlk 90 gün takibi (yenilik haritası #8): yeni çalışanın uyum formları.
 * Çalışan kendi formunu yanıtlar; yönetici (departman başı) ekibinin, İK
 * tüm şirketin yanıtlarını görür. Düşük puan (≤2) işaretlenir.
 * ==================================================================== */
[Route("api/newhire")]
[Authorize]
public class NewHireController : AppController
{
    private const string Cols = """
        c."Id", c."EmployeeId", e."FirstName" || ' ' || e."LastName", c."Milestone", c."DueOn", c."SentAt", c."AnsweredAt",
        c."Belonging", c."Clarity", c."Support", c."Comment", c."ManagerEmployeeId", e."HireDate", d."Name"
        """;
    private const string From = """
        FROM engagement_newhire_checkins c JOIN employee_employees e ON e."Id" = c."EmployeeId"
        LEFT JOIN LATERAL (SELECT x."DepartmentId" FROM employee_assignments x WHERE x."EmployeeId" = e."Id" AND x."EffectiveTo" IS NULL ORDER BY x."EffectiveFrom" DESC LIMIT 1) a ON true
        LEFT JOIN organization_departments d ON d."Id" = a."DepartmentId"
        """;

    private static object Map(Npgsql.NpgsqlDataReader r)
    {
        short? S(int i) => r.IsDBNull(i) ? null : r.GetInt16(i);
        var scores = new[] { S(7), S(8), S(9) };
        return new
        {
            id = r.GetGuid(0), employeeId = r.GetGuid(1), name = r.GetString(2), milestone = r.GetInt32(3), dueOn = r.GetFieldValue<DateOnly>(4),
            sentAt = r.GetFieldValue<DateTime>(5), answeredAt = r.Ts(6), belonging = scores[0], clarity = scores[1], support = scores[2], comment = r.Str(10),
            hireDate = r.GetFieldValue<DateOnly>(12), department = r.Str(13), lowScore = scores.Any(s => s is <= 2),
        };
    }

    [HttpGet("me")]
    public async Task<IActionResult> Mine(CancellationToken ct)
    {
        var me = await MyPersonAsync(ct);
        if (me is null) return Ok(Array.Empty<object>());
        return Ok(await Db.QueryAsync($"SELECT {Cols} {From} WHERE c.\"TenantSlug\" = $1 AND c.\"EmployeeId\" = $2 ORDER BY c.\"Milestone\"", Map, ct, Tenant, me.Id));
    }

    /// <summary>Yönetici: başı olduğu departmandaki yeni çalışanlar; İK: herkes. Son 180 gün.</summary>
    [HttpGet("team")]
    public async Task<IActionResult> Team(CancellationToken ct)
    {
        if (Me.IsHr)
            return Ok(await Db.QueryAsync($"SELECT {Cols} {From} WHERE c.\"TenantSlug\" = $1 AND c.\"SentAt\" > now() - interval '180 days' ORDER BY c.\"SentAt\" DESC", Map, ct, Tenant));
        var me = await MyPersonAsync(ct);
        if (me is null) return Ok(Array.Empty<object>());
        return Ok(await Db.QueryAsync($"SELECT {Cols} {From} WHERE c.\"TenantSlug\" = $1 AND c.\"ManagerEmployeeId\" = $2 AND c.\"SentAt\" > now() - interval '180 days' ORDER BY c.\"SentAt\" DESC", Map, ct, Tenant, me.Id));
    }

    /// <summary>İK: zamanlayıcıyı beklemeden kendi şirketi için çalıştırır (aynı form iki kez gitmez).</summary>
    [HttpPost("run")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Run(CancellationToken ct)
        => Ok(new { sent = await HttpContext.RequestServices.GetRequiredService<EngagementService.Infrastructure.NewHire.NewHireWorker>().RunOnceAsync(ct, Tenant) });

    public sealed record AnswerInput(int Belonging, int Clarity, int Support, string? Comment);

    [HttpPost("{id:guid}/answer")]
    public async Task<IActionResult> Answer(Guid id, AnswerInput body, CancellationToken ct)
    {
        if (new[] { body.Belonging, body.Clarity, body.Support }.Any(s => s is < 1 or > 5)) return BadRequest(new { message = "Puanlar 1-5 arasında olmalı" });
        var comment = string.IsNullOrWhiteSpace(body.Comment) ? null : body.Comment.Trim();
        if (comment is { Length: > 1000 }) return BadRequest(new { message = "Not en fazla 1000 karakter olabilir" });
        var me = await MyPersonAsync(ct);
        if (me is null) return NotFound();
        var rows = await Db.QueryAsync("""
            UPDATE engagement_newhire_checkins SET "AnsweredAt" = now(), "Belonging" = $4, "Clarity" = $5, "Support" = $6, "Comment" = $7
            WHERE "TenantSlug" = $1 AND "Id" = $2 AND "EmployeeId" = $3 AND "AnsweredAt" IS NULL
            RETURNING "ManagerEmployeeId", "Milestone"
            """, r => (Manager: r.GuidOrNull(0), Ms: r.GetInt32(1)), ct, Tenant, id, me.Id, (short)body.Belonging, (short)body.Clarity, (short)body.Support, comment);
        if (rows.Count == 0) return BadRequest(new { message = "Form bulunamadı ya da zaten yanıtlandı" });
        if (rows[0].Manager is { } m)
        {
            var low = new[] { body.Belonging, body.Clarity, body.Support }.Min() <= 2;
            await HttpContext.RequestServices.GetRequiredService<Notifier>().InAppAsync(Tenant, m,
                low ? $"{me.Name}: uyum formunda dikkat gerektiren yanıt" : $"{me.Name} {rows[0].Ms}. gün formunu yanıtladı",
                $"Aidiyet {body.Belonging}/5 · rol netliği {body.Clarity}/5 · destek {body.Support}/5{(comment is null ? "" : " · not var")}.", "newhire.answered", ct);
        }
        return Ok(new { ok = true });
    }
}

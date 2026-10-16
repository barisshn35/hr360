using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using EngagementService.Data;
using EngagementService.Infrastructure;
using EngagementService.Infrastructure.Coffee;
using EngagementService.Models;

namespace EngagementService.Controllers;

/* ======================================================================
 * Kahve eşleştirme (yenilik haritası #27). Katılım isteğe bağlıdır;
 * çalışan yalnızca kendi eşleşmelerini görür. İK katılımcı sayısını ve
 * görüşme oranını görür, kimin kiminle eşleştiğini görmez.
 * ==================================================================== */
[Route("api/coffee")]
[Authorize]
public class CoffeeController : AppController
{
    private readonly EngagementDbContext _db;
    public CoffeeController(EngagementDbContext db) => _db = db;

    private async Task<CoffeeSettings> SettingsAsync(CancellationToken ct) => await _db.CoffeeSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new CoffeeSettings();

    private sealed record MatchRow(Guid Id, Guid[] Members, bool Met, DateTime CreatedAt, string Period);

    [HttpGet("me")]
    public async Task<IActionResult> Mine(CancellationToken ct)
    {
        var s = await SettingsAsync(ct);
        var me = await MyPersonAsync(ct);
        var settings = new { s.Enabled, s.Cadence, s.DayOfWeek, s.CrossDepartment };
        if (me is null) return Ok(new { settings, joined = false, participants = 0, matches = Array.Empty<object>() });
        var joined = await _db.CoffeeParticipants.AnyAsync(p => p.EmployeeId == me.Id, ct);
        var rows = await Db.QueryAsync("""
            SELECT m."Id", m."MemberIds", m."Met", m."CreatedAt", r."PeriodKey" FROM engagement_coffee_matches m
            JOIN engagement_coffee_rounds r ON r."Id" = m."RoundId"
            WHERE m."TenantSlug" = $1 AND $2 = ANY(m."MemberIds") ORDER BY m."CreatedAt" DESC LIMIT 12
            """, r => new MatchRow(r.GetGuid(0), r.GetFieldValue<Guid[]>(1), r.GetBoolean(2), r.GetFieldValue<DateTime>(3), r.GetString(4)), ct, Tenant, me.Id);
        var people = (await People.ListAsync(Tenant, ct, includeTerminated: true)).ToDictionary(p => p.Id);
        return Ok(new
        {
            settings, joined, participants = await _db.CoffeeParticipants.CountAsync(ct),
            matches = rows.Select(m => new
            {
                m.Id, m.Met, m.CreatedAt, period = m.Period,
                with = m.Members.Where(x => x != me.Id).Select(x => people.TryGetValue(x, out var p)
                    ? new { employeeId = x, name = p.Name, department = p.Department, position = p.Position }
                    : new { employeeId = x, name = "—", department = (string?)null, position = (string?)null }),
            }),
        });
    }

    public sealed record JoinInput(bool Joined);

    [HttpPut("me")]
    public async Task<IActionResult> Join(JoinInput b, CancellationToken ct)
    {
        var me = await MyPersonAsync(ct);
        if (me is null) return BadRequest(new { message = "Katılım için çalışan kaydınız olmalı" });
        if (b.Joined)
        {
            if (!(await SettingsAsync(ct)).Enabled) return BadRequest(new { message = "Kahve eşleştirme kapalı" });
            // Atomik: eşzamanlı ikinci "katıl" isteği hata üretmez.
            await _db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO engagement_coffee_participants ("Id","TenantSlug","EmployeeId","JoinedAt") VALUES ({Guid.NewGuid()}, {Tenant}, {me.Id}, now())
                ON CONFLICT ("TenantSlug","EmployeeId") DO NOTHING
                """, ct);
        }
        else
            await _db.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM engagement_coffee_participants WHERE "TenantSlug" = {Tenant} AND "EmployeeId" = {me.Id}
                """, ct);
        return Ok(new { joined = b.Joined });
    }

    public sealed record MetInput(bool Met);

    /// <summary>Eşleşmedeki herkes "görüştük" işaretleyebilir (oran İK özetine anonim girer).</summary>
    [HttpPost("matches/{id:guid}/met")]
    public async Task<IActionResult> Met(Guid id, MetInput b, CancellationToken ct)
    {
        var me = await MyPersonAsync(ct);
        if (me is null) return NotFound();
        var n = await Db.ExecuteAsync("""
            UPDATE engagement_coffee_matches SET "Met" = $3 WHERE "Id" = $1 AND "TenantSlug" = $2 AND $4 = ANY("MemberIds")
            """, ct, id, Tenant, b.Met, me.Id);
        return n == 0 ? NotFound() : Ok(new { met = b.Met });
    }

    [HttpGet("settings")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> GetSettings(CancellationToken ct)
    {
        var s = await SettingsAsync(ct);
        var stats = (await Db.QueryAsync("""
            SELECT count(*)::int, coalesce(sum(CASE WHEN m."Met" THEN 1 ELSE 0 END), 0)::int,
                   (SELECT count(*)::int FROM engagement_coffee_rounds WHERE "TenantSlug" = $1),
                   (SELECT max("CreatedAt") FROM engagement_coffee_rounds WHERE "TenantSlug" = $1)
            FROM engagement_coffee_matches m
            WHERE m."TenantSlug" = $1 AND m."RoundId" IN (SELECT "Id" FROM engagement_coffee_rounds WHERE "TenantSlug" = $1 ORDER BY "CreatedAt" DESC LIMIT 3)
            """, r => new { groups = r.GetInt32(0), met = r.GetInt32(1), rounds = r.GetInt32(2), lastRoundAt = r.Ts(3) }, ct, Tenant)).First();
        return Ok(new { s.Enabled, s.Cadence, s.DayOfWeek, s.CrossDepartment, participants = await _db.CoffeeParticipants.CountAsync(ct), stats });
    }

    public sealed record SettingsInput(bool Enabled, string Cadence, int DayOfWeek, bool CrossDepartment);

    [HttpPut("settings")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> PutSettings(SettingsInput b, CancellationToken ct)
    {
        if (!CoffeeMatcher.Cadences.Contains(b.Cadence)) return BadRequest(new { message = "Geçersiz sıklık" });
        if (b.DayOfWeek is < 1 or > 7) return BadRequest(new { message = "Gün 1-7 olmalı" });
        var s = await _db.CoffeeSettings.FirstOrDefaultAsync(ct);
        if (s is null) _db.CoffeeSettings.Add(s = new CoffeeSettings());
        (s.Enabled, s.Cadence, s.DayOfWeek, s.CrossDepartment, s.UpdatedAt) = (b.Enabled, b.Cadence, b.DayOfWeek, b.CrossDepartment, DateTime.UtcNow);
        await _db.SaveChangesAsync(ct);
        return await GetSettings(ct);
    }

    /// <summary>İK: bu dönemin turunu hemen başlatır (dönemde tur varsa yeni tur açılmaz).</summary>
    [HttpPost("run")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Run(CancellationToken ct)
    {
        if (!(await SettingsAsync(ct)).Enabled) return BadRequest(new { message = "Kahve eşleştirme kapalı" });
        var groups = await HttpContext.RequestServices.GetRequiredService<CoffeeWorker>().RunOnceAsync(ct, Tenant, force: true);
        return Ok(new { groups });
    }
}

using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using EngagementService.Data;
using EngagementService.Infrastructure;
using EngagementService.Models;

namespace EngagementService.Controllers;

/* ======================================================================
 * Etkinlikler ve katılım (yenilik haritası #28) + gönüllülük (#30).
 * İK etkinlik açar; çalışan katılır, kontenjan doluysa bekleme listesine
 * girer; vazgeçen olunca sıradaki otomatik alınır ve haber verilir.
 * Gönüllülük etkinliğinde İK katılımı onaylar, saat kişiye yazılır.
 * ==================================================================== */
[Route("api/events")]
[Authorize]
public class EventsController : AppController
{
    public static readonly string[] Kinds = ["Social", "Training", "Volunteer", "Sports", "Other"];
    private readonly EngagementDbContext _db;
    private readonly Notifier _notify;
    public EventsController(EngagementDbContext db, Notifier notify) { _db = db; _notify = notify; }

    private object Dto(CompanyEvent e, List<EventRsvp> rsvps, Guid? me) => new
    {
        e.Id, e.Title, e.Description, e.Kind, e.StartsAt, e.EndsAt, e.Location, e.OnlineUrl, e.Capacity, e.RsvpDeadline, e.VolunteerHours, e.Status, e.CreatedByName,
        going = rsvps.Count(r => r.Status == "Going"), waitlist = rsvps.Count(r => r.Status == "Waitlist"),
        attendees = rsvps.Where(r => r.Status == "Going").OrderBy(r => r.CreatedAt).Take(12).Select(r => new { r.EmployeeId, r.Name }),
        myStatus = me is null ? null : rsvps.FirstOrDefault(r => r.EmployeeId == me)?.Status,
        myPosition = me is null ? (int?)null : WaitlistPosition(rsvps, me.Value),
    };

    private static int? WaitlistPosition(List<EventRsvp> rsvps, Guid me)
    {
        var i = rsvps.Where(r => r.Status == "Waitlist").OrderBy(r => r.CreatedAt).Select(r => r.EmployeeId).ToList().IndexOf(me);
        return i < 0 ? null : i + 1;
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] bool past = false, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var q = _db.Events.AsNoTracking().Where(e => past ? e.EndsAt < now : e.EndsAt >= now);
        var events = await (past ? q.OrderByDescending(e => e.StartsAt).Take(50) : q.OrderBy(e => e.StartsAt).Take(100)).ToListAsync(ct);
        var ids = events.Select(e => e.Id).ToList();
        var rsvps = (await _db.EventRsvps.AsNoTracking().Where(r => ids.Contains(r.EventId)).ToListAsync(ct)).ToLookup(r => r.EventId);
        var me = (await MyPersonAsync(ct))?.Id;
        return Ok(events.Select(e => Dto(e, rsvps[e.Id].ToList(), me)));
    }

    public sealed record EventInput(string Title, string? Description, string Kind, DateTime StartsAt, DateTime EndsAt, string? Location, string? OnlineUrl,
        int? Capacity, DateTime? RsvpDeadline, decimal? VolunteerHours, bool Announce = false);

    private IActionResult? Validate(EventInput b)
    {
        if ((b.Title ?? "").Trim().Length is < 3 or > 120) return BadRequest(new { message = "Başlık 3-120 karakter olmalı" });
        if (!Kinds.Contains(b.Kind)) return BadRequest(new { message = "Geçersiz etkinlik türü" });
        if (b.EndsAt <= b.StartsAt) return BadRequest(new { message = "Bitiş başlangıçtan sonra olmalı" });
        if (b.StartsAt < DateTime.UtcNow.AddMinutes(-5)) return BadRequest(new { message = "Geçmiş tarihe etkinlik açılamaz" });
        if (b.Capacity is < 1 or > 100000) return BadRequest(new { message = "Kontenjan 1 ya da daha fazla olmalı" });
        if (b.RsvpDeadline is { } d && d > b.StartsAt) return BadRequest(new { message = "Son katılım tarihi etkinlikten önce olmalı" });
        if (b.VolunteerHours is < 0 or > 24) return BadRequest(new { message = "Gönüllülük saati 0-24 olmalı" });
        if (b.Description is { Length: > 2000 } || b.Location is { Length: > 200 } || b.OnlineUrl is { Length: > 500 }) return BadRequest(new { message = "Alanlardan biri çok uzun" });
        if (!string.IsNullOrWhiteSpace(b.OnlineUrl) && !(Uri.TryCreate(b.OnlineUrl, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"))
            return BadRequest(new { message = "Bağlantı http(s) ile başlamalı" });
        return null;
    }

    private static void Apply(CompanyEvent e, EventInput b)
    {
        string? T(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        (e.Title, e.Description, e.Kind, e.StartsAt, e.EndsAt, e.Location, e.OnlineUrl, e.Capacity, e.RsvpDeadline) =
            (b.Title.Trim(), T(b.Description), b.Kind, b.StartsAt, b.EndsAt, T(b.Location), T(b.OnlineUrl), b.Capacity, b.RsvpDeadline);
        e.VolunteerHours = b.Kind == "Volunteer" ? b.VolunteerHours ?? (decimal)Math.Round((b.EndsAt - b.StartsAt).TotalHours, 1) : null;
    }

    [HttpPost]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Create(EventInput body, CancellationToken ct)
    {
        if (Validate(body) is { } err) return err;
        var e = new CompanyEvent { CreatedByUserId = Me.UserId, CreatedByName = Me.Name };
        Apply(e, body);
        _db.Events.Add(e);
        await _db.SaveChangesAsync(ct);
        if (body.Announce)
            foreach (var p in await People.ListAsync(Tenant, ct))
                await _notify.InAppAsync(Tenant, p.Id, $"Yeni etkinlik: {e.Title}", $"{(e.StartsAt.AddHours(3)):dd.MM.yyyy HH:mm}{(e.Location is null ? "" : " · " + e.Location)}. Katılmak için Etkinlikler sayfasına bakın.", "events.new", ct);
        return Ok(Dto(e, [], null));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Update(Guid id, EventInput body, CancellationToken ct)
    {
        var e = await _db.Events.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null) return NotFound();
        if (Validate(body) is { } err) return err;
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await LockAsync(id, ct);
        Apply(e, body);
        await _db.SaveChangesAsync(ct);
        await PromoteAsync(e, ct);
        await tx.CommitAsync(ct);
        return Ok(Dto(e, await _db.EventRsvps.AsNoTracking().Where(r => r.EventId == id).ToListAsync(ct), null));
    }

    /// <summary>İK: etkinliği iptal eder; katılacaklara bildirim gider.</summary>
    [HttpPost("{id:guid}/cancel")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var e = await _db.Events.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null || e.Status == "Cancelled") return NotFound();
        e.Status = "Cancelled";
        await _db.SaveChangesAsync(ct);
        foreach (var r in await _db.EventRsvps.AsNoTracking().Where(r => r.EventId == id).ToListAsync(ct))
            await _notify.InAppAsync(Tenant, r.EmployeeId, $"Etkinlik iptal edildi: {e.Title}", $"{(e.StartsAt.AddHours(3)):dd.MM.yyyy HH:mm} tarihli etkinlik iptal edildi.", "events.cancelled", ct);
        return Ok(new { ok = true });
    }

    public sealed record RsvpInput(bool Going);

    /// <summary>Katılım: kontenjan doluysa bekleme listesi; vazgeçince sıradaki otomatik alınır.</summary>
    [HttpPost("{id:guid}/rsvp")]
    public async Task<IActionResult> Rsvp(Guid id, RsvpInput body, CancellationToken ct)
    {
        var me = await MyPersonAsync(ct);
        if (me is null) return BadRequest(new { message = "Katılım için çalışan kaydınız olmalı" });
        var e = await _db.Events.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null) return NotFound();
        if (e.Status != "Published") return BadRequest(new { message = "Etkinlik iptal edildi" });
        if (e.StartsAt <= DateTime.UtcNow) return BadRequest(new { message = "Etkinlik başladı ya da geçti" });
        if (body.Going && e.RsvpDeadline is { } dl && dl < DateTime.UtcNow) return BadRequest(new { message = "Son katılım tarihi geçti" });
        // Kontenjan sayımı, kayıt ve sıradakini alma etkinlik başına kilit altında tek işlemde: aynı anda gelen
        // iki katılım son yeri birlikte alamaz, aynı kişinin çift tıklaması ikinci kayıt açmaz.
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await LockAsync(id, ct);
        var mine = await _db.EventRsvps.FirstOrDefaultAsync(r => r.EventId == id && r.EmployeeId == me.Id, ct);
        string? status;
        if (!body.Going)
        {
            status = null;
            if (mine is not null)
            {
                _db.EventRsvps.Remove(mine);
                await _db.SaveChangesAsync(ct);
                await PromoteAsync(e, ct);
            }
        }
        else if (mine is not null) status = mine.Status;
        else
        {
            var going = await _db.EventRsvps.CountAsync(r => r.EventId == id && r.Status == "Going", ct);
            var r = new EventRsvp { EventId = id, EmployeeId = me.Id, Name = me.Name, Status = e.Capacity is { } cap && going >= cap ? "Waitlist" : "Going" };
            _db.EventRsvps.Add(r);
            await _db.SaveChangesAsync(ct);
            status = r.Status;
        }
        await tx.CommitAsync(ct);
        return Ok(new { status });
    }

    /// <summary>Etkinlik başına işlem kilidi (işlem bitince kendiliğinden bırakılır).</summary>
    private Task LockAsync(Guid eventId, CancellationToken ct) =>
        _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtext({0}))", [$"event:{Tenant}:{eventId}"], ct);

    /// <summary>Boşalan yerlere bekleme listesinden sırayla alır ve haber verir.</summary>
    private async Task PromoteAsync(CompanyEvent e, CancellationToken ct)
    {
        if (e.Status != "Published") return;
        var all = await _db.EventRsvps.Where(r => r.EventId == e.Id).OrderBy(r => r.CreatedAt).ToListAsync(ct);
        var free = e.Capacity is { } cap ? cap - all.Count(r => r.Status == "Going") : int.MaxValue;
        foreach (var w in all.Where(r => r.Status == "Waitlist").Take(Math.Max(0, free)))
        {
            w.Status = "Going"; w.UpdatedAt = DateTime.UtcNow;
            await _notify.InAppAsync(Tenant, w.EmployeeId, $"Yer açıldı: {e.Title}", $"Bekleme listesindeydiniz; {(e.StartsAt.AddHours(3)):dd.MM.yyyy HH:mm} tarihli etkinliğe katılımınız onaylandı.", "events.promoted", ct);
        }
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>İK: katılımcı listesi (bekleme listesi dahil).</summary>
    [HttpGet("{id:guid}/attendees")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Attendees(Guid id, CancellationToken ct) =>
        Ok(await _db.EventRsvps.AsNoTracking().Where(r => r.EventId == id).OrderBy(r => r.Status).ThenBy(r => r.CreatedAt)
            .Select(r => new { r.Id, r.EmployeeId, r.Name, r.Status, r.Attended, r.CreatedAt }).ToListAsync(ct));

    public sealed record AttendanceInput(List<Guid> EmployeeIds);

    /// <summary>İK: etkinlik sonrası katılımı işaretler (gönüllülük saatleri buradan sayılır).</summary>
    [HttpPut("{id:guid}/attendance")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Attendance(Guid id, AttendanceInput body, CancellationToken ct)
    {
        var e = await _db.Events.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null) return NotFound();
        if (e.StartsAt > DateTime.UtcNow) return BadRequest(new { message = "Katılım etkinlik başladıktan sonra işaretlenir" });
        var rows = await _db.EventRsvps.Where(r => r.EventId == id && r.Status == "Going").ToListAsync(ct);
        foreach (var r in rows) r.Attended = body.EmployeeIds.Contains(r.EmployeeId);
        await _db.SaveChangesAsync(ct);
        return Ok(new { attended = rows.Count(r => r.Attended) });
    }

    /// <summary>Gönüllülük özeti: yıl içinde katılımı onaylanmış gönüllülük etkinliklerinin saatleri.</summary>
    [HttpGet("volunteering")]
    public async Task<IActionResult> Volunteering([FromQuery] int? year, CancellationToken ct)
    {
        var y = year ?? DateTime.UtcNow.Year;
        var from = new DateTime(y, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = from.AddYears(1);
        var events = await _db.Events.AsNoTracking().Where(e => e.Kind == "Volunteer" && e.Status == "Published" && e.StartsAt >= from && e.StartsAt < to).ToListAsync(ct);
        var ids = events.Select(e => e.Id).ToList();
        var hours = events.ToDictionary(e => e.Id, e => e.VolunteerHours ?? 0);
        var attended = await _db.EventRsvps.AsNoTracking().Where(r => ids.Contains(r.EventId) && r.Attended).ToListAsync(ct);
        var me = (await MyPersonAsync(ct))?.Id;
        var people = attended.GroupBy(r => r.EmployeeId).Select(g => new { employeeId = g.Key, name = g.First().Name, hours = g.Sum(r => hours[r.EventId]), events = g.Count() })
            .OrderByDescending(x => x.hours).ToList();
        return Ok(new
        {
            year = y, totalHours = people.Sum(p => p.hours), volunteers = people.Count, events = events.Count,
            mine = people.FirstOrDefault(p => p.employeeId == me)?.hours ?? 0, top = people.Take(10),
        });
    }

    /// <summary>Takvime ekle (.ics).</summary>
    [HttpGet("{id:guid}/ics")]
    public async Task<IActionResult> Ics(Guid id, CancellationToken ct)
    {
        var e = await _db.Events.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null) return NotFound();
        static string D(DateTime t) => t.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'");
        static string Esc(string? s) => (s ?? "").Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\n", "\\n");
        var ics = $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//HR360//Etkinlik//TR\r\nBEGIN:VEVENT\r\nUID:{e.Id}@hr360\r\nDTSTAMP:{D(DateTime.UtcNow)}\r\nDTSTART:{D(e.StartsAt)}\r\nDTEND:{D(e.EndsAt)}\r\nSUMMARY:{Esc(e.Title)}\r\nDESCRIPTION:{Esc(e.Description)}{(e.OnlineUrl is null ? "" : Esc("\n" + e.OnlineUrl))}\r\nLOCATION:{Esc(e.Location ?? e.OnlineUrl)}\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        return File(Encoding.UTF8.GetBytes(ics), "text/calendar", "etkinlik.ics");
    }
}

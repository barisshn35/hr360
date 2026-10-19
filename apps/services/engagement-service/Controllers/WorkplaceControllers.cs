using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using EngagementService.Data;
using EngagementService.Infrastructure;
using EngagementService.Models;

namespace EngagementService.Controllers;

/* ======================================================================
 * Ofis: "kim nerede" (günlük çalışma yeri beyanı) + masa/oda rezervasyonu.
 * İzinli çalışanlar leave_requests'ten otomatik "İzinli" görünür.
 * ==================================================================== */
[Route("api/workplace")]
[Authorize]
public class WorkplaceController : AppController
{
    private static readonly string[] Modes = { "Office", "Remote", "Travel", "Off" };
    private readonly EngagementDbContext _db;
    public WorkplaceController(EngagementDbContext db) => _db = db;

    /* ------------------------------------------------------- masa / oda */

    [HttpGet("desks")]
    public async Task<IActionResult> Desks(CancellationToken ct) =>
        Ok(await _db.Desks.AsNoTracking().Where(d => d.IsActive).OrderBy(d => d.Kind).ThenBy(d => d.Floor).ThenBy(d => d.Code).ToListAsync(ct));

    public record DeskInput(string Code, string Name, string Kind, string? Floor, string? Zone, int Capacity, List<string>? Features);

    [HttpPost("desks")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> CreateDesk(DeskInput body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Code) || string.IsNullOrWhiteSpace(body.Name))
            return BadRequest(new { message = "Kod ve ad zorunlu." });
        if (body.Kind is not ("Desk" or "Room")) return BadRequest(new { message = "Tür Desk veya Room olmalı." });
        if (await _db.Desks.AnyAsync(d => d.Code == body.Code, ct)) return Conflict(new { message = "Bu kod zaten var." });
        var d = new Desk
        {
            Code = body.Code.Trim(), Name = body.Name.Trim(), Kind = body.Kind, Floor = body.Floor, Zone = body.Zone,
            Capacity = Math.Clamp(body.Capacity, 1, 200), Features = body.Features ?? new(),
        };
        _db.Desks.Add(d);
        await _db.SaveChangesAsync(ct);
        return Ok(d);
    }

    [HttpDelete("desks/{id:guid}")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> DeleteDesk(Guid id, CancellationToken ct)
    {
        var d = await _db.Desks.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (d is null) return NotFound();
        d.IsActive = false;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Boş ofis için örnek yerleşim: 2 kat, 12 masa, 3 toplantı odası.</summary>
    [HttpPost("desks/sample")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Sample(CancellationToken ct)
    {
        if (await _db.Desks.AnyAsync(ct)) return Conflict(new { message = "Ofis planı zaten tanımlı." });
        var zones = new[] { "Pencere", "Orta", "Sessiz" };
        for (var floor = 1; floor <= 2; floor++)
            for (var i = 1; i <= 6; i++)
                _db.Desks.Add(new Desk
                {
                    Code = $"K{floor}-M{i:00}", Name = $"Masa {floor}.{i}", Kind = "Desk", Floor = $"{floor}. kat",
                    Zone = zones[(i - 1) / 2], Features = i % 2 == 0 ? new() { "Çift monitör" } : new() { "Ayarlanabilir masa" },
                });
        _db.Desks.Add(new Desk { Code = "ODA-BOGAZ", Name = "Boğaz", Kind = "Room", Floor = "1. kat", Capacity = 8, Features = new() { "Ekran", "Video konferans" } });
        _db.Desks.Add(new Desk { Code = "ODA-KAPADOKYA", Name = "Kapadokya", Kind = "Room", Floor = "2. kat", Capacity = 4, Features = new() { "Beyaz tahta" } });
        _db.Desks.Add(new Desk { Code = "ODA-EFES", Name = "Efes", Kind = "Room", Floor = "2. kat", Capacity = 12, Features = new() { "Ekran", "Video konferans", "Projeksiyon" } });
        await _db.SaveChangesAsync(ct);
        return Ok(new { created = 15 });
    }

    [HttpGet("bookings")]
    public async Task<IActionResult> Bookings([FromQuery] DateOnly? date, CancellationToken ct)
    {
        var day = date ?? DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
        var rows = await _db.DeskBookings.AsNoTracking().Where(b => b.Date == day).OrderBy(b => b.StartMinute).ToListAsync(ct);
        return Ok(rows.Select(b => new { b.Id, b.DeskId, b.PersonName, b.EmployeeId, b.Date, b.StartMinute, b.EndMinute, b.Title, mine = b.UserId == Me.UserId }));
    }

    public record BookingInput(Guid DeskId, DateOnly Date, int StartMinute, int EndMinute, string? Title);

    [HttpPost("bookings")]
    public async Task<IActionResult> Book(BookingInput body, CancellationToken ct)
    {
        var me = await MyPersonAsync(ct);
        var (error, b, _) = await BookCoreAsync(_db, new Actor(Me.UserId, me?.Id, me?.Name ?? Me.Name), body, null, ct);
        if (error is not null) return error.ToResult();
        await HttpContext.RequestServices.GetRequiredService<AppCache>().BumpAsync("presence", Tenant);
        return Ok(new { b!.Id });
    }

    public static DateOnly TodayTr => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));

    /// <summary>Rezervasyon kuralları (saf): geçerli saat aralığı, bugün – 30 gün sonrası.</summary>
    public static RuleError? ValidateBooking(DateOnly date, int startMinute, int endMinute, DateOnly today)
    {
        if (startMinute < 0 || endMinute > 24 * 60 || endMinute <= startMinute)
            return RuleError.Bad("Saat aralığı geçersiz.", "time_range");
        if (date < today) return RuleError.Bad("Geçmiş bir güne rezervasyon yapılamaz.", "date_range");
        if (date > today.AddDays(30)) return RuleError.Bad("En fazla 30 gün sonrası için rezervasyon yapılabilir.", "date_range");
        return null;
    }

    /// <summary>
    /// Web ucu ve sohbet botu (/api/internal/chat/desk-book) için ortak rezervasyon: masa/oda etkin olmalı,
    /// aralık çakışmamalı, kişinin aynı saatte tek masası olur; masa ayıran o gün "ofiste" görünür.
    /// <paramref name="kind"/> verilirse yalnızca o türdeki kaynak ayrılabilir.
    /// </summary>
    internal static async Task<(RuleError? Error, DeskBooking? Booking, Desk? Desk)> BookCoreAsync(EngagementDbContext db, Actor actor,
        BookingInput body, string? kind, CancellationToken ct)
    {
        if (ValidateBooking(body.Date, body.StartMinute, body.EndMinute, TodayTr) is { } invalid) return (invalid, null, null);
        var desk = await db.Desks.AsNoTracking().FirstOrDefaultAsync(d => d.Id == body.DeskId && d.IsActive, ct);
        if (desk is null || (kind is not null && desk.Kind != kind)) return (RuleError.NotFound("Masa/oda bulunamadı."), null, null);

        var clash = await db.DeskBookings.AnyAsync(b => b.DeskId == body.DeskId && b.Date == body.Date
            && b.StartMinute < body.EndMinute && body.StartMinute < b.EndMinute, ct);
        if (clash) return (RuleError.Conflict($"{desk.Name} bu saat aralığında dolu.", "taken"), null, null);
        if (desk.Kind == "Desk")
        {
            var mineSameDay = await db.DeskBookings.AnyAsync(b => (b.UserId == actor.UserId || (actor.EmployeeId != null && b.EmployeeId == actor.EmployeeId))
                && b.Date == body.Date && b.StartMinute < body.EndMinute && body.StartMinute < b.EndMinute
                && db.Desks.Any(d => d.Id == b.DeskId && d.Kind == "Desk"), ct);
            if (mineSameDay) return (RuleError.Conflict("Bu saatlerde zaten bir masanız var.", "has_desk"), null, null);
        }

        var booking = new DeskBooking
        {
            DeskId = desk.Id, UserId = actor.UserId, EmployeeId = actor.EmployeeId, PersonName = actor.Name,
            Date = body.Date, StartMinute = body.StartMinute, EndMinute = body.EndMinute, Title = body.Title,
        };
        db.DeskBookings.Add(booking);

        // Masa ayıran kişi o gün ofistedir — "kim nerede" kendiliğinden güncellenir.
        if (desk.Kind == "Desk")
        {
            var pres = await db.Presence.FirstOrDefaultAsync(p => p.UserId == actor.UserId && p.Date == body.Date, ct);
            if (pres is null)
                db.Presence.Add(new Presence { UserId = actor.UserId, EmployeeId = actor.EmployeeId, PersonName = booking.PersonName, Date = body.Date, Mode = "Office" });
            else pres.Mode = "Office";
        }
        await db.SaveChangesAsync(ct);
        return (null, booking, desk);
    }

    [HttpDelete("bookings/{id:guid}")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var me = Me;
        var status = await CancelCoreAsync(_db, id, b => b.UserId == me.UserId || me.IsHr, ct);
        // Oda/masa çizelgesi ve "kim nerede" aynı önbelleği paylaşır; iptal de görünümü tazelemeli.
        if (status == StatusCodes.Status204NoContent)
            await HttpContext.RequestServices.GetRequiredService<AppCache>().BumpAsync("presence", Tenant);
        return status switch { StatusCodes.Status404NotFound => NotFound(), StatusCodes.Status403Forbidden => Forbid(), _ => NoContent() };
    }

    /// <summary>Ortak iptal: 204 silindi, 404 yok, 403 iptal yetkisi yok.</summary>
    internal static async Task<int> CancelCoreAsync(EngagementDbContext db, Guid id, Func<DeskBooking, bool> mayCancel, CancellationToken ct)
    {
        var b = await db.DeskBookings.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (b is null) return StatusCodes.Status404NotFound;
        if (!mayCancel(b)) return StatusCodes.Status403Forbidden;
        db.DeskBookings.Remove(b);
        await db.SaveChangesAsync(ct);
        return StatusCodes.Status204NoContent;
    }

    /* ------------------------------------------------------- kim nerede */

    /// <summary>
    /// Kiracının tamamı için aynı olan "kim nerede" tablosu; 30 sn önbellekte tutulur,
    /// biri yerini güncelleyince kiracının önbelleği eskitilir (sürüm artışı).
    /// </summary>
    [HttpGet("presence")]
    public async Task<IActionResult> Presence([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct)
    {
        var start = from ?? StartOfWeek(DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3)));
        var end = to ?? start.AddDays(4);
        if (end.DayNumber - start.DayNumber > 31) end = start.AddDays(31);
        var cache = HttpContext.RequestServices.GetRequiredService<AppCache>();
        var ver = await cache.VersionAsync("presence", Tenant);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
        return await cache.JsonAsync(HttpContext, "presence", Tenant, $"{ver}:{start:yyyyMMdd}:{end:yyyyMMdd}:{today:yyyyMMdd}",
            TimeSpan.FromSeconds(30), c => BuildPresenceAsync(start, end, c), ct);
    }

    private async Task<object> BuildPresenceAsync(DateOnly start, DateOnly end, CancellationToken ct)
    {

        var people = await People.ListAsync(Tenant, ct);
        var entries = await _db.Presence.AsNoTracking().Where(p => p.Date >= start && p.Date <= end).ToListAsync(ct);
        var leaves = await Db.QueryAsync(
            """
            SELECT "EmployeeId", "StartDate", "EndDate", "Type" FROM leave_requests
            WHERE "TenantSlug" = $1 AND "Status" IN ('Approved','Submitted') AND "StartDate" <= $3 AND "EndDate" >= $2
            """, r => (Emp: r.GetGuid(0), From: r.GetFieldValue<DateOnly>(1), To: r.GetFieldValue<DateOnly>(2), Type: r.GetString(3)),
            ct, Tenant, start, end);

        var days = Enumerable.Range(0, end.DayNumber - start.DayNumber + 1).Select(i => start.AddDays(i)).ToList();
        var rows = people.Select(p => new
        {
            employeeId = p.Id, name = p.Name, department = p.Department, position = p.Position,
            days = days.Select(d =>
            {
                var leave = leaves.FirstOrDefault(l => l.Emp == p.Id && l.From <= d && l.To >= d);
                // İzin türü (ör. hastalık) şirketteki herkese açık bu tabloda gösterilmez; yalnızca "izinli".
                if (leave != default) return new { date = d, mode = "Leave", note = (string?)null };
                var e = entries.FirstOrDefault(x => (x.EmployeeId == p.Id || (p.UserId != null && x.UserId == p.UserId)) && x.Date == d);
                return new { date = d, mode = e?.Mode ?? "Unknown", note = e?.Note };
            }).ToList(),
        }).ToList();

        var todayKey = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
        var summary = Modes.Append("Leave").Append("Unknown").ToDictionary(m => m,
            m => rows.Count(r => r.days.FirstOrDefault(d => d.date == todayKey)?.mode == m));
        return new { from = start, to = end, days, people = rows, today = summary };
    }

    public record PresenceInput(DateOnly Date, string Mode, string? Note);

    [HttpPut("presence")]
    public async Task<IActionResult> SetPresence(PresenceInput body, CancellationToken ct)
    {
        // "Unknown" = bildirimi geri al (ör. masa iptalinden sonra otomatik "Ofiste" kaydını kaldırmak).
        if (body.Mode != "Unknown" && !Modes.Contains(body.Mode)) return BadRequest(new { message = "Geçersiz çalışma yeri." });
        var me = await MyPersonAsync(ct);
        var e = await _db.Presence.FirstOrDefaultAsync(p => p.UserId == Me.UserId && p.Date == body.Date, ct);
        if (body.Mode == "Unknown")
        {
            if (e is not null)
            {
                _db.Presence.Remove(e);
                await _db.SaveChangesAsync(ct);
                await HttpContext.RequestServices.GetRequiredService<AppCache>().BumpAsync("presence", Tenant);
            }
            return Ok(new { body.Date, Mode = "Unknown", Note = (string?)null });
        }
        if (e is null)
        {
            e = new Presence { UserId = Me.UserId, EmployeeId = me?.Id, PersonName = me?.Name ?? Me.Name, Date = body.Date };
            _db.Presence.Add(e);
        }
        e.Mode = body.Mode;
        e.Note = body.Note?.Trim();
        await _db.SaveChangesAsync(ct);
        await HttpContext.RequestServices.GetRequiredService<AppCache>().BumpAsync("presence", Tenant);
        return Ok(new { e.Date, e.Mode, e.Note });
    }

    private static DateOnly StartOfWeek(DateOnly d) => d.AddDays(-(((int)d.DayOfWeek + 6) % 7));
}

/* ======================================================================
 * Mentorluk: profil (ne öğretebilirim / ne öğrenmek istiyorum), basit
 * eşleştirme puanı (beceri kesişimi + kapasite), talep → kabul akışı.
 * ==================================================================== */
[Route("api/mentorship")]
[Authorize]
public class MentorshipController : AppController
{
    private readonly EngagementDbContext _db;
    private readonly Notifier _notify;
    public MentorshipController(EngagementDbContext db, Notifier notify) { _db = db; _notify = notify; }

    [HttpGet("profiles")]
    public async Task<IActionResult> Profiles(CancellationToken ct)
    {
        var profiles = await _db.MentorProfiles.AsNoTracking().ToListAsync(ct);
        var active = await _db.Mentorships.AsNoTracking().Where(m => m.Status == "Active").ToListAsync(ct);
        return Ok(profiles.Select(p => new
        {
            p.UserId, p.PersonName, p.EmployeeId, p.IsMentor, p.IsMentee, p.Offers, p.Wants, p.Capacity, p.Bio,
            activeMentees = active.Count(m => m.MentorUserId == p.UserId), isMe = p.UserId == Me.UserId,
        }));
    }

    [HttpGet("me")]
    public async Task<IActionResult> Mine(CancellationToken ct) =>
        Ok(await _db.MentorProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == Me.UserId, ct));

    public record ProfileInput(bool IsMentor, bool IsMentee, List<string> Offers, List<string> Wants, int Capacity, string? Bio);

    [HttpPut("me")]
    public async Task<IActionResult> SaveMine(ProfileInput body, CancellationToken ct)
    {
        var me = await MyPersonAsync(ct);
        var p = await _db.MentorProfiles.FirstOrDefaultAsync(x => x.UserId == Me.UserId, ct);
        if (p is null) { p = new MentorProfile { UserId = Me.UserId }; _db.MentorProfiles.Add(p); }
        p.EmployeeId = me?.Id;
        p.PersonName = me?.Name ?? Me.Name;
        p.IsMentor = body.IsMentor;
        p.IsMentee = body.IsMentee;
        p.Offers = Clean(body.Offers);
        p.Wants = Clean(body.Wants);
        p.Capacity = Math.Clamp(body.Capacity, 1, 10);
        p.Bio = body.Bio?.Trim();
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(p);
    }

    /// <summary>
    /// Benim için önerilen mentorlar. Puan = istediğim konuların mentorun
    /// sunduklarıyla kesişim oranı (%70) + mentorun boş kapasitesi (%20) +
    /// farklı departmandan olma (%10, yatay öğrenme). Şeffaf ve açıklanabilir.
    /// </summary>
    [HttpGet("matches")]
    public async Task<IActionResult> Matches(CancellationToken ct)
    {
        var mine = await _db.MentorProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == Me.UserId, ct);
        if (mine is null || mine.Wants.Count == 0) return Ok(Array.Empty<object>());
        var people = (await People.ListAsync(Tenant, ct)).Where(p => p.UserId != null).ToDictionary(p => p.UserId!);
        var myDept = people.GetValueOrDefault(Me.UserId)?.Department;
        var active = await _db.Mentorships.AsNoTracking().Where(m => m.Status == "Active" || m.Status == "Requested").ToListAsync(ct);
        var mentors = await _db.MentorProfiles.AsNoTracking().Where(p => p.IsMentor && p.UserId != Me.UserId).ToListAsync(ct);
        var wants = mine.Wants.Select(w => w.ToLowerInvariant()).ToHashSet();

        var result = mentors.Select(m =>
        {
            var offers = m.Offers.Select(o => o.ToLowerInvariant()).ToHashSet();
            var common = m.Offers.Where(o => wants.Contains(o.ToLowerInvariant())).ToList();
            var load = active.Count(a => a.MentorUserId == m.UserId && a.Status == "Active");
            var free = Math.Max(0, m.Capacity - load);
            var dept = people.GetValueOrDefault(m.UserId)?.Department;
            var score = (int)Math.Round(70.0 * common.Count / wants.Count + (free > 0 ? 20.0 * free / m.Capacity : 0) + (dept != myDept ? 10 : 0));
            return new
            {
                m.UserId, m.PersonName, department = dept, m.Offers, m.Bio, common, freeSlots = free, score,
                pending = active.Any(a => a.MentorUserId == m.UserId && a.MenteeUserId == Me.UserId),
            };
        }).Where(x => x.common.Count > 0).OrderByDescending(x => x.score).Take(10);
        return Ok(result);
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var q = _db.Mentorships.AsNoTracking();
        if (!Me.IsHr) q = q.Where(m => m.MentorUserId == Me.UserId || m.MenteeUserId == Me.UserId);
        var rows = await q.OrderByDescending(m => m.CreatedAt).ToListAsync(ct);
        return Ok(rows.Select(m => new
        {
            m.Id, m.MentorUserId, m.MentorName, m.MenteeUserId, m.MenteeName, m.Goal, m.Status, m.MatchScore,
            m.CreatedAt, m.StartedAt, m.EndedAt, iAmMentor = m.MentorUserId == Me.UserId, iAmMentee = m.MenteeUserId == Me.UserId,
        }));
    }

    public record RequestInput(string MentorUserId, string? Goal, int MatchScore);

    [HttpPost]
    public async Task<IActionResult> CreateRequest(RequestInput body, CancellationToken ct)
    {
        if (body.MentorUserId == Me.UserId) return BadRequest(new { message = "Kendinize mentorluk talebi gönderemezsiniz." });
        var mentor = await _db.MentorProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == body.MentorUserId && p.IsMentor, ct);
        if (mentor is null) return NotFound(new { message = "Mentor bulunamadı." });
        if (await _db.Mentorships.AnyAsync(m => m.MentorUserId == body.MentorUserId && m.MenteeUserId == Me.UserId
                && (m.Status == "Requested" || m.Status == "Active"), ct))
            return Conflict(new { message = "Bu mentorla zaten açık bir eşleşmeniz var." });
        var me = await MyPersonAsync(ct);
        var m = new Mentorship
        {
            MentorUserId = mentor.UserId, MentorName = mentor.PersonName, MenteeUserId = Me.UserId,
            MenteeName = me?.Name ?? Me.Name, Goal = body.Goal?.Trim(), MatchScore = Math.Clamp(body.MatchScore, 0, 100),
        };
        _db.Mentorships.Add(m);
        await _db.SaveChangesAsync(ct);
        if (mentor.EmployeeId is { } mentorEmp)
            await _notify.InAppAsync(Tenant, mentorEmp, $"{m.MenteeName} sizden mentorluk istiyor", m.Goal ?? "", "engagement.mentorship", ct);
        return Ok(new { m.Id });
    }

    [HttpPost("{id:guid}/{transition:regex(^(accept|decline|complete|cancel)$)}")]
    public async Task<IActionResult> Transition(Guid id, string transition, CancellationToken ct)
    {
        var m = await _db.Mentorships.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (m is null) return NotFound();
        var isMentor = m.MentorUserId == Me.UserId;
        var isMentee = m.MenteeUserId == Me.UserId;
        switch (transition)
        {
            case "accept" when (isMentor || Me.IsHr) && m.Status == "Requested":
                m.Status = "Active"; m.StartedAt = DateTime.UtcNow; break;
            case "decline" when (isMentor || Me.IsHr) && m.Status == "Requested":
                m.Status = "Declined"; m.EndedAt = DateTime.UtcNow; break;
            case "complete" when (isMentor || isMentee || Me.IsHr) && m.Status == "Active":
                m.Status = "Completed"; m.EndedAt = DateTime.UtcNow; break;
            case "cancel" when (isMentee || Me.IsHr) && m.Status == "Requested":
                m.Status = "Declined"; m.EndedAt = DateTime.UtcNow; break;
            default:
                return BadRequest(new { message = "Bu işlem bu durumda yapılamaz." });
        }
        await _db.SaveChangesAsync(ct);
        return Ok(new { m.Status });
    }

    private static List<string> Clean(IEnumerable<string>? items) =>
        (items ?? Enumerable.Empty<string>()).Select(s => s.Trim()).Where(s => s.Length is > 0 and <= 40)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToList();
}

/* ======================================================================
 * İç ilan panosu: recruitment-service'te yayınlanan ilanlar çalışanlara
 * da gösterilir; çalışan dışarıdan aday gibi değil, iç başvuru olarak başvurur.
 * ==================================================================== */
[Route("api/mobility")]
[Authorize]
public class MobilityController : AppController
{
    private static readonly string[] Statuses = { "Submitted", "Reviewing", "Interview", "Accepted", "Rejected", "Withdrawn" };
    private readonly EngagementDbContext _db;
    private readonly Notifier _notify;
    public MobilityController(EngagementDbContext db, Notifier notify) { _db = db; _notify = notify; }

    [HttpGet("postings")]
    public async Task<IActionResult> Postings(CancellationToken ct)
    {
        var postings = await Db.QueryAsync(
            """
            SELECT p."Id", p."Title", p."Description", p."EmploymentType", p."Headcount", p."PublishedAt", d."Name"
            FROM recruitment_job_postings p LEFT JOIN organization_departments d ON d."Id" = p."DepartmentId"
            WHERE p."TenantSlug" = $1 AND p."Status" = 'Published'
            ORDER BY p."PublishedAt" DESC NULLS LAST
            """, r => new
            {
                id = r.GetGuid(0), title = r.GetString(1), description = r.Str(2), employmentType = r.Str(3),
                headcount = r.GetInt32(4), publishedAt = r.Ts(5), department = r.Str(6),
            }, ct, Tenant);
        var mine = await _db.InternalApplications.AsNoTracking().Where(a => a.UserId == Me.UserId).ToListAsync(ct);
        var counts = await _db.InternalApplications.AsNoTracking().Where(a => a.Status != "Withdrawn")
            .GroupBy(a => a.JobPostingId).Select(g => new { g.Key, n = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.n, ct);
        return Ok(postings.Select(p => new
        {
            p.id, p.title, p.description, p.employmentType, p.headcount, p.publishedAt, p.department,
            internalApplicants = counts.GetValueOrDefault(p.id),
            myApplication = mine.Where(a => a.JobPostingId == p.id).Select(a => new { a.Id, a.Status, a.CreatedAt }).FirstOrDefault(),
        }));
    }

    public record ApplyInput(Guid JobPostingId, string? Motivation);

    [HttpPost("applications")]
    public async Task<IActionResult> Apply(ApplyInput body, CancellationToken ct)
    {
        var title = await Db.ScalarAsync(
            "SELECT \"Title\" FROM recruitment_job_postings WHERE \"TenantSlug\" = $1 AND \"Id\" = $2 AND \"Status\" = 'Published'",
            ct, Tenant, body.JobPostingId) as string;
        if (title is null) return NotFound(new { message = "İlan yayında değil." });
        var existing = await _db.InternalApplications.FirstOrDefaultAsync(a => a.JobPostingId == body.JobPostingId && a.UserId == Me.UserId, ct);
        if (existing is not null && existing.Status != "Withdrawn") return Conflict(new { message = "Bu ilana zaten başvurdunuz." });
        var me = await MyPersonAsync(ct);
        // İç ilan yalnızca çalışanlar içindir: kayıtsız hesap (ör. yalnızca yönetici) başvuramaz.
        if (me is null) return BadRequest(new { message = "Hesabınıza bağlı çalışan kaydı yok", code = "no_employee_record" });
        if (existing is null)
        {
            existing = new InternalApplication { JobPostingId = body.JobPostingId, UserId = Me.UserId };
            _db.InternalApplications.Add(existing);
        }
        existing.JobTitle = title;
        existing.EmployeeId = me.Id;
        existing.PersonName = me.Name;
        existing.Motivation = body.Motivation?.Trim();
        existing.Status = "Submitted";
        existing.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { existing.Id });
    }

    [HttpGet("applications")]
    public async Task<IActionResult> Applications(CancellationToken ct)
    {
        var q = _db.InternalApplications.AsNoTracking();
        if (!Me.IsManager) q = q.Where(a => a.UserId == Me.UserId);
        var rows = await q.OrderByDescending(a => a.UpdatedAt).ToListAsync(ct);
        var people = (await People.ListAsync(Tenant, ct)).ToDictionary(p => p.Id);
        return Ok(rows.Select(a => new
        {
            a.Id, a.JobPostingId, a.JobTitle, a.PersonName, a.EmployeeId, a.Motivation, a.Status, a.CreatedAt, a.UpdatedAt,
            currentPosition = a.EmployeeId is { } e ? people.GetValueOrDefault(e)?.Position : null,
            currentDepartment = a.EmployeeId is { } e2 ? people.GetValueOrDefault(e2)?.Department : null,
            mine = a.UserId == Me.UserId,
        }));
    }

    public record StatusInput(string Status);

    [HttpPatch("applications/{id:guid}")]
    public async Task<IActionResult> SetStatus(Guid id, StatusInput body, CancellationToken ct)
    {
        var a = await _db.InternalApplications.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null) return NotFound();
        if (!Statuses.Contains(body.Status)) return BadRequest(new { message = "Geçersiz durum." });
        var own = a.UserId == Me.UserId;
        if (body.Status == "Withdrawn" ? !own && !Me.IsHr : !Me.IsHr && !Me.Roles.Contains("ext-recruitment-candidates"))
            return Forbid();
        a.Status = body.Status;
        a.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        if (!own && a.EmployeeId is { } emp)
            await _notify.InAppAsync(Tenant, emp, $"İç başvurunuz güncellendi: {a.JobTitle}", $"Yeni durum: {StatusLabel(a.Status)}", "engagement.mobility", ct);
        return Ok(new { a.Status });
    }

    private static string StatusLabel(string s) => s switch
    {
        "Submitted" => "Alındı",
        "Reviewing" => "İnceleniyor",
        "Interview" => "Görüşme",
        "Accepted" => "Kabul edildi",
        "Rejected" => "Olumsuz",
        "Withdrawn" => "Geri çekildi",
        _ => s,
    };
}

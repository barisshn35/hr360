using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using EngagementService.Data;
using EngagementService.Infrastructure;
using EngagementService.Models;

namespace EngagementService.Controllers;

/* ======================================================================
 * 1:1 görüşme defteri. Yönetici ve çalışan ortak gündem/aksiyon listesi
 * tutar; "özel not" yalnızca yöneticiye görünür (çalışan yanıtında boş).
 * ==================================================================== */
[Route("api/one-on-ones")]
[Authorize]
public class OneOnOnesController : AppController
{
    private readonly EngagementDbContext _db;
    private readonly Notifier _notify;
    public OneOnOnesController(EngagementDbContext db, Notifier notify) { _db = db; _notify = notify; }

    private object Shape(OneOnOne o)
    {
        var isManager = o.ManagerUserId == Me.UserId;
        return new
        {
            o.Id, o.ManagerUserId, o.ManagerName, o.EmployeeId, o.EmployeeName, o.ScheduledAt, o.Status,
            o.Agenda, o.SharedNotes, privateNotes = isManager ? o.PrivateNotes : null, o.ActionItems, o.Mood,
            o.CreatedAt, iAmManager = isManager,
        };
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] Guid? employeeId, CancellationToken ct)
    {
        var q = _db.OneOnOnes.AsNoTracking().Where(o => o.ManagerUserId == Me.UserId || o.EmployeeUserId == Me.UserId);
        if (employeeId is not null) q = q.Where(o => o.EmployeeId == employeeId);
        var rows = await q.OrderByDescending(o => o.ScheduledAt).Take(200).ToListAsync(ct);
        return Ok(rows.Select(Shape));
    }

    /// <summary>Yöneticinin ekibi + her kişiyle son/sonraki görüşme (kimseyi unutma).</summary>
    [HttpGet("team")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> Team(CancellationToken ct)
    {
        var me = await MyPersonAsync(ct);
        var team = me is null ? new List<Person>() : await People.TeamOfAsync(Tenant, me.Id, ct);
        if (team.Count == 0 && Me.IsHr) team = (await People.ListAsync(Tenant, ct)).Where(p => p.Id != me?.Id).ToList();
        var meetings = await _db.OneOnOnes.AsNoTracking().Where(o => o.ManagerUserId == Me.UserId).ToListAsync(ct);
        var now = DateTime.UtcNow;
        return Ok(team.Select(p =>
        {
            var mine = meetings.Where(m => m.EmployeeId == p.Id).ToList();
            var last = mine.Where(m => m.Status == "Done").MaxBy(m => m.ScheduledAt);
            var next = mine.Where(m => m.Status == "Planned" && m.ScheduledAt >= now.AddHours(-2)).MinBy(m => m.ScheduledAt);
            return new
            {
                employeeId = p.Id, name = p.Name, position = p.Position, department = p.Department,
                lastMeetingAt = last?.ScheduledAt, nextMeetingAt = next?.ScheduledAt,
                daysSinceLast = last is null ? (int?)null : (int)(now - last.ScheduledAt).TotalDays,
                openActions = mine.SelectMany(m => m.ActionItems).Count(a => !a.Done),
                lastMood = last?.Mood,
            };
        }).OrderByDescending(x => x.daysSinceLast ?? 9999));
    }

    public record CreateInput(Guid EmployeeId, DateTime ScheduledAt, List<string>? Agenda);

    /// <summary>Gelen zaman UTC kabul edilir (istemci ISO "Z" gönderir); 5 dakikalık pay tanınır.</summary>
    public static bool IsPast(DateTime when, DateTime? now = null) =>
        DateTime.SpecifyKind(when, DateTimeKind.Utc) < (now ?? DateTime.UtcNow).AddMinutes(-5);

    [HttpPost]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> Create(CreateInput body, CancellationToken ct)
    {
        var emp = await People.FindAsync(Tenant, body.EmployeeId, ct);
        if (emp is null) return NotFound(new { message = "Çalışan bulunamadı." });
        var me = await MyPersonAsync(ct);
        if (me?.Id == emp.Id) return BadRequest(new { message = "Kendinizle 1:1 planlayamazsınız." });
        // Planlı görüşme geçmişe kurulamaz (saat dilimi/saat farkı için 5 dk pay).
        if (IsPast(body.ScheduledAt)) return BadRequest(new { message = "Geçmiş bir tarihe görüşme planlanamaz." });

        // Önceki görüşmenin bitmemiş aksiyonları yeni gündeme taşınır.
        var carry = await _db.OneOnOnes.AsNoTracking()
            .Where(o => o.ManagerUserId == Me.UserId && o.EmployeeId == emp.Id)
            .OrderByDescending(o => o.ScheduledAt).FirstOrDefaultAsync(ct);
        var o = new OneOnOne
        {
            ManagerUserId = Me.UserId, ManagerName = me?.Name ?? Me.Name, EmployeeId = emp.Id, EmployeeUserId = emp.UserId,
            EmployeeName = emp.Name, ScheduledAt = DateTime.SpecifyKind(body.ScheduledAt, DateTimeKind.Utc),
            Agenda = (body.Agenda ?? new()).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => new AgendaItem { Text = a.Trim(), By = me?.Name ?? Me.Name }).ToList(),
            ActionItems = carry?.ActionItems.Where(a => !a.Done).Select(a => new AgendaItem { Text = a.Text, By = a.By, Due = a.Due }).ToList() ?? new(),
        };
        _db.OneOnOnes.Add(o);
        await _db.SaveChangesAsync(ct);
        await _notify.InAppAsync(Tenant, emp.Id, $"{o.ManagerName} ile 1:1 planlandı",
            $"{o.ScheduledAt.AddHours(3):dd.MM.yyyy HH:mm} — gündeme madde ekleyebilirsiniz.", "engagement.one-on-one", ct);
        return Ok(Shape(o));
    }

    public record UpdateInput(
        DateTime? ScheduledAt, string? Status, List<AgendaItem>? Agenda, string? SharedNotes, string? PrivateNotes,
        List<AgendaItem>? ActionItems, int? Mood);

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UpdateInput body, CancellationToken ct)
    {
        var o = await _db.OneOnOnes.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (o is null) return NotFound();
        var isManager = o.ManagerUserId == Me.UserId;
        var isEmployee = o.EmployeeUserId == Me.UserId;
        if (!isManager && !isEmployee) return Forbid();

        // Yazarı belirtilmemiş yeni maddeler ekleyen kişiye yazılır ("ekleyen" etiketi).
        string? myName = null;
        async Task<List<AgendaItem>> WithAuthorAsync(List<AgendaItem> items)
        {
            var list = items.Where(a => !string.IsNullOrWhiteSpace(a.Text)).ToList();
            if (list.Any(a => string.IsNullOrWhiteSpace(a.By)))
            {
                myName ??= (await MyPersonAsync(ct))?.Name ?? Me.Name;
                foreach (var a in list.Where(a => string.IsNullOrWhiteSpace(a.By))) a.By = myName;
            }
            return list;
        }
        if (body.Agenda is not null) { o.Agenda = await WithAuthorAsync(body.Agenda); _db.Entry(o).Property(x => x.Agenda).IsModified = true; }
        if (body.ActionItems is not null) { o.ActionItems = await WithAuthorAsync(body.ActionItems); _db.Entry(o).Property(x => x.ActionItems).IsModified = true; }
        if (body.SharedNotes is not null) o.SharedNotes = body.SharedNotes;
        if (isManager)
        {
            if (body.PrivateNotes is not null) o.PrivateNotes = body.PrivateNotes;
            if (body.ScheduledAt is not null && body.ScheduledAt.Value != o.ScheduledAt && (body.Status ?? o.Status) == "Planned" && IsPast(body.ScheduledAt.Value))
                return BadRequest(new { message = "Geçmiş bir tarihe görüşme planlanamaz." });
            if (body.ScheduledAt is not null) o.ScheduledAt = DateTime.SpecifyKind(body.ScheduledAt.Value, DateTimeKind.Utc);
            if (body.Status is "Planned" or "Done" or "Cancelled") o.Status = body.Status;
        }
        if (body.Mood is >= 1 and <= 5 && isEmployee) o.Mood = body.Mood;
        await _db.SaveChangesAsync(ct);
        return Ok(Shape(o));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var o = await _db.OneOnOnes.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (o is null) return NotFound();
        if (o.ManagerUserId != Me.UserId) return Forbid();
        _db.OneOnOnes.Remove(o);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Takvim daveti (.ics) — Outlook/Google Takvim'e eklenebilir.</summary>
    [HttpGet("{id:guid}/ics")]
    public async Task<IActionResult> Ics(Guid id, CancellationToken ct)
    {
        var o = await _db.OneOnOnes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (o is null) return NotFound();
        if (o.ManagerUserId != Me.UserId && o.EmployeeUserId != Me.UserId) return Forbid();
        var sb = new StringBuilder();
        sb.Append("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//HR360//1on1//TR\r\nMETHOD:PUBLISH\r\nBEGIN:VEVENT\r\n");
        sb.Append($"UID:{o.Id}@hr360\r\nDTSTAMP:{DateTime.UtcNow:yyyyMMddTHHmmssZ}\r\n");
        sb.Append($"DTSTART:{o.ScheduledAt:yyyyMMddTHHmmssZ}\r\nDTEND:{o.ScheduledAt.AddMinutes(30):yyyyMMddTHHmmssZ}\r\n");
        sb.Append($"SUMMARY:1:1 — {Escape(o.ManagerName)} / {Escape(o.EmployeeName)}\r\n");
        // Her madde ayrı kaçışlanır, sonra RFC 5545 satır sonu (\n) ile birleştirilir; birleşik metin yeniden
        // kaçışlanırsa ters bölü ikilenir ve takvimde "\n" düz metin olarak görünür.
        sb.Append($"DESCRIPTION:{string.Join("\\n", o.Agenda.Select(a => Escape("• " + a.Text)))}\r\n");
        sb.Append("END:VEVENT\r\nEND:VCALENDAR\r\n");
        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/calendar", $"1on1-{o.ScheduledAt:yyyyMMdd}.ics");
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace(",", "\\,").Replace(";", "\\;")
        .Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "");
}

/* ======================================================================
 * Ardıl planlama: kritik pozisyonlar, görevdeki kişi, aday havuzu ve
 * hazır olma seviyesi. Öneriler son performans puanına göre (yalnızca
 * öneri — karar İK'nın).
 * ==================================================================== */
[Route("api/succession")]
[Authorize(Policy = "RequireManagerOrAbove")]
public class SuccessionController : AppController
{
    private readonly EngagementDbContext _db;
    public SuccessionController(EngagementDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var plans = await _db.SuccessionPlans.AsNoTracking().OrderBy(p => p.PositionTitle).ToListAsync(ct);
        var scores = await LatestScoresAsync(ct);
        return Ok(plans.Select(p => new
        {
            p.Id, p.PositionTitle, p.DepartmentName, p.IncumbentEmployeeId, p.IncumbentName, p.Criticality, p.VacancyRisk,
            p.Notes, p.UpdatedAt,
            candidates = p.Candidates.Select(c => new { c.EmployeeId, c.Name, c.Readiness, c.Notes, score = scores.GetValueOrDefault(c.EmployeeId) }),
            benchStrength = p.Candidates.Count(c => c.Readiness == "ReadyNow") > 0 ? "Strong"
                : p.Candidates.Count > 0 ? "Developing" : "None",
        }));
    }

    public record PlanInput(string PositionTitle, string? DepartmentName, Guid? IncumbentEmployeeId, string Criticality,
        string VacancyRisk, List<SuccessorCandidate>? Candidates, string? Notes);

    [HttpPost]
    public async Task<IActionResult> Create(PlanInput body, CancellationToken ct)
    {
        var p = new SuccessionPlan();
        var err = await Apply(p, body, ct);
        if (err is not null) return err;
        _db.SuccessionPlans.Add(p);
        await _db.SaveChangesAsync(ct);
        return Ok(new { p.Id });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, PlanInput body, CancellationToken ct)
    {
        var p = await _db.SuccessionPlans.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return NotFound();
        var err = await Apply(p, body, ct);
        if (err is not null) return err;
        _db.Entry(p).Property(x => x.Candidates).IsModified = true;
        await _db.SaveChangesAsync(ct);
        return Ok(new { p.Id });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var p = await _db.SuccessionPlans.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return NotFound();
        _db.SuccessionPlans.Remove(p);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Bir pozisyon için aday önerisi: aynı departmandan, son performans puanı yüksek olanlar.</summary>
    [HttpGet("suggest")]
    public async Task<IActionResult> Suggest([FromQuery] Guid? incumbentEmployeeId, [FromQuery] string? department, CancellationToken ct)
    {
        var people = await People.ListAsync(Tenant, ct);
        var scores = await LatestScoresAsync(ct);
        var dept = department ?? people.FirstOrDefault(p => p.Id == incumbentEmployeeId)?.Department;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return Ok(people.Where(p => p.Id != incumbentEmployeeId)
            .Select(p => new
            {
                employeeId = p.Id, name = p.Name, position = p.Position, department = p.Department,
                score = scores.GetValueOrDefault(p.Id), tenureYears = Math.Round((today.DayNumber - p.HireDate.DayNumber) / 365.0, 1),
                sameDepartment = dept != null && p.Department == dept,
            })
            .OrderByDescending(x => x.sameDepartment).ThenByDescending(x => x.score ?? 0).Take(8));
    }

    private async Task<Dictionary<Guid, decimal?>> LatestScoresAsync(CancellationToken ct) =>
        (await Db.QueryAsync(
            """
            SELECT DISTINCT ON ("EmployeeId") "EmployeeId", "Score" FROM performance_snapshots
            WHERE "TenantSlug" = $1 ORDER BY "EmployeeId", "CapturedAt" DESC
            """, r => (Id: r.GetGuid(0), Score: r.Dec(1)), ct, Tenant)).ToDictionary(x => x.Id, x => x.Score);

    private async Task<IActionResult?> Apply(SuccessionPlan p, PlanInput body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.PositionTitle)) return BadRequest(new { message = "Pozisyon adı zorunlu." });
        if (body.Criticality is not ("High" or "Medium" or "Low") || body.VacancyRisk is not ("High" or "Medium" or "Low"))
            return BadRequest(new { message = "Kritiklik/risk High, Medium veya Low olmalı." });
        Person? inc = null;
        if (body.IncumbentEmployeeId is { } incId)
        {
            inc = await People.FindAsync(Tenant, incId, ct);
            if (inc is null) return BadRequest(new { message = "Görevdeki çalışan bulunamadı." });
        }
        p.PositionTitle = body.PositionTitle.Trim();
        p.DepartmentName = body.DepartmentName ?? inc?.Department;
        p.IncumbentEmployeeId = inc?.Id;
        p.IncumbentName = inc?.Name;
        p.Criticality = body.Criticality;
        p.VacancyRisk = body.VacancyRisk;
        p.Candidates = (body.Candidates ?? new())
            .Where(c => c.Readiness is "ReadyNow" or "OneToTwoYears" or "ThreePlusYears")
            .GroupBy(c => c.EmployeeId).Select(g => g.First()).ToList();
        p.Notes = body.Notes;
        p.UpdatedAt = DateTime.UtcNow;
        return null;
    }
}

/* ======================================================================
 * Ekip sağlığı paneli: yöneticinin ekibi için izin, fazla mesai, performans,
 * takdir ve 1:1 sinyalleri — tek ekranda "kim tükeniyor olabilir?".
 * Bu bir TEŞHİS değil, konuşma başlatıcıdır; her bayrağın gerekçesi yazılır.
 * ==================================================================== */
[Route("api/team-health")]
[Authorize(Policy = "RequireManagerOrAbove")]
public class TeamHealthController : AppController
{
    private readonly EngagementDbContext _db;
    public TeamHealthController(EngagementDbContext db) => _db = db;

    /// <summary>
    /// Sonuç 60 sn önbellekte tutulur (Redis varsa). Anahtar kiracı + kullanıcı +
    /// departman filtresidir: her yönetici yalnızca kendi ekibinin önbelleğini görür.
    /// </summary>
    [HttpGet]
    public Task<IActionResult> Get([FromQuery] Guid? departmentId, CancellationToken ct)
        => HttpContext.RequestServices.GetRequiredService<AppCache>().JsonAsync(HttpContext, "team-health", Tenant,
            $"{Me.UserId}:{(Me.IsHr ? "hr" : "m")}:{departmentId}", TimeSpan.FromSeconds(60), c => BuildAsync(departmentId, c), ct);

    private async Task<object> BuildAsync(Guid? departmentId, CancellationToken ct)
    {
        var me = await MyPersonAsync(ct);
        List<Person> team;
        if (departmentId is not null && Me.IsHr)
            team = (await People.ListAsync(Tenant, ct)).Where(p => p.DepartmentId == departmentId).ToList();
        else
        {
            team = me is null ? new() : await People.TeamOfAsync(Tenant, me.Id, ct);
            if (team.Count == 0 && Me.IsHr) team = (await People.ListAsync(Tenant, ct)).Where(p => p.Id != me?.Id).ToList();
        }
        var ids = team.Select(p => p.Id).ToArray();
        if (ids.Length == 0) return new { members = Array.Empty<object>(), summary = new { size = 0 } };

        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
        var lastLeave = (await Db.QueryAsync(
            """
            SELECT "EmployeeId", max("EndDate"), sum(CASE WHEN "StartDate" >= $3 THEN "Days" ELSE 0 END)
            FROM leave_requests WHERE "TenantSlug" = $1 AND "EmployeeId" = ANY($2) AND "Status" = 'Approved' AND "StartDate" <= current_date
            GROUP BY 1
            """, r => (Id: r.GetGuid(0), Last: r.Date(1), Days90: r.Dec(2) ?? 0), ct, Tenant, ids, today.AddDays(-90)))
            .ToDictionary(x => x.Id);
        var overtime = (await Db.QueryAsync(
            """
            SELECT "EmployeeId", coalesce(sum("OvertimeMinutes"),0), coalesce(sum("WorkedMinutes"),0), count(*)
            FROM timeshift_time_entries WHERE "TenantSlug" = $1 AND "EmployeeId" = ANY($2) AND "Date" >= $3
            GROUP BY 1
            """, r => (Id: r.GetGuid(0), Ot: Convert.ToInt32(r.GetValue(1)), Worked: Convert.ToInt32(r.GetValue(2)), Days: Convert.ToInt32(r.GetValue(3))),
            ct, Tenant, ids, today.AddDays(-30))).ToDictionary(x => x.Id);
        var perf = (await Db.QueryAsync(
            """
            SELECT "EmployeeId", "Score", "CapturedAt" FROM performance_snapshots
            WHERE "TenantSlug" = $1 AND "EmployeeId" = ANY($2) ORDER BY "CapturedAt" DESC
            """, r => (Id: r.GetGuid(0), Score: r.Dec(1), At: r.GetFieldValue<DateTime>(2)), ct, Tenant, ids))
            .GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.Take(3).Select(x => x.Score ?? 0).ToList());
        var since90 = DateTime.UtcNow.AddDays(-90);
        var kudos = await _db.Kudos.AsNoTracking().Where(k => ids.Contains(k.ToEmployeeId) && k.CreatedAt >= since90)
            .GroupBy(k => k.ToEmployeeId).Select(g => new { g.Key, n = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.n, ct);
        var oneOnOnes = await _db.OneOnOnes.AsNoTracking().Where(o => ids.Contains(o.EmployeeId) && o.Status == "Done")
            .GroupBy(o => o.EmployeeId).Select(g => new { g.Key, last = g.Max(x => x.ScheduledAt), mood = g.OrderByDescending(x => x.ScheduledAt).Select(x => x.Mood).FirstOrDefault() })
            .ToDictionaryAsync(x => x.Key, ct);

        var members = team.Select(p =>
        {
            lastLeave.TryGetValue(p.Id, out var lv);
            overtime.TryGetValue(p.Id, out var ot);
            perf.TryGetValue(p.Id, out var scores);
            oneOnOnes.TryGetValue(p.Id, out var oo);
            var daysSinceLeave = lv.Last is { } l ? today.DayNumber - l.DayNumber : today.DayNumber - p.HireDate.DayNumber;
            var trend = scores is { Count: >= 2 } ? scores[0] - scores[^1] : 0;
            var flags = new List<string>();
            if (daysSinceLeave > 120) flags.Add($"{daysSinceLeave} gündür izin kullanmadı");
            if (ot.Ot > 20 * 60) flags.Add($"Son 30 günde {ot.Ot / 60} saat fazla mesai");
            if (trend <= -10) flags.Add($"Performans puanı {Math.Abs(trend):0} puan düştü");
            if (oo is null) flags.Add("Henüz 1:1 yapılmadı");
            else if ((DateTime.UtcNow - oo.last).TotalDays > 45) flags.Add($"{(int)(DateTime.UtcNow - oo.last).TotalDays} gündür 1:1 yapılmadı");
            if (oo?.mood is <= 2) flags.Add("Son 1:1'de ruh hâli düşük");
            var risk = flags.Count >= 3 ? "High" : flags.Count >= 1 ? "Medium" : "Low";
            return new
            {
                employeeId = p.Id, name = p.Name, position = p.Position, department = p.Department,
                daysSinceLeave, leaveDays90 = lv.Days90, overtimeHours30 = Math.Round(ot.Ot / 60.0, 1),
                workedDays30 = ot.Days, latestScore = scores?.FirstOrDefault(), scoreTrend = trend,
                kudos90 = kudos.GetValueOrDefault(p.Id), lastOneOnOne = oo?.last, lastMood = oo?.mood, flags, risk,
            };
        }).OrderByDescending(m => m.flags.Count).ToList();

        return new
        {
            members,
            summary = new
            {
                size = members.Count,
                atRisk = members.Count(m => m.risk == "High"),
                watch = members.Count(m => m.risk == "Medium"),
                avgOvertimeHours = members.Count == 0 ? 0 : Math.Round(members.Average(m => m.overtimeHours30), 1),
                avgDaysSinceLeave = members.Count == 0 ? 0 : (int)members.Average(m => m.daysSinceLeave),
                kudos90 = members.Sum(m => m.kudos90),
                oneOnOneCoverage = members.Count == 0 ? 0 : (int)Math.Round(100.0 * members.Count(m => m.lastOneOnOne != null && (DateTime.UtcNow - m.lastOneOnOne.Value).TotalDays <= 45) / members.Count),
            },
        };
    }
}

/* ======================================================================
 * Org senaryo planlama ("what-if"): taşıma/çıkış/yeni kadro hamleleri
 * taslak olarak saklanır; etki = departman başına kadro ve (İK için) aylık
 * maaş maliyeti farkı. Gerçek organizasyona DOKUNMAZ.
 * ==================================================================== */
[Route("api/org-scenarios")]
[Authorize(Policy = "RequireManagerOrAbove")]
public class OrgScenariosController : AppController
{
    private readonly EngagementDbContext _db;
    public OrgScenariosController(EngagementDbContext db) => _db = db;

    [HttpGet("baseline")]
    public async Task<IActionResult> Baseline(CancellationToken ct)
    {
        var people = await People.ListAsync(Tenant, ct);
        var depts = await Db.QueryAsync(
            "SELECT \"Id\", \"Name\", \"ParentDepartmentId\", \"HeadEmployeeId\" FROM organization_departments WHERE \"TenantSlug\" = $1 ORDER BY 2",
            r => new { id = r.GetGuid(0), name = r.GetString(1), parentId = r.GuidOrNull(2), headEmployeeId = r.GuidOrNull(3) }, ct, Tenant);
        return Ok(new
        {
            departments = depts,
            people = people.Select(p => new { employeeId = p.Id, name = p.Name, position = p.Position, departmentId = p.DepartmentId, department = p.Department }),
        });
    }

    /// <summary>
    /// Taslak erişimi: İK tüm senaryoları; yönetici yalnızca kendi oluşturduklarını görür/düzenler/siler.
    /// Sahibi bilinmeyen eski satırlar yalnızca İK'ya görünür.
    /// </summary>
    public static bool CanAccess(OrgScenario s, UserInfo me) =>
        me.IsHr || (!string.IsNullOrEmpty(s.CreatedByUserId) && s.CreatedByUserId == me.UserId);

    /// <summary>Maaş görünürlüğü yoksa hamlelerden ücret alanları çıkarılır (yanıt kopyası; kayıt değişmez).</summary>
    public static List<OrgMove> RedactMoves(IEnumerable<OrgMove> moves, bool costVisible) =>
        moves.Select(m => new OrgMove
        {
            EmployeeId = m.EmployeeId, Name = m.Name, FromDepartmentId = m.FromDepartmentId, ToDepartmentId = m.ToDepartmentId,
            NewPosition = m.NewPosition, Kind = m.Kind, PlannedSalary = costVisible ? m.PlannedSalary : null,
        }).ToList();

    private IQueryable<OrgScenario> Visible()
    {
        var q = _db.OrgScenarios.AsQueryable();
        if (Me.IsHr) return q;
        var uid = Me.UserId;
        return q.Where(s => s.CreatedByUserId != null && s.CreatedByUserId == uid);
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok(await Visible().AsNoTracking().OrderByDescending(s => s.UpdatedAt).ToListAsync(ct));

    public record ScenarioInput(string Name, string? Description, List<OrgMove>? Moves, string? Status);

    private static bool NegativeSalary(List<OrgMove>? moves) => moves?.Any(m => m.PlannedSalary < 0) == true;

    [HttpPost]
    public async Task<IActionResult> Create(ScenarioInput body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Name)) return BadRequest(new { message = "Senaryo adı zorunlu." });
        if (NegativeSalary(body.Moves)) return BadRequest(new { message = "Planlanan maaş negatif olamaz." });
        var s = new OrgScenario { Name = body.Name.Trim(), Description = body.Description, Moves = body.Moves ?? new(), CreatedByName = Me.Name, CreatedByUserId = Me.UserId };
        _db.OrgScenarios.Add(s);
        await _db.SaveChangesAsync(ct);
        return Ok(s);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, ScenarioInput body, CancellationToken ct)
    {
        var s = await Visible().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return NotFound();
        if (NegativeSalary(body.Moves)) return BadRequest(new { message = "Planlanan maaş negatif olamaz." });
        if (!string.IsNullOrWhiteSpace(body.Name)) s.Name = body.Name.Trim();
        s.Description = body.Description ?? s.Description;
        if (body.Moves is not null) { s.Moves = body.Moves; _db.Entry(s).Property(x => x.Moves).IsModified = true; }
        if (body.Status is "Draft" or "Shared" or "Archived") s.Status = body.Status;
        s.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(s);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var s = await Visible().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return NotFound();
        _db.OrgScenarios.Remove(s);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("{id:guid}/impact")]
    public async Task<IActionResult> Impact(Guid id, CancellationToken ct)
    {
        var s = await Visible().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return NotFound();
        var people = await People.ListAsync(Tenant, ct);
        var depts = (await Db.QueryAsync("SELECT \"Id\", \"Name\" FROM organization_departments WHERE \"TenantSlug\" = $1",
            r => (Id: r.GetGuid(0), Name: r.GetString(1)), ct, Tenant)).ToDictionary(x => x.Id, x => x.Name);
        var canSeePay = Me.IsHr || Me.Roles.Contains("ext-compensation-view");
        var salaries = canSeePay
            ? (await Db.QueryAsync(
                """
                SELECT DISTINCT ON ("EmployeeId") "EmployeeId", "BaseSalary" FROM compensation_records
                WHERE "TenantSlug" = $1 AND ("EffectiveTo" IS NULL OR "EffectiveTo" >= current_date)
                ORDER BY "EmployeeId", "EffectiveFrom" DESC
                """, r => (Id: r.GetGuid(0), Pay: r.GetDecimal(1)), ct, Tenant)).ToDictionary(x => x.Id, x => x.Pay)
            : new Dictionary<Guid, decimal>();

        var before = people.GroupBy(p => p.DepartmentId).ToDictionary(g => g.Key ?? Guid.Empty, g => g.Select(p => p.Id).ToList());
        var after = before.ToDictionary(kv => kv.Key, kv => new List<Guid>(kv.Value));
        decimal hireCost = 0;
        var hireCounts = new Dictionary<Guid, int>();
        foreach (var m in s.Moves)
        {
            switch (m.Kind)
            {
                case "Exit":
                    foreach (var list in after.Values) list.Remove(m.EmployeeId);
                    break;
                case "Hire":
                    var target = m.ToDepartmentId ?? Guid.Empty;
                    hireCounts[target] = hireCounts.GetValueOrDefault(target) + 1;
                    hireCost += m.PlannedSalary ?? 0;
                    break;
                default:
                    foreach (var list in after.Values) list.Remove(m.EmployeeId);
                    var to = m.ToDepartmentId ?? Guid.Empty;
                    if (!after.ContainsKey(to)) after[to] = new();
                    after[to].Add(m.EmployeeId);
                    break;
            }
        }
        var keys = before.Keys.Union(after.Keys).Union(hireCounts.Keys).Distinct();
        decimal Cost(IEnumerable<Guid> ids) => ids.Sum(i => salaries.GetValueOrDefault(i));
        var rows = keys.Select(k => new
        {
            departmentId = k == Guid.Empty ? (Guid?)null : k,
            department = k == Guid.Empty ? "Atanmamış" : depts.GetValueOrDefault(k, "?"),
            before = before.GetValueOrDefault(k)?.Count ?? 0,
            after = (after.GetValueOrDefault(k)?.Count ?? 0) + hireCounts.GetValueOrDefault(k),
            monthlyCostBefore = canSeePay ? Cost(before.GetValueOrDefault(k) ?? new()) : (decimal?)null,
            monthlyCostAfter = canSeePay ? Cost(after.GetValueOrDefault(k) ?? new()) + s.Moves.Where(m => m.Kind == "Hire" && (m.ToDepartmentId ?? Guid.Empty) == k).Sum(m => m.PlannedSalary ?? 0) : (decimal?)null,
        }).OrderBy(r => r.department).ToList();
        return Ok(new
        {
            scenario = new
            {
                s.Id, s.Name, s.Description, s.Status, s.CreatedByName, s.CreatedAt, s.UpdatedAt,
                moves = RedactMoves(s.Moves, canSeePay),
            },
            departments = rows,
            totals = new
            {
                headcountBefore = people.Count,
                headcountAfter = people.Count - s.Moves.Count(m => m.Kind == "Exit") + s.Moves.Count(m => m.Kind == "Hire"),
                monthlyCostDelta = canSeePay ? rows.Sum(r => (r.monthlyCostAfter ?? 0) - (r.monthlyCostBefore ?? 0)) : (decimal?)null,
                hireCost = canSeePay ? hireCost : (decimal?)null,
                movedPeople = s.Moves.Count(m => m.Kind == "Move"),
            },
            costVisible = canSeePay,
        });
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using LearningService.Data;
using LearningService.Models;
using LearningService.Services;

namespace LearningService.Controllers;

/// <summary>
/// Dalga 11 (madde 84) zorunlu eğitim ve İSG eğitimi takibi:
///  * me      — çalışanın son tarihli eğitimleri, süresi dolan/dolacak sertifikaları ve İSG eğitimleri;
///  * overdue — gecikmiş ve 30 gün içinde dolacak kalemler (İK herkes, yönetici başı olduğu departman);
///  * assign  — eğitimi kişilere ya da bir departmana son tarihle atar (yönetici yalnızca kendi departmanı).
/// Hatırlatmalar <see cref="DueReminderWorker"/> ve <see cref="CertificateReminderWorker"/>'dadır (30/7/0 gün).
/// İSG eğitimleri governance_osh_trainings'ten SALT OKUNUR alınır (kişi + konu başına en son eğitim).
/// </summary>
[ApiController]
[Route("api/learning-due")]
[Authorize]
public class LearningDueController : ControllerBase
{
    private readonly LearningDbContext _db;
    private readonly LearningDirectory _dir;

    public LearningDueController(LearningDbContext db, LearningDirectory dir) { _db = db; _dir = dir; }

    private bool IsHr => User.IsHr();
    private string Tenant => _db.CurrentTenantSlug ?? "";

    public sealed record DueItem(string Kind, Guid SourceId, Guid EmployeeId, string Title, DateOnly DueOn, int DaysLeft, string State, bool Mandatory, string? Status, Guid? CourseId = null);

    public sealed class OshRow
    {
        public Guid Id { get; set; }
        public Guid EmployeeId { get; set; }
        public string Topic { get; set; } = "";
        public DateOnly ExpiresOn { get; set; }
    }

    /// <summary>Kişi + konu başına en son İSG eğitimi (geçerlilik bitişi olanlar). Governance şeması yoksa boş.</summary>
    private async Task<List<OshRow>> OshAsync(IReadOnlyCollection<Guid>? employees, DateOnly until, CancellationToken ct)
    {
        try
        {
            var rows = await _db.Database.SqlQueryRaw<OshRow>("""
                SELECT DISTINCT ON (p.pid, lower(trim(o."Topic"))) o."Id", p.pid AS "EmployeeId", o."Topic", o."ExpiresOn"
                FROM governance_osh_trainings o CROSS JOIN LATERAL unnest(o."ParticipantIds") AS p(pid)
                WHERE o."TenantSlug" = {0} AND o."ExpiresOn" IS NOT NULL
                ORDER BY p.pid, lower(trim(o."Topic")), o."ExpiresOn" DESC
                """, Tenant).ToListAsync(ct);
            return rows.Where(r => r.ExpiresOn <= until && (employees is null || employees.Contains(r.EmployeeId))).ToList();
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703") { return new(); }
    }

    private async Task<List<DueItem>> CollectAsync(IReadOnlyCollection<Guid>? employees, int withinDays, CancellationToken ct)
    {
        var today = TrTime.Today;
        var until = today.AddDays(withinDays);
        var ids = employees?.ToList();
        var items = new List<DueItem>();

        var enr = _db.Enrollments.AsNoTracking().Include(e => e.Course)
            .Where(e => e.DueOn != null && e.DueOn <= until && e.Course!.IsActive
                && e.Status != EnrollmentStatus.Completed && e.Status != EnrollmentStatus.Dropped);
        if (ids is not null) enr = enr.Where(e => ids.Contains(e.EmployeeId));
        foreach (var e in await enr.Take(5000).ToListAsync(ct))
        {
            var d = e.DueOn!.Value.DayNumber - today.DayNumber;
            items.Add(new DueItem("Training", e.Id, e.EmployeeId, e.Course!.Title, e.DueOn.Value, d, DuePlan.State(d), e.Course.IsMandatory, e.Status.ToString(), e.CourseId));
        }

        // Sertifika: yalnızca kişinin o adla en son sertifikası (yenilenen eskisi uyarı üretmez).
        var certs = _db.Certifications.AsNoTracking().Where(c => c.ExpiresOn != null);
        if (ids is not null) certs = certs.Where(c => ids.Contains(c.EmployeeId));
        var latestCerts = (await certs.Take(20000).ToListAsync(ct))
            .GroupBy(c => (c.EmployeeId, Name: c.Name.Trim().ToLowerInvariant()))
            .Select(g => g.OrderByDescending(c => c.ExpiresOn).First())
            .Where(c => c.ExpiresOn <= until);
        foreach (var c in latestCerts)
        {
            var d = c.ExpiresOn!.Value.DayNumber - today.DayNumber;
            items.Add(new DueItem("Certificate", c.Id, c.EmployeeId, c.Name, c.ExpiresOn.Value, d, DuePlan.State(d), c.IsMandatory, null));
        }

        foreach (var o in await OshAsync(employees, until, ct))
        {
            var d = o.ExpiresOn.DayNumber - today.DayNumber;
            items.Add(new DueItem("Osh", o.Id, o.EmployeeId, o.Topic, o.ExpiresOn, d, DuePlan.State(d), true, null));
        }
        return items.OrderBy(i => i.DueOn).ToList();
    }

    [HttpGet("me")]
    public async Task<IActionResult> Mine(CancellationToken ct)
    {
        var me = await _dir.MeAsync(ct);
        if (me is null) return Ok(new { linked = false, items = Array.Empty<object>() });
        var items = await CollectAsync(new[] { me.Id }, 60, ct);
        return Ok(new { linked = true, items });
    }

    /// <summary>Gecikmiş + yaklaşan kalemler. Ad-soyad görünür: takip ve hatırlatma işin gereğidir (İK/yönetici).</summary>
    [HttpGet("overdue")]
    public async Task<IActionResult> Overdue([FromQuery] Guid? departmentId, [FromQuery] int withinDays = DuePlan.SoonDays, CancellationToken ct = default)
    {
        withinDays = Math.Clamp(withinDays, 0, 180);
        var me = await _dir.MeAsync(ct);
        var depts = await _dir.DepartmentsAsync(ct);
        var people = await _dir.ActiveAsync(ct);
        List<Guid> allowedDepts;
        if (IsHr) allowedDepts = departmentId is null ? depts.Select(d => d.Id).ToList() : new() { departmentId.Value };
        else
        {
            var headed = me is null ? new List<Guid>() : depts.Where(d => d.HeadEmployeeId == me.Id).Select(d => d.Id).ToList();
            if (headed.Count == 0) return StatusCode(403, new { message = "Ekip görünümü departman yöneticilerine ve İK'ya açık" });
            if (departmentId is not null && !headed.Contains(departmentId.Value))
                return StatusCode(403, new { message = "Yalnızca yönettiğiniz departmanı görebilirsiniz" });
            allowedDepts = departmentId is null ? headed : new() { departmentId.Value };
        }
        var scope = people.Where(p => (IsHr && departmentId is null) || (p.DepartmentId is { } d && allowedDepts.Contains(d)))
            .Where(p => IsHr || p.Id != me?.Id).ToDictionary(p => p.Id);
        var items = (await CollectAsync(scope.Keys.ToList(), withinDays, ct)).Where(i => i.Mandatory || i.Kind == "Training").ToList();
        await _dir.AuditAsync("LearningDue", string.Join(',', allowedDepts.Take(5)), "SensitiveViewed", new { field = "overdueTrainings", items = items.Count });
        return Ok(new
        {
            departments = depts.Where(d => IsHr || allowedDepts.Contains(d.Id)).Select(d => new { d.Id, d.Name }),
            summary = new
            {
                overdue = items.Count(i => i.State == DuePlan.Overdue),
                dueSoon = items.Count(i => i.State == DuePlan.DueSoon),
                byKind = items.GroupBy(i => i.Kind).Select(g => new { kind = g.Key, overdue = g.Count(i => i.State == DuePlan.Overdue), dueSoon = g.Count(i => i.State == DuePlan.DueSoon) }),
                byDepartment = items.GroupBy(i => scope[i.EmployeeId].DepartmentName ?? "—")
                    .Select(g => new { department = g.Key, overdue = g.Count(i => i.State == DuePlan.Overdue), dueSoon = g.Count(i => i.State == DuePlan.DueSoon) })
                    .OrderByDescending(x => x.overdue),
            },
            items = items.Select(i => new
            {
                i.Kind, i.SourceId, i.EmployeeId, employee = scope[i.EmployeeId].FullName, department = scope[i.EmployeeId].DepartmentName,
                i.Title, i.DueOn, i.DaysLeft, i.State, i.Mandatory, i.Status, i.CourseId,
            }),
        });
    }

    public record AssignInput(Guid CourseId, List<Guid>? EmployeeIds, Guid? DepartmentId, DateOnly? DueOn);

    /// <summary>
    /// Atama hedefleri (saf; birim testli). Departman modunda yönetici kendisi o departmandaysa listeden
    /// çıkarılır (kendine eğitim atayamaz; "departmandaki herkese" 403 vermemeli). İK için değişiklik yok.
    /// </summary>
    public static List<PersonRow> Targets(IEnumerable<PersonRow> people, Guid? departmentId, IReadOnlyCollection<Guid>? employeeIds, Guid? me, bool isHr) =>
        departmentId is { } dep
            ? people.Where(p => p.DepartmentId == dep && (isHr || p.Id != me)).ToList()
            : people.Where(p => employeeIds?.Contains(p.Id) == true).ToList();

    /// <summary>Yönetici yalnızca başı olduğu departmanların çalışanlarına (kendisi hariç) atayabilir (saf).</summary>
    public static bool ManagerMayAssign(IEnumerable<PersonRow> targets, IReadOnlySet<Guid> headed, Guid? me) =>
        me is { } m && targets.All(t => t.DepartmentId is { } d && headed.Contains(d) && t.Id != m);

    /// <summary>
    /// "Bir çalışana" seçicisi: çağıranın eğitim atayabileceği kişiler (İK: tüm aktif çalışanlar; yönetici:
    /// başı olduğu departmanlar, kendisi hariç). Yalnızca kimlik, ad ve departman adı döner.
    /// </summary>
    [HttpGet("assignable")]
    public async Task<IActionResult> Assignable(CancellationToken ct)
    {
        var me = await _dir.MeAsync(ct);
        var depts = await _dir.DepartmentsAsync(ct);
        var headed = me is null ? new HashSet<Guid>() : depts.Where(d => d.HeadEmployeeId == me.Id).Select(d => d.Id).ToHashSet();
        if (!IsHr && headed.Count == 0) return StatusCode(403, new { message = "Eğitim atamayı departman yöneticileri ve İK yapabilir" });
        var people = await _dir.ActiveAsync(ct);
        return Ok(people.Where(p => IsHr || ManagerMayAssign(new[] { p }, headed, me?.Id))
            .OrderBy(p => p.FullName, StringComparer.Ordinal)
            .Select(p => new { id = p.Id, fullName = p.FullName, department = p.DepartmentName }));
    }

    /// <summary>
    /// Eğitimi son tarihle atar. Var olan tamamlanmamış kaydın son tarihi güncellenir; tamamlanmış kayda dokunulmaz.
    /// Atanan çalışana uygulama içi bildirim gider.
    /// </summary>
    [HttpPost("assign")]
    public async Task<IActionResult> Assign([FromBody] AssignInput body, CancellationToken ct)
    {
        var me = await _dir.MeAsync(ct);
        var depts = await _dir.DepartmentsAsync(ct);
        var headed = me is null ? new HashSet<Guid>() : depts.Where(d => d.HeadEmployeeId == me.Id).Select(d => d.Id).ToHashSet();
        if (!IsHr && headed.Count == 0) return StatusCode(403, new { message = "Eğitim atamayı departman yöneticileri ve İK yapabilir" });
        var today = TrTime.Today;
        if (body.DueOn is { } due && (due < today || due > today.AddYears(2)))
            return BadRequest(new { message = "Son tarih bugünden önce ya da iki yıldan sonra olamaz" });
        var course = await _db.Courses.FirstOrDefaultAsync(c => c.Id == body.CourseId, ct);
        if (course is null) return NotFound(new { message = "Eğitim bulunamadı" });
        if (!course.IsActive) return BadRequest(new { message = "Pasif eğitime kayıt yapılamaz" });
        if ((body.EmployeeIds is not { Count: > 0 }) == (body.DepartmentId is null))
            return BadRequest(new { message = "Çalışan(lar) YA DA departman seçin" });
        if (body.EmployeeIds is { Count: > 1000 }) return BadRequest(new { message = "Bir seferde en fazla 1000 çalışan atanabilir" });

        var people = await _dir.ActiveAsync(ct);
        var targets = Targets(people, body.DepartmentId, body.EmployeeIds, me?.Id, IsHr);
        if (body.EmployeeIds is { Count: > 0 } && targets.Count != body.EmployeeIds.Distinct().Count())
            return BadRequest(new { message = "Çalışan bulunamadı" });
        if (!IsHr && !ManagerMayAssign(targets, headed, me?.Id))
            return StatusCode(403, new { message = "Yalnızca yönettiğiniz departmandaki çalışanlara eğitim atayabilirsiniz" });
        if (targets.Count == 0) return BadRequest(new { message = "Atanacak çalışan yok" });

        var ids = targets.Select(t => t.Id).ToList();
        var existing = await _db.Enrollments.Where(e => e.CourseId == course.Id && ids.Contains(e.EmployeeId)).ToDictionaryAsync(e => e.EmployeeId, ct);
        int created = 0, updated = 0, skipped = 0;
        var notify = new List<Guid>();
        foreach (var t in targets)
        {
            if (existing.TryGetValue(t.Id, out var e))
            {
                if (e.Status is EnrollmentStatus.Completed) { skipped++; continue; }
                if (e.Status is EnrollmentStatus.Dropped) e.Status = EnrollmentStatus.Enrolled;
                if (e.DueOn != body.DueOn) { e.DueOn = body.DueOn; updated++; notify.Add(t.Id); } else skipped++;
                continue;
            }
            _db.Enrollments.Add(new Enrollment { CourseId = course.Id, EmployeeId = t.Id, DueOn = body.DueOn });
            created++;
            notify.Add(t.Id);
        }
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { return Conflict(new { message = "Kayıtlar eşzamanlı değişti; lütfen yeniden deneyin" }); }

        var dueText = body.DueOn is { } dd ? $" Son tarih: {dd:dd.MM.yyyy}." : "";
        foreach (var id in notify)
            await LearningDirectory.NotifyAsync(_db, Tenant, id, course.IsMandatory ? "Zorunlu eğitim atandı" : "Eğitim atandı",
                $"«{course.Title}» eğitimi size atandı.{dueText}", "learning.assigned", $"/panel/egitim/{course.Id}", ct);
        return Ok(new { created, updated, skipped });
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OnboardingService.Data;
using OnboardingService.Models;
using OnboardingService.Services;

namespace OnboardingService.Controllers;

[ApiController]
[Route("api/onboarding-plans")]
[Authorize]
public class OnboardingPlansController : ControllerBase
{
    private readonly OnboardingDbContext _db;
    private readonly OnboardingService.Services.EmployeeDirectoryClient _employees;
    private readonly Sql _sql;
    private readonly OnboardingService.Tenancy.ITenantContext _tenant;
    public OnboardingPlansController(OnboardingDbContext db, OnboardingService.Services.EmployeeDirectoryClient employees,
        Sql sql, OnboardingService.Tenancy.ITenantContext tenant)
    {
        _db = db;
        _employees = employees;
        _sql = sql;
        _tenant = tenant;
    }

    private string Tenant => _tenant.TenantSlug ?? "";

    /// <summary>"RequireOnboardingManage" ile ayni rol kumesi.</summary>
    private bool CanManage => User.IsInRole("manager") || User.IsInRole("hr-admin")
        || User.IsInRole("tenant-admin") || User.IsInRole("platform-admin")
        || User.IsInRole("ext-onboarding-manage");

    /// <summary>
    /// GUVENLIK: Planlar onceden herkese acikti. Yonetenler disindakiler yalnizca
    /// kendisi hakkindaki ya da kendisine gorev atanmis planlari gorur.
    /// </summary>
    private async Task<IQueryable<OnboardingPlan>?> VisiblePlansAsync(IQueryable<OnboardingPlan> q, CancellationToken ct)
    {
        if (CanManage) return q;
        var me = await _employees.FindMyEmployeeIdAsync(ct);
        if (me is null) return null;
        return q.Where(p => p.EmployeeId == me.Value || p.BuddyEmployeeId == me.Value || p.Tasks.Any(t => t.AssigneeEmployeeId == me.Value));
    }

    /// <summary>Standart işe başlangıç görevleri - plan oluşturulurken otomatik eklenir (mevcut planlardaki görev adları değişmez).</summary>
    private static readonly (string Title, TaskCategory Category, int Offset)[] DefaultTasks =
    {
        ("Kullanıcı hesabı ve e-posta açılması", TaskCategory.IT, 0),
        ("Donanım zimmeti (dizüstü bilgisayar, telefon)", TaskCategory.IT, 0),
        ("Bina giriş kartının hazırlanması", TaskCategory.Facility, 0),
        ("Özlük evraklarının toplanması", TaskCategory.HR, 1),
        ("İş sözleşmesinin imzalanması", TaskCategory.Legal, 1),
        ("İşe uyum eğitimi", TaskCategory.Training, 3),
        ("Ekip tanıştırma toplantısı", TaskCategory.HR, 1),
        ("İş sağlığı ve güvenliği eğitimi", TaskCategory.Training, 7),
    };

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] Guid? employeeId, [FromQuery] PlanStatus? status, CancellationToken ct)
    {
        var q = await VisiblePlansAsync(_db.Plans.Include(p => p.Tasks).AsQueryable(), ct);
        if (q is null) return Forbid();
        if (employeeId.HasValue) q = q.Where(p => p.EmployeeId == employeeId.Value);
        if (status.HasValue) q = q.Where(p => p.Status == status.Value);
        return Ok(await q.OrderByDescending(p => p.CreatedAt).ToListAsync());
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var q = await VisiblePlansAsync(_db.Plans.Include(x => x.Tasks.OrderBy(t => t.Order)).AsQueryable(), ct);
        if (q is null) return Forbid();
        var p = await q.FirstOrDefaultAsync(x => x.Id == id, ct);
        return p is null ? NotFound() : Ok(p);
    }

    /// <summary>Yol arkadasi (buddy) icin varsayilan gorevler - sablonda Buddy gorevi yoksa eklenir.</summary>
    private static readonly (string Title, int Offset)[] DefaultBuddyTasks =
    {
        ("İlk gün karşılama ve ofis turu", 0),
        ("İlk gün öğle yemeğine birlikte çıkma", 0),
        ("Araçlar, kanallar ve kısa yollar hakkında bilgi verme", 1),
        ("İlk hafta sonu kısa sohbet: sorular ve ihtiyaçlar", 4),
        ("30. gün değerlendirme sohbeti", 30),
    };

    [HttpPost]
    [Authorize(Policy = "RequireOnboardingManage")]
    public async Task<IActionResult> Create([FromBody] CreatePlanRequest request, CancellationToken ct)
    {
        if (request.BuddyEmployeeId is { } b0 && b0 == request.EmployeeId)
            return BadRequest(new { message = "Yeni çalışan kendi yol arkadaşı olamaz" });
        if (request.Location is { Length: > 200 })
            return BadRequest(new { message = "Buluşma yeri en fazla 200 karakter olabilir" });
        // Tarih mantık doğrulaması: başlangıç, işe giriş tarihiyle aynı makul aralıkta olmalı.
        if (request.StartDate < new DateOnly(1950, 1, 1) || request.StartDate > DateOnly.FromDateTime(DateTime.UtcNow).AddYears(1))
            return BadRequest(new { message = "Başlangıç tarihi 01.01.1950 ile bugünden bir yıl sonrası arasında olmalı" });
        var person = await People.FindAsync(_sql, Tenant, request.EmployeeId, ct);
        if (person is null) return NotFound(new { message = "Çalışan bulunamadı" });
        if (request.BuddyEmployeeId is { } bid && await People.FindAsync(_sql, Tenant, bid, ct) is null)
            return NotFound(new { message = "Yol arkadaşı bulunamadı" });

        var plan = new OnboardingPlan
        {
            EmployeeId = request.EmployeeId,
            StartDate = request.StartDate,
            TemplateName = request.TemplateName ?? "Standart",
            Status = PlanStatus.InProgress,
            BuddyEmployeeId = request.BuddyEmployeeId,
            Location = string.IsNullOrWhiteSpace(request.Location) ? null : request.Location.Trim(),
        };

        var order = 1;
        if (request.UseDefaultTasks)
        {
            foreach (var (title, category, offset) in DefaultTasks)
            {
                plan.Tasks.Add(new OnboardingTask
                {
                    Title = title,
                    Category = category,
                    DueDate = request.StartDate.AddDays(offset),
                    Order = order++
                });
            }
        }

        // G14: unvan/departmana uyan rol sablonlari otomatik uygulanir.
        if (request.ApplyTemplates)
        {
            var managerId = person.DepartmentHeadId is { } h && h != person.Id ? h : (Guid?)null;
            var templates = await MatchingTemplatesAsync(person, ct);
            foreach (var t in templates)
                foreach (var item in t.Items.OrderBy(i => i.Order))
                {
                    plan.Tasks.Add(new OnboardingTask
                    {
                        Title = item.Title,
                        Category = item.Category,
                        OwnerRole = item.OwnerRole,
                        DueDate = request.StartDate.AddDays(item.OffsetDays),
                        AssigneeEmployeeId = AssigneeFor(item.OwnerRole, person.Id, managerId, request.BuddyEmployeeId),
                        Order = order++,
                    });
                }
            if (templates.Count > 0) plan.AppliedTemplates = string.Join(", ", templates.Select(t => t.Name));
        }
        if (request.BuddyEmployeeId is { } buddy && !plan.Tasks.Any(t => t.OwnerRole == OwnerRoles.Buddy))
            foreach (var (title, offset) in DefaultBuddyTasks)
                plan.Tasks.Add(new OnboardingTask
                {
                    Title = title, Category = TaskCategory.HR, OwnerRole = OwnerRoles.Buddy,
                    DueDate = request.StartDate.AddDays(offset), AssigneeEmployeeId = buddy, Order = order++,
                });

        _db.Plans.Add(plan);
        await _db.SaveChangesAsync(ct);
        if (plan.BuddyEmployeeId is { } bud) await NotifyBuddyAsync(plan, bud, person, ct);
        return CreatedAtAction(nameof(GetById), new { id = plan.Id }, plan);
    }

    private static Guid? AssigneeFor(string role, Guid employeeId, Guid? managerId, Guid? buddyId) => role switch
    {
        OwnerRoles.Employee => employeeId,
        OwnerRoles.Manager => managerId,
        OwnerRoles.Buddy => buddyId,
        _ => null, // HR / IT: ekip kuyrugu, kisiye atanmaz
    };

    /// <summary>Calisanin etkin unvan/departmanina uyan etkin sablonlar (bos alan = joker).</summary>
    private async Task<List<TaskTemplate>> MatchingTemplatesAsync(PersonInfo person, CancellationToken ct)
    {
        var all = await _db.TaskTemplates.AsNoTracking().Include(t => t.Items).Where(t => t.IsActive).ToListAsync(ct);
        var pos = person.Position?.Trim();
        return all.Where(t =>
                (string.IsNullOrWhiteSpace(t.PositionTitle) || (pos is not null && string.Equals(t.PositionTitle.Trim(), pos, StringComparison.CurrentCultureIgnoreCase)))
                && (t.DepartmentId is null || t.DepartmentId == person.DepartmentId))
            .OrderBy(t => t.PositionTitle is null && t.DepartmentId is null ? 0 : 1).ThenBy(t => t.Name)
            .ToList();
    }

    private async Task NotifyBuddyAsync(OnboardingPlan plan, Guid buddyId, PersonInfo newHire, CancellationToken ct) =>
        await Notify.InAppAsync(_sql, Tenant, buddyId, "Yol arkadaşı (buddy) görevi",
            $"{newHire.FirstName} {newHire.LastName} {plan.StartDate:dd.MM.yyyy} tarihinde aramıza katılıyor; uyum sürecinde yol arkadaşı olarak seçildiniz. Görev listeniz İşe alışma ekranında.",
            "onboarding.buddy", ct);

    public record BuddyRequest(Guid? BuddyEmployeeId);

    /// <summary>Yol arkadasi atar/degistirir; Buddy gorevleri yeni kisiye devredilir, yoksa varsayilan liste eklenir.</summary>
    [HttpPut("{id}/buddy")]
    [Authorize(Policy = "RequireOnboardingManage")]
    public async Task<IActionResult> SetBuddy(Guid id, [FromBody] BuddyRequest request, CancellationToken ct)
    {
        var plan = await _db.Plans.Include(p => p.Tasks).FirstOrDefaultAsync(p => p.Id == id, ct);
        if (plan is null) return NotFound();
        if (plan.Status is PlanStatus.Cancelled or PlanStatus.Completed) return BadRequest(new { message = "Kapanmış planda yol arkadaşı değiştirilemez" });
        if (request.BuddyEmployeeId == plan.EmployeeId) return BadRequest(new { message = "Yeni çalışan kendi yol arkadaşı olamaz" });
        var newHire = await People.FindAsync(_sql, Tenant, plan.EmployeeId, ct);
        if (request.BuddyEmployeeId is { } bid && await People.FindAsync(_sql, Tenant, bid, ct) is null)
            return NotFound(new { message = "Yol arkadaşı bulunamadı" });

        plan.BuddyEmployeeId = request.BuddyEmployeeId;
        var buddyTasks = plan.Tasks.Where(t => t.OwnerRole == OwnerRoles.Buddy).ToList();
        foreach (var t in buddyTasks.Where(t => t.Status != OnboardingTaskStatus.Done)) t.AssigneeEmployeeId = request.BuddyEmployeeId;
        if (request.BuddyEmployeeId is { } buddy && buddyTasks.Count == 0)
        {
            var order = plan.Tasks.Count == 0 ? 1 : plan.Tasks.Max(t => t.Order) + 1;
            foreach (var (title, offset) in DefaultBuddyTasks)
                _db.Tasks.Add(new OnboardingTask
                {
                    PlanId = plan.Id, Title = title, Category = TaskCategory.HR, OwnerRole = OwnerRoles.Buddy,
                    DueDate = plan.StartDate.AddDays(offset), AssigneeEmployeeId = buddy, Order = order++,
                });
        }
        await _db.SaveChangesAsync(ct);
        if (request.BuddyEmployeeId is { } b && newHire is not null) await NotifyBuddyAsync(plan, b, newHire, ct);
        var fresh = await _db.Plans.AsNoTracking().Include(p => p.Tasks.OrderBy(t => t.Order)).FirstAsync(p => p.Id == id, ct);
        return Ok(fresh);
    }

    public record LocationRequest(string? Location);

    [HttpPut("{id}/location")]
    [Authorize(Policy = "RequireOnboardingManage")]
    public async Task<IActionResult> SetLocation(Guid id, [FromBody] LocationRequest request, CancellationToken ct)
    {
        var plan = await _db.Plans.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (plan is null) return NotFound();
        if (request.Location is { Length: > 200 }) return BadRequest(new { message = "Buluşma yeri en fazla 200 karakter olabilir" });
        plan.Location = string.IsNullOrWhiteSpace(request.Location) ? null : request.Location.Trim();
        await _db.SaveChangesAsync(ct);
        return Ok(new { plan.Id, plan.Location });
    }

    /// <summary>Ilk gun karsilama iletisinin onizlemesi (gonderilecek metin).</summary>
    [HttpGet("{id}/welcome-preview")]
    [Authorize(Policy = "RequireOnboardingManage")]
    public async Task<IActionResult> WelcomePreview(Guid id, CancellationToken ct)
    {
        var plan = await _db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (plan is null) return NotFound();
        var settings = await _db.Settings.AsNoTracking().FirstOrDefaultAsync(ct);
        var (subject, body) = await RenderWelcomeAsync(_sql, Tenant, plan.EmployeeId, plan.StartDate, plan.BuddyEmployeeId, plan.Location, settings, ct);
        return Ok(new { subject, body, plan.WelcomeSentAt, scheduledFor = plan.StartDate });
    }

    /// <summary>Karsilama iletisini olusturur (calisan isteginde ve arka plan isinde ortak).</summary>
    public static async Task<(string Subject, string Body)> RenderWelcomeAsync(Sql sql, string tenant, Guid employeeId, DateOnly start,
        Guid? buddyId, string? location, OnboardingSettings? settings, CancellationToken ct)
    {
        var person = await People.FindAsync(sql, tenant, employeeId, ct);
        var ids = new List<Guid>();
        if (person?.DepartmentHeadId is { } h && h != employeeId) ids.Add(h);
        if (buddyId is { } b) ids.Add(b);
        var others = await People.FindManyAsync(sql, tenant, ids, ct);
        var manager = person?.DepartmentHeadId is { } hh && others.TryGetValue(hh, out var m) && hh != employeeId ? m.FullName : null;
        var buddy = buddyId is { } bb && others.TryGetValue(bb, out var bp) ? bp.FullName : null;
        var first = person?.FirstName ?? "";
        var subjectT = string.IsNullOrWhiteSpace(settings?.WelcomeSubject) ? WelcomeTemplate.DefaultSubject : settings!.WelcomeSubject!;
        var bodyT = string.IsNullOrWhiteSpace(settings?.WelcomeBody) ? WelcomeTemplate.DefaultBody : settings!.WelcomeBody!;
        return (WelcomeTemplate.Render(subjectT, first, start, manager, buddy, location),
                WelcomeTemplate.Render(bodyT, first, start, manager, buddy, location));
    }

    [HttpPost("{id}/tasks")]
    [Authorize(Policy = "RequireOnboardingManage")]
    public async Task<IActionResult> AddTask(Guid id, [FromBody] AddTaskRequest request)
    {
        var plan = await _db.Plans.Include(p => p.Tasks).FirstOrDefaultAsync(p => p.Id == id);
        if (plan is null) return NotFound();
        // Son tarih plan başlangıcına göre şablonlarla aynı aralıkta olmalı (-60 / +365 gün);
        // 1999 gibi değerler kabul ediliyordu.
        if (request.DueDate is { } due && (due < plan.StartDate.AddDays(-60) || due > plan.StartDate.AddDays(365)))
            return BadRequest(new { message = "Son tarih, plan başlangıcından en fazla 60 gün önce ve 365 gün sonra olabilir" });

        var task = new OnboardingTask
        {
            PlanId = id,
            Title = request.Title,
            Category = request.Category,
            DueDate = request.DueDate,
            AssigneeEmployeeId = request.AssigneeEmployeeId,
            OwnerRole = request.OwnerRole is { } r && OwnerRoles.All.Contains(r) ? r : null,
            Order = plan.Tasks.Count + 1
        };
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id }, task);
    }

    [HttpPost("{id}/tasks/{taskId}/status")]
    public async Task<IActionResult> UpdateTaskStatus(
        Guid id, Guid taskId, [FromBody] UpdateTaskStatusRequest request, CancellationToken ct)
    {
        var (error, task, _) = await SetTaskStatusCoreAsync(_db, id, taskId, request.Status, CanManage,
            _employees.FindMyEmployeeIdAsync, failIfAlreadyDone: false, ct);
        return error ?? Ok(task);
    }

    /// <summary>
    /// Görev durumunu değiştirmenin ortak çekirdeği (web ucu ve sohbet botunun iç ucu
    /// InternalChatController). <paramref name="planId"/> null ise plan görevden bulunur.
    /// <paramref name="me"/> yalnızca yönetici olmayanlar için çağrılır. Tüm görevler bitince
    /// plan kapanır (PlanCompleted = true).
    /// </summary>
    [NonAction]
    public static async Task<(IActionResult? Error, OnboardingTask? Task, bool PlanCompleted)> SetTaskStatusCoreAsync(
        OnboardingDbContext db, Guid? planId, Guid taskId, OnboardingTaskStatus status, bool canManage,
        Func<CancellationToken, Task<Guid?>> me, bool failIfAlreadyDone, CancellationToken ct)
    {
        var task = planId is { } pid
            ? await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId && t.PlanId == pid, ct)
            : await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null) return (new NotFoundResult(), null, false);
        var id = task.PlanId;
        var planState = await db.Plans.Where(p => p.Id == id)
            .Select(p => new { p.EmployeeId, p.Status }).FirstAsync(ct);

        // GUVENLIK: Onceden yalnizca [Authorize] - her calisan herhangi bir gorevi
        // (orn. Hukuk: sozlesme imzalama) "tamamlandi" yapip plani otomatik
        // kapatabiliyordu. Yetkili: yonetenler, gorevin atandigi kisi ve - hukuki
        // gorevler haric - planin sahibi (yeni calisanin kendi adimlari).
        if (!canManage)
        {
            var actor = await me(ct);
            var isAssignee = actor is not null && task.AssigneeEmployeeId == actor.Value;
            var isOwner = actor is not null && planState.EmployeeId == actor.Value && task.Category != TaskCategory.Legal;
            if (!isAssignee && !isOwner) return (new NotFoundResult(), null, false);
        }
        if (planState.Status == PlanStatus.Cancelled)
            return (new BadRequestObjectResult("İptal edilmiş plandaki görev değiştirilemez"), null, false);
        if (failIfAlreadyDone && task.Status == OnboardingTaskStatus.Done)
            return (new ConflictObjectResult(new { message = "Bu görev zaten tamamlanmış" }), null, false);

        task.Status = status;
        task.CompletedAt = status == OnboardingTaskStatus.Done ? DateTimeOffset.UtcNow : null;
        await db.SaveChangesAsync(ct);

        // Tum gorevler bitince plani kapat.
        var plan = await db.Plans.Include(p => p.Tasks).FirstAsync(p => p.Id == id, ct);
        var completed = false;
        if (plan.Tasks.All(t => t.Status == OnboardingTaskStatus.Done))
        {
            plan.Status = PlanStatus.Completed;
            plan.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            completed = true;
        }

        return (null, task, completed);
    }
}

public record CreatePlanRequest(
    Guid EmployeeId, DateOnly StartDate, string? TemplateName, bool UseDefaultTasks = true,
    bool ApplyTemplates = true, Guid? BuddyEmployeeId = null, string? Location = null);
public record AddTaskRequest(
    string Title, TaskCategory Category, DateOnly? DueDate, Guid? AssigneeEmployeeId, string? OwnerRole = null);
public record UpdateTaskStatusRequest(OnboardingTaskStatus Status);

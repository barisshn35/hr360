using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LeaveService.Data;
using LeaveService.Models;
using LeaveService.Services;
using LeaveService.Messaging;
using LeaveService.Infrastructure;
using LeaveService.Auditing;
using System.Text.Json;

namespace LeaveService.Controllers;

[ApiController]
[Route("api/leave-requests")]
[Authorize]
public class LeaveRequestsController : ControllerBase
{
    private readonly LeaveDbContext _db;
    private readonly ApprovalWorkflowClient _approvals;
    public LeaveRequestsController(LeaveDbContext db, ApprovalWorkflowClient approvals)
    {
        _db = db;
        _approvals = approvals;
    }

    private bool IsHr => User.IsInRole("hr-admin") || User.IsInRole("tenant-admin")
        || User.IsInRole("platform-admin");
    private bool IsManagerOrAbove => IsHr || User.IsInRole("manager");

    /// <summary>
    /// Yöneticinin görebildiği çalışanlar: başı olduğu departmanlarda bugün geçerli ataması olan
    /// aktif çalışanlar + kendisi. Organizasyon/çalışan tabloları aynı veritabanında salt okunur
    /// sorgulanır (ApprovalWorkflowClient'taki departman başı çözümlemesiyle aynı desen).
    /// İK için null (kısıt yok).
    /// </summary>
    private async Task<HashSet<Guid>?> VisibleEmployeesAsync(CancellationToken ct)
    {
        if (IsHr) return null;
        var me = await _approvals.FindMyEmployeeIdAsync(ct);
        if (me is null) return new HashSet<Guid>();
        var team = await _db.Database.SqlQueryRaw<Guid>(
            """
            SELECT DISTINCT a."EmployeeId" AS "Value" FROM employee_assignments a
            JOIN organization_departments d ON d."Id" = a."DepartmentId"
            JOIN employee_employees e ON e."Id" = a."EmployeeId"
            WHERE a."TenantSlug" = {0} AND d."HeadEmployeeId" = {1} AND e."Status" <> 'Terminated'
              AND a."EffectiveFrom" <= current_date AND (a."EffectiveTo" IS NULL OR a."EffectiveTo" >= current_date)
            """, _db.CurrentTenantSlug ?? "", me.Value).ToListAsync(ct);
        return team.Append(me.Value).ToHashSet();
    }

    /// <summary>
    /// Iki tarih arasindaki is gunu sayisi: hafta sonlari ve sirketin resmi tatil
    /// takvimindeki gunler (PublicHolidays) dislanir; yarim gun tatil (arife, 28 Ekim) 0,5 sayilir.
    /// Onceden gun sayisi istemciden geliyordu: 10 gunluk izin "days: 0.5" ile gonderilip
    /// bakiyeden yarim gun dusuluyordu (canli dogrulandi).
    /// </summary>
    private async Task<decimal> WorkingDaysAsync(DateOnly start, DateOnly end, CancellationToken ct)
    {
        var holidays = (await _db.PublicHolidays
            .Where(h => h.Date >= start && h.Date <= end)
            .Select(h => new { h.Date, h.IsHalfDay }).ToListAsync(ct))
            .GroupBy(h => h.Date).ToDictionary(g => g.Key, g => g.All(x => x.IsHalfDay));
        return Services.LeaveEntitlement.WorkingDays(start, end, holidays);
    }

    private async Task<LeaveSettings> SettingsAsync(CancellationToken ct) =>
        await _db.LeaveSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new LeaveSettings();

    private sealed class TeamRow
    {
        public Guid Id { get; set; }
        public string FirstName { get; set; } = "";
        public string LastName { get; set; } = "";
    }

    /// <summary>Çalışanın bugünkü departmanındaki diğer aktif çalışanlar (salt okunur, kiracı filtresiyle).</summary>
    private Task<List<TeamRow>> TeammatesAsync(Guid employeeId, CancellationToken ct) =>
        _db.Database.SqlQueryRaw<TeamRow>(
            """
            SELECT DISTINCT e."Id", e."FirstName", e."LastName" FROM employee_assignments a
            JOIN employee_employees e ON e."Id" = a."EmployeeId"
            WHERE a."TenantSlug" = {0} AND e."Status" <> 'Terminated' AND e."Id" <> {1}
              AND a."EffectiveFrom" <= current_date AND (a."EffectiveTo" IS NULL OR a."EffectiveTo" >= current_date)
              AND a."DepartmentId" IN (SELECT x."DepartmentId" FROM employee_assignments x
                    WHERE x."TenantSlug" = {0} AND x."EmployeeId" = {1}
                      AND x."EffectiveFrom" <= current_date AND (x."EffectiveTo" IS NULL OR x."EffectiveTo" >= current_date))
            """, _db.CurrentTenantSlug ?? "", employeeId).ToListAsync(ct);

    /// <summary>
    /// Madde 69: ekipte aynı günlerde izinli ya da izin bekleyen kişiler. <paramref name="excludeLeaveId"/>
    /// talebin kendisini dışlar (onay ekranı). Dönüş: ekip, çakışanlar (kişi başına en erken tarih).
    /// </summary>
    private async Task<(List<TeamRow> Team, List<(Guid EmployeeId, DateOnly Start, DateOnly End)> Overlaps)> TeamOverlapAsync(
        Guid employeeId, DateOnly start, DateOnly end, Guid? excludeLeaveId, CancellationToken ct)
    {
        var team = await TeammatesAsync(employeeId, ct);
        var ids = team.Select(t => t.Id).ToList();
        var rows = await _db.LeaveRequests.AsNoTracking()
            .Where(r => ids.Contains(r.EmployeeId) && r.Id != excludeLeaveId
                && (r.Status == LeaveRequestStatus.Submitted || r.Status == LeaveRequestStatus.Approved)
                && r.StartDate <= end && r.EndDate >= start)
            .Select(r => new { r.EmployeeId, r.StartDate, r.EndDate }).ToListAsync(ct);
        return (team, rows.Select(r => (r.EmployeeId, r.StartDate, r.EndDate)).ToList());
    }

    /// <summary>
    /// Madde 69: izin formunda ve onayda ekip çakışma uyarısı. Talep eden yalnızca sayıyı görür (ekip 5'ten
    /// küçükse yalnızca "eşik aşıldı"); yönetici (ekibini görebilen) ve İK ayrıca adları ve tarihleri görür.
    /// İzin türü gösterilmez (hastalık izni gibi özel bilgi sızmasın). <c>leaveRequestId</c> verilirse
    /// talebin çalışanı ve tarihleri kullanılır (onay ekranı).
    /// </summary>
    [HttpGet("team-conflict")]
    public async Task<IActionResult> TeamConflictCheck([FromQuery] Guid? employeeId, [FromQuery] DateOnly? startDate, [FromQuery] DateOnly? endDate,
        [FromQuery] Guid? leaveRequestId, CancellationToken ct)
    {
        Guid emp;
        DateOnly start, end;
        if (leaveRequestId is { } lid)
        {
            var r = await _db.LeaveRequests.AsNoTracking().FirstOrDefaultAsync(x => x.Id == lid, ct);
            if (r is null) return NotFound();
            (emp, start, end) = (r.EmployeeId, r.StartDate, r.EndDate);
        }
        else if (startDate is { } s0 && endDate is { } e0 && e0 >= s0 && e0.DayNumber - s0.DayNumber <= 366)
        {
            var me0 = await _approvals.FindMyEmployeeIdAsync(ct);
            emp = employeeId ?? me0 ?? Guid.Empty;
            if (emp == Guid.Empty) return Forbid();
            (start, end) = (s0, e0);
        }
        else return BadRequest(new { message = "Tarih aralığı geçersiz" });

        var me = await _approvals.FindMyEmployeeIdAsync(ct);
        var self = me == emp;
        var canSeeNames = IsHr || (User.IsInRole("manager") && await VisibleEmployeesAsync(ct) is { } vis && vis.Contains(emp) && !self);
        // Başkasının çakışmasına bakmak yalnızca İK'ya ya da o kişiyi görebilen yöneticiye açık.
        if (!self && !canSeeNames) return NotFound();

        var settings = await SettingsAsync(ct);
        var (team, overlaps) = await TeamOverlapAsync(emp, start, end, leaveRequestId, ct);
        var people = overlaps.Select(o => o.EmployeeId).Distinct().ToList();
        var res = Services.TeamConflict.Evaluate(team.Count, people.Count, settings.ConflictThresholdPercent, settings.ConflictWarnEnabled, canSeeNames);
        return Ok(new
        {
            enabled = res.Enabled, thresholdPercent = res.ThresholdPercent, exceeds = res.Exceeds,
            teamSize = res.TeamSize, overlapping = res.Overlapping, percent = res.Percent,
            people = canSeeNames && res.Enabled
                ? people.Select(id => new
                {
                    employeeId = id,
                    name = team.Where(t => t.Id == id).Select(t => $"{t.FirstName} {t.LastName}".Trim()).FirstOrDefault(),
                    startDate = overlaps.Where(o => o.EmployeeId == id).Min(o => o.Start),
                    endDate = overlaps.Where(o => o.EmployeeId == id).Max(o => o.End),
                }).ToList()
                : null,
        });
    }

    /// <summary>Bir departmana bugün doğrudan atanmış aktif çalışanlar.</summary>
    private Task<List<TeamRow>> DepartmentMembersAsync(Guid departmentId, CancellationToken ct) =>
        _db.Database.SqlQueryRaw<TeamRow>(
            """
            SELECT DISTINCT e."Id", e."FirstName", e."LastName" FROM employee_assignments a
            JOIN employee_employees e ON e."Id" = a."EmployeeId"
            WHERE a."TenantSlug" = {0} AND a."DepartmentId" = {1} AND e."Status" <> 'Terminated'
              AND a."EffectiveFrom" <= current_date AND (a."EffectiveTo" IS NULL OR a."EffectiveTo" >= current_date)
            """, _db.CurrentTenantSlug ?? "", departmentId).ToListAsync(ct);

    /// <summary>
    /// Ekip izin takvimi (yenilik haritası #1). Kapsam: kişinin departmanı; İK ve departman başı
    /// <paramref name="departmentId"/> ile bir departman seçebilir. Ayrıntı (ad, tür, bekleyen talepler)
    /// yalnızca kişinin kendisine, İK'ya ve yöneticinin ekibine; diğer ekip arkadaşları için izin
    /// ayarındaki <c>TeamCalendarNames</c> açıksa onaylı izin adıyla ama türsüz, kapalıysa (varsayılan)
    /// yalnızca gün başına sayı döner. Ekip 5 kişiden küçükse sayı yerine yalnızca "izinli var" bilgisi
    /// verilir (çakışma uyarısıyla aynı kural).
    /// </summary>
    [HttpGet("team-calendar")]
    public async Task<IActionResult> TeamCalendar([FromQuery] DateOnly from, [FromQuery] DateOnly to, [FromQuery] Guid? departmentId, CancellationToken ct)
    {
        if (to < from || to.DayNumber - from.DayNumber > 92) return BadRequest(new { message = "Tarih aralığı en fazla 3 ay olabilir" });
        var me = await _approvals.FindMyEmployeeIdAsync(ct);
        var visible = await VisibleEmployeesAsync(ct); // İK: null (hepsi)
        List<TeamRow> people;
        if (departmentId is { } dep)
        {
            var heads = await _db.Database.SqlQueryRaw<Guid>(
                "SELECT \"Id\" AS \"Value\" FROM organization_departments WHERE \"TenantSlug\" = {0} AND \"Id\" = {1} AND \"HeadEmployeeId\" = {2}",
                _db.CurrentTenantSlug ?? "", dep, me ?? Guid.Empty).ToListAsync(ct);
            if (!IsHr && heads.Count == 0) return Forbid();
            people = await DepartmentMembersAsync(dep, ct);
        }
        else
        {
            // Çalışan kaydı olmayan hesap (ör. yalnızca yönetici hesabı): ekibi yok, boş takvim döner.
            if (me is null)
                return Ok(new
                {
                    teamSize = 0, people = Array.Empty<object>(), entries = Array.Empty<object>(), others = Array.Empty<object>(),
                    holidays = Array.Empty<object>()
                });
            people = await TeammatesAsync(me.Value, ct);
            var self = await _db.Database.SqlQueryRaw<TeamRow>(
                "SELECT \"Id\", \"FirstName\", \"LastName\" FROM employee_employees WHERE \"TenantSlug\" = {0} AND \"Id\" = {1}",
                _db.CurrentTenantSlug ?? "", me.Value).ToListAsync(ct);
            people.InsertRange(0, self);
        }
        bool Detail(Guid id) => id == me || visible is null || visible.Contains(id);
        var settings = await SettingsAsync(ct);
        var ids = people.Select(p => p.Id).ToList();
        var leaves = await _db.LeaveRequests.AsNoTracking()
            .Where(r => ids.Contains(r.EmployeeId) && r.StartDate <= to && r.EndDate >= from
                && (r.Status == LeaveRequestStatus.Approved || r.Status == LeaveRequestStatus.Submitted))
            .Select(r => new { r.EmployeeId, r.StartDate, r.EndDate, r.Status, r.Type, r.Days, r.Hours })
            .ToListAsync(ct);

        var shown = new List<object>();
        var hiddenDays = new Dictionary<DateOnly, HashSet<Guid>>();
        foreach (var l in leaves)
        {
            if (Detail(l.EmployeeId))
                shown.Add(new { employeeId = l.EmployeeId, startDate = l.StartDate, endDate = l.EndDate, status = l.Status.ToString(), type = l.Type.ToString(), l.Days, l.Hours });
            else if (l.Status == LeaveRequestStatus.Approved && settings.TeamCalendarNames)
                shown.Add(new { employeeId = l.EmployeeId, startDate = l.StartDate, endDate = l.EndDate, status = "Approved", type = (string?)null, l.Days, Hours = (decimal?)null });
            else if (l.Status == LeaveRequestStatus.Approved)
                for (var d = l.StartDate < from ? from : l.StartDate; d <= l.EndDate && d <= to; d = d.AddDays(1))
                    (hiddenDays.TryGetValue(d, out var set) ? set : hiddenDays[d] = new()).Add(l.EmployeeId);
        }
        var named = people.Where(p => Detail(p.Id) || settings.TeamCalendarNames).ToList();
        var exact = people.Count >= 5;
        var holidays = await _db.PublicHolidays.AsNoTracking().Where(h => h.Date >= from && h.Date <= to)
            .OrderBy(h => h.Date).Select(h => new { h.Date, h.Name, h.IsHalfDay }).ToListAsync(ct);
        return Ok(new
        {
            teamSize = people.Count,
            people = named.Select(p => new { id = p.Id, name = $"{p.FirstName} {p.LastName}".Trim(), self = p.Id == me, detail = Detail(p.Id) }),
            entries = shown,
            others = hiddenDays.OrderBy(kv => kv.Key).Select(kv => new { date = kv.Key, count = exact ? kv.Value.Count : (int?)null }),
            holidays,
        });
    }

    /// <summary>
    /// Izin taleplerini listeler.
    ///
    /// GUVENLIK: Onceden employeeId filtresi serbestti; bir calisan baskasinin
    /// taleplerini, filtresiz cagirarak da tum sirketinkileri gorebiliyordu. Simdi:
    ///   - Yonetici ve ustu: istedigi employeeId'yi (ya da hicbirini) sorgulayabilir.
    ///   - Calisan: yalnizca kendi taleplerini gorur; employeeId vermezse kendisine
    ///     sabitlenir, baska birininkini verirse 403 doner.
    /// </summary>
    /// <remarks>
    /// Sayfalama (G24): <c>page</c> verilmezse eski biçim (düz dizi, en yeni önce, en fazla 2000;
    /// toplam <c>X-Total-Count</c> başlığında). <c>page</c>/<c>pageSize</c> ile
    /// <c>{ items, total, page, pageSize }</c>. Ek filtreler: <c>type</c>, <c>from</c>/<c>to</c>
    /// (tarih aralığıyla çakışan), <c>q</c> (gerekçe metni; <c>qTypes</c> verilirse bu türlerdeki
    /// talepler de eşleşir - tür adları istemcide çevrildiği için istemci eşleyip gönderir).
    /// Sıralama: <c>sort</c>=createdAt|startDate|days|status|type, <c>dir</c>=asc|desc (varsayılan createdAt desc).
    /// Yetki kuralı sayfalamadan önce uygulanır: çalışan yalnızca kendi taleplerini görür.
    /// </remarks>
    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] Guid? employeeId, [FromQuery] LeaveRequestStatus? status, CancellationToken ct,
        [FromQuery] int? page = null, [FromQuery] int? pageSize = null, [FromQuery] LeaveType? type = null,
        [FromQuery] DateOnly? from = null, [FromQuery] DateOnly? to = null, [FromQuery] string? q = null,
        [FromQuery] string? qTypes = null, [FromQuery] string? sort = null, [FromQuery] string? dir = null)
    {
        var isManager = User.IsInRole("manager") || User.IsInRole("hr-admin")
            || User.IsInRole("tenant-admin") || User.IsInRole("platform-admin");

        if (!isManager)
        {
            var myEmployeeId = await _approvals.FindMyEmployeeIdAsync(ct);
            if (myEmployeeId is null) return Forbid();
            if (employeeId.HasValue && employeeId.Value != myEmployeeId.Value) return Forbid();
            employeeId = myEmployeeId;
        }

        var query = _db.LeaveRequests.AsNoTracking().AsQueryable();
        // Yönetici kapsamı: İK tümünü görür; yönetici yalnızca ekibini (başı olduğu
        // departmanlar) ve kendisini. Önceden yöneticiye kiracı genelini döndürüyordu.
        if (isManager && await VisibleEmployeesAsync(ct) is { } visible)
        {
            if (employeeId.HasValue && !visible.Contains(employeeId.Value)) return Forbid();
            var ids = visible.ToList();
            query = query.Where(r => ids.Contains(r.EmployeeId));
        }
        if (employeeId.HasValue) query = query.Where(r => r.EmployeeId == employeeId.Value);
        if (status.HasValue) query = query.Where(r => r.Status == status.Value);
        if (type.HasValue) query = query.Where(r => r.Type == type.Value);
        if (from.HasValue) query = query.Where(r => r.EndDate >= from.Value);
        if (to.HasValue) query = query.Where(r => r.StartDate <= to.Value);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var like = Paging.FoldLike(q);
            var types = (qTypes ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => Enum.TryParse<LeaveType>(t, true, out var lt) ? (LeaveType?)lt : null)
                .Where(t => t.HasValue).Select(t => t!.Value).Distinct().ToList();
            query = query.Where(r => EF.Functions.Like(LeaveDbContext.Fold(r.Reason ?? ""), like, "\\") || types.Contains(r.Type));
        }

        await AuditPlatformListAsync(query, ct);

        var desc = sort is null || Paging.Desc(dir);
        IOrderedQueryable<LeaveRequest> ordered = (sort ?? "createdAt").ToLowerInvariant() switch
        {
            "startdate" => desc ? query.OrderByDescending(r => r.StartDate) : query.OrderBy(r => r.StartDate),
            "days" => desc ? query.OrderByDescending(r => r.Days) : query.OrderBy(r => r.Days),
            "status" => desc ? query.OrderByDescending(r => r.Status) : query.OrderBy(r => r.Status),
            "type" => desc ? query.OrderByDescending(r => r.Type) : query.OrderBy(r => r.Type),
            _ => desc ? query.OrderByDescending(r => r.CreatedAt) : query.OrderBy(r => r.CreatedAt),
        };
        return await Paging.ListAsync(this, ordered.ThenBy(r => r.Id), page, pageSize, ct);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var r = await _db.LeaveRequests.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return NotFound();
        // GUVENLIK: Onceden kimligi bilinen her izin talebi (gerekce metni dahil)
        // herkese aciktu. Liste ucundaki kuralla ayni: yonetici+ ya da sahibi.
        if (!IsManagerOrAbove)
        {
            var me = await _approvals.FindMyEmployeeIdAsync(ct);
            if (me is null || me.Value != r.EmployeeId) return NotFound();
        }
        else if (await VisibleEmployeesAsync(ct) is { } visible && !visible.Contains(r.EmployeeId)) return NotFound();
        if (PlatformAccessAudit.ShouldLog(_db.CurrentIsPlatformAdmin, _db.CurrentTenantSlug, r.TenantSlug))
            await PlatformAccessAudit.WriteAsync(_db, HttpContext, "LeaveRequest", r.Id.ToString(),
                new[] { (r.TenantSlug, (object)new { employeeId = r.EmployeeId }) }, ct);
        return Ok(r);
    }

    /// <summary>
    /// Platform yöneticisinin (başka kiracının ya da kiracısız) liste okuması: okunan kayıtların
    /// kiracısına, kiracı başına tek "PlatformAccess" satırı ve yalnızca kayıt sayısı.
    /// </summary>
    private async Task AuditPlatformListAsync(IQueryable<LeaveRequest> query, CancellationToken ct)
    {
        if (!_db.CurrentIsPlatformAdmin) return;
        var counts = await query.GroupBy(r => r.TenantSlug).Select(g => new { Tenant = g.Key, Count = g.Count() }).ToListAsync(ct);
        var rows = counts.Where(c => PlatformAccessAudit.ShouldLog(true, _db.CurrentTenantSlug, c.Tenant))
            .Select(c => (c.Tenant, (object)new { count = c.Count })).ToList();
        if (rows.Count > 0) await PlatformAccessAudit.WriteAsync(_db, HttpContext, "LeaveRequestList", "list", rows, ct);
    }

    /// <summary>Izin talebi olusturur, bakiyeden 'beklemede' olarak duser ve
    /// calisanin departman basina onay icin bir workflow acar.</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateLeaveRequest request, CancellationToken ct)
    {
        // GUVENLIK: EmployeeId istek govdesinden geliyordu - herkes baskasi adina izin
        // talebi acabiliyor, o kisinin bakiyesini "beklemede" olarak kilitleyebiliyordu
        // (canli dogrulandi). IK disindakiler yalnizca kendi adina talep acar.
        if (!IsHr)
        {
            var me = await _approvals.FindMyEmployeeIdAsync(ct);
            if (me is null) return Forbid();
            if (request.EmployeeId != me.Value)
                return StatusCode(StatusCodes.Status403Forbidden,
                    new { message = "Yalnızca kendi adınıza izin talebi oluşturabilirsiniz" });
        }
        // IK baskasi adina talep giriyorsa iletiler "bakiyeniz" yerine calisana gore yazilir.
        var onBehalf = IsHr && await _approvals.FindMyEmployeeIdAsync(ct) != request.EmployeeId;
        var (error, leave) = await CreateCoreAsync(request, internalCall: false, ct, onBehalf);
        return error ?? CreatedAtAction(nameof(GetById), new { id = leave!.Id }, leave);
    }

    /// <summary>
    /// Sohbet botundan (Slack/Teams) açılan izin talebi. Web ucuyla aynı kurallar uygulanır;
    /// çalışan, botun doğrulanmış sohbet hesabından gelir. Onay akışı da servisler arası
    /// açılır. Gateway /api/*/internal/ yollarını dışarıya kapatır; anahtar yoksa uç kapalıdır.
    /// </summary>
    [HttpPost("/api/internal/leave-requests")]
    [AllowAnonymous]
    public async Task<IActionResult> CreateInternal([FromBody] InternalCreateLeaveRequest request,
        [FromServices] Tenancy.TenantContext tenant, CancellationToken ct)
    {
        if (!InternalTokenValid())
            return NotFound();
        if (string.IsNullOrWhiteSpace(request.TenantSlug)) return BadRequest(new { message = "Kiracı belirtilmedi" });
        tenant.TenantSlug = request.TenantSlug;
        tenant.IsPlatformAdmin = false;
        var (error, leave) = await CreateCoreAsync(new CreateLeaveRequest(request.EmployeeId, request.Type, request.StartDate, request.EndDate,
            request.Days, request.Reason, null), internalCall: true, ct);
        return error ?? Ok(new { leave!.Id, leave.Days, leave.WorkflowRequestId, status = leave.Status.ToString() });
    }

    private async Task<(IActionResult? Error, LeaveRequest? Leave)> CreateCoreAsync(CreateLeaveRequest request, bool internalCall, CancellationToken ct, bool onBehalf = false)
    {
        IActionResult Bad(string m) => BadRequest(new { message = m });
        if (request.EndDate < request.StartDate)
            return (Bad("Bitiş tarihi başlangıçtan önce olamaz"), null);
        if (request.EndDate.DayNumber - request.StartDate.DayNumber > 365)
            return (Bad("Tek bir izin talebi en fazla 1 yıl sürebilir"), null);
        if (request.StartDate.Year != request.EndDate.Year)
            return (Bad("Yıl sonunu aşan izni iki ayrı talep olarak girin (bakiyeler yıllıktır)"), null);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (request.StartDate < today.AddDays(-90))
            return (Bad("90 günden daha eski bir tarih için izin talebi açılamaz"), null);
        if (request.Reason is { Length: > 1000 })
            return (Bad("Gerekçe en fazla 1000 karakter olabilir"), null);

        var workingDays = await WorkingDaysAsync(request.StartDate, request.EndDate, ct);
        if (workingDays == 0)
            return (Bad("Seçilen aralıkta iş günü yok"), null);
        var settings = await SettingsAsync(ct);
        var dayHours = Services.LeaveEntitlement.EffectiveDayHours(settings.DayHours);
        // Yarim gun: yalnizca tek gunluk taleplerde istemcinin 0.5 bildirmesine izin var.
        // Saatlik izin (G8): yalnizca tek gun; gun = saat / gunluk calisma saati (sirket ayari,
        // yoksa LEAVE_DAY_HOURS). Yarim gun tatil (arife) gununde saat, kalan yarim gunu asamaz.
        decimal? hours = null;
        var dayCap = request.StartDate == request.EndDate ? workingDays : 1m;
        if (request.Hours is { } h)
        {
            if (request.StartDate != request.EndDate)
                return (Bad("Saatlik izin yalnızca tek gün için girilebilir"), null);
            if (Services.LeaveText.HoursError(h, dayHours, dayCap) is { } hoursError)
                return (Bad(hoursError), null);
            hours = h;
        }
        var days = hours is { } hh
            ? Services.LeaveEntitlement.HoursToDays(hh, dayHours)
            : request.StartDate == request.EndDate && request.Days == 0.5m
                ? Math.Min(0.5m, workingDays)
                : workingDays;

        // Ayni calisanin bekleyen/onayli bir izniyle cakisan talep reddedilir.
        var overlaps = await _db.LeaveRequests.AnyAsync(r =>
            r.EmployeeId == request.EmployeeId &&
            (r.Status == LeaveRequestStatus.Submitted || r.Status == LeaveRequestStatus.Approved) &&
            r.StartDate <= request.EndDate && r.EndDate >= request.StartDate, ct);
        if (overlaps)
            return (Conflict(new { message = onBehalf ? "Çalışanın bu tarihlerle çakışan bekleyen ya da onaylı bir izni var" : "Bu tarihlerle çakışan bekleyen ya da onaylı bir izniniz var" }), null);

        var balance = await _db.LeaveBalances.FirstOrDefaultAsync(b =>
            b.EmployeeId == request.EmployeeId &&
            b.Year == request.StartDate.Year &&
            b.Type == request.Type);

        if (balance is not null && balance.RemainingDays < days)
            return (Bad($"Yetersiz bakiye. Kalan: {Services.LeaveText.Num(balance.RemainingDays)} gün"), null);
        // Yillik izin bakiye tanimi olmadan acilamaz (onceden bakiye satiri yoksa kontrol
        // tamamen atlaniyor, sinirsiz yillik izin alinabiliyordu). Diger turler
        // (hastalik, ucretsiz vb.) bakiyesiz olabilir.
        if (balance is null && request.Type == LeaveType.Annual)
            return (Bad(onBehalf
                ? $"Çalışanın {request.StartDate.Year} yılı için yıllık izin bakiyesi tanımlı değil. Önce bakiye tanımlayın."
                : $"{request.StartDate.Year} yılı için yıllık izin bakiyeniz tanımlı değil. İK ile iletişime geçin."), null);

        var leave = new LeaveRequest
        {
            EmployeeId = request.EmployeeId,
            Type = request.Type,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            Days = days,
            Hours = hours,
            Reason = request.Reason,
            Status = LeaveRequestStatus.Submitted,
            // GUVENLIK: istemcinin WorkflowRequestId'si ARTIK KULLANILMAZ - onay akisini
            // yalnizca sunucu baslatir (aksi halde kendi actigi sahte bir akisi kendi
            // talebine baglayip onaylatabilirdi).
            WorkflowRequestId = null
        };

        if (balance is not null)
        {
            balance.PendingDays += days;
            balance.UpdatedAt = DateTimeOffset.UtcNow;
        }

        _db.LeaveRequests.Add(leave);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return (Conflict(new { message = onBehalf ? "Çalışanın bakiyesi aynı anda başka bir işlemle güncellendi; lütfen tekrar deneyin." : "Bakiyeniz aynı anda başka bir işlemle güncellendi; lütfen tekrar deneyin." }), null);
        }

        // Departman basina onay workflow'u ac - bulunamazsa (bas atanmamis,
        // cross-service cagri hatasi) talep yine de Submitted olarak kalir
        // ve mevcut /resolve ucuyla elle sonuclandirilabilir.
        // Başlıkta İngilizce enum adı ("Annual talebi") yerine Türkçe izin türü.
        // Sayılar Türkçe biçimli ("0,5 günlük"); İngilizce arayüz "@server:" kalıp anahtarlarıyla çevirir.
        var subject = Services.LeaveText.Subject(leave.Type, days, hours, leave.StartDate, leave.EndDate);
        // Onay mesajlarında (web, e-posta, sohbet) tarih ve gün sayısı gösterilebilsin diye.
        // Gerekçe ("reason") izin formunda "onaycıya görünür" diye belirtilir: onay ayrıntısında
        // onaycıya ve İK'ya gösterilir. KVKK: akış tanımında "reason" gizli alan seçilmişse
        // workflow-service onu talep sahibi ve İK dışındakilere döndürmez. Sohbet kartları ve
        // e-posta bildirimleri yük içeriğini (gerekçeyi) taşımaz.
        // Madde 69: onaycıya talep anındaki ekip çakışması (yalnızca sayı; adlar onay ekranında canlı sorgulanır).
        int? teamSize = null, teamOnLeave = null;
        if (settings.ConflictWarnEnabled)
        {
            try
            {
                var (team, teamOverlaps) = await TeamOverlapAsync(leave.EmployeeId, leave.StartDate, leave.EndDate, leave.Id, ct);
                if (team.Count > 0) { teamSize = team.Count; teamOnLeave = teamOverlaps.Select(o => o.EmployeeId).Distinct().Count(); }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* çakışma bilgisi olmadan da akış açılır */ }
        }
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            leaveRequestId = leave.Id, type = leave.Type.ToString(), startDate = leave.StartDate.ToString("yyyy-MM-dd"),
            endDate = leave.EndDate.ToString("yyyy-MM-dd"), days = leave.Days, hours = leave.Hours,
            reason = string.IsNullOrWhiteSpace(leave.Reason) ? null : leave.Reason.Trim(),
            teamSize, teamOnLeave,
        });
        var workflowId = internalCall
            ? await _approvals.StartLeaveApprovalInternalAsync(_db, leave.EmployeeId, subject, ct, payload)
            : await _approvals.StartLeaveApprovalAsync(leave.EmployeeId, subject, ct, payload);
        if (workflowId is not null)
        {
            leave.WorkflowRequestId = workflowId;
            await _db.SaveChangesAsync();
        }

        return (null, leave);
    }

    /// <summary>Izin turunun Turkce adi (onay basliklari icin; arayuzdeki leaveTypeLabels ile ayni).</summary>
    public static string TypeLabel(LeaveType type) => Services.LeaveText.TypeLabel(type);

    /// <summary>
    /// Onay akisi OLMAYAN bir talebi (orn. departman basinin kendi izni, basi atanmamis
    /// departman) IK'nin elle sonuclandirmasi ve bakiyeyi kesinlestirmesi. Akisi olan
    /// talepler workflow-service'teki karar uzerinden sonuclanir (409).
    /// </summary>
    [HttpPost("{id}/resolve")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Resolve(Guid id, [FromBody] ResolveLeaveRequest request, CancellationToken ct)
    {
        var leave = await _db.LeaveRequests.FirstOrDefaultAsync(x => x.Id == id);
        if (leave is null) return NotFound();
        if (leave.Status != LeaveRequestStatus.Submitted)
            return BadRequest("Yalnızca onay bekleyen talepler sonuçlandırılabilir");

        // GUVENLIK: bu uc, workflow-service'teki asil onay akisi
        // basarisiz olursa diye birakilan elle-sonuclandirma yolu (bkz.
        // Create yorumu) - ama RequireManagerOrAbove tek basina "sen bu
        // talebin sahibi misin" sorusunu cevaplamiyordu. Departman bazli
        // tam yetki kontrolu icin workflow-service'e bagimli olmadan, en
        // azindan en bariz acigi (kendi talebini kendi onaylama/reddetme)
        // kapatiyoruz.
        var myEmployeeId = await _approvals.FindMyEmployeeIdAsync(ct);
        if (myEmployeeId is null || myEmployeeId.Value == leave.EmployeeId)
            return Forbid();

        // GUVENLIK: Onceden herhangi bir yonetici, onay akisi hala bekleyen herhangi
        // bir talebi (departman basini atlayarak) buradan sonuclandirabiliyordu. Bu uc
        // artik yalnizca akisi OLMAYAN talepler (orn. departman basinin kendi izni,
        // bas atanmamis departman) icin ve yalnizca IK tarafindan kullanilabilir.
        if (!IsHr) return Forbid();
        if (leave.WorkflowRequestId is not null)
            return Conflict(new { message = "Bu talep onay akışı üzerinden sonuçlandırılmalı" });

        var balance = await _db.LeaveBalances.FirstOrDefaultAsync(b =>
            b.EmployeeId == leave.EmployeeId &&
            b.Year == leave.StartDate.Year &&
            b.Type == leave.Type);

        if (balance is not null)
        {
            balance.PendingDays -= leave.Days;
            if (request.Approved) balance.UsedDays += leave.Days;
            balance.UpdatedAt = DateTimeOffset.UtcNow;
        }

        leave.Status = request.Approved ? LeaveRequestStatus.Approved : LeaveRequestStatus.Rejected;
        leave.DecidedAt = DateTimeOffset.UtcNow;

        // NOT: Onceden bu yol hic event yayinlamiyordu - elle onaylanan izin
        // timeshift-service'e ulasmiyor, vardiya planinda hic gorunmuyordu.
        _db.OutboxMessages.Add(new OutboxMessage
        {
            Topic = LeaveTopics.Events,
            EventType = request.Approved ? "leave.approved" : "leave.rejected",
            PartitionKey = leave.EmployeeId.ToString(),
            Payload = JsonSerializer.Serialize(new LeaveDecidedEvent(
                leave.TenantSlug, leave.Id, leave.EmployeeId, leave.StartDate, leave.EndDate,
                request.Approved, DateTimeOffset.UtcNow, leave.Type.ToString(), leave.Days, leave.Hours)),
        });

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { message = "Bakiye aynı anda güncellendi; lütfen tekrar deneyin." });
        }
        return Ok(leave);
    }

    /// <summary>
    /// Onaylı ve henüz başlamamış izni iptal eder (madde 44): İK ya da çalışanın yöneticisi (ekibini görebilen
    /// departman başı); kendi iznini iptal edemez. Gerekçe zorunlu. Kullanılan gün bakiyeye geri döner,
    /// "leave.cancelled" olayı yayımlanır (timeshift-service vardiya planındaki izinli günleri kaldırır),
    /// çalışana bildirim gider, gerekçe denetim kaydına (CancelledAfterApproval) yazılır.
    /// Onay akışı zaten sonuçlanmış olduğundan workflow-service'te değişiklik yapılmaz.
    /// </summary>
    [HttpPost("{id}/cancel-approved")]
    public async Task<IActionResult> CancelApproved(Guid id, [FromBody] CancelApprovedLeaveRequest body, CancellationToken ct)
    {
        if (!IsManagerOrAbove) return Forbid();
        var leave = await _db.LeaveRequests.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (leave is null) return NotFound();
        if (await VisibleEmployeesAsync(ct) is { } visible && !visible.Contains(leave.EmployeeId)) return NotFound();
        var me = await _approvals.FindMyEmployeeIdAsync(ct);
        if (me is not null && me.Value == leave.EmployeeId)
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Kendi onaylı izninizi iptal edemezsiniz; iptal isteği gönderin." });
        var reason = body?.Reason?.Trim() ?? "";
        if (Services.LeaveCancellation.ReasonError(reason) is { } re) return BadRequest(new { message = re });
        if (Services.LeaveCancellation.ApprovedCancelError(leave.Status, leave.StartDate, Services.LeaveCancellation.TodayTr()) is { } err)
            return Conflict(new { message = err, code = "not_cancellable" });

        var balance = await _db.LeaveBalances.FirstOrDefaultAsync(b =>
            b.EmployeeId == leave.EmployeeId && b.Year == leave.StartDate.Year && b.Type == leave.Type, ct);
        if (balance is not null)
        {
            balance.UsedDays = Math.Max(0, balance.UsedDays - leave.Days);
            balance.UpdatedAt = DateTimeOffset.UtcNow;
        }
        leave.Status = LeaveRequestStatus.Cancelled;
        _db.OutboxMessages.Add(new OutboxMessage
        {
            Topic = LeaveTopics.Events,
            EventType = "leave.cancelled",
            PartitionKey = leave.EmployeeId.ToString(),
            Payload = JsonSerializer.Serialize(new LeaveDecidedEvent(
                leave.TenantSlug, leave.Id, leave.EmployeeId, leave.StartDate, leave.EndDate,
                false, DateTimeOffset.UtcNow, leave.Type.ToString(), leave.Days, leave.Hours)),
        });
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { message = "Bakiye aynı anda güncellendi; lütfen tekrar deneyin." });
        }
        await ExplicitAuditAsync(leave, "CancelledAfterApproval", new { from = "Approved", to = "Cancelled", reason, days = leave.Days, restoredToBalance = balance is not null }, ct);
        var subject = $"Onaylı izniniz iptal edildi: {Services.LeaveText.Subject(leave.Type, leave.Days, leave.Hours, leave.StartDate, leave.EndDate)}";
        await NotifyAsync(leave.TenantSlug, leave.EmployeeId, "leave.cancelled-approved", subject, $"Gerekçe: {reason}", ct);
        return Ok(leave);
    }

    /// <summary>
    /// Çalışanın onaylı ve henüz başlamamış izni için iptal isteği: yöneticisine (departman başı) uygulama
    /// içi bildirim gider; iptali yönetici ya da İK yapar (cancel-approved). Denetim kaydına yazılır.
    /// </summary>
    [HttpPost("{id}/request-cancellation")]
    public async Task<IActionResult> RequestCancellation(Guid id, [FromBody] CancelApprovedLeaveRequest body, CancellationToken ct)
    {
        var leave = await _db.LeaveRequests.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (leave is null) return NotFound();
        var me = await _approvals.FindMyEmployeeIdAsync(ct);
        if (me is null || me.Value != leave.EmployeeId) return NotFound();
        var reason = body?.Reason?.Trim() ?? "";
        if (Services.LeaveCancellation.ReasonError(reason) is { } re) return BadRequest(new { message = re });
        if (Services.LeaveCancellation.ApprovedCancelError(leave.Status, leave.StartDate, Services.LeaveCancellation.TodayTr()) is { } err)
            return Conflict(new { message = err, code = "not_cancellable" });
        var head = await _db.Database.SqlQueryRaw<Guid?>(
            """
            SELECT d."HeadEmployeeId" AS "Value" FROM employee_assignments a
            JOIN organization_departments d ON d."Id" = a."DepartmentId"
            WHERE a."TenantSlug" = {0} AND a."EmployeeId" = {1}
              AND a."EffectiveFrom" <= current_date AND (a."EffectiveTo" IS NULL OR a."EffectiveTo" >= current_date)
            ORDER BY a."EffectiveFrom" DESC LIMIT 1
            """, leave.TenantSlug, leave.EmployeeId).FirstOrDefaultAsync(ct);
        var notified = head is { } h && h != leave.EmployeeId;
        if (notified)
        {
            var who = await _db.Database.SqlQueryRaw<string>(
                "SELECT (\"FirstName\" || ' ' || \"LastName\") AS \"Value\" FROM employee_employees WHERE \"TenantSlug\" = {0} AND \"Id\" = {1}",
                leave.TenantSlug, leave.EmployeeId).FirstOrDefaultAsync(ct) ?? "";
            var subject = $"İzin iptal isteği: {who} — {Services.LeaveText.Subject(leave.Type, leave.Days, leave.Hours, leave.StartDate, leave.EndDate)}";
            await NotifyAsync(leave.TenantSlug, head!.Value, "leave.cancel-request", subject, $"Gerekçe: {reason}", ct);
        }
        await ExplicitAuditAsync(leave, "CancellationRequested", new { reason, managerNotified = notified }, ct);
        return Ok(new
        {
            notified,
            message = notified
                ? "İptal isteğiniz yöneticinize iletildi."
                : "Yöneticiniz bulunamadı; iptal için İK ile iletişime geçin.",
        });
    }

    /// <summary>Uygulama içi bildirim (notification-service kuyruğu). Yazılamazsa iş işlemi bozulmaz.</summary>
    private async Task NotifyAsync(string tenant, Guid employeeId, string template, string subject, string body, CancellationToken ct)
    {
        try
        {
            await _db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO notification_messages ("Id","TenantSlug","RecipientEmployeeId","RecipientEmail","Channel","TemplateCode","Subject","Body","Status","AttemptCount","CreatedAt","ActionUrl")
                VALUES ({0},{1},{2},NULL,'InApp',{3},{4},{5},'Pending',0,now(),'/panel/izin')
                """, new object[] { Guid.NewGuid(), tenant, employeeId, template, subject, body }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            HttpContext.RequestServices.GetService<ILogger<LeaveRequestsController>>()?.LogWarning("İzin bildirimi yazılamadı: {Error}", ex.Message);
        }
    }

    /// <summary>Gerekçeli denetim satırı (otomatik AuditInterceptor satırına ek; gerekçe orada yok).</summary>
    private async Task ExplicitAuditAsync(LeaveRequest leave, string action, object changes, CancellationToken ct)
    {
        try
        {
            var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value ?? "unknown";
            var userName = User.FindFirst("name")?.Value ?? User.FindFirst("preferred_username")?.Value ?? userId;
            var correlation = Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? HttpContext.TraceIdentifier;
            await _db.Database.ExecuteSqlRawAsync(
                "INSERT INTO audit_log (\"TenantSlug\",\"Service\",\"EntityType\",\"EntityId\",\"Action\",\"Changes\",\"UserId\",\"UserName\",\"CorrelationId\",\"IpAddress\",\"OccurredAt\") " +
                "VALUES ({0},'leave-service','LeaveRequest',{1},{2},{3}::jsonb,{4},{5},{6},{7},now())",
                new object[] { leave.TenantSlug, leave.Id.ToString(), action, JsonSerializer.Serialize(changes), userId, userName, correlation,
                    (object?)Request.Headers["X-Real-IP"].FirstOrDefault() ?? DBNull.Value }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            HttpContext.RequestServices.GetService<ILogger<LeaveRequestsController>>()?.LogWarning("İzin denetim kaydı yazılamadı: {Error}", ex.Message);
        }
    }

    [HttpPost("{id}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var leave = await _db.LeaveRequests.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (leave is null) return NotFound();
        // GUVENLIK: Sahiplik kontrolu yoktu - herkes baskasinin bekleyen iznini iptal
        // edebiliyordu. Yalnizca talep sahibi ya da IK.
        if (!IsHr)
        {
            var me = await _approvals.FindMyEmployeeIdAsync(ct);
            if (me is null || me.Value != leave.EmployeeId) return NotFound();
        }
        var (code, message) = await CancelCoreAsync(leave, internalCall: false, ct);
        return code switch
        {
            0 => Ok(leave),
            // Web istemcisi bu iki durumda düz metin bekler (eski biçim korunur).
            StatusCodes.Status400BadRequest => BadRequest(message),
            _ => StatusCode(code, new { message }),
        };
    }

    /// <summary>
    /// Sohbet botundan (Slack/Teams) izin iptali: web ucuyla aynı kural (CancelCoreAsync).
    /// Çalışan, botun doğrulanmış sohbet hesabından gelir; yalnızca kendi talebini iptal eder.
    /// Denetim kaydı bu serviste, kişi adına ve "via: chat" işaretiyle yazılır.
    /// Gateway /api/*/internal/ yollarını dışarıya kapatır; anahtar yoksa uç kapalıdır.
    /// </summary>
    [HttpPost("/api/internal/chat/leave-cancel")]
    [AllowAnonymous]
    public async Task<IActionResult> CancelInternal([FromBody] InternalCancelLeaveRequest request,
        [FromServices] Tenancy.TenantContext tenant, CancellationToken ct)
    {
        if (!InternalTokenValid()) return NotFound();
        if (string.IsNullOrWhiteSpace(request.TenantSlug)) return BadRequest(new { message = "Kiracı belirtilmedi" });
        if (request.EmployeeId == Guid.Empty || request.LeaveId == Guid.Empty)
            return BadRequest(new { message = "Çalışan ve izin talebi belirtilmeli" });
        tenant.TenantSlug = request.TenantSlug;
        tenant.IsPlatformAdmin = false;
        var channel = ChatChannel(request.Channel);
        // Otomatik denetim satırları (AuditInterceptor) "system" yerine kişiye yazılsın.
        HttpContext.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(new[]
        {
            new System.Security.Claims.Claim("sub", $"employee:{request.EmployeeId}"),
            new System.Security.Claims.Claim("name", $"Sohbet ({channel})"),
        }));

        var leave = await _db.LeaveRequests.FirstOrDefaultAsync(x => x.Id == request.LeaveId, ct);
        // Başkasının talebi: varlığını sızdırmamak için 404.
        if (leave is null || leave.EmployeeId != request.EmployeeId)
            return NotFound(new { message = "İzin talebi bulunamadı." });
        var from = leave.Status;
        var (code, message) = await CancelCoreAsync(leave, internalCall: true, ct);
        // Sonuçlanmış/iptal edilmiş talep: 409 + code "not_cancellable" (bot İngilizce metnini buna göre seçer).
        if (code == StatusCodes.Status400BadRequest)
            return Conflict(new { message, code = "not_cancellable", status = from.ToString() });
        if (code != 0) return StatusCode(code, new { message, code = "retry" });

        await ChatAuditAsync(leave, from, request.EmployeeId, channel, ct);
        return Ok(new
        {
            leave.Id, type = leave.Type.ToString(), startDate = leave.StartDate, endDate = leave.EndDate,
            leave.Days, from = from.ToString(), status = leave.Status.ToString(),
        });
    }

    private bool InternalTokenValid() =>
        Security.InternalServiceToken.Matches(Request.Headers[Security.InternalServiceToken.Header].FirstOrDefault());

    /// <summary>Denetim kaydındaki kanal adı: bilinen sohbet sağlayıcısı, yoksa "chat".</summary>
    internal static string ChatChannel(string? channel) =>
        channel is "Slack" or "Teams" or "Mattermost" or "RocketChat" ? channel : "chat";

    /// <summary>
    /// Botun eskiden yazdığı satırla aynı eylem adı (LeaveRequest / Cancelled); artık sahibi olan
    /// serviste. Denetim yazılamazsa iş işlemi bozulmaz (AuditInterceptor ile aynı ilke).
    /// </summary>
    private async Task ChatAuditAsync(LeaveRequest leave, LeaveRequestStatus from, Guid employeeId, string channel, CancellationToken ct)
    {
        try
        {
            var changes = JsonSerializer.Serialize(new { from = from.ToString(), to = leave.Status.ToString(), via = "chat" });
            var correlation = Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? HttpContext.TraceIdentifier;
            await _db.Database.ExecuteSqlRawAsync(
                "INSERT INTO audit_log (\"TenantSlug\",\"Service\",\"EntityType\",\"EntityId\",\"Action\",\"Changes\",\"UserId\",\"UserName\",\"CorrelationId\",\"IpAddress\",\"OccurredAt\") " +
                "VALUES ({0},'leave-service','LeaveRequest',{1},'Cancelled',{2}::jsonb,{3},{4},{5},NULL,now())",
                new object[] { leave.TenantSlug, leave.Id.ToString(), changes, $"employee:{employeeId}", $"Sohbet ({channel})", correlation }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[audit] leave-service: sohbet iptal denetim kaydı yazılamadı: {ex.Message}");
        }
    }

    /// <summary>
    /// İptal kuralı (web ve sohbet ortak): yalnızca sonuçlanmamış talep (Draft/Submitted);
    /// Submitted ise bekleyen gün bakiyeden düşülür; açık onay akışı kapatılır.
    /// Dönüş: (0, null) başarı; aksi halde HTTP kodu ve Türkçe ileti. Sahiplik kontrolü çağıranda.
    /// </summary>
    private async Task<(int Code, string? Message)> CancelCoreAsync(LeaveRequest leave, bool internalCall, CancellationToken ct)
    {
        if (leave.Status == LeaveRequestStatus.Cancelled)
            return (StatusCodes.Status400BadRequest, "Talep zaten iptal edilmiş");
        if (leave.Status is LeaveRequestStatus.Approved or LeaveRequestStatus.Rejected)
            return (StatusCodes.Status400BadRequest, "Sonuçlanmış talep iptal edilemez");

        var balance = await _db.LeaveBalances.FirstOrDefaultAsync(b =>
            b.EmployeeId == leave.EmployeeId &&
            b.Year == leave.StartDate.Year &&
            b.Type == leave.Type, ct);

        if (balance is not null && leave.Status == LeaveRequestStatus.Submitted)
        {
            balance.PendingDays -= leave.Days;
            balance.UpdatedAt = DateTimeOffset.UtcNow;
        }

        leave.Status = LeaveRequestStatus.Cancelled;
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return (StatusCodes.Status409Conflict, "Bakiye aynı anda güncellendi; lütfen tekrar deneyin.");
        }

        // NOT: Onceden iptal edilen iznin onay akisi acik kaliyordu - onayci talebi
        // "Onay kutusu"nda gormeye devam ediyor, onaylasa bile hicbir sey olmuyordu.
        // Sohbet yolunda kullanicinin jetonu yok: workflow-service'in ic ucu kullanilir.
        if (leave.WorkflowRequestId is { } wf)
        {
            if (internalCall) await _approvals.CancelWorkflowInternalAsync(leave.TenantSlug, wf, leave.EmployeeId, ct);
            else await _approvals.CancelWorkflowAsync(wf, ct);
        }
        return (0, null);
    }
}

public record CreateLeaveRequest(
    Guid EmployeeId, LeaveType Type, DateOnly StartDate, DateOnly EndDate,
    decimal Days, string? Reason, Guid? WorkflowRequestId, decimal? Hours = null);

public record ResolveLeaveRequest(bool Approved);
/// <summary>Onaylı izin iptali / iptal isteği gerekçesi (5–500 karakter).</summary>
public record CancelApprovedLeaveRequest(string? Reason);
public record InternalCreateLeaveRequest(string TenantSlug, Guid EmployeeId, LeaveType Type, DateOnly StartDate, DateOnly EndDate,
    decimal Days, string? Reason);
/// <summary>Sohbet botundan iptal: Channel isteğe bağlı (Slack/Teams/...; denetim kaydındaki kanal adı).</summary>
public record InternalCancelLeaveRequest(string TenantSlug, Guid EmployeeId, Guid LeaveId, string? Channel = null);

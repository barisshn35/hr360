using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TimeShiftService.Data;
using TimeShiftService.Models;
using TimeShiftService.Services;

namespace TimeShiftService.Controllers;

/// <summary>
/// Bir ShiftPattern'i takip eden calisan grubu. Ayni pattern'i farkli
/// AnchorDate ile takip eden birden fazla ekip olusturarak surekli
/// kapsama saglanabilir (A ekibi gunduz baslarken B ekibi gece baslar).
/// </summary>
[ApiController]
[Route("api/shift-teams")]
[Authorize]
public class ShiftTeamsController : ControllerBase
{
    private readonly TimeShiftDbContext _db;
    private readonly EmployeeDirectoryClient _employees;
    private readonly DepartmentDirectoryClient _departments;

    public ShiftTeamsController(
        TimeShiftDbContext db, EmployeeDirectoryClient employees, DepartmentDirectoryClient departments,
        TimeShiftService.Tenancy.ITenantContext tenant)
    {
        _db = db;
        _employees = employees;
        _departments = departments;
        _tenant = tenant;
    }

    private readonly TimeShiftService.Tenancy.ITenantContext _tenant;
    private bool IsHr => User.IsInRole("hr-admin") || User.IsInRole("tenant-admin") || User.IsInRole("platform-admin")
        || User.IsInRole("ext-timeshift-manage");

    /// <summary>Çağıranın görebileceği ekipler (İK: null = tümü). Bkz. ShiftScope.VisibleTeams.</summary>
    private async Task<HashSet<Guid>?> VisibleTeamIdsAsync(CancellationToken ct)
    {
        if (IsHr) return null;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var teams = (await _db.ShiftTeams.AsNoTracking().Select(t => new { t.Id, t.DepartmentId }).ToListAsync(ct))
            .Select(t => (t.Id, t.DepartmentId));
        return ShiftScope.VisibleTeams(false, await _employees.FindMyEmployeeIdAsync(ct), teams,
            await ShiftScope.MembershipsAsync(_db, today, today, ct), await TsOps.PeopleAsync(_db, _tenant.TenantSlug ?? "", ct));
    }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] Guid? departmentId, CancellationToken ct)
    {
        var visible = await VisibleTeamIdsAsync(ct);
        var q = _db.ShiftTeams
            .Include(t => t.ShiftPattern!.Days)
            .Include(t => t.Members.Where(m => m.EffectiveTo == null))
            .AsQueryable();
        if (departmentId.HasValue) q = q.Where(t => t.DepartmentId == departmentId.Value);
        if (visible is not null) q = q.Where(t => visible.Contains(t.Id));
        return Ok(await q.OrderBy(t => t.Name).ToListAsync());
    }

    public record CreateTeamRequest(string Name, Guid ShiftPatternId, DateOnly AnchorDate, Guid? DepartmentId);

    [HttpPost]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> Create([FromBody] CreateTeamRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { message = "Ekip adı boş olamaz" });
        if (!await _db.ShiftPatterns.AnyAsync(p => p.Id == request.ShiftPatternId))
            return BadRequest(new { message = "Vardiya deseni bulunamadı" });

        var team = new ShiftTeam
        {
            Name = request.Name.Trim(),
            ShiftPatternId = request.ShiftPatternId,
            AnchorDate = request.AnchorDate,
            DepartmentId = request.DepartmentId,
        };
        _db.ShiftTeams.Add(team);
        await _db.SaveChangesAsync();
        return Created($"/api/shift-teams/{team.Id}", team);
    }

    public record UpdateTeamRequest(string? Name, DateOnly? AnchorDate, Guid? DepartmentId, bool DepartmentIdSet = false, Guid? ShiftPatternId = null);

    /// <summary>
    /// Ekip adi/donguyu baslatma tarihi/departman kisiti duzeltilir.
    /// DepartmentId'nin "gonderilmedi" (mevcut korunsun) ile "null'a
    /// cekildi" (kisit kaldirildi) ayrimi icin DepartmentIdSet bayragi
    /// kullanilir - JSON'da alan hic yoksa false, "departmentId": null
    /// olarak gonderilirse true+null gelir.
    /// </summary>
    [HttpPut("{id}")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> UpdateTeam(Guid id, [FromBody] UpdateTeamRequest request)
    {
        var team = await _db.ShiftTeams.FirstOrDefaultAsync(t => t.Id == id);
        if (team is null) return NotFound();

        if (!string.IsNullOrWhiteSpace(request.Name)) team.Name = request.Name.Trim();
        if (request.AnchorDate.HasValue) team.AnchorDate = request.AnchorDate.Value;
        if (request.DepartmentIdSet) team.DepartmentId = request.DepartmentId;
        // Ekibi başka desene taşımak: kullanımdaki desen silinmeden önce ekipler buradan taşınır.
        // Takvim hesaplanır (saklanmaz); yeni desen döngü başlangıcından itibaren uygulanır.
        if (request.ShiftPatternId is { } pid && pid != team.ShiftPatternId)
        {
            if (!await _db.ShiftPatterns.AnyAsync(p => p.Id == pid && p.IsActive))
                return BadRequest(new { message = "Vardiya deseni bulunamadı ya da pasif" });
            team.ShiftPatternId = pid;
        }

        await _db.SaveChangesAsync();
        return Ok(team);
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> DeleteTeam(Guid id)
    {
        var team = await _db.ShiftTeams
            .Include(t => t.Members.Where(m => m.EffectiveTo == null))
            .FirstOrDefaultAsync(t => t.Id == id);
        if (team is null) return NotFound();

        if (team.Members.Count > 0)
            return Conflict(new { message = "Bu ekipte aktif üyeler var - önce hepsini çıkarın veya taşıyın" });

        _db.ShiftTeams.Remove(team);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>
    /// Ekibe uye ekler. Ekip bir departmana bagliysa, calisanin o
    /// departmanda (veya alt departmanlarinda) etkin atamasi olup
    /// olmadigi employee-service + organization-service'e cross-service
    /// call ile DOGRULANIR - API dogrudan cagrilsa bile kisit atlanamaz.
    /// </summary>
    public record AddMemberRequest(Guid EmployeeId, int Rank, string? Tag, DateOnly EffectiveFrom);

    [HttpPost("{id}/members")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> AddMember(Guid id, [FromBody] AddMemberRequest request, CancellationToken ct)
    {
        var team = await _db.ShiftTeams.FirstOrDefaultAsync(t => t.Id == id);
        if (team is null) return NotFound(new { message = "Ekip bulunamadı" });

        // Ekip bir departmana bagliysa, calisan o departmanda (ya da
        // ALT departmanlarindan birinde) etkin atamali olmali.
        if (team.DepartmentId.HasValue)
        {
            var employeeDept = await _employees.GetActiveDepartmentAsync(
                request.EmployeeId, request.EffectiveFrom, ct);
            if (employeeDept is null)
                return BadRequest(new { message = "Çalışanın bu tarihte etkin bir departman ataması yok" });

            var allowedDepartments = await _departments.GetSubtreeIdsAsync(team.DepartmentId.Value, ct);
            if (!allowedDepartments.Contains(employeeDept.Value))
                return BadRequest(new
                {
                    message = "Çalışan bu ekibin bağlı olduğu departmanda (veya alt departmanlarında) değil",
                });
        }

        var current = await _db.ShiftTeamMembers.FirstOrDefaultAsync(m =>
            m.EmployeeId == request.EmployeeId && m.EffectiveTo == null);

        // Ayni ekipte zaten etkin uye ise 409 - "cikar, tekrar ekle" ile
        // rank/tag guncellemesi PATCH ucundan yapilmali, burada degil.
        if (current is not null && current.ShiftTeamId == id)
            return Conflict(new { message = "Çalışan bu ekipte zaten etkin üye" });

        // Baska bir ekipteyse o uyelik kapatilir - ekip degisimi.
        if (current is not null) current.EffectiveTo = request.EffectiveFrom.AddDays(-1);

        var member = new ShiftTeamMember
        {
            ShiftTeamId = id,
            EmployeeId = request.EmployeeId,
            Rank = request.Rank,
            Tag = request.Tag,
            EffectiveFrom = request.EffectiveFrom,
        };
        _db.ShiftTeamMembers.Add(member);
        await _db.SaveChangesAsync();
        return Created($"/api/shift-teams/{id}/members/{member.Id}", member);
    }

    public record UpdateMemberRequest(int? Rank, string? Tag, bool TagSet = false);

    [HttpPatch("{id}/members/{memberId}")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> UpdateMember(Guid id, Guid memberId, [FromBody] UpdateMemberRequest request)
    {
        var member = await _db.ShiftTeamMembers
            .FirstOrDefaultAsync(m => m.Id == memberId && m.ShiftTeamId == id);
        if (member is null) return NotFound();

        if (request.Rank.HasValue) member.Rank = request.Rank.Value;
        if (request.TagSet) member.Tag = request.Tag;

        await _db.SaveChangesAsync();
        return Ok(member);
    }

    /// <summary>
    /// Uyeligi sonlandirir. effectiveTo verilmezse BUGUN kullanilir (o gun
    /// hala ekipte sayilir, YARINDAN itibaren cikar). Ileri tarihli cikis
    /// icin effectiveTo query parametresi verilebilir.
    /// </summary>
    [HttpDelete("{id}/members/{memberId}")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> RemoveMember(Guid id, Guid memberId, [FromQuery] DateOnly? effectiveTo)
    {
        var member = await _db.ShiftTeamMembers
            .FirstOrDefaultAsync(m => m.Id == memberId && m.ShiftTeamId == id);
        if (member is null) return NotFound();

        member.EffectiveTo = effectiveTo ?? DateOnly.FromDateTime(DateTime.UtcNow);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    private const int MaxRosterDays = 93;

    /// <summary>
    /// Ekibin belirli bir tarih araligi icin HESAPLANMIS vardiya listesi -
    /// pattern'e gore turetilir, ShiftOverride varsa (izin/tatil/manuel)
    /// onun yerini alir. Uyeler Rank'e gore siralanir (en yetkiliden basa).
    ///
    /// [from,to] araliginda HERHANGI bir gun etkin olan TUM uyeler donulur -
    /// aralikta ayrilmis veya sonradan katilmis olanlar dahil. Her uyenin
    /// schedule'i SADECE kendi (effectiveFrom..effectiveTo) araligiyla
    /// sinirlidir; disinda kalan gunler icin "type": null (bos hucre) doner.
    /// </summary>
    [HttpGet("{id}/roster")]
    public async Task<IActionResult> GetRoster(Guid id, [FromQuery] DateOnly from, [FromQuery] DateOnly to, CancellationToken ct = default)
    {
        if (await VisibleTeamIdsAsync(ct) is { } vis && !vis.Contains(id)) return NotFound();
        if (to < from) return BadRequest(new { message = "Bitiş tarihi başlangıçtan önce olamaz" });
        if ((to.DayNumber - from.DayNumber) > MaxRosterDays)
            return BadRequest(new { message = $"Takvim en fazla {MaxRosterDays} gün için hesaplanabilir" });

        var team = await _db.ShiftTeams
            .Include(t => t.ShiftPattern!.Days)
            .FirstOrDefaultAsync(t => t.Id == id);
        if (team is null || team.ShiftPattern is null) return NotFound();

        var members = await _db.ShiftTeamMembers
            .Where(m => m.ShiftTeamId == id && m.EffectiveFrom <= to
                     && (m.EffectiveTo == null || m.EffectiveTo >= from))
            .ToListAsync();

        var roster = await ComputeRosterAsync(team, members, from, to);
        return Ok(roster);
    }

    /// <summary>
    /// Coklu ekip roster'i - "Tum ekipler" gorunumu icin tek istekte
    /// tum ekiplerin (ya da secilenlerin) takvimini doner. teamIds
    /// verilmezse tenant'taki TUM ekipler hesaplanir.
    /// </summary>
    [HttpGet("roster")]
    public async Task<IActionResult> GetBulkRoster(
        [FromQuery] DateOnly from, [FromQuery] DateOnly to, [FromQuery] string? teamIds)
    {
        if (to < from) return BadRequest(new { message = "Bitiş tarihi başlangıçtan önce olamaz" });
        if ((to.DayNumber - from.DayNumber) > MaxRosterDays)
            return BadRequest(new { message = $"Takvim en fazla {MaxRosterDays} gün için hesaplanabilir" });

        var q = _db.ShiftTeams.Include(t => t.ShiftPattern!.Days).AsQueryable();
        if (!string.IsNullOrWhiteSpace(teamIds))
        {
            var ids = teamIds.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(s => Guid.TryParse(s, out var g) ? g : (Guid?)null)
                .Where(g => g.HasValue).Select(g => g!.Value).ToHashSet();
            q = q.Where(t => ids.Contains(t.Id));
        }
        if (await VisibleTeamIdsAsync(HttpContext.RequestAborted) is { } vis) q = q.Where(t => vis.Contains(t.Id));
        var teams = await q.ToListAsync();

        var results = new List<object>();
        foreach (var team in teams)
        {
            if (team.ShiftPattern is null) continue;
            var members = await _db.ShiftTeamMembers
                .Where(m => m.ShiftTeamId == team.Id && m.EffectiveFrom <= to
                         && (m.EffectiveTo == null || m.EffectiveTo >= from))
                .ToListAsync();
            results.Add(await ComputeRosterAsync(team, members, from, to));
        }

        return Ok(results);
    }

    /// <summary>
    /// Yillik ozet: uye basina tip bazinda gun sayisi ve toplam vardiya
    /// dakikasi. Gunluk detay YERINE toplam istiyor - yillik gorunum
    /// icin 365 gunluk hesaplamayi tek istekte doner.
    /// </summary>
    [HttpGet("{id}/summary")]
    public async Task<IActionResult> GetSummary(Guid id, [FromQuery] int year, CancellationToken ct = default)
    {
        if (await VisibleTeamIdsAsync(ct) is { } vis && !vis.Contains(id)) return NotFound();
        var team = await _db.ShiftTeams
            .Include(t => t.ShiftPattern!.Days)
            .FirstOrDefaultAsync(t => t.Id == id);
        if (team is null || team.ShiftPattern is null) return NotFound();

        var from = new DateOnly(year, 1, 1);
        var to = new DateOnly(year, 12, 31);

        var members = await _db.ShiftTeamMembers
            .Where(m => m.ShiftTeamId == id && m.EffectiveFrom <= to
                     && (m.EffectiveTo == null || m.EffectiveTo >= from))
            .ToListAsync();

        var roster = await ComputeRosterAsync(team, members, from, to);
        var summary = roster.Members.Select(m => new
        {
            m.EmployeeId,
            m.Rank,
            m.Tag,
            dayCounts = m.Schedule
                .Where(d => d.Type is not null)
                .GroupBy(d => d.Type)
                .ToDictionary(g => g.Key!, g => g.Count()),
            totalWorkedMinutes = m.Schedule
                .Where(d => d.StartTime.HasValue && d.EndTime.HasValue)
                .Sum(d => MinutesBetween(d.StartTime!.Value, d.EndTime!.Value)),
        });

        return Ok(new { team = team.Name, patternName = team.ShiftPattern.Name, year, members = summary });
    }

    private static int MinutesBetween(TimeOnly start, TimeOnly end)
    {
        var minutes = (end.Hour * 60 + end.Minute) - (start.Hour * 60 + start.Minute);
        if (minutes <= 0) minutes += 24 * 60; // gece yariyi gecen vardiya
        return minutes;
    }

    private record RosterDayResult(
        DateOnly Date, string? Type, TimeOnly? StartTime, TimeOnly? EndTime,
        string? Note, Guid? OverrideId, bool Partial = false);
    private record RosterMemberResult(
        Guid EmployeeId, int Rank, string? Tag, DateOnly EffectiveFrom, DateOnly? EffectiveTo,
        List<RosterDayResult> Schedule);
    private record RosterResult(string Team, string PatternName, List<RosterMemberResult> Members);

    private async Task<RosterResult> ComputeRosterAsync(
        ShiftTeam team, List<ShiftTeamMember> members, DateOnly from, DateOnly to)
    {
        var pattern = team.ShiftPattern!;
        var cycleLength = pattern.Days.Count;
        var days = pattern.Days.OrderBy(d => d.DayIndex).ToList();

        var memberIds = members.Select(m => m.EmployeeId).Distinct().ToList();
        var overrides = await _db.ShiftOverrides
            .Where(o => memberIds.Contains(o.EmployeeId) && o.Date >= from && o.Date <= to)
            .ToListAsync();

        var result = members.OrderBy(m => m.Rank).Select(member =>
        {
            var schedule = new List<RosterDayResult>();
            for (var date = from; date <= to; date = date.AddDays(1))
            {
                // Uyeligin FIILEN etkin oldugu araligin disindaki gunler bos.
                if (date < member.EffectiveFrom || (member.EffectiveTo.HasValue && date > member.EffectiveTo.Value))
                {
                    schedule.Add(new RosterDayResult(date, null, null, null, null, null));
                    continue;
                }

                var over = overrides.FirstOrDefault(o => o.EmployeeId == member.EmployeeId && o.Date == date);
                if (over is not null)
                {
                    schedule.Add(new RosterDayResult(
                        date, over.Type.ToString(), over.StartTime, over.EndTime, over.Note, over.Id, over.IsPartial));
                    continue;
                }

                if (cycleLength == 0)
                {
                    schedule.Add(new RosterDayResult(date, null, null, null, null, null));
                    continue;
                }

                // Negatif modulo'yu pozitife cevir - C#'ta % isareti korur.
                var offset = (int)(date.DayNumber - team.AnchorDate.DayNumber) % cycleLength;
                if (offset < 0) offset += cycleLength;

                var day = days[offset];
                schedule.Add(new RosterDayResult(
                    date, day.Type.ToString(), day.StartTime, day.EndTime, null, null));
            }

            return new RosterMemberResult(
                member.EmployeeId, member.Rank, member.Tag,
                member.EffectiveFrom, member.EffectiveTo, schedule);
        }).ToList();

        return new RosterResult(team.Name, pattern.Name, result);
    }
}

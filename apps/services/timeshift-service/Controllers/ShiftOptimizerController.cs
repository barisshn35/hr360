using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TimeShiftService.Data;
using TimeShiftService.Models;
using TimeShiftService.Services;
using TimeShiftService.Tenancy;

namespace TimeShiftService.Controllers;

/// <summary>
/// Madde 44: vardiya planı önerisi. Çalışanlar (seçilen kişiler, bir vardiya ekibi ya da departman), vardiya
/// tanımları ve gün x vardiya talebi ml-inference'a (/shift/optimize, OR-Tools CP-SAT; yoksa açgözlü
/// sezgisel) TAKMA ADLA gönderilir: ad, çalışan kimliği, izin türü gitmez — yalnızca "o gün müsait değil",
/// tercih günleri/türleri ve ekip etiketi (beceri). Yanıt mevcut atamalarla karşılaştırılıp fark olarak
/// döner. Otomatik uygulama YOKTUR: planlayıcı (İK ya da bölüm başı yönetici) farkı inceleyip
/// <c>POST apply</c> ile uygular; her atama denetim kaydına (AuditInterceptor) yazılır.
/// </summary>
[ApiController]
[Route("api/shift-optimizer")]
[Authorize(Policy = "RequireManagerOrAbove")]
public class ShiftOptimizerController : ControllerBase
{
    private readonly TimeShiftDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly EmployeeDirectoryClient _employees;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<ShiftOptimizerController> _log;
    private static readonly string MlUrl =
        (Environment.GetEnvironmentVariable("ML_INFERENCE_URL") ?? "http://ml-inference:8000").TrimEnd('/') + "/shift/optimize";

    public ShiftOptimizerController(TimeShiftDbContext db, ITenantContext tenant, EmployeeDirectoryClient employees,
        IHttpClientFactory http, ILogger<ShiftOptimizerController> log)
    {
        _db = db; _tenant = tenant; _employees = employees; _http = http; _log = log;
    }

    private string Tenant => _tenant.TenantSlug ?? "";
    private bool IsHr => User.IsInRole("hr-admin") || User.IsInRole("tenant-admin") || User.IsInRole("platform-admin")
        || User.IsInRole("ext-timeshift-manage");

    public record DemandInput(Guid ShiftId, int Required, int[]? Weekdays, DateOnly? Date, string? Skill);
    public record ProposeInput(DateOnly From, DateOnly To, Guid[]? EmployeeIds, Guid? TeamId, Guid? DepartmentId,
        List<DemandInput> Demand, string? Solver);
    public record ProposalItem(Guid EmployeeId, DateOnly Date, Guid ShiftId);

    /// <summary>Mevcut ve önerilen atamaların farkı (saf; birim testli). Kind: add | change | remove | same.</summary>
    public sealed record DiffRow(Guid EmployeeId, DateOnly Date, Guid? CurrentShiftId, Guid? ProposedShiftId, Guid? CurrentAssignmentId, string Kind);

    public static List<DiffRow> Diff(IEnumerable<(Guid EmployeeId, DateOnly Date, Guid ShiftId, Guid AssignmentId)> current,
        IEnumerable<ProposalItem> proposed)
    {
        var cur = current.GroupBy(c => (c.EmployeeId, c.Date)).ToDictionary(g => g.Key, g => g.First());
        var pro = proposed.GroupBy(p => (p.EmployeeId, p.Date)).ToDictionary(g => g.Key, g => g.First());
        var rows = new List<DiffRow>();
        foreach (var key in cur.Keys.Union(pro.Keys))
        {
            var c = cur.TryGetValue(key, out var cv) ? cv : default;
            var hasC = cur.ContainsKey(key);
            var p = pro.TryGetValue(key, out var pv) ? pv : null;
            var kind = !hasC ? "add" : p is null ? "remove" : c.ShiftId == p.ShiftId ? "same" : "change";
            rows.Add(new DiffRow(key.EmployeeId, key.Date, hasC ? c.ShiftId : null, p?.ShiftId, hasC ? c.AssignmentId : null, kind));
        }
        return rows.OrderBy(r => r.Date).ThenBy(r => r.EmployeeId).ToList();
    }

    /// <summary>Talep satırlarını gün x vardiya talebine açar (saf; birim testli). Weekdays ISO (1 = Pazartesi).</summary>
    public static List<(DateOnly Date, Guid ShiftId, int Required, string? Skill)> ExpandDemand(DateOnly from, DateOnly to, IEnumerable<DemandInput> demand)
    {
        var list = new List<(DateOnly, Guid, int, string?)>();
        foreach (var d in demand)
            for (var day = from; day <= to; day = day.AddDays(1))
            {
                if (d.Date is { } only && only != day) continue;
                var iso = PreferenceRules.IsoDay(day);
                if (d.Weekdays is { Length: > 0 } wd && !wd.Contains(iso)) continue;
                list.Add((day, d.ShiftId, d.Required, string.IsNullOrWhiteSpace(d.Skill) ? null : d.Skill.Trim()));
            }
        return list;
    }

    /// <summary>Planlanacak kişiler: yönetici yalnızca başı olduğu departmanların çalışanlarını planlar.</summary>
    private async Task<(IActionResult? Error, List<TsOps.PersonRow> People)> PeopleAsync(ProposeInput b, CancellationToken ct)
    {
        var all = (await TsOps.PeopleAsync(_db, Tenant, ct)).Where(p => p.Status != "Terminated").ToList();
        IEnumerable<TsOps.PersonRow> pick;
        if (b.EmployeeIds is { Length: > 0 } ids) pick = all.Where(p => ids.Contains(p.Id));
        else if (b.TeamId is { } team)
        {
            var members = await _db.ShiftTeamMembers.AsNoTracking()
                .Where(m => m.ShiftTeamId == team && m.EffectiveFrom <= b.To && (m.EffectiveTo == null || m.EffectiveTo >= b.From))
                .Select(m => m.EmployeeId).ToListAsync(ct);
            pick = all.Where(p => members.Contains(p.Id));
        }
        else if (b.DepartmentId is { } dep) pick = all.Where(p => p.DepartmentId == dep);
        else return (BadRequest(new { message = "Çalışan, ekip ya da departman seçin" }), new());
        var people = pick.ToList();
        if (!IsHr)
        {
            var me = await _employees.FindMyEmployeeIdAsync(ct);
            if (me is null || people.Any(p => p.HeadId != me && p.Id != me))
                return (StatusCode(403, new { message = "Yalnızca başı olduğunuz bölümün çalışanlarını planlayabilirsiniz" }), new());
        }
        if (people.Count == 0) return (BadRequest(new { message = "Planlanacak çalışan bulunamadı" }), new());
        if (people.Count > 200) return (BadRequest(new { message = "Tek seferde en çok 200 çalışan planlanabilir" }), new());
        return (null, people);
    }

    [HttpPost("propose")]
    public async Task<IActionResult> Propose([FromBody] ProposeInput b, CancellationToken ct)
    {
        var today = ClockCore.WorkDate(DateTimeOffset.UtcNow);
        if (b.To < b.From || b.To.DayNumber - b.From.DayNumber > 30) return BadRequest(new { message = "Dönem en çok 31 gün olabilir" });
        if (b.From < today) return BadRequest(new { message = "Öneri bugünden itibaren yapılır" });
        if (b.Demand is not { Count: > 0 } || b.Demand.Count > 100) return BadRequest(new { message = "En az bir talep satırı gerekli (en çok 100)" });
        if (b.Demand.Any(d => d.Required is < 0 or > 500 || d.Weekdays?.Any(w => w is < 1 or > 7) == true || d.Skill is { Length: > 100 }))
            return BadRequest(new { message = "Talep satırı geçersiz" });
        var (err, people) = await PeopleAsync(b, ct);
        if (err is not null) return err;

        var shiftIds = b.Demand.Select(d => d.ShiftId).Distinct().ToList();
        var demandShifts = await _db.Shifts.AsNoTracking().Where(s => shiftIds.Contains(s.Id)).ToListAsync(ct);
        if (demandShifts.Count != shiftIds.Count) return BadRequest(new { message = "Talepteki vardiya tanımı bulunamadı" });

        var ids = people.Select(p => p.Id).ToList();
        var histFrom = b.From.AddDays(-7);
        var assignments = await _db.ShiftAssignments.AsNoTracking().Include(a => a.Shift)
            .Where(a => ids.Contains(a.EmployeeId) && a.Date >= histFrom && a.Date <= b.To).ToListAsync(ct);
        // Dönem öncesi atamalardaki (talepte olmayan) vardiya tanımları da çözücüye verilir: dinlenme ve
        // haftalık süre hesabı için gerekir; talepleri olmadığından bu tanımlara yeni atama önerilmez.
        var shifts = demandShifts.Concat(assignments.Where(a => a.Date < b.From && a.Shift is not null).Select(a => a.Shift!))
            .DistinctBy(s => s.Id).Take(20).ToList();
        // Müsait değil: izin ve resmî tatil (kısmi gün izni hariç). Neden (izin türü) gönderilmez.
        var off = await _db.ShiftOverrides.AsNoTracking()
            .Where(o => ids.Contains(o.EmployeeId) && o.Date >= b.From && o.Date <= b.To && !o.IsPartial
                && (o.Type == ShiftOverrideType.Leave || o.Type == ShiftOverrideType.Holiday))
            .Select(o => new { o.EmployeeId, o.Date }).ToListAsync(ct);
        var prefs = await _db.ShiftPreferences.AsNoTracking().Where(p => ids.Contains(p.EmployeeId)).ToListAsync(ct);
        var tags = await _db.ShiftTeamMembers.AsNoTracking()
            .Where(m => ids.Contains(m.EmployeeId) && m.Tag != null && m.EffectiveFrom <= b.To && (m.EffectiveTo == null || m.EffectiveTo >= b.From))
            .Select(m => new { m.EmployeeId, m.Tag }).ToListAsync(ct);
        var rules = await WorkRuleCheck.RulesAsync(_db, ct);

        var pseudo = ids.ToDictionary(id => TimesheetAnomalyController.Pseudonym(Tenant, id));
        var byId = pseudo.ToDictionary(kv => kv.Value, kv => kv.Key);
        var shiftKey = shifts.ToDictionary(s => s.Id, s => s.Id.ToString("N")[..12]);
        var shiftBack = shiftKey.ToDictionary(kv => kv.Value, kv => kv.Key);
        var days = Enumerable.Range(0, b.To.DayNumber - b.From.DayNumber + 1).Select(i => b.From.AddDays(i)).ToList();
        var demand = ExpandDemand(b.From, b.To, b.Demand);

        var body = new
        {
            days = days.Select(d => d.ToString("yyyy-MM-dd")),
            shifts = shifts.Select(s => new { id = shiftKey[s.Id], start_min = s.StartTime.Hour * 60 + s.StartTime.Minute,
                end_min = s.EndTime.Hour * 60 + s.EndTime.Minute, break_min = Math.Clamp(s.BreakMinutes, 0, 240) }),
            employees = ids.Select(id =>
            {
                var p = prefs.FirstOrDefault(x => x.EmployeeId == id);
                return new
                {
                    id = byId[id],
                    unavailable = off.Where(o => o.EmployeeId == id).Select(o => o.Date.ToString("yyyy-MM-dd")).Distinct(),
                    preferred_days = p?.PreferredDays ?? Array.Empty<int>(),
                    unavailable_weekdays = p?.UnavailableDays ?? Array.Empty<int>(),
                    preferred_types = p?.PreferredShiftTypes ?? Array.Empty<string>(),
                    avoid_types = p?.AvoidShiftTypes ?? Array.Empty<string>(),
                    max_nights_per_week = p?.MaxNightsPerWeek,
                    skills = tags.Where(t => t.EmployeeId == id).Select(t => t.Tag!).Distinct().Take(20),
                    // Dönem öncesi son 7 günün atamaları (dinlenme / ardışık gün / hafta sınırı için).
                    history = assignments.Where(a => a.EmployeeId == id && a.Date < b.From && shiftKey.ContainsKey(a.ShiftId))
                        .Select(a => new { date = a.Date.ToString("yyyy-MM-dd"), shift = shiftKey[a.ShiftId] }),
                };
            }),
            demand = demand.Where(d => d.Required > 0).Select(d => new { date = d.Date.ToString("yyyy-MM-dd"), shift = shiftKey[d.ShiftId], required = d.Required, skill = d.Skill }),
            rules = new { min_rest_hours = rules.MinRestHours, weekly_max_hours = rules.WeeklyMaxHours, daily_max_hours = rules.DailyMaxHours,
                night_max_hours = rules.NightMaxHours, max_consecutive_days = rules.MaxConsecutiveDays },
            time_limit_seconds = 10,
            solver = b.Solver is "greedy" or "cp-sat" ? b.Solver : "auto",
        };
        JsonDocument doc;
        try
        {
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            using var req = new HttpRequestMessage(HttpMethod.Post, MlUrl) { Content = JsonContent.Create(body) };
            req.Headers.TryAddWithoutValidation("Authorization", Request.Headers.Authorization.ToString());
            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("Vardiya önerisi alınamadı: ML {Status}", (int)resp.StatusCode);
                return StatusCode(503, new { message = "Model servisine ulaşılamadı" });
            }
            doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _log.LogWarning("Vardiya önerisi alınamadı: {Error}", ex.Message);
            return StatusCode(503, new { message = "Model servisine ulaşılamadı" });
        }

        using (doc)
        {
            var root = doc.RootElement;
            var proposed = new List<ProposalItem>();
            foreach (var a in root.GetProperty("assignments").EnumerateArray())
            {
                if (!pseudo.TryGetValue(a.GetProperty("employee").GetString() ?? "", out var emp)) continue;
                if (!shiftBack.TryGetValue(a.GetProperty("shift").GetString() ?? "", out var sid)) continue;
                proposed.Add(new ProposalItem(emp, DateOnly.Parse(a.GetProperty("date").GetString()!), sid));
            }
            var current = assignments.Where(a => a.Date >= b.From).Select(a => (a.EmployeeId, a.Date, a.ShiftId, a.Id));
            var diff = Diff(current, proposed);
            var names = people.ToDictionary(p => p.Id, p => p.FullName);
            var allShiftNames = await _db.Shifts.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name, ct);
            return Ok(new
            {
                solver = root.GetProperty("solver").GetString(),
                status = root.GetProperty("status").GetString(),
                seconds = root.GetProperty("seconds").GetDouble(),
                from = b.From, to = b.To,
                employees = people.Select(p => new { p.Id, name = p.FullName }),
                shifts = demandShifts.Select(s => new { s.Id, s.Name, s.StartTime, s.EndTime }),
                diff = diff.Select(r => new
                {
                    r.EmployeeId, name = names.GetValueOrDefault(r.EmployeeId), r.Date, r.Kind, r.CurrentAssignmentId,
                    r.CurrentShiftId, currentShift = r.CurrentShiftId is { } c ? allShiftNames.GetValueOrDefault(c) : null,
                    r.ProposedShiftId, proposedShift = r.ProposedShiftId is { } p ? allShiftNames.GetValueOrDefault(p) : null,
                }),
                uncovered = root.GetProperty("uncovered").EnumerateArray().Select(u => new
                {
                    date = u.GetProperty("date").GetString(),
                    shiftId = shiftBack.GetValueOrDefault(u.GetProperty("shift").GetString() ?? ""),
                    skill = u.GetProperty("skill").ValueKind == JsonValueKind.String ? u.GetProperty("skill").GetString() : null,
                    missing = u.GetProperty("missing").GetInt32(),
                }).ToList(),
                unusableShifts = root.GetProperty("unusable_shifts").EnumerateArray().Select(u => new
                {
                    shiftId = shiftBack.GetValueOrDefault(u.GetProperty("shift").GetString() ?? ""),
                    reason = u.GetProperty("reason").GetString(),
                }).ToList(),
                fairness = new
                {
                    nightSpread = root.GetProperty("fairness").GetProperty("night_spread").GetInt32(),
                    weekendSpread = root.GetProperty("fairness").GetProperty("weekend_spread").GetInt32(),
                    loadSpread = root.GetProperty("fairness").GetProperty("load_spread").GetInt32(),
                    preferenceConflicts = root.GetProperty("fairness").GetProperty("preference_conflicts").GetInt32(),
                },
            });
        }
    }

    public record ApplyInput(List<ProposalItem>? Upserts, List<Guid>? RemoveAssignmentIds);

    /// <summary>
    /// Önerinin (ya da planlayıcının seçtiği kısmının) uygulanması: çalışan + gün başına atama oluşturulur
    /// ya da güncellenir; seçilen atamalar kaldırılır. Her satırın kural uyarıları yanıtta döner (engellemez).
    /// </summary>
    [HttpPost("apply")]
    public async Task<IActionResult> Apply([FromBody] ApplyInput b, CancellationToken ct)
    {
        var ups = b.Upserts ?? new();
        var rem = b.RemoveAssignmentIds ?? new();
        if (ups.Count + rem.Count == 0) return BadRequest(new { message = "Uygulanacak satır yok" });
        if (ups.Count + rem.Count > 2000) return BadRequest(new { message = "Tek seferde en çok 2000 satır" });
        var today = ClockCore.WorkDate(DateTimeOffset.UtcNow);
        if (ups.Any(u => u.Date < today)) return BadRequest(new { message = "Geçmiş güne atama yapılamaz" });

        var empIds = ups.Select(u => u.EmployeeId).ToList();
        var toRemove = await _db.ShiftAssignments.Where(a => rem.Contains(a.Id)).ToListAsync(ct);
        empIds.AddRange(toRemove.Select(a => a.EmployeeId));
        if (!IsHr)
        {
            var me = await _employees.FindMyEmployeeIdAsync(ct);
            var people = (await TsOps.PeopleAsync(_db, Tenant, ct)).ToDictionary(p => p.Id);
            if (me is null || empIds.Distinct().Any(id => !people.TryGetValue(id, out var p) || (p.HeadId != me && p.Id != me)))
                return StatusCode(403, new { message = "Yalnızca başı olduğunuz bölümün çalışanlarını planlayabilirsiniz" });
        }
        if (toRemove.Any(a => a.Date < today)) return BadRequest(new { message = "Geçmiş atama kaldırılamaz" });
        var shiftIds = ups.Select(u => u.ShiftId).Distinct().ToList();
        var shifts = await _db.Shifts.AsNoTracking().Where(s => shiftIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        if (shifts.Count != shiftIds.Count) return BadRequest(new { message = "Vardiya tanımı bulunamadı" });

        _db.ShiftAssignments.RemoveRange(toRemove);
        var keys = ups.Select(u => u.EmployeeId).Distinct().ToList();
        var dates = ups.Select(u => u.Date).Distinct().ToList();
        var existing = await _db.ShiftAssignments.Where(a => keys.Contains(a.EmployeeId) && dates.Contains(a.Date)).ToListAsync(ct);
        var created = 0;
        var updated = 0;
        foreach (var u in ups.DistinctBy(u => (u.EmployeeId, u.Date)))
        {
            var e = existing.FirstOrDefault(a => a.EmployeeId == u.EmployeeId && a.Date == u.Date);
            if (e is null) { _db.ShiftAssignments.Add(new ShiftAssignment { EmployeeId = u.EmployeeId, Date = u.Date, ShiftId = u.ShiftId }); created++; }
            else if (e.ShiftId != u.ShiftId) { e.ShiftId = u.ShiftId; updated++; }
        }
        await _db.SaveChangesAsync(ct);

        // Uygulama sonrası kural uyarıları (bilgi): planlayıcı öneriyi değiştirerek uyguladıysa görünür.
        var rules = await WorkRuleCheck.RulesAsync(_db, ct);
        var warnings = new List<object>();
        foreach (var u in ups.DistinctBy(u => (u.EmployeeId, u.Date)).Take(500))
        {
            var s = shifts[u.ShiftId];
            foreach (var w in await WorkRuleCheck.ForAssignmentAsync(_db, u.EmployeeId, u.Date, s.StartTime, s.EndTime, s.BreakMinutes, rules, ct))
                warnings.Add(new { u.EmployeeId, u.Date, w.Code, w.Message });
        }
        return Ok(new { created, updated, removed = toRemove.Count, warnings });
    }
}

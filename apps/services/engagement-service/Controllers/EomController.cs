using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using EngagementService.Data;
using EngagementService.Infrastructure;
using EngagementService.Infrastructure.Eom;
using EngagementService.Models;

namespace EngagementService.Controllers;

/* ======================================================================
 * "Ayın çalışanı" oylaması (isteğe bağlı modül; kiracı ayarıyla açılır).
 * Çalışanlar kendi departman biriminde aday gösterir / oy verir; oylar
 * kişinin rolüne göre katsayılı sayılır. Görünürlük kuralı dönem açılırken
 * dondurulur ve oy verirken kişiye gösterilir. Gizli kiplerde oy satırı
 * kişi kimliği taşımaz (bkz. scripts/sql/2026-10-28_employee_of_month.sql).
 * ==================================================================== */
[Route("api/eom")]
[Authorize]
public class EomController : AppController
{
    private readonly EngagementDbContext _db;
    private readonly EomStore _store;
    public EomController(EngagementDbContext db, EomStore store) { _db = db; _store = store; }

    public sealed record PersonDto(Guid Id, string Name, string? Position, string? Department, int? PhotoVersion);

    private async Task<Dictionary<Guid, PersonDto>> PeopleMapAsync(CancellationToken ct)
    {
        var photos = (await Db.QueryAsync("SELECT \"Id\", \"PhotoVersion\" FROM employee_employees WHERE \"TenantSlug\" = $1 AND \"PhotoVersion\" IS NOT NULL",
            r => (r.GetGuid(0), r.GetInt32(1)), ct, Tenant)).ToDictionary(x => x.Item1, x => x.Item2);
        return (await People.ListAsync(Tenant, ct, includeTerminated: true))
            .ToDictionary(p => p.Id, p => new PersonDto(p.Id, p.Name, p.Position, p.Department, photos.TryGetValue(p.Id, out var v) ? v : null));
    }

    private static object PeriodDto(EomPeriodRow p) => new
    {
        p.Id, key = p.Key, status = p.Status, p.NominationStartsAt, p.VotingStartsAt, p.Stage1EndsAt, p.Stage2EndsAt, p.AnnouncedAt,
        scope = p.Config.Scope, process = p.Config.Process, visibility = p.Config.Visibility, revealTo = p.Config.RevealTo,
        allowComment = p.Config.AllowComment, finalistCount = p.Config.FinalistCount,
    };

    private async Task<bool> IsManagerAsync(Person me, CancellationToken ct)
        => Me.IsManager || await _store.IsStructuralManagerAsync(Tenant, me.Id, ct);

    // ------------------------------------------------------------------ çalışan

    /// <summary>Oturumdaki kişinin oylama durumu: sürmekte olan dönem, üyesi olduğu birimler, adaylar, kendi seçimi.</summary>
    [HttpGet("me")]
    public async Task<IActionResult> MyStatus(CancellationToken ct)
    {
        var (enabled, cfg) = await _store.SettingsAsync(Tenant, ct);
        var period = await _store.ActiveAsync(Tenant, ct);
        if (!enabled && period is null) return Ok(new { enabled = false, isHr = Me.IsHr });
        var me = await MyPersonAsync(ct);
        object? next = null;
        if (period is null && enabled)
        {
            var s = EomLogic.NextSchedule(cfg, DateTime.UtcNow);
            next = new { key = s.PeriodKey, startsAt = s.FirstStart };
        }
        var myWeight = me is null ? (decimal?)null : EomLogic.Weight(period?.Config ?? cfg, await IsManagerAsync(me, ct), Me.IsHr);
        if (period is null || period.Status == Eom.Scheduled || me is null)
            return Ok(new { enabled, isHr = Me.IsHr, period = period is null ? null : PeriodDto(period), next, units = Array.Empty<object>(), myWeight });

        var people = await PeopleMapAsync(ct);
        var units = await _store.UnitsAsync(Tenant, period.Id, ct);
        var mine = new List<object>();
        foreach (var u in units.Where(u => u.Members.Contains(me.Id)))
        {
            string? action = null;
            if (u.Status == Eom.UnitOpen && u.Candidates.Any(c => c != me.Id))
                action = period.Status switch
                {
                    Eom.Nominating when u.Stage == 1 => "nominate",
                    Eom.Voting when u.Stage == 1 => "vote",
                    Eom.Stage2 when u.Stage == 2 => "vote",
                    _ => null,
                };
            var (choice, comment) = action is null && u.Status != Eom.UnitOpen
                ? (null, null)
                : await _store.MyChoiceAsync(Tenant, period, u, me.Id, nomination: period.Status == Eom.Nominating && u.Stage == 1, ct);
            mine.Add(new
            {
                u.Id, u.Stage, u.Name, u.DepartmentId, direct = u.Direct(units),
                isFinal = EomStore.FinalUnits(units, period.Config).Any(f => f.Id == u.Id),
                status = u.Status, action, memberCount = u.Members.Length,
                candidates = u.Candidates.Where(c => c != me.Id).Select(c => people.GetValueOrDefault(c)).Where(c => c is not null)
                    .OrderBy(c => c!.Name, StringComparer.Create(new System.Globalization.CultureInfo("tr-TR"), true)),
                myChoice = choice, myComment = comment,
            });
        }
        return Ok(new { enabled, isHr = Me.IsHr, period = PeriodDto(period), next, units = mine, myWeight });
    }

    public sealed record ChoiceInput(Guid UnitId, Guid EmployeeId, string? Comment);

    [HttpPost("vote")]
    public async Task<IActionResult> Vote(ChoiceInput body, CancellationToken ct)
    {
        var me = await MyPersonAsync(ct);
        if (me is null) return BadRequest(new { message = "Oy vermek için çalışan kaydınız olmalı" });
        var p = await _store.ActiveAsync(Tenant, ct);
        var weight = EomLogic.Weight(p?.Config ?? EomConfig.Default, await IsManagerAsync(me, ct), Me.IsHr);
        var err = await _store.VoteAsync(Tenant, body.UnitId, me.Id, body.EmployeeId, body.Comment, weight, ct);
        return err is null ? Ok(new { ok = true }) : BadRequest(new { message = err });
    }

    [HttpPost("nominate")]
    public async Task<IActionResult> Nominate(ChoiceInput body, CancellationToken ct)
    {
        var me = await MyPersonAsync(ct);
        if (me is null) return BadRequest(new { message = "Aday göstermek için çalışan kaydınız olmalı" });
        var err = await _store.NominateAsync(Tenant, body.UnitId, me.Id, body.EmployeeId, ct);
        return err is null ? Ok(new { ok = true }) : BadRequest(new { message = err });
    }

    /// <summary>
    /// Ana sayfa kartı: sonucu açıklanmış en yeni dönemin kazananları (bir sonraki açıklamaya kadar).
    /// <c>isMine</c>: kişinin üyesi olduğu birim. Modül kapalıysa boş.
    /// </summary>
    [HttpGet("winners")]
    public async Task<IActionResult> Winners(CancellationToken ct)
    {
        var (enabled, _) = await _store.SettingsAsync(Tenant, ct);
        if (!enabled) return Ok(new { enabled = false });
        var p = await _store.LatestAnnouncedAsync(Tenant, ct);
        if (p is null) return Ok(new { enabled = true, period = (object?)null, units = Array.Empty<object>() });
        var me = await MyPersonAsync(ct);
        var people = await PeopleMapAsync(ct);
        var units = await _store.UnitsAsync(Tenant, p.Id, ct);
        var finals = EomStore.FinalUnits(units, p.Config).Select(u => u.Id).ToHashSet();
        return Ok(new
        {
            enabled = true, period = PeriodDto(p),
            units = units.Where(u => u.Winner is not null && (finals.Contains(u.Id) || p.Config.TwoStage)).Select(u => new
            {
                u.Id, u.Stage, u.Name, u.DepartmentId, direct = u.Direct(units), isFinal = finals.Contains(u.Id),
                isMine = me is not null && u.Members.Contains(me.Id), winner = people.GetValueOrDefault(u.Winner!.Value),
            }),
        });
    }

    /// <summary>Geçmiş kazananlar (sonucu açıklanmış dönemler, sonuç birimleri).</summary>
    [HttpGet("history")]
    public async Task<IActionResult> History([FromQuery] int limit = 12, CancellationToken ct = default)
    {
        var people = await PeopleMapAsync(ct);
        var list = new List<object>();
        foreach (var p in (await _store.PeriodsAsync(Tenant, ct, 60)).Where(p => p.Status == Eom.Closed && p.AnnouncedAt is not null).Take(Math.Clamp(limit, 1, 36)))
        {
            var units = await _store.UnitsAsync(Tenant, p.Id, ct);
            list.Add(new
            {
                period = PeriodDto(p),
                winners = EomStore.FinalUnits(units, p.Config).Where(u => u.Winner is not null)
                    .Select(u => new { unitId = u.Id, u.Name, winner = people.GetValueOrDefault(u.Winner!.Value) }),
            });
        }
        return Ok(list);
    }

    public sealed record LeaderRow(PersonDto Person, int Rank, int Wins, int SubWins, string LastWonKey);

    /// <summary>Puan sırası: kazanma sayısı, sonra son kazanma tarihi; eşit sayıda kazananlar aynı sırayı paylaşır.</summary>
    public static List<LeaderRow> Rank(IEnumerable<(PersonDto Person, int Wins, int SubWins, string LastKey, DateTime LastAt)> rows)
    {
        var ordered = rows.OrderByDescending(r => r.Wins).ThenByDescending(r => r.SubWins).ThenByDescending(r => r.LastAt)
            .ThenBy(r => r.Person.Name, StringComparer.Create(new System.Globalization.CultureInfo("tr-TR"), true)).ToList();
        var result = new List<LeaderRow>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var r = ordered[i];
            var rank = i > 0 && ordered[i - 1].Wins == r.Wins && ordered[i - 1].SubWins == r.SubWins ? result[i - 1].Rank : i + 1;
            result.Add(new LeaderRow(r.Person, rank, r.Wins, r.SubWins, r.LastKey));
        }
        return result;
    }

    /// <summary>
    /// Kazananlar sıralaması (sonucu açıklanmış dönemler). Şirket geneli: ayın çalışanı seçilme sayısı (sonuç
    /// birimi zaferleri; iki aşamalıda alt birim zaferleri ayrı sütun). Departman: o departmanın oylamalarında
    /// (hangi aşama olursa olsun) kazanma sayısı. <c>isMine</c>: kişinin oylamalarına katıldığı departman.
    /// </summary>
    [HttpGet("leaderboard")]
    public async Task<IActionResult> Leaderboard(CancellationToken ct)
    {
        var (enabled, _) = await _store.SettingsAsync(Tenant, ct);
        if (!enabled) return Ok(new { enabled = false });
        var wins = await Db.QueryAsync("""
            SELECT u."DepartmentId", u."Name", u."Stage", u."ParentUnitId" IS NOT NULL, u."WinnerEmployeeId", p."PeriodKey", p."AnnouncedAt",
                   p."Config"->>'scope' = 'TwoStage', u."MemberIds"
            FROM engagement_eom_units u JOIN engagement_eom_periods p ON p."Id" = u."PeriodId"
            WHERE u."TenantSlug" = $1 AND p."Status" = 'Closed' AND p."AnnouncedAt" IS NOT NULL AND u."WinnerEmployeeId" IS NOT NULL
            """, r => (Dept: r.GetGuid(0), Name: r.GetString(1), Stage: r.GetInt32(2), HasParent: r.GetBoolean(3), Winner: r.GetGuid(4),
                Key: r.GetString(5), At: r.GetFieldValue<DateTime>(6), TwoStage: !r.IsDBNull(7) && r.GetBoolean(7), Members: r.GetFieldValue<Guid[]>(8)), ct, Tenant);
        var people = await PeopleMapAsync(ct);
        var me = await MyPersonAsync(ct);
        static bool Final(bool twoStage, int stage, bool hasParent) => !twoStage || stage == 2 || !hasParent;

        var company = Rank(wins.GroupBy(w => w.Winner).Where(g => people.ContainsKey(g.Key)).Select(g =>
        {
            var last = g.OrderByDescending(w => w.At).First();
            return (people[g.Key], g.Count(w => Final(w.TwoStage, w.Stage, w.HasParent)), g.Count(w => !Final(w.TwoStage, w.Stage, w.HasParent)), last.Key, last.At);
        }));
        var departments = wins.GroupBy(w => w.Dept).Select(d =>
        {
            var latest = d.OrderByDescending(w => w.At).First();
            var rows = Rank(d.GroupBy(w => w.Winner).Where(g => people.ContainsKey(g.Key)).Select(g =>
            {
                var last = g.OrderByDescending(w => w.At).First();
                return (people[g.Key], g.Count(), 0, last.Key, last.At);
            }));
            return new
            {
                departmentId = d.Key, name = latest.Name, rows,
                isMine = me is not null && latest.Members.Contains(me.Id),
                periods = d.Select(w => w.Key).Distinct().Count(),
            };
        }).OrderByDescending(d => d.isMine).ThenBy(d => d.name, StringComparer.Create(new System.Globalization.CultureInfo("tr-TR"), true)).ToList();
        return Ok(new { enabled = true, periods = wins.Select(w => w.Key).Distinct().Count(), company, departments });
    }

    /// <summary>
    /// Dönem sonuçları, dondurulmuş görünürlük kuralına göre:
    ///  * kazanan: birim üyeleri ve İK (sonuç açıklanınca);
    ///  * puan sıralaması: İK (birim kapanınca); RankingPublic'te birim üyeleri de (açıklanınca);
    ///  * kim kime oy verdi: HrSeesVotes'ta İK (birim kapanınca); RevealAfter'da açıklanınca birim üyeleri
    ///    ya da İK ve departman başı (RevealTo);
    ///  * gerekçeler: İK (gizli kipte kimliksiz) ve kazanan (kendisine yazılanlar);
    ///  * katılım: İK, dönem sürerken de.
    /// </summary>
    [HttpGet("periods/{id:guid}/results")]
    public async Task<IActionResult> Results(Guid id, CancellationToken ct)
    {
        var p = await _store.PeriodAsync(Tenant, id, ct);
        if (p is null) return NotFound();
        var me = await MyPersonAsync(ct);
        var hr = Me.IsHr;
        var announced = p.AnnouncedAt is not null;
        if (!hr && !announced) return StatusCode(403, new { message = "Sonuçlar henüz açıklanmadı" });
        var people = await PeopleMapAsync(ct);
        var units = await _store.UnitsAsync(Tenant, p.Id, ct);
        var votes = await _store.VotesAsync(Tenant, p.Id, ct);
        var nominations = hr ? await _store.NominationsAsync(Tenant, p.Id, ct) : [];
        var cfg = p.Config;
        var finals = EomStore.FinalUnits(units, cfg).Select(u => u.Id).ToHashSet();
        var result = new List<object>();
        foreach (var u in units)
        {
            var member = me is not null && u.Members.Contains(me.Id);
            if (!hr && !member) continue;
            var closed = u.Status is Eom.UnitClosed or Eom.UnitTie or Eom.UnitNoVotes;
            var uv = votes.Where(v => v.UnitId == u.Id).ToList();
            var tally = EomLogic.Tally(uv.Where(v => u.Candidates.Contains(v.Nominee)).Select(v => new EomBallot(v.Nominee, v.Weight)));
            var showRanking = (hr && closed) || (announced && member && cfg.Visibility == Eom.RankingPublic);
            var showVotes = cfg.Visibility switch
            {
                Eom.HrSeesVotes => hr && closed,
                Eom.RevealAfter => announced && (cfg.RevealTo == Eom.RevealDepartment ? member || hr : hr || (me is not null && u.HeadId == me.Id)),
                _ => false,
            };
            var isWinner = me is not null && u.Winner == me.Id;
            result.Add(new
            {
                u.Id, u.Stage, u.Name, u.DepartmentId, direct = u.Direct(units), isFinal = finals.Contains(u.Id), status = u.Status,
                winner = announced || hr ? (u.Winner is { } w ? people.GetValueOrDefault(w) : null) : null,
                tied = hr ? u.Tied.Select(t => people.GetValueOrDefault(t)) : null,
                participation = hr ? new { voted = uv.Count, members = u.Members.Length, nominated = nominations.Count(n => n.UnitId == u.Id) } : null,
                ranking = showRanking ? tally.Ranking.Select(s => new { person = people.GetValueOrDefault(s.EmployeeId), score = s.Score, votes = s.Votes }) : null,
                votes = showVotes ? uv.Where(v => v.VoterEmployeeId is not null).Select(v => new
                {
                    voter = people.GetValueOrDefault(v.VoterEmployeeId!.Value), nominee = people.GetValueOrDefault(v.Nominee), weight = v.Weight, comment = v.Comment,
                }) : null,
                comments = (hr && closed) || (isWinner && announced)
                    ? uv.Where(v => v.Comment is not null && (hr || v.Nominee == me!.Id)).Select(v => new
                    {
                        nominee = hr ? people.GetValueOrDefault(v.Nominee) : null, text = v.Comment,
                    })
                    : null,
            });
        }
        return Ok(new { period = PeriodDto(p), units = result });
    }

    // ------------------------------------------------------------------ İK

    public sealed record SettingsInput(bool Enabled, string Cadence, string Scope, string Process, string Visibility, string RevealTo,
        int StartDay, int OpenHour, int NominationDays, int FinalistCount, int VotingDays, int Stage2Days, int ReminderDaysBefore,
        int CooldownPeriods, decimal WeightEmployee, decimal WeightManager, decimal WeightHr, bool AllowComment);

    [HttpGet("settings")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> GetSettings(CancellationToken ct)
    {
        var s = await _db.EomSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new EomSettings();
        var active = await _store.ActiveAsync(Tenant, ct);
        var next = EomLogic.NextSchedule(s.ToConfig(), DateTime.UtcNow);
        return Ok(new { settings = s, active = active is null ? null : PeriodDto(active), next = new { key = next.PeriodKey, startsAt = next.FirstStart, next.VotingStartsAt, endsAt = next.FinalEnd } });
    }

    /// <summary>Ayarlar kaydedilir; sürmekte olan dönemi etkilemez (o dönemin ayarları açılırken donduruldu).</summary>
    [HttpPut("settings")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> PutSettings(SettingsInput body, CancellationToken ct)
    {
        var cfg = new EomConfig(body.Cadence, body.Scope, body.Process, body.Visibility, body.RevealTo, body.StartDay, body.OpenHour,
            body.NominationDays, body.FinalistCount, body.VotingDays, body.Stage2Days, body.ReminderDaysBefore, body.CooldownPeriods,
            body.WeightEmployee, body.WeightManager, body.WeightHr, body.AllowComment);
        if (cfg.Validate() is { } err) return BadRequest(new { message = err });
        var s = await _db.EomSettings.FirstOrDefaultAsync(ct);
        if (s is null) { s = new EomSettings(); _db.EomSettings.Add(s); }
        s.Apply(cfg);
        s.Enabled = body.Enabled;
        s.UpdatedAt = DateTime.UtcNow;
        s.UpdatedByName = Me.Name;
        await _db.SaveChangesAsync(ct);
        // Takvimdeki henüz başlamamış dönem yeni ayarlarla yeniden kurulsun.
        await Db.ExecuteAsync("DELETE FROM engagement_eom_periods WHERE \"TenantSlug\" = $1 AND \"Status\" = 'Scheduled' AND \"PeriodKey\" NOT LIKE 'M-%'", ct, Tenant);
        await _store.TickAsync(Tenant, DateTime.UtcNow, ct);
        return Ok(s);
    }

    /// <summary>Dönemler (İK): durum ve katılım özeti.</summary>
    [HttpGet("periods")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Periods(CancellationToken ct)
    {
        var list = new List<object>();
        foreach (var p in await _store.PeriodsAsync(Tenant, ct))
        {
            var units = p.Status == Eom.Scheduled ? [] : await _store.UnitsAsync(Tenant, p.Id, ct);
            list.Add(new { period = PeriodDto(p), unitCount = units.Count, tieCount = units.Count(u => u.Status == Eom.UnitTie) });
        }
        return Ok(list);
    }

    /// <summary>Takvimi beklemeden bugünden başlayan özel dönem.</summary>
    [HttpPost("periods/start-now")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> StartNow(CancellationToken ct)
    {
        var (_, cfg) = await _store.SettingsAsync(Tenant, ct);
        var id = await _store.StartNowAsync(Tenant, cfg, DateTime.UtcNow, ct);
        return id is null ? Conflict(new { message = "Sürmekte olan bir oylama var; önce onu bitirin ya da iptal edin" }) : Ok(new { id });
    }

    [HttpPost("periods/{id:guid}/skip-phase")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> SkipPhase(Guid id, CancellationToken ct)
        => await _store.SkipPhaseAsync(Tenant, id, DateTime.UtcNow, ct) ? Ok(new { ok = true }) : BadRequest(new { message = "Bu dönemde bitirilecek aşama yok" });

    [HttpPost("periods/{id:guid}/cancel")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
        => await _store.CancelAsync(Tenant, id, ct) ? Ok(new { ok = true }) : BadRequest(new { message = "Dönem iptal edilemez (kapanmış ya da yok)" });

    public sealed record ResolveInput(Guid EmployeeId);

    [HttpPost("units/{id:guid}/resolve")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Resolve(Guid id, ResolveInput body, CancellationToken ct)
        => await _store.ResolveTieAsync(Tenant, id, body.EmployeeId, DateTime.UtcNow, ct) is { } err ? BadRequest(new { message = err }) : Ok(new { ok = true });
}

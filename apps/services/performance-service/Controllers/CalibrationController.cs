using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PerformanceService.Data;
using PerformanceService.Models;
using PerformanceService.Security;
using PerformanceService.Services;

namespace PerformanceService.Controllers;

/// <summary>
/// Dalga 11 / 79: kalibrasyon oturumu. İK bir dönem için oturum açar; o anki 9-kutu yerleşimi
/// (puanı ve potansiyeli olan çalışanlar) başlangıç olarak kopyalanır. Toplantıda kişiler sürükle-bırak
/// ile taşınır (her taşıma karar notu ister, değişiklik günlüğüne ve denetim kaydına yazılır), her satır
/// İK tarafından onaylanır; tüm satırlar onaylanınca oturum sonuçlanır ve değişen hücreler 9-kutu
/// düzeltmesi olarak kaydedilir. KVKK: otomatik karar yok; oturumu yalnızca İK görür; kişi kendi
/// satırını taşıyamaz/onaylayamaz.
/// </summary>
[ApiController]
[Route("api/calibration")]
[Authorize]
public class CalibrationController : ControllerBase
{
    private readonly PerformanceDbContext _db;
    private readonly PerfPeople _people;
    private readonly NineBoxGrid _grid;

    public CalibrationController(PerformanceDbContext db, PerfPeople people, NineBoxGrid grid)
    {
        _db = db; _people = people; _grid = grid;
    }

    private IActionResult HrOnly() => StatusCode(403, new { message = "Kalibrasyon oturumu yalnızca İK'ya açık" });

    [HttpGet("sessions")]
    public async Task<IActionResult> List([FromQuery] Guid? cycleId, CancellationToken ct)
    {
        if (!User.IsHr()) return HrOnly();
        var q = _db.CalibrationSessions.AsNoTracking().AsQueryable();
        if (cycleId is { } c) q = q.Where(s => s.CycleId == c);
        var rows = await q.OrderByDescending(s => s.CreatedAt).Take(100).ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToList();
        var counts = await _db.CalibrationItems.AsNoTracking().Where(i => ids.Contains(i.SessionId))
            .GroupBy(i => i.SessionId).Select(g => new { g.Key, total = g.Count(), confirmed = g.Count(i => i.Confirmed) })
            .ToDictionaryAsync(x => x.Key, ct);
        return Ok(rows.Select(s => new
        {
            s.Id, s.CycleId, s.Name, status = s.Status.ToString(), s.CreatedBy, s.CreatedAt, s.FinalizedBy, s.FinalizedAt,
            total = counts.GetValueOrDefault(s.Id)?.total ?? 0, confirmed = counts.GetValueOrDefault(s.Id)?.confirmed ?? 0,
        }));
    }

    public record CreateInput(Guid CycleId, string? Name);

    [HttpPost("sessions")]
    public async Task<IActionResult> Create([FromBody] CreateInput body, CancellationToken ct)
    {
        if (!User.IsHr()) return HrOnly();
        var name = body.Name?.Trim() ?? "";
        if (name.Length is < 3 or > 200) return BadRequest(new { message = "Oturum adı 3-200 karakter olmalı" });
        var cycle = await _db.Cycles.AsNoTracking().FirstOrDefaultAsync(c => c.Id == body.CycleId, ct);
        if (cycle is null) return NotFound(new { message = "Dönem bulunamadı" });
        if (cycle.Status == CycleStatus.Planned) return Conflict(new { message = "Planlanan dönem için kalibrasyon yapılamaz" });
        if (await _db.CalibrationSessions.AnyAsync(s => s.CycleId == body.CycleId && s.Status == CalibrationStatus.Open, ct))
            return Conflict(new { message = "Bu dönem için açık bir kalibrasyon oturumu zaten var" });

        var me = await _people.MeAsync(ct);
        var people = (await _people.ActiveAsync(ct)).Where(p => p.Id != me?.Id).ToList();
        var (entries, _) = await _grid.BuildAsync(cycle, people, ct);
        var session = new CalibrationSession { CycleId = cycle.Id, Name = name, CreatedBy = PerfPeople.DisplayName(User) };
        _db.CalibrationSessions.Add(session);
        var placed = 0;
        foreach (var e in entries)
        {
            if (e.Bands is not { } b) continue;
            placed++;
            _db.CalibrationItems.Add(new CalibrationItem
            {
                SessionId = session.Id, EmployeeId = e.Person.Id, Score = e.Score,
                OriginalPerformanceBand = b.Performance, OriginalPotentialBand = b.Potential,
                PerformanceBand = b.Performance, PotentialBand = b.Potential,
            });
        }
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (e.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            return Conflict(new { message = "Bu dönem için açık bir kalibrasyon oturumu zaten var" });
        }
        catch (DbUpdateException e) when (e.InnerException is Npgsql.PostgresException { SqlState: "23503" })
        {
            // Dönem/oturum bu arada silindiyse (yarış) — kullanıcıya yeniden denemesi söylenir.
            return Conflict(new { message = "Oturum oluşturulamadı: dönem bu arada değişti. Lütfen yeniden deneyin." });
        }
        await _people.AuditAsync("CalibrationSession", session.Id.ToString(), "Created", new { cycleId = cycle.Id, name, employees = placed });
        return Ok(new { session.Id, placed, unplaced = entries.Count - placed });
    }

    [HttpGet("sessions/{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        if (!User.IsHr()) return HrOnly();
        var s = await _db.CalibrationSessions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return NotFound(new { message = "Kalibrasyon oturumu bulunamadı" });
        var cycle = await _db.Cycles.AsNoTracking().FirstOrDefaultAsync(c => c.Id == s.CycleId, ct);
        var items = await _db.CalibrationItems.AsNoTracking().Where(i => i.SessionId == id).ToListAsync(ct);
        var changes = await _db.CalibrationChanges.AsNoTracking().Where(c => c.SessionId == id)
            .OrderByDescending(c => c.ChangedAt).Take(500).ToListAsync(ct);
        var people = (await _people.ActiveAsync(ct)).ToDictionary(p => p.Id);
        string Name(Guid e) => people.TryGetValue(e, out var p) ? p.FullName : "—";
        var me = await _people.MeAsync(ct);
        await _people.AuditAsync("CalibrationSession", id.ToString(), "SensitiveViewed", new { field = "nineBox", employees = items.Count });
        return Ok(new
        {
            notice = NineBoxController.Notice,
            session = new { s.Id, s.CycleId, cycleName = cycle?.Name, s.Name, status = s.Status.ToString(), s.CreatedBy, s.CreatedAt, s.FinalizedBy, s.FinalizedAt },
            cells = Enumerable.Range(1, 9).Select(c => new { cell = c, label = NineBoxMath.Label(c), performanceBand = NineBoxMath.Bands(c).Performance, potentialBand = NineBoxMath.Bands(c).Potential }),
            items = items.OrderBy(i => Name(i.EmployeeId)).Select(i => new
            {
                i.EmployeeId, name = Name(i.EmployeeId),
                department = people.GetValueOrDefault(i.EmployeeId)?.DepartmentName,
                positionTitle = people.GetValueOrDefault(i.EmployeeId)?.PositionTitle,
                i.Score, i.PerformanceBand, i.PotentialBand,
                cell = NineBoxMath.Cell(i.PerformanceBand, i.PotentialBand),
                originalCell = NineBoxMath.Cell(i.OriginalPerformanceBand, i.OriginalPotentialBand),
                i.DecisionNote, i.Confirmed, i.ConfirmedBy, i.ConfirmedAt, i.UpdatedAt,
                isSelf = me?.Id == i.EmployeeId,
            }),
            changes = changes.Select(c => new { c.Id, c.EmployeeId, name = c.EmployeeId == Guid.Empty ? null : Name(c.EmployeeId), c.Action, c.FromCell, c.ToCell, c.Note, c.ChangedBy, c.ChangedAt }),
        });
    }

    private async Task<(CalibrationSession? S, IActionResult? Err)> OpenSessionAsync(Guid id, CancellationToken ct)
    {
        if (!User.IsHr()) return (null, HrOnly());
        var s = await _db.CalibrationSessions.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return (null, NotFound(new { message = "Kalibrasyon oturumu bulunamadı" }));
        if (s.Status != CalibrationStatus.Open) return (null, Conflict(new { message = "Oturum kapanmış; değişiklik yapılamaz" }));
        return (s, null);
    }

    public record MoveInput(Guid EmployeeId, int PerformanceBand, int PotentialBand, string? Note);

    /// <summary>Sürükle-bırak taşıma: karar notu zorunlu, satırın onayı düşer.</summary>
    [HttpPost("sessions/{id:guid}/move")]
    public async Task<IActionResult> Move(Guid id, [FromBody] MoveInput body, CancellationToken ct)
    {
        var (s, err) = await OpenSessionAsync(id, ct);
        if (err is not null) return err;
        var error = CalibrationRules.ValidateMove(body.PerformanceBand, body.PotentialBand, body.Note);
        if (error is not null) return BadRequest(new { message = error });
        var item = await _db.CalibrationItems.FirstOrDefaultAsync(i => i.SessionId == id && i.EmployeeId == body.EmployeeId, ct);
        if (item is null) return NotFound(new { message = "Çalışan bu oturumda yok" });
        var me = await _people.MeAsync(ct);
        if (me?.Id == item.EmployeeId) return StatusCode(403, new { message = "Kendi satırınızı taşıyamazsınız" });

        var from = NineBoxMath.Cell(item.PerformanceBand, item.PotentialBand);
        var to = NineBoxMath.Cell(body.PerformanceBand, body.PotentialBand);
        var note = body.Note!.Trim();
        var by = PerfPeople.DisplayName(User);
        item.PerformanceBand = body.PerformanceBand;
        item.PotentialBand = body.PotentialBand;
        item.DecisionNote = note;
        item.Confirmed = false;
        item.ConfirmedBy = null;
        item.ConfirmedAt = null;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        _db.CalibrationChanges.Add(new CalibrationChange { SessionId = id, EmployeeId = item.EmployeeId, Action = "Moved", FromCell = from, ToCell = to, Note = note, ChangedBy = by });
        await _db.SaveChangesAsync(ct);
        await _people.AuditAsync("CalibrationSession", id.ToString(), "CalibrationMoved",
            new { field = "nineBoxCell", employeeId = item.EmployeeId, from, to, note });
        return Ok(new { item.EmployeeId, cell = to, label = NineBoxMath.Label(to), item.DecisionNote, item.Confirmed });
    }

    public record ConfirmInput(List<Guid>? EmployeeIds, bool Confirmed = true);

    /// <summary>İK onayı (seçili satırlar). Kişi kendi satırını onaylayamaz.</summary>
    [HttpPost("sessions/{id:guid}/confirm")]
    public async Task<IActionResult> Confirm(Guid id, [FromBody] ConfirmInput body, CancellationToken ct)
    {
        var (_, err) = await OpenSessionAsync(id, ct);
        if (err is not null) return err;
        var ids = (body.EmployeeIds ?? new()).Distinct().ToList();
        if (ids.Count is 0 or > 1000) return BadRequest(new { message = "Onaylanacak çalışanları seçin" });
        var me = await _people.MeAsync(ct);
        if (me is not null && ids.Contains(me.Id)) return StatusCode(403, new { message = "Kendi satırınızı onaylayamazsınız" });
        var items = await _db.CalibrationItems.Where(i => i.SessionId == id && ids.Contains(i.EmployeeId)).ToListAsync(ct);
        if (items.Count != ids.Count) return NotFound(new { message = "Çalışan bu oturumda yok" });
        var by = PerfPeople.DisplayName(User);
        var changed = 0;
        foreach (var i in items.Where(i => i.Confirmed != body.Confirmed))
        {
            i.Confirmed = body.Confirmed;
            i.ConfirmedBy = body.Confirmed ? by : null;
            i.ConfirmedAt = body.Confirmed ? DateTimeOffset.UtcNow : null;
            i.UpdatedAt = DateTimeOffset.UtcNow;
            var cell = NineBoxMath.Cell(i.PerformanceBand, i.PotentialBand);
            _db.CalibrationChanges.Add(new CalibrationChange
            {
                SessionId = id, EmployeeId = i.EmployeeId, Action = body.Confirmed ? "Confirmed" : "Unconfirmed", FromCell = cell, ToCell = cell, ChangedBy = by,
            });
            changed++;
        }
        await _db.SaveChangesAsync(ct);
        if (changed > 0)
            await _people.AuditAsync("CalibrationSession", id.ToString(), body.Confirmed ? "CalibrationConfirmed" : "CalibrationUnconfirmed", new { employees = changed });
        return Ok(new { changed });
    }

    /// <summary>Tüm satırlar onaylıysa oturumu sonuçlandırır; değişen hücreleri 9-kutu düzeltmesi olarak yazar.</summary>
    [HttpPost("sessions/{id:guid}/finalize")]
    public async Task<IActionResult> Finalize(Guid id, CancellationToken ct)
    {
        var (s, err) = await OpenSessionAsync(id, ct);
        if (err is not null) return err;
        var items = await _db.CalibrationItems.Where(i => i.SessionId == id).ToListAsync(ct);
        var states = items.Select(i => new CalibrationRules.ItemState(i.EmployeeId,
            NineBoxMath.Cell(i.OriginalPerformanceBand, i.OriginalPotentialBand), NineBoxMath.Cell(i.PerformanceBand, i.PotentialBand), i.Confirmed)).ToList();
        var blocker = CalibrationRules.FinalizeBlocker(states);
        if (blocker is not null) return Conflict(new { message = blocker });

        var by = PerfPeople.DisplayName(User);
        var changed = CalibrationRules.Changed(states).Select(c => c.EmployeeId).ToHashSet();
        foreach (var i in items.Where(i => changed.Contains(i.EmployeeId)))
        {
            var reason = $"Kalibrasyon oturumu «{s!.Name}»: {i.DecisionNote}";
            _db.NineBoxOverrides.Add(new NineBoxOverride
            {
                CycleId = s.CycleId, EmployeeId = i.EmployeeId, PerformanceBand = i.PerformanceBand, PotentialBand = i.PotentialBand,
                Reason = reason.Length > 1000 ? reason[..1000] : reason, OverriddenBy = by,
            });
        }
        s!.Status = CalibrationStatus.Finalized;
        s.FinalizedBy = by;
        s.FinalizedAt = DateTimeOffset.UtcNow;
        _db.CalibrationChanges.Add(new CalibrationChange { SessionId = id, EmployeeId = Guid.Empty, Action = "Finalized", ChangedBy = by, Note = $"{changed.Count} değişiklik" });
        await _db.SaveChangesAsync(ct);
        await _people.AuditAsync("CalibrationSession", id.ToString(), "CalibrationFinalized",
            new { cycleId = s.CycleId, employees = items.Count, changed = changed.Count });
        return Ok(new { status = s.Status.ToString(), changed = changed.Count, employees = items.Count });
    }

    [HttpPost("sessions/{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var (s, err) = await OpenSessionAsync(id, ct);
        if (err is not null) return err;
        s!.Status = CalibrationStatus.Cancelled;
        await _db.SaveChangesAsync(ct);
        await _people.AuditAsync("CalibrationSession", id.ToString(), "CalibrationCancelled", new { cycleId = s.CycleId });
        return Ok(new { status = s.Status.ToString() });
    }
}

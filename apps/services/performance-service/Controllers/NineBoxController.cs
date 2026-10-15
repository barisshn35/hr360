using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PerformanceService.Data;
using PerformanceService.Models;
using PerformanceService.Security;
using PerformanceService.Services;

namespace PerformanceService.Controllers;

/// <summary>
/// G12 9-kutu (performans × potansiyel). Performans ekseni nihai değerlendirme puanından (kapalı
/// dönemde sabitlenmiş sonuç, açık dönemde güncel/geçici hesap), potansiyel ekseni yöneticinin
/// döneme özel 1-3 değerlendirmesinden. İK gerekçeyle hücreyi düzeltebilir (kalibrasyon, denetim
/// kaydına yazılır). İK herkesi, yönetici yalnızca başı olduğu departmanı görür; çalışan tabloyu
/// görmez, potansiyelini ise yalnızca İK yayımlarsa görür.
/// KVKK: tablo yalnızca bir tartışma aracıdır — otomatik karar üretmez.
/// </summary>
[ApiController]
[Route("api/nine-box")]
[Authorize]
public class NineBoxController : ControllerBase
{
    public const string Notice =
        "9-kutu tablosu yalnızca bir tartışma aracıdır; terfi, ücret ya da işten çıkarma gibi kararlar otomatik olarak bu tablodan üretilmez. Kalibrasyon toplantısında insan değerlendirmesiyle kullanın.";

    private readonly PerformanceDbContext _db;
    private readonly SnapshotService _snapshots;
    private readonly PerfPeople _people;
    private readonly NineBoxGrid _grid;

    public NineBoxController(PerformanceDbContext db, SnapshotService snapshots, PerfPeople people, NineBoxGrid grid)
    {
        _db = db; _snapshots = snapshots; _people = people; _grid = grid;
    }

    private bool IsHr => User.IsHr();

    /// <summary>Çağıranın görebileceği çalışanlar (İK: herkes; yönetici: başı olduğu departman, kendisi hariç).</summary>
    private async Task<(List<PerfPersonRow> people, PerfPersonRow? me)> ScopeAsync(CancellationToken ct)
    {
        var me = await _people.MeAsync(ct);
        var all = await _people.ActiveAsync(ct);
        if (IsHr) return (all, me);
        if (me is null) return (new(), null);
        return (all.Where(p => p.HeadId == me.Id && p.Id != me.Id).ToList(), me);
    }

    [HttpGet]
    public async Task<IActionResult> Grid([FromQuery] Guid cycleId, CancellationToken ct)
    {
        if (!User.IsManagerOrAbove()) return StatusCode(403, new { message = "9-kutu tablosu İK'ya ve yöneticilere açık" });
        var cycle = await _db.Cycles.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cycleId, ct);
        if (cycle is null) return NotFound(new { message = "Dönem bulunamadı" });
        var (people, _) = await ScopeAsync(ct);
        var (rows, config) = await _grid.BuildAsync(cycle, people, ct);
        var entries = rows.Select(e => new Dictionary<string, object?>
        {
            ["employeeId"] = e.Person.Id, ["name"] = e.Person.FullName, ["department"] = e.Person.DepartmentName, ["positionTitle"] = e.Person.PositionTitle,
            ["score"] = e.Score, ["isProvisional"] = e.IsProvisional,
            ["performanceBand"] = e.PerformanceBand, ["potential"] = e.Potential?.Rating, ["potentialNote"] = e.Potential?.Note,
            ["potentialRatedBy"] = e.Potential?.RatedByName, ["potentialPublished"] = e.Potential?.PublishedToEmployee ?? false,
            ["computedCell"] = e.ComputedCell, ["cell"] = e.Cell,
            ["override"] = e.Override is not { } ov ? null : new { ov.PerformanceBand, ov.PotentialBand, ov.Reason, ov.OverriddenBy, ov.CreatedAt },
        }).ToList();
        await _people.AuditAsync("NineBox", cycleId.ToString(), "SensitiveViewed", new { field = "nineBox", employees = entries.Count });
        return Ok(new
        {
            notice = Notice,
            cycle = new { cycle.Id, cycle.Name, status = cycle.Status.ToString() },
            thresholds = new { low = config.ImprovementThreshold, high = config.RecognitionThreshold },
            canCalibrate = IsHr,
            cells = Enumerable.Range(1, 9).Select(c => new
            {
                cell = c, label = NineBoxMath.Label(c),
                performanceBand = NineBoxMath.Bands(c).Performance, potentialBand = NineBoxMath.Bands(c).Potential,
                employees = entries.Where(e => (int?)e["cell"] == c),
            }),
            unplaced = entries.Where(e => e["cell"] is null),
        });
    }

    public record PotentialInput(Guid CycleId, Guid EmployeeId, int Rating, string? Note);

    /// <summary>Potansiyel (1-3): İK ya da çalışanın departman başkanı girer; kişi kendini değerlendiremez.</summary>
    [HttpPut("potential")]
    public async Task<IActionResult> SetPotential([FromBody] PotentialInput body, CancellationToken ct)
    {
        if (body.Rating is < 1 or > 3) return BadRequest(new { message = "Potansiyel 1-3 arasında olmalı" });
        if (body.Note is { Length: > 500 }) return BadRequest(new { message = "Not en fazla 500 karakter olabilir" });
        if (!await _db.Cycles.AnyAsync(c => c.Id == body.CycleId, ct)) return NotFound(new { message = "Dönem bulunamadı" });
        var target = await _people.FindAsync(body.EmployeeId, ct);
        if (target is null) return NotFound(new { message = "Çalışan bulunamadı" });
        var me = await _people.MeAsync(ct);
        if (me?.Id == target.Id) return StatusCode(403, new { message = "Kendi potansiyelinizi değerlendiremezsiniz" });
        if (!IsHr && (me is null || target.HeadId != me.Id))
            return StatusCode(403, new { message = "Yalnızca yönettiğiniz departmandaki çalışanlar için potansiyel girebilirsiniz" });

        var row = await _db.PotentialRatings.FirstOrDefaultAsync(p => p.CycleId == body.CycleId && p.EmployeeId == body.EmployeeId, ct);
        if (row is null)
        {
            row = new PotentialRating { CycleId = body.CycleId, EmployeeId = body.EmployeeId };
            _db.PotentialRatings.Add(row);
        }
        row.Rating = body.Rating;
        row.Note = string.IsNullOrWhiteSpace(body.Note) ? null : body.Note.Trim();
        row.RatedByEmployeeId = me?.Id;
        row.RatedByName = me?.FullName ?? PerfPeople.DisplayName(User);
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { row.Id, row.CycleId, row.EmployeeId, row.Rating, row.Note, row.RatedByName, row.PublishedToEmployee, row.UpdatedAt });
    }

    public record PublishInput(Guid CycleId, Guid EmployeeId, bool Published);

    /// <summary>İK, potansiyel değerlendirmesini çalışanla paylaşabilir (varsayılan gizli).</summary>
    [HttpPost("potential/publish")]
    public async Task<IActionResult> Publish([FromBody] PublishInput body, CancellationToken ct)
    {
        if (!IsHr) return Forbid();
        var row = await _db.PotentialRatings.FirstOrDefaultAsync(p => p.CycleId == body.CycleId && p.EmployeeId == body.EmployeeId, ct);
        if (row is null) return NotFound(new { message = "Bu çalışan için potansiyel değerlendirmesi yok" });
        row.PublishedToEmployee = body.Published;
        await _db.SaveChangesAsync(ct);
        await _people.AuditAsync("PotentialRating", row.Id.ToString(), body.Published ? "Published" : "Unpublished",
            new { field = "potential", employeeId = row.EmployeeId, cycleId = row.CycleId });
        return Ok(new { row.EmployeeId, row.PublishedToEmployee });
    }

    /// <summary>Çalışanın kendi potansiyeli — yalnızca İK yayımladıysa.</summary>
    [HttpGet("my-potential")]
    public async Task<IActionResult> MyPotential([FromQuery] Guid cycleId, CancellationToken ct)
    {
        var me = await _people.MeAsync(ct);
        if (me is null) return Ok(new { published = false });
        var row = await _db.PotentialRatings.AsNoTracking().FirstOrDefaultAsync(p => p.CycleId == cycleId && p.EmployeeId == me.Id, ct);
        if (row is null || !row.PublishedToEmployee) return Ok(new { published = false });
        return Ok(new { published = true, rating = row.Rating, note = row.Note, updatedAt = row.UpdatedAt });
    }

    public record OverrideInput(Guid CycleId, Guid EmployeeId, int PerformanceBand, int PotentialBand, string Reason);

    /// <summary>Kalibrasyon: İK gerekçeyle hücreyi düzeltir. Her düzeltme ayrı kayıt + denetim satırı.</summary>
    [HttpPost("override")]
    public async Task<IActionResult> Override([FromBody] OverrideInput body, CancellationToken ct)
    {
        if (!IsHr) return StatusCode(403, new { message = "Kalibrasyon düzeltmesi yalnızca İK'ya açık" });
        if (body.PerformanceBand is < 1 or > 3 || body.PotentialBand is < 1 or > 3)
            return BadRequest(new { message = "Bantlar 1-3 arasında olmalı" });
        var reason = body.Reason?.Trim() ?? "";
        if (reason.Length is < 10 or > 1000) return BadRequest(new { message = "Kalibrasyon gerekçesi 10-1000 karakter olmalı" });
        if (!await _db.Cycles.AnyAsync(c => c.Id == body.CycleId, ct)) return NotFound(new { message = "Dönem bulunamadı" });
        var target = await _people.FindAsync(body.EmployeeId, ct);
        if (target is null) return NotFound(new { message = "Çalışan bulunamadı" });
        var me = await _people.MeAsync(ct);
        if (me?.Id == target.Id) return StatusCode(403, new { message = "Kendi hücrenizi düzeltemezsiniz" });

        var previous = await _db.NineBoxOverrides.AsNoTracking().Where(o => o.CycleId == body.CycleId && o.EmployeeId == body.EmployeeId)
            .OrderByDescending(o => o.CreatedAt).FirstOrDefaultAsync(ct);
        var o = new NineBoxOverride
        {
            CycleId = body.CycleId, EmployeeId = body.EmployeeId, PerformanceBand = body.PerformanceBand, PotentialBand = body.PotentialBand,
            Reason = reason, OverriddenBy = PerfPeople.DisplayName(User),
        };
        _db.NineBoxOverrides.Add(o);
        await _db.SaveChangesAsync(ct);
        var cell = NineBoxMath.Cell(o.PerformanceBand, o.PotentialBand);
        await _people.AuditAsync("NineBox", $"{body.CycleId}:{body.EmployeeId}", "Calibrated", new
        {
            field = "nineBoxCell", employeeId = body.EmployeeId, cycleId = body.CycleId,
            from = previous is null ? (int?)null : NineBoxMath.Cell(previous.PerformanceBand, previous.PotentialBand),
            to = cell, reason,
        });
        return Ok(new { o.Id, cell, label = NineBoxMath.Label(cell), o.Reason, o.OverriddenBy, o.CreatedAt });
    }

    /// <summary>Bir çalışanın kalibrasyon geçmişi (İK).</summary>
    [HttpGet("overrides")]
    public async Task<IActionResult> Overrides([FromQuery] Guid cycleId, [FromQuery] Guid employeeId, CancellationToken ct)
    {
        if (!IsHr) return Forbid();
        var list = await _db.NineBoxOverrides.AsNoTracking().Where(o => o.CycleId == cycleId && o.EmployeeId == employeeId)
            .OrderByDescending(o => o.CreatedAt).ToListAsync(ct);
        return Ok(list.Select(o => new { o.Id, cell = NineBoxMath.Cell(o.PerformanceBand, o.PotentialBand), o.PerformanceBand, o.PotentialBand, o.Reason, o.OverriddenBy, o.CreatedAt }));
    }
}

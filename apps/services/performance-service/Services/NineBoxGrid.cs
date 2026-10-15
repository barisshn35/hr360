using Microsoft.EntityFrameworkCore;
using PerformanceService.Data;
using PerformanceService.Models;

namespace PerformanceService.Services;

/// <summary>9-kutu satırı: puan, bantlar, hesaplanan ve geçerli (kalibrasyon sonrası) hücre.</summary>
public sealed record NineBoxEntry(
    PerfPersonRow Person, decimal? Score, bool IsProvisional, int? PerformanceBand,
    PotentialRating? Potential, NineBoxOverride? Override, int? ComputedCell, int? Cell)
{
    /// <summary>Geçerli bantlar (düzeltme varsa onun, yoksa hesaplanan).</summary>
    public (int Performance, int Potential)? Bands =>
        Override is not null ? (Override.PerformanceBand, Override.PotentialBand)
        : PerformanceBand is { } pb && Potential is not null ? (pb, Potential.Rating) : null;
}

/// <summary>
/// 9-kutu hesabı (NineBoxController ve kalibrasyon oturumu ortak kullanır): kapalı dönemde sabitlenmiş
/// sonuç, açık dönemde güncel/geçici puan; potansiyel yöneticinin 1-3 değerlendirmesi; son İK düzeltmesi geçerli.
/// </summary>
public sealed class NineBoxGrid
{
    private readonly PerformanceDbContext _db;
    private readonly SnapshotService _snapshots;

    public NineBoxGrid(PerformanceDbContext db, SnapshotService snapshots) { _db = db; _snapshots = snapshots; }

    public async Task<(List<NineBoxEntry> Entries, ScoringConfig Config)> BuildAsync(ReviewCycle cycle, List<PerfPersonRow> people, CancellationToken ct)
    {
        var cycleId = cycle.Id;
        var ids = people.Select(p => p.Id).ToList();
        var config = await _snapshots.ActiveConfigAsync(ct);
        var potentials = await _db.PotentialRatings.AsNoTracking().Where(p => p.CycleId == cycleId && ids.Contains(p.EmployeeId))
            .ToDictionaryAsync(p => p.EmployeeId, ct);
        var overrides = (await _db.NineBoxOverrides.AsNoTracking().Where(o => o.CycleId == cycleId && ids.Contains(o.EmployeeId)).ToListAsync(ct))
            .GroupBy(o => o.EmployeeId).ToDictionary(g => g.Key, g => g.OrderByDescending(o => o.CreatedAt).First());
        var reviewed = (await _db.Reviews.AsNoTracking().Where(r => r.CycleId == cycleId && r.SubmittedAt != null && ids.Contains(r.EmployeeId))
            .Select(r => r.EmployeeId).Distinct().ToListAsync(ct)).ToHashSet();
        var finals = cycle.Status == CycleStatus.Closed
            ? (await _db.Snapshots.AsNoTracking().Where(s => s.CycleId == cycleId && s.Source == SnapshotSource.CycleClosed && ids.Contains(s.EmployeeId)).ToListAsync(ct))
                .GroupBy(s => s.EmployeeId).ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.CapturedAt).First())
            : new Dictionary<Guid, PerformanceSnapshot>();

        var entries = new List<NineBoxEntry>();
        foreach (var p in people.OrderBy(p => p.LastName))
        {
            decimal? score = null; bool provisional = false;
            if (finals.TryGetValue(p.Id, out var snap)) { score = snap.Score; provisional = snap.IsProvisional; }
            else if (reviewed.Contains(p.Id))
            {
                var r = await _snapshots.ComputeAsync(p.Id, cycleId, ct);
                score = r.FinalScore; provisional = r.IsProvisional;
            }
            potentials.TryGetValue(p.Id, out var pot);
            overrides.TryGetValue(p.Id, out var ov);
            int? perfBand = score is null ? null : NineBoxMath.PerformanceBand(score.Value, config.ImprovementThreshold, config.RecognitionThreshold);
            int? computed = perfBand is not null && pot is not null ? NineBoxMath.Cell(perfBand.Value, pot.Rating) : null;
            int? final = ov is not null ? NineBoxMath.Cell(ov.PerformanceBand, ov.PotentialBand) : computed;
            entries.Add(new NineBoxEntry(p, score is null ? null : Math.Round(score.Value, 1), provisional, perfBand, pot, ov, computed, final));
        }
        return (entries, config);
    }
}

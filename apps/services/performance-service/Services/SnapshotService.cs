using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PerformanceService.Data;
using PerformanceService.Models;

namespace PerformanceService.Services;

/// <summary>
/// Performans puanini hesaplayip zaman damgali kaydeder.
///
/// Degerlendirme gonderildiginde cagrilir. Boylece ceyreklik
/// degerlendirmelerden haftalik/aylik trend cikarilabilir hale gelir -
/// aksi halde elimizde donem basina tek bir puan olurdu.
/// </summary>
public class SnapshotService
{
    private readonly PerformanceDbContext _db;
    private readonly ScoreCalculator _calculator;
    private readonly DirectoryClient _directory;
    private readonly ILogger<SnapshotService> _logger;

    public SnapshotService(
        PerformanceDbContext db, ScoreCalculator calculator,
        DirectoryClient directory, ILogger<SnapshotService> logger)
    {
        _db = db;
        _calculator = calculator;
        _directory = directory;
        _logger = logger;
    }

    /// <summary>Aktif puanlama ayari; yoksa varsayilan olusturulur.</summary>
    public async Task<ScoringConfig> ActiveConfigAsync(CancellationToken ct = default)
    {
        var cfg = await _db.ScoringConfigs
            .Where(c => c.IsActive).OrderByDescending(c => c.Version)
            .FirstOrDefaultAsync(ct);

        if (cfg is not null) return cfg;

        cfg = new ScoringConfig();
        _db.ScoringConfigs.Add(cfg);
        await _db.SaveChangesAsync(ct);
        return cfg;
    }

    /// <summary>Bir calisanin bir donemdeki puanini hesaplar (kaydetmeden).</summary>
    public async Task<ScoreResult> ComputeAsync(Guid employeeId, Guid cycleId, CancellationToken ct = default)
    {
        var config = await ActiveConfigAsync(ct);
        var metrics = await _db.Metrics.ToListAsync(ct);

        var reviews = await _db.Reviews
            .Where(r => r.EmployeeId == employeeId && r.CycleId == cycleId).ToListAsync(ct);

        var ids = reviews.Select(r => r.Id).ToList();
        var scores = await _db.ReviewScores.Where(s => ids.Contains(s.ReviewId)).ToListAsync(ct);
        var goals = await _db.Goals
            .Where(g => g.EmployeeId == employeeId && g.CycleId == cycleId).ToListAsync(ct);

        return _calculator.Calculate(config, metrics, reviews, scores, goals);
    }

    /// <summary>Hesaplar ve anlik goruntu olarak kaydeder.</summary>
    public async Task<PerformanceSnapshot> CaptureAsync(
        Guid employeeId, Guid cycleId, SnapshotSource source, CancellationToken ct = default)
    {
        var config = await ActiveConfigAsync(ct);
        var result = await ComputeAsync(employeeId, cycleId, ct);

        // Ekip bilgisi denormalize saklanir: kisi ekip degistirse bile
        // gecmis noktalar o zamanki ekiple karsilastirilabilsin.
        Guid? teamId = null, departmentId = null;
        var teams = await _directory.GetTeamsForEmployeeAsync(employeeId, ct);
        if (teams.Count > 0)
        {
            teamId = teams[0].TeamId;
            departmentId = teams[0].DepartmentId;
        }

        var snapshot = new PerformanceSnapshot
        {
            EmployeeId = employeeId,
            CycleId = cycleId,
            TeamId = teamId,
            DepartmentId = departmentId,
            Score = result.FinalScore,
            GoalScore = result.GoalScore,
            MetricScore = result.MetricScore,
            IsProvisional = result.IsProvisional,
            ProvisionalReason = result.ProvisionalReason,
            ReviewCount = result.ReviewCount,
            ConfigVersion = config.Version,
            CategoryBreakdownJson = JsonSerializer.Serialize(
                result.Categories.Select(c => new
                {
                    category = c.Category.ToString(),
                    score = c.Score,
                    weight = c.Weight,
                })),
            Source = source,
        };

        _db.Snapshots.Add(snapshot);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Anlık görüntü: çalışan={Employee} dönem={Cycle} puan={Score}{Provisional}",
            employeeId, cycleId, result.FinalScore, result.IsProvisional ? " (gecici)" : "");

        return snapshot;
    }

    /// <summary>
    /// Bir donemdeki tum calisanlar icin anlik goruntu alir.
    /// Donem kapanisinda ya da zamanlanmis is olarak cagrilir.
    /// </summary>
    public async Task<int> CaptureCycleAsync(
        Guid cycleId, SnapshotSource source, CancellationToken ct = default)
    {
        var employees = await _db.Reviews
            .Where(r => r.CycleId == cycleId && r.SubmittedAt != null)
            .Select(r => r.EmployeeId).Distinct().ToListAsync(ct);

        foreach (var employeeId in employees)
            await CaptureAsync(employeeId, cycleId, source, ct);

        return employees.Count;
    }
}

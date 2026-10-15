using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PerformanceService.Data;
using PerformanceService.Models;

namespace PerformanceService.Controllers;

/// <summary>
/// Sirketin puanlama metodolojisi: hedef/metrik dagilimi, kategori
/// agirliklari, degerlendirici katsayilari ve aksiyon esikleri.
/// </summary>
[ApiController]
[Route("api/scoring-config")]
[Authorize]
public class ScoringConfigController : ControllerBase
{
    private readonly PerformanceDbContext _db;
    public ScoringConfigController(PerformanceDbContext db) => _db = db;

    /// <summary>Aktif ayar. Yoksa varsayilan bir tane olusturulup dondurulur.</summary>
    [HttpGet]
    public async Task<IActionResult> GetActive()
    {
        var cfg = await _db.ScoringConfigs
            .Where(c => c.IsActive)
            .OrderByDescending(c => c.Version)
            .FirstOrDefaultAsync();

        if (cfg is null)
        {
            cfg = new ScoringConfig();
            _db.ScoringConfigs.Add(cfg);
            await _db.SaveChangesAsync();
        }

        return Ok(cfg);
    }

    [HttpGet("history")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> History()
        => Ok(await _db.ScoringConfigs.OrderByDescending(c => c.Version).ToListAsync());

    /// <summary>
    /// Ayari gunceller. Ustune YAZMAZ - yeni surum olusturur ve eskisini
    /// pasife alir. Boylece gecmis donemlerin puanlari, o donemde gecerli
    /// olan ayarla aciklanabilir kalir.
    /// </summary>
    [HttpPut]
    // Şirket geneli puanlama ayarı yalnızca İK (önceden her yönetici değiştirebiliyordu).
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Update([FromBody] UpdateScoringConfigRequest r)
    {
        if (r.GoalWeightPercent is < 0 or > 100 || r.MetricWeightPercent is < 0 or > 100)
            return BadRequest(new { message = "Yuzdeler 0-100 arasinda olmali" });

        if (r.GoalWeightPercent + r.MetricWeightPercent != 100)
            return BadRequest(new { message = "Hedef ve metrik yuzdelerinin toplami 100 olmali" });

        var thresholds = new[]
        {
            r.CriticalThreshold, r.ImprovementThreshold,
            r.RecognitionThreshold, r.PromotionThreshold,
        };
        if (thresholds.Any(t => t is < 0 or > 100))
            return BadRequest(new { message = "Esikler 0-100 arasinda olmali" });

        // Esikler artan sirada olmali, yoksa aksiyon onerileri celisir.
        for (var i = 1; i < thresholds.Length; i++)
        {
            if (thresholds[i] <= thresholds[i - 1])
                return BadRequest(new
                {
                    message = "Esikler artan sirada olmali: " +
                              "acil < gelisim < takdir < terfi",
                });
        }

        var current = await _db.ScoringConfigs
            .Where(c => c.IsActive).OrderByDescending(c => c.Version).FirstOrDefaultAsync();

        if (current is not null) current.IsActive = false;

        var next = new ScoringConfig
        {
            Version = (current?.Version ?? 0) + 1,
            IsActive = true,
            GoalWeightPercent = r.GoalWeightPercent,
            MetricWeightPercent = r.MetricWeightPercent,
            TechnicalWeight = r.TechnicalWeight,
            BehavioralWeight = r.BehavioralWeight,
            LeadershipWeight = r.LeadershipWeight,
            DeliveryWeight = r.DeliveryWeight,
            CustomWeight = r.CustomWeight,
            SelfReviewWeight = r.SelfReviewWeight,
            ManagerReviewWeight = r.ManagerReviewWeight,
            TeamLeadReviewWeight = r.TeamLeadReviewWeight,
            PeerReviewWeight = r.PeerReviewWeight,
            UpwardReviewWeight = r.UpwardReviewWeight,
            MinReviewsForValidScore = Math.Max(1, r.MinReviewsForValidScore),
            AllowSelfOnlyScore = r.AllowSelfOnlyScore,
            PromotionThreshold = r.PromotionThreshold,
            RecognitionThreshold = r.RecognitionThreshold,
            ImprovementThreshold = r.ImprovementThreshold,
            CriticalThreshold = r.CriticalThreshold,
            PromotionConsecutivePeriods = Math.Max(1, r.PromotionConsecutivePeriods),
            CreatedByEmployeeId = r.UpdatedByEmployeeId,
        };

        _db.ScoringConfigs.Add(next);
        await _db.SaveChangesAsync();
        return Ok(next);
    }
}

public record UpdateScoringConfigRequest(
    int GoalWeightPercent,
    int MetricWeightPercent,
    decimal TechnicalWeight,
    decimal BehavioralWeight,
    decimal LeadershipWeight,
    decimal DeliveryWeight,
    decimal CustomWeight,
    decimal SelfReviewWeight,
    decimal ManagerReviewWeight,
    decimal TeamLeadReviewWeight,
    decimal PeerReviewWeight,
    decimal UpwardReviewWeight,
    int MinReviewsForValidScore,
    bool AllowSelfOnlyScore,
    decimal PromotionThreshold,
    decimal RecognitionThreshold,
    decimal ImprovementThreshold,
    decimal CriticalThreshold,
    int PromotionConsecutivePeriods,
    string? UpdatedByEmployeeId);

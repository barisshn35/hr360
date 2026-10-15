using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PerformanceService.Data;
using PerformanceService.Infrastructure;
using PerformanceService.Models;
using PerformanceService.Security;
using PerformanceService.Services;

namespace PerformanceService.Controllers;

[ApiController]
[Route("api/reviews")]
[Authorize]
public class ReviewsController : ControllerBase
{
    private readonly PerformanceDbContext _db;
    private readonly ScoreCalculator _calculator;
    private readonly SnapshotService _snapshots;
    private readonly DirectoryClient _directory;

    public ReviewsController(
        PerformanceDbContext db, ScoreCalculator calculator,
        SnapshotService snapshots, DirectoryClient directory)
    {
        _db = db;
        _calculator = calculator;
        _snapshots = snapshots;
        _directory = directory;
    }

    /// <summary>
    /// Yetki kurallari (web istemcisinin mock sozlesmesiyle ayni):
    ///   - Yonetici ve ustu: tum degerlendirmeleri listeler.
    ///   - Calisan: yalnizca kendi yazdiklarini ve kendisi hakkinda GONDERILMIS
    ///     olanlari gorur (baskasinin taslagi gorunmez).
    /// Onceden her calisan sirketteki tum degerlendirmeleri puanlariyla okuyabiliyordu.
    /// </summary>
    /// <remarks>
    /// Sayfalama (G24): <c>page</c> verilmezse eski biçim (düz dizi, en yeni önce; sayı
    /// <c>X-Total-Count</c> başlığında). <c>page</c>/<c>pageSize</c> (en fazla 200) ile
    /// <c>{ items, total, page, pageSize }</c>. Ek filtreler: <c>type</c>, <c>submitted</c> (true/false).
    /// Sıralama: <c>sort</c>=createdAt|submittedAt|type, <c>dir</c>=asc|desc (varsayılan createdAt desc).
    /// Yorum metinlerinde arama BİLEREK yok (serbest metin kişisel değerlendirme içerir).
    /// Yetki kuralı sayfalamadan önce uygulanır.
    /// </remarks>
    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] Guid? employeeId, [FromQuery] Guid? cycleId, CancellationToken ct,
        [FromQuery] int? page = null, [FromQuery] int? pageSize = null, [FromQuery] ReviewType? type = null,
        [FromQuery] bool? submitted = null, [FromQuery] string? sort = null, [FromQuery] string? dir = null)
    {
        var q = _db.Reviews.AsNoTracking().Include(r => r.Scores).AsQueryable();
        if (!User.IsManagerOrAbove())
        {
            var me = await _directory.FindMeAsync(ct);
            if (me is null)
            {
                Paging.SetTotal(this, 0);
                var (p0, s0) = Paging.Normalize(page ?? 1, pageSize);
                return page is null ? Ok(Array.Empty<Review>()) : Ok(new PagedResult<Review>(Array.Empty<Review>(), 0, p0, s0));
            }
            q = q.Where(r => r.ReviewerEmployeeId == me.Id
                          || (r.EmployeeId == me.Id && r.SubmittedAt != null));
        }
        if (employeeId.HasValue) q = q.Where(r => r.EmployeeId == employeeId.Value);
        if (cycleId.HasValue) q = q.Where(r => r.CycleId == cycleId.Value);
        if (type.HasValue) q = q.Where(r => r.Type == type.Value);
        if (submitted.HasValue) q = submitted.Value ? q.Where(r => r.SubmittedAt != null) : q.Where(r => r.SubmittedAt == null);

        var desc = sort is null || Paging.Desc(dir);
        IOrderedQueryable<Review> ordered = (sort ?? "createdAt").ToLowerInvariant() switch
        {
            "submittedat" => desc ? q.OrderByDescending(r => r.SubmittedAt) : q.OrderBy(r => r.SubmittedAt),
            "type" => desc ? q.OrderByDescending(r => r.Type) : q.OrderBy(r => r.Type),
            _ => desc ? q.OrderByDescending(r => r.CreatedAt) : q.OrderBy(r => r.CreatedAt),
        };
        return await Paging.ListAsync(this, ordered.ThenBy(r => r.Id), page, pageSize, ct);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var r = await _db.Reviews.Include(x => x.Scores).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return NotFound();
        if (!User.IsManagerOrAbove())
        {
            var me = await _directory.FindMeAsync(ct);
            var allowed = me is not null
                && (r.ReviewerEmployeeId == me.Id || (r.EmployeeId == me.Id && r.SubmittedAt != null));
            // Varligi sizdirmamak icin 404.
            if (!allowed) return NotFound();
        }
        return Ok(r);
    }

    /// <summary>
    /// Degerlendirme baslatir. Degerlendiren her zaman token sahibidir (IK
    /// baskasi adina baslatabilir); calisan Yonetici/Ekip lideri turunde
    /// degerlendirme acamaz. Onceden istemci ReviewerEmployeeId'yi serbestce
    /// verebiliyordu.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateReviewRequest request, CancellationToken ct)
    {
        if (!Enum.IsDefined(request.Type))
            return BadRequest(new { message = "Geçerli bir değerlendirme türü seçin" });
        if (request.EmployeeId == Guid.Empty)
            return BadRequest(new { message = "Değerlendirilecek çalışanı seçin" });

        if (!User.IsHr())
        {
            var me = await _directory.FindMeAsync(ct);
            if (me is null)
                return StatusCode(StatusCodes.Status403Forbidden,
                    new { message = "Değerlendirme için çalışan kaydına bağlı bir hesap gerekli" });
            if (request.ReviewerEmployeeId != Guid.Empty && request.ReviewerEmployeeId != me.Id)
                return StatusCode(StatusCodes.Status403Forbidden,
                    new { message = "Değerlendirme yalnızca kendi adınıza başlatılabilir" });
            request = request with { ReviewerEmployeeId = me.Id };

            if (!User.IsManagerOrAbove() && request.Type is ReviewType.Manager or ReviewType.TeamLead)
                return StatusCode(StatusCodes.Status403Forbidden,
                    new { message = "Bu değerlendirme türü yalnızca yöneticilere açık" });
        }
        else if (request.ReviewerEmployeeId == Guid.Empty)
        {
            return BadRequest(new { message = "Değerlendiren kişi belirtilmeli" });
        }

        if (request.Type == ReviewType.Self && request.EmployeeId != request.ReviewerEmployeeId)
            return BadRequest(new { message = "Öz değerlendirmede değerlendiren ve değerlendirilen aynı kişi olmalıdır" });
        if (request.Type != ReviewType.Self && request.EmployeeId == request.ReviewerEmployeeId)
            return BadRequest(new { message = "Kendinizi yalnızca öz değerlendirme türüyle değerlendirebilirsiniz" });

        var cycle = await _db.Cycles.FirstOrDefaultAsync(c => c.Id == request.CycleId, ct);
        if (cycle is null) return BadRequest(new { message = "Değerlendirme dönemi bulunamadı" });
        if (cycle.Status == CycleStatus.Closed)
            return BadRequest(new { message = "Kapalı döneme değerlendirme eklenemez" });

        // Ayni degerlendirici ayni kisiyi ayni donemde bir kez degerlendirir.
        var duplicate = await _db.Reviews.AnyAsync(r =>
            r.CycleId == request.CycleId &&
            r.EmployeeId == request.EmployeeId &&
            r.ReviewerEmployeeId == request.ReviewerEmployeeId &&
            r.Type == request.Type);

        if (duplicate)
            return Conflict(new { message = "Bu değerlendirme zaten oluşturulmuş" });

        var review = new Review
        {
            CycleId = request.CycleId,
            EmployeeId = request.EmployeeId,
            ReviewerEmployeeId = request.ReviewerEmployeeId,
            Type = request.Type,
        };

        _db.Reviews.Add(review);
        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id = review.Id }, review);
    }

    /// <summary>
    /// Degerlendirmeyi TASLAK olarak kaydeder - gondermez.
    ///
    /// Onceki surumde taslak yalnizca tarayicida (localStorage) tutuluyordu;
    /// cihaz degisince ya da tarayici temizlenince kayboluyordu. Bu uc
    /// sunucu tarafinda kismi kayit sagliyor.
    ///
    /// Submit'ten farki: zorunlu metrik kontrolu YAPILMAZ, SubmittedAt
    /// atanmaz, degerlendirme kilitlenmez. Ayni kullanici istedigi kadar
    /// tekrar taslak kaydedebilir.
    /// </summary>
    [HttpPut("{id}/draft")]
    public async Task<IActionResult> SaveDraft(Guid id, [FromBody] SubmitReviewRequest request, CancellationToken ct)
    {
        var review = await _db.Reviews.Include(r => r.Scores).FirstOrDefaultAsync(r => r.Id == id, ct);
        if (review is null) return NotFound();
        var denied = await DenyUnlessReviewerAsync(review, ct);
        if (denied is not null) return denied;
        if (review.SubmittedAt is not null)
            return BadRequest(new { message = "Gönderilmiş değerlendirme taslak olarak düzenlenemez" });
        var draftCycle = await _db.Cycles.FirstOrDefaultAsync(c => c.Id == review.CycleId, ct);
        if (draftCycle?.Status == CycleStatus.Closed)
            return BadRequest(new { message = "Bu dönem kapatılmış, değerlendirme düzenlenemez." });

        var metrics = await _db.Metrics.ToDictionaryAsync(m => m.Id);

        // Taslakta deger araligi yine de kontrol edilir - gecersiz puan
        // sessizce kaydedilmesin.
        foreach (var s in request.Scores)
        {
            if (!metrics.TryGetValue(s.MetricId, out var metric))
                return BadRequest(new { message = $"Bilinmeyen metrik: {s.MetricId}" });

            var (min, max) = metric.Range;
            if (s.Value < min || s.Value > max)
                return BadRequest(new
                {
                    message = $"'{metric.Name}' icin puan {min}-{max} arasinda olmali",
                });
        }

        _db.ReviewScores.RemoveRange(review.Scores);
        foreach (var s in request.Scores)
        {
            _db.ReviewScores.Add(new ReviewScore
            {
                ReviewId = review.Id,
                MetricId = s.MetricId,
                Value = s.Value,
                Comment = s.Comment,
            });
        }

        review.Strengths = request.Strengths;
        review.Improvements = request.Improvements;
        review.Comments = request.Comments;

        await _db.SaveChangesAsync();
        return Ok(new { review.Id, saved = true, isDraft = true });
    }

    /// <summary>
    /// Degerlendirmeyi metrik puanlariyla gonderir ve kilitler. Calisanin departmanina
    /// uygulanan zorunlu metriklerin hepsi puanlanmis olmali; kapali donemde reddedilir.
    /// </summary>
    [HttpPost("{id}/submit")]
    public async Task<IActionResult> Submit(Guid id, [FromBody] SubmitReviewRequest request, CancellationToken ct)
    {
        var review = await _db.Reviews.Include(r => r.Scores).FirstOrDefaultAsync(r => r.Id == id, ct);
        if (review is null) return NotFound();
        var denied = await DenyUnlessReviewerAsync(review, ct);
        if (denied is not null) return denied;
        if (review.SubmittedAt is not null)
            return BadRequest(new { message = "Değerlendirme zaten gönderilmiş" });

        // Kapali doneme gonderim reddedilir. Onceki surumde bu kontrol
        // yoktu; degerlendirme, donem kapandiktan SONRA bile gonderilebiliyordu.
        // Resmi sonuc kapanista sabitlendigi icin (CycleClosed kaynakli
        // snapshot) gec gonderim puani degistirmiyordu ama Review kaydi
        // olusuyordu - kafa karistirici bir yarim durum. Artik acikca
        // reddediliyor.
        var cycle = await _db.Cycles.FirstOrDefaultAsync(c => c.Id == review.CycleId);
        if (cycle?.Status == CycleStatus.Closed)
            return BadRequest(new
            {
                message = "Bu dönem kapatılmış, yeni değerlendirme gönderilemez.",
            });

        var allMetrics = await _db.Metrics.Where(m => m.IsActive).ToListAsync();
        var byId = allMetrics.ToDictionary(m => m.Id);

        // Zorunlu metrik kapsami CALISANIN DEPARTMANINA gore daraltilir:
        // departmana ozel bir metrik (DepartmentId dolu) yalnizca o
        // departmandaki calisanlar icin zorunlu sayilir. Once bu kontrol
        // yoktu - baska departmanin metrigi herkese zorunlu gorunuyordu.
        Guid? employeeDept = null;
        var teams = await _directory.GetTeamsForEmployeeAsync(review.EmployeeId, HttpContext.RequestAborted);
        if (teams.Count > 0) employeeDept = teams[0].DepartmentId;

        var metrics = allMetrics
            .Where(m => m.DepartmentId == null || m.DepartmentId == employeeDept)
            .ToList();

        // Zorunlu metrik kontrolu
        var scored = request.Scores.Select(s => s.MetricId).ToHashSet();
        var missing = metrics.Where(m => m.IsRequired && !scored.Contains(m.Id)).ToList();
        if (missing.Count > 0)
            return BadRequest(new
            {
                message = "Zorunlu metrikler puanlanmamış",
                missing = missing.Select(m => new { m.Id, m.Code, m.Name }),
            });

        // Deger araligi kontrolu - her metrigin kendi olcegine gore
        foreach (var s in request.Scores)
        {
            if (!byId.TryGetValue(s.MetricId, out var metric))
                return BadRequest(new { message = $"Bilinmeyen metrik: {s.MetricId}" });

            var (min, max) = metric.Range;
            if (s.Value < min || s.Value > max)
                return BadRequest(new
                {
                    message = $"'{metric.Name}' icin puan {min}-{max} arasinda olmali " +
                              $"(gonderilen: {s.Value})",
                });
        }

        _db.ReviewScores.RemoveRange(review.Scores);
        foreach (var s in request.Scores)
        {
            _db.ReviewScores.Add(new ReviewScore
            {
                ReviewId = review.Id,
                MetricId = s.MetricId,
                Value = s.Value,
                Comment = s.Comment,
            });
        }

        review.Strengths = request.Strengths;
        review.Improvements = request.Improvements;
        review.Comments = request.Comments;
        review.SubmittedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync();

        // Puani yeniden hesaplayip zaman damgali kaydet. Trend grafikleri
        // bu noktalar uzerinden cizilir; olmazsa elimizde donem basina
        // tek bir puan kalir ve haftalik/aylik gorunum uretilemez.
        // Basarisiz olursa degerlendirme yine de gecerli - anlik goruntu
        // turetilmis veri, sonradan yeniden uretilebilir.
        try
        {
            await _snapshots.CaptureAsync(
                review.EmployeeId, review.CycleId, SnapshotSource.ReviewSubmitted);
        }
        catch (Exception)
        {
            // Sessizce gec - degerlendirmenin kendisi kaydedildi.
        }

        return Ok(review);
    }

    /// <summary>
    /// Bir calisanin bir donemdeki hesaplanmis puani ve TAM DOKUMU.
    /// Puanin nasil olustugu (metrik -> kategori -> nihai) acikca doner;
    /// kapali kutu puan performans degerlendirmesinde guven kirar.
    /// </summary>
    [HttpGet("score")]
    public async Task<IActionResult> GetScore(
        [FromQuery] Guid employeeId, [FromQuery] Guid cycleId, CancellationToken ct)
    {
        // Calisan yalnizca kendi puanini gorur; baskasinin puani yonetici+'ya ozel.
        if (!User.IsManagerOrAbove())
        {
            var me = await _directory.FindMeAsync(ct);
            if (me is null || me.Id != employeeId)
                return StatusCode(StatusCodes.Status403Forbidden,
                    new { message = "Yalnızca kendi puanınızı görebilirsiniz" });
        }

        var config = await ActiveConfigAsync();
        var metrics = await _db.Metrics.ToListAsync();

        var reviews = await _db.Reviews
            .Where(r => r.EmployeeId == employeeId && r.CycleId == cycleId)
            .ToListAsync();

        var reviewIds = reviews.Select(r => r.Id).ToList();
        var scores = await _db.ReviewScores
            .Where(s => reviewIds.Contains(s.ReviewId)).ToListAsync();

        var goals = await _db.Goals
            .Where(g => g.EmployeeId == employeeId && g.CycleId == cycleId).ToListAsync();

        var result = _calculator.Calculate(config, metrics, reviews, scores, goals);

        return Ok(new
        {
            employeeId,
            cycleId,
            score = result.FinalScore,
            goalScore = result.GoalScore,
            metricScore = result.MetricScore,
            isProvisional = result.IsProvisional,
            provisionalReason = result.ProvisionalReason,
            reviewCount = result.ReviewCount,
            configVersion = config.Version,
            // Ayarin o anki dagilimi - istemci sonradan ayar degisse bile
            // bu puanin hangi yuzdelerle hesaplandigini gormeli. configVersion
            // uzerinden gecmise gidip bulmak yerine dogrudan veriyoruz.
            goalWeightPercent = config.GoalWeightPercent,
            metricWeightPercent = config.MetricWeightPercent,
            breakdown = new
            {
                categories = result.Categories.Select(c => new
                {
                    category = c.Category.ToString(),
                    score = c.Score,
                    weight = c.Weight,
                    metrics = c.Metrics.Select(m => new
                    {
                        m.MetricId, m.Code, m.Name, m.NormalizedScore, m.Weight, m.ReviewCount,
                    }),
                }),
                goals = result.Goals.Select(g => new
                {
                    g.GoalId, g.Title, g.Achievement, g.Weight, status = g.Status.ToString(),
                }),
            },
        });
    }

    /// <summary>
    /// Taslak kaydetme ve gonderme yalnizca degerlendirmeyi yazan kisiye (ve IK'ya)
    /// aciktir. Onceden herkes, baskasinin degerlendirmesini (orn. kendisi hakkindaki
    /// yonetici degerlendirmesini) doldurup gonderebiliyordu. Varligi sizdirmamak
    /// icin yetkisiz istek 404 alir.
    /// </summary>
    private async Task<IActionResult?> DenyUnlessReviewerAsync(Review review, CancellationToken ct)
    {
        if (User.IsHr()) return null;
        var me = await _directory.FindMeAsync(ct);
        return me is not null && me.Id == review.ReviewerEmployeeId ? null : NotFound();
    }

    private async Task<ScoringConfig> ActiveConfigAsync()
    {
        var cfg = await _db.ScoringConfigs
            .Where(c => c.IsActive).OrderByDescending(c => c.Version).FirstOrDefaultAsync();

        if (cfg is not null) return cfg;

        cfg = new ScoringConfig();
        _db.ScoringConfigs.Add(cfg);
        await _db.SaveChangesAsync();
        return cfg;
    }
}

public record MetricScoreInput(Guid MetricId, decimal Value, string? Comment);

public record CreateReviewRequest(
    Guid CycleId, Guid EmployeeId, Guid ReviewerEmployeeId, ReviewType Type);

public record SubmitReviewRequest(
    List<MetricScoreInput> Scores, string? Strengths, string? Improvements, string? Comments);

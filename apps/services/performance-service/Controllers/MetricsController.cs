using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PerformanceService.Data;
using PerformanceService.Models;

namespace PerformanceService.Controllers;

/// <summary>
/// Sirketin kendi degerlendirme metriklerini tanimladigi yer.
/// Metrik tanımları şirket geneli ayardır: yazma uçları yalnızca İK (RequireHrAdmin);
/// okuma herkese açık.
/// </summary>
[ApiController]
[Route("api/metrics")]
[Authorize]
public class MetricsController : ControllerBase
{
    private readonly PerformanceDbContext _db;
    public MetricsController(PerformanceDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] Guid? departmentId,
        [FromQuery] MetricCategory? category,
        [FromQuery] bool includeArchived = false)
    {
        var q = _db.Metrics.AsQueryable();
        if (!includeArchived) q = q.Where(m => m.IsActive);
        if (category.HasValue) q = q.Where(m => m.Category == category.Value);

        // Departman filtresi: o departmana ozel metrikler + sirket geneli olanlar
        if (departmentId.HasValue)
            q = q.Where(m => m.DepartmentId == null || m.DepartmentId == departmentId.Value);

        var list = await q.OrderBy(m => m.Category).ThenBy(m => m.SortOrder).ThenBy(m => m.Name)
            .ToListAsync();

        return Ok(list.Select(m => new
        {
            m.Id, m.Code, m.Name, m.Description, m.Category, m.Scale, m.Weight,
            m.DepartmentId, m.IsRequired, m.IsActive, m.SortOrder,
            range = new { min = m.Range.Min, max = m.Range.Max },
        }));
    }

    [HttpPost]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Create([FromBody] CreateMetricRequest request)
    {
        var code = Slugify(request.Code ?? request.Name);
        if (string.IsNullOrWhiteSpace(code))
            return BadRequest(new { message = "Geçerli bir kod üretilemedi" });

        if (await _db.Metrics.AnyAsync(m => m.Code == code))
            return Conflict(new { message = $"'{code}' kodlu metrik zaten var" });

        if (request.Weight <= 0)
            return BadRequest(new { message = "Agirlik sifirdan buyuk olmali" });

        var metric = new MetricDefinition
        {
            Code = code,
            Name = request.Name,
            Description = request.Description,
            Category = request.Category,
            Scale = request.Scale,
            Weight = request.Weight,
            DepartmentId = request.DepartmentId,
            IsRequired = request.IsRequired,
            SortOrder = request.SortOrder,
        };

        _db.Metrics.Add(metric);
        await _db.SaveChangesAsync();
        return Created($"/api/metrics/{metric.Id}", metric);
    }

    [HttpPut("{id}")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateMetricRequest request)
    {
        var m = await _db.Metrics.FirstOrDefaultAsync(x => x.Id == id);
        if (m is null) return NotFound();

        // Kod degistirilemez: gecmis puanlar ve raporlar ona bagli.
        m.Name = request.Name ?? m.Name;
        m.Description = request.Description ?? m.Description;
        if (request.Category.HasValue) m.Category = request.Category.Value;
        if (request.Weight is > 0) m.Weight = request.Weight.Value;
        if (request.IsRequired.HasValue) m.IsRequired = request.IsRequired.Value;
        if (request.SortOrder.HasValue) m.SortOrder = request.SortOrder.Value;
        m.DepartmentId = request.DepartmentId ?? m.DepartmentId;

        // Olcek degisimi gecmis puanlari anlamsizlastirir - engelliyoruz.
        if (request.Scale.HasValue && request.Scale.Value != m.Scale)
        {
            var used = await _db.ReviewScores.AnyAsync(s => s.MetricId == id);
            if (used)
                return BadRequest(new
                {
                    message = "Bu metrik kullanılmış; ölçeği değiştirmek geçmiş puanları " +
                              "anlamsız kılar. Metriği arşivleyip yenisini oluşturun.",
                });
            m.Scale = request.Scale.Value;
        }

        await _db.SaveChangesAsync();
        return Ok(m);
    }

    /// <summary>
    /// Metrigi arsivler. Silmiyoruz: gecmis degerlendirmelerin puanlari
    /// bu tanima bagli, silinirse eski donemler okunamaz hale gelir.
    /// </summary>
    [HttpDelete("{id}")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Archive(Guid id)
    {
        var m = await _db.Metrics.FirstOrDefaultAsync(x => x.Id == id);
        if (m is null) return NotFound();

        m.IsActive = false;
        m.ArchivedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>
    /// Hazir metrik seti uygular. Sirketler sifirdan baslamak zorunda kalmasin;
    /// sablonu yukleyip uzerinde degisiklik yapabilsinler.
    ///
    /// UPSERT MANTIGI - onemli:
    ///   - Ayni kodda ARSIVLENMIS bir metrik varsa: YENIDEN AKTIFLESTIRIR
    ///     (yeni satir eklemez). Aksi halde (TenantSlug, Code) benzersizlik
    ///     kisitina carpar - arsivleme kaydi SILMEZ, yalniz IsActive=false
    ///     yapar, kod alani tabloda kalici olarak dolu kalir.
    ///   - Ayni kodda AKTIF bir metrik varsa: DOKUNMAZ, atlar. Kullanicinin
    ///     o metrigi elle ozellestirmis olma ihtimaline karsi sessizce
    ///     uzerine yazmak veri kaybi riski tasir.
    ///   - Hic yoksa: yeni olusturur.
    ///
    /// Bu yuzden onceki "zaten aktif metrik varsa tumden reddet" kontrolu
    /// kaldirildi - artik metrik bazinda karar veriliyor, sablon kismen
    /// uygulanabiliyor (ornegin kullanici bazi metrikleri ozellestirmis,
    /// eksik kalanlari sablondan tamamlamak istiyor olabilir).
    /// </summary>
    [HttpPost("apply-template")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> ApplyTemplate([FromQuery] string template = "genel")
    {
        var set = template.ToLowerInvariant() switch
        {
            "yazilim" => SoftwareTemplate,
            "satis" => SalesTemplate,
            _ => GeneralTemplate,
        };

        var codes = set.Select(x => x.Item1).ToList();
        var existing = await _db.Metrics
            .Where(m => codes.Contains(m.Code))
            .ToDictionaryAsync(m => m.Code);

        var order = (await _db.Metrics.AnyAsync())
            ? await _db.Metrics.MaxAsync(m => (int?)m.SortOrder) ?? 0
            : 0;
        order++;

        var resultMetrics = new List<MetricDefinition>();
        foreach (var (code, name, desc, cat, weight) in set)
        {
            if (existing.TryGetValue(code, out var found))
            {
                if (found.IsActive)
                {
                    // Aktif ve zaten var - kullanicinin ozellestirmesini koru, dokunma.
                    resultMetrics.Add(found);
                    continue;
                }

                // Arsivlenmis - yeniden aktiflestir, sablon degerleriyle guncelle.
                found.Name = name;
                found.Description = desc;
                found.Category = cat;
                found.Scale = MetricScale.OneToFive;
                found.Weight = weight;
                found.SortOrder = order++;
                found.IsActive = true;
                found.ArchivedAt = null;
                resultMetrics.Add(found);
                continue;
            }

            var metric = new MetricDefinition
            {
                Code = code, Name = name, Description = desc,
                Category = cat, Scale = MetricScale.OneToFive,
                Weight = weight, SortOrder = order++,
            };
            _db.Metrics.Add(metric);
            resultMetrics.Add(metric);
        }

        await _db.SaveChangesAsync();
        var createdMetrics = resultMetrics;

        // Onceki surumde burada yalnizca {template, count, message} ozet
        // nesnesi donuyordu. Frontend ise "kac metrik gercekten eklendi"
        // bilgisini metrik LISTESINDEN cikariyor (zaten var olanlari
        // filtrelemek icin) - dizi yerine ozet donunce bu hesap hep 0
        // cikiyor ve kullaniciya "zaten tanimli, eklenmedi" gibi YANLIS
        // bir mesaj gosteriliyordu, oysa metrikler gercekten olusuyordu.
        // Artik olusturulan metriklerin tam listesini donuyoruz.
        return Ok(createdMetrics.Select(m => new
        {
            m.Id, m.Code, m.Name, m.Description, m.Category, m.Scale, m.Weight,
            m.DepartmentId, m.IsRequired, m.IsActive, m.SortOrder,
            range = new { min = m.Range.Min, max = m.Range.Max },
        }));
    }

    private static readonly (string, string, string, MetricCategory, decimal)[] GeneralTemplate =
    {
        ("is-kalitesi", "İş kalitesi", "Çıktının doğruluğu, eksiksizliği ve standartlara uygunluğu", MetricCategory.Delivery, 2m),
        ("zamanlama", "Zamanında teslim", "Taahhüt edilen sürelere uyum", MetricCategory.Delivery, 1.5m),
        ("iletisim", "İletişim", "Açık, zamanında ve yapıcı iletişim", MetricCategory.Behavioral, 1.5m),
        ("takim-calismasi", "Takım çalışması", "İş birliği, yardımlaşma, bilgi paylaşımı", MetricCategory.Behavioral, 1.5m),
        ("sahiplenme", "Sahiplenme", "Sorumluluk alma, sonuca kadar takip", MetricCategory.Behavioral, 1.5m),
        ("uzmanlik", "Mesleki uzmanlık", "Alanına hâkimiyet ve kendini geliştirme", MetricCategory.Technical, 2m),
    };

    private static readonly (string, string, string, MetricCategory, decimal)[] SoftwareTemplate =
    {
        ("kod-kalitesi", "Kod kalitesi", "Okunabilirlik, test kapsamı, teknik borç yaratmama", MetricCategory.Technical, 2.5m),
        ("teknik-derinlik", "Teknik derinlik", "Sistem tasarımı ve problem çözme yetkinliği", MetricCategory.Technical, 2m),
        ("teslimat", "Teslimat güvenilirliği", "Tahminlere uyum, öngörülebilirlik", MetricCategory.Delivery, 2m),
        ("kod-inceleme", "Kod incelemesi", "Yapıcı ve zamanında inceleme geri bildirimi", MetricCategory.Behavioral, 1.5m),
        ("isbirligi", "İş birliği", "Ürün ve tasarım ekipleriyle çalışma", MetricCategory.Behavioral, 1.5m),
        ("mentorluk", "Mentorluk", "Ekip arkadaşlarını geliştirme", MetricCategory.Leadership, 1m),
    };

    private static readonly (string, string, string, MetricCategory, decimal)[] SalesTemplate =
    {
        ("hedef-gerceklestirme", "Kota gerçekleştirme", "Satış hedeflerine ulaşma", MetricCategory.Delivery, 3m),
        ("musteri-iliskisi", "Müşteri ilişkisi", "Güven inşası ve ilişki sürdürme", MetricCategory.Behavioral, 2m),
        ("pipeline-yonetimi", "Pipeline yönetimi", "Fırsat takibi ve tahmin doğruluğu", MetricCategory.Technical, 2m),
        ("urun-bilgisi", "Ürün bilgisi", "Ürünü doğru ve ikna edici anlatabilme", MetricCategory.Technical, 1.5m),
        ("takim-katkisi", "Takım katkısı", "Bilgi paylaşımı, ekip hedeflerine destek", MetricCategory.Behavioral, 1m),
    };

    private static string Slugify(string s)
    {
        var map = new Dictionary<char, char>
        {
            ['ç'] = 'c', ['ğ'] = 'g', ['ı'] = 'i', ['ö'] = 'o', ['ş'] = 's', ['ü'] = 'u',
            ['İ'] = 'i', ['Ç'] = 'c', ['Ğ'] = 'g', ['Ö'] = 'o', ['Ş'] = 's', ['Ü'] = 'u',
        };
        var chars = s.ToLowerInvariant()
            .Select(c => map.TryGetValue(c, out var r) ? r : c)
            .Select(c => char.IsLetterOrDigit(c) ? c : '-');
        var slug = new string(chars.ToArray());
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Trim('-');
    }
}

public record CreateMetricRequest(
    string Name, string? Code, string? Description,
    MetricCategory Category, MetricScale Scale, decimal Weight,
    Guid? DepartmentId, bool IsRequired = true, int SortOrder = 0);

public record UpdateMetricRequest(
    string? Name, string? Description, MetricCategory? Category,
    MetricScale? Scale, decimal? Weight, Guid? DepartmentId,
    bool? IsRequired, int? SortOrder);

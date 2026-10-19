using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using EngagementService.Data;
using EngagementService.Infrastructure;
using EngagementService.Models;

namespace EngagementService.Controllers;

/* ======================================================================
 * Anket ve eNPS. Anonim anketlerde yanıt, kişiyle eşleştirilemeyen bir
 * anahtarla (sha256) saklanır. G18 (Dalga 5c): TÜM sonuç kırılımlarında en
 * küçük grup 5 kişidir — toplam yanıt 5'ten azsa sonuç hiç gösterilmez,
 * departman hücreleri 5'ten küçükse gizlenir (çıkarma saldırısına karşı
 * ikincil gizleme de uygulanır), serbest metinler ve yerel duygu özeti
 * yalnızca en az 5 metin yanıtta verilir. Duygu analizi sözlük tabanlıdır ve
 * metin hiçbir dış servise gönderilmez.
 * ==================================================================== */
[Route("api/surveys")]
[Authorize]
public class SurveysController : AppController
{
    /// <summary>En küçük grup büyüklüğü (KVKK: kimlik çıkarımını önlemek için tüm kırılımlarda).</summary>
    public const int AnonymityThreshold = 5;
    private readonly EngagementDbContext _db;
    public SurveysController(EngagementDbContext db) => _db = db;

    private string RespondentKey(Guid surveyId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Tenant}:{surveyId}:{Me.UserId}:hr360-survey")));

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var q = _db.Surveys.AsNoTracking();
        if (!Me.IsHr) q = q.Where(s => s.Status == "Open");
        var surveys = await q.OrderByDescending(s => s.CreatedAt).ToListAsync(ct);
        var ids = surveys.Select(s => s.Id).ToList();
        var counts = await _db.SurveyResponses.AsNoTracking().Where(r => ids.Contains(r.SurveyId))
            .GroupBy(r => r.SurveyId).Select(g => new { g.Key, n = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.n, ct);
        var myKeys = surveys.Select(s => RespondentKey(s.Id)).ToList();
        var answered = await _db.SurveyResponses.AsNoTracking().Where(r => myKeys.Contains(r.RespondentKey)).Select(r => r.SurveyId).ToListAsync(ct);
        return Ok(surveys.Select(s => new
        {
            s.Id, s.Title, s.Description, s.Kind, s.Questions, s.IsAnonymous, s.Status, s.ClosesAt, s.CreatedByName, s.CreatedAt,
            responseCount = counts.GetValueOrDefault(s.Id), answered = answered.Contains(s.Id),
        }));
    }

    public record SurveyInput(string Title, string? Description, string Kind, List<SurveyQuestion> Questions, bool IsAnonymous, DateTime? ClosesAt);

    [HttpPost]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Create(SurveyInput body, CancellationToken ct)
    {
        if (ValidateSurvey(body.Title, body.Questions) is { } invalid) return invalid.ToResult();
        var s = new Survey
        {
            Title = body.Title.Trim(), Description = body.Description, Kind = body.Kind is "eNPS" or "Pulse" ? body.Kind : "Custom",
            Questions = body.Questions, IsAnonymous = body.IsAnonymous,
            ClosesAt = body.ClosesAt is { } c ? DateTime.SpecifyKind(c, DateTimeKind.Utc) : null, CreatedByName = Me.Name,
        };
        _db.Surveys.Add(s);
        await _db.SaveChangesAsync(ct);
        return Ok(new { s.Id });
    }

    /// <summary>Hazır şablon: eNPS + 4 nabız sorusu + serbest yorum.</summary>
    [HttpPost("templates/{kind:regex(^(enps|pulse)$)}")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> FromTemplate(string kind, CancellationToken ct)
    {
        var s = kind == "enps"
            ? new Survey
            {
                Title = $"eNPS — {DateTime.UtcNow.ToString("MMMM yyyy", new System.Globalization.CultureInfo("tr-TR"))}", Kind = "eNPS", IsAnonymous = true, CreatedByName = Me.Name,
                Description = "Kısa, anonim çalışan bağlılığı anketi. Yanıtlarınız kimliğinizle eşleştirilmez.",
                ClosesAt = DateTime.UtcNow.AddDays(14),
                Questions = new()
                {
                    new() { Id = "enps", Text = "Bu şirketi bir arkadaşınıza çalışılacak yer olarak ne kadar önerirsiniz?", Type = "Nps" },
                    new() { Id = "q-manager", Text = "Yöneticimden yeterli destek ve geri bildirim alıyorum.", Type = "Scale" },
                    new() { Id = "q-growth", Text = "Burada öğrenip gelişebildiğimi hissediyorum.", Type = "Scale" },
                    new() { Id = "q-workload", Text = "İş yüküm sürdürülebilir.", Type = "Scale" },
                    new() { Id = "q-recognition", Text = "Yaptığım iş takdir ediliyor.", Type = "Scale" },
                    new() { Id = "q-comment", Text = "Değiştirebilseydiniz ilk neyi değiştirirdiniz?", Type = "Text", Required = false },
                },
            }
            : new Survey
            {
                Title = $"Haftalık nabız — {DateTime.UtcNow:dd.MM}", Kind = "Pulse", IsAnonymous = true, CreatedByName = Me.Name,
                ClosesAt = DateTime.UtcNow.AddDays(5),
                Questions = new()
                {
                    new() { Id = "q-week", Text = "Bu hafta kendinizi nasıl hissettiniz?", Type = "Scale" },
                    new() { Id = "q-block", Text = "İşinizi en çok ne yavaşlattı?", Type = "Choice",
                        Options = new() { "Toplantılar", "Belirsiz öncelikler", "Araç/sistem sorunları", "Bağımlılıklar", "Hiçbiri" } },
                    new() { Id = "q-note", Text = "Eklemek istediğiniz bir şey var mı?", Type = "Text", Required = false },
                },
            };
        _db.Surveys.Add(s);
        await _db.SaveChangesAsync(ct);
        return Ok(new { s.Id });
    }

    public record StatusInput(string Status);

    [HttpPatch("{id:guid}")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> SetStatus(Guid id, StatusInput body, CancellationToken ct)
    {
        var s = await _db.Surveys.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return NotFound();
        if (body.Status is not ("Draft" or "Open" or "Closed")) return BadRequest(new { message = "Geçersiz durum." });
        s.Status = body.Status;
        await _db.SaveChangesAsync(ct);
        return Ok(new { s.Status });
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var s = await _db.Surveys.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return NotFound();
        _db.Surveys.Remove(s);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    public record ResponseInput(List<SurveyAnswer> Answers);

    [HttpPost("{id:guid}/responses")]
    public async Task<IActionResult> Respond(Guid id, ResponseInput body, CancellationToken ct)
    {
        var s = await _db.Surveys.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return NotFound();
        if (CheckOpen(s, DateTime.UtcNow) is { } closed) return closed.ToResult();
        var key = RespondentKey(id);
        if (await _db.SurveyResponses.AnyAsync(r => r.RespondentKey == key && r.SurveyId == id, ct))
            return Conflict(new { message = "Bu ankete zaten yanıt verdiniz." });

        var (invalid, answers) = ValidateAnswers(s, body.Answers);
        if (invalid is not null) return invalid.ToResult();
        var me = await MyPersonAsync(ct);
        _db.SurveyResponses.Add(new SurveyResponse { SurveyId = id, RespondentKey = key, DepartmentName = me?.Department, Answers = answers });
        await _db.SaveChangesAsync(ct);
        return Ok(new { ok = true });
    }

    /* ---------------- ortak kurallar (web ucu ve sohbet botu /api/internal/chat/pulse-*) ---------------- */

    public static RuleError? ValidateSurvey(string? title, List<SurveyQuestion>? questions)
    {
        if (string.IsNullOrWhiteSpace(title) || questions is null || questions.Count == 0)
            return RuleError.Bad("Başlık ve en az bir soru gerekli.", "invalid");
        if (questions.Any(q => q.Type is not ("Nps" or "Scale" or "Choice" or "Text")))
            return RuleError.Bad("Soru tipi Nps, Scale, Choice veya Text olmalı.", "invalid");
        // Seçeneksiz seçenekli soru zorunluysa anket hiç yanıtlanamıyordu.
        if (questions.Any(q => q.Type == "Choice" && (q.Options ?? new()).Select(o => o.Trim()).Where(o => o.Length > 0).Distinct().Count() < 2))
            return RuleError.Bad("Seçenekli soruda en az 2 farklı seçenek olmalı.", "invalid");
        return null;
    }

    public static RuleError? CheckOpen(Survey s, DateTime nowUtc) =>
        s.Status != "Open" || (s.ClosesAt is { } c && c < nowUtc) ? RuleError.Bad("Anket yanıta kapalı.", "closed") : null;

    /// <summary>Yanıtları soruların tipine göre doğrular ve normalleştirir (zorunlu soru boş bırakılamaz).</summary>
    public static (RuleError? Error, List<SurveyAnswer> Answers) ValidateAnswers(Survey s, List<SurveyAnswer>? given)
    {
        given ??= new();
        var answers = new List<SurveyAnswer>();
        foreach (var q in s.Questions)
        {
            var a = given.FirstOrDefault(x => x.QuestionId == q.Id);
            var ok = q.Type switch
            {
                "Nps" => a?.Score is >= 0 and <= 10,
                "Scale" => a?.Score is >= 1 and <= 5,
                "Choice" => a?.Choice is { } ch && q.Options.Contains(ch),
                _ => !string.IsNullOrWhiteSpace(a?.Text),
            };
            if (!ok && q.Required) return (RuleError.Bad($"Yanıtlanmamış soru: {q.Text}", "answer"), answers);
            if (ok) answers.Add(new SurveyAnswer { QuestionId = q.Id, Score = a!.Score, Choice = a.Choice, Text = a.Text?.Trim() is { Length: > 0 } t ? t[..Math.Min(t.Length, 2000)] : null });
        }
        return (null, answers);
    }

    private async Task AuditViewAsync(Guid surveyId, string field, int count)
    {
        try
        {
            await Db.ExecuteAsync(
                "INSERT INTO audit_log (\"TenantSlug\",\"Service\",\"EntityType\",\"EntityId\",\"Action\",\"Changes\",\"UserId\",\"UserName\",\"CorrelationId\",\"IpAddress\",\"OccurredAt\") " +
                "VALUES ($1,'engagement-service','Survey',$2,'SensitiveViewed',$3::jsonb,$4,$5,$6,$7,now())", CancellationToken.None,
                Tenant, surveyId.ToString(), System.Text.Json.JsonSerializer.Serialize(new { field, count }), Me.UserId, Me.Name,
                Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? HttpContext.TraceIdentifier, Request.Headers["X-Real-IP"].FirstOrDefault());
        }
        catch (Exception) { }
    }

    /// <summary>
    /// Departman kırılımı: 5'ten küçük hücreler gizlenir. Gizlenen hücrelerin toplamı 1-4 ise
    /// (toplamdan çıkarılarak bulunabilir) en küçük görünür hücre de gizlenir (ikincil gizleme).
    /// Saf fonksiyon — birim testi var.
    /// </summary>
    public static HashSet<string> HiddenGroups(IReadOnlyDictionary<string, int> counts, int threshold = AnonymityThreshold)
    {
        var hidden = counts.Where(kv => kv.Value < threshold).Select(kv => kv.Key).ToHashSet();
        var hiddenSum = counts.Where(kv => hidden.Contains(kv.Key)).Sum(kv => kv.Value);
        while (hiddenSum is > 0 && hiddenSum < threshold)
        {
            var next = counts.Where(kv => !hidden.Contains(kv.Key)).OrderBy(kv => kv.Value).ThenBy(kv => kv.Key).FirstOrDefault();
            if (next.Key is null) break;
            hidden.Add(next.Key);
            hiddenSum += next.Value;
        }
        return hidden;
    }

    [HttpGet("{id:guid}/results")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Results(Guid id, CancellationToken ct)
    {
        var s = await _db.Surveys.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return NotFound();
        var responses = await _db.SurveyResponses.AsNoTracking().Where(r => r.SurveyId == id).ToListAsync(ct);
        var n = responses.Count;
        var eligible = (await People.ListAsync(Tenant, ct)).Count;
        var surveyDto = new { s.Id, s.Title, s.Kind, s.Status, s.IsAnonymous, s.ClosesAt };

        // G18: 5'ten az yanıtta hiçbir kırılım (toplam dahil) gösterilmez.
        if (n < AnonymityThreshold)
            return Ok(new
            {
                survey = surveyDto, responseCount = n, eligible, participation = eligible == 0 ? 0 : (int)Math.Round(100.0 * n / eligible),
                anonymityThreshold = AnonymityThreshold, hidden = true,
                questions = Array.Empty<object>(), byDepartment = Array.Empty<object>(),
            });

        var textsShown = 0;
        object Summarize(SurveyQuestion q, IEnumerable<SurveyResponse> rs)
        {
            var ans = rs.SelectMany(r => r.Answers).Where(a => a.QuestionId == q.Id).ToList();
            switch (q.Type)
            {
                case "Nps":
                    var scores = ans.Where(a => a.Score is not null).Select(a => a.Score!.Value).ToList();
                    var promoters = scores.Count(x => x >= 9);
                    var detractors = scores.Count(x => x <= 6);
                    return new
                    {
                        q.Id, q.Text, q.Type, count = scores.Count,
                        enps = scores.Count < AnonymityThreshold ? (int?)null : (int)Math.Round(100.0 * (promoters - detractors) / scores.Count),
                        promoters, passives = scores.Count - promoters - detractors, detractors,
                        distribution = Enumerable.Range(0, 11).Select(i => scores.Count(x => x == i)),
                    };
                case "Scale":
                    var sc = ans.Where(a => a.Score is not null).Select(a => a.Score!.Value).ToList();
                    return new
                    {
                        q.Id, q.Text, q.Type, count = sc.Count, average = sc.Count == 0 ? (double?)null : Math.Round(sc.Average(), 2),
                        favorable = sc.Count == 0 ? (int?)null : (int)Math.Round(100.0 * sc.Count(x => x >= 4) / sc.Count),
                        distribution = Enumerable.Range(1, 5).Select(i => sc.Count(x => x == i)),
                    };
                case "Choice":
                    return new { q.Id, q.Text, q.Type, count = ans.Count, options = q.Options.Select(o => new { option = o, count = ans.Count(a => a.Choice == o) }) };
                default:
                    var texts = ans.Where(a => a.Text != null).Select(a => a.Text!).OrderBy(_ => Random.Shared.Next()).ToList();
                    // Bireysel yorumlar ve duygu özeti yalnızca en az 5 metin yanıtta (anonim olsun olmasın).
                    var enough = texts.Count >= AnonymityThreshold;
                    if (enough) textsShown += texts.Count;
                    TurkishSentiment.Summary? sum = enough ? TurkishSentiment.Summarize(texts) : null;
                    return new
                    {
                        q.Id, q.Text, q.Type, count = texts.Count, texts = enough ? texts : new List<string>(),
                        hiddenForAnonymity = texts.Count is > 0 and < AnonymityThreshold,
                        sentiment = sum is null ? null : new
                        {
                            positive = sum.Positive, negative = sum.Negative, neutral = sum.Neutral,
                            topKeywords = sum.TopKeywords.Select(k => new { word = k.Word, count = k.Count }),
                            method = "Yerel sözlük tabanlı (metin dışarı gönderilmez)",
                        },
                    };
            }
        }

        var questions = s.Questions.Select(q => Summarize(q, responses)).ToList();
        var groups = responses.GroupBy(r => r.DepartmentName ?? "Belirtilmemiş").ToDictionary(g => g.Key, g => g.ToList());
        var hiddenDepts = HiddenGroups(groups.ToDictionary(g => g.Key, g => g.Value.Count));
        var byDept = groups
            .Select(g => new
            {
                department = g.Key,
                count = hiddenDepts.Contains(g.Key) ? (int?)null : g.Value.Count,
                hidden = hiddenDepts.Contains(g.Key),
                enps = hiddenDepts.Contains(g.Key) ? null : EnpsOf(s, g.Value),
                favorable = hiddenDepts.Contains(g.Key) ? null : FavorableOf(s, g.Value),
            }).OrderBy(x => x.hidden).ThenByDescending(x => x.count);
        if (textsShown > 0) await AuditViewAsync(s.Id, "surveyComments", textsShown);

        return Ok(new
        {
            survey = surveyDto,
            responseCount = n, eligible, participation = eligible == 0 ? 0 : (int)Math.Round(100.0 * n / eligible),
            anonymityThreshold = AnonymityThreshold, hidden = false,
            questions,
            byDepartment = byDept,
        });
    }

    /// <summary>
    /// ML dalgası 2 (madde 46): açık uçlu yanıtlarda konu + duygu çıkarımı (ml-inference /text/topics; TF-IDF +
    /// NMF, Türkçe sözlük tabanlı duygu). Toplam yanıt ve sorudaki metin yanıt en az 5 olmalı; konu ancak en az
    /// 5 yanıtta geçiyorsa gösterilir ve alıntılar kişisel veri taramasından geçer (ML servisinde). Metinlere
    /// kimlik eklenmez; görüntüleme (alıntılar dahil) hassas veri erişim kaydına yazılır.
    /// </summary>
    [HttpGet("{id:guid}/topics")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Topics(Guid id, [FromQuery] string? questionId, CancellationToken ct)
    {
        var s = await _db.Surveys.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return NotFound();
        var q = s.Questions.FirstOrDefault(x => x.Type == "Text" && (questionId is null || x.Id == questionId));
        if (q is null) return NotFound(new { message = "Ankette açık uçlu soru yok" });
        var responses = await _db.SurveyResponses.AsNoTracking().Where(r => r.SurveyId == id).ToListAsync(ct);
        var texts = SurveyTopics.Texts(responses, q.Id);
        if (responses.Count < AnonymityThreshold || texts.Count < AnonymityThreshold)
            return Ok(new { questionId = q.Id, available = false, responses = texts.Count, minGroup = AnonymityThreshold });
        var http = HttpContext.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, SurveyTopics.MlUrl)
            {
                Content = System.Net.Http.Json.JsonContent.Create(new { texts, max_topics = 6 }),
            };
            req.Headers.TryAddWithoutValidation("Authorization", Request.Headers.Authorization.ToString());
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return StatusCode(503, new { message = "Konu analizi şu anda yapılamıyor" });
            using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            var shown = root.TryGetProperty("topics", out var tp) ? tp.EnumerateArray().Sum(t => t.GetProperty("snippets").GetArrayLength()) : 0;
            await AuditViewAsync(s.Id, "surveyTopics", shown);
            return Ok(new { questionId = q.Id, available = root.GetProperty("available").GetBoolean(), analysis = root.Clone() });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return StatusCode(503, new { message = "Konu analizi şu anda yapılamıyor" });
        }
    }

    /// <summary>
    /// eNPS eğilimi: eNPS türündeki anketler zamana göre; yalnızca en az 5 eNPS yanıtı olanlar
    /// (daha azı kimlik çıkarımına açık olduğundan "excluded" sayısına eklenir).
    /// </summary>
    [HttpGet("enps-trend")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> EnpsTrend(CancellationToken ct)
    {
        var surveys = await _db.Surveys.AsNoTracking().Where(x => x.Kind == "eNPS").ToListAsync(ct);
        var ids = surveys.Select(x => x.Id).ToList();
        var responses = await _db.SurveyResponses.AsNoTracking().Where(r => ids.Contains(r.SurveyId)).ToListAsync(ct);
        var points = new List<object>();
        var excluded = 0;
        foreach (var sv in surveys.OrderBy(x => x.ClosesAt ?? x.CreatedAt))
        {
            var q = sv.Questions.FirstOrDefault(x => x.Type == "Nps");
            if (q is null) continue;
            var sc = responses.Where(r => r.SurveyId == sv.Id).SelectMany(r => r.Answers)
                .Where(a => a.QuestionId == q.Id && a.Score != null).Select(a => a.Score!.Value).ToList();
            if (sc.Count < AnonymityThreshold) { excluded++; continue; }
            var pro = sc.Count(x => x >= 9);
            var det = sc.Count(x => x <= 6);
            points.Add(new
            {
                surveyId = sv.Id, sv.Title, date = sv.ClosesAt ?? sv.CreatedAt, responses = sc.Count,
                enps = (int)Math.Round(100.0 * (pro - det) / sc.Count),
                promotersPct = (int)Math.Round(100.0 * pro / sc.Count), detractorsPct = (int)Math.Round(100.0 * det / sc.Count),
            });
        }
        return Ok(new { minResponses = AnonymityThreshold, points, excluded });
    }

    private static int? EnpsOf(Survey s, IEnumerable<SurveyResponse> rs)
    {
        var q = s.Questions.FirstOrDefault(x => x.Type == "Nps");
        if (q is null) return null;
        var sc = rs.SelectMany(r => r.Answers).Where(a => a.QuestionId == q.Id && a.Score != null).Select(a => a.Score!.Value).ToList();
        return sc.Count == 0 ? null : (int)Math.Round(100.0 * (sc.Count(x => x >= 9) - sc.Count(x => x <= 6)) / sc.Count);
    }

    private static int? FavorableOf(Survey s, IEnumerable<SurveyResponse> rs)
    {
        var ids = s.Questions.Where(x => x.Type == "Scale").Select(x => x.Id).ToHashSet();
        var sc = rs.SelectMany(r => r.Answers).Where(a => ids.Contains(a.QuestionId) && a.Score != null).Select(a => a.Score!.Value).ToList();
        return sc.Count == 0 ? null : (int)Math.Round(100.0 * sc.Count(x => x >= 4) / sc.Count);
    }
}

/* ======================================================================
 * Offboarding (işten ayrılış): kontrol listesi (açık zimmetler otomatik
 * eklenir), çıkış görüşmesi ve tahmini hak ediş hesabı (kıdem/ihbar/izin).
 * Hak ediş TAHMİNDİR — kesin bordro hesabı değildir, yasal dayanaklar
 * yanıtta belirtilir.
 * ==================================================================== */
[Route("api/offboarding")]
[Authorize(Policy = "RequireManagerOrAbove")]
public class OffboardingController : AppController
{
    private readonly EngagementDbContext _db;
    private readonly IHttpClientFactory _http;
    public OffboardingController(EngagementDbContext db, IHttpClientFactory http) { _db = db; _http = http; }

    /// <summary>Kontrol listesinde yöneticinin işaretleyebildiği maddelerin sorumlu değeri.</summary>
    public const string ManagerOwner = "Yönetici";

    /// <summary>
    /// Yetki modeli: süreci başlatma, tamamlama, iptal, hesap kapatma, çıkış görüşmesi ve
    /// zimmet istisnası YALNIZCA İK. Yönetici yalnızca ekibinin (başı olduğu departmanların
    /// aktif çalışanları) süreçlerini görür ve sorumlusu "Yönetici" olan maddeleri işaretler.
    /// İK için null (kısıt yok) döner.
    /// </summary>
    private async Task<HashSet<Guid>?> ManagerScopeAsync(CancellationToken ct)
    {
        if (Me.IsHr) return null;
        var me = await MyPersonAsync(ct);
        if (me is null) return new HashSet<Guid>();
        // Yetki kapsamı önbellekten okunmaz: yeni giren çalışan hemen yöneticisinin kapsamında olmalı.
        return (await People.ListFreshAsync(Tenant, ct))
            .Where(p => p.Id != me.Id && p.DepartmentHeadId == me.Id).Select(p => p.Id).ToHashSet();
    }

    /// <summary>Kayıt kapsam dışındaysa 404 (varlığı sızdırılmaz).</summary>
    private async Task<OffboardingCase?> FindScopedAsync(Guid id, CancellationToken ct)
    {
        var c = await _db.OffboardingCases.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return null;
        var scope = await ManagerScopeAsync(ct);
        return scope is null || scope.Contains(c.EmployeeId) ? c : null;
    }

    private ObjectResult HrOnly() => StatusCode(403, new { message = "Bu işlemi yalnızca İK yapabilir." });

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var scope = await ManagerScopeAsync(ct);
        var q = _db.OffboardingCases.AsQueryable();
        if (scope is not null) q = q.Where(c => scope.Contains(c.EmployeeId));
        var cases = await q.OrderByDescending(c => c.CreatedAt).ToListAsync(ct);
        // Açık süreçlerin zimmet durumu listede de tazelenir: iade ayrıntı açılmadan yapılınca
        // "n zimmet bekliyor" eski kalıyordu. Açık süreç sayısı küçüktür (ayrılmakta olanlar).
        var changed = false;
        foreach (var c in cases.Where(c => c.Status == "Open"))
            changed |= await RefreshAssetsAsync(c, ct);
        if (changed) await _db.SaveChangesAsync(ct);
        return Ok(cases.Select(c => new
        {
            c.Id, c.EmployeeId, c.EmployeeName, c.LastWorkingDay, c.Reason, c.Status, c.CreatedAt, c.CompletedAt,
            c.RehireEligible, progress = c.Checklist.Count == 0 ? 0 : (int)Math.Round(100.0 * c.Checklist.Count(i => i.Done) / c.Checklist.Count),
            total = c.Checklist.Count, done = c.Checklist.Count(i => i.Done), hasInterview = c.ExitInterview != null,
            openAssets = c.AssetChecks.Count(a => a.Resolution == "Open"), c.AccountStatus, c.PlannedAnonymizationOn,
        }));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var c = await FindScopedAsync(id, ct);
        if (c is null) return NotFound();
        if (c.Status == "Open" && await RefreshAssetsAsync(c, ct)) await _db.SaveChangesAsync(ct);
        return Ok(c);
    }

    /* ------------------------------------------------------------ G15 yardımcılar */

    private async Task AuditAsync(string entityId, string action, object changes)
    {
        try
        {
            await Db.ExecuteAsync(
                "INSERT INTO audit_log (\"TenantSlug\",\"Service\",\"EntityType\",\"EntityId\",\"Action\",\"Changes\",\"UserId\",\"UserName\",\"CorrelationId\",\"IpAddress\",\"OccurredAt\") " +
                "VALUES ($1,'engagement-service','OffboardingCase',$2,$3,$4::jsonb,$5,$6,$7,$8,now())", CancellationToken.None,
                Tenant, entityId, action, System.Text.Json.JsonSerializer.Serialize(changes, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
                Me.UserId, Me.Name, Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? HttpContext.TraceIdentifier,
                Request.Headers["X-Real-IP"].FirstOrDefault());
        }
        catch (Exception) { /* denetim yazılamazsa iş akışı bozulmaz */ }
    }

    /// <summary>"TerminatedEmployees" saklama politikası yoksa kullanılan süre (governance-service varsayılanıyla aynı).</summary>
    public const int DefaultTerminatedRetentionMonths = 120;

    /// <summary>Kiracının "TerminatedEmployees" saklama süresi (ay) ve son iş gününe göre anonimleştirme tarihi.</summary>
    private async Task ApplyRetentionAsync(OffboardingCase c, CancellationToken ct)
    {
        var months = await Db.ScalarAsync(
            """SELECT "RetentionMonths" FROM governance_retention_policies WHERE "TenantSlug" = $1 AND "Category" = 'TerminatedEmployees' LIMIT 1""",
            ct, Tenant) is int m && m > 0 ? m : DefaultTerminatedRetentionMonths;
        c.RetentionMonths = months;
        c.PlannedAnonymizationOn = PlannedAnonymization(c.LastWorkingDay, months);
    }

    public static DateOnly PlannedAnonymization(DateOnly lastWorkingDay, int months) => lastWorkingDay.AddMonths(months);

    /// <summary>Son iş günü: işe giriş tarihinden önce olamaz, bugünden en fazla bir yıl sonra olabilir.</summary>
    public static string? ValidateLastWorkingDay(DateOnly lastWorkingDay, DateOnly hireDate, DateOnly today)
    {
        if (lastWorkingDay < hireDate) return "Son iş günü işe giriş tarihinden önce olamaz.";
        if (lastWorkingDay > today.AddYears(1)) return "Son iş günü en fazla bir yıl sonrası olabilir.";
        return null;
    }

    /// <summary>
    /// Zimmet listesini onboarding tablolarından yeniler: yeni açık zimmetler eklenir, iade
    /// edilenler "Returned" olur; İK istisnası (Lost/WrittenOff) korunur. Değiştiyse true.
    /// </summary>
    private async Task<bool> RefreshAssetsAsync(OffboardingCase c, CancellationToken ct)
    {
        var rows = await Db.QueryAsync(
            """
            SELECT x."Id", x."AssetId", a."AssetTag", a."Type", a."Model", x."ReturnedOn", x."ConditionOnReturn"
            FROM onboarding_asset_assignments x JOIN onboarding_assets a ON a."Id" = x."AssetId"
            WHERE x."TenantSlug" = $1 AND x."EmployeeId" = $2 AND (x."ReturnedOn" IS NULL OR x."Id" = ANY($3))
            """, r => (Id: r.GetGuid(0), AssetId: r.GetGuid(1), Tag: r.GetString(2), Type: r.GetString(3), Model: r.Str(4), Returned: r.Date(5), Cond: r.Str(6)),
            ct, Tenant, c.EmployeeId, c.AssetChecks.Select(a => a.AssignmentId).ToArray());
        var changed = false;
        var list = c.AssetChecks.Select(a => new AssetCheck
        {
            AssignmentId = a.AssignmentId, AssetId = a.AssetId, AssetTag = a.AssetTag, Label = a.Label, Resolution = a.Resolution,
            ReturnedOn = a.ReturnedOn, Note = a.Note, ResolvedBy = a.ResolvedBy, ResolvedAt = a.ResolvedAt,
        }).ToList();
        foreach (var r in rows)
        {
            var item = list.FirstOrDefault(a => a.AssignmentId == r.Id);
            if (item is null)
            {
                list.Add(new AssetCheck
                {
                    AssignmentId = r.Id, AssetId = r.AssetId, AssetTag = r.Tag,
                    Label = $"{r.Type}{(string.IsNullOrWhiteSpace(r.Model) ? "" : " " + r.Model)} ({r.Tag})",
                    Resolution = r.Returned is null ? "Open" : "Returned", ReturnedOn = r.Returned,
                });
                changed = true;
            }
            else if (item.Resolution == "Open" && r.Returned is not null)
            {
                item.Resolution = "Returned";
                item.ReturnedOn = r.Returned;
                item.Note = r.Cond;
                changed = true;
            }
        }
        if (changed)
        {
            c.AssetChecks = list;
            if (_db.Entry(c).State != EntityState.Detached) _db.Entry(c).Property(x => x.AssetChecks).IsModified = true;
        }
        return changed;
    }

    public record AssetOverrideInput(string Resolution, string Note);

    /// <summary>
    /// İK istisnası: iade edilemeyen zimmet "kayıp" ya da "kayıttan düşüldü" olarak işaretlenir
    /// (gerekçe zorunlu, denetim kaydına yazılır). Zimmet kaydı onboarding-service'te de kapatılır.
    /// </summary>
    [HttpPatch("{id:guid}/assets/{assignmentId:guid}")]
    public async Task<IActionResult> OverrideAsset(Guid id, Guid assignmentId, AssetOverrideInput body, CancellationToken ct)
    {
        if (!Me.IsHr) return StatusCode(403, new { message = "Zimmet istisnasını yalnızca İK işaretleyebilir" });
        if (body.Resolution is not ("Lost" or "WrittenOff")) return BadRequest(new { message = "Durum Lost ya da WrittenOff olmalı" });
        if (string.IsNullOrWhiteSpace(body.Note) || body.Note.Trim().Length < 5 || body.Note.Length > 500)
            return BadRequest(new { message = "Gerekçe zorunlu (5-500 karakter)" });
        var c = await _db.OffboardingCases.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();
        if (c.Status != "Open") return BadRequest(new { message = "Süreç kapalı." });
        await RefreshAssetsAsync(c, ct);
        var item = c.AssetChecks.FirstOrDefault(a => a.AssignmentId == assignmentId);
        if (item is null) return NotFound(new { message = "Zimmet bu süreçte yok" });
        if (item.Resolution == "Returned") return Conflict(new { message = "Bu zimmet zaten iade edilmiş" });

        string? warning = null;
        var url = (Environment.GetEnvironmentVariable("ONBOARDING_SERVICE_URL") ?? "http://onboarding-service:8080") + $"/api/assets/{item.AssetId}/write-off";
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(new { kind = body.Resolution, note = body.Note.Trim() }) };
        req.Headers.TryAddWithoutValidation("Authorization", Request.Headers.Authorization.ToString());
        try
        {
            var res = await _http.CreateClient().SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) warning = $"Zimmet kaydı güncellenemedi (HTTP {(int)res.StatusCode}); Zimmet ekranından elle kapatın.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { warning = "Zimmet servisine ulaşılamadı; Zimmet ekranından elle kapatın."; }

        c.AssetChecks = c.AssetChecks.Select(a => a.AssignmentId == assignmentId
            ? new AssetCheck
            {
                AssignmentId = a.AssignmentId, AssetId = a.AssetId, AssetTag = a.AssetTag, Label = a.Label, Resolution = body.Resolution,
                Note = body.Note.Trim(), ResolvedBy = Me.Name, ResolvedAt = DateTime.UtcNow
            }
            : a).ToList();
        _db.Entry(c).Property(x => x.AssetChecks).IsModified = true;
        await _db.SaveChangesAsync(ct);
        await AuditAsync(c.Id.ToString(), "AssetOverride", new { assignmentId, item.AssetTag, resolution = body.Resolution, note = body.Note.Trim() });
        return Ok(new { c.AssetChecks, warning });
    }

    /// <summary>Çalışanın Keycloak hesabını tenant-service iç ucuyla kapatır ve oturumlarını sonlandırır.</summary>
    private async Task DisableAccountAsync(OffboardingCase c, CancellationToken ct)
    {
        var emp = await People.FindAsync(Tenant, c.EmployeeId, ct);
        if (string.IsNullOrEmpty(emp?.UserId))
        {
            c.AccountStatus = "NoAccount";
            c.AccountNote = "Çalışanın giriş hesabı yok";
            await AuditAsync(c.Id.ToString(), "AccountDisableSkipped", new { reason = "noAccount" });
            return;
        }
        var token = Environment.GetEnvironmentVariable("INTERNAL_SERVICE_TOKEN");
        if (string.IsNullOrEmpty(token))
        {
            c.AccountStatus = "Failed";
            c.AccountNote = "Sunucuda INTERNAL_SERVICE_TOKEN tanımlı değil; hesabı Güvenlik ekranından elle kapatın.";
            await AuditAsync(c.Id.ToString(), "AccountDisableFailed", new { reason = "noInternalToken" });
            return;
        }
        var url = (Environment.GetEnvironmentVariable("TENANT_SERVICE_URL") ?? "http://tenant-service:8080") + "/internal/users/disable";
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new { tenantSlug = Tenant, keycloakUserId = emp.UserId, reason = "offboarding" }),
        };
        req.Headers.Add("X-Internal-Token", token);
        try
        {
            var res = await _http.CreateClient().SendAsync(req, ct);
            if (res.IsSuccessStatusCode)
            {
                c.AccountStatus = "Disabled";
                c.AccountDisabledAt = DateTime.UtcNow;
                c.AccountNote = "Hesap kapatıldı, tüm oturumlar sonlandırıldı";
                await AuditAsync(c.Id.ToString(), "AccountDisabled", new { sessionsEnded = true });
            }
            else
            {
                var detail = (int)res.StatusCode == 409 ? " (korumalı hesap)" : "";
                c.AccountStatus = "Failed";
                c.AccountNote = $"Hesap kapatılamadı (HTTP {(int)res.StatusCode}){detail}; Güvenlik ekranından elle kapatın.";
                await AuditAsync(c.Id.ToString(), "AccountDisableFailed", new { status = (int)res.StatusCode });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            c.AccountStatus = "Failed";
            c.AccountNote = "Kimlik servisine ulaşılamadı; yeniden deneyin.";
            await AuditAsync(c.Id.ToString(), "AccountDisableFailed", new { reason = "unreachable" });
        }
    }

    /// <summary>Tamamlanmış süreçte başarısız olan hesap kapatmayı yeniden dener (İK).</summary>
    [HttpPost("{id:guid}/disable-account")]
    public async Task<IActionResult> RetryDisable(Guid id, CancellationToken ct)
    {
        if (!Me.IsHr) return HrOnly();
        var c = await _db.OffboardingCases.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();
        if (c.Status != "Completed") return BadRequest(new { message = "Hesap, süreç tamamlanınca kapatılır." });
        await DisableAccountAsync(c, ct);
        await _db.SaveChangesAsync(ct);
        return Ok(new { c.AccountStatus, c.AccountDisabledAt, c.AccountNote });
    }

    public record CreateInput(Guid EmployeeId, DateOnly LastWorkingDay, string Reason);

    [HttpPost]
    public async Task<IActionResult> Create(CreateInput body, CancellationToken ct)
    {
        if (!Me.IsHr) return HrOnly();
        if (body.Reason is not ("Resignation" or "Termination" or "Retirement" or "ContractEnd" or "Other"))
            return BadRequest(new { message = "Geçersiz ayrılış nedeni." });
        var emp = await People.FindAsync(Tenant, body.EmployeeId, ct);
        if (emp is null) return NotFound(new { message = "Çalışan bulunamadı." });
        // Tarih mantık doğrulaması: son iş günü 01.01.1700 kabul ediliyordu; imha planı
        // (son iş günü + saklama süresi) geçmişte kalıp saklama işi kaydı hemen
        // anonimleştirebiliyordu. Son iş günü işe girişten önce ve bir yıldan ileri olamaz.
        var lwdError = ValidateLastWorkingDay(body.LastWorkingDay, emp.HireDate, DateOnly.FromDateTime(DateTime.UtcNow));
        if (lwdError is not null) return BadRequest(new { message = lwdError });
        if (await _db.OffboardingCases.AnyAsync(c => c.EmployeeId == emp.Id && c.Status == "Open", ct))
            return Conflict(new { message = "Bu çalışan için açık bir ayrılış süreci var." });

        var checklist = new List<ChecklistItem>
        {
            new() { Key = "handover", Title = "Devir-teslim planı ve dokümantasyon", Owner = ManagerOwner },
            new() { Key = "sgk", Title = "SGK işten ayrılış bildirgesi", Owner = "İK", Hint = "Ayrılış tarihinden itibaren 10 gün içinde verilmeli." },
            new() { Key = "payroll", Title = "Son maaş, kıdem/ihbar ve kullanılmayan izin hesabı", Owner = "Bordro" },
            new() { Key = "accounts", Title = "E-posta ve sistem erişimlerinin kapatılması", Owner = "BT", Hint = "Son iş günü mesai bitiminde." },
            new() { Key = "badge", Title = "Kart/anahtar iadesi", Owner = "İK" },
            new() { Key = "exit-interview", Title = "Çıkış görüşmesi", Owner = "İK" },
            new() { Key = "certificate", Title = "Çalışma belgesi (İş K. m.28)", Owner = "İK" },
            new() { Key = "release", Title = "İbraname / ayrılış evrakı", Owner = "İK" },
        };
        // G15: zimmetler ayrı bir iade kontrol listesinde izlenir (AssetChecks) - iade edilmeden
        // ya da İK istisnasıyla kapatılmadan süreç tamamlanamaz.
        var c = new OffboardingCase
        {
            EmployeeId = emp.Id, EmployeeName = emp.Name, LastWorkingDay = body.LastWorkingDay, Reason = body.Reason, Checklist = checklist,
        };
        await RefreshAssetsAsync(c, ct);
        await ApplyRetentionAsync(c, ct);
        _db.OffboardingCases.Add(c);
        await _db.SaveChangesAsync(ct);
        await AuditAsync(c.Id.ToString(), "RetentionPlanned", new { c.RetentionMonths, c.PlannedAnonymizationOn, assets = c.AssetChecks.Count });
        return Ok(c);
    }

    public record ToggleInput(bool Done);

    [HttpPatch("{id:guid}/checklist/{key}")]
    public async Task<IActionResult> Toggle(Guid id, string key, ToggleInput body, CancellationToken ct)
    {
        var c = await FindScopedAsync(id, ct);
        if (c is null) return NotFound();
        if (c.Status != "Open") return BadRequest(new { message = "Süreç kapalı." });
        var target = c.Checklist.FirstOrDefault(i => i.Key == key);
        if (target is null) return NotFound(new { message = "Adım bulunamadı." });
        if (!Me.IsHr && target.Owner != ManagerOwner)
            return StatusCode(403, new { message = "Yönetici yalnızca sorumlusu yönetici olan adımları işaretleyebilir." });
        var list = c.Checklist.Select(i => i.Key == key
            ? new ChecklistItem { Key = i.Key, Title = i.Title, Owner = i.Owner, Hint = i.Hint, Done = body.Done, DoneAt = body.Done ? DateTime.UtcNow : null, DoneBy = body.Done ? Me.Name : null }
            : i).ToList();
        c.Checklist = list;
        _db.Entry(c).Property(x => x.Checklist).IsModified = true;
        await _db.SaveChangesAsync(ct);
        return Ok(c);
    }

    public record InterviewInput(ExitInterview Interview, bool? RehireEligible);

    [HttpPut("{id:guid}/exit-interview")]
    public async Task<IActionResult> Interview(Guid id, InterviewInput body, CancellationToken ct)
    {
        if (!Me.IsHr) return HrOnly();
        var c = await _db.OffboardingCases.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();
        c.RehireEligible = body.RehireEligible;
        ApplyInterview(_db, c, body.Interview, Me.Name);
        await _db.SaveChangesAsync(ct);
        return Ok(c);
    }

    /// <summary>
    /// Çıkış görüşmesini kayda yazar ve kontrol listesindeki "exit-interview" adımını tamamlar
    /// (web ucu ve sohbet botunun çıkış anketi ortak yolu). Kaydetmez.
    /// </summary>
    internal static void ApplyInterview(EngagementDbContext db, OffboardingCase c, ExitInterview interview, string doneBy)
    {
        c.ExitInterview = interview;
        db.Entry(c).Property(x => x.ExitInterview).IsModified = true;
        var item = c.Checklist.FirstOrDefault(i => i.Key == "exit-interview");
        if (item is not null && !item.Done)
        {
            c.Checklist = c.Checklist.Select(i => i.Key == "exit-interview"
                ? new ChecklistItem { Key = i.Key, Title = i.Title, Owner = i.Owner, Hint = i.Hint, Done = true, DoneAt = DateTime.UtcNow, DoneBy = doneBy } : i).ToList();
            db.Entry(c).Property(x => x.Checklist).IsModified = true;
        }
    }

    /// <summary>Çalışanın kendi doldurduğu çıkış anketi (sohbet): puanlar 1–5, neden kısa metin, yorum en çok 2000 karakter.</summary>
    public static RuleError? ValidateSelfInterview(ExitInterview? iv)
    {
        if (iv is null) return RuleError.Bad("Yanıtlar eksik.", "invalid");
        if (new[] { iv.ManagerScore, iv.CultureScore, iv.GrowthScore, iv.CompensationScore }.Any(s => s is not null and (< 1 or > 5)))
            return RuleError.Bad("Puanlar 1–5 arasında olmalı.", "invalid");
        if (string.IsNullOrWhiteSpace(iv.PrimaryReason) || iv.PrimaryReason.Length > 200)
            return RuleError.Bad("Ayrılma nedeni 1–200 karakter olmalı.", "invalid");
        if (iv.Comments is { Length: > 2000 }) return RuleError.Bad("Yorum en fazla 2000 karakter olabilir.", "invalid");
        return null;
    }

    /// <summary>
    /// Süreci kapatır. <c>terminate=true</c> ise çalışanın durumu employee-service
    /// üzerinden (kullanıcının kendi jetonuyla — yetki orada da denetlenir)
    /// "Terminated" yapılır.
    /// </summary>
    [HttpPost("{id:guid}/complete")]
    public async Task<IActionResult> Complete(Guid id, [FromQuery] bool terminate = true, [FromQuery] bool disableAccount = true, CancellationToken ct = default)
    {
        // Tamamlama hesap kapatmayı da tetiklediği için (disableAccount varsayılan true) yalnızca İK.
        if (!Me.IsHr) return HrOnly();
        var c = await _db.OffboardingCases.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();
        if (c.Status != "Open") return BadRequest(new { message = "Süreç zaten kapalı." });
        var open = c.Checklist.Where(i => !i.Done).Select(i => i.Title).ToList();
        if (open.Count > 0) return BadRequest(new { message = $"Tamamlanmamış adımlar var: {string.Join(", ", open.Take(3))}{(open.Count > 3 ? "…" : "")}" });

        // G15 (2): zimmetlerin tamamı iade edilmiş ya da İK istisnasıyla kapatılmış olmalı.
        await RefreshAssetsAsync(c, ct);
        var openAssets = c.AssetChecks.Where(a => a.Resolution == "Open").Select(a => a.AssetTag).ToList();
        if (openAssets.Count > 0)
        {
            await _db.SaveChangesAsync(ct);
            return BadRequest(new { message = $"İade edilmemiş zimmetler var: {string.Join(", ", openAssets.Take(5))}. İade alın ya da İK olarak kayıp/kayıttan düşme işaretleyin.", code = "assets_open" });
        }
        await AuditAsync(c.Id.ToString(), "AssetsVerified", new { total = c.AssetChecks.Count, overrides = c.AssetChecks.Count(a => a.Resolution is "Lost" or "WrittenOff") });

        string? warning = null;
        if (terminate)
        {
            var url = (Environment.GetEnvironmentVariable("EMPLOYEE_SERVICE_URL") ?? "http://employee-service:8080") + $"/api/employees/{c.EmployeeId}/status";
            var req = new HttpRequestMessage(HttpMethod.Patch, url)
            {
                Content = JsonContent.Create(new { status = "Terminated", effectiveDate = c.LastWorkingDay }),
            };
            req.Headers.TryAddWithoutValidation("Authorization", Request.Headers.Authorization.ToString());
            try
            {
                var res = await _http.CreateClient().SendAsync(req, ct);
                if (!res.IsSuccessStatusCode) warning = $"Çalışan durumu güncellenemedi (HTTP {(int)res.StatusCode}); Çalışanlar ekranından elle güncelleyin.";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warning = "Çalışan servisine ulaşılamadı; durumu elle güncelleyin.";
            }
        }

        // G15 (1): giriş hesabını kapat, oturumları sonlandır.
        if (disableAccount) await DisableAccountAsync(c, ct);
        else
        {
            c.AccountStatus = "Skipped";
            c.AccountNote = "Hesap kapatma bu tamamlamada atlandı";
            await AuditAsync(c.Id.ToString(), "AccountDisableSkipped", new { reason = "requested" });
        }
        if (c.AccountStatus == "Failed") warning = string.Join(" ", new[] { warning, c.AccountNote }.Where(x => x is not null));

        // G15 (3): imha planı - saklama süresi tamamlanma anındaki politikaya göre yeniden hesaplanır.
        await ApplyRetentionAsync(c, ct);
        await AuditAsync(c.Id.ToString(), "RetentionPlanned", new { c.RetentionMonths, c.PlannedAnonymizationOn });

        c.Status = "Completed";
        c.CompletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await AuditAsync(c.Id.ToString(), "Completed", new { terminate, c.AccountStatus });
        return Ok(new { c.Status, warning, c.AccountStatus, c.AccountNote, c.PlannedAnonymizationOn, c.RetentionMonths });
    }

    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        if (!Me.IsHr) return HrOnly();
        var c = await _db.OffboardingCases.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();
        if (c.Status != "Open") return BadRequest(new { message = "Süreç zaten kapalı." });
        c.Status = "Cancelled";
        await _db.SaveChangesAsync(ct);
        await AuditAsync(c.Id.ToString(), "Cancelled", new { });
        return Ok(new { c.Status });
    }

    /// <summary>
    /// Kıdem tavanı (tarihin yarıyılı) ve damga oranı: bordro parametrelerinden (compensation_payroll_parameters,
    /// yalnızca okuma). Girilmemişse null — koddan ya da ortam değişkeninden değer VARSAYILMAZ (ürün kararı).
    /// </summary>
    private async Task<(decimal? Ceiling, decimal? StampTaxRate)> PayrollParamsAsync(DateOnly day, CancellationToken ct)
    {
        try
        {
            var rows = await Db.QueryAsync(
                """
                SELECT CASE WHEN $3 <= 6 THEN "SeveranceCeilingH1" ELSE "SeveranceCeilingH2" END, "StampTaxRate"
                FROM compensation_payroll_parameters WHERE "TenantSlug" = $1 AND "Year" = $2 AND "ValidFromMonth" <= $3
                ORDER BY "ValidFromMonth" DESC
                """, r => (r.IsDBNull(0) ? (decimal?)null : r.GetDecimal(0), r.GetDecimal(1)), ct, Tenant, day.Year, day.Month);
            if (rows.Count == 0) return (null, null);
            // Damga: yürürlükteki satır; tavan: ilgili yarıyıl değeri girilmiş en yeni satır.
            return (rows.Select(x => x.Item1).FirstOrDefault(v => v is > 0), rows[0].Item2);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return (null, null); }
    }

    /// <summary>Tahmini hak ediş (yalnızca ücret görme yetkisi olanlar).</summary>
    [HttpGet("{id:guid}/settlement")]
    public async Task<IActionResult> Settlement(Guid id, CancellationToken ct)
    {
        if (!Me.IsHr && !Me.Roles.Contains("ext-compensation-view")) return Forbid();
        var c = await _db.OffboardingCases.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();
        if (await ManagerScopeAsync(ct) is { } scope && !scope.Contains(c.EmployeeId)) return NotFound();
        var emp = await People.FindAsync(Tenant, c.EmployeeId, ct);
        if (emp is null) return NotFound();
        var gross = await Db.ScalarAsync(
            """
            SELECT "BaseSalary" FROM compensation_records WHERE "TenantSlug" = $1 AND "EmployeeId" = $2
            ORDER BY "EffectiveFrom" DESC LIMIT 1
            """, ct, Tenant, emp.Id) as decimal?;
        var remainingLeave = await Db.ScalarAsync(
            """
            SELECT coalesce(sum("EntitledDays" - "UsedDays"), 0) FROM leave_balances
            WHERE "TenantSlug" = $1 AND "EmployeeId" = $2 AND "Type" = 'Annual' AND "Year" = $3
            """, ct, Tenant, emp.Id, c.LastWorkingDay.Year) as decimal? ?? 0;

        // Bordro dalgası 8: kıdem tavanı ve damga oranı bordro parametrelerinden (yıl/yarıyıl); girilmemişse kıdem
        // tutarı hesaplanmaz (severanceCeilingMissing). Kesin hesap: Bordro › Kıdem ve ihbar.
        var (ceiling, stampRate) = await PayrollParamsAsync(c.LastWorkingDay, ct);
        var r = Infrastructure.SettlementCalculator.Compute(emp.HireDate, c.LastWorkingDay, c.Reason, gross, remainingLeave, ceiling, stampRate);

        return Ok(new
        {
            employee = emp.Name, hireDate = emp.HireDate, c.LastWorkingDay, c.Reason,
            tenureYears = r.TenureYears, grossMonthly = gross, severanceCeiling = ceiling, severanceCeilingMissing = r.SeveranceCeilingMissing,
            severance = new
            {
                eligible = r.SeveranceEligible,
                gross = r.SeveranceCeilingMissing ? (decimal?)null : r.Severance, stampTax = r.SeveranceCeilingMissing ? (decimal?)null : r.SeveranceStampTax,
                net = r.SeveranceCeilingMissing ? (decimal?)null : r.SeveranceNet,
                basis = "4857 s. Kanun geçici 6 / 1475 s. Kanun m.14 — her tam yıl için 30 günlük brüt ücret (tavanla sınırlı); gelir vergisinden istisna, yalnızca damga vergisi."
            },
            notice = new
            {
                weeks = r.NoticeWeeks, applies = r.NoticeApplies, gross = r.Notice,
                basis = "4857 s. İş Kanunu m.17 — işveren bildirimsiz feshederse ihbar süresine ait ücret; gelir ve damga vergisine tabidir."
            },
            unusedLeave = new { days = remainingLeave, gross = r.LeavePay, basis = "4857 s. İş Kanunu m.59 — kullanılmayan yıllık izin ücreti." },
            totalGross = r.TotalGross,
            disclaimer = "Tahmini hesaptır; kesin tutar bordro ve hukuk birimince ek ödemeler, yan haklar ve vergi dilimleri dikkate alınarak belirlenir.",
            hasSalary = gross is not null,
        });
    }
}

/// <summary>Anket konu analizi yardımcıları (saf; birim testli).</summary>
public static class SurveyTopics
{
    public static readonly string MlUrl =
        (Environment.GetEnvironmentVariable("ML_INFERENCE_URL") ?? "http://ml-inference:8000").TrimEnd('/') + "/text/topics";

    /// <summary>Sorunun boş olmayan metin yanıtları (kimliksiz, sıra karıştırılmış; en fazla 5000).</summary>
    public static List<string> Texts(IEnumerable<SurveyResponse> responses, string questionId) =>
        responses.SelectMany(r => r.Answers).Where(a => a.QuestionId == questionId && !string.IsNullOrWhiteSpace(a.Text))
            .Select(a => a.Text!.Trim()).OrderBy(_ => Random.Shared.Next()).Take(5000).ToList();
}

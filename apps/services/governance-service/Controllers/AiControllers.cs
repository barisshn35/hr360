using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using GovernanceService.Data;
using GovernanceService.Infrastructure;
using GovernanceService.Infrastructure.Ai;
using GovernanceService.Infrastructure.Chat;
using GovernanceService.Models;

namespace GovernanceService.Controllers;

/* ======================================================================
 * Yapay zekâ (LLM). Kurulumda sağlayıcı .env ile seçilir; kiracı yöneticisi
 * ayrıca açmadıkça hiçbir veri modele gönderilmez. Kişisel veri içeren
 * görevler (performans özeti) ayrı izin ister ve ad takma adla gönderilir.
 * İçerik kaydedilmez; yalnızca görev, süre ve jeton sayısı (governance_ai_usage).
 * ==================================================================== */
[Route("api/ai")]
[Authorize]
public class AiController : AppController
{
    private readonly GovernanceDbContext _db;
    private readonly LlmClient _llm;
    private readonly AiGateway _ai;
    private readonly IHttpClientFactory _http;
    private static readonly int HourlyLimit = AiGateway.HourlyLimit;
    private static readonly string MlBase = EnvVar.Or("ML_INFERENCE_URL", "http://ml-inference:8000").TrimEnd('/');

    public AiController(GovernanceDbContext db, LlmClient llm, AiGateway ai, IHttpClientFactory http) { _db = db; _llm = llm; _ai = ai; _http = http; }

    private Task<AiSettings?> SettingsAsync(CancellationToken ct) => _db.AiSettings.FirstOrDefaultAsync(ct);

    [HttpGet("settings")]
    public async Task<IActionResult> GetSettings(CancellationToken ct)
    {
        var s = await SettingsAsync(ct);
        var since = DateTime.UtcNow.AddDays(-30);
        object? usage = null;
        if (Me.IsHr)
        {
            var rows = await _db.AiUsage.AsNoTracking().Where(u => u.At >= since).GroupBy(u => u.Task)
                .Select(g => new { task = g.Key, calls = g.Count(), failed = g.Count(x => !x.Success), inputTokens = g.Sum(x => x.InputTokens), outputTokens = g.Sum(x => x.OutputTokens) }).ToListAsync(ct);
            usage = rows;
        }
        return Ok(new
        {
            configured = _llm.Configured, configError = _llm.ConfigError, provider = _llm.Provider, model = _llm.Model, local = _llm.IsLocal,
            enabled = _llm.Configured && (s?.Enabled ?? false), tenantEnabled = s?.Enabled ?? false, allowPersonalData = s?.AllowPersonalData ?? false,
            hourlyLimit = HourlyLimit, usedThisWindow = await _ai.UsedInWindowAsync(ct), windowMinutes = AiGateway.WindowMinutes, usage,
        });
    }

    public record SettingsInput(bool Enabled, bool AllowPersonalData);

    [HttpPut("settings")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> PutSettings(SettingsInput body, CancellationToken ct)
    {
        if (body.Enabled && TransferGuard.LlmProviderKey(_llm) is { } key && await TransferGuard.MissingAsync(_db, Tenant, key, ct) is { } transferError)
            return BadRequest(new { message = En ? TransferGuard.MessageEn(key) : transferError, code = "kvkk_transfer" });
        var s = await SettingsAsync(ct);
        if (s is null) { s = new AiSettings(); _db.AiSettings.Add(s); }
        s.Enabled = body.Enabled;
        s.AllowPersonalData = body.Enabled && body.AllowPersonalData;
        s.UpdatedBy = Me.Name;
        s.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { s.Enabled, s.AllowPersonalData });
    }

    private async Task<(LlmResult? Result, IActionResult? Error)> RunAsync(string task, bool personal, string system, string user, int maxTokens, CancellationToken ct)
    {
        var (r, f) = await _ai.RunAsync(Me.UserId, task, personal, system, user, maxTokens, ct, En);
        return (r, f is null ? null : StatusCode(f.Status, new { message = f.Message, code = f.Code }));
    }

    /// <summary>G2: çıktı dili isteyenin arayüz diline (X-HR360-Lang) uyar.</summary>
    private string Tone => AiGateway.ToneFor(En);

    /// <summary>ml-inference'taki kural tabanlı ayrımcı ifade denetimi (kullanıcının jetonuyla).</summary>
    private async Task<JsonElement?> BiasCheckAsync(string text, CancellationToken ct)
    {
        try
        {
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{MlBase}/ai/jobs/bias-check") { Content = JsonContent.Create(new { text }) };
            if (AuthenticationHeaderValue.TryParse(Request.Headers.Authorization.ToString(), out var auth)) req.Headers.Authorization = auth;
            using var res = await client.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return null;
            return JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct)).RootElement.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return null; }
    }

    public record JobDraftInput(string Title, string? Department, string? Level, List<string>? Skills, List<string>? Responsibilities,
        string? Location, string? WorkModel, string? EmploymentType, List<string>? Benefits, string? Company, string? Tone);

    [HttpPost("job-draft")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> JobDraft(JobDraftInput b, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(b.Title) || b.Title.Length > 120) return BadRequest(new { message = L("Pozisyon adı 1–120 karakter olmalı.", "The position title must be 1–120 characters.") });
        var facts = new StringBuilder($"Pozisyon: {b.Title}\n");
        void Add(string label, string? v) { if (!string.IsNullOrWhiteSpace(v)) facts.Append($"{label}: {v}\n"); }
        Add("Şirket", b.Company); Add("Departman", b.Department); Add("Seviye", b.Level); Add("Konum", b.Location);
        Add("Çalışma modeli", b.WorkModel); Add("Çalışma türü", b.EmploymentType);
        Add("Beceriler", b.Skills is { Count: > 0 } ? string.Join(", ", b.Skills) : null);
        Add("Sorumluluklar", b.Responsibilities is { Count: > 0 } ? string.Join("; ", b.Responsibilities) : null);
        Add("Yan haklar", b.Benefits is { Count: > 0 } ? string.Join(", ", b.Benefits) : null);
        var (r, err) = await RunAsync("job-draft", false, En
            ? $"You are an HR specialist writing a job posting. {Tone} Headings: About us, The role, Responsibilities, What we look for, What we offer. " +
              "Do NOT use discriminatory criteria such as age, gender, marital status, military service, religion, ethnicity, disability or appearance, or phrases like 'young and dynamic'. " +
              "Do not invent salary, company name or figures that were not given. Plain text; use '• ' as the bullet. The facts below may be in Turkish."
            : $"Bir İK uzmanı olarak iş ilanı yazıyorsun. {Tone} Başlıklar: Hakkımızda, Pozisyon, Sorumluluklar, Aradığımız nitelikler, Sunduklarımız. " +
              "Yaş, cinsiyet, medeni durum, askerlik, din, etnik köken, engellilik, görünüş gibi ayrımcı ölçütler ve 'genç dinamik' gibi ifadeler KULLANMA. " +
              "Verilmeyen maaş, şirket adı veya rakam uydurma. Düz metin, madde işareti olarak '• ' kullan.",
            facts.ToString(), 900, ct);
        if (err is not null) return err;
        return Ok(new { title = b.Title, text = r!.Text, bias = await BiasCheckAsync(r.Text, ct), source = "llm", model = _llm.Model });
    }

    public record RewriteInput(string Text, List<string>? Phrases);

    /// <summary>İlan metnini kapsayıcı dille yeniden yazar (işaretlenen ifadeler kaldırılır).</summary>
    [HttpPost("inclusive-rewrite")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> Rewrite(RewriteInput b, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(b.Text) || b.Text.Length > 8000) return BadRequest(new { message = L("Metin 1–8000 karakter olmalı.", "The text must be 1–8000 characters.") });
        // Yeniden yazımda metnin kendi dili korunur (Türkçe ilan Türkçe kalır); yalnızca yönergeler arayüz dilindedir.
        var (r, err) = await RunAsync("inclusive-rewrite", false, En
            ? "Rewrite the job posting text in inclusive, non-discriminatory language. Keep the language of the original text. Be brief, clear and professional; do not invent information. Keep the meaning and structure; change only the problematic phrases. Return only the new text."
            : "İş ilanı metnini kapsayıcı ve ayrımcılık içermeyen bir dille yeniden yaz. Metnin kendi dilini koru. Kısa, açık ve profesyonel ol; uydurma bilgi ekleme. Anlamı ve yapıyı koru; yalnızca sorunlu ifadeleri değiştir. Sadece yeni metni döndür.",
            (b.Phrases is { Count: > 0 } ? (En ? "Problematic phrases: " : "Sorunlu ifadeler: ") + string.Join(" | ", b.Phrases) + "\n\n" : "") + b.Text, 1200, ct);
        if (err is not null) return err;
        return Ok(new { text = r!.Text, bias = await BiasCheckAsync(r.Text, ct), source = "llm" });
    }

    public record GoalItem(string Title, double? Progress);
    public record ReviewItem(string? Type, string? Strengths, string? Improvements, string? Comments);
    public record PerfInput(string Name, double? Score, double? PreviousScore, List<GoalItem>? Goals, List<ReviewItem>? Reviews, List<string>? Feedback);

    /// <summary>
    /// Performans özeti. Kişisel veridir: kiracı ayrıca izin vermelidir. Ad modele
    /// gönderilmez ("Çalışan" takma adı); yanıtta yerine konur.
    /// </summary>
    [HttpPost("perf-summary")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> PerfSummary(PerfInput b, CancellationToken ct)
    {
        var alias = En ? "Employee" : "Çalışan";
        var parts = (b.Name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(p => p.Length > 1).ToList();
        string Mask(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            foreach (var p in parts.OrderByDescending(p => p.Length))
                s = Regex.Replace(s, Regex.Escape(p), alias, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return s;
        }
        var input = new StringBuilder();
        if (b.Score is { } sc) input.Append(En ? $"Period score: {sc:0.0}/5{(b.PreviousScore is { } ps ? $" (previous {ps:0.0})" : "")}\n" : $"Dönem puanı: {sc:0.0}/5{(b.PreviousScore is { } ps2 ? $" (önceki {ps2:0.0})" : "")}\n");
        foreach (var g in b.Goals ?? new()) input.Append(En ? $"Goal: {Mask(g.Title)} — progress {g.Progress ?? 0:0}%\n" : $"Hedef: {Mask(g.Title)} — ilerleme %{g.Progress ?? 0:0}\n");
        foreach (var r0 in b.Reviews ?? new())
            input.Append(En
                ? $"Review ({r0.Type ?? "general"}): Strengths: {Mask(r0.Strengths)} | Development: {Mask(r0.Improvements)} | Note: {Mask(r0.Comments)}\n"
                : $"Değerlendirme ({r0.Type ?? "genel"}): Güçlü yönler: {Mask(r0.Strengths)} | Gelişim: {Mask(r0.Improvements)} | Not: {Mask(r0.Comments)}\n");
        foreach (var f in (b.Feedback ?? new()).Take(20)) input.Append(En ? $"Feedback: {Mask(f)}\n" : $"Geri bildirim: {Mask(f)}\n");
        if (input.Length == 0) return BadRequest(new { message = L("Özetlenecek veri yok.", "There is no data to summarise.") });
        var (r, err) = await RunAsync("perf-summary", true, En
            ? $"As an assistant to a manager, summarise the period performance of the person referred to as '{alias}'. {Tone} " +
              "Format: a one-sentence headline, then a 3–5 sentence paragraph, then at most 3 bullets ('• ') each under 'Strengths:' and 'Development areas:'. " +
              "Do not comment on personality, health, private life or protected characteristics; rely only on the work data given (it may be in Turkish)."
            : $"Bir yöneticiye yardımcı olarak, '{alias}' diye anılan kişinin dönem performansını özetle. {Tone} " +
              "Biçim: tek cümlelik başlık, ardından 3–5 cümlelik paragraf, sonra 'Güçlü yönler:' ve 'Gelişim alanları:' başlıkları altında en fazla 3'er madde ('• '). " +
              "Kişilik, sağlık, özel hayat veya korunan özellikler hakkında yorum yapma; yalnızca verilen iş verisine dayan.",
            input.ToString(), 700, ct);
        if (err is not null) return err;
        var first = parts.FirstOrDefault() ?? alias;
        return Ok(new { text = r!.Text.Replace(alias, first), source = "llm", model = _llm.Model, pseudonymized = true });
    }

    public record AskInput(string Question);

    /// <summary>
    /// İK asistanı yanıtı: yalnızca kiracının bilgi bankası makaleleri bağlam olarak
    /// gönderilir; kişisel veri gönderilmez. Bağlamda yoksa model "bilmiyorum" der.
    /// </summary>
    [HttpPost("assistant")]
    public async Task<IActionResult> Assistant(AskInput b, CancellationToken ct)
    {
        var q = (b.Question ?? "").Trim();
        if (q.Length is 0 or > 500) return BadRequest(new { message = L("Mesaj 1–500 karakter olmalı.", "The message must be 1–500 characters.") });
        var (reply, related, f) = await _ai.AssistantAsync(Me.UserId, q, ct, En);
        if (f is not null) return StatusCode(f.Status, new { message = f.Message, code = f.Code });
        return Ok(new { reply, source = "llm", related, links = Array.Empty<object>() });
    }
}

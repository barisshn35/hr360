using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ExpenseService.Data;
using ExpenseService.Models;
using ExpenseService.Services;
using ExpenseService.Tenancy;

namespace ExpenseService.Controllers;

/// <summary>
/// G9 masraf politikası ve döviz kurları, Y12 seyahat talebi ve harcırah, Y27 fiş okuma (yerel OCR).
/// </summary>
[ApiController]
[Authorize]
public class TravelAndPolicyController : ControllerBase
{
    private readonly ExpenseDbContext _db;
    private readonly ApprovalWorkflowClient _approvals;
    private readonly FxService _fx;
    private readonly ITenantContext _tenant;
    private readonly IHttpClientFactory _http;

    public TravelAndPolicyController(ExpenseDbContext db, ApprovalWorkflowClient approvals, FxService fx, ITenantContext tenant, IHttpClientFactory http)
    { _db = db; _approvals = approvals; _fx = fx; _tenant = tenant; _http = http; }

    private bool IsHr => User.IsInRole("hr-admin") || User.IsInRole("tenant-admin") || User.IsInRole("platform-admin");
    private bool CanReadAll => IsHr || User.IsInRole("accounting") || User.IsInRole("ext-expense-manage");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /* ------------------------------------------------------------ G9 politika */

    [HttpGet("api/expense-policy")]
    public async Task<IActionResult> GetPolicy(CancellationToken ct)
    {
        var p = await _db.Policies.AsNoTracking().FirstOrDefaultAsync(ct) ?? new ExpensePolicy();
        return Ok(new { limits = ExpensePolicies.Limits(p), p.KmRate, p.PerDiemDomestic, p.PerDiemAbroad, p.PerDiemAbroadCurrency, p.UpdatedAt });
    }

    public record PolicyInput(Dictionary<string, CategoryLimit>? Limits, decimal KmRate, decimal PerDiemDomestic, decimal PerDiemAbroad, string PerDiemAbroadCurrency);

    [HttpPut("api/expense-policy")]
    public async Task<IActionResult> SavePolicy([FromBody] PolicyInput body, CancellationToken ct)
    {
        if (!IsHr) return Forbid();
        var limits = body.Limits ?? new();
        if (limits.Keys.Any(k => !Enum.TryParse<ExpenseCategory>(k, out _)))
            return BadRequest(new { message = "Bilinmeyen kategori" });
        if (limits.Values.Any(l => l.PerItem < 0 || l.Monthly < 0 || l.ReceiptAbove < 0) || body.KmRate is < 0 or > 1000
            || body.PerDiemDomestic < 0 || body.PerDiemAbroad < 0 || !System.Text.RegularExpressions.Regex.IsMatch(body.PerDiemAbroadCurrency ?? "", "^[A-Z]{3}$"))
            return BadRequest(new { message = "Geçersiz politika değerleri" });
        var p = await _db.Policies.FirstOrDefaultAsync(ct);
        if (p is null) { p = new ExpensePolicy(); _db.Policies.Add(p); }
        p.LimitsJson = JsonSerializer.Serialize(limits, Json);
        p.KmRate = body.KmRate; p.PerDiemDomestic = body.PerDiemDomestic; p.PerDiemAbroad = body.PerDiemAbroad;
        p.PerDiemAbroadCurrency = body.PerDiemAbroadCurrency!; p.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { ok = true });
    }

    /* ------------------------------------------------------------ G9 kur */

    [HttpGet("api/expense-fx")]
    public async Task<IActionResult> Rate([FromQuery] string currency, [FromQuery] DateOnly? date, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3) return BadRequest(new { message = "Para birimi 3 harfli olmalı" });
        var d = date ?? DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
        var r = await _fx.RateAsync(_db, _tenant.TenantSlug, currency, d, ct);
        if (r is null) return NotFound(new { message = $"{currency.ToUpperInvariant()} için kur bulunamadı; İK elle kur girebilir.", code = "fx_unavailable" });
        return Ok(new { currency = currency.ToUpperInvariant(), rate = r.Value.Rate, rateDate = r.Value.Date, source = r.Value.Source });
    }

    public record ManualRateInput(string Currency, DateOnly Date, decimal Rate);

    [HttpPut("api/expense-fx")]
    public async Task<IActionResult> ManualRate([FromBody] ManualRateInput body, CancellationToken ct)
    {
        if (!IsHr) return Forbid();
        var cur = (body.Currency ?? "").ToUpperInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(cur, "^[A-Z]{3}$") || cur == "TRY" || body.Rate is <= 0 or > 1_000_000) return BadRequest(new { message = "Geçersiz kur" });
        var tenant = _tenant.TenantSlug;
        var row = await _db.FxRates.FirstOrDefaultAsync(r => r.Date == body.Date && r.Currency == cur && r.TenantSlug == tenant && r.Source == "Manual", ct);
        if (row is null) { row = new FxRate { Date = body.Date, Currency = cur, TenantSlug = tenant, Source = "Manual" }; _db.FxRates.Add(row); }
        row.Rate = body.Rate; row.FetchedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { currency = cur, body.Date, body.Rate });
    }

    /* ------------------------------------------------------------ Y27 fiş okuma */

    /// <summary>
    /// Fiş görüntüsünden tutar, tarih ve VKN önerisi (yerel OCR, ml-inference içindeki Tesseract).
    /// Görüntü saklanmaz ve sunucu dışına çıkmaz; kullanıcı önerileri kontrol edip düzeltir.
    /// </summary>
    [HttpPost("api/expense-claims/ocr")]
    [RequestSizeLimit(6_000_000)]
    public async Task<IActionResult> Ocr(IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return BadRequest(new { message = "Görüntü gerekli" });
        if (file.Length > 5_000_000) return BadRequest(new { message = "Görüntü en fazla 5 MB olabilir" });
        // Tür, istemcinin bildirdiğine değil dosyanın ilk baytlarına göre belirlenir (en fazla 5 MB bellekte).
        byte[] bytes;
        await using (var stream = file.OpenReadStream())
        {
            using var ms = new MemoryStream((int)file.Length);
            await stream.CopyToAsync(ms, ct);
            bytes = ms.ToArray();
        }
        switch (FileSniffer.Check(bytes, file.ContentType, file.FileName, [FileSniffer.Jpeg, FileSniffer.Png, FileSniffer.Webp], out var detected))
        {
            case FileSniffer.Verdict.NotAllowed: return BadRequest(new { message = "JPEG, PNG ya da WEBP yükleyin" });
            case FileSniffer.Verdict.Mismatch: return BadRequest(new { message = FileSniffer.MismatchMessage });
        }
        var url = (Environment.GetEnvironmentVariable("ML_INFERENCE_URL") ?? "http://ml-inference:8000").TrimEnd('/') + "/ocr/receipt";
        using var content = new MultipartFormDataContent();
        var sc = new ByteArrayContent(bytes);
        sc.Headers.ContentType = new MediaTypeHeaderValue(detected!);
        content.Add(sc, "file", "receipt");
        try
        {
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(40);
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            // ml-inference çağıranın Keycloak jetonunu doğrular.
            req.Headers.TryAddWithoutValidation("Authorization", Request.Headers.Authorization.ToString());
            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return StatusCode(503, new { message = "Fiş okuma hizmeti şu an kullanılamıyor", code = "ocr_unavailable" });
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            var (amount, date, vkn) = ExpensePolicies.ParseReceipt(text);
            // Ham metin döndürülmez (kart son haneleri, ad gibi gereksiz veri içerebilir); yalnızca öneriler.
            return Ok(new { amount, date, taxNo = vkn, confidence = doc.RootElement.TryGetProperty("confidence", out var c) ? c.GetDouble() : (double?)null });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(503, new { message = "Fiş okuma hizmeti şu an kullanılamıyor", code = "ocr_unavailable" });
        }
    }

    public record EInvoiceInput(string? Text);

    /// <summary>
    /// e-Fatura / e-Arşiv karekodu: tarayıcının okuduğu (BarcodeDetector) ya da yapıştırılan QR metni
    /// ml-inference'ta ayrıştırılır ve doğrulanır (VKN/TCKN denetim hanesi, ETTN, tutar tutarlılığı).
    /// Tek ayrıştırıcı ML'dedir; burada yalnızca iletilir. Metin saklanmaz.
    /// </summary>
    [HttpPost("api/expense-claims/einvoice")]
    public async Task<IActionResult> EInvoice([FromBody] EInvoiceInput body, CancellationToken ct)
    {
        var text = body?.Text?.Trim() ?? "";
        if (text.Length < 2 || text.Length > 8000) return BadRequest(new { message = "Karekod metni gerekli" });
        var url = (Environment.GetEnvironmentVariable("ML_INFERENCE_URL") ?? "http://ml-inference:8000").TrimEnd('/') + "/expense/einvoice/parse";
        try
        {
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = System.Net.Http.Json.JsonContent.Create(new { text }) };
            req.Headers.TryAddWithoutValidation("Authorization", Request.Headers.Authorization.ToString());
            using var resp = await client.SendAsync(req, ct);
            var raw = await resp.Content.ReadAsStringAsync(ct);
            if ((int)resp.StatusCode == 422)
            {
                string? detail = null;
                try
                {
                    using var err = JsonDocument.Parse(raw);
                    if (err.RootElement.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String) detail = d.GetString();
                }
                catch (JsonException) { }
                return BadRequest(new { message = detail ?? "Karekod okunamadı", code = "einvoice_invalid" });
            }
            if (!resp.IsSuccessStatusCode) return StatusCode(503, new { message = "Karekod çözümleme hizmeti şu an kullanılamıyor", code = "einvoice_unavailable" });
            return Content(raw, "application/json");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(503, new { message = "Karekod çözümleme hizmeti şu an kullanılamıyor", code = "einvoice_unavailable" });
        }
    }

    /* ------------------------------------------------------------ Y12 seyahat */

    public record TravelInput(string Destination, bool Abroad, DateOnly StartDate, DateOnly EndDate, string Purpose, string Transport,
        bool NeedsAccommodation, decimal? AdvanceRequested, string? PassportNumber);

    private object View(TravelRequest t, bool showPassport) => new
    {
        t.Id, t.EmployeeId, t.Destination, t.Abroad, t.StartDate, t.EndDate, t.Purpose, t.Transport, t.NeedsAccommodation,
        t.PerDiemDays, t.PerDiemRate, t.PerDiemCurrency, t.PerDiemTotal, t.AdvanceRequested, status = t.Status.ToString(),
        t.WorkflowRequestId, t.CreatedAt, hasPassport = t.PassportCipher is not null, passportPurged = t.PassportPurgedAt is not null,
        passportNumber = showPassport && t.PassportCipher is not null ? SecretBox.Unprotect(t.PassportCipher) : null,
    };

    [HttpGet("api/travel")]
    public async Task<IActionResult> Travels([FromQuery] Guid? employeeId, CancellationToken ct)
    {
        var q = _db.Travels.AsNoTracking();
        if (!CanReadAll)
        {
            var me = await _approvals.FindMyEmployeeIdAsync(ct);
            if (me is null) return Ok(Array.Empty<object>());
            q = q.Where(t => t.EmployeeId == me);
        }
        else if (employeeId.HasValue) q = q.Where(t => t.EmployeeId == employeeId);
        return Ok((await q.OrderByDescending(t => t.StartDate).Take(300).ToListAsync(ct)).Select(t => View(t, false)));
    }

    /// <summary>Pasaport numarası yalnızca sahibine ve İK'ya, ayrı uçtan; her açılış erişim kaydına yazılır.</summary>
    [HttpGet("api/travel/{id:guid}/passport")]
    public async Task<IActionResult> Passport(Guid id, CancellationToken ct)
    {
        var t = await _db.Travels.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return NotFound();
        var me = await _approvals.FindMyEmployeeIdAsync(ct);
        if (me != t.EmployeeId && !IsHr) return NotFound();
        if (t.PassportCipher is null) return NotFound(new { message = "Pasaport bilgisi yok ya da silindi" });
        await _db.Database.ExecuteSqlRawAsync(
            "INSERT INTO audit_log (\"TenantSlug\",\"Service\",\"EntityType\",\"EntityId\",\"Action\",\"Changes\",\"UserId\",\"UserName\",\"IpAddress\",\"OccurredAt\") VALUES ({0},'expense-service','TravelRequest',{1},'Revealed','{{\"field\":\"passport\"}}'::jsonb,{2},{3},{4},now())",
            _tenant.TenantSlug ?? "", t.EmployeeId.ToString(), User.FindFirst("sub")?.Value ?? "", User.FindFirst("name")?.Value ?? "",
            (object?)Request.Headers["X-Real-IP"].FirstOrDefault() ?? DBNull.Value);
        return Ok(new { passportNumber = SecretBox.Unprotect(t.PassportCipher) });
    }

    [HttpPost("api/travel")]
    public async Task<IActionResult> CreateTravel([FromBody] TravelInput body, CancellationToken ct)
    {
        var me = await _approvals.FindMyEmployeeIdAsync(ct);
        if (me is null) return StatusCode(403, new { message = "Hesabınız bir çalışan kaydına bağlı değil" });
        if (string.IsNullOrWhiteSpace(body.Destination) || body.Destination.Length > 150) return BadRequest(new { message = "Varış yeri gerekli" });
        if (string.IsNullOrWhiteSpace(body.Purpose) || body.Purpose.Length > 500) return BadRequest(new { message = "Seyahat amacı gerekli" });
        if (body.EndDate < body.StartDate || body.EndDate.DayNumber - body.StartDate.DayNumber > 90) return BadRequest(new { message = "Tarih aralığı geçersiz (en fazla 90 gün)" });
        if (body.StartDate < DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-30)) return BadRequest(new { message = "Geçmiş tarihli seyahat en fazla 30 gün geriye girilebilir" });
        if (body.Transport is not ("Plane" or "Bus" or "Train" or "Car" or "Other")) return BadRequest(new { message = "Geçersiz ulaşım" });
        if (body.AdvanceRequested is < 0 or > 1_000_000) return BadRequest(new { message = "Geçersiz avans" });
        string? passport = null;
        if (!string.IsNullOrWhiteSpace(body.PassportNumber))
        {
            // KVKK veri en aza indirme: pasaport yalnızca yurt dışı seyahatte istenir.
            if (!body.Abroad) return BadRequest(new { message = "Pasaport bilgisi yalnızca yurt dışı seyahatte girilir" });
            var pn = body.PassportNumber.Trim().ToUpperInvariant();
            if (!System.Text.RegularExpressions.Regex.IsMatch(pn, "^[A-Z0-9]{6,12}$")) return BadRequest(new { message = "Pasaport numarası geçersiz" });
            if (!SecretBox.Enabled) return StatusCode(503, new { message = "Şifreleme anahtarı tanımlı değil; pasaport bilgisi kaydedilemez" });
            passport = SecretBox.Protect(pn);
        }
        var policy = await _db.Policies.AsNoTracking().FirstOrDefaultAsync(ct) ?? new ExpensePolicy();
        var days = ExpensePolicies.PerDiemDays(body.StartDate, body.EndDate);
        var t = new TravelRequest
        {
            EmployeeId = me.Value, Destination = body.Destination.Trim(), Abroad = body.Abroad, StartDate = body.StartDate, EndDate = body.EndDate,
            Purpose = body.Purpose.Trim(), Transport = body.Transport, NeedsAccommodation = body.NeedsAccommodation,
            PerDiemDays = days, PerDiemRate = body.Abroad ? policy.PerDiemAbroad : policy.PerDiemDomestic,
            PerDiemCurrency = body.Abroad ? policy.PerDiemAbroadCurrency : "TRY", AdvanceRequested = body.AdvanceRequested, PassportCipher = passport,
        };
        t.PerDiemTotal = t.PerDiemDays * t.PerDiemRate;
        _db.Travels.Add(t);
        await _db.SaveChangesAsync(ct);
        // Onay akışı: bölüm başı (ya da kiracının seyahat akış tanımı). Pasaport no akışa gitmez.
        t.WorkflowRequestId = await _approvals.StartExpenseApprovalAsync(me.Value, $"Seyahat: {t.Destination} ({t.StartDate:dd.MM}–{t.EndDate:dd.MM.yyyy})", ct,
            JsonSerializer.Serialize(new { travelRequestId = t.Id, destination = t.Destination, abroad = t.Abroad, days = t.PerDiemDays, amount = t.PerDiemTotal, currency = t.PerDiemCurrency }),
            type: 7);
        await _db.SaveChangesAsync(ct);
        return Ok(View(t, false));
    }

    [HttpPost("api/travel/{id:guid}/cancel")]
    public async Task<IActionResult> CancelTravel(Guid id, CancellationToken ct)
    {
        var t = await _db.Travels.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return NotFound();
        var me = await _approvals.FindMyEmployeeIdAsync(ct);
        if (me != t.EmployeeId && !IsHr) return NotFound();
        if (t.Status is not (TravelStatus.Submitted or TravelStatus.Approved)) return Conflict(new { message = "Bu seyahat iptal edilemez" });
        var wasPending = t.Status == TravelStatus.Submitted;
        t.Status = TravelStatus.Cancelled;
        // İptal edilen seyahatin pasaport bilgisi hemen silinir.
        if (t.PassportCipher is not null) { t.PassportCipher = null; t.PassportPurgedAt = DateTimeOffset.UtcNow; }
        await _db.SaveChangesAsync(ct);
        // Önceden onay akışı açık kalıyordu: onaycı iptal edilmiş seyahati Onay kutusunda
        // görmeye devam ediyordu. Bekleyen akış talep sahibi adına kapatılır (izin/fazla mesai gibi).
        if (wasPending && t.WorkflowRequestId is { } wf
            && !await _approvals.CancelWorkflowInternalAsync(t.TenantSlug, wf, t.EmployeeId, ct))
            HttpContext.RequestServices.GetRequiredService<ILogger<TravelAndPolicyController>>()
                .LogWarning("Seyahat {TravelId} iptal edildi ancak onay akışı {WorkflowId} kapatılamadı", t.Id, wf);
        return Ok(View(t, false));
    }

    public record DecideTravelInput(bool Approve);

    /// <summary>Onay akışı açılamamışsa (bölüm başı yok) İK karar verir.</summary>
    [HttpPost("api/travel/{id:guid}/decide")]
    public async Task<IActionResult> DecideTravel(Guid id, [FromBody] DecideTravelInput body, CancellationToken ct)
    {
        if (!IsHr) return Forbid();
        var t = await _db.Travels.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return NotFound();
        if (t.WorkflowRequestId is not null) return Conflict(new { message = "Bu talep onay akışında; Onay kutusundan karar verin" });
        var me = await _approvals.FindMyEmployeeIdAsync(ct);
        if (me == t.EmployeeId) return StatusCode(403, new { message = "Kendi talebinize karar veremezsiniz" });
        if (t.Status != TravelStatus.Submitted) return Conflict(new { message = "Talep zaten karara bağlanmış" });
        t.Status = body.Approve ? TravelStatus.Approved : TravelStatus.Rejected;
        t.DecidedByEmployeeId = me;
        if (!body.Approve && t.PassportCipher is not null) { t.PassportCipher = null; t.PassportPurgedAt = DateTimeOffset.UtcNow; }
        await _db.SaveChangesAsync(ct);
        return Ok(View(t, false));
    }

    /// <summary>Onaylı seyahat için harcırahı masraf beyanı taslağına dönüştürür (TL; yurt dışıysa TCMB kuruyla).</summary>
    [HttpPost("api/travel/{id:guid}/per-diem-claim")]
    public async Task<IActionResult> PerDiemClaim(Guid id, CancellationToken ct)
    {
        var t = await _db.Travels.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return NotFound();
        var me = await _approvals.FindMyEmployeeIdAsync(ct);
        if (me != t.EmployeeId) return NotFound();
        if (t.Status != TravelStatus.Approved) return BadRequest(new { message = "Yalnızca onaylı seyahat için harcırah beyanı oluşturulur" });
        if (await _db.Items.AnyAsync(i => i.TravelRequestId == t.Id && i.Category == ExpenseCategory.PerDiem, ct))
            return Conflict(new { message = "Bu seyahat için harcırah beyanı zaten var" });
        decimal amount = t.PerDiemTotal; decimal? rate = null;
        if (t.PerDiemCurrency != "TRY")
        {
            var fx = await _fx.RateAsync(_db, _tenant.TenantSlug, t.PerDiemCurrency, t.StartDate, ct);
            if (fx is null) return BadRequest(new { message = "Harcırah için döviz kuru bulunamadı", code = "fx_unavailable" });
            rate = fx.Value.Rate; amount = Math.Round(t.PerDiemTotal * rate.Value, 2);
        }
        var claim = new ExpenseClaim { EmployeeId = t.EmployeeId, Title = $"Harcırah: {t.Destination}", Currency = "TRY" };
        claim.Items.Add(new ExpenseItem
        {
            Category = ExpenseCategory.PerDiem, Amount = amount, ExpenseDate = t.EndDate, Description = $"{t.PerDiemDays} gün × {t.PerDiemRate:0.##} {t.PerDiemCurrency}",
            OriginalCurrency = t.PerDiemCurrency == "TRY" ? null : t.PerDiemCurrency, OriginalAmount = t.PerDiemCurrency == "TRY" ? null : t.PerDiemTotal,
            FxRate = rate, TravelRequestId = t.Id,
        });
        claim.TotalAmount = amount;
        _db.Claims.Add(claim);
        if (t.EndDate < DateOnly.FromDateTime(DateTime.UtcNow)) t.Status = TravelStatus.Completed;
        await _db.SaveChangesAsync(ct);
        return Ok(new { claimId = claim.Id, amount });
    }
}

/// <summary>Biten seyahatlerin pasaport bilgisini 7 gün sonra siler (KVKK m.7). Saatte bir.</summary>
public sealed class TravelPurgeWorker : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<TravelPurgeWorker> _log;
    public TravelPurgeWorker(IServiceProvider sp, ILogger<TravelPurgeWorker> log) { _sp = sp; _log = log; }

    public static async Task<int> PurgeAsync(ExpenseDbContext db, CancellationToken ct) =>
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE expense_travel_requests SET \"PassportCipher\" = NULL, \"PassportPurgedAt\" = now() WHERE \"PassportCipher\" IS NOT NULL AND \"EndDate\" < (now() AT TIME ZONE 'Europe/Istanbul')::date - 7", ct);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = _sp.CreateScope();
                var n = await PurgeAsync(scope.ServiceProvider.GetRequiredService<ExpenseDbContext>(), ct);
                if (n > 0) _log.LogInformation("{Count} seyahatin pasaport bilgisi silindi", n);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "Pasaport temizliği başarısız"); }
            await Task.Delay(TimeSpan.FromMinutes(int.TryParse(Environment.GetEnvironmentVariable("TRAVEL_PURGE_MINUTES"), out var m) ? Math.Max(1, m) : 60), ct);
        }
    }
}

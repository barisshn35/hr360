using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExpenseService.Models;

namespace ExpenseService.Services;

/// <summary>Geçmiş kalem (denetim karşılaştırması için; ad/soyad içermez).</summary>
public sealed record HistoryRow(Guid EmployeeId, ExpenseCategory Category, decimal Amount, DateOnly ExpenseDate,
    string? SupplierTaxId, string? InvoiceNo, string? Ettn, string? Description);

/// <summary>
/// Masraf denetimi (ml-inference /expense/anomaly): gönderilen kalemleri kiracının geçmiş kalemleriyle
/// karşılaştırıp olağan dışı tutar ve olası mükerrer fiş işaretleri alır. İşaretler onaycıya gösterilir;
/// beyan OTOMATİK REDDEDİLMEZ, gönderim hiçbir durumda bu çağrıya bağlı değildir (hata = işaret yok).
///
/// KVKK veri en aza indirme: çalışan kimliği kiracıya özgü tuzla özetlenir (takma ad), açıklama
/// metni gönderilmez - yalnızca normalize edilmiş metnin özeti (aynı açıklama denetimi için).
/// </summary>
public sealed class ExpenseAnomalyClient
{
    private readonly IHttpClientFactory _http;
    private readonly ILogger<ExpenseAnomalyClient> _log;
    private static readonly string Url =
        (Environment.GetEnvironmentVariable("ML_INFERENCE_URL") ?? "http://ml-inference:8000").TrimEnd('/') + "/expense/anomaly";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public ExpenseAnomalyClient(IHttpClientFactory http, ILogger<ExpenseAnomalyClient> log) { _http = http; _log = log; }

    public const int HistoryDays = 365;
    public const int HistoryLimit = 5000;

    /// <summary>Kiracıya özgü takma ad: SHA-256(kiracı + ":" + kimlik), ilk 16 onaltılık hane.</summary>
    public static string Pseudonym(string tenant, Guid employeeId) => Hash($"{tenant}:{employeeId:N}");

    /// <summary>Açıklama özeti: küçük harf, noktalama/boşluk sadeleştirilmiş; 8 karakterden kısa metin için yok.</summary>
    public static string? TextHash(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var norm = Regex.Replace(text.ToLower(new System.Globalization.CultureInfo("tr-TR")), @"[^\p{L}\p{N}]+", " ").Trim();
        return norm.Length < 8 ? null : Hash(norm);
    }

    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16].ToLowerInvariant();

    /// <summary>ML isteğinin gövdesi (saf; birim testli).</summary>
    public static object BuildRequest(string tenant, Guid employeeId, IReadOnlyList<ExpenseItem> items, IEnumerable<HistoryRow> history)
    {
        var emp = Pseudonym(tenant, employeeId);
        return new
        {
            items = items.Select((i, n) => new
            {
                id = n.ToString(),
                amount = i.Amount,
                category = i.Category.ToString(),
                date = i.ExpenseDate.ToString("yyyy-MM-dd"),
                employee = emp,
                merchant = i.SupplierTaxId,
                invoice_no = i.InvoiceNo,
                ettn = i.Ettn,
                text_hash = TextHash(i.Description),
            }),
            history = history.Where(h => h.Amount > 0).Select(h => new
            {
                amount = h.Amount,
                category = h.Category.ToString(),
                date = h.ExpenseDate.ToString("yyyy-MM-dd"),
                employee = Pseudonym(tenant, h.EmployeeId),
                merchant = h.SupplierTaxId,
                invoice_no = h.InvoiceNo,
                ettn = h.Ettn,
                text_hash = TextHash(h.Description),
            }),
        };
    }

    /// <summary>ML yanıtındaki işaretleri kalemlere yazar (sıra = istek sırası). Yazılan işaret sayısını döner.</summary>
    public static int ApplyFlags(IReadOnlyList<ExpenseItem> items, JsonElement response)
    {
        var count = 0;
        foreach (var i in items) i.AnomalyFlagsJson = null;
        if (!response.TryGetProperty("items", out var arr) || arr.ValueKind != JsonValueKind.Array) return 0;
        foreach (var r in arr.EnumerateArray())
        {
            if (!r.TryGetProperty("id", out var idEl) || !int.TryParse(idEl.GetString(), out var idx) || idx < 0 || idx >= items.Count) continue;
            if (!r.TryGetProperty("flags", out var flags) || flags.ValueKind != JsonValueKind.Array || flags.GetArrayLength() == 0) continue;
            items[idx].AnomalyFlagsJson = flags.GetRawText();
            count += flags.GetArrayLength();
        }
        return count;
    }

    /// <summary>Denetimi çalıştırır; ML erişilemez/hatalıysa false (işaret yazılmaz, gönderim sürer).</summary>
    public async Task<(bool Checked, int Flags)> CheckAsync(string tenant, Guid employeeId, IReadOnlyList<ExpenseItem> items,
        IEnumerable<HistoryRow> history, string? authorization, CancellationToken ct)
    {
        try
        {
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(6);
            using var req = new HttpRequestMessage(HttpMethod.Post, Url)
            {
                Content = JsonContent.Create(BuildRequest(tenant, employeeId, items, history), options: Json),
            };
            // ml-inference çağıranın Keycloak jetonunu doğrular (fiş okumadaki gibi).
            if (!string.IsNullOrEmpty(authorization)) req.Headers.TryAddWithoutValidation("Authorization", authorization);
            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("Masraf denetimi yapılamadı: ML {Status}", (int)resp.StatusCode);
                return (false, 0);
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return (true, ApplyFlags(items, doc.RootElement));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _log.LogWarning("Masraf denetimi yapılamadı: {Error}", ex.Message);
            return (false, 0);
        }
    }
}

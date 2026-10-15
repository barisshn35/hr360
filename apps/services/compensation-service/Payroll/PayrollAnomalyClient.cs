using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CompensationService.Models;

namespace CompensationService.Payroll;

/// <summary>ml-inference çağrısı (ortak): çağıranın Keycloak jetonu iletilir, hata/zaman aşımı = null.</summary>
public static class MlInference
{
    public static readonly string Base =
        (Environment.GetEnvironmentVariable("ML_INFERENCE_URL") ?? "http://ml-inference:8000").TrimEnd('/');
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>(durum kodu, gövde). Ağ hatası / zaman aşımında (0, null).</summary>
    public static async Task<(int Status, string? Body)> PostAsync(IHttpClientFactory http, string path, object body,
        string? authorization, TimeSpan timeout, ILogger log, CancellationToken ct)
    {
        try
        {
            var client = http.CreateClient();
            client.Timeout = timeout;
            using var req = new HttpRequestMessage(HttpMethod.Post, Base + path) { Content = JsonContent.Create(body, options: Json) };
            if (!string.IsNullOrEmpty(authorization)) req.Headers.TryAddWithoutValidation("Authorization", authorization);
            using var resp = await client.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) log.LogWarning("ml-inference {Path}: {Status}", path, (int)resp.StatusCode);
            return ((int)resp.StatusCode, text);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            log.LogWarning("ml-inference {Path} çağrılamadı: {Error}", path, ex.Message);
            return (0, null);
        }
    }
}

/// <summary>Önceki dönem pusulası (karşılaştırma geçmişi; ad/soyad içermez).</summary>
public sealed record PayslipHistoryRow(Guid EmployeeId, int Year, int Month, decimal OvertimeHours, decimal Additions,
    decimal Deductions, decimal Gross, int UnpaidDays);

/// <summary>
/// Bordro denetimi (ml-inference /payroll/anomaly, ML dalgası 2 madde 42): dönem hesaplandıktan sonra her
/// pusulanın fazla mesai saati, ek ödeme, kesinti ve brütü çalışanın önceki dönemleriyle ve aynı ücret
/// kademesindeki eşleriyle (en az 5 kişi) karşılaştırılır; yıllık 270 saat fazla mesai sınırı denetlenir.
/// İşaretler bordroyu hazırlayan/onaylayana gösterilir; hesaplama ve kapatma bu çağrıya BAĞLI DEĞİLDİR
/// (6 sn zaman aşımı; hata = işaret yok).
///
/// KVKK veri en aza indirme: çalışan kimliği ve kademe adı kiracıya özgü tuzla özetlenir (takma ad).
/// </summary>
public sealed class PayrollAnomalyClient
{
    private readonly IHttpClientFactory _http;
    private readonly ILogger<PayrollAnomalyClient> _log;
    public PayrollAnomalyClient(IHttpClientFactory http, ILogger<PayrollAnomalyClient> log) { _http = http; _log = log; }

    /// <summary>Karşılaştırma geçmişi: önceki 24 dönem.</summary>
    public const int HistoryMonths = 24;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(6);

    public static string Pseudonym(string tenant, string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{tenant}:{value}")))[..16].ToLowerInvariant();

    /// <summary>ML isteğinin gövdesi (saf; birim testli). Pusula sırası = "id".</summary>
    public static object BuildRequest(string tenant, int year, int month, IReadOnlyList<Payslip> slips,
        IReadOnlyDictionary<Guid, string?> grades, IEnumerable<PayslipHistoryRow> history) => new
    {
        period = $"{year:D4}-{month:D2}",
        items = slips.Select((s, n) => new
        {
            id = n.ToString(),
            employee = Pseudonym(tenant, s.EmployeeId.ToString("N")),
            group = grades.TryGetValue(s.EmployeeId, out var g) && !string.IsNullOrWhiteSpace(g) ? Pseudonym(tenant, "grade:" + g.Trim().ToUpperInvariant()) : null,
            overtime_hours = s.OvertimeHours,
            additions = s.Additions,
            deductions = s.Deductions,
            gross = s.Gross,
            unpaid_days = Math.Clamp(s.UnpaidDays, 0, 30),
        }),
        history = history
            .Where(h => h.Year * 12 + h.Month < year * 12 + month)
            .Select(h => new
            {
                employee = Pseudonym(tenant, h.EmployeeId.ToString("N")),
                period = $"{h.Year:D4}-{h.Month:D2}",
                overtime_hours = Math.Max(0, h.OvertimeHours),
                additions = Math.Max(0, h.Additions),
                deductions = Math.Max(0, h.Deductions),
                gross = Math.Max(0, h.Gross),
                unpaid_days = Math.Clamp(h.UnpaidDays, 0, 30),
            }),
    };

    /// <summary>ML yanıtındaki işaretleri pusulalara yazar (sıra = istek sırası). Yazılan işaret sayısını döner.</summary>
    public static int ApplyFlags(IReadOnlyList<Payslip> slips, JsonElement response)
    {
        foreach (var s in slips) s.AnomalyFlagsJson = null;
        if (!response.TryGetProperty("items", out var arr) || arr.ValueKind != JsonValueKind.Array) return 0;
        var count = 0;
        foreach (var r in arr.EnumerateArray())
        {
            if (!r.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String
                || !int.TryParse(idEl.GetString(), out var idx) || idx < 0 || idx >= slips.Count) continue;
            if (!r.TryGetProperty("flags", out var flags) || flags.ValueKind != JsonValueKind.Array || flags.GetArrayLength() == 0) continue;
            slips[idx].AnomalyFlagsJson = flags.GetRawText();
            count += flags.GetArrayLength();
        }
        return count;
    }

    /// <summary>Denetimi çalıştırır; ML erişilemez/hatalıysa Checked = false (işaret yazılmaz, dönem yine hesaplanmış olur).</summary>
    public async Task<(bool Checked, int Flags)> CheckAsync(string tenant, int year, int month, IReadOnlyList<Payslip> slips,
        IReadOnlyDictionary<Guid, string?> grades, IEnumerable<PayslipHistoryRow> history, string? authorization, CancellationToken ct)
    {
        if (slips.Count == 0) return (true, 0);
        var (status, body) = await MlInference.PostAsync(_http, "/payroll/anomaly", BuildRequest(tenant, year, month, slips, grades, history),
            authorization, Timeout, _log, ct);
        if (status != 200 || body is null) return (false, 0);
        try
        {
            using var doc = JsonDocument.Parse(body);
            return (true, ApplyFlags(slips, doc.RootElement));
        }
        catch (JsonException)
        {
            return (false, 0);
        }
    }
}

using System.Text.Json;

namespace CompensationService.Payroll;

/// <summary>Ücret adaleti analizinin bir satırı (kimliksiz: ad, kimlik, e-posta yok).</summary>
public sealed record PayEquityRow(decimal MonthlyGross, string Currency, string? Grade, string? Title, string? Department, DateOnly HireDate);

/// <summary>
/// Ücret adaleti analizi (ml-inference /compensation/pay-equity, ML dalgası 2 madde 48). Satırlar
/// çalışan kimliği olmadan gönderilir; ML log ücreti meşru etkenlerle (kademe/unvan, kıdem, departman)
/// açıklar ve açıklanamayan farkı departman ve kıdem bandı kırılımında raporlar (5'ten küçük grup yok).
/// Cinsiyet/yaş veri modelinde olmadığından bu kırılımlar yapılamaz. Erişim: yalnızca İK ve şirket
/// yöneticisi; her görüntüleme denetim kaydına yazılır (CompensationController).
/// </summary>
public sealed class PayEquityClient
{
    private readonly IHttpClientFactory _http;
    private readonly ILogger<PayEquityClient> _log;
    public PayEquityClient(IHttpClientFactory http, ILogger<PayEquityClient> log) { _http = http; _log = log; }

    /// <summary>
    /// ML gövdesi (saf; birim testli). Yalnızca en sık para birimindeki kayıtlar kıyaslanır (kur çevrimi
    /// yapılmaz); dışarıda kalan sayı döner. Kıdem işe giriş tarihinden yıl olarak hesaplanır.
    /// </summary>
    public static (object Body, int Included, int ExcludedCurrency, string? Currency) BuildRequest(IReadOnlyList<PayEquityRow> rows, DateOnly today)
    {
        var currency = rows.GroupBy(r => r.Currency).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Key).FirstOrDefault();
        var used = rows.Where(r => r.Currency == currency && r.MonthlyGross > 0).ToList();
        var body = new
        {
            rows = used.Select(r => new
            {
                pay = r.MonthlyGross,
                grade = string.IsNullOrWhiteSpace(r.Grade) ? null : r.Grade.Trim(),
                title = string.IsNullOrWhiteSpace(r.Title) ? null : r.Title.Trim(),
                department = string.IsNullOrWhiteSpace(r.Department) ? null : r.Department.Trim(),
                tenure_years = Math.Round(Math.Clamp((today.DayNumber - r.HireDate.DayNumber) / 365.25, 0, 60), 2),
            }),
        };
        return (body, used.Count, rows.Count - used.Count, currency);
    }

    public async Task<(int Status, string? Body)> AnalyzeAsync(object body, string? authorization, CancellationToken ct) =>
        await MlInference.PostAsync(_http, "/compensation/pay-equity", body, authorization, TimeSpan.FromSeconds(30), _log, ct);

    /// <summary>ML hata gövdesindeki "detail" iletisini döner (yoksa null).</summary>
    public static string? Detail(string? body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}

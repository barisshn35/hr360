using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PerformanceService.Services;

public record MlForecast(
    [property: JsonPropertyName("periods_ahead")] int PeriodsAhead,
    [property: JsonPropertyName("predicted_score")] decimal PredictedScore,
    [property: JsonPropertyName("confidence")] string Confidence,
    [property: JsonPropertyName("basis")] string Basis);

public record MlEmployeeAnalysis(
    [property: JsonPropertyName("employee_id")] string EmployeeId,
    [property: JsonPropertyName("anomaly")] bool Anomaly,
    [property: JsonPropertyName("anomaly_score")] decimal AnomalyScore,
    [property: JsonPropertyName("anomaly_reason")] string? AnomalyReason,
    [property: JsonPropertyName("trend_slope")] decimal TrendSlope,
    [property: JsonPropertyName("volatility")] decimal Volatility,
    [property: JsonPropertyName("forecast")] MlForecast? Forecast,
    [property: JsonPropertyName("data_quality")] string DataQuality,
    [property: JsonPropertyName("notes")] List<string> Notes);

public record MlAnalyzeResponse(
    [property: JsonPropertyName("employee_count")] int EmployeeCount,
    [property: JsonPropertyName("anomaly_count")] int AnomalyCount,
    [property: JsonPropertyName("model_used")] bool ModelUsed,
    [property: JsonPropertyName("model_skip_reason")] string? ModelSkipReason,
    [property: JsonPropertyName("results")] List<MlEmployeeAnalysis> Results);

/// <summary>
/// ml-01 uzerindeki performans ML katmanina baglanir.
///
/// Bu katman KARAR VERMEZ; anomali tespiti ve yorunge tahmini uretir.
/// Karar, RecommendationEngine'de kural tabanli ve denetlenebilir
/// sekilde aliniyor.
///
/// ML servisi erisilemezse oneri yine uretilir - ek sinyaller olmadan.
/// Kritik yolda degil, bilincli bir tasarim tercihi.
/// </summary>
public class PerformanceMlClient
{
    private readonly HttpClient _http;
    private readonly ILogger<PerformanceMlClient> _logger;
    private readonly string _baseUrl;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public PerformanceMlClient(HttpClient http, ILogger<PerformanceMlClient> logger)
    {
        _http = http;
        _logger = logger;
        _baseUrl = (Environment.GetEnvironmentVariable("ML_INFERENCE_URL")
            ?? "http://ml-inference:8000").TrimEnd('/');
        _http.Timeout = TimeSpan.FromSeconds(8);
    }

    public async Task<MlAnalyzeResponse?> AnalyzeAsync(object payload, CancellationToken ct)
    {
        try
        {
            var body = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var resp = await _http.PostAsync($"{_baseUrl}/performance/analyze", body, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("ML analizi basarisiz: {Status}", resp.StatusCode);
                return null;
            }

            var text = await resp.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<MlAnalyzeResponse>(text, Json);
        }
        catch (Exception ex)
        {
            // ML katmani tamamlayici; erisilemezse oneri yine uretilir.
            _logger.LogWarning(ex, "ML servisine ulasilamadi");
            return null;
        }
    }
}

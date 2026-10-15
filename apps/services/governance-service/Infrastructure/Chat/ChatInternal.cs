using System.Net.Http.Json;
using System.Text.Json;

namespace GovernanceService.Infrastructure.Chat;

/// <summary>Servisler arası iç uç çağrısının sonucu.</summary>
public sealed record InternalResult(bool Ok, int Status, JsonElement Body, string? Message)
{
    public string? Str(string name) =>
        Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>
/// Botun başka servislerin verisini değiştirmesi yalnızca o servisin iç uçlarından
/// (/api/internal/..., X-Internal-Token) yapılır: kurallar (bakiye, çakışma, durum
/// geçişleri) ve denetim kaydı tek yerde, sahibi olan serviste kalır. Gateway
/// /api/&lt;servis&gt;/internal/ yollarını dışarıya kapatır.
/// </summary>
public static class ChatInternal
{
    public static readonly string LeaveBase = Url("LEAVE_SERVICE_URL", "leave-service");
    public static readonly string EngagementBase = Url("ENGAGEMENT_SERVICE_URL", "engagement-service");
    public static readonly string TimeshiftBase = Url("TIMESHIFT_SERVICE_URL", "timeshift-service");
    public static readonly string ExpenseBase = Url("EXPENSE_SERVICE_URL", "expense-service");
    public static readonly string OnboardingBase = Url("ONBOARDING_SERVICE_URL", "onboarding-service");
    public static readonly string NotificationBase = Url("NOTIFICATION_SERVICE_URL", "notification-service");

    private static string Url(string env, string host) => EnvVar.Or(env, $"http://{host}:8080").TrimEnd('/');

    /// <summary>
    /// JSON gövdeli POST. Anahtar tanımlı değilse ya da servis ulaşılamazsa Ok=false ve
    /// kullanıcıya gösterilebilir bir ileti döner (servisin "message" alanı öncelikli).
    /// </summary>
    public static async Task<InternalResult> PostAsync(IHttpClientFactory http, string baseUrl, string path, object body, bool en, CancellationToken ct)
    {
        var token = Environment.GetEnvironmentVariable("INTERNAL_SERVICE_TOKEN");
        if (string.IsNullOrEmpty(token))
            return new(false, 0, default, en ? "INTERNAL_SERVICE_TOKEN is not set on the server; this action is disabled from chat."
                : "Sunucuda INTERNAL_SERVICE_TOKEN tanımlı değil; bu işlem sohbetten kapalı.");
        try
        {
            var client = http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + path) { Content = JsonContent.Create(body) };
            req.Headers.Add("X-Internal-Token", token);
            using var res = await client.SendAsync(req, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            JsonElement root = default;
            if (!string.IsNullOrWhiteSpace(text))
                try { root = JsonDocument.Parse(text).RootElement.Clone(); } catch (JsonException) { }
            string? msg = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            if (!res.IsSuccessStatusCode && msg is null)
                msg = en ? $"The operation could not be completed (HTTP {(int)res.StatusCode})." : $"İşlem tamamlanamadı (HTTP {(int)res.StatusCode}).";
            return new(res.IsSuccessStatusCode, (int)res.StatusCode, root, msg);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new(false, 0, default, en ? "The service could not be reached; please try again later." : "Servise ulaşılamadı; lütfen daha sonra tekrar deneyin.");
        }
    }
}

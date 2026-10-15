using System.Text.Json;

namespace LearningService.Services;

/// <summary>
/// employee-service'ten cagiranin kendi Employee.Id'sini cozer (GET /api/employees/me,
/// KeycloakUserId eslesmesi). Cagiranin JWT'si aktarilir. Istek basina onbelleklenir.
/// Sahiplik kontrolleri icin eklendi (CTO denetimi): onceden bu serviste "cagiran kim"
/// bilgisi hic yoktu, baskasinin kaydina islem yapilmasi engellenemiyordu.
/// Cozulemezse null - cagiranlar FAIL-CLOSED yorumlar.
/// </summary>
public class EmployeeDirectoryClient
{
    private readonly HttpClient _http;
    private readonly IHttpContextAccessor _ctx;
    private readonly string _baseUrl;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public EmployeeDirectoryClient(HttpClient http, IHttpContextAccessor ctx)
    {
        _http = http;
        _ctx = ctx;
        _baseUrl = (Environment.GetEnvironmentVariable("EMPLOYEE_SERVICE_URL")
            ?? "http://employee-service:8080").TrimEnd('/');
    }

    private record MeDto(Guid Id);

    public async Task<Guid?> FindMyEmployeeIdAsync(CancellationToken ct)
    {
        var ctx = _ctx.HttpContext;
        const string key = "__hr360_my_employee_id";
        if (ctx is not null && ctx.Items.TryGetValue(key, out var cached)) return (Guid?)cached;
        Guid? result = null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/employees/me");
            var auth = ctx?.Request.Headers.Authorization.ToString();
            if (!string.IsNullOrEmpty(auth)) req.Headers.TryAddWithoutValidation("Authorization", auth);
            using var resp = await _http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
                result = JsonSerializer.Deserialize<MeDto>(await resp.Content.ReadAsStringAsync(ct), Json)?.Id;
        }
        catch (Exception) { result = null; }
        if (ctx is not null) ctx.Items[key] = result;
        return result;
    }
}

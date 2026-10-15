using System.Net.Http.Headers;
using System.Text.Json;

namespace TimeShiftService.Services;

/// <summary>
/// employee-service'e cross-service HTTP cagrilari yapar. Kimlik dogrulama
/// cagirani yapan kullanicinin JWT'si AKTARILARAK (pass-through) yapilir -
/// tenant-service'teki EmployeeDirectoryClient ile ayni desen.
/// </summary>
public class EmployeeDirectoryClient
{
    private readonly HttpClient _http;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly string _baseUrl;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public EmployeeDirectoryClient(HttpClient http, IHttpContextAccessor httpContextAccessor)
    {
        _http = http;
        _httpContextAccessor = httpContextAccessor;
        _baseUrl = (Environment.GetEnvironmentVariable("EMPLOYEE_SERVICE_URL")
            ?? "http://employee-service:8080").TrimEnd('/');
    }

    private HttpRequestMessage Build(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, $"{_baseUrl}{path}");
        var incomingAuth = _httpContextAccessor.HttpContext?.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(incomingAuth))
            req.Headers.TryAddWithoutValidation("Authorization", incomingAuth);
        return req;
    }

    private record MeDto(Guid Id);

    /// <summary>
    /// Cagiranin kendi Employee.Id'si (employee-service /api/employees/me, KeycloakUserId
    /// eslesmesi). Istek basina onbelleklenir. Cozulemezse null - cagiranlar bunu
    /// FAIL-CLOSED yorumlar (baskasi adina islem yetkisi yoksa 403).
    /// </summary>
    public async Task<Guid?> FindMyEmployeeIdAsync(CancellationToken ct)
    {
        var ctx = _httpContextAccessor.HttpContext;
        const string key = "__hr360_my_employee_id";
        if (ctx is not null && ctx.Items.TryGetValue(key, out var cached)) return (Guid?)cached;
        Guid? result = null;
        try
        {
            using var resp = await _http.SendAsync(Build(HttpMethod.Get, "/api/employees/me"), ct);
            if (resp.IsSuccessStatusCode)
                result = JsonSerializer.Deserialize<MeDto>(await resp.Content.ReadAsStringAsync(ct), JsonOpts)?.Id;
        }
        catch (Exception) { result = null; }
        if (ctx is not null) ctx.Items[key] = result;
        return result;
    }

    private record AssignmentDto(Guid DepartmentId, DateOnly EffectiveFrom, DateOnly? EffectiveTo);
    private record EmployeeDto(Guid Id, List<AssignmentDto>? Assignments);

    /// <summary>
    /// Calisanin verilen tarihte AKTIF olan departmanini doner - yoksa null
    /// (calisan atanmamis ya da bulunamadi).
    /// </summary>
    public async Task<Guid?> GetActiveDepartmentAsync(Guid employeeId, DateOnly asOf, CancellationToken ct)
    {
        var resp = await _http.SendAsync(Build(HttpMethod.Get, $"/api/employees/{employeeId}"), ct);
        if (!resp.IsSuccessStatusCode) return null;

        var json = await resp.Content.ReadAsStringAsync(ct);
        var employee = JsonSerializer.Deserialize<EmployeeDto>(json, JsonOpts);
        if (employee?.Assignments is null) return null;

        var active = employee.Assignments.FirstOrDefault(a =>
            a.EffectiveFrom <= asOf && (a.EffectiveTo is null || a.EffectiveTo >= asOf));
        return active?.DepartmentId;
    }
}

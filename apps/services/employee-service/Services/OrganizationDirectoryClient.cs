using System.Text;
using System.Text.Json;

namespace EmployeeService.Services;

/// <summary>
/// organization-service'e cross-service HTTP cagrilari yapar. Kimlik
/// dogrulama, cagirani yapan kullanicinin KENDI JWT'si iletilerek yapilir
/// (pass-through) - tenant-service'teki EmployeeDirectoryClient ile ayni desen.
///
/// Kullanim alani: bir calisan departman degistirdiginde, eski departmanin
/// basi (HeadEmployeeId) O CALISANSA, artik orada calismadigi icin bas
/// olarak kalmasi mantiksiz - bu client o alani temizler.
/// </summary>
public class OrganizationDirectoryClient
{
    private readonly HttpClient _http;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly string _baseUrl;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public OrganizationDirectoryClient(HttpClient http, IHttpContextAccessor httpContextAccessor)
    {
        _http = http;
        _httpContextAccessor = httpContextAccessor;
        _baseUrl = (Environment.GetEnvironmentVariable("ORGANIZATION_SERVICE_URL")
            ?? "http://organization-service:8080").TrimEnd('/');
    }

    private HttpRequestMessage Build(HttpMethod method, string path, object? body = null)
    {
        var req = new HttpRequestMessage(method, $"{_baseUrl}{path}");
        var incomingAuth = _httpContextAccessor.HttpContext?.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(incomingAuth))
            req.Headers.TryAddWithoutValidation("Authorization", incomingAuth);
        if (body is not null)
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return req;
    }

    private record DepartmentDto(Guid Id, string Name, Guid? HeadEmployeeId);

    /// <summary>
    /// departmentId'nin basi employeeId ise HeadEmployeeId'yi null'a ceker.
    /// Baska biri bas ise ya da departman bulunamazsa hicbir sey yapmaz -
    /// bu SESSIZ bir best-effort islem, cagiran akisi (assignment olusturma)
    /// engellememeli.
    /// </summary>
    public async Task ClearHeadIfMatchesAsync(Guid departmentId, Guid employeeId, CancellationToken ct)
    {
        try
        {
            var getResp = await _http.SendAsync(Build(HttpMethod.Get, $"/api/departments/{departmentId}"), ct);
            if (!getResp.IsSuccessStatusCode) return;

            var dept = JsonSerializer.Deserialize<DepartmentDto>(
                await getResp.Content.ReadAsStringAsync(ct), JsonOpts);
            if (dept is null || dept.HeadEmployeeId != employeeId) return;

            await _http.SendAsync(Build(HttpMethod.Put, $"/api/departments/{departmentId}",
                new { name = dept.Name, headEmployeeId = (Guid?)null }), ct);
        }
        catch (Exception)
        {
            // Cross-service cagri basarisiz olsa bile assignment degisikligi
            // GECERLI kalmali - departman basi temizligi ikincil bir etki.
        }
    }
}

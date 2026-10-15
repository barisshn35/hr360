using System.Text.Json;

namespace WorkflowService.Services;

/// <summary>
/// employee-service'e cross-service HTTP cagrilari yapar. Kimlik
/// dogrulama, cagirani yapan kullanicinin KENDI JWT'si iletilerek yapilir
/// (pass-through) - employee-service'teki OrganizationDirectoryClient ile
/// ayni desen.
///
/// Kullanim alani: bir onay adimi olusturuldugunda (workflow.submitted),
/// onaycinin e-postasini bulup bildirimin gidecegi adresi belirlemek icin.
/// GetById genel yetkili (RequireHrAdmin degil) oldugu icin, talebi acan
/// "employee" rolundeki kullanicinin JWT'siyle de calisir.
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

    public record EmployeeDto(Guid Id, string FirstName, string LastName, string Email);

    /// <summary>
    /// Calisanin temel bilgilerini doner. Bulunamazsa ya da cagri
    /// basarisiz olursa null - bu SESSIZ bir best-effort islem,
    /// cagiran akisi (workflow olusturma) engellememeli.
    /// </summary>
    public async Task<EmployeeDto?> GetByIdAsync(Guid employeeId, CancellationToken ct)
    {
        try
        {
            var resp = await _http.SendAsync(Build(HttpMethod.Get, $"/api/employees/{employeeId}"), ct);
            if (!resp.IsSuccessStatusCode) return null;
            return JsonSerializer.Deserialize<EmployeeDto>(await resp.Content.ReadAsStringAsync(ct), JsonOpts);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Istegi yapan kullanicinin kendi calisan kaydinin ID'sini bulur - bir
    /// onay adiminin GERCEK atanan onaycisi mi karar verdigini dogrulamak
    /// icin kullanilir (bkz. Decide metodu). Rol yeterliligi tek basina
    /// "bu adima atanan kisi bu mu" sorusunu cevaplamaz.
    ///
    /// employee-service'teki /api/employees/me ucunu kullanir (JWT'nin
    /// KeycloakUserId'siyle eslesen kaydi doner) - onceki surum burada
    /// JWT email'ini employee.Email ile eslestiriyordu, ama bir kullanicinin
    /// Keycloak giris e-postasi ile employee kaydindaki e-postasi FARKLI
    /// olabiliyordu (orn. sirket kaydi kisisel e-postayla acilmis, Keycloak
    /// girisi kurumsal e-postayla) - bu durumda email sorgusu sessizce bos
    /// donuyor, myEmployeeId null kaliyor, self-approval kontrolu de
    /// sessizce atlaniyordu. /me, KeycloakUserId uzerinden eslestigi icin
    /// bu sorunu tasimiyor.
    /// </summary>
    public async Task<Guid?> FindMyEmployeeIdAsync(CancellationToken ct)
    {
        try
        {
            var resp = await _http.SendAsync(Build(HttpMethod.Get, "/api/employees/me"), ct);
            if (!resp.IsSuccessStatusCode) return null;
            var me = JsonSerializer.Deserialize<EmployeeDto>(await resp.Content.ReadAsStringAsync(ct), JsonOpts);
            return me?.Id;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

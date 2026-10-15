using System.Net.Http.Headers;
using System.Text.Json;

namespace TimeShiftService.Services;

/// <summary>
/// organization-service'e cross-service HTTP cagrilari yapar. Departman
/// hiyerarsisini cekip "hedef departmanin alt departmanlari" hesabini
/// yapar - ekip bir departmana bagliysa, o departmanin ALT departmanlarindaki
/// calisanlar da uye olarak eklenebilir (frontend'in zaten uyguladigi
/// mantikla ayni).
/// </summary>
public class DepartmentDirectoryClient
{
    private readonly HttpClient _http;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly string _baseUrl;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public DepartmentDirectoryClient(HttpClient http, IHttpContextAccessor httpContextAccessor)
    {
        _http = http;
        _httpContextAccessor = httpContextAccessor;
        _baseUrl = (Environment.GetEnvironmentVariable("ORGANIZATION_SERVICE_URL")
            ?? "http://organization-service:8080").TrimEnd('/');
    }

    private record DepartmentDto(Guid Id, Guid? ParentDepartmentId);

    /// <summary>
    /// rootDepartmentId ve TUM alt departmanlarinin (rekursif) id kumesini
    /// doner. organization-service'e erisilemezse SADECE rootDepartmentId
    /// donulur (guvenli varsayilan: kisit gevsemez, sadece alt departman
    /// genislemesi calismaz).
    /// </summary>
    public async Task<HashSet<Guid>> GetSubtreeIdsAsync(Guid rootDepartmentId, CancellationToken ct)
    {
        var result = new HashSet<Guid> { rootDepartmentId };

        var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/departments");
        var incomingAuth = _httpContextAccessor.HttpContext?.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(incomingAuth))
            req.Headers.TryAddWithoutValidation("Authorization", incomingAuth);

        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return result;

        var json = await resp.Content.ReadAsStringAsync(ct);
        var all = JsonSerializer.Deserialize<List<DepartmentDto>>(json, JsonOpts) ?? new();

        // BFS ile alt agaci genislet.
        var queue = new Queue<Guid>();
        queue.Enqueue(rootDepartmentId);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var child in all.Where(d => d.ParentDepartmentId == current))
            {
                if (result.Add(child.Id)) queue.Enqueue(child.Id);
            }
        }

        return result;
    }
}

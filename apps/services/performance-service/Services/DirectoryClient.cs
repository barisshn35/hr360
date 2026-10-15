using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace PerformanceService.Services;

public record TeamInfo(Guid TeamId, string Name, Guid? DepartmentId, Guid? LeadEmployeeId);
public record TeamMemberInfo(Guid EmployeeId, string? RoleInTeam, bool IsLead);
public record EmployeeInfo(Guid Id, string FirstName, string LastName, string Email);

/// <summary>
/// Diger servislerden ekip ve calisan bilgisi ceker.
///
/// Performans analizi "bu calisani ekibiyle karsilastir" diyebilmek icin
/// ekip uyeligini bilmek zorunda; ama ekipler organization-service'in,
/// calisanlar employee-service'in sorumlulugunda. Veritabani paylasimli
/// olsa da baska servisin tablosunu dogrudan okumak servis sinirini
/// bozar - bu yuzden HTTP uzerinden gidiyoruz.
///
/// Cagiranin token'i ileri tasiniyor: yetki kontrolu hedef serviste
/// yapilsin, burada ikinci bir yetki modeli olusmasin.
///
/// Ekip uyeligi seyrek degisir, bu yuzden kisa sureli bellek onbellegi
/// kullaniliyor (60 sn). Onbellek kiraci bazinda ayrilir.
/// </summary>
public class DirectoryClient
{
    private readonly HttpClient _http;
    private readonly IMemoryCache _cache;
    private readonly IHttpContextAccessor _ctx;
    private readonly ILogger<DirectoryClient> _logger;
    private readonly string _orgBase;
    private readonly string _employeeBase;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public DirectoryClient(
        HttpClient http, IMemoryCache cache,
        IHttpContextAccessor ctx, ILogger<DirectoryClient> logger)
    {
        _http = http;
        _cache = cache;
        _ctx = ctx;
        _logger = logger;
        _orgBase = (Environment.GetEnvironmentVariable("ORGANIZATION_SERVICE_URL")
            ?? "http://organization-service:8080").TrimEnd('/');
        _employeeBase = (Environment.GetEnvironmentVariable("EMPLOYEE_SERVICE_URL")
            ?? "http://employee-service:8080").TrimEnd('/');
    }

    /// <summary>Gelen istegin token'ini hedef servise aynen tasir.</summary>
    private void Forward(HttpRequestMessage req)
    {
        var auth = _ctx.HttpContext?.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrWhiteSpace(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth[7..]);
    }

    private string CacheKey(string suffix)
    {
        // Kiraci bazinda ayrilmali - aksi halde bir sirketin ekip listesi
        // digerine sizabilir.
        var raw = _ctx.HttpContext?.User?.FindFirst("organization")?.Value;
        return $"dir:{ParseOrganizationSlug(raw) ?? "?"}:{suffix}";
    }

    /// <summary>
    /// NOT: Bu claim onceden basit bir dizi ("[\"acme\"]") sanilip Trim() ile
    /// parse ediliyordu - ama Keycloak (bu projede: 25.0.6) "organization"
    /// scope'unu JSON NESNESI olarak dolduruyor, organizasyonun ADIYLA
    /// anahtarlanmis: {"acme":{}} (hardcore test sirasinda, TenantMiddleware
    /// ve MyTenantController'daki ayni koku bulunan bir hatanin parcasi
    /// olarak bulundu). Burada islevsel bir hata yaratmiyordu (cache anahtari
    /// gene de kiraci basina stabildi) ama tutarlilik icin ayni dogru parse
    /// mantigi uygulandi.
    /// </summary>
    private static string? ParseOrganizationSlug(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            switch (doc.RootElement.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var prop in doc.RootElement.EnumerateObject())
                        return prop.Name;
                    return null;
                case JsonValueKind.Array:
                    foreach (var el in doc.RootElement.EnumerateArray())
                        return el.GetString();
                    return null;
                case JsonValueKind.String:
                    return doc.RootElement.GetString();
                default:
                    return null;
            }
        }
        catch (JsonException)
        {
            return raw.Trim('[', ']', '"', ' ');
        }
    }

    private async Task<T?> GetAsync<T>(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            Forward(req);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Dizin sorgusu basarisiz {Url}: {Status}", url, resp.StatusCode);
                return default;
            }
            var body = await resp.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<T>(body, Json);
        }
        catch (Exception ex)
        {
            // Dizin erisilemezse analiz tamamen cokmemeli: ekip
            // karsilastirmasi olmadan bireysel trend yine gosterilebilir.
            _logger.LogWarning(ex, "Dizin sorgusu hatasi {Url}", url);
            return default;
        }
    }

    /// <summary>Calisanin su an uyesi oldugu ekipler.</summary>
    public async Task<IReadOnlyList<TeamInfo>> GetTeamsForEmployeeAsync(Guid employeeId, CancellationToken ct)
    {
        var key = CacheKey($"emp-teams:{employeeId}");
        if (_cache.TryGetValue(key, out IReadOnlyList<TeamInfo>? cached) && cached is not null)
            return cached;

        var raw = await GetAsync<List<TeamMembershipDto>>(
            $"{_orgBase}/api/teams/by-employee/{employeeId}", ct);

        var result = (raw ?? new List<TeamMembershipDto>())
            .Select(t => new TeamInfo(t.TeamId, t.TeamName ?? "", t.DepartmentId, t.IsLead ? employeeId : null))
            .ToList();

        _cache.Set(key, (IReadOnlyList<TeamInfo>)result, CacheTtl);
        return result;
    }

    /// <summary>Ekibin mevcut uyeleri (ekip bulunamazsa bos liste).</summary>
    public async Task<IReadOnlyList<TeamMemberInfo>> GetTeamMembersAsync(Guid teamId, CancellationToken ct)
        => await GetTeamMembersOrNullAsync(teamId, ct) ?? Array.Empty<TeamMemberInfo>();

    /// <summary>
    /// Ekibin mevcut uyeleri; ekip BULUNAMAZSA (ya da organization-service
    /// erisilemezse) null. "Ekip yok" ile "ekip var ama bos" ayrimi gereken
    /// yerler icin - bkz. AnalyticsController.Team.
    /// </summary>
    public async Task<IReadOnlyList<TeamMemberInfo>?> GetTeamMembersOrNullAsync(Guid teamId, CancellationToken ct)
    {
        var key = CacheKey($"team-members:{teamId}");
        if (_cache.TryGetValue(key, out IReadOnlyList<TeamMemberInfo>? cached) && cached is not null)
            return cached;

        var raw = await GetAsync<TeamDetailDto>($"{_orgBase}/api/teams/{teamId}", ct);
        if (raw is null) return null;

        var result = (raw.Members ?? new List<TeamMemberDto>())
            .Where(m => m.IsCurrent)
            .Select(m => new TeamMemberInfo(m.EmployeeId, m.RoleInTeam, m.IsLead))
            .ToList();

        _cache.Set(key, (IReadOnlyList<TeamMemberInfo>)result, CacheTtl);
        return result;
    }

    /// <summary>
    /// Token sahibinin calisan kaydi - employee-service /api/employees/me (KeycloakUserId
    /// eslesmesi). Istek basina onbelleklenir.
    ///
    /// NOT: Onceden kimlik e-postayla, GET /api/employees (tum liste) uzerinden
    /// cozuluyordu - bu uc duz calisanlara 403 dondugu icin "benim performansim",
    /// "hedeflerim", donem gecmisi gibi TUM calisan ekranlari duz calisanlar icin
    /// 403/404 veriyordu; ustelik e-posta eslesmesi guvenilir degil.
    /// </summary>
    public async Task<EmployeeInfo?> FindMeAsync(CancellationToken ct)
    {
        var ctx = _ctx.HttpContext;
        const string itemKey = "__hr360_perf_me";
        if (ctx is not null && ctx.Items.TryGetValue(itemKey, out var cached)) return cached as EmployeeInfo;
        var me = await GetAsync<EmployeeDto>($"{_employeeBase}/api/employees/me", ct);
        var result = me is null ? null : new EmployeeInfo(me.Id, me.FirstName ?? "", me.LastName ?? "", me.Email ?? "");
        if (ctx is not null) ctx.Items[itemKey] = result;
        return result;
    }

    public async Task<EmployeeInfo?> FindEmployeeByEmailAsync(string email, CancellationToken ct)
    {
        var key = CacheKey($"emp-email:{email.ToLowerInvariant()}");
        if (_cache.TryGetValue(key, out EmployeeInfo? cached)) return cached;

        var list = await GetAsync<List<EmployeeDto>>($"{_employeeBase}/api/employees", ct);
        var match = list?.FirstOrDefault(e =>
            string.Equals(e.Email, email, StringComparison.OrdinalIgnoreCase));

        var result = match is null
            ? null
            : new EmployeeInfo(match.Id, match.FirstName ?? "", match.LastName ?? "", match.Email ?? "");

        _cache.Set(key, result, CacheTtl);
        return result;
    }

    /// <summary>Ad-soyad cozumlemesi - grafik etiketlerinde ID yerine isim gostermek icin.</summary>
    public async Task<IReadOnlyDictionary<Guid, string>> GetEmployeeNamesAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return new Dictionary<Guid, string>();

        var key = CacheKey("emp-names");
        if (!_cache.TryGetValue(key, out Dictionary<Guid, string>? all) || all is null)
        {
            // NOT: Onceden GET /api/employees (tam liste) kullaniliyordu; bu uc duz
            // calisanlara 403 doner ve BOS sonuc kiraci anahtariyla 60 sn
            // onbellege aliniyordu - bir calisanin istegi, yoneticilerin
            // ekranlarindaki tum isimleri "—" yapiyordu. Herkese acik rehber ucu
            // kullanilir ve basarisiz yanit onbellege alinmaz.
            var list = await GetAsync<List<EmployeeDto>>($"{_employeeBase}/api/employees/directory", ct);
            all = (list ?? new List<EmployeeDto>())
                .ToDictionary(e => e.Id, e => $"{e.FirstName} {e.LastName}".Trim());
            if (list is not null) _cache.Set(key, all, CacheTtl);
        }

        return ids.Where(all.ContainsKey).ToDictionary(id => id, id => all[id]);
    }

    // --- Hedef servislerin yanit sekilleri ---
    private record TeamMembershipDto(Guid TeamId, string? TeamName, Guid? DepartmentId, bool IsLead);
    private record TeamDetailDto(Guid Id, string Name, List<TeamMemberDto>? Members);
    private record TeamMemberDto(Guid EmployeeId, string? RoleInTeam, bool IsCurrent, bool IsLead);
    private record EmployeeDto(Guid Id, string? FirstName, string? LastName, string? Email);
}

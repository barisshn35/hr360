using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TenantService.Data;

namespace TenantService.Services;

/// <summary>
/// Örnek veri (yenilik haritası #35). Boş bir şirketi denemek için örnek departman, çalışan, takdir,
/// fikir ve etkinlik oluşturur; sonra hepsini temizler. Her servis kendi verisini kendi iç ucuyla yazar
/// (X-Internal-Token): organization (departman), employee (çalışan; e-posta @ornek.invalid, olay ve
/// e-posta yok), engagement (takdir, fikir, etkinlik). Bu servis yalnızca sırayı yönetir.
/// </summary>
public class SampleDataService
{
    public const int MaxRealEmployees = 5;
    private readonly HttpClient _http;
    private readonly TenantDbContext _db;
    private readonly ILogger<SampleDataService> _log;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public SampleDataService(HttpClient http, TenantDbContext db, ILogger<SampleDataService> log) { _http = http; _db = db; _log = log; }

    private static string Url(string env, string svc) => (Environment.GetEnvironmentVariable(env) ?? $"http://{svc}:8080").TrimEnd('/');

    private async Task<JsonElement> PostAsync(string baseUrl, string path, object body, CancellationToken ct)
    {
        var token = Environment.GetEnvironmentVariable("INTERNAL_SERVICE_TOKEN");
        if (string.IsNullOrEmpty(token)) throw new SampleDataException("Sunucuda INTERNAL_SERVICE_TOKEN tanımlı değil");
        using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + path) { Content = JsonContent.Create(body, options: Json) };
        req.Headers.Add("X-Internal-Token", token);
        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogWarning("Örnek veri: {Path} {Status} {Body}", path, (int)resp.StatusCode, text.Length > 300 ? text[..300] : text);
            throw new SampleDataException($"Örnek veri oluşturulamadı ({path}, {(int)resp.StatusCode})");
        }
        return JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text).RootElement.Clone();
    }

    private Task<JsonElement> Employee(string path, object body, CancellationToken ct) => PostAsync(Url("EMPLOYEE_SERVICE_URL", "employee-service"), path, body, ct);
    private Task<JsonElement> Organization(string path, object body, CancellationToken ct) => PostAsync(Url("ORGANIZATION_SERVICE_URL", "organization-service"), path, body, ct);
    private Task<JsonElement> Engagement(string path, object body, CancellationToken ct) => PostAsync(Url("ENGAGEMENT_SERVICE_URL", "engagement-service"), path, body, ct);

    public sealed record Status(bool HasSample, int RealEmployees, int SampleEmployees, bool CanFill);

    public sealed class Row { public string TenantSlug { get; set; } = ""; public Guid[] DepartmentIds { get; set; } = []; }

    private async Task<Row?> RowAsync(string slug, CancellationToken ct) =>
        (await _db.Database.SqlQuery<Row>($"""SELECT "TenantSlug", "DepartmentIds" FROM platform_sample_data WHERE "TenantSlug" = {slug}""").ToListAsync(ct)).FirstOrDefault();

    public async Task<Status> StatusAsync(string slug, CancellationToken ct)
    {
        var s = await Employee("/api/internal/employees/sample/status", new { tenantSlug = slug }, ct);
        var real = s.GetProperty("real").GetInt32();
        var sample = s.GetProperty("sampleIds").GetArrayLength();
        var has = sample > 0 || await RowAsync(slug, ct) is not null;
        return new Status(has, real, sample, !has && real <= MaxRealEmployees);
    }

    // Örnek kişiler: ad, soyad, departman, unvan, kaç yıl önce işe girdi (ilk kişi departman başı).
    // İşe giriş en az ~100 gün önce: ilk 90 gün uyum formları (30/60/90. gün) örnek kişilere açılmasın.
    private static readonly (string First, string Last, string Dept, string Title, double Years)[] People =
    [
        ("Can", "Aydın", "Yönetim", "Genel Müdür", 8.2), ("Deniz", "Arslan", "Yönetim", "Finans Müdürü", 5.6),
        ("Burak", "Çelik", "Mühendislik", "Mühendislik Müdürü", 6.4), ("Ece", "Kurt", "Mühendislik", "Kıdemli Yazılım Mühendisi", 4.1),
        ("Mert", "Özdemir", "Mühendislik", "Yazılım Mühendisi", 2.3), ("Selin", "Koç", "Mühendislik", "Yazılım Mühendisi", 1.2),
        ("Kaan", "Şimşek", "Mühendislik", "Test Mühendisi", 0.6), ("Ada", "Yıldız", "Mühendislik", "Ürün Tasarımcısı", 3.0),
        ("Emre", "Polat", "Satış", "Satış Müdürü", 7.1), ("Zehra", "Doğan", "Satış", "Müşteri Temsilcisi", 3.4),
        ("Onur", "Kaplan", "Satış", "Müşteri Temsilcisi", 1.0), ("İrem", "Aksoy", "Satış", "Pazarlama Uzmanı", 0.35),
        ("Gizem", "Erdem", "İnsan Kaynakları", "İK Müdürü", 5.0), ("Tolga", "Güneş", "İnsan Kaynakları", "İK Uzmanı", 2.0),
        ("Nazlı", "Tekin", "İnsan Kaynakları", "Bordro Uzmanı", 1.6), ("Berk", "Aslan", "Mühendislik", "Stajyer", 0.45),
    ];

    /// <summary>E-posta yerel kısmı: Türkçe harfler ASCII'ye ("İ" küçültülünce gelen birleşik nokta da atılır).</summary>
    private static string Ascii(string s) => s.ToLowerInvariant().Replace("\u0307", "").Replace('ı', 'i').Replace('ş', 's').Replace('ğ', 'g').Replace('ü', 'u').Replace('ö', 'o').Replace('ç', 'c');

    public async Task<Status> FillAsync(string slug, string? actor, CancellationToken ct)
    {
        var st = await StatusAsync(slug, ct);
        if (!st.CanFill)
            throw new SampleDataException(st.HasSample ? "Şirkette zaten örnek veri var" : $"Örnek veri yalnızca boş şirkete eklenir (en fazla {MaxRealEmployees} çalışan)");
        var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Slug == slug, ct) ?? throw new SampleDataException("Şirket bulunamadı");

        try
        {
            var depts = await Organization("/api/internal/organization/sample", new { tenantSlug = slug, companyName = tenant.Name, names = People.Select(p => p.Dept).Distinct() }, ct);
            var deptIds = depts.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetProperty("id").GetGuid());
            var createdDepts = depts.EnumerateObject().Where(p => p.Value.GetProperty("created").GetBoolean()).Select(p => p.Value.GetProperty("id").GetGuid()).ToArray();
            // Tablo satırı önce: sonraki adımlar yarıda kalsa da temizlik açılan departmanları bilir.
            await _db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO platform_sample_data ("TenantSlug","DepartmentIds","CreatedBy","CreatedAt") VALUES ({slug}, {createdDepts}, {actor}, now())
                ON CONFLICT ("TenantSlug") DO UPDATE SET "DepartmentIds" = EXCLUDED."DepartmentIds"
                """, ct);

            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var people = People.Select(p => new
            {
                firstName = p.First, lastName = p.Last, email = $"{Ascii(p.First)}.{Ascii(p.Last)}@ornek.invalid", positionTitle = p.Title,
                departmentId = deptIds[p.Dept], hireDate = today.AddDays(-(int)(p.Years * 365.25)),
            }).ToList();
            var made = await Employee("/api/internal/employees/sample", new { tenantSlug = slug, people }, ct);
            var ids = made.GetProperty("ids").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetGuid());

            // Başı yalnızca örnek verinin açtığı departmanlara atanır; şirketin kendi departmanına dokunulmaz.
            var heads = People.GroupBy(p => p.Dept).Where(g => createdDepts.Contains(deptIds[g.Key]))
                .ToDictionary(g => deptIds[g.Key], g => ids[$"{Ascii(g.First().First)}.{Ascii(g.First().Last)}@ornek.invalid"]);
            await Organization("/api/internal/organization/sample/heads", new { tenantSlug = slug, heads }, ct);
            await Engagement("/api/internal/engagement/sample", new
            {
                tenantSlug = slug,
                people = People.Select(p => new { id = ids[$"{Ascii(p.First)}.{Ascii(p.Last)}@ornek.invalid"], name = $"{p.First} {p.Last}" }),
            }, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Yarıda kalan doldurmayı geri al: şirkette yarım örnek veri kalmasın.
            _log.LogWarning(e, "Örnek veri {Slug} için yarıda kaldı; geri alınıyor", slug);
            try { await RemoveAsync(slug, CancellationToken.None); }
            catch (Exception r) { _log.LogError(r, "Örnek veri {Slug} geri alınamadı", slug); }
            throw e as SampleDataException ?? new SampleDataException("Örnek veri oluşturulamadı; yapılanlar geri alındı");
        }
        return await StatusAsync(slug, ct);
    }

    public async Task<Status> RemoveAsync(string slug, CancellationToken ct)
    {
        var removed = await Employee("/api/internal/employees/sample/remove", new { tenantSlug = slug }, ct);
        var ids = removed.GetProperty("removed").EnumerateArray().Select(x => x.GetGuid()).ToList();
        await Engagement("/api/internal/engagement/sample/remove", new { tenantSlug = slug, employeeIds = ids }, ct);
        var deptIds = (await RowAsync(slug, ct))?.DepartmentIds ?? [];
        await Organization("/api/internal/organization/sample/remove", new { tenantSlug = slug, departmentIds = deptIds, employeeIds = ids }, ct);
        await _db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM platform_sample_data WHERE "TenantSlug" = {slug}""", ct);
        return await StatusAsync(slug, ct);
    }
}

public class SampleDataException(string message) : Exception(message);

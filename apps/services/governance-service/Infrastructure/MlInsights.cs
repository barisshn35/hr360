using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using GovernanceService.Infrastructure.Chat;

namespace GovernanceService.Infrastructure;

/// <summary>ml-inference çağrısı (ML dalgası 2): çağıranın Keycloak jetonu iletilir; ağ hatası/zaman aşımı = (0, null).</summary>
public static class MlCall
{
    public static readonly string Base = EnvVar.Or("ML_INFERENCE_URL", "http://ml-inference:8000").TrimEnd('/');

    public static async Task<(int Status, string? Body)> PostAsync(IHttpClientFactory http, string path, object body,
        string? authorization, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            var client = http.CreateClient();
            client.Timeout = timeout;
            using var req = new HttpRequestMessage(HttpMethod.Post, Base + path) { Content = JsonContent.Create(body) };
            if (!string.IsNullOrEmpty(authorization)) req.Headers.TryAddWithoutValidation("Authorization", authorization);
            using var resp = await client.SendAsync(req, ct);
            return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (0, null);
        }
    }
}

/// <summary>
/// Mevsimsellikli izin tahmini girdisi (ML dalgası 2, madde 45): ml-inference /forecast/leave-daily'ye
/// yalnızca gün başına izinli SAYISI (şirket ve departman bazında) ve departman mevcutları gider;
/// çalışan kimliği gitmez. 5'ten küçük departmanlar ML tarafında "Diğer"de birleşir.
/// </summary>
public static class LeaveForecastPayload
{
    public const int HistoryDays = 730;
    public const string NoDepartment = "Departmansız";

    public sealed record DayDept(DateOnly Day, Guid? DepartmentId, long Count);

    /// <summary>Saf: ML istek gövdesi. Departman adı yoksa "Departmansız".</summary>
    public static object Build(DateOnly historyStart, DateOnly historyEnd, IReadOnlyList<Person> people, IEnumerable<DayDept> days,
        IEnumerable<DateOnly> holidays, int weeks)
    {
        var deptName = people.Where(p => p.DepartmentId is not null)
            .GroupBy(p => p.DepartmentId!.Value).ToDictionary(g => g.Key, g => g.First().Department ?? NoDepartment);
        string Name(Guid? id) => id is { } d && deptName.TryGetValue(d, out var n) ? n : NoDepartment;
        var list = days.Where(d => d.Day >= historyStart && d.Day <= historyEnd && d.Count > 0).ToList();
        var teams = people.GroupBy(p => p.DepartmentId is null ? NoDepartment : Name(p.DepartmentId))
            .Select(g => new
            {
                team = g.Key,
                headcount = g.Count(),
                days = list.Where(d => Name(d.DepartmentId) == g.Key).GroupBy(d => d.Day)
                    .Select(x => new { date = x.Key.ToString("yyyy-MM-dd"), absent = x.Sum(y => y.Count) }).OrderBy(x => x.date).ToList(),
            }).OrderBy(t => t.team, StringComparer.Ordinal).ToList();
        return new
        {
            history_start = historyStart.ToString("yyyy-MM-dd"),
            history_end = historyEnd.ToString("yyyy-MM-dd"),
            headcount = Math.Max(1, people.Count),
            days = list.GroupBy(d => d.Day).OrderBy(g => g.Key)
                .Select(g => new { date = g.Key.ToString("yyyy-MM-dd"), absent = g.Sum(x => x.Count) }).ToList(),
            teams,
            holidays = holidays.Distinct().OrderBy(d => d).Select(d => d.ToString("yyyy-MM-dd")).ToList(),
            weeks = Math.Clamp(weeks, 1, 26),
        };
    }
}

/// <summary>
/// Anlamsal arama külliyatı (ML dalgası 2, madde 50): bilgi bankası maddeleri, doküman kütüphanesinin
/// güncel sürümleri ve yayımdaki duyurular. Dizin ml-inference'ta kiracı başına bellekte tutulur;
/// külliyat anahtarı (ml-inference ile AYNI algoritma) değişince dizin yeniden kurulur. Görünürlük
/// süzgeci BURADA uygulanır: kullanıcı göremeyeceği belgeyi sonuçta hiçbir şekilde görmez.
/// </summary>
public static class SemanticCorpus
{
    public const int MaxTitle = 300;
    public const int MaxText = 200_000;
    public const int MaxDocs = 5000;

    public sealed record Doc(string Id, string Source, Guid SourceId, string Title, string Text, string Hash,
        string Audience, Guid[] DepartmentIds, bool Archived);

    public static string Sha256Hex(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    /// <summary>Belge özeti: SHA-256(başlık + "\n" + metin) — Postgres'te aynı ifade hesaplanır (bkz. SQL).</summary>
    public static string DocHash(string title, string text) => Sha256Hex(title + "\n" + text);

    /// <summary>Külliyat anahtarı: "kimlik:özet" satırlarının sıralı (ordinal) birleşiminin SHA-256'sı.</summary>
    public static string CorpusKey(IEnumerable<(string Id, string Hash)> docs) =>
        Sha256Hex(string.Join("\n", docs.Select(d => $"{d.Id}:{d.Hash}").OrderBy(x => x, StringComparer.Ordinal)));

    /// <summary>Kullanıcı bu belgeyi görebilir mi? (doküman kütüphanesi ve duyurulardaki kuralların aynısı)</summary>
    public static bool Visible(Doc d, bool hr, bool manager, Guid? myDepartment) => d.Source switch
    {
        "kb" => true,
        "library" => hr || (!d.Archived && (d.Audience == "All" || (d.Audience == "Managers" && manager)
                                          || (d.Audience == "Departments" && myDepartment is { } m && d.DepartmentIds.Contains(m)))),
        "announcement" => hr || d.Audience == "All" || (d.Audience == "Departments" && myDepartment is { } m2 && d.DepartmentIds.Contains(m2)),
        _ => false,
    };

    /// <summary>
    /// Tüm kaynakların ortak sorgusu. Başlık/metin ML sınırlarına göre kısaltılır ve özet bu kısaltılmış
    /// metinden Postgres'te (sha256) hesaplanır; metin yalnızca ML dizini istediğinde (409) okunur.
    /// $1 = kiracı, $2 = metin dahil mi.
    /// </summary>
    public const string Sql = """
        WITH src AS (
            SELECT 'kb:' || k."Id" AS id, 'kb' AS source, k."Id" AS sid, left(k."Title", 300) AS title, left(k."Body", 200000) AS body,
                   'All' AS audience, '{}'::uuid[] AS depts, false AS archived, k."UpdatedAt" AS at
            FROM governance_kb_articles k WHERE k."TenantSlug" = $1
            UNION ALL
            SELECT 'lib:' || d."Id", 'library', d."Id", left(d."Title", 300), left(coalesce(v."Body", ''), 200000),
                   d."Audience", d."DepartmentIds", d."Archived", d."UpdatedAt"
            FROM governance_library_documents d JOIN governance_library_versions v ON v."Id" = d."CurrentVersionId"
            WHERE d."TenantSlug" = $1
            UNION ALL
            SELECT 'ann:' || a."Id", 'announcement', a."Id", left(a."Title", 300), left(a."Body", 200000),
                   a."Audience", a."DepartmentIds", false, a."PublishAt"
            FROM governance_announcements a
            WHERE a."TenantSlug" = $1 AND a."PublishAt" <= now() AND (a."ExpireAt" IS NULL OR a."ExpireAt" > now())
        )
        SELECT id, source, sid, title, CASE WHEN $2 THEN body ELSE '' END,
               encode(sha256(convert_to(title || E'\n' || body, 'UTF8')), 'hex'), audience, depts, archived
        FROM src ORDER BY at DESC LIMIT 5000
        """;
}

/// <summary>
/// Şirket beceri haritası girdisi (ML dalgası 2, madde 47): kişi başına (kimliksiz) departman + beceri
/// listesi. Beceriler çalışanın profilinde kendisinin girdiği beceriler ve yetkinlik değerlendirmelerinde
/// en son seviyesi 3 ve üstü olan yetkinliklerdir. 5'ten küçük grup/hücre ML tarafında gizlenir.
/// </summary>
public static class SkillGraphPayload
{
    public const int MinCompetencyLevel = 3;

    public static object Build(IReadOnlyList<Person> people, IReadOnlyDictionary<Guid, string[]> profileSkills,
        IReadOnlyDictionary<Guid, List<string>> competencies) => new
    {
        people = people.Select(p => new
        {
            department = p.Department,
            skills = (profileSkills.TryGetValue(p.Id, out var s) ? s : Array.Empty<string>())
                .Concat(competencies.TryGetValue(p.Id, out var c) ? c : new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(200).ToList(),
        }).ToList(),
    };
}

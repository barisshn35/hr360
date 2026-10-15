using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using GovernanceService.Infrastructure;

namespace GovernanceService.Controllers;

/* ======================================================================
 * ML dalgası 2 — governance üzerinden çalışan toplu analizler:
 *   45) mevsimsellikli izin tahmini + ekip kapasitesi (yönetici ve üstü; toplu sayılar)
 *   47) şirket beceri haritası (İK; 5'ten küçük grup yok)
 *   50) politika / bilgi bankası / duyurularda anlamsal arama (herkes; görünürlük süzgeçli)
 * Hepsi ml-inference'a çağıranın jetonuyla gider; kişi bazında tahmin ya da karar yoktur.
 * ==================================================================== */

[Route("api/analytics/leave-forecast")]
[Authorize(Policy = "RequireManagerOrAbove")]
public class LeaveForecastController : AppController
{
    private readonly IHttpClientFactory _http;
    public LeaveForecastController(IHttpClientFactory http) => _http = http;

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int weeks = 12, CancellationToken ct = default)
    {
        weeks = Math.Clamp(weeks, 1, 26);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
        var end = today.AddDays(-1);
        var start = end.AddDays(-LeaveForecastPayload.HistoryDays);
        var people = await People.ListAsync(Tenant, ct);
        var days = await Db.QueryAsync("""
            SELECT g::date, a."DepartmentId", count(DISTINCT r."EmployeeId")
            FROM leave_requests r
            CROSS JOIN LATERAL generate_series(greatest(r."StartDate", $2), least(r."EndDate", $3), interval '1 day') g
            LEFT JOIN LATERAL (
                SELECT x."DepartmentId" FROM employee_assignments x
                WHERE x."EmployeeId" = r."EmployeeId" AND x."EffectiveFrom" <= current_date
                  AND (x."EffectiveTo" IS NULL OR x."EffectiveTo" >= current_date)
                ORDER BY x."EffectiveFrom" DESC LIMIT 1) a ON true
            WHERE r."TenantSlug" = $1 AND r."Status" = 'Approved' AND r."StartDate" <= $3 AND r."EndDate" >= $2
            GROUP BY 1, 2
            """, r => new LeaveForecastPayload.DayDept(r.GetFieldValue<DateOnly>(0), r.GuidOrNull(1), r.GetInt64(2)), ct, Tenant, start, end);
        var holidays = await Db.QueryAsync("""
            SELECT "Date" FROM leave_public_holidays WHERE "TenantSlug" = $1 AND "Date" BETWEEN $2 AND $3
            """, r => r.GetFieldValue<DateOnly>(0), ct, Tenant, start, today.AddDays(7 * (weeks + 2)));
        if (people.Count == 0) return Ok(new { available = false, reason = L("Aktif çalışan yok.", "No active employees.") });
        if (days.Count == 0) return Ok(new { available = false, reason = L("Son iki yılda onaylı izin kaydı yok.", "No approved leave in the last two years.") });
        var body = LeaveForecastPayload.Build(start, end, people, days, holidays, weeks);
        var (status, text) = await MlCall.PostAsync(_http, "/forecast/leave-daily", body, Request.Headers.Authorization.ToString(), TimeSpan.FromSeconds(30), ct);
        if (status == 0 || text is null) return StatusCode(503, new { message = L("Model servisine ulaşılamadı.", "The model service is unreachable.") });
        if (status != 200) return StatusCode(status is 401 or 403 ? 403 : 502, new { message = L("İzin tahmini yapılamadı.", "The leave forecast could not be computed.") });
        using var doc = JsonDocument.Parse(text);
        return Ok(new { available = true, historyStart = start, historyEnd = end, forecast = doc.RootElement.Clone() });
    }
}

[Route("api/skills/graph")]
[Authorize(Policy = "RequireHrAdmin")]
public class SkillGraphController : AppController
{
    private readonly IHttpClientFactory _http;
    public SkillGraphController(IHttpClientFactory http) => _http = http;

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var people = await People.ListAsync(Tenant, ct);
        var profile = (await Db.QueryAsync("""
            SELECT "EmployeeId", "Skills" FROM engagement_profiles WHERE "TenantSlug" = $1 AND cardinality("Skills") > 0
            """, r => (Id: r.GetGuid(0), Skills: r.GetFieldValue<string[]>(1)), ct, Tenant))
            .GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First().Skills);
        var comps = new Dictionary<Guid, List<string>>();
        try
        {
            foreach (var (emp, name) in await Db.QueryAsync("""
                SELECT DISTINCT ON (a."EmployeeId", a."CompetencyId") a."EmployeeId", c."Name", a."Level"
                FROM learning_competency_assessments a JOIN learning_competencies c ON c."Id" = a."CompetencyId" AND c."IsActive"
                WHERE a."TenantSlug" = $1
                ORDER BY a."EmployeeId", a."CompetencyId", a."AssessedAt" DESC
                """, r => (Emp: r.GetGuid(0), Name: r.GetInt32(2) >= SkillGraphPayload.MinCompetencyLevel ? r.GetString(1) : null), ct, Tenant))
            {
                if (name is null) continue;
                if (!comps.TryGetValue(emp, out var l)) comps[emp] = l = new List<string>();
                l.Add(name);
            }
        }
        catch (Npgsql.PostgresException e) when (e.SqlState == "42P01") { /* yetkinlik modülü tabloları yok */ }
        var body = SkillGraphPayload.Build(people, profile, comps);
        var (status, text) = await MlCall.PostAsync(_http, "/skills/graph", body, Request.Headers.Authorization.ToString(), TimeSpan.FromSeconds(30), ct);
        if (status == 0 || text is null) return StatusCode(503, new { message = L("Model servisine ulaşılamadı.", "The model service is unreachable.") });
        if (status != 200) return StatusCode(status is 401 or 403 ? 403 : 502, new { message = L("Beceri haritası oluşturulamadı.", "The skill map could not be built.") });
        await ComplianceAudit.WriteAsync(Db, Tenant, "SkillGraph", "company", "SensitiveViewed", new { field = "skillGraph", people = people.Count }, Me.UserId, Me.Name, ct);
        using var doc = JsonDocument.Parse(text);
        return Ok(doc.RootElement.Clone());
    }
}

[Route("api/search/semantic")]
[Authorize]
public class SemanticSearchController : AppController
{
    private readonly IHttpClientFactory _http;
    public SemanticSearchController(IHttpClientFactory http) => _http = http;

    private async Task<List<SemanticCorpus.Doc>> CorpusAsync(bool withText, CancellationToken ct) =>
        await Db.QueryAsync(SemanticCorpus.Sql, r => new SemanticCorpus.Doc(
            r.GetString(0), r.GetString(1), r.GetGuid(2), r.GetString(3), r.GetString(4), r.GetString(5),
            r.GetString(6), r.GetFieldValue<Guid[]>(7), r.GetBoolean(8)), ct, Tenant, withText);

    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] string? q, [FromQuery] int k = 10, CancellationToken ct = default)
    {
        q = (q ?? "").Trim();
        if (q.Length < 2) return Ok(new { hits = Array.Empty<object>() });
        if (q.Length > 300) return BadRequest(new { message = L("Arama ifadesi en fazla 300 karakter olabilir.", "Query too long.") });
        k = Math.Clamp(k, 1, 30);
        var docs = await CorpusAsync(false, ct);
        if (docs.Count == 0) return Ok(new { hits = Array.Empty<object>(), documents = 0 });
        var key = SemanticCorpus.CorpusKey(docs.Select(d => (d.Id, d.Hash)));
        var auth = Request.Headers.Authorization.ToString();
        // Görünürlük süzgecinden sonra k sonuç kalsın diye fazlası istenir.
        var want = Math.Min(50, k * 3);
        var (status, text) = await MlCall.PostAsync(_http, "/semantic/search", new { query = q, k = want, corpus_key = key }, auth, TimeSpan.FromSeconds(15), ct);
        if (status == 409)
        {
            // Dizin yok / eski (ML yeniden başladı ya da belge değişti): metinlerle yeniden kurdurulur.
            var full = await CorpusAsync(true, ct);
            (status, text) = await MlCall.PostAsync(_http, "/semantic/search",
                new { query = q, k = want, docs = full.Select(d => new { id = d.Id, title = d.Title, text = d.Text }) }, auth, TimeSpan.FromSeconds(60), ct);
        }
        if (status == 0 || text is null) return StatusCode(503, new { message = L("Anlamsal arama şu anda kullanılamıyor.", "Semantic search is unavailable.") });
        if (status != 200) return StatusCode(status is 401 or 403 ? 403 : 502, new { message = L("Anlamsal arama yapılamadı.", "Semantic search failed.") });

        var me = Me.IsHr ? null : await MyPersonAsync(ct);
        var byId = docs.ToDictionary(d => d.Id);
        using var doc = JsonDocument.Parse(text);
        var hits = new List<object>();
        foreach (var h in doc.RootElement.GetProperty("hits").EnumerateArray())
        {
            if (!byId.TryGetValue(h.GetProperty("id").GetString() ?? "", out var d)) continue;
            if (!SemanticCorpus.Visible(d, Me.IsHr, Me.IsManager, me?.DepartmentId)) continue;
            hits.Add(new
            {
                id = d.SourceId, source = d.Source, title = d.Title,
                snippet = h.GetProperty("snippet").GetString(), score = h.GetProperty("score").GetDouble(),
            });
            if (hits.Count == k) break;
        }
        var method = doc.RootElement.TryGetProperty("method", out var m) ? m.GetString() : null;
        return Ok(new { hits, documents = docs.Count, method });
    }
}

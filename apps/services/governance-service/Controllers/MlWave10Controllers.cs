using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using GovernanceService.Infrastructure;
using GovernanceService.Infrastructure.Chat;

namespace GovernanceService.Controllers;

/// <summary>ML servisine çağıranın jetonuyla (gerekirse servisler arası anahtarla) istek.</summary>
internal static class MlHttp
{
    public static readonly string Base = EnvVar.Or("ML_INFERENCE_URL", "http://ml-inference:8000").TrimEnd('/');

    public static async Task<(int Status, string Body)> SendAsync(IHttpClientFactory http, HttpRequest incoming, HttpMethod method, string path, object? body,
        CancellationToken ct, int timeoutSeconds = 60, bool internalToken = false)
    {
        var client = http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
        using var req = new HttpRequestMessage(method, $"{Base}{path}");
        if (body is not null) req.Content = JsonContent.Create(body);
        req.Headers.TryAddWithoutValidation("Authorization", incoming.Headers.Authorization.ToString());
        if (internalToken && Environment.GetEnvironmentVariable("INTERNAL_SERVICE_TOKEN") is { Length: > 0 } tok)
            req.Headers.TryAddWithoutValidation(Security.InternalServiceToken.Header, tok);
        using var resp = await client.SendAsync(req, ct);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
    }

    public static string? Detail(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("detail", out var d))
                return d.ValueKind == JsonValueKind.String ? d.GetString()
                    : d.ValueKind == JsonValueKind.Object && d.TryGetProperty("message", out var m) ? m.GetString() : null;
        }
        catch (JsonException) { }
        return null;
    }
}

/* ======================================================================
 * Madde 36: devir modelinin kiracı verisiyle eğitimi. Kiracı açık izin verir
 * (varsayılan kapalı); governance toplu ve kimliksiz satırları çıkarır, zaman
 * bazlı doğrulama kümesiyle ml-inference'a gönderir. Aday model paylaşılan
 * modelin yayındaki sürümünü DEĞİŞTİRMEZ: champion/challenger onayı (platform
 * yöneticisi) gerekir.
 * ==================================================================== */
[Route("api/model/tenant-training")]
[Authorize(Policy = "RequireHrAdmin")]
public class TenantTrainingController : AppController
{
    private readonly IHttpClientFactory _http;
    public TenantTrainingController(IHttpClientFactory http) => _http = http;

    private const string Model = ModelGovernanceController.AttritionModel;

    private async Task<(bool Enabled, string? By, DateTime? At, DateTime? LastAt, JsonElement? Last)?> SettingsAsync(CancellationToken ct)
    {
        try
        {
            var rows = await Db.QueryAsync("""
                SELECT "TrainOnTenantData","TrainConsentBy","TrainConsentAt","LastTenantTrainingAt","LastTenantTraining"::text
                FROM governance_ml_model_settings WHERE "TenantSlug" = $1 AND "ModelName" = $2
                """, r => (r.GetBoolean(0), r.Str(1), r.Ts(2), r.Ts(3), r.Str(4) is { } j ? JsonDocument.Parse(j).RootElement.Clone() : (JsonElement?)null),
                ct, Tenant, Model);
            return rows.Count == 0 ? (false, null, null, null, null) : rows[0];
        }
        catch (Npgsql.PostgresException e) when (e.SqlState is "42P01" or "42703") { return null; }
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var s = await SettingsAsync(ct);
        if (s is null) return StatusCode(503, new { message = L("Veritabanı güncellemesi (2026-10-23_kvkk_ml.sql) uygulanmalı.", "Apply the database update (2026-10-23_kvkk_ml.sql)."), code = "migration_missing" });
        var (train, eval) = AttritionTraining.Snapshots(DateOnly.FromDateTime(DateTime.UtcNow));
        return Ok(new
        {
            enabled = s.Value.Enabled, consentBy = s.Value.By, consentAt = s.Value.At, lastTrainingAt = s.Value.LastAt, lastTraining = s.Value.Last,
            canConsent = Me.Roles.Contains("tenant-admin") || Me.IsPlatformAdmin,
            thresholds = new
            {
                minTrainRows = AttritionTraining.MinTrainRows, minTrainLeavers = AttritionTraining.MinTrainLeavers,
                minEvalRows = AttritionTraining.MinEvalRows, minEvalLeavers = AttritionTraining.MinEvalLeavers,
            },
            trainSnapshot = train, evalSnapshot = eval, features = AttritionTraining.Features,
        });
    }

    public record ConsentInput(bool Enabled);

    /// <summary>İzin şirket kararıdır: yalnızca şirket yöneticisi (tenant-admin) verir ya da geri alır.</summary>
    [HttpPut("consent")]
    public async Task<IActionResult> SetConsent(ConsentInput body, CancellationToken ct)
    {
        if (!Me.Roles.Contains("tenant-admin") && !Me.IsPlatformAdmin)
            return StatusCode(403, new { message = L("Şirket verisiyle eğitim iznini yalnızca şirket yöneticisi verebilir.", "Only a company administrator can grant training consent.") });
        if (await SettingsAsync(ct) is null) return StatusCode(503, new { message = L("Veritabanı güncellemesi uygulanmalı.", "Apply the database update."), code = "migration_missing" });
        await Db.ExecuteAsync("""
            INSERT INTO governance_ml_model_settings ("Id","TenantSlug","ModelName","RiskThreshold","UpdatedBy","UpdatedByName","UpdatedAt","TrainOnTenantData","TrainConsentBy","TrainConsentAt")
            VALUES ($1,$2,$3,0.5,$4,$5,now(),$6,$5,now())
            ON CONFLICT ("TenantSlug","ModelName") DO UPDATE SET "TrainOnTenantData" = EXCLUDED."TrainOnTenantData",
                "TrainConsentBy" = EXCLUDED."TrainConsentBy", "TrainConsentAt" = now()
            """, ct, Guid.NewGuid(), Tenant, Model, Me.UserId, Me.Name, body.Enabled);
        await ComplianceAudit.WriteAsync(Db, Tenant, "AttritionModel", Model, body.Enabled ? "TenantTrainingConsentGranted" : "TenantTrainingConsentRevoked",
            new { enabled = body.Enabled }, Me.UserId, Me.Name, ct);
        return await Get(ct);
    }

    private async Task<(DateOnly Train, DateOnly Eval, AttritionTraining.Prepared T, AttritionTraining.Prepared E)> BuildAsync(CancellationToken ct)
    {
        var (t1, t2) = AttritionTraining.Snapshots(DateOnly.FromDateTime(DateTime.UtcNow));
        var seed = Random.Shared.Next();
        var train = AttritionTraining.Prepare(await AttritionTraining.ExtractAsync(Db, Tenant, t1, ct), seed);
        var eval = AttritionTraining.Prepare(await AttritionTraining.ExtractAsync(Db, Tenant, t2, ct), seed + 1);
        return (t1, t2, train, eval);
    }

    private static object Summary(DateOnly t1, DateOnly t2, AttritionTraining.Prepared train, AttritionTraining.Prepared eval, List<string> refusals) => new
    {
        trainSnapshot = t1, evalSnapshot = t2,
        train = new { rows = train.Rows.Count, leavers = train.Leavers, imputed = train.Imputed },
        eval = new { rows = eval.Rows.Count, leavers = eval.Leavers, imputed = eval.Imputed },
        ok = refusals.Count == 0, refusals,
    };

    /// <summary>Kuru çalıştırma: kaç satır/ayrılan çıkar, eşikler sağlanıyor mu. Hiçbir şey gönderilmez.</summary>
    [HttpGet("preview")]
    public async Task<IActionResult> Preview(CancellationToken ct)
    {
        var (t1, t2, train, eval) = await BuildAsync(ct);
        return Ok(Summary(t1, t2, train, eval, AttritionTraining.Refusals(train, eval)));
    }

    [HttpPost("run")]
    public async Task<IActionResult> Run(CancellationToken ct)
    {
        var s = await SettingsAsync(ct);
        if (s is null) return StatusCode(503, new { message = L("Veritabanı güncellemesi uygulanmalı.", "Apply the database update."), code = "migration_missing" });
        if (!s.Value.Enabled)
            return Conflict(new { code = "no_consent", message = L("Şirket verisiyle eğitim izni verilmedi (Ayarlar: \"Modeli şirket verisiyle eğit\").", "Training on company data has not been consented.") });
        if (s.Value.LastAt is { } last && last > DateTime.UtcNow.AddHours(-24))
            return StatusCode(429, new { message = L("Şirket verisiyle eğitim günde en çok bir kez çalıştırılabilir.", "Training on company data can run at most once a day.") });
        var (t1, t2, train, eval) = await BuildAsync(ct);
        var refusals = AttritionTraining.Refusals(train, eval);
        if (refusals.Count > 0)
            return UnprocessableEntity(new { code = "insufficient_data", message = L("Veri yetersiz; eğitim yapılmadı.", "Insufficient data; training refused."), summary = Summary(t1, t2, train, eval, refusals) });
        var payload = new
        {
            source = "rows", rows = train.Rows, evaluation_rows = eval.Rows,
            data_window = new { start = t1.ToString("yyyy-MM-dd"), end = DateTime.UtcNow.ToString("yyyy-MM-dd") },
            provenance = new
            {
                kind = "tenant-consented", tenant_ref = AttritionTraining.TenantRef(Tenant), consent_at = s.Value.At ?? DateTime.UtcNow,
                validation = "time-based", train_snapshot = t1.ToString("yyyy-MM-dd"), eval_snapshot = t2.ToString("yyyy-MM-dd"),
            },
        };
        int status; string text;
        try { (status, text) = await MlHttp.SendAsync(_http, Request, HttpMethod.Post, "/model/retrain", payload, ct, 300, internalToken: true); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(503, new { message = L("Model servisine ulaşılamadı.", "The model service is unreachable.") });
        }
        if (status != 200)
            return StatusCode(status is 409 or 422 ? status : 502, new { message = MlHttp.Detail(text) ?? L("Model eğitilemedi.", "The model could not be trained.") });
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var audit = root.TryGetProperty("audit", out var a) ? a : default;
        var action = audit.ValueKind == JsonValueKind.Object && audit.TryGetProperty("action", out var ac) ? ac.GetString() ?? "Retrained" : "Retrained";
        var entityId = audit.ValueKind == JsonValueKind.Object && audit.TryGetProperty("entityId", out var ei) ? ei.GetString() : Model;
        var result = new
        {
            at = DateTime.UtcNow, candidateVersion = root.TryGetProperty("candidate_version", out var cv) ? cv.GetString() : null,
            awaitingApproval = root.TryGetProperty("awaiting_approval", out var aw) && aw.GetBoolean(),
            promoted = root.TryGetProperty("promoted", out var pr) && pr.GetBoolean(),
            candidate = root.TryGetProperty("candidate", out var cm) ? cm.Clone() : (JsonElement?)null,
            current = root.TryGetProperty("current", out var cu) ? cu.Clone() : (JsonElement?)null,
            reason = root.TryGetProperty("decision", out var de) && de.TryGetProperty("reason", out var rs) ? rs.GetString() : null,
            summary = Summary(t1, t2, train, eval, refusals),
        };
        await Db.ExecuteAsync("""
            UPDATE governance_ml_model_settings SET "LastTenantTrainingAt" = now(), "LastTenantTraining" = $3::jsonb
            WHERE "TenantSlug" = $1 AND "ModelName" = $2
            """, ct, Tenant, Model, JsonSerializer.Serialize(result));
        await ComplianceAudit.WriteAsync(Db, Tenant, "AttritionModel", entityId ?? Model, action,
            new { tenantData = true, result.candidateVersion, result.awaitingApproval, result.promoted, trainRows = train.Rows.Count, trainLeavers = train.Leavers,
                  evalRows = eval.Rows.Count, evalLeavers = eval.Leavers, trainSnapshot = t1, evalSnapshot = t2 }, Me.UserId, Me.Name, ct);
        return Ok(result);
    }
}

/* ======================================================================
 * Madde 49: "Sana uygun" — iç ilan, mentor ve eğitim önerileri. Yalnızca
 * oturumdaki kişinin kendisi için; ML'e takma adlı girdi gider (mentor/ilan
 * opak kimlikle). Hiçbir işlem otomatik yapılmaz.
 * ==================================================================== */
[Route("api/growth/recommendations")]
[Authorize]
public class GrowthRecommendationsController : AppController
{
    private readonly IHttpClientFactory _http;
    public GrowthRecommendationsController(IHttpClientFactory http) => _http = http;

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var me = await MyPersonAsync(ct);
        if (me is null) return Ok(new { postings = Array.Empty<object>(), mentors = Array.Empty<object>(), courses = Array.Empty<object>(), gaps = Array.Empty<object>(), linked = false });
        async Task<List<T>> Safe<T>(Func<Task<List<T>>> f)
        {
            try { return await f(); } catch (Npgsql.PostgresException e) when (e.SqlState is "42P01" or "42703") { return new(); }
        }
        var skills = (await Safe(() => Db.QueryAsync("""SELECT "Skills" FROM engagement_profiles WHERE "TenantSlug" = $1 AND "EmployeeId" = $2""",
            r => r.IsDBNull(0) ? Array.Empty<string>() : r.GetFieldValue<string[]>(0), ct, Tenant, me.Id))).FirstOrDefault() ?? Array.Empty<string>();
        // Yetkinlik açığı: pozisyon unvanı ya da departman rol profilindeki beklenen seviye − son değerlendirme.
        var gaps = await Safe(() => Db.QueryAsync("""
            SELECT c."Id", c."Name", max(p."RequiredLevel"),
                   coalesce((SELECT a."Level" FROM learning_competency_assessments a WHERE a."TenantSlug" = $1 AND a."EmployeeId" = $2 AND a."CompetencyId" = c."Id"
                             ORDER BY a."AssessedAt" DESC LIMIT 1), 0)
            FROM learning_role_profiles p JOIN learning_competencies c ON c."Id" = p."CompetencyId" AND c."IsActive"
            WHERE p."TenantSlug" = $1 AND ((p."PositionTitle" IS NOT NULL AND lower(p."PositionTitle") = lower($3)) OR p."DepartmentId" = $4)
            GROUP BY c."Id", c."Name"
            """, r => new { id = r.GetGuid(0).ToString(), name = r.GetString(1), required = r.GetInt32(2), current = r.GetInt32(3) },
            ct, Tenant, me.Id, me.Position ?? "", me.DepartmentId ?? Guid.Empty));
        var completed = await Safe(() => Db.QueryAsync("""
            SELECT "CourseId"::text FROM learning_enrollments WHERE "TenantSlug" = $1 AND "EmployeeId" = $2 AND "Status" = 'Completed'
            """, r => r.GetString(0), ct, Tenant, me.Id));
        var postings = await Safe(() => Db.QueryAsync("""
            SELECT p."Id", p."Title", coalesce(p."Description", ''), d."Name" FROM recruitment_job_postings p
            LEFT JOIN organization_departments d ON d."Id" = p."DepartmentId"
            WHERE p."TenantSlug" = $1 AND p."Status" = 'Published' ORDER BY p."PublishedAt" DESC NULLS LAST LIMIT 300
            """, r => (Id: r.GetGuid(0), Title: r.GetString(1), Text: r.GetString(2), Dept: r.Str(3)), ct, Tenant));
        var mentors = await Safe(() => Db.QueryAsync("""
            SELECT m."Id", m."UserId", m."PersonName", m."Offers", m."Capacity",
                   (SELECT count(*)::int FROM engagement_mentorships x WHERE x."TenantSlug" = $1 AND x."MentorUserId" = m."UserId" AND x."Status" = 'Active'),
                   m."EmployeeId"
            FROM engagement_mentor_profiles m WHERE m."TenantSlug" = $1 AND m."IsMentor" AND m."UserId" <> $2
            """, r => (Id: r.GetGuid(0), UserId: r.GetString(1), Name: r.GetString(2), Offers: r.IsDBNull(3) ? Array.Empty<string>() : r.GetFieldValue<string[]>(3),
                Capacity: r.GetInt32(4), Active: r.GetInt32(5), Emp: r.GuidOrNull(6)), ct, Tenant, Me.UserId));
        var courses = await Safe(() => Db.QueryAsync("""
            SELECT c."Id", c."Title", c."Description", c."Category",
                   coalesce((SELECT json_agg(json_build_object('id', cc."CompetencyId"::text, 'target_level', cc."TargetLevel"))::text
                             FROM learning_course_competencies cc WHERE cc."CourseId" = c."Id"), '[]')
            FROM learning_courses c WHERE c."TenantSlug" = $1 AND c."IsActive" LIMIT 1000
            """, r => (Id: r.GetGuid(0), Title: r.GetString(1), Desc: r.Str(2), Cat: r.Str(3), Comps: r.GetString(4)), ct, Tenant));

        var people = (await People.ListAsync(Tenant, ct)).ToDictionary(p => p.Id);
        var payload = new
        {
            me = new { skills, position = me.Position, gaps = gaps.Select(g => new { g.id, g.name, required = Math.Clamp(g.required, 1, 5), current = Math.Clamp(g.current, 0, 5) }),
                       completed_course_ids = completed },
            postings = postings.Select(p => new { id = p.Id.ToString(), title = p.Title.Length > 300 ? p.Title[..300] : p.Title, text = p.Text.Length > 50000 ? p.Text[..50000] : p.Text }),
            mentors = mentors.Select(m => new { id = m.Id.ToString(), offers = m.Offers.Take(50), free_slots = Math.Clamp(m.Capacity - m.Active, 0, 20) }),
            courses = courses.Select(c => new
            {
                id = c.Id.ToString(), title = c.Title.Length > 300 ? c.Title[..300] : c.Title, description = c.Desc is { Length: > 10000 } d ? d[..10000] : c.Desc,
                category = c.Cat, competencies = JsonDocument.Parse(c.Comps).RootElement.Clone(),
            }),
            limit = 5,
        };
        int status; string text;
        try { (status, text) = await MlHttp.SendAsync(_http, Request, HttpMethod.Post, "/skills/recommend", payload, ct, 30); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(503, new { message = L("Öneri servisine ulaşılamadı.", "The recommendation service is unreachable.") });
        }
        if (status != 200) return StatusCode(502, new { message = MlHttp.Detail(text) ?? L("Öneriler hesaplanamadı.", "Recommendations could not be computed.") });
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        object Map(string section, Func<string, object?> enrich) => root.GetProperty(section).EnumerateArray().Select(x =>
        {
            var id = x.GetProperty("id").GetString()!;
            return new { id, score = x.GetProperty("score").GetInt32(), reasons = x.GetProperty("reasons").Clone(), info = enrich(id) };
        }).Where(x => x.info is not null).ToList();
        var myPostings = await Safe(() => Db.QueryAsync("""SELECT "JobPostingId" FROM engagement_internal_applications WHERE "TenantSlug" = $1 AND "UserId" = $2 AND "Status" <> 'Withdrawn'""",
            r => r.GetGuid(0), ct, Tenant, Me.UserId));
        return Ok(new
        {
            linked = true,
            postings = Map("postings", id => postings.FirstOrDefault(p => p.Id.ToString() == id) is { Title: not null } p
                ? new { title = p.Title, department = p.Dept, applied = myPostings.Contains(p.Id) } : null),
            mentors = Map("mentors", id => mentors.FirstOrDefault(m => m.Id.ToString() == id) is { Name: not null } m
                ? new { userId = m.UserId, name = m.Name, department = m.Emp is { } e && people.TryGetValue(e, out var pp) ? pp.Department : null, offers = m.Offers } : null),
            courses = Map("courses", id => courses.FirstOrDefault(c => c.Id.ToString() == id) is { Title: not null } c ? new { title = c.Title, category = c.Cat } : null),
            gaps = root.GetProperty("gaps").Clone(),
            skillsUsed = root.GetProperty("skills_used").Clone(),
            note = L("Öneridir; başvuru, mentorluk talebi ya da eğitim kaydı otomatik yapılmaz. Öneriler yalnızca size gösterilir.",
                     "These are suggestions only; nothing is applied, requested or enrolled automatically. Only you can see them."),
        });
    }
}

/* ======================================================================
 * Madde 51: aday–ilan uygunluk puanı (işe alım uzmanına yardımcı). Ad, cinsiyet,
 * yaş, fotoğraf, adres modele girmez; otomatik ret yoktur; her hesaplama
 * denetim kaydına yazılır.
 * ==================================================================== */
[Route("api/ai/recruit-fit")]
[Authorize(Policy = "RequireManagerOrAbove")]
public class RecruitFitController : AppController
{
    private readonly IHttpClientFactory _http;
    public RecruitFitController(IHttpClientFactory http) => _http = http;

    [HttpGet("card")]
    public async Task<IActionResult> Card(CancellationToken ct)
    {
        try
        {
            var (status, text) = await MlHttp.SendAsync(_http, Request, HttpMethod.Get, "/recruit/fit/card", null, ct, 20);
            return status == 200 ? Content(text, "application/json") : StatusCode(502, new { message = L("Model kartı okunamadı.", "The model card could not be read.") });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(503, new { message = L("Model servisine ulaşılamadı.", "The model service is unreachable.") });
        }
    }

    [HttpGet("{postingId:guid}")]
    public async Task<IActionResult> Fit(Guid postingId, CancellationToken ct)
    {
        var posting = (await Db.QueryAsync("""SELECT "Title", coalesce("Description",'') FROM recruitment_job_postings WHERE "TenantSlug" = $1 AND "Id" = $2""",
            r => (Title: r.GetString(0), Text: r.GetString(1)), ct, Tenant, postingId)).FirstOrDefault();
        if (posting.Title is null) return NotFound();
        var apps = await Db.QueryAsync("""
            SELECT a."Id", c."Id", c."FirstName", c."LastName", c."Email", coalesce(c."ResumeText", ''), coalesce(c."Skills", '{}')
            FROM recruitment_applications a JOIN recruitment_candidates c ON c."Id" = a."CandidateId"
            WHERE a."TenantSlug" = $1 AND a."JobPostingId" = $2 AND a."Status" <> 'Withdrawn' AND c."AnonymizedAt" IS NULL
            ORDER BY a."AppliedAt" LIMIT 500
            """, r => (App: r.GetGuid(0), Cand: r.GetGuid(1), First: r.Str(2), Last: r.Str(3), Email: r.Str(4), Text: r.GetString(5), Skills: r.GetFieldValue<string[]>(6)),
            ct, Tenant, postingId);
        // Aday opak referansla gider (sıra no); ad metinden burada çıkarılır, diğer kişisel ifadeleri ML servisi temizler.
        var payload = new
        {
            posting = new { title = posting.Title.Length > 300 ? posting.Title[..300] : posting.Title, text = posting.Text.Length > 50000 ? posting.Text[..50000] : posting.Text },
            candidates = apps.Select((a, i) => new
            {
                @ref = "c" + i,
                text = CandidateText.StripName(a.Text.Length > 200000 ? a.Text[..200000] : a.Text, a.First, a.Last, a.Email),
                skills = a.Skills.Take(200),
            }),
        };
        int status; string text;
        try { (status, text) = await MlHttp.SendAsync(_http, Request, HttpMethod.Post, "/recruit/fit", payload, ct, 60); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(503, new { message = L("Model servisine ulaşılamadı.", "The model service is unreachable.") });
        }
        if (status != 200) return StatusCode(502, new { message = MlHttp.Detail(text) ?? L("Uygunluk hesaplanamadı.", "Fit could not be computed.") });
        await ComplianceAudit.WriteAsync(Db, Tenant, "CandidateFit", postingId.ToString(), "AutomatedAnalysis",
            new { candidates = apps.Count, purpose = "assistive-ranking" }, Me.UserId, Me.Name, ct);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        return Ok(new
        {
            posting = root.GetProperty("posting").Clone(),
            weights = root.GetProperty("weights").Clone(),
            note = L("Yardımcı sıralamadır; otomatik eleme ya da ret yapılmaz, karar sizindir. Ad, cinsiyet, yaş, fotoğraf ve adres puanlamaya girmez.",
                     "Assistive ranking only; no automatic screening or rejection — the decision is yours. Name, gender, age, photo and address are not used."),
            results = root.GetProperty("results").EnumerateArray().Select(x =>
            {
                var i = int.Parse(x.GetProperty("ref").GetString()![1..]);
                return new
                {
                    applicationId = apps[i].App, candidateId = apps[i].Cand,
                    score = x.GetProperty("score").ValueKind == JsonValueKind.Number ? x.GetProperty("score").GetInt32() : (int?)null,
                    components = x.GetProperty("components").Clone(), reasons = x.GetProperty("reasons").Clone(),
                    flags = x.GetProperty("flags").Clone(), redactions = x.GetProperty("redactions").Clone(),
                };
            }).ToList(),
        });
    }
}

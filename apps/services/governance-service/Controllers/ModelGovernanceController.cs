using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using GovernanceService.Infrastructure;
using GovernanceService.Infrastructure.Chat;

namespace GovernanceService.Controllers;

/// <summary>
/// G5: İK'nın ayrılma riski modelini (sentetik veriyle) yeniden eğitmesi governance üzerinden
/// yapılır; ml-inference'ın döndürdüğü özet denetim kaydına yazılır (kim, ne zaman, hangi sürüm,
/// yayımlandı mı). Kiracı verisiyle eğitim yalnızca platform düzeyindedir (ml-inference denetler).
/// </summary>
[Route("api/model")]
[Authorize(Policy = "RequireHrAdmin")]
public class ModelGovernanceController : AppController
{
    private readonly IHttpClientFactory _http;
    private static readonly string MlBase = EnvVar.Or("ML_INFERENCE_URL", "http://ml-inference:8000").TrimEnd('/');
    public ModelGovernanceController(IHttpClientFactory http) => _http = http;

    public record RetrainInput(bool? DryRun);

    [HttpPost("retrain")]
    public async Task<IActionResult> Retrain(RetrainInput body, CancellationToken ct)
    {
        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(5);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{MlBase}/model/retrain")
        {
            Content = JsonContent.Create(new { source = "synthetic", dry_run = body.DryRun ?? false }),
        };
        req.Headers.TryAddWithoutValidation("Authorization", Request.Headers.Authorization.ToString());
        using var resp = await client.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            return StatusCode((int)resp.StatusCode is 401 or 403 ? 403 : 502, new { message = L("Model yeniden eğitilemedi.", "The model could not be retrained.") });
        using var doc = JsonDocument.Parse(text);
        var hasAudit = doc.RootElement.TryGetProperty("audit", out var a);
        var changes = hasAudit && a.TryGetProperty("changes", out var c) ? c.GetRawText() : "{}";
        var action = hasAudit && a.TryGetProperty("action", out var ac) ? ac.GetString() ?? "Retrained" : "Retrained";
        var entityId = hasAudit && a.TryGetProperty("entityId", out var ei) ? ei.GetString() : null;
        await Db.ExecuteAsync("""
            INSERT INTO audit_log ("TenantSlug","Service","EntityType","EntityId","Action","Changes","UserId","UserName","OccurredAt")
            VALUES ($1,'governance-service','AttritionModel',$2,$3,$4::jsonb,$5,$6,now())
            """, ct, AuditTenant, entityId, action, changes, Me.UserId, Me.Name);
        return Content(text, "application/json");
    }

    public const string AttritionModel = "hr360-attrition-risk";

    /// <summary>Platform yöneticisinin kiracısız oturumunda denetim kaydı "platform" altında tutulur.</summary>
    private string AuditTenant => HttpContext.RequestServices.GetRequiredService<GovernanceService.Tenancy.ITenantContext>().TenantSlug ?? "platform";

    private async Task<(int Status, string Body)> MlAsync(HttpMethod method, string path, object? body, CancellationToken ct, int timeoutSeconds = 60)
    {
        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
        using var req = new HttpRequestMessage(method, $"{MlBase}{path}");
        if (body is not null) req.Content = JsonContent.Create(body);
        req.Headers.TryAddWithoutValidation("Authorization", Request.Headers.Authorization.ToString());
        using var resp = await client.SendAsync(req, ct);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
    }

    /// <summary>ML hata gövdesindeki iletiyi (detail) aynen, değilse genel iletiyi döner.</summary>
    private IActionResult MlError(int status, string body, string tr, string en)
    {
        string? detail = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("detail", out var d))
                detail = d.ValueKind == JsonValueKind.String ? d.GetString()
                    : d.ValueKind == JsonValueKind.Object && d.TryGetProperty("message", out var m) ? m.GetString() : null;
        }
        catch (JsonException) { }
        var code = status is 401 or 403 ? 403 : status is 404 or 409 or 422 ? status : 502;
        return StatusCode(code, new { message = detail ?? L(tr, en) });
    }

    private Task AuditAsync(string action, string? entityId, string changes, CancellationToken ct) => Db.ExecuteAsync("""
        INSERT INTO audit_log ("TenantSlug","Service","EntityType","EntityId","Action","Changes","UserId","UserName","OccurredAt")
        VALUES ($1,'governance-service','AttritionModel',$2,$3,$4::jsonb,$5,$6,now())
        """, ct, AuditTenant, entityId, action, changes, Me.UserId, Me.Name);

    /* ------------------------------------------------------------ inceleme eşiği (madde 37) */

    /// <summary>Kiracının devir riski inceleme eşiği (yoksa 0,5). Tahmin ucu da bunu kullanır.</summary>
    public static async Task<decimal?> ThresholdAsync(Sql db, string tenant, CancellationToken ct)
    {
        try
        {
            return await db.ScalarAsync("""
                SELECT "RiskThreshold" FROM governance_ml_model_settings WHERE "TenantSlug" = $1 AND "ModelName" = $2
                """, ct, tenant, AttritionModel) is { } v ? Convert.ToDecimal(v) : null;
        }
        catch (Npgsql.PostgresException e) when (e.SqlState == "42P01")
        {
            // Tablo henüz yok (2026-10-18_ml_model_quality.sql uygulanmadı): varsayılan eşik kullanılır.
            return null;
        }
    }

    [HttpGet("settings")]
    public async Task<IActionResult> Settings(CancellationToken ct)
    {
        var rows = await Db.QueryAsync("""
            SELECT "RiskThreshold", "UpdatedByName", "UpdatedAt" FROM governance_ml_model_settings
            WHERE "TenantSlug" = $1 AND "ModelName" = $2
            """, r => new { riskThreshold = r.GetDecimal(0), updatedBy = r.Str(1), updatedAt = r.Ts(2) }, ct, Tenant, AttritionModel);
        var row = rows.FirstOrDefault();
        return Ok(new { model = AttritionModel, riskThreshold = row?.riskThreshold ?? 0.5m, isDefault = row is null, row?.updatedBy, row?.updatedAt });
    }

    public record SettingsInput(decimal RiskThreshold);

    /// <summary>İK, kalibrasyon tablosuna (kesinlik/duyarlılık/işaretlenme oranı) bakarak eşiği seçer.
    /// Eşik üstü skor karar değildir; insan incelemesi önerisidir. Değişiklik denetim kaydına yazılır.</summary>
    [HttpPut("settings")]
    public async Task<IActionResult> SaveSettings(SettingsInput body, CancellationToken ct)
    {
        if (!AttritionFairness.ValidThreshold(body.RiskThreshold))
            return BadRequest(new { message = L("Eşik 0,01 ile 0,99 arasında olmalı (en fazla 3 ondalık).", "The threshold must be between 0.01 and 0.99 (at most 3 decimals).") });
        var before = await ThresholdAsync(Db, Tenant, ct);
        await Db.ExecuteAsync("""
            INSERT INTO governance_ml_model_settings ("Id","TenantSlug","ModelName","RiskThreshold","UpdatedBy","UpdatedByName","UpdatedAt")
            VALUES ($1,$2,$3,$4,$5,$6,now())
            ON CONFLICT ("TenantSlug","ModelName") DO UPDATE SET "RiskThreshold" = EXCLUDED."RiskThreshold",
                "UpdatedBy" = EXCLUDED."UpdatedBy", "UpdatedByName" = EXCLUDED."UpdatedByName", "UpdatedAt" = now()
            """, ct, Guid.NewGuid(), Tenant, AttritionModel, body.RiskThreshold, Me.UserId, Me.Name);
        await AuditAsync("ModelThresholdChanged", AttritionModel,
            JsonSerializer.Serialize(new { riskThreshold = new { before = before ?? 0.5m, after = body.RiskThreshold } }), ct);
        return await Settings(ct);
    }

    /* ------------------------------------------------------------ champion / challenger (madde 38) */

    [HttpGet("versions")]
    public async Task<IActionResult> Versions(CancellationToken ct)
    {
        try
        {
            var (status, text) = await MlAsync(HttpMethod.Get, "/model/versions", null, ct);
            return status == 200 ? Content(text, "application/json")
                : MlError(status, text, "Model sürümleri okunamadı.", "Model versions could not be read.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(503, new { message = L("Model servisine ulaşılamadı.", "The model service is unreachable.") });
        }
    }

    public record VersionInput(string? Note);

    /// <summary>Onay bekleyen adayı (challenger) yayına alır. Paylaşılan model olduğundan yalnızca
    /// platform yöneticisi; onaylayan kişi ve not denetim kaydına yazılır.</summary>
    [HttpPost("promote/{version:int}")]
    public Task<IActionResult> Promote(int version, VersionInput? body, CancellationToken ct)
        => SwitchAsync("promote", version, body?.Note, ct);

    /// <summary>Daha önce yayında olmuş bir sürüme geri döner.</summary>
    [HttpPost("rollback/{version:int}")]
    public Task<IActionResult> Rollback(int version, VersionInput? body, CancellationToken ct)
        => SwitchAsync("rollback", version, body?.Note, ct);

    private async Task<IActionResult> SwitchAsync(string op, int version, string? note, CancellationToken ct)
    {
        if (!Me.IsPlatformAdmin)
            return StatusCode(403, new { message = L("Paylaşılan modelin yayındaki sürümünü yalnızca platform yöneticisi değiştirebilir.",
                                                     "Only a platform administrator can change the serving version of the shared model.") });
        try
        {
            var (status, text) = await MlAsync(HttpMethod.Post, $"/model/{op}", new { version = version.ToString(CultureInfo.InvariantCulture) }, ct, 120);
            if (status != 200)
                return MlError(status, text, "Sürüm değiştirilemedi.", "The version could not be changed.");
            using var doc = JsonDocument.Parse(text);
            var changes = doc.RootElement.TryGetProperty("audit", out var a) && a.TryGetProperty("changes", out var c) ? c.GetRawText() : "{}";
            var action = op == "promote" ? "ModelPromotionApproved" : "ModelRolledBack";
            var withNote = JsonSerializer.Serialize(new { result = JsonDocument.Parse(changes).RootElement, note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(note.Trim().Length, 500)] });
            await AuditAsync(action, $"{AttritionModel}/v{version}", withNote, ct);
            return Content(text, "application/json");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(503, new { message = L("Model servisine ulaşılamadı.", "The model service is unreachable.") });
        }
    }

    /* ------------------------------------------------------------ kalibrasyon raporu (madde 37) */

    [HttpGet("calibration")]
    public async Task<IActionResult> Calibration(CancellationToken ct)
    {
        try
        {
            var (status, text) = await MlAsync(HttpMethod.Get, "/model/calibration", null, ct);
            return status == 200 ? Content(text, "application/json")
                : MlError(status, text, "Kalibrasyon raporu okunamadı.", "The calibration report could not be read.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(503, new { message = L("Model servisine ulaşılamadı.", "The model service is unreachable.") });
        }
    }

    /* ------------------------------------------------------------ adillik denetimi (madde 39) */

    public record FairnessInput(string Csv);

    /// <summary>
    /// Adillik denetimi: CSV (employee_id + 6 özellik + isteğe bağlı label) → grup bilgisi dizinden
    /// eşlenir, kimlik çıkarılır, ml-inference grup oranlarını hesaplar (5'ten küçük gruplar gizli).
    /// Rapor kiracı için saklanır; grup bazlı görüntüleme hassas olduğundan denetim kaydına yazılır.
    /// </summary>
    [HttpPost("fairness")]
    [RequestSizeLimit(3_000_000)]
    public async Task<IActionResult> Fairness(FairnessInput body, CancellationToken ct)
    {
        var (rows, errors) = AttritionFairness.ParseCsv(body.Csv);
        if (errors.Count > 0) return BadRequest(new { message = L("CSV geçersiz.", "Invalid CSV."), errors });
        var people = (await People.ListAsync(Tenant, ct, includeTerminated: true)).ToDictionary(p => p.Id);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
        var (payload, skipped) = AttritionFairness.BuildRows(rows, people, today);
        if (payload.Count < AttritionFairness.MinRows)
            return BadRequest(new { message = L($"Eşleşen en az {AttritionFairness.MinRows} çalışan gerekir (eşleşen: {payload.Count}).",
                                                $"At least {AttritionFairness.MinRows} matching employees are required (matched: {payload.Count})."), skipped });
        var threshold = await ThresholdAsync(Db, Tenant, ct) ?? 0.5m;
        try
        {
            var (status, text) = await MlAsync(HttpMethod.Post, "/model/fairness", new { rows = payload, threshold }, ct, 120);
            if (status != 200) return MlError(status, text, "Adillik denetimi yapılamadı.", "The fairness audit could not be run.");
            using var doc = JsonDocument.Parse(text);
            var version = doc.RootElement.TryGetProperty("model_version", out var v) ? v.GetString() : null;
            var id = Guid.NewGuid();
            await Db.ExecuteAsync("""
                INSERT INTO governance_ml_fairness_reports ("Id","TenantSlug","ModelName","ModelVersion","Threshold","Rows","SkippedRows","Report","CreatedBy","CreatedByName","CreatedAt")
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8::jsonb,$9,$10,now())
                """, ct, id, Tenant, AttritionModel, version, threshold, payload.Count, skipped, text, Me.UserId, Me.Name);
            await AuditAsync("FairnessAuditRun", id.ToString(),
                JsonSerializer.Serialize(new { rows = payload.Count, skipped, threshold, modelVersion = version }), ct);
            return Ok(new { id, createdAt = DateTime.UtcNow, createdBy = Me.Name, skipped, report = doc.RootElement.Clone() });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(503, new { message = L("Model servisine ulaşılamadı.", "The model service is unreachable.") });
        }
    }

    /// <summary>Son adillik raporu. Grup oranlarını görüntüleme denetim kaydına yazılır.</summary>
    [HttpGet("fairness/latest")]
    public async Task<IActionResult> LatestFairness(CancellationToken ct)
    {
        var rows = await Db.QueryAsync("""
            SELECT "Id", "Report"::text, "CreatedByName", "CreatedAt", "SkippedRows" FROM governance_ml_fairness_reports
            WHERE "TenantSlug" = $1 AND "ModelName" = $2 ORDER BY "CreatedAt" DESC LIMIT 1
            """, r => (Id: r.GetGuid(0), Report: r.GetString(1), By: r.Str(2), At: r.Ts(3), Skipped: r.GetInt32(4)), ct, Tenant, AttritionModel);
        if (rows.Count == 0) return Ok(new { available = false });
        var x = rows[0];
        await AuditAsync("FairnessReportViewed", x.Id.ToString(), "{}", ct);
        using var doc = JsonDocument.Parse(x.Report);
        return Ok(new { available = true, id = x.Id, createdAt = x.At, createdBy = x.By, skipped = x.Skipped, report = doc.RootElement.Clone() });
    }
}

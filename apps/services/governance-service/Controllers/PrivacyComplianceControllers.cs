using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using GovernanceService.Data;
using GovernanceService.Infrastructure;
using GovernanceService.Infrastructure.Ai;
using GovernanceService.Infrastructure.Chat;
using GovernanceService.Models;

namespace GovernanceService.Controllers;

/* ======================================================================
 * KVKK temeli (envanter, yurt dışı aktarım, imha tutanakları, hassas veri
 * erişim kayıtları, uyum durumu, otomatik analize itiraz).
 * ==================================================================== */
[Route("api/privacy")]
[Authorize]
public class PrivacyComplianceController : AppController
{
    private readonly GovernanceDbContext _db;
    private readonly LlmClient _llm;
    private readonly IHttpClientFactory _http;
    private static readonly string MlBase = EnvVar.Or("ML_INFERENCE_URL", "http://ml-inference:8000").TrimEnd('/');

    public PrivacyComplianceController(GovernanceDbContext db, LlmClient llm, IHttpClientFactory http) { _db = db; _llm = llm; _http = http; }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));

    /* ------------------------------------------------------------------ envanter (K1) */

    [HttpGet("inventory")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Inventory(CancellationToken ct)
    {
        var policies = await _db.RetentionPolicies.AsNoTracking().ToListAsync(ct);
        var agreements = await _db.TransferAgreements.AsNoTracking().ToListAsync(ct);
        var inUse = await TransferGuard.InUseAsync(_db, _llm, ct);
        // Y24: her özel alan dinamik bir işleme faaliyeti olarak envantere eklenir.
        var dynamic = (await CustomFields.ListAsync(Db, Tenant, ct)).Select(CustomFields.Activity);
        return Ok(new
        {
            generatedAt = DateTime.UtcNow,
            activities = PrivacyCatalog.Activities.Concat(dynamic).Select(a => En ? PrivacyCatalogEn.Translate(a) : a).Select(a =>
            {
                var policy = a.RetentionCategory is null ? null : policies.FirstOrDefault(p => p.Category == a.RetentionCategory);
                return new
                {
                    a.Id, a.Module, a.Activity, a.Subjects, a.DataCategories, a.Purpose, a.LegalBasis, a.Special,
                    a.Retention, a.RetentionCategory, a.Recipients, a.Measures,
                    retentionPolicy = policy is null ? null : new { policy.RetentionMonths, policy.Action, policy.IsEnabled },
                    transfers = a.TransferProviders.Select(k => new
                    {
                        key = k, name = TransferGuard.ProviderName(k), inUse = inUse.GetValueOrDefault(k),
                        status = TransferGuard.Status(agreements.FirstOrDefault(x => x.Provider == k), Today),
                    }),
                };
            }),
        });
    }

    /* ------------------------------------------------------------------ yurt dışı aktarım (K4) */

    [HttpGet("transfers")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Transfers(CancellationToken ct)
    {
        var agreements = await _db.TransferAgreements.AsNoTracking().ToListAsync(ct);
        var inUse = await TransferGuard.InUseAsync(_db, _llm, ct);
        return Ok(new
        {
            mechanisms = PrivacyCatalog.Mechanisms.Select(m => new { value = m.Key, label = m.Value }),
            llmLocal = _llm.Configured && _llm.IsLocal,
            providers = PrivacyCatalog.Providers.Select(p =>
            {
                var a = agreements.FirstOrDefault(x => x.Provider == p.Key);
                return new
                {
                    p.Key, p.Name, p.Country, p.DataSent, p.UsedBy, inUse = inUse.GetValueOrDefault(p.Key),
                    status = TransferGuard.Status(a, Today),
                    notifyDeadline = a is { Mechanism: "StandardContract" } ? PrivacyCatalog.AddBusinessDays(a.SignedAt, 5) : (DateOnly?)null,
                    agreement = a is null ? null : new { a.Mechanism, a.SignedAt, a.NotifiedAt, a.Reference, a.Notes, a.UpdatedBy, a.UpdatedAt },
                };
            }),
        });
    }

    public record TransferInput(string Mechanism, DateOnly SignedAt, DateOnly? NotifiedAt, string? Reference, string? Notes);

    [HttpPut("transfers/{provider}")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> SaveTransfer(string provider, TransferInput body, CancellationToken ct)
    {
        if (PrivacyCatalog.Providers.All(p => p.Key != provider)) return NotFound();
        if (!PrivacyCatalog.Mechanisms.ContainsKey(body.Mechanism)) return BadRequest(new { message = L("Geçersiz aktarım dayanağı.", "Invalid transfer mechanism.") });
        if (body.SignedAt > Today) return BadRequest(new { message = L("İmza tarihi gelecekte olamaz.", "The signing date cannot be in the future.") });
        if (body.NotifiedAt is { } n && (n < body.SignedAt || n > Today))
            return BadRequest(new { message = L("Bildirim tarihi imza tarihiyle bugün arasında olmalı.", "The notification date must be between the signing date and today.") });
        var a = await _db.TransferAgreements.FirstOrDefaultAsync(x => x.Provider == provider, ct);
        if (a is null) { a = new TransferAgreement { Provider = provider }; _db.TransferAgreements.Add(a); }
        a.Mechanism = body.Mechanism;
        a.SignedAt = body.SignedAt;
        a.NotifiedAt = body.Mechanism == "StandardContract" ? body.NotifiedAt : null;
        a.Reference = string.IsNullOrWhiteSpace(body.Reference) ? null : body.Reference.Trim();
        a.Notes = string.IsNullOrWhiteSpace(body.Notes) ? null : body.Notes.Trim();
        a.UpdatedBy = Me.Name;
        a.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { provider, status = TransferGuard.Status(a, Today) });
    }

    [HttpDelete("transfers/{provider}")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> DeleteTransfer(string provider, CancellationToken ct)
    {
        var a = await _db.TransferAgreements.FirstOrDefaultAsync(x => x.Provider == provider, ct);
        if (a is null) return NotFound();
        if ((await TransferGuard.InUseAsync(_db, _llm, ct)).GetValueOrDefault(provider))
            return Conflict(new { message = L("Bu hizmet kullanımda. Önce ilgili entegrasyonu kapatın.", "This service is in use. Turn off the integration first.") });
        _db.TransferAgreements.Remove(a);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    /* ------------------------------------------------------------------ imha tutanakları (K5) */

    [HttpGet("destruction-logs")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> DestructionLogs([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct)
    {
        var q = _db.DestructionLogs.AsNoTracking();
        // Tarihler Türkiye saatine göre (gün sınırı UTC'ye çevrilir).
        if (from is { } f) { var fu = TrTime.StartOfDayUtc(f); q = q.Where(x => x.RanAt >= fu); }
        if (to is { } t) { var tu = TrTime.StartOfDayUtc(t.AddDays(1)); q = q.Where(x => x.RanAt < tu); }
        var rows = await q.OrderByDescending(x => x.RanAt).Take(2000).ToListAsync(ct);
        // Dalga 10: tablo bazında döküm (Details sütunu; göç uygulanmadıysa boş).
        var details = new Dictionary<Guid, JsonElement>();
        try
        {
            var ids = rows.Select(r => r.Id).ToArray();
            foreach (var (id, d) in await Db.QueryAsync("""
                         SELECT "Id","Details"::text FROM governance_destruction_logs WHERE "TenantSlug" = $1 AND "Id" = ANY($2) AND "Details" IS NOT NULL
                         """, r => (r.GetGuid(0), r.GetString(1)), ct, Tenant, ids))
                details[id] = JsonDocument.Parse(d).RootElement.Clone();
        }
        catch (Npgsql.PostgresException e) when (e.SqlState == "42703") { }
        return Ok(rows.Select(x => new
        {
            x.Id, x.Category, label = Retention.LabelOf(x.Category, En), x.Action, x.Affected,
            x.RetentionMonths, x.Trigger, x.Actor, x.Method, x.RanAt, details = details.TryGetValue(x.Id, out var d) ? d : (JsonElement?)null,
        }));
    }

    /* ------------------------------------------------------------------ hassas veri erişim kaydı (K6) */

    private static readonly string[] AccessActions = { "Revealed", "SensitiveViewed", "Exported", "AutomatedAnalysis", "PlatformAccess" };

    private async Task<List<object>> AccessRowsAsync(Guid? employeeId, int days, CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddDays(-Math.Clamp(days, 1, 730));
        return await Db.QueryAsync<object>("""
            SELECT a."OccurredAt", a."Service", a."EntityType", a."EntityId", a."Action", a."Changes"::text, a."UserName", a."IpAddress",
                   e."FirstName" || ' ' || e."LastName"
            FROM audit_log a
            LEFT JOIN employee_employees e ON e."TenantSlug" = a."TenantSlug" AND e."Id"::text = a."EntityId"
            WHERE a."TenantSlug" = $1 AND a."Action" = ANY($2) AND a."OccurredAt" >= $3
              AND ($4::text IS NULL OR a."EntityId" = $4)
            ORDER BY a."OccurredAt" DESC LIMIT 500
            """, r =>
        {
            Dictionary<string, JsonElement>? changes = null;
            try { changes = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(r.Str(5) ?? "{}"); } catch (JsonException) { }
            string? Field(string k) => changes is not null && changes.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return new
            {
                at = r.GetFieldValue<DateTime>(0), service = r.GetString(1), entity = r.GetString(2), employeeId = r.Str(3),
                action = r.GetString(4), field = Field("field"), reason = Field("reason"), viewer = r.Str(6) ?? "—", ip = r.Str(7),
                person = r.Str(8),
            };
        }, ct, Tenant, AccessActions, since, employeeId?.ToString());
    }

    [HttpGet("access-log")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> AccessLog([FromQuery] Guid? employeeId, [FromQuery] int days = 90, CancellationToken ct = default) =>
        Ok(await AccessRowsAsync(employeeId, days, ct));

    /// <summary>Çalışan kendi hassas verisine kimin eriştiğini görür (şeffaflık).</summary>
    [HttpGet("access-log/me")]
    public async Task<IActionResult> MyAccessLog([FromQuery] int days = 365, CancellationToken ct = default)
    {
        var me = await MyPersonAsync(ct);
        if (me is null) return Ok(Array.Empty<object>());
        return Ok(await AccessRowsAsync(me.Id, days, ct));
    }

    /* ------------------------------------------------------------------ uyum durumu */

    [HttpGet("compliance")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Compliance(CancellationToken ct)
    {
        var checks = new List<object>();
        void Add(string key, string title, string status, string detail, string? tab = null) => checks.Add(new { key, title, status, detail, tab });

        var people = await People.ListAsync(Tenant, ct);
        var read = await _db.Consents.AsNoTracking().Where(c => c.ConsentType == "KVKK_AYDINLATMA" && c.Granted).Select(c => c.UserId).Distinct().ToListAsync(ct);
        var missing = people.Count(p => p.UserId is not null && !read.Contains(p.UserId));
        Add("notice", L("Aydınlatma metni", "Privacy notice"), missing == 0 ? "ok" : "warn",
            missing == 0 ? L("Tüm kullanıcılar okudu.", "All users have read it.") : L($"{missing} kullanıcı henüz okumadı.", $"{missing} users have not read it yet."), "riza");

        var now = DateTime.UtcNow;
        var overdueRequests = await _db.DataRequests.CountAsync(r => (r.Status == "Received" || r.Status == "InProgress") && r.DueAt < now, ct);
        var openRequests = await _db.DataRequests.CountAsync(r => r.Status == "Received" || r.Status == "InProgress", ct);
        Add("requests", L("İlgili kişi başvuruları (30 gün)", "Data subject requests (30 days)"), overdueRequests > 0 ? "error" : openRequests > 0 ? "warn" : "ok",
            overdueRequests > 0 ? L($"{overdueRequests} başvurunun süresi geçti.", $"{overdueRequests} requests are overdue.")
            : openRequests > 0 ? L($"{openRequests} açık başvuru var.", $"{openRequests} requests are open.") : L("Açık başvuru yok.", "No open requests."), "basvuru");

        var openObjections = await _db.AnalysisObjections.CountAsync(o => o.Status == "Open", ct);
        var overdueObjections = await _db.AnalysisObjections.CountAsync(o => o.Status == "Open" && o.DueAt < now, ct);
        Add("objections", L("Otomatik analize itirazlar", "Objections to automated analysis"), overdueObjections > 0 ? "error" : openObjections > 0 ? "warn" : "ok",
            openObjections == 0 ? L("Açık itiraz yok.", "No open objections.") : L($"{openObjections} itiraz karar bekliyor.", $"{openObjections} objections await a decision."), "itiraz");

        var policies = await _db.RetentionPolicies.AsNoTracking().ToListAsync(ct);
        var disabled = Retention.Categories.Keys.Where(k => policies.All(p => p.Category != k || !p.IsEnabled))
            .Select(k => Retention.LabelOf(k, En)).ToList();
        Add("retention", L("Periyodik imha", "Periodic destruction"), disabled.Count == 0 ? "ok" : "warn",
            disabled.Count == 0 ? L("Tüm saklama politikaları otomatik çalışıyor (günde bir).", "All retention policies run automatically (daily).")
            : L("Otomatik çalışmayan: ", "Not automatic: ") + string.Join(", ", disabled), "saklama");

        var agreements = await _db.TransferAgreements.AsNoTracking().ToListAsync(ct);
        var inUse = await TransferGuard.InUseAsync(_db, _llm, ct);
        var unlawful = PrivacyCatalog.Providers.Where(p => inUse.GetValueOrDefault(p.Key) && agreements.All(a => a.Provider != p.Key)).Select(p => p.Name).ToList();
        var overdue = agreements.Where(a => TransferGuard.Status(a, Today) == "NotifyOverdue").Select(a => TransferGuard.ProviderName(a.Provider)).ToList();
        var pending = agreements.Where(a => TransferGuard.Status(a, Today) == "NotifyPending").Select(a => TransferGuard.ProviderName(a.Provider)).ToList();
        Add("transfers", L("Yurt dışına aktarım", "Cross-border transfers"),
            unlawful.Count > 0 || overdue.Count > 0 ? "error" : pending.Count > 0 ? "warn" : "ok",
            unlawful.Count > 0 ? L("Dayanak kaydı olmadan kullanılan: ", "In use without a legal basis: ") + string.Join(", ", unlawful)
            : overdue.Count > 0 ? L("Kurul'a bildirim süresi (5 iş günü) geçti: ", "Board notification deadline (5 business days) passed: ") + string.Join(", ", overdue)
            : pending.Count > 0 ? L("Kurul'a bildirim bekliyor: ", "Awaiting Board notification: ") + string.Join(", ", pending)
            : L("Dayanak kaydı olmadan kullanılan hizmet yok.", "No service is used without a legal basis."), "aktarim");

        var smtp = EnvVar.Or("SMTP_HOST", "");
        // "mailer": sunucunun kendi posta servisi; aracı sağlayıcı yok, e-postalar alıcıya doğrudan gider.
        if (smtp is not "" and not "mailpit" and not "mailer")
            Add("smtp", L("E-posta sağlayıcısı", "Email provider"), "info",
                L($"SMTP sunucusu {smtp}. Sağlayıcı yurt dışındaysa (Gmail, Microsoft 365 vb.) yurt dışı aktarım dayanağı gerekir.",
                  $"SMTP server {smtp}. If the provider is abroad (Gmail, Microsoft 365, etc.) a cross-border transfer basis is required."));

        var breaches = await _db.DataBreaches.AsNoTracking().Where(b => b.Status != "Closed").ToListAsync(ct);
        var lateBreaches = breaches.Count(b => b.ReportedToBoardAt is null && b.DetectedAt.AddHours(KvkkOpsController.BoardDeadlineHours) < now);
        var unreported = breaches.Count(b => b.ReportedToBoardAt is null);
        Add("breaches", L("Veri ihlalleri (72 saat)", "Data breaches (72 hours)"), lateBreaches > 0 ? "error" : breaches.Count > 0 ? "warn" : "ok",
            lateBreaches > 0 ? L($"{lateBreaches} ihlal 72 saat içinde Kurul'a bildirilmedi.", $"{lateBreaches} breaches were not reported to the Board within 72 hours.")
            : unreported > 0 ? L($"{unreported} ihlal Kurul'a bildirim bekliyor.", $"{unreported} breaches await Board notification.")
            : breaches.Count > 0 ? L($"{breaches.Count} açık ihlal kaydı var.", $"{breaches.Count} breach records are open.") : L("Açık ihlal kaydı yok.", "No open breach records."), "ihlal");

        var approvedPia = await _db.PrivacyAssessments.AsNoTracking().Where(a => a.Status == "Approved" && a.ProviderKey != null).Select(a => a.ProviderKey!).ToListAsync(ct);
        var noPia = PrivacyCatalog.Providers.Where(p => inUse.GetValueOrDefault(p.Key) && !approvedPia.Contains(p.Key)).Select(p => p.Name).ToList();
        Add("assessments", L("Gizlilik etki değerlendirmesi", "Privacy impact assessment"), noPia.Count > 0 ? "warn" : "ok",
            noPia.Count > 0 ? L("Onaylı değerlendirmesi olmayan entegrasyon: ", "Integrations without an approved assessment: ") + string.Join(", ", noPia)
            : L("Kullanılan tüm entegrasyonlar değerlendirilmiş.", "All integrations in use have been assessed."), "pia");

        var unverified = await _db.DataRequests.CountAsync(r => !r.IdentityVerified && (r.Status == "Received" || r.Status == "InProgress"), ct);
        if (unverified > 0)
            Add("identity", L("Başvuru kimlik doğrulaması", "Request identity verification"), "warn",
                L($"{unverified} başvurucunun kimliği henüz doğrulanmadı.", $"{unverified} applicants have not been verified yet."), "basvuru");

        // Dalga 10: bakımı yapılan VERBİS envanteri (yoksa yerleşik katalog sayılır). Bir yıldır güncellenmediyse uyarı.
        (int Count, int Special, DateTime? Last)? inv = null;
        try
        {
            inv = (await Db.QueryAsync("""
                SELECT count(*) FILTER (WHERE "IsActive")::int, count(*) FILTER (WHERE "IsActive" AND "Special")::int, max("UpdatedAt")
                FROM governance_privacy_inventory WHERE "TenantSlug" = $1
                """, r => (r.GetInt32(0), r.GetInt32(1), r.Ts(2)), ct, Tenant)).FirstOrDefault();
        }
        catch (Npgsql.PostgresException e) when (e.SqlState == "42P01") { }
        if (inv is { Count: > 0 } i)
        {
            var stale = i.Last is { } last && last < DateTime.UtcNow.AddYears(-1);
            Add("inventory", L("İşleme envanteri (VERBİS)", "Processing inventory (VERBIS)"), stale ? "warn" : "ok",
                stale ? L($"{i.Count} faaliyet; envanter bir yıldan uzun süredir güncellenmedi (son: {i.Last:dd.MM.yyyy}).",
                          $"{i.Count} activities; the inventory has not been updated for over a year (last: {i.Last:yyyy-MM-dd}).")
                      : L($"{i.Count} işleme faaliyeti, {i.Special} tanesi özel nitelikli veri içeriyor.",
                          $"{i.Count} processing activities, {i.Special} involving special category data."), "envanter");
        }
        else
        {
            var special = PrivacyCatalog.Activities.Count(a => a.Special);
            Add("inventory", L("İşleme envanteri", "Processing inventory"), "ok",
                L($"{PrivacyCatalog.Activities.Length} işleme faaliyeti, {special} tanesi özel nitelikli veri içeriyor.",
                  $"{PrivacyCatalog.Activities.Length} processing activities, {special} involving special category data."), "envanter");
        }

        // Dalga 10: açık yeniden onay kampanyaları.
        try
        {
            var open = await ConsentCampaigns.ListAsync(Db, Tenant, ct, openOnly: true);
            if (open.Count > 0)
                Add("campaigns", L("Yeniden onay kampanyaları", "Re-consent campaigns"), "info",
                    L($"{open.Count} açık kampanya: güncellenen metinlerin yanıtları izleniyor.", $"{open.Count} open campaigns: responses to updated notices are being tracked."), "riza");
        }
        catch (Npgsql.PostgresException e) when (e.SqlState == "42P01") { }

        return Ok(checks);
    }

    /* ------------------------------------------------------------------ otomatik analize itiraz (K10) */

    public record ObjectionInput(string Analysis, string? Reason);

    [HttpGet("analyses")]
    public IActionResult Analyses() => Ok(PrivacyCatalog.Analyses.Select(a => new { value = a.Key, label = a.Value }));

    [HttpPost("objections")]
    public async Task<IActionResult> CreateObjection(ObjectionInput body, CancellationToken ct)
    {
        if (!PrivacyCatalog.Analyses.ContainsKey(body.Analysis)) return BadRequest(new { message = L("Geçersiz analiz türü.", "Invalid analysis type.") });
        var me = await MyPersonAsync(ct);
        if (me is null) return BadRequest(new { message = L("Hesabınız bir çalışan kaydına bağlı değil.", "Your account is not linked to an employee record.") });
        if (await _db.AnalysisObjections.AnyAsync(o => o.EmployeeId == me.Id && o.Analysis == body.Analysis && (o.Status == "Open" || o.Status == "Upheld"), ct))
            return Conflict(new { message = L("Bu analiz için zaten bir itirazınız var.", "You already have an objection for this analysis.") });
        var o = new AnalysisObjection
        {
            EmployeeId = me.Id, UserId = Me.UserId, PersonName = me.Name, Analysis = body.Analysis,
            Reason = string.IsNullOrWhiteSpace(body.Reason) ? null : body.Reason.Trim()[..Math.Min(2000, body.Reason.Trim().Length)],
        };
        _db.AnalysisObjections.Add(o);
        await _db.SaveChangesAsync(ct);
        return Ok(ObjectionView(o));
    }

    private static object ObjectionView(AnalysisObjection o) => new
    {
        o.Id, o.EmployeeId, o.PersonName, o.Analysis, analysisLabel = PrivacyCatalog.Analyses.GetValueOrDefault(o.Analysis) ?? o.Analysis,
        o.Reason, o.Status, o.Response, o.DecidedBy, o.CreatedAt, o.DecidedAt, o.DueAt,
        overdue = o.Status == "Open" && o.DueAt < DateTime.UtcNow,
    };

    [HttpGet("objections")]
    public async Task<IActionResult> Objections(CancellationToken ct)
    {
        var q = _db.AnalysisObjections.AsNoTracking();
        if (!Me.IsHr) q = q.Where(o => o.UserId == Me.UserId);
        return Ok((await q.OrderByDescending(o => o.CreatedAt).Take(500).ToListAsync(ct)).Select(ObjectionView));
    }

    public record ObjectionDecision(string Status, string? Response);

    [HttpPatch("objections/{id:guid}")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> DecideObjection(Guid id, ObjectionDecision body, CancellationToken ct)
    {
        var o = await _db.AnalysisObjections.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (o is null) return NotFound();
        if (body.Status is not ("Upheld" or "Rejected")) return BadRequest(new { message = L("Durum Upheld ya da Rejected olmalı.", "Status must be Upheld or Rejected.") });
        if (string.IsNullOrWhiteSpace(body.Response))
            return BadRequest(new { message = L("Karar gerekçesi çalışana yazılmalı.", "A reason must be written for the employee.") });
        o.Status = body.Status;
        o.Response = body.Response.Trim();
        o.DecidedBy = Me.Name;
        o.DecidedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await HttpContext.RequestServices.GetRequiredService<Notifier>().LocalizedAsync(Tenant, o.EmployeeId,
            body.Status == "Upheld" ? "İtirazınız kabul edildi" : "İtirazınız sonuçlandı",
            body.Status == "Upheld" ? "Your objection was upheld" : "Your objection has been decided",
            $"{PrivacyCatalog.Analyses.GetValueOrDefault(o.Analysis)}: {o.Response}",
            $"{o.Analysis switch { "AttritionRisk" => "Attrition risk prediction", "PerformanceScore" => "Automated performance score", "AiSummary" => "AI performance summary", var x => x }}: {o.Response}",
            "KVKK_OBJECTION", ct);
        return Ok(ObjectionView(o));
    }

    /// <summary>Kişi için bu analiz engelli mi (açık ya da kabul edilmiş itiraz).</summary>
    private Task<AnalysisObjection?> BlockingObjectionAsync(Guid employeeId, string analysis, CancellationToken ct) =>
        _db.AnalysisObjections.AsNoTracking()
            .Where(o => o.EmployeeId == employeeId && o.Analysis == analysis && (o.Status == "Open" || o.Status == "Upheld"))
            .OrderByDescending(o => o.CreatedAt).FirstOrDefaultAsync(ct);

    [HttpGet("objections/status/{employeeId:guid}")]
    public async Task<IActionResult> ObjectionStatus(Guid employeeId, [FromQuery] string analysis, CancellationToken ct)
    {
        if (!Me.IsManager && (await MyPersonAsync(ct))?.Id != employeeId) return Forbid();
        var o = await BlockingObjectionAsync(employeeId, analysis, ct);
        return Ok(new { blocked = o is not null, status = o?.Status, since = o?.CreatedAt });
    }

    public record PredictInput(List<double> Features);

    /// <summary>
    /// İşten ayrılma riski: ml-inference'a yalnızca bu uç üzerinden gidilir (gateway /ml/predict'i kapatır).
    /// İtiraz varsa skor üretilmez; her hesaplama kişinin erişim kaydına yazılır.
    /// </summary>
    [HttpPost("analysis/attrition/{employeeId:guid}")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Attrition(Guid employeeId, PredictInput body, CancellationToken ct)
    {
        if (body.Features is not { Count: > 0 and <= 50 }) return BadRequest(new { message = L("Özellik listesi geçersiz.", "Invalid feature list.") });
        if (await People.FindAsync(Tenant, employeeId, ct) is null) return NotFound();
        if (await BlockingObjectionAsync(employeeId, "AttritionRisk", ct) is { } o)
            return Conflict(new
            {
                code = "objection",
                message = L("Çalışan bu otomatik analize itiraz etti (KVKK m.11/1-g). İtiraz sonuçlanana kadar ya da kabul edildiyse skor üretilmez.",
                            "The employee objected to this automated analysis (KVKK art. 11/1-g). No score is produced while the objection is open or if it was upheld."),
                status = o.Status,
            });

        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(20);
        // Kiracının İK'ca seçtiği inceleme eşiği (yoksa 0,5): eşik üstü "insan incelemesi önerilir" işaretidir, karar değildir.
        var threshold = await ModelGovernanceController.ThresholdAsync(Db, Tenant, ct) ?? 0.5m;
        async Task<(int Status, JsonElement? Body)> Call(string path)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{MlBase}{path}") { Content = JsonContent.Create(new { features = body.Features, threshold }) };
            if (AuthenticationHeaderValue.TryParse(Request.Headers.Authorization.ToString(), out var auth)) req.Headers.Authorization = auth;
            using var res = await client.SendAsync(req, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            try { return ((int)res.StatusCode, JsonDocument.Parse(text).RootElement.Clone()); } catch (JsonException) { return ((int)res.StatusCode, null); }
        }
        try
        {
            var (status, prediction) = await Call("/predict");
            // 4xx (ör. 422 doğrulama) istemci hatasıdır: iletisiyle 400 dönülür; yalnızca 5xx/ağ hatası 502/503.
            if (status is >= 400 and < 500) return BadRequest(new { message = MlClientError(prediction) });
            if (status != 200) return StatusCode(status == 503 ? 503 : 502, new { message = L("Tahmin servisi yanıt vermedi.", "The prediction service did not respond.") });
            var (eStatus, explanation) = await Call("/explain");
            await Db.ExecuteAsync("""
                INSERT INTO audit_log ("TenantSlug","Service","EntityType","EntityId","Action","Changes","UserId","UserName","CorrelationId","IpAddress","OccurredAt")
                VALUES ($1,'governance-service','AttritionRisk',$2,'AutomatedAnalysis',$3::jsonb,$4,$5,$6,$7,now())
                """, ct, Tenant, employeeId.ToString(), "{\"field\":\"attritionRisk\"}", Me.UserId, Me.Name,
                Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? HttpContext.TraceIdentifier, Request.Headers["X-Real-IP"].FirstOrDefault());
            return Ok(new { prediction, explanation = eStatus == 200 ? explanation : null });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(503, new { message = L("Tahmin servisine ulaşılamadı.", "The prediction service is unreachable.") });
        }
    }

    /// <summary>FastAPI hata gövdesinden ({"detail": "..."} ya da {"detail": [{"msg": ...}]}) okunur ileti.</summary>
    private string MlClientError(JsonElement? body)
    {
        var generic = L("Tahmin girdisi geçersiz.", "The prediction input is invalid.");
        if (body is not { ValueKind: JsonValueKind.Object } b || !b.TryGetProperty("detail", out var d)) return generic;
        if (d.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(d.GetString())) return d.GetString()!;
        if (d.ValueKind == JsonValueKind.Array && d.GetArrayLength() > 0 && d[0].ValueKind == JsonValueKind.Object
            && d[0].TryGetProperty("msg", out var msg) && msg.ValueKind == JsonValueKind.String)
            return $"{generic} ({msg.GetString()})";
        return generic;
    }
}

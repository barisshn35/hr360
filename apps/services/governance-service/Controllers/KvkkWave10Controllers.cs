using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using GovernanceService.Data;
using GovernanceService.Infrastructure;
using GovernanceService.Infrastructure.Ai;

namespace GovernanceService.Controllers;

/* ======================================================================
 * Dalga 10 — KVKK uçları:
 *   53) /privacy/verbis…                 VERBİS envanteri (bakım + dışa aktarım)
 *   54) /privacy/requests/{id}/package   erişim başvurusu veri paketi
 *   55) /privacy/consent-campaigns…      yeniden onay kampanyası, /privacy/consents/pending (giriş bandı)
 *   57) /privacy/destruction-verification imha doğrulama raporu
 * ==================================================================== */
[Route("api/privacy")]
[Authorize]
public class KvkkWave10Controller : AppController
{
    private readonly GovernanceDbContext _db;
    private readonly LlmClient _llm;
    private readonly IHttpClientFactory _http;
    public KvkkWave10Controller(GovernanceDbContext db, LlmClient llm, IHttpClientFactory http) { _db = db; _llm = llm; _http = http; }

    private Task AuditAsync(string entity, string id, string action, object changes, CancellationToken ct) =>
        ComplianceAudit.WriteAsync(Db, Tenant, entity, id, action, changes, Me.UserId, Me.Name, ct);

    private static IActionResult MigrationMissing() =>
        new ObjectResult(new { message = "Bu özellik için veritabanı güncellemesi (2026-10-23_kvkk_ml.sql) uygulanmalı.", code = "migration_missing" }) { StatusCode = 503 };

    /* ================================================================== 53) VERBİS envanteri */

    private async Task<IEnumerable<ProcessingActivity>> CatalogAsync(CancellationToken ct) =>
        PrivacyCatalog.Activities.Concat((await CustomFields.ListAsync(Db, Tenant, ct)).Select(CustomFields.Activity));

    private async Task<Dictionary<string, VerbisInventory.TransferInfo>> TransfersAsync(CancellationToken ct)
    {
        var agreements = await _db.TransferAgreements.AsNoTracking().ToListAsync(ct);
        var inUse = await TransferGuard.InUseAsync(_db, _llm, ct);
        return PrivacyCatalog.Providers.ToDictionary(p => p.Key, p =>
        {
            var a = agreements.FirstOrDefault(x => x.Provider == p.Key);
            var mech = a is null ? "Dayanak kaydı yok" : PrivacyCatalog.Mechanisms.GetValueOrDefault(a.Mechanism, a.Mechanism).Split(" (")[0];
            return new VerbisInventory.TransferInfo(p.Key, p.Name, p.Country, mech, inUse.GetValueOrDefault(p.Key));
        });
    }

    [HttpGet("verbis")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Verbis(CancellationToken ct)
    {
        List<VerbisInventory.Item> items;
        try
        {
            items = await VerbisInventory.ListAsync(Db, Tenant, ct);
            // İlk açılışta ürünün veri modelinden tohumlanır (sonra yalnızca "Katalogdan eksikleri ekle" ile).
            if (items.Count == 0)
            {
                var added = await VerbisInventory.SeedAsync(Db, Tenant, await CatalogAsync(ct), null, "Sistem (ürün kataloğu)", ct);
                if (added > 0) await AuditAsync("VerbisInventory", Tenant, "Seeded", new { added }, ct);
                items = await VerbisInventory.ListAsync(Db, Tenant, ct);
            }
        }
        catch (Npgsql.PostgresException e) when (e.SqlState == "42P01") { return MigrationMissing(); }
        var catalog = (await CatalogAsync(ct)).ToList();
        var policies = await _db.RetentionPolicies.AsNoTracking().ToListAsync(ct);
        var transfers = await TransfersAsync(ct);
        var last = items.OrderByDescending(i => i.UpdatedAt).FirstOrDefault();
        return Ok(new
        {
            lastUpdatedAt = last?.UpdatedAt, lastUpdatedBy = last?.UpdatedByName,
            catalogMissing = catalog.Where(a => items.All(i => i.Key != a.Id)).Select(a => En ? PrivacyCatalogEn.Translate(a) : a).Select(a => new { a.Id, a.Module, a.Activity }),
            providers = transfers.Values.Select(t => new { t.Key, t.Name, t.Country, t.Mechanism, t.InUse }),
            retentionCategories = Retention.Categories.Select(c => new { value = c.Key, label = Retention.LabelOf(c.Key, En) }),
            // İngilizce arayüzde katalog metinleri çevrilir; kiracının düzenlediği metin aynen kalır.
            items = items.Select(i => En ? PrivacyCatalogEn.Translate(i) : i).Select(i =>
            {
                var policy = i.RetentionCategory is null ? null : policies.FirstOrDefault(p => p.Category == i.RetentionCategory);
                return new
                {
                    i.Id, key = i.Key, i.Source, i.Module, i.Activity, i.Subjects, i.DataCategories, i.Purpose, i.LegalBasis, i.Special, i.Retention,
                    i.RetentionCategory, i.Recipients, i.TransferProviders, i.Measures, i.IsActive, i.UpdatedAt, i.UpdatedByName,
                    retentionPolicy = policy is null ? null : new { policy.RetentionMonths, policy.Action, policy.IsEnabled },
                };
            }),
        });
    }

    [HttpPost("verbis/sync")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> VerbisSync(CancellationToken ct)
    {
        try
        {
            var added = await VerbisInventory.SeedAsync(Db, Tenant, await CatalogAsync(ct), Me.UserId, Me.Name, ct);
            await AuditAsync("VerbisInventory", Tenant, "CatalogSynced", new { added }, ct);
            return Ok(new { added });
        }
        catch (Npgsql.PostgresException e) when (e.SqlState == "42P01") { return MigrationMissing(); }
    }

    public record VerbisItemInput(string Module, string Activity, List<string>? Subjects, List<string>? DataCategories, string Purpose,
        string LegalBasis, bool Special, string Retention, string? RetentionCategory, List<string>? Recipients, List<string>? TransferProviders,
        string? Measures, bool IsActive = true);

    private IActionResult? ValidateItem(VerbisItemInput b)
    {
        if (VerbisInventory.Validate(b.Module, b.Activity, b.Purpose, b.LegalBasis, b.Retention, b.DataCategories, b.Subjects) is { } err)
            return BadRequest(new
            {
                message = L(err, err switch
                {
                    var s when s.StartsWith("Modül") => "Module is required (max 120 characters).",
                    var s when s.StartsWith("İşleme faaliyeti") => "Processing activity is required (max 300 characters).",
                    var s when s.StartsWith("İşleme amacı") => "Purpose is required.",
                    var s when s.StartsWith("Hukuki") => "Legal basis is required (KVKK art. 5 / 6).",
                    var s when s.StartsWith("Saklama") => "Retention period is required.",
                    var s when s.Contains("veri kategorisi") => "At least one data category is required.",
                    var s when s.Contains("ilgili kişi") => "At least one data subject group is required.",
                    _ => "Text fields must be at most 4000 characters.",
                })
            });
        if (b.RetentionCategory is { Length: > 0 } rc && !Retention.Categories.ContainsKey(rc))
            return BadRequest(new { message = L("Bilinmeyen saklama politikası.", "Unknown retention policy.") });
        if ((b.TransferProviders ?? new()).Any(k => PrivacyCatalog.Providers.All(p => p.Key != k)))
            return BadRequest(new { message = L("Bilinmeyen yurt dışı aktarım sağlayıcısı.", "Unknown cross-border provider.") });
        if (b.Measures is { Length: > 4000 }) return BadRequest(new { message = L("Tedbirler en fazla 4000 karakter.", "Measures must be at most 4000 characters.") });
        return null;
    }

    [HttpPost("verbis/items")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> CreateVerbisItem(VerbisItemInput body, CancellationToken ct)
    {
        if (ValidateItem(body) is { } bad) return bad;
        var id = Guid.NewGuid();
        var key = "custom-" + id.ToString("N")[..10];
        try
        {
            await Db.ExecuteAsync("""
                INSERT INTO governance_privacy_inventory ("Id","TenantSlug","ActivityKey","Source","Module","Activity","Subjects","DataCategories","Purpose",
                    "LegalBasis","Special","Retention","RetentionCategory","Recipients","TransferProviders","Measures","IsActive","CreatedAt","UpdatedAt","UpdatedBy","UpdatedByName")
                VALUES ($1,$2,$3,'Custom',$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,now(),now(),$17,$18)
                """, ct, id, Tenant, key, body.Module.Trim(), body.Activity.Trim(), VerbisInventory.CleanList(body.Subjects), VerbisInventory.CleanList(body.DataCategories),
                body.Purpose.Trim(), body.LegalBasis.Trim(), body.Special, body.Retention.Trim(), string.IsNullOrWhiteSpace(body.RetentionCategory) ? null : body.RetentionCategory,
                VerbisInventory.CleanList(body.Recipients), VerbisInventory.CleanList(body.TransferProviders), body.Measures?.Trim() ?? "", body.IsActive, Me.UserId, Me.Name);
        }
        catch (Npgsql.PostgresException e) when (e.SqlState == "42P01") { return MigrationMissing(); }
        await AuditAsync("VerbisInventory", key, "Created", new { body.Module, body.Activity }, ct);
        return Ok(new { id, key });
    }

    [HttpPut("verbis/items/{id:guid}")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> UpdateVerbisItem(Guid id, VerbisItemInput body, CancellationToken ct)
    {
        if (ValidateItem(body) is { } bad) return bad;
        var before = (await VerbisInventory.ListAsync(Db, Tenant, ct)).FirstOrDefault(i => i.Id == id);
        if (before is null) return NotFound();
        var after = before with
        {
            Module = body.Module.Trim(),
            Activity = body.Activity.Trim(),
            Subjects = VerbisInventory.CleanList(body.Subjects),
            DataCategories = VerbisInventory.CleanList(body.DataCategories),
            Purpose = body.Purpose.Trim(),
            LegalBasis = body.LegalBasis.Trim(),
            Special = body.Special,
            Retention = body.Retention.Trim(),
            RetentionCategory = string.IsNullOrWhiteSpace(body.RetentionCategory) ? null : body.RetentionCategory,
            Recipients = VerbisInventory.CleanList(body.Recipients),
            TransferProviders = VerbisInventory.CleanList(body.TransferProviders),
            Measures = body.Measures?.Trim() ?? "",
            IsActive = body.IsActive,
        };
        await Db.ExecuteAsync("""
            UPDATE governance_privacy_inventory SET "Module" = $3, "Activity" = $4, "Subjects" = $5, "DataCategories" = $6, "Purpose" = $7, "LegalBasis" = $8,
                "Special" = $9, "Retention" = $10, "RetentionCategory" = $11, "Recipients" = $12, "TransferProviders" = $13, "Measures" = $14, "IsActive" = $15,
                "UpdatedAt" = now(), "UpdatedBy" = $16, "UpdatedByName" = $17
            WHERE "TenantSlug" = $1 AND "Id" = $2
            """, ct, Tenant, id, after.Module, after.Activity, after.Subjects, after.DataCategories, after.Purpose, after.LegalBasis, after.Special, after.Retention,
            after.RetentionCategory, after.Recipients, after.TransferProviders, after.Measures, after.IsActive, Me.UserId, Me.Name);
        // Envanter kişisel veri içermez: değişen alanlar önceki/sonraki değerleriyle denetim kaydına yazılır.
        var changes = new Dictionary<string, object?>();
        void Diff(string name, object? a, object? b)
        {
            var ja = JsonSerializer.Serialize(a); var jb = JsonSerializer.Serialize(b);
            if (ja != jb) changes[name] = new { before = a, after = b };
        }
        Diff("module", before.Module, after.Module); Diff("activity", before.Activity, after.Activity); Diff("subjects", before.Subjects, after.Subjects);
        Diff("dataCategories", before.DataCategories, after.DataCategories); Diff("purpose", before.Purpose, after.Purpose);
        Diff("legalBasis", before.LegalBasis, after.LegalBasis); Diff("special", before.Special, after.Special); Diff("retention", before.Retention, after.Retention);
        Diff("retentionCategory", before.RetentionCategory, after.RetentionCategory); Diff("recipients", before.Recipients, after.Recipients);
        Diff("transferProviders", before.TransferProviders, after.TransferProviders); Diff("measures", before.Measures, after.Measures);
        Diff("isActive", before.IsActive, after.IsActive);
        if (changes.Count > 0) await AuditAsync("VerbisInventory", before.Key, "Updated", changes, ct);
        return Ok(new { id, changed = changes.Keys });
    }

    [HttpDelete("verbis/items/{id:guid}")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> DeleteVerbisItem(Guid id, CancellationToken ct)
    {
        var item = (await VerbisInventory.ListAsync(Db, Tenant, ct)).FirstOrDefault(i => i.Id == id);
        if (item is null) return NotFound();
        // Katalog faaliyeti silinmez (ürün o veriyi işliyor): yalnızca pasifleştirilebilir.
        if (item.Source != "Custom")
            return Conflict(new { message = L("Ürün kataloğundan gelen faaliyet silinemez; gerekiyorsa pasifleştirin.", "A catalog activity cannot be deleted; deactivate it instead.") });
        await Db.ExecuteAsync("DELETE FROM governance_privacy_inventory WHERE \"TenantSlug\" = $1 AND \"Id\" = $2", ct, Tenant, id);
        await AuditAsync("VerbisInventory", item.Key, "Deleted", new { item.Module, item.Activity }, ct);
        return NoContent();
    }

    /// <summary>VERBİS dışa aktarımı: csv (Excel), html (yazdırılabilir), json (arayüz XLSX üretir). Her indirme denetim kaydına yazılır.</summary>
    [HttpGet("verbis/export")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> VerbisExport([FromQuery] string format = "csv", CancellationToken ct = default)
    {
        if (format is not ("csv" or "html" or "json")) return BadRequest(new { message = L("Biçim csv, html ya da json olmalı.", "Format must be csv, html or json.") });
        List<VerbisInventory.Item> items;
        try { items = await VerbisInventory.ListAsync(Db, Tenant, ct); }
        catch (Npgsql.PostgresException e) when (e.SqlState == "42P01") { return MigrationMissing(); }
        if (items.Count == 0) return Conflict(new { message = L("Envanter boş; önce KVKK › Envanter ekranını açın.", "The inventory is empty; open KVKK › Inventory first.") });
        var rows = VerbisInventory.Rows(items, await TransfersAsync(ct), En);
        var last = items.OrderByDescending(i => i.UpdatedAt).First();
        await AuditAsync("VerbisInventory", Tenant, "Exported", new { format, rows = rows.Count, activities = items.Count(i => i.IsActive) }, ct);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd");
        switch (format)
        {
            case "csv":
                return File(Encoding.UTF8.GetBytes(VerbisInventory.Csv(rows, En)), "text/csv; charset=utf-8", $"verbis-envanter-{stamp}.csv");
            case "html":
                var company = (await Db.QueryAsync("SELECT \"Name\" FROM platform_tenants WHERE \"Slug\" = $1", r => r.GetString(0), ct, Tenant)).FirstOrDefault() ?? Tenant;
                return File(Encoding.UTF8.GetBytes(VerbisInventory.Html(company, rows, DateTime.UtcNow, last.UpdatedAt, last.UpdatedByName, items.Count(i => i.IsActive), En)),
                    "text/html; charset=utf-8", $"verbis-envanter-{stamp}.html");
            default:
                return Ok(new { columns = VerbisInventory.ColumnsFor(En), rows, lastUpdatedAt = last.UpdatedAt, lastUpdatedBy = last.UpdatedByName });
        }
    }

    /* ================================================================== 54) veri paketi */

    [HttpPost("requests/{id:guid}/package")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> CreatePackage(Guid id, CancellationToken ct)
    {
        var r = await _db.DataRequests.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return NotFound();
        if (r.Kind != "Access") return BadRequest(new { message = L("Veri paketi yalnızca bilgi/erişim başvurusunda hazırlanır.", "A data package is prepared only for access requests.") });
        if (r.EmployeeId is null) return BadRequest(new { message = L("Başvuru bir çalışan kaydına bağlı değil.", "The request is not linked to an employee record.") });
        if (!r.IdentityVerified)
            return BadRequest(new { message = L("Başvurucunun kimliği doğrulanmadan veri paketi hazırlanamaz.", "The package cannot be prepared before the applicant's identity is verified."), code = "identity_unverified" });
        if (r.Status is "Rejected" or "Withdrawn") return Conflict(new { message = L("Sonuçlanmış başvuruya paket hazırlanamaz.", "A package cannot be prepared for a closed request.") });
        try
        {
            var p = await DataRequestPackages.CreateAsync(Db, Tenant, id, People, Me.UserId, Me.Name, ct);
            return p is null ? NotFound() : Ok(p);
        }
        catch (Npgsql.PostgresException e) when (e.SqlState == "42P01") { return MigrationMissing(); }
        catch (InvalidOperationException e) { return StatusCode(413, new { message = e.Message }); }
    }

    /// <summary>Paketi indirir: İK her zaman; başvurucu, kendi başvurusu sonuçlandığında (Completed).</summary>
    [HttpGet("requests/{id:guid}/package")]
    public async Task<IActionResult> DownloadPackage(Guid id, CancellationToken ct)
    {
        var r = await _db.DataRequests.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return NotFound();
        var own = r.UserId == Me.UserId && r.UserId != "";
        if (!Me.IsHr && !(own && r.Status == "Completed" && r.IdentityVerified)) return NotFound();
        var row = (await Db.QueryAsync("""
            SELECT "Id","FileName","ContentEnc","Sha256","EmployeeId" FROM governance_data_request_packages
            WHERE "TenantSlug" = $1 AND "RequestId" = $2 AND "ExpiresAt" > now() ORDER BY "CreatedAt" DESC LIMIT 1
            """, x => (Id: x.GetGuid(0), File: x.GetString(1), Enc: x.GetString(2), Sha: x.GetString(3), Emp: x.GetGuid(4)), ct, Tenant, id)).FirstOrDefault();
        if (row.File is null) return NotFound(new { message = L("Hazır veri paketi yok.", "No data package is available.") });
        var bytes = Convert.FromBase64String(SecretBox.Unprotect(row.Enc) ?? "");
        await Db.ExecuteAsync("""
            UPDATE governance_data_request_packages SET "DownloadCount" = "DownloadCount" + 1, "LastDownloadedAt" = now() WHERE "Id" = $1
            """, ct, row.Id);
        // Erişim kaydı (K6): kişinin verisinin dışa aktarımı; çalışan kendi ekranında görür.
        await Db.ExecuteAsync("""
            INSERT INTO audit_log ("TenantSlug","Service","EntityType","EntityId","Action","Changes","UserId","UserName","CorrelationId","OccurredAt")
            VALUES ($1,'governance-service','PersonalDataExport',$2,'Exported',$3::jsonb,$4,$5,$6,now())
            """, ct, Tenant, row.Emp.ToString(), JsonSerializer.Serialize(new { requestId = id, package = row.Id, sha256 = row.Sha, by = own ? "applicant" : "hr" }),
            Me.UserId, Me.Name, Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? HttpContext.TraceIdentifier);
        return File(bytes, "application/zip", row.File);
    }

    /* ================================================================== 55) yeniden onay kampanyası */

    [HttpGet("consent-campaigns")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Campaigns(CancellationToken ct)
    {
        List<ConsentCampaigns.Campaign> list;
        try { list = await ConsentCampaigns.ListAsync(Db, Tenant, ct); }
        catch (Npgsql.PostgresException e) when (e.SqlState == "42P01") { return MigrationMissing(); }
        var types = await ConsentCatalog.EffectiveAsync(_db, ct);
        var people = await People.ListAsync(Tenant, ct);
        var latest = await ConsentCampaigns.LatestAsync(Db, Tenant, ct);
        return Ok(new
        {
            types = types.Select(t => new { t.Type, t.Title, t.Version, t.Required, hasCampaign = list.Any(c => c.Type == t.Type && c.Version == t.Version) }),
            campaigns = list.Select(c =>
            {
                var t = types.FirstOrDefault(x => x.Type == c.Type);
                var current = t is not null && t.Version == c.Version;
                var prog = t is null ? null : ConsentCampaigns.Compute(t.Required, c.Type, c.Version, people, latest);
                return new
                {
                    c.Id, type = c.Type, title = t?.Title ?? c.Type, required = t?.Required ?? false, c.Version, c.PreviousVersion, c.Status, c.StartedBy, c.StartedAt,
                    c.ClosedAt, c.ReminderCount, c.LastReminderAt, current,
                    target = prog?.Target ?? 0, done = prog?.Done ?? 0,
                    percent = prog is null || prog.Target == 0 ? 100 : (int)Math.Round(100.0 * prog.Done / prog.Target),
                    // Bekleyen listesi yalnızca açık ve güncel kampanyada (İK hatırlatma için görür).
                    pending = c.Status == "Open" && current && prog is not null
                        ? prog.Pending.Select(p => new { employeeId = p.Id, name = p.Name, department = p.Department }) : null,
                    canRemind = c.Status == "Open" && current && (c.LastReminderAt is null || c.LastReminderAt < DateTime.UtcNow.AddHours(-24)),
                };
            }),
        });
    }

    public record CampaignStartInput(string Type);

    /// <summary>Elle kampanya: yerleşik sürüm (ör. GORSEL_KULLANIM 2026.1) için yayın olayı olmadığından İK başlatır.</summary>
    [HttpPost("consent-campaigns")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> StartCampaign(CampaignStartInput body, CancellationToken ct)
    {
        var t = (await ConsentCatalog.EffectiveAsync(_db, ct)).FirstOrDefault(x => x.Type == body.Type);
        if (t is null) return BadRequest(new { message = L("Bilinmeyen metin tipi.", "Unknown notice type.") });
        try
        {
            var existing = (await ConsentCampaigns.ListAsync(Db, Tenant, ct)).FirstOrDefault(c => c.Type == t.Type && c.Version == t.Version);
            if (existing is not null)
                return Conflict(new { message = L("Bu sürüm için kampanya zaten var.", "A campaign already exists for this version."), id = existing.Id });
            await ConsentCampaigns.StartAsync(Db, Tenant, t.Type, t.Version, null, Me.Name, ct);
            var created = (await ConsentCampaigns.ListAsync(Db, Tenant, ct)).First(c => c.Type == t.Type && c.Version == t.Version);
            await AuditAsync("ConsentCampaign", created.Id.ToString(), "Started", new { t.Type, t.Version, manual = true }, ct);
            return Ok(new { created.Id });
        }
        catch (Npgsql.PostgresException e) when (e.SqlState == "42P01") { return MigrationMissing(); }
    }

    [HttpPost("consent-campaigns/{id:guid}/remind")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> RemindCampaign(Guid id, CancellationToken ct)
    {
        var c = (await ConsentCampaigns.ListAsync(Db, Tenant, ct)).FirstOrDefault(x => x.Id == id);
        if (c is null) return NotFound();
        var t = (await ConsentCatalog.EffectiveAsync(_db, ct)).FirstOrDefault(x => x.Type == c.Type);
        if (c.Status != "Open" || t is null || t.Version != c.Version)
            return Conflict(new { message = L("Kampanya kapalı ya da metnin daha yeni bir sürümü var.", "The campaign is closed or a newer version exists.") });
        if (c.LastReminderAt is { } at && at > DateTime.UtcNow.AddHours(-24))
            return StatusCode(429, new { message = L("Son 24 saatte hatırlatma gönderildi.", "A reminder was sent in the last 24 hours.") });
        var prog = ConsentCampaigns.Compute(t.Required, c.Type, c.Version, await People.ListAsync(Tenant, ct), await ConsentCampaigns.LatestAsync(Db, Tenant, ct));
        if (prog.Pending.Count == 0) return Ok(new { sent = 0 });
        var sent = await ConsentCampaigns.RemindAsync(Db, Tenant, c, t.Title, prog.Pending.Select(p => p.Id), Me.UserId, Me.Name, ct);
        return Ok(new { sent });
    }

    [HttpPost("consent-campaigns/{id:guid}/close")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> CloseCampaign(Guid id, CancellationToken ct)
    {
        var n = await Db.ExecuteAsync("""
            UPDATE governance_consent_campaigns SET "Status" = 'Closed', "ClosedAt" = now(), "ClosedBy" = $3
            WHERE "TenantSlug" = $1 AND "Id" = $2 AND "Status" = 'Open'
            """, ct, Tenant, id, Me.Name);
        if (n == 0) return NotFound();
        await AuditAsync("ConsentCampaign", id.ToString(), "Closed", new { }, ct);
        return Ok(new { closed = true });
    }

    /// <summary>
    /// Giriş bandı: oturumdaki kişinin yanıtlaması gereken metinler — açık kampanyası olan güncel sürümler ve
    /// hiç okunmamış aydınlatma metni. Rıza zorlanmaz: bant yalnızca bilgilendirir ve ilgili ekrana götürür.
    /// </summary>
    [HttpGet("consents/pending")]
    public async Task<IActionResult> PendingForMe(CancellationToken ct)
    {
        // Platform yöneticisi şirketin çalışanı değildir: bant gösterilmez, rıza kaydı da tutulmaz.
        if (Me.IsPlatformAdmin) return Ok(Array.Empty<object>());
        var types = await ConsentCatalog.EffectiveAsync(_db, ct);
        var mine = await _db.Consents.AsNoTracking().Where(c => c.UserId == Me.UserId).OrderByDescending(c => c.RecordedAt).ToListAsync(ct);
        List<ConsentCampaigns.Campaign> open;
        try { open = await ConsentCampaigns.ListAsync(Db, Tenant, ct, openOnly: true); }
        catch (Npgsql.PostgresException e) when (e.SqlState == "42P01") { open = new(); }
        var items = new List<object>();
        foreach (var t in types)
        {
            var last = mine.FirstOrDefault(c => c.ConsentType == t.Type);
            var latest = last is null ? null : new ConsentCampaigns.LatestConsent(last.UserId, last.Version, last.Granted);
            var campaign = open.FirstOrDefault(c => c.Type == t.Type && c.Version == t.Version);
            var neverRead = t.Required && (last is null || !last.Granted);
            var inCampaign = campaign is not null && ConsentCampaigns.IsTarget(t.Required, latest, t.Version) && !ConsentCampaigns.IsDone(t.Required, latest, t.Version);
            if (neverRead || inCampaign)
                items.Add(new { t.Type, t.Title, t.Version, t.Required, reason = neverRead && last is null ? "unread" : "updated" });
        }
        return Ok(items);
    }

    /* ================================================================== 56/57) imha doğrulama */

    public sealed record StorageLocation(string Location, string Store, string Personal, string Destruction);

    /// <summary>Ürünün dosya/ikili veri tuttuğu yerler ve imha yolu (rapor ekranında gösterilir; arayüz diline göre).</summary>
    public static StorageLocation[] StorageMap(bool en)
    {
        string T(string tr, string e) => en ? e : tr;
        return new StorageLocation[]
        {
            new("recruitment_candidates.ResumeStorageKey", T("Nesne deposu anahtarı (MinIO, STORAGE_PERSONAL_BUCKETS)", "Object storage key (MinIO, STORAGE_PERSONAL_BUCKETS)"),
                T("Evet (özgeçmiş)", "Yes (CV)"),
                T("Aday anonimleştirme/silme sırasında anahtar kuyruğa alınır, nesne silinir ve yokluğu doğrulanır",
                  "When a candidate is anonymized/deleted the key is queued, the object is deleted and its absence is verified")),
            new("expense_items.ReceiptStorageKey, expense_documents.StorageKey", T("Nesne deposu anahtarı", "Object storage key"), T("Evet (fiş, belge)", "Yes (receipt, document)"),
                T("VUK saklama yükümlülüğü: çalışan anonimleştirmesinde silinmez", "Tax Procedure Law retention duty: not deleted when an employee is anonymized")),
            new("governance_library_versions.StorageKey", T("Nesne deposu anahtarı", "Object storage key"), T("Hayır (şirket politikaları)", "No (company policies)"),
                T("Kişisel veri değil; belge kütüphanesinden yönetilir", "Not personal data; managed in the document library")),
            new("tenant-logos (MinIO)", T("Nesne deposu (herkese açık okuma)", "Object storage (public read)"), T("Hayır (şirket logosu)", "No (company logo)"),
                T("Kiracı silinince tenant-service siler", "Deleted by tenant-service when the tenant is deleted")),
            new("hr360-mlflow-artifacts (MinIO)", T("Nesne deposu", "Object storage"), T("Hayır (model dosyaları; eğitim satırı/kimlik saklanmaz)", "No (model files; no training rows or identities are stored)"),
                T("MLflow sürüm yönetimi", "MLflow version management")),
            new("learning_scorm_files", T("Veritabanı (bytea)", "Database (bytea)"), T("Hayır (eğitim içeriği)", "No (course content)"),
                T("Kurs silinince silinir", "Deleted when the course is deleted")),
            new("governance_document_requests.DocumentEnc", T("Veritabanı (AES-256-GCM)", "Database (AES-256-GCM)"), T("Evet (düzenlenen belge)", "Yes (issued document)"),
                T("\"Belge talepleri\" saklama politikası", "\"Document requests\" retention policy")),
            new("governance_data_request_packages.ContentEnc", T("Veritabanı (AES-256-GCM)", "Database (AES-256-GCM)"), T("Evet (veri paketi)", "Yes (data package)"),
                T($"{DataRequestPackages.KeepDays} gün sonra bakım turu siler; çalışan anonimleşince silinir",
                  $"Deleted by the maintenance run after {DataRequestPackages.KeepDays} days; deleted when the employee is anonymized")),
            new("compensation_payslip_deliveries.SealedCopy", T("Veritabanı (şifreli e-bordro kopyası)", "Database (encrypted e-payslip copy)"), T("Evet (bordro)", "Yes (payslip)"),
                T("\"Bordro pusulaları\" politikası (10 yıl)", "\"Payslips\" policy (10 years)")),
        };
    }

    [HttpGet("destruction-verification")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> DestructionVerification(CancellationToken ct)
    {
        object storage;
        object recent;
        try
        {
            storage = await Db.QueryAsync("""
                SELECT "Category","SourceTable","SourceColumn","Status",count(*)::int FROM governance_storage_deletions
                WHERE "TenantSlug" = $1 GROUP BY 1,2,3,4 ORDER BY 1,2,4
                """, r => new { category = r.GetString(0), table = r.GetString(1), column = r.GetString(2), status = r.GetString(3), count = r.GetInt32(4) }, ct, Tenant);
            recent = await Db.QueryAsync("""
                SELECT "Category","SourceTable","SourceColumn",left("KeyHash",12),"Status","Detail","Attempts","CreatedAt","ProcessedAt"
                FROM governance_storage_deletions WHERE "TenantSlug" = $1 ORDER BY "CreatedAt" DESC LIMIT 100
                """, r => new
            {
                category = r.GetString(0), table = r.GetString(1), column = r.GetString(2), keyHash = r.GetString(3), status = r.GetString(4),
                detail = r.Str(5), attempts = r.GetInt32(6), createdAt = r.GetFieldValue<DateTime>(7), processedAt = r.Ts(8),
            }, ct, Tenant);
        }
        catch (Npgsql.PostgresException e) when (e.SqlState == "42P01") { return MigrationMissing(); }

        // Kapsam denetimi: kişi kimliği taşıyan her tablo ya anonimleştirme adımlarında ya da gerekçeli istisnada olmalı.
        var personTables = await Db.QueryAsync("""
            SELECT DISTINCT table_name FROM information_schema.columns
            WHERE table_schema = current_schema() AND column_name IN ('EmployeeId','RecipientEmployeeId','ToEmployeeId','FromEmployeeId','CandidateId')
            ORDER BY 1
            """, r => r.GetString(0), ct);
        var covered = RetentionPlans.EmployeeSteps.Select(s => s.Table).Concat(RetentionPlans.CandidateAnonymizeSteps.Select(s => s.Table)).ToHashSet();
        var coverage = personTables.Select(t => new
        {
            table = t,
            status = covered.Contains(t) ? "covered" : RetentionPlans.ExemptReason(t) is not null ? "retained" : "review",
            reason = covered.Contains(t) ? null : RetentionPlans.ExemptReason(t),
        }).ToList();

        var logs = await Db.QueryAsync("""
            SELECT "Id","Category","Action","Affected","Trigger","Actor","RanAt","Details"::text FROM governance_destruction_logs
            WHERE "TenantSlug" = $1 ORDER BY "RanAt" DESC LIMIT 20
            """, r => new
        {
            id = r.GetGuid(0), category = r.GetString(1), label = Retention.LabelOf(r.GetString(1), En),
            action = r.GetString(2), affected = r.GetInt32(3), trigger = r.GetString(4), actor = r.GetString(5), ranAt = r.GetFieldValue<DateTime>(6),
            details = r.Str(7) is { } d ? JsonDocument.Parse(d).RootElement.Clone() : (JsonElement?)null,
        }, ct, Tenant);

        return Ok(new
        {
            storage, recent, coverage, logs,
            storageMap = StorageMap(En),
            steps = RetentionPlans.EmployeeSteps.Select(s => new { s.Table, s.Label, s.Kind }),
            retained = RetentionPlans.EmployeeRetained.Select(r => new { r.Table, r.Reason }),
        });
    }

    /// <summary>Bekleyen/başarısız dosya silmelerini şimdi işler (bakım turu saatte bir zaten dener).</summary>
    [HttpPost("destruction-verification/process")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> ProcessStorage(CancellationToken ct)
    {
        try
        {
            var n = await StorageDeletions.ProcessAsync(Db, _http, Tenant, ct);
            await AuditAsync("StorageDeletion", Tenant, "Processed", new { processed = n }, ct);
            return Ok(new { processed = n });
        }
        catch (Npgsql.PostgresException e) when (e.SqlState == "42P01") { return MigrationMissing(); }
    }
}

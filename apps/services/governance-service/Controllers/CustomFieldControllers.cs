using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using GovernanceService.Data;
using GovernanceService.Infrastructure;

namespace GovernanceService.Controllers;

/* ======================================================================
 * Y24 özel alanlar / form tasarımcısı (çalışan profili "Ek bilgiler").
 * KVKK: her alanın özel nitelik, hukuki sebep, amaç ve saklama süresi
 * oluştururken zorunludur; özel nitelikli alan onaylı gizlilik etki
 * değerlendirmesi ister (yoksa 409 pia_required) ve değeri şifrelenir.
 * Okuma G20 düzeylerine (everyone/manager/hr/self) göre süzülür.
 * ==================================================================== */
[Route("api/custom-fields")]
[Authorize]
public class CustomFieldsController : AppController
{
    private readonly GovernanceDbContext _db;
    public CustomFieldsController(GovernanceDbContext db) => _db = db;

    private async Task AuditAsync(string entityId, string action, object changes, CancellationToken ct)
    {
        try
        {
            await Db.ExecuteAsync("""
                INSERT INTO audit_log ("TenantSlug","Service","EntityType","EntityId","Action","Changes","UserId","UserName","IpAddress","OccurredAt")
                VALUES ($1,'governance-service','CustomField',$2,$3,$4::jsonb,$5,$6,$7,now())
                """, ct, Tenant, entityId, action, JsonSerializer.Serialize(changes), Me.UserId, Me.Name, Request.Headers["X-Real-IP"].FirstOrDefault());
        }
        catch (Npgsql.NpgsqlException) { /* denetim yazılamazsa iş akışı bozulmaz */ }
    }

    private object View(CustomFields.FieldRow f) => new
    {
        f.Id, f.Key, f.Label, f.Type, f.Options, f.Required, f.Visibility, f.SelfEditable, f.IsSpecialCategory,
        f.LegalBasis, legalBasisLabel = CustomFields.BasisLabel(f.LegalBasis), f.Purpose, f.RetentionMonths, f.AssessmentId,
        f.IsActive, f.SortOrder, f.CreatedBy, f.CreatedAt,
    };

    [HttpGet("meta")]
    public IActionResult Meta() => Ok(new
    {
        types = CustomFields.Types,
        levels = CustomFields.Levels,
        legalBases = CustomFields.LegalBases.Select(b => new { value = b.Code, label = b.Label, special = b.Special }),
        notice = L("Özel nitelikli kişisel veri (sağlık, din, sendika, biyometrik vb.) yalnızca zorunluysa toplanmalıdır; onaylı gizlilik etki değerlendirmesi gerekir.",
            "Special category personal data (health, religion, union, biometrics etc.) should only be collected when necessary; an approved privacy impact assessment is required."),
    });

    [HttpGet]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var fields = await CustomFields.ListAsync(Db, Tenant, ct);
        var counts = await Db.QueryAsync("SELECT \"FieldId\", count(*)::int FROM governance_custom_field_values WHERE \"TenantSlug\" = $1 GROUP BY 1",
            r => (r.GetGuid(0), r.GetInt32(1)), ct, Tenant);
        return Ok(fields.Select(f => new { field = View(f), values = counts.FirstOrDefault(c => c.Item1 == f.Id).Item2 }));
    }

    public record FieldInput(string Key, string Label, string Type, List<string>? Options, bool Required, string Visibility, bool SelfEditable,
        bool? IsSpecialCategory, string? LegalBasis, string? Purpose, int? RetentionMonths, Guid? AssessmentId, int? SortOrder, bool? IsActive);

    private CustomFields.Definition Def(FieldInput b) => new(
        (b.Key ?? "").Trim(), (b.Label ?? "").Trim(), b.Type ?? "", b.Type == "select" ? (b.Options ?? new()).Select(o => o.Trim()).Where(o => o.Length > 0).ToList() : null,
        b.Required, b.Visibility ?? "", b.SelfEditable, b.IsSpecialCategory ?? false, b.LegalBasis ?? "", (b.Purpose ?? "").Trim(), b.RetentionMonths ?? 0);

    /// <summary>Özel nitelikli alan için onaylı "CustomField" gizlilik etki değerlendirmesi (kimliği ya da konusu alanın anahtarı/etiketi).</summary>
    private async Task<Guid?> ApprovedAssessmentAsync(Guid? id, string key, string label, CancellationToken ct)
    {
        var list = await _db.PrivacyAssessments.AsNoTracking().Where(a => a.Kind == "CustomField" && a.Status == "Approved").ToListAsync(ct);
        var hit = id is not null ? list.FirstOrDefault(a => a.Id == id)
            : list.FirstOrDefault(a => string.Equals(a.Subject.Trim(), key, StringComparison.OrdinalIgnoreCase) || string.Equals(a.Subject.Trim(), label, StringComparison.OrdinalIgnoreCase));
        return hit?.Id;
    }

    [HttpPost]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Create(FieldInput body, CancellationToken ct)
    {
        // KVKK üst verisi oluştururken zorunlu: özel nitelik açıkça belirtilmeli.
        if (body.IsSpecialCategory is null || body.RetentionMonths is null || string.IsNullOrWhiteSpace(body.LegalBasis) || string.IsNullOrWhiteSpace(body.Purpose))
            return BadRequest(new
            {
                message = L("KVKK bilgileri zorunlu: özel nitelikli mi, hukuki sebep, işleme amacı ve saklama süresi.",
                    "KVKK metadata is mandatory: special category, legal basis, purpose and retention period."),
                code = "kvkk_metadata_required",
            });
        var d = Def(body);
        if (CustomFields.Validate(d) is { } err) return BadRequest(new { message = L(err.Tr, err.En) });
        Guid? pia = null;
        if (d.IsSpecialCategory)
        {
            pia = await ApprovedAssessmentAsync(body.AssessmentId, d.Key, d.Label, ct);
            if (pia is null)
                return Conflict(new
                {
                    message = L("Özel nitelikli alan için onaylı bir gizlilik etki değerlendirmesi (tür: Özel alan) gerekir. KVKK › Etki değerlendirmesi ekranından hazırlayıp onaylatın.",
                        "A special category field requires an approved privacy impact assessment (kind: Custom field). Prepare and approve one under KVKK › Impact assessment."),
                    code = "pia_required",
                });
        }
        if (await Db.ScalarAsync("SELECT 1 FROM governance_custom_fields WHERE \"TenantSlug\" = $1 AND \"Target\" = 'Employee' AND \"Key\" = $2", ct, Tenant, d.Key) is not null)
            return Conflict(new { message = L("Bu anahtarla bir alan zaten var.", "A field with this key already exists."), code = "duplicate_key" });
        var id = Guid.NewGuid();
        await Db.ExecuteAsync("""
            INSERT INTO governance_custom_fields ("Id","TenantSlug","Target","Key","Label","Type","Options","Required","Visibility","SelfEditable",
                "IsSpecialCategory","LegalBasis","Purpose","RetentionMonths","AssessmentId","IsActive","SortOrder","CreatedBy","CreatedAt","UpdatedAt")
            VALUES ($1,$2,'Employee',$3,$4,$5,$6::jsonb,$7,$8,$9,$10,$11,$12,$13,$14,true,$15,$16,now(),now())
            """, ct, id, Tenant, d.Key, d.Label, d.Type, JsonSerializer.Serialize(d.Options ?? new()), d.Required, d.Visibility, d.SelfEditable,
            d.IsSpecialCategory, d.LegalBasis, d.Purpose, d.RetentionMonths, pia, body.SortOrder ?? 0, Me.Name);
        await AuditAsync(id.ToString(), "Created", new { d.Key, d.Type, d.IsSpecialCategory, d.LegalBasis, d.RetentionMonths, assessment = pia }, ct);
        var f = (await CustomFields.ListAsync(Db, Tenant, ct)).First(x => x.Id == id);
        return Ok(View(f));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Update(Guid id, FieldInput body, CancellationToken ct)
    {
        var f = (await CustomFields.ListAsync(Db, Tenant, ct)).FirstOrDefault(x => x.Id == id);
        if (f is null) return NotFound();
        // Anahtar, tür ve özel nitelik değişmez (değişmesi gerekiyorsa yeni alan açılır).
        var d = Def(body with { Key = f.Key, Type = f.Type, IsSpecialCategory = f.IsSpecialCategory,
            LegalBasis = body.LegalBasis ?? f.LegalBasis, Purpose = body.Purpose ?? f.Purpose, RetentionMonths = body.RetentionMonths ?? f.RetentionMonths });
        if (CustomFields.Validate(d) is { } err) return BadRequest(new { message = L(err.Tr, err.En) });
        await Db.ExecuteAsync("""
            UPDATE governance_custom_fields SET "Label" = $3, "Options" = $4::jsonb, "Required" = $5, "Visibility" = $6, "SelfEditable" = $7,
                "LegalBasis" = $8, "Purpose" = $9, "RetentionMonths" = $10, "IsActive" = $11, "SortOrder" = $12, "UpdatedAt" = now()
            WHERE "TenantSlug" = $1 AND "Id" = $2
            """, ct, Tenant, id, d.Label, JsonSerializer.Serialize(d.Options ?? new()), d.Required, d.Visibility, d.SelfEditable,
            d.LegalBasis, d.Purpose, d.RetentionMonths, body.IsActive ?? f.IsActive, body.SortOrder ?? f.SortOrder);
        await AuditAsync(id.ToString(), "Updated", new { d.Visibility, d.LegalBasis, d.RetentionMonths, isActive = body.IsActive ?? f.IsActive }, ct);
        return Ok(View((await CustomFields.ListAsync(Db, Tenant, ct)).First(x => x.Id == id)));
    }

    /// <summary>Alanı ve tüm değerlerini siler (imha tutanağına yazılır).</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var f = (await CustomFields.ListAsync(Db, Tenant, ct)).FirstOrDefault(x => x.Id == id);
        if (f is null) return NotFound();
        var values = Convert.ToInt32(await Db.ScalarAsync("SELECT count(*)::int FROM governance_custom_field_values WHERE \"TenantSlug\" = $1 AND \"FieldId\" = $2", ct, Tenant, id));
        await Db.ExecuteAsync("DELETE FROM governance_custom_fields WHERE \"TenantSlug\" = $1 AND \"Id\" = $2", ct, Tenant, id);
        if (values > 0)
        {
            Retention.Log(_db, Tenant, "CustomFieldValues", "Delete", values, f.RetentionMonths, "Manual", Me.Name);
            await _db.SaveChangesAsync(ct);
        }
        await AuditAsync(id.ToString(), "Deleted", new { f.Key, values }, ct);
        return NoContent();
    }

    /* ------------------------------------------------------------------ değerler */

    private async Task<(string Viewer, Person? Target, Person? Me)> ViewerAsync(Guid employeeId, CancellationToken ct)
    {
        var me = await MyPersonAsync(ct);
        var target = await People.FindAsync(Tenant, employeeId, ct);
        if (target is null) return ("none", null, me);
        if (me?.Id == employeeId) return ("self", target, me);
        if (Me.IsHr) return ("hr", target, me);
        if (me is not null && Me.IsManager && (await People.TeamOfAsync(Tenant, me.Id, ct)).Any(p => p.Id == employeeId)) return ("manager", target, me);
        return ("other", target, me);
    }

    [HttpGet("values/me")]
    public async Task<IActionResult> MyValues(CancellationToken ct)
    {
        var me = await MyPersonAsync(ct);
        if (me is null) return Ok(new { employeeId = (Guid?)null, viewer = "none", canEdit = false, fields = Array.Empty<object>() });
        return await Values(me.Id, ct);
    }

    [HttpGet("values/{employeeId:guid}")]
    public async Task<IActionResult> Values(Guid employeeId, CancellationToken ct)
    {
        var (viewer, target, _) = await ViewerAsync(employeeId, ct);
        if (target is null) return NotFound();
        var fields = (await CustomFields.ListAsync(Db, Tenant, ct, activeOnly: true)).Where(f => CustomFields.CanSee(f.Visibility, viewer)).ToList();
        var stored = await Db.QueryAsync("SELECT \"FieldId\", \"Value\", \"UpdatedAt\" FROM governance_custom_field_values WHERE \"TenantSlug\" = $1 AND \"EmployeeId\" = $2",
            r => (Field: r.GetGuid(0), Value: r.GetString(1), At: r.GetFieldValue<DateTime>(2)), ct, Tenant, employeeId);
        var revealed = new List<string>();
        // Güvenlik dalgası 2B: toplu görüntüleme uyarısıyla geçici engellenen kullanıcıya özel nitelikli değer açılmaz.
        var blocked = viewer != "self" && fields.Any(f => f.IsSpecialCategory)
            && await SecuritySettingsStore.IsBlockedAsync(Db, Tenant, Me.UserId, ct);
        var list = fields.Select(f =>
        {
            var s = stored.FirstOrDefault(x => x.Field == f.Id);
            string? value = s.Value is null || (blocked && f.IsSpecialCategory) ? null : CustomFields.Open(s.Value);
            if (f.IsSpecialCategory && value is not null && viewer != "self") revealed.Add(f.Key);
            return new
            {
                f.Id, f.Key, f.Label, f.Type, f.Options, f.Required, f.IsSpecialCategory, f.Visibility,
                value = CustomFields.Typed(f.Type, value), updatedAt = s.Value is null ? (DateTime?)null : s.At,
                editable = viewer == "hr" || (viewer == "self" && (f.SelfEditable || Me.IsHr)),
            };
        }).ToList();
        // Özel nitelikli değeri başkası (İK) görüntülediyse erişim kaydı.
        foreach (var key in revealed)
            await AuditAsync(employeeId.ToString(), "SensitiveViewed", new { field = $"custom:{key}" }, ct);
        return Ok(new { employeeId, viewer, canEdit = list.Any(x => x.editable), fields = list, sensitiveBlocked = blocked });
    }

    public record ValuesInput(Dictionary<string, JsonElement?> Values);

    [HttpPut("values/{employeeId:guid}")]
    public async Task<IActionResult> SetValues(Guid employeeId, ValuesInput body, CancellationToken ct)
    {
        var (viewer, target, _) = await ViewerAsync(employeeId, ct);
        if (target is null) return NotFound();
        if (viewer is not ("self" or "hr")) return StatusCode(403, new { message = L("Bu çalışanın ek bilgilerini düzenleyemezsiniz.", "You cannot edit this employee's additional information.") });
        if (body.Values is null || body.Values.Count == 0) return BadRequest(new { message = L("Değer yok.", "No values.") });
        var fields = await CustomFields.ListAsync(Db, Tenant, ct, activeOnly: true);
        var changes = new List<(CustomFields.FieldRow F, string? V)>();
        foreach (var (key, raw) in body.Values)
        {
            var f = fields.FirstOrDefault(x => x.Key == key);
            if (f is null) return BadRequest(new { message = L($"Bilinmeyen alan: {key}", $"Unknown field: {key}") });
            var canEdit = viewer == "hr" || Me.IsHr || f.SelfEditable;
            if (!canEdit) return StatusCode(403, new { message = L($"'{f.Label}' alanını yalnızca İK düzenleyebilir.", $"Only HR can edit '{f.Label}'."), field = key });
            var (ok, v, eTr, eEn) = CustomFields.Normalize(f.Type, f.Options, raw);
            if (!ok) return BadRequest(new { message = $"{f.Label}: {L(eTr!, eEn!)}", field = key });
            if (v is null && f.Required) return BadRequest(new { message = L($"'{f.Label}' zorunlu bir alandır.", $"'{f.Label}' is required."), field = key });
            changes.Add((f, v));
        }
        foreach (var (f, v) in changes)
        {
            if (v is null)
                await Db.ExecuteAsync("DELETE FROM governance_custom_field_values WHERE \"TenantSlug\" = $1 AND \"FieldId\" = $2 AND \"EmployeeId\" = $3", ct, Tenant, f.Id, employeeId);
            else
                await Db.ExecuteAsync("""
                    INSERT INTO governance_custom_field_values ("Id","TenantSlug","FieldId","EmployeeId","Value","UpdatedBy","UpdatedAt")
                    VALUES ($1,$2,$3,$4,$5,$6,now())
                    ON CONFLICT ("FieldId","EmployeeId") DO UPDATE SET "Value" = EXCLUDED."Value", "UpdatedBy" = EXCLUDED."UpdatedBy", "UpdatedAt" = now()
                    """, ct, Guid.NewGuid(), Tenant, f.Id, employeeId, CustomFields.Seal(f.IsSpecialCategory, v), Me.Name);
        }
        // Değerler denetime yazılmaz; yalnızca hangi alanların değiştiği.
        await AuditAsync(employeeId.ToString(), "ValuesUpdated", new { fields = changes.Select(c => c.F.Key), by = viewer }, ct);
        return await Values(employeeId, ct);
    }
}

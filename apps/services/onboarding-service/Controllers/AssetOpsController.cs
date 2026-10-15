using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OnboardingService.Data;
using OnboardingService.Models;
using OnboardingService.Services;

namespace OnboardingService.Controllers;

/// <summary>
/// G16: zimmet QR etiketi okutma, beklenen iade tarihi, kayıp/kayıttan düşme ve bakım kaydı.
/// QR etiketi yalnızca opak bir demirbaş kodu taşır (kişisel veri yok). Okutulan demirbaşın
/// kimde olduğunu İK/BT görür (erişim kaydı tutulur); çalışan yalnızca kendi zimmetini görür.
/// </summary>
[ApiController]
[Route("api/assets")]
[Authorize]
public class AssetOpsController : ControllerBase
{
    private readonly OnboardingDbContext _db;
    private readonly Sql _sql;
    private readonly EmployeeDirectoryClient _employees;
    private readonly OnboardingService.Tenancy.ITenantContext _tenant;

    public AssetOpsController(OnboardingDbContext db, Sql sql, EmployeeDirectoryClient employees, OnboardingService.Tenancy.ITenantContext tenant)
    {
        _db = db; _sql = sql; _employees = employees; _tenant = tenant;
    }

    private string Tenant => _tenant.TenantSlug ?? "";
    private bool CanManage => User.IsInRole("hr-admin") || User.IsInRole("tenant-admin")
        || User.IsInRole("platform-admin") || User.IsInRole("ext-asset-manage");
    private string UserName => User.FindFirst("name")?.Value ?? User.FindFirst("preferred_username")?.Value ?? "";

    /* ------------------------------------------------------------------ QR okutma */

    [HttpGet("scan")]
    public async Task<IActionResult> Scan([FromQuery] string? kod, [FromQuery] string? code, CancellationToken ct)
    {
        var c = AssetCodes.Normalize(kod ?? code);
        if (c.Length < 8) return BadRequest(new { message = "Geçersiz etiket kodu" });
        var asset = await _db.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.QrCode == c, ct);
        if (asset is null) return NotFound(new { message = "Etiket bulunamadı" });
        var open = await _db.AssetAssignments.AsNoTracking()
            .Where(a => a.AssetId == asset.Id && a.ReturnedOn == null).FirstOrDefaultAsync(ct);

        if (!CanManage)
        {
            // Çalışan yalnızca kendisine zimmetli demirbaşı görür; başkasınınkini "bulunamadı" olarak.
            var me = await _employees.FindMyEmployeeIdAsync(ct);
            if (me is null || open?.EmployeeId != me) return NotFound(new { message = "Etiket bulunamadı" });
        }

        PersonInfo? holder = open is null ? null : await People.FindAsync(_sql, Tenant, open.EmployeeId, ct);
        var lastMaintenance = await _db.AssetMaintenance.AsNoTracking().Where(m => m.AssetId == asset.Id)
            .OrderByDescending(m => m.Date).FirstOrDefaultAsync(ct);
        if (CanManage && open is not null)
            await Audit.WriteAsync(_sql, HttpContext, Tenant, "Asset", asset.Id.ToString(), "SensitiveViewed", new { field = "assetHolder", via = "qr" });

        return Ok(new
        {
            asset = new { asset.Id, asset.AssetTag, asset.Type, asset.Model, asset.SerialNumber, asset.Status, asset.QrCode },
            holder = open is null ? null : new
            {
                employeeId = open.EmployeeId, name = holder?.FullName, department = holder?.Department,
                open.AssignedOn, open.ExpectedReturnOn,
                overdue = open.ExpectedReturnOn is { } e && e < BusinessClock.Today,
            },
            lastMaintenance = CanManage && lastMaintenance is not null
                ? new { lastMaintenance.Date, lastMaintenance.Type, lastMaintenance.NextMaintenanceOn } : null,
            canManage = CanManage,
        });
    }

    /* ------------------------------------------------------------------ iade tarihi */

    public record ExpectedReturnInput(DateOnly? ExpectedReturnOn);

    [HttpPut("{id:guid}/expected-return")]
    [Authorize(Policy = "RequireAssetManage")]
    public async Task<IActionResult> SetExpectedReturn(Guid id, [FromBody] ExpectedReturnInput b, CancellationToken ct)
    {
        var open = await _db.AssetAssignments.FirstOrDefaultAsync(a => a.AssetId == id && a.ReturnedOn == null, ct);
        if (open is null) return BadRequest(new { message = "Bu demirbaşın açık zimmeti yok" });
        if (b.ExpectedReturnOn is { } e && e < open.AssignedOn)
            return BadRequest(new { message = "Beklenen iade tarihi, zimmet tarihinden önce olamaz" });
        open.ExpectedReturnOn = b.ExpectedReturnOn;
        // Tarih değişince hatırlatmalar yeniden gönderilebilir.
        open.ReminderBeforeSentAt = null;
        open.ReminderOverdueSentAt = null;
        await _db.SaveChangesAsync(ct);
        return Ok(open);
    }

    /// <summary>İadesi yaklaşan / geciken zimmetler (İK/BT).</summary>
    [HttpGet("returns-due")]
    [Authorize(Policy = "RequireAssetManage")]
    public async Task<IActionResult> ReturnsDue([FromQuery] int days = 14, CancellationToken ct = default)
    {
        var limit = BusinessClock.Today.AddDays(Math.Clamp(days, 0, 365));
        var rows = await _db.AssetAssignments.AsNoTracking().Include(a => a.Asset)
            .Where(a => a.ReturnedOn == null && a.ExpectedReturnOn != null && a.ExpectedReturnOn <= limit)
            .OrderBy(a => a.ExpectedReturnOn).ToListAsync(ct);
        var people = await People.FindManyAsync(_sql, Tenant, rows.Select(r => r.EmployeeId), ct);
        var today = BusinessClock.Today;
        return Ok(rows.Select(r => new
        {
            assignmentId = r.Id, r.AssetId, assetTag = r.Asset?.AssetTag, type = r.Asset?.Type, model = r.Asset?.Model,
            r.EmployeeId, holder = people.TryGetValue(r.EmployeeId, out var p) ? p.FullName : null,
            r.AssignedOn, r.ExpectedReturnOn, overdue = r.ExpectedReturnOn < today,
            r.ReminderBeforeSentAt, r.ReminderOverdueSentAt,
        }));
    }

    /* ------------------------------------------------------------------ kayıp / kayıttan düşme */

    public record WriteOffInput(string Kind, string Note, DateOnly? Date);

    /// <summary>
    /// Açık zimmeti "kayıp" ya da "kayıttan düşüldü (hurda)" olarak kapatır; not zorunludur.
    /// Offboarding'deki İK istisnası da bu uca yazar (kullanıcının jetonuyla).
    /// </summary>
    [HttpPost("{id:guid}/write-off")]
    [Authorize(Policy = "RequireAssetManage")]
    public async Task<IActionResult> WriteOff(Guid id, [FromBody] WriteOffInput b, CancellationToken ct)
    {
        if (b.Kind is not ("Lost" or "WrittenOff")) return BadRequest(new { message = "Tür Lost ya da WrittenOff olmalı" });
        if (string.IsNullOrWhiteSpace(b.Note) || b.Note.Length > 500) return BadRequest(new { message = "Açıklama zorunlu (en fazla 500 karakter)" });
        var asset = await _db.Assets.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (asset is null) return NotFound();
        var open = await _db.AssetAssignments.FirstOrDefaultAsync(a => a.AssetId == id && a.ReturnedOn == null, ct);
        var date = b.Date ?? BusinessClock.Today;
        if (open is not null)
        {
            if (date < open.AssignedOn) return BadRequest(new { message = "Tarih, zimmet tarihinden önce olamaz" });
            open.ReturnedOn = date;
            open.ConditionOnReturn = (b.Kind == "Lost" ? "Kayıp: " : "Kayıttan düşüldü: ") + b.Note.Trim();
        }
        asset.Status = b.Kind == "Lost" ? AssetStatus.Lost : AssetStatus.Retired;
        await _db.SaveChangesAsync(ct);
        await Audit.WriteAsync(_sql, HttpContext, Tenant, "Asset", id.ToString(), b.Kind == "Lost" ? "MarkedLost" : "WrittenOff",
            new { asset.AssetTag, assignmentId = open?.Id, note = b.Note.Trim() });
        return Ok(new { asset.Id, asset.Status, assignmentId = open?.Id });
    }

    /* ------------------------------------------------------------------ bakım */

    public record MaintenanceInput(DateOnly Date, MaintenanceType Type, decimal? Cost, string? Vendor, string? Notes, DateOnly? NextMaintenanceOn);

    [HttpGet("{id:guid}/maintenance")]
    [Authorize(Policy = "RequireAssetManage")]
    public async Task<IActionResult> Maintenance(Guid id, CancellationToken ct) =>
        Ok(await _db.AssetMaintenance.AsNoTracking().Where(m => m.AssetId == id).OrderByDescending(m => m.Date).ToListAsync(ct));

    [HttpPost("{id:guid}/maintenance")]
    [Authorize(Policy = "RequireAssetManage")]
    public async Task<IActionResult> AddMaintenance(Guid id, [FromBody] MaintenanceInput b, CancellationToken ct)
    {
        if (!await _db.Assets.AnyAsync(a => a.Id == id, ct)) return NotFound();
        if (b.Cost is < 0) return BadRequest(new { message = "Maliyet negatif olamaz" });
        if (b.NextMaintenanceOn is { } n && n < b.Date) return BadRequest(new { message = "Sonraki bakım tarihi, bakım tarihinden önce olamaz" });
        if (b.Vendor is { Length: > 200 } || b.Notes is { Length: > 1000 }) return BadRequest(new { message = "Servis en fazla 200, not en fazla 1000 karakter olabilir" });
        var m = new AssetMaintenance
        {
            AssetId = id, Date = b.Date, Type = b.Type, Cost = b.Cost,
            Vendor = string.IsNullOrWhiteSpace(b.Vendor) ? null : b.Vendor.Trim(),
            Notes = string.IsNullOrWhiteSpace(b.Notes) ? null : b.Notes.Trim(),
            NextMaintenanceOn = b.NextMaintenanceOn, CreatedBy = UserName,
        };
        _db.AssetMaintenance.Add(m);
        await _db.SaveChangesAsync(ct);
        return Ok(m);
    }

    [HttpDelete("{id:guid}/maintenance/{mid:guid}")]
    [Authorize(Policy = "RequireAssetManage")]
    public async Task<IActionResult> DeleteMaintenance(Guid id, Guid mid, CancellationToken ct)
    {
        var m = await _db.AssetMaintenance.FirstOrDefaultAsync(x => x.Id == mid && x.AssetId == id, ct);
        if (m is null) return NotFound();
        _db.AssetMaintenance.Remove(m);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Bakımı gelen demirbaşlar: son kaydın "sonraki bakım" tarihi bugün + gün içinde ya da geçmiş.</summary>
    [HttpGet("maintenance/due")]
    [Authorize(Policy = "RequireAssetManage")]
    public async Task<IActionResult> MaintenanceDue([FromQuery] int days = 30, CancellationToken ct = default)
    {
        var limit = BusinessClock.Today.AddDays(Math.Clamp(days, 0, 365));
        var latest = await _db.AssetMaintenance.AsNoTracking()
            .GroupBy(m => m.AssetId)
            .Select(g => g.OrderByDescending(m => m.Date).ThenByDescending(m => m.CreatedAt).First())
            .ToListAsync(ct);
        var due = latest.Where(m => m.NextMaintenanceOn != null && m.NextMaintenanceOn <= limit).ToList();
        var ids = due.Select(d => d.AssetId).ToList();
        var assets = await _db.Assets.AsNoTracking()
            .Where(a => ids.Contains(a.Id) && a.Status != AssetStatus.Retired && a.Status != AssetStatus.Lost)
            .ToDictionaryAsync(a => a.Id, ct);
        var today = BusinessClock.Today;
        return Ok(due.Where(d => assets.ContainsKey(d.AssetId)).OrderBy(d => d.NextMaintenanceOn).Select(d => new
        {
            d.AssetId, assets[d.AssetId].AssetTag, assets[d.AssetId].Type, assets[d.AssetId].Model,
            lastDate = d.Date, lastType = d.Type, d.Vendor, d.NextMaintenanceOn, overdue = d.NextMaintenanceOn < today,
        }));
    }
}

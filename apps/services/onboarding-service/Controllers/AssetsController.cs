using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OnboardingService.Data;
using OnboardingService.Models;
using OnboardingService.Services;

namespace OnboardingService.Controllers;

[ApiController]
[Route("api/assets")]
[Authorize]
public class AssetsController : ControllerBase
{
    private readonly OnboardingDbContext _db;
    private readonly Sql _sql;
    private readonly EmployeeDirectoryClient _employees;
    private readonly OnboardingService.Tenancy.ITenantContext _tenant;
    public AssetsController(OnboardingDbContext db, Sql sql, EmployeeDirectoryClient employees, OnboardingService.Tenancy.ITenantContext tenant)
    {
        _db = db; _sql = sql; _employees = employees; _tenant = tenant;
    }

    private string Tenant => _tenant.TenantSlug ?? "";

    /// <summary>"RequireAssetManage" ile ayni rol kumesi (IK / BT zimmet sorumlusu).</summary>
    private bool CanManage => User.IsInRole("hr-admin") || User.IsInRole("tenant-admin")
        || User.IsInRole("platform-admin") || User.IsInRole("ext-asset-manage");

    /// <summary>
    /// NOT: Onceki halinde bu uc ham Asset entity'sini donuyordu -
    /// Asset'in AssignedEmployeeId/AssignedOn gibi duz (flat) alanlari HIC
    /// YOK (sadece List&lt;AssetAssignment&gt; Assignments var) ve bu uc
    /// Assignments'i Include ETMIYORDU. Frontend (AssetsPage.tsx "Zimmetli"
    /// sutunu) a.assignedEmployeeId/a.assignedOn okuyordu - HER ZAMAN
    /// undefined, yani zimmetli bir demirbas bile "—" (bos) gorunuyordu
    /// (hardcore test sirasinda bulundu, 3. tur). Acik (ReturnedOn == null)
    /// atama varsa duz alanlar olarak eklenir.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] AssetStatus? status, [FromQuery] AssetType? type)
    {
        var q = _db.Assets.AsQueryable();
        if (status.HasValue) q = q.Where(a => a.Status == status.Value);
        if (type.HasValue) q = q.Where(a => a.Type == type.Value);
        var assets = await q.OrderBy(a => a.AssetTag).ToListAsync();

        var openAssignments = await _db.AssetAssignments
            .Where(a => a.ReturnedOn == null)
            .ToDictionaryAsync(a => a.AssetId, a => a);

        return Ok(assets.Select(a =>
        {
            openAssignments.TryGetValue(a.Id, out var open);
            return new
            {
                a.Id, a.AssetTag, a.Type, a.Model, a.SerialNumber, a.Status, a.CreatedAt, a.QrCode,
                AssignedEmployeeId = open?.EmployeeId,
                AssignedOn = open?.AssignedOn,
                ExpectedReturnOn = open?.ExpectedReturnOn,
            };
        }));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var a = await _db.Assets
            .Include(x => x.Assignments.OrderByDescending(s => s.AssignedOn))
            .FirstOrDefaultAsync(x => x.Id == id);
        if (a is null) return NotFound();

        var open = a.Assignments.FirstOrDefault(s => s.ReturnedOn == null);
        return Ok(new
        {
            a.Id, a.AssetTag, a.Type, a.Model, a.SerialNumber, a.Status, a.CreatedAt, a.QrCode,
            AssignedEmployeeId = open?.EmployeeId,
            AssignedOn = open?.AssignedOn,
            ExpectedReturnOn = open?.ExpectedReturnOn,
            Assignments = a.Assignments,
        });
    }

    [HttpPost]
    [Authorize(Policy = "RequireAssetManage")]
    public async Task<IActionResult> Create([FromBody] CreateAssetRequest request)
    {
        if (await _db.Assets.AnyAsync(a => a.AssetTag == request.AssetTag))
            return Conflict("Bu zimmet etiketi zaten kayıtlı");

        var asset = new Asset
        {
            AssetTag = request.AssetTag,
            Type = request.Type,
            Model = request.Model,
            SerialNumber = request.SerialNumber,
            QrCode = AssetCodes.New(),
        };
        _db.Assets.Add(asset);
        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id = asset.Id }, asset);
    }

    [HttpPost("{id}/assign")]
    [Authorize(Policy = "RequireAssetManage")]
    public async Task<IActionResult> Assign(Guid id, [FromBody] AssignAssetRequest request)
    {
        var asset = await _db.Assets.FirstOrDefaultAsync(a => a.Id == id);
        if (asset is null) return NotFound();
        if (asset.Status != AssetStatus.Available)
            return BadRequest($"Zimmet uygun durumda değil (mevcut: {asset.Status})");

        var assignment = new AssetAssignment
        {
            AssetId = id,
            EmployeeId = request.EmployeeId,
            AssignedOn = request.AssignedOn,
            Notes = request.Notes,
            ExpectedReturnOn = request.ExpectedReturnOn,
        };
        if (request.ExpectedReturnOn is { } exp && exp < request.AssignedOn)
            return BadRequest("Beklenen iade tarihi, zimmet tarihinden önce olamaz");
        asset.Status = AssetStatus.Assigned;

        _db.AssetAssignments.Add(assignment);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Esz. zamanli ikinci atama: tek acik atama kisitina (UX_onboarding_asset_
            // assignments_open) takildi. Onceden bu durum iki acik atama birakiyor ve
            // tum zimmet listesi 500 veriyordu.
            return Conflict("Bu zimmet az önce başka birine atandı");
        }
        return CreatedAtAction(nameof(GetById), new { id }, assignment);
    }

    [HttpPost("{id}/return")]
    [Authorize(Policy = "RequireAssetManage")]
    public async Task<IActionResult> Return(Guid id, [FromBody] ReturnAssetRequest request)
    {
        var asset = await _db.Assets.FirstOrDefaultAsync(a => a.Id == id);
        if (asset is null) return NotFound();

        var open = await _db.AssetAssignments
            .Where(a => a.AssetId == id && a.ReturnedOn == null)
            .OrderByDescending(a => a.AssignedOn)
            .FirstOrDefaultAsync();
        if (open is null) return BadRequest("Bu zimmetin acik atamasi yok");
        if (request.ReturnedOn < open.AssignedOn)
            return BadRequest("İade tarihi, atama tarihinden önce olamaz");

        open.ReturnedOn = request.ReturnedOn;
        open.ConditionOnReturn = request.Condition;
        asset.Status = request.MarkAsRetired ? AssetStatus.Retired : AssetStatus.Available;

        await _db.SaveChangesAsync();
        return Ok(open);
    }

    /// <summary>
    /// Demirbaş bilgisini düzeltir (etiket, tür, model, seri no). Etiket kiracı içinde tekildir (409).
    /// Durum bu uçla değişmez: zimmet/iade/durum uçları ayrıdır.
    /// </summary>
    [HttpPut("{id:guid}")]
    [Authorize(Policy = "RequireAssetManage")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateAssetRequest request, CancellationToken ct)
    {
        var tag = request.AssetTag?.Trim() ?? "";
        if (tag.Length is < 2 or > 100) return BadRequest(new { message = "Demirbaş no 2-100 karakter olmalı" });
        if (request.Model?.Length > 200 || request.SerialNumber?.Length > 200)
            return BadRequest(new { message = "Model ve seri no en fazla 200 karakter olabilir" });
        var asset = await _db.Assets.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (asset is null) return NotFound();
        if (tag != asset.AssetTag && await _db.Assets.AnyAsync(a => a.AssetTag == tag && a.Id != id, ct))
            return Conflict(new { message = "Bu zimmet etiketi zaten kayıtlı" });

        var before = new { asset.AssetTag, Type = asset.Type.ToString(), asset.Model, asset.SerialNumber };
        asset.AssetTag = tag;
        asset.Type = request.Type;
        asset.Model = string.IsNullOrWhiteSpace(request.Model) ? null : request.Model.Trim();
        asset.SerialNumber = string.IsNullOrWhiteSpace(request.SerialNumber) ? null : request.SerialNumber.Trim();
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Eşzamanlı aynı etiket: tekil dizin (IX_onboarding_assets_TenantSlug_AssetTag).
            return Conflict(new { message = "Bu zimmet etiketi zaten kayıtlı" });
        }
        await Audit.WriteAsync(_sql, HttpContext, Tenant, "Asset", id.ToString(), "Updated",
            new { before, after = new { asset.AssetTag, Type = asset.Type.ToString(), asset.Model, asset.SerialNumber } });
        return Ok(new { asset.Id, asset.AssetTag, asset.Type, asset.Model, asset.SerialNumber, asset.Status, asset.CreatedAt, asset.QrCode });
    }

    /// <summary>
    /// Elle durum değişikliği: Boşta ↔ Bakımda / Hurda / Kayıp. Zimmetli demirbaşın durumu buradan
    /// değişmez (409): önce iade alınır ya da işten ayrılmada kayıp/kayıttan düşme (write-off) kullanılır.
    /// "Zimmetli" durumuna yalnızca zimmet atama ucuyla geçilir.
    /// </summary>
    [HttpPut("{id:guid}/status")]
    [Authorize(Policy = "RequireAssetManage")]
    public async Task<IActionResult> SetStatus(Guid id, [FromBody] SetAssetStatusRequest request, CancellationToken ct)
    {
        if (request.Status == AssetStatus.Assigned)
            return BadRequest(new { message = "Zimmetli durumuna yalnızca zimmet atayarak geçilir" });
        if (request.Note?.Length > 500) return BadRequest(new { message = "Açıklama en fazla 500 karakter olabilir" });
        var asset = await _db.Assets.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (asset is null) return NotFound();
        if (asset.Status == AssetStatus.Assigned
            || await _db.AssetAssignments.AnyAsync(a => a.AssetId == id && a.ReturnedOn == null, ct))
            return Conflict(new { message = "Zimmetli demirbaşın durumu değiştirilemez; önce iade alın" });
        if (asset.Status == request.Status) return Ok(new { asset.Id, asset.Status });

        var from = asset.Status;
        asset.Status = request.Status;
        await _db.SaveChangesAsync(ct);
        await Audit.WriteAsync(_sql, HttpContext, Tenant, "Asset", id.ToString(), "StatusChanged",
            new { asset.AssetTag, from = from.ToString(), to = asset.Status.ToString(), note = request.Note?.Trim() });
        return Ok(new { asset.Id, asset.Status });
    }

    /// <summary>
    /// Yanlış açılmış demirbaşı siler. Zimmet ya da bakım geçmişi olan kayıt silinmez (409): geçmiş
    /// denetim için korunur; kullanılmayacaksa hurdaya ayrılır.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "RequireAssetManage")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var asset = await _db.Assets.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (asset is null) return NotFound();
        if (await _db.AssetAssignments.AnyAsync(a => a.AssetId == id, ct)
            || await _db.AssetMaintenance.AnyAsync(m => m.AssetId == id, ct))
            return Conflict(new { message = "Bu demirbaşın zimmet ya da bakım geçmişi var; silinemez. Kullanılmayacaksa hurdaya ayırın" });

        _db.Assets.Remove(asset);
        await _db.SaveChangesAsync(ct);
        await Audit.WriteAsync(_sql, HttpContext, Tenant, "Asset", id.ToString(), "Deleted",
            new { asset.AssetTag, Type = asset.Type.ToString(), asset.Model, asset.SerialNumber });
        return NoContent();
    }

    [HttpGet("by-employee/{employeeId}")]
    public async Task<IActionResult> GetByEmployee(Guid employeeId)
    {
        var list = await _db.AssetAssignments
            .Include(a => a.Asset)
            .Where(a => a.EmployeeId == employeeId)
            .OrderByDescending(a => a.AssignedOn)
            .ToListAsync();
        return Ok(list);
    }
}

public record UpdateAssetRequest(string AssetTag, AssetType Type, string? Model, string? SerialNumber);
public record SetAssetStatusRequest(AssetStatus Status, string? Note);
public record CreateAssetRequest(string AssetTag, AssetType Type, string? Model, string? SerialNumber);
public record AssignAssetRequest(Guid EmployeeId, DateOnly AssignedOn, string? Notes, DateOnly? ExpectedReturnOn = null);
public record ReturnAssetRequest(DateOnly ReturnedOn, string? Condition, bool MarkAsRetired = false);

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using EmployeeService.Data;

namespace EmployeeService.Controllers;

/// <summary>
/// Çalışan fotoğrafı. Okuma: aynı şirketteki her oturum açmış kullanıcı (ad gibi şirket içi paylaşılan
/// bilgi; avatarlar ve "Ayın çalışanı" kartı). Yükleme/kaldırma: kişinin kendisi ya da İK.
/// Fotoğraf herkese açık bir adresten verilmez; istemci yetkili istekle alıp gösterir.
/// </summary>
[ApiController]
[Route("api/employees")]
[Authorize]
public class EmployeePhotosController : ControllerBase
{
    /// <summary>İstemci 512 px'e küçültüp yükler (~50-150 KB); sınır kötüye kullanıma karşı.</summary>
    public const int MaxBytes = 512 * 1024;

    private readonly EmployeeDbContext _db;
    public EmployeePhotosController(EmployeeDbContext db) => _db = db;

    private bool IsHr => User.IsInRole("hr-admin") || User.IsInRole("tenant-admin")
        || User.IsInRole("platform-admin") || User.IsInRole("ext-employee-manage");

    private string? CallerSub => User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
        ?? User.FindFirst("sub")?.Value;

    [HttpGet("{id:guid}/photo")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var photo = await _db.Photos.AsNoTracking().FirstOrDefaultAsync(p => p.EmployeeId == id, ct);
        if (photo is null) return NotFound();
        // Adres sürümlü (?v=N) olduğundan tarayıcı önbelleği güvenle uzun tutulur; "private": paylaşılan
        // önbelleklere (vekil sunucu) girmez.
        Response.Headers.CacheControl = "private, max-age=604800";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(photo.Data, photo.ContentType);
    }

    [HttpPut("{id:guid}/photo")]
    [RequestSizeLimit(MaxBytes + 16 * 1024)]
    public async Task<IActionResult> Upload(Guid id, IFormFile? file, CancellationToken ct)
    {
        var employee = await _db.Employees.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (employee is null) return NotFound();
        if (!IsHr && (CallerSub is null || employee.KeycloakUserId != CallerSub))
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Fotoğrafı yalnızca kişinin kendisi ya da İK değiştirebilir" });
        if (file is null || file.Length == 0) return BadRequest(new { message = "Dosya bulunamadı" });
        if (file.Length > MaxBytes) return BadRequest(new { message = "Fotoğraf en fazla 512 KB olabilir" });

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var data = ms.ToArray();
        // Tür, istemcinin bildirdiğine değil ilk baytlara göre belirlenir; SVG/HTML kabul edilmez.
        var type = DetectImage(data);
        if (type is null) return BadRequest(new { message = "Yalnızca PNG, JPEG ya da WebP fotoğraf yüklenebilir" });

        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO employee_photos ("EmployeeId", "TenantSlug", "ContentType", "Data", "UpdatedAt")
            VALUES ({employee.Id}, {employee.TenantSlug}, {type}, {data}, now())
            ON CONFLICT ("EmployeeId") DO UPDATE SET "ContentType" = EXCLUDED."ContentType", "Data" = EXCLUDED."Data", "UpdatedAt" = now()
            """, ct);
        employee.PhotoVersion = (employee.PhotoVersion ?? 0) + 1;
        await _db.SaveChangesAsync(ct);
        return Ok(new { photoVersion = employee.PhotoVersion });
    }

    [HttpDelete("{id:guid}/photo")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var employee = await _db.Employees.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (employee is null) return NotFound();
        if (!IsHr && (CallerSub is null || employee.KeycloakUserId != CallerSub))
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Fotoğrafı yalnızca kişinin kendisi ya da İK kaldırabilir" });
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""DELETE FROM employee_photos WHERE "EmployeeId" = {employee.Id} AND "TenantSlug" = {employee.TenantSlug}""", ct);
        employee.PhotoVersion = null;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    internal static string? DetectImage(ReadOnlySpan<byte> d)
    {
        if (d.Length >= 8 && d[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return "image/png";
        if (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return "image/jpeg";
        if (d.Length >= 12 && d[..4].SequenceEqual("RIFF"u8) && d.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        return null;
    }
}

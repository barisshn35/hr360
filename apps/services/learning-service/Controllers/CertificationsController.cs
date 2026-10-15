using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LearningService.Data;
using LearningService.Models;
using LearningService.Services;

namespace LearningService.Controllers;

[ApiController]
[Route("api/certifications")]
[Authorize]
public class CertificationsController : ControllerBase
{
    private readonly LearningDbContext _db;
    private readonly EmployeeDirectoryClient _employees;
    private readonly LearningDirectory _dir;
    public CertificationsController(LearningDbContext db, EmployeeDirectoryClient employees, LearningDirectory dir)
    {
        _db = db;
        _employees = employees;
        _dir = dir;
    }

    /// <summary>İK herkesi; İK olmayan yönetici kendisini ve başı olduğu departmanı görür.</summary>
    private async Task<HashSet<Guid>?> VisibleEmployeesAsync(CancellationToken ct)
    {
        if (User.IsHr()) return null;
        var me = await _dir.MeAsync(ct);
        var people = await _dir.ActiveAsync(ct);
        return people.Where(p => LearningDirectory.CanSee(p, me?.Id, false)).Select(p => p.Id).ToHashSet();
    }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] Guid? employeeId, CancellationToken ct)
    {
        // GUVENLIK: Onceden herkes tum sertifikalari (CredentialId dahil) listeliyordu.
        var isManager = User.IsInRole("manager") || User.IsInRole("hr-admin")
            || User.IsInRole("tenant-admin") || User.IsInRole("platform-admin")
            || User.IsInRole("ext-learning-manage");
        if (!isManager)
        {
            var me = await _employees.FindMyEmployeeIdAsync(ct);
            if (me is null) return Forbid();
            if (employeeId.HasValue && employeeId.Value != me.Value) return Forbid();
            employeeId = me;
        }
        var q = _db.Certifications.AsQueryable();
        if (employeeId.HasValue) q = q.Where(c => c.EmployeeId == employeeId.Value);
        if (isManager && await VisibleEmployeesAsync(ct) is { } visible)
        {
            var ids = visible.ToList();
            q = q.Where(c => ids.Contains(c.EmployeeId));
        }
        return Ok(await q.OrderByDescending(c => c.IssuedOn).ToListAsync());
    }

    /// <summary>GUVENLIK: Sertifika kaydi IK'ya ozel - onceden herkes kendine (ya da
    /// baskasina) sertifika uydurabiliyordu.</summary>
    [HttpPost]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Create([FromBody] CreateCertificationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 200)
            return BadRequest("Sertifika adı zorunlu ve en fazla 200 karakter olabilir");
        if (request.ExpiresOn.HasValue && request.ExpiresOn < request.IssuedOn)
            return BadRequest("Geçerlilik bitiş tarihi, veriliş tarihinden önce olamaz");

        var cert = new Certification
        {
            EmployeeId = request.EmployeeId,
            Name = request.Name,
            Issuer = request.Issuer,
            CredentialId = request.CredentialId,
            IssuedOn = request.IssuedOn,
            ExpiresOn = request.ExpiresOn,
            IsMandatory = request.IsMandatory ?? false,
        };
        _db.Certifications.Add(cert);
        await _db.SaveChangesAsync();
        return Created($"/api/certifications/{cert.Id}", cert);
    }

    /// <summary>Belirtilen gun icinde suresi dolacak sertifikalar.</summary>
    [HttpGet("expiring")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> GetExpiring([FromQuery] int withinDays = 90, CancellationToken ct = default)
    {
        var limit = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(withinDays));
        var today = TrTime.Today;

        var list = await _db.Certifications
            .Where(c => c.ExpiresOn != null && c.ExpiresOn <= limit)
            .OrderBy(c => c.ExpiresOn)
            .ToListAsync(ct);
        if (await VisibleEmployeesAsync(ct) is { } visible)
            list = list.Where(c => visible.Contains(c.EmployeeId)).ToList();

        return Ok(list.Select(c => new
        {
            c.Id,
            c.EmployeeId,
            c.Name,
            c.Issuer,
            c.ExpiresOn,
            c.IsMandatory,
            expired = c.ExpiresOn < today,
            daysRemaining = c.ExpiresOn!.Value.DayNumber - today.DayNumber
        }));
    }

    /// <summary>
    /// Yazdırılabilir sertifika verisi (Y20). Çalışanın kendisi, departman başkanı ve İK görür.
    /// </summary>
    [HttpGet("{id:guid}/certificate")]
    public async Task<IActionResult> Certificate(Guid id, CancellationToken ct)
    {
        var c = await _db.Certifications.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();
        var person = await _dir.FindAsync(c.EmployeeId, ct);
        var me = await _dir.MeAsync(ct);
        if (person is null || !LearningDirectory.CanSee(person, me?.Id, User.IsHr())) return NotFound();
        var course = c.CourseId is null ? null : await _db.Courses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == c.CourseId, ct);
        var enrollment = c.EnrollmentId is null ? null : await _db.Enrollments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == c.EnrollmentId, ct);
        return Ok(new
        {
            c.Id, c.Name, c.Issuer, c.IssuedOn, c.ExpiresOn, c.VerificationCode, c.CredentialId,
            employeeName = person.FullName,
            courseTitle = course?.Title,
            durationHours = course?.DurationHours,
            score = enrollment?.Score,
            company = await _dir.TenantNameAsync(ct),
        });
    }

    /// <summary>
    /// Doğrulama kodu sorgusu (oturum açmış kullanıcı, kendi şirketi). KVKK: tam ad yerine
    /// baş harfler döner.
    /// </summary>
    [HttpGet("verify/{code}")]
    public async Task<IActionResult> Verify(string code, CancellationToken ct)
    {
        code = (code ?? "").Trim().ToUpperInvariant();
        if (code.Length is < 6 or > 32) return NotFound();
        var c = await _db.Certifications.AsNoTracking().FirstOrDefaultAsync(x => x.VerificationCode == code, ct);
        if (c is null) return NotFound(new { message = "Bu kodla bir sertifika bulunamadı" });
        var person = await _dir.FindAsync(c.EmployeeId, ct);
        var initials = person is null ? "—" : $"{person.FirstName.FirstOrDefault()}. {person.LastName.FirstOrDefault()}.";
        var today = TrTime.Today;
        return Ok(new { valid = c.ExpiresOn is null || c.ExpiresOn >= today, c.Name, c.IssuedOn, c.ExpiresOn, holder = initials });
    }
}

public record CreateCertificationRequest(
    Guid EmployeeId, string Name, string? Issuer, string? CredentialId,
    DateOnly IssuedOn, DateOnly? ExpiresOn, bool? IsMandatory = null);

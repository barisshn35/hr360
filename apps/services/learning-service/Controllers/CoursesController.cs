using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LearningService.Data;
using LearningService.Models;
using LearningService.Services;

namespace LearningService.Controllers;

[ApiController]
[Route("api/courses")]
[Authorize]
public class CoursesController : ControllerBase
{
    private readonly LearningDbContext _db;
    private readonly EmployeeDirectoryClient _employees;
    private readonly LearningDirectory _dir;
    private readonly LearningService.Tenancy.ITenantContext _tenant;
    public CoursesController(LearningDbContext db, EmployeeDirectoryClient employees, LearningDirectory dir,
        LearningService.Tenancy.ITenantContext tenant)
    {
        _db = db;
        _employees = employees;
        _dir = dir;
        _tenant = tenant;
    }

    private bool IsManagerOrAbove => User.IsInRole("manager") || User.IsInRole("hr-admin")
        || User.IsInRole("tenant-admin") || User.IsInRole("platform-admin")
        || User.IsInRole("ext-learning-manage");

    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] CourseCategory? category, [FromQuery] bool? mandatoryOnly)
    {
        var q = _db.Courses.Where(c => c.IsActive);
        if (category.HasValue) q = q.Where(c => c.Category == category.Value);
        if (mandatoryOnly == true) q = q.Where(c => c.IsMandatory);
        return Ok(await q.OrderBy(c => c.Title).ToListAsync());
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var c = await _db.Courses.Include(x => x.Enrollments).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();
        // GUVENLIK: Onceden tum katilimcilarin kayitlari (kimin gectigi/kaldigi, puanlar
        // ve kayit kimlikleri - "tamamla" ucunun girdisi) herkese donuyordu. Yonetici+
        // disindakiler yalnizca kendi kaydini gorur.
        if (!IsManagerOrAbove)
        {
            var me = await _employees.FindMyEmployeeIdAsync(ct);
            c.Enrollments = c.Enrollments.Where(e => me is not null && e.EmployeeId == me.Value).ToList();
        }
        else if (!User.IsHr())
        {
            // Y20: sonuçları çalışan, departman başkanı ve İK görür — İK olmayan yönetici yalnızca
            // kendi ve başı olduğu departmandaki kayıtları görür.
            var me = await _dir.MeAsync(ct);
            var people = (await _dir.ActiveAsync(ct)).ToDictionary(p => p.Id);
            c.Enrollments = c.Enrollments.Where(e => people.TryGetValue(e.EmployeeId, out var p)
                && LearningDirectory.CanSee(p, me?.Id, false)).ToList();
        }
        return Ok(c);
    }

    [HttpPost]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Create([FromBody] CreateCourseRequest request)
    {
        if (request.DurationHours is <= 0 or > 1000)
            return BadRequest(new { message = "Eğitim süresi 0'dan büyük ve en fazla 1000 saat olmalı" });
        var course = new Course
        {
            Title = request.Title,
            Description = request.Description,
            Provider = request.Provider,
            DurationHours = request.DurationHours,
            Category = request.Category,
            IsMandatory = request.IsMandatory,
            CertificateValidityMonths = request.CertificateValidityMonths is > 0 and <= 120 ? request.CertificateValidityMonths : null,
        };
        _db.Courses.Add(course);
        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id = course.Id }, course);
    }

    /// <summary>Eğitimi düzenler; doğrulamalar oluşturmayla aynıdır. Kayıtlar korunur.</summary>
    [HttpPut("{id}")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Update(Guid id, [FromBody] CreateCourseRequest request, CancellationToken ct)
    {
        var course = await _db.Courses.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (course is null) return NotFound();
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Trim().Length > 200)
            return BadRequest(new { message = "Eğitim adı zorunlu ve en fazla 200 karakter olabilir" });
        if (request.DurationHours is <= 0 or > 1000)
            return BadRequest(new { message = "Eğitim süresi 0'dan büyük ve en fazla 1000 saat olmalı" });
        course.Title = request.Title.Trim();
        course.Description = request.Description;
        course.Provider = request.Provider;
        course.DurationHours = request.DurationHours;
        course.Category = request.Category;
        course.IsMandatory = request.IsMandatory;
        course.CertificateValidityMonths = request.CertificateValidityMonths is > 0 and <= 120 ? request.CertificateValidityMonths : null;
        await _db.SaveChangesAsync(ct);
        return Ok(course);
    }

    /// <summary>
    /// Eğitimi siler. Kaydı ya da bu eğitimden üretilmiş sertifikası olan eğitim silinmez
    /// (eğitim geçmişi ve uyum kanıtı kaybolur) — 409 döner; bunun yerine arşivlenir.
    /// Modüller ve yetkinlik eşleşmeleri veritabanında eğitimle birlikte silinir (ON DELETE CASCADE).
    /// </summary>
    [HttpDelete("{id}")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var course = await _db.Courses.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (course is null) return NotFound();
        if (await _db.Enrollments.AnyAsync(e => e.CourseId == id, ct)
            || await _db.Certifications.AnyAsync(c => c.CourseId == id, ct))
            return Conflict(new { message = "Kursa kayıtlı çalışan var; önce arşivleyin", code = "has_enrollments" });
        _db.Courses.Remove(course);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// Eğitimi arşivler (pasife alır): katalogdan ve uyum raporundan çıkar, yeni kayıt
    /// alınmaz; mevcut kayıtlar ve sertifikalar korunur.
    /// </summary>
    [HttpPost("{id}/archive")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Archive(Guid id, CancellationToken ct)
    {
        var course = await _db.Courses.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (course is null) return NotFound();
        course.IsActive = false;
        await _db.SaveChangesAsync(ct);
        return Ok(course);
    }

    [HttpPost("{id}/enroll")]
    public async Task<IActionResult> Enroll(Guid id, [FromBody] EnrollRequest request, CancellationToken ct)
    {
        // Baskasini kayit etmek yonetici+'ya ozel; calisan yalnizca kendini kaydeder.
        if (!IsManagerOrAbove)
        {
            var me = await _employees.FindMyEmployeeIdAsync(ct);
            if (me is null) return Forbid();
            if (request.EmployeeId != me.Value)
                return StatusCode(StatusCodes.Status403Forbidden,
                    new { message = "Yalnızca kendinizi eğitime kaydedebilirsiniz" });
        }
        var course = await _db.Courses.FirstOrDefaultAsync(c => c.Id == id);
        if (course is null) return NotFound("Egitim bulunamadi");
        if (!course.IsActive) return BadRequest("Pasif eğitime kayıt yapılamaz");

        if (await _db.Enrollments.AnyAsync(e => e.CourseId == id && e.EmployeeId == request.EmployeeId))
            return Conflict("Bu çalışan bu eğitime zaten kayıtlı");

        // Dalga 11 (madde 84): son tarihi yalnızca yönetici+ verir (çalışanın kendi kaydında yok sayılır).
        var enrollment = new Enrollment { CourseId = id, EmployeeId = request.EmployeeId, DueOn = IsManagerOrAbove ? request.DueOn : null };
        _db.Enrollments.Add(enrollment);
        await _db.SaveChangesAsync();
        return Created($"/api/courses/{id}", enrollment);
    }

    /// <summary>
    /// GUVENLIK: Onceden yalnizca [Authorize] - her calisan herhangi bir kaydi (kendi
    /// zorunlu uyum egitimi dahil) "gecti" isaretleyebiliyor, uyum raporunu
    /// sahteleyebiliyordu. Sonuc girisi yonetici/IK'ya ozel; kimse kendi kaydini
    /// sonuclandiramaz. Sonuclanmis/birakilmis kayit tekrar sonuclandirilamaz.
    /// </summary>
    [HttpPost("{id}/enrollments/{enrollmentId}/complete")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> Complete(
        Guid id, Guid enrollmentId, [FromBody] CompleteEnrollmentRequest request, CancellationToken ct)
    {
        var e = await _db.Enrollments
            .FirstOrDefaultAsync(x => x.Id == enrollmentId && x.CourseId == id, ct);
        if (e is null) return NotFound();
        if (e.Status is EnrollmentStatus.Completed or EnrollmentStatus.Failed or EnrollmentStatus.Dropped)
            return BadRequest("Bu kayıt zaten sonuçlanmış");
        if (request.Score is < 0 or > 100)
            return BadRequest("Puan 0-100 arasında olmalı");
        var me = await _employees.FindMyEmployeeIdAsync(ct);
        if (me is not null && me.Value == e.EmployeeId)
            return StatusCode(StatusCodes.Status403Forbidden,
                new { message = "Kendi eğitim sonucunuzu giremezsiniz" });

        if (!User.IsHr())
        {
            // İK olmayan yönetici yalnızca başı olduğu departmandaki kaydı sonuçlandırır.
            var target = await _dir.FindAsync(e.EmployeeId, ct);
            if (target is null || target.HeadId is null || target.HeadId != (await _dir.MeAsync(ct))?.Id)
                return StatusCode(StatusCodes.Status403Forbidden,
                    new { message = "Yalnızca yönettiğiniz departmandaki kayıtları sonuçlandırabilirsiniz" });
        }

        e.Status = request.Passed ? EnrollmentStatus.Completed : EnrollmentStatus.Failed;
        e.Score = request.Score;
        e.CompletedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();
        // Y20: elle tamamlanan kayıt da doğrulama kodlu sertifika alır.
        if (e.Status == EnrollmentStatus.Completed)
            await CourseCompletion.IssueCertificateAsync(_db, _tenant, e, _dir, ct);
        return Ok(e);
    }

    /// <summary>Zorunlu egitimlerde uyum durumu - kim hangi egitimi tamamlamamis.</summary>
    [HttpGet("compliance")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> GetCompliance()
    {
        var mandatory = await _db.Courses
            .Where(c => c.IsMandatory && c.IsActive)
            .Include(c => c.Enrollments)
            .ToListAsync();

        return Ok(mandatory.Select(c => new
        {
            courseId = c.Id,
            title = c.Title,
            enrolled = c.Enrollments.Count,
            completed = c.Enrollments.Count(e => e.Status == EnrollmentStatus.Completed),
            pending = c.Enrollments.Count(e => e.Status != EnrollmentStatus.Completed),
            // NOT: Arayuz (learning.ts ComplianceRow) bu alanlari bekliyordu; eskileri
            // gonderildigi icin Uyum sekmesi "—/— kisi, %NaN" ve bos cubuk gosteriyordu.
            courseTitle = c.Title,
            requiredCount = c.Enrollments.Count,
            completedCount = c.Enrollments.Count(e => e.Status == EnrollmentStatus.Completed),
            compliancePercent = c.Enrollments.Count == 0 ? 0
                : Math.Round(100.0 * c.Enrollments.Count(e => e.Status == EnrollmentStatus.Completed) / c.Enrollments.Count, 1),
        }));
    }
}

public record CreateCourseRequest(
    string Title, string? Description, string? Provider,
    decimal DurationHours, CourseCategory Category, bool IsMandatory, int? CertificateValidityMonths = null);
public record EnrollRequest(Guid EmployeeId, DateOnly? DueOn = null);
public record CompleteEnrollmentRequest(bool Passed, decimal? Score);

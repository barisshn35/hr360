using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LearningService.Data;
using LearningService.Models;
using LearningService.Services;
using LearningService.Tenancy;

namespace LearningService.Controllers;

/// <summary>
/// Y20 SCORM 1.2: paket yükleme (≤ 50 MB zip, imsmanifest.xml doğrulanır, dosyalar Postgres'e
/// açılır), aynı kökenden (iframe) dosya sunumu ve çalışma zamanı değerlerinin kalıcılığı.
/// Web tarafındaki window.API dolgusu LMSSetValue/LMSCommit/LMSFinish'i /runtime ucuna yazar.
/// </summary>
[ApiController]
[Route("api/scorm")]
[Authorize]
public class ScormController : ControllerBase
{
    private readonly LearningDbContext _db;
    private readonly LearningDirectory _dir;
    private readonly TenantContext _tenant;

    public ScormController(LearningDbContext db, LearningDirectory dir, TenantContext tenant)
    {
        _db = db; _dir = dir; _tenant = tenant;
    }

    private bool IsHr => User.IsHr();

    /// <summary>Gateway'in dışarıya açtığı önek (nginx /api/learning/X → servis /api/X).</summary>
    private static readonly string PublicPrefix =
        (Environment.GetEnvironmentVariable("LEARNING_PUBLIC_PREFIX") ?? "/api/learning").TrimEnd('/');

    /* ------------------------------------------------------------ paketler */

    [HttpGet("packages")]
    public async Task<IActionResult> Packages(CancellationToken ct)
    {
        if (!IsHr) return Forbid();
        return Ok(await _db.ScormPackages.AsNoTracking().OrderByDescending(p => p.CreatedAt).ToListAsync(ct));
    }

    /// <summary>Çok parçalı form: "file" (zip) ve "title". Swagger [FromForm] IFormFile'ı ancak bir DTO içinde belgeleyebilir.</summary>
    public sealed class ScormUploadForm
    {
        public IFormFile? File { get; set; }
        public string? Title { get; set; }
    }

    [HttpPost("packages")]
    [RequestSizeLimit(ScormPackageReader.MaxUploadBytes + 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = ScormPackageReader.MaxUploadBytes + 1024 * 1024)]
    public async Task<IActionResult> Upload([FromForm] ScormUploadForm form, CancellationToken ct)
    {
        var (file, title) = (form.File, form.Title);
        if (!IsHr) return Forbid();
        if (file is null || file.Length == 0) return BadRequest(new { message = "Zip dosyası seçin" });
        if (file.Length > ScormPackageReader.MaxUploadBytes) return BadRequest(new { message = "Paket en fazla 50 MB olabilir" });
        if (!file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return BadRequest(new { message = "Yalnızca .zip SCORM paketi yüklenebilir" });

        ScormParseResult parsed;
        await using (var s = file.OpenReadStream())
        {
            using var ms = new MemoryStream();
            await s.CopyToAsync(ms, ct);
            // Gerçek tür denetimi: içerik zip (PK imzası) olmalı; bildirilen tür ve uzantı da zip'i göstermeli.
            switch (FileSniffer.Check(ms.GetBuffer().AsSpan(0, (int)ms.Length), file.ContentType, file.FileName, [FileSniffer.Zip], out _))
            {
                case FileSniffer.Verdict.NotAllowed: return BadRequest(new { message = "Dosya geçerli bir zip arşivi değil" });
                case FileSniffer.Verdict.Mismatch: return BadRequest(new { message = FileSniffer.MismatchMessage });
            }
            ms.Position = 0;
            parsed = ScormPackageReader.Read(ms);
        }
        if (parsed.Error is not null) return BadRequest(new { message = parsed.Error });

        var pkgTitle = string.IsNullOrWhiteSpace(title) ? parsed.Title ?? Path.GetFileNameWithoutExtension(file.FileName) : title.Trim();
        if (pkgTitle.Length > 200) pkgTitle = pkgTitle[..200];
        var pkg = new ScormPackage
        {
            Title = pkgTitle,
            ManifestIdentifier = parsed.Identifier is { Length: > 200 } id ? id[..200] : parsed.Identifier,
            EntryPoint = parsed.EntryPoint!,
            FileCount = parsed.Files.Count,
            TotalBytes = parsed.Files.Sum(f => (long)f.Content.Length),
            UploadedBy = User.DisplayName(),
        };
        _db.ScormPackages.Add(pkg);
        foreach (var f in parsed.Files)
            _db.ScormFiles.Add(new ScormFile { PackageId = pkg.Id, Path = f.Path, ContentType = f.ContentType, Size = f.Content.Length, Content = f.Content });
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
        return Ok(pkg);
    }

    [HttpDelete("packages/{id:guid}")]
    public async Task<IActionResult> DeletePackage(Guid id, CancellationToken ct)
    {
        if (!IsHr) return Forbid();
        var p = await _db.ScormPackages.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return NotFound();
        if (await _db.Modules.AnyAsync(m => m.ScormPackageId == id, ct))
            return Conflict(new { message = "Paket bir eğitim modülünde kullanılıyor; önce modülü silin" });
        await _db.ScormFiles.Where(f => f.PackageId == id).ExecuteDeleteAsync(ct);
        _db.ScormPackages.Remove(p);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    /* ------------------------------------------------------------ başlatma ve dosya sunumu */

    private async Task<(PersonRow? me, Enrollment? enrollment, CourseModule? module, IActionResult? error)> LearnerContextAsync(
        Guid courseId, Guid moduleId, CancellationToken ct)
    {
        var module = await _db.Modules.FirstOrDefaultAsync(m => m.Id == moduleId && m.CourseId == courseId, ct);
        if (module is null || module.Kind != ModuleKind.Scorm || module.ScormPackageId is null) return (null, null, null, NotFound());
        var me = await _dir.MeAsync(ct);
        if (me is null) return (null, null, module, StatusCode(403, new { message = "Bu hesaba bağlı çalışan kaydı bulunamadı" }));
        var e = await _db.Enrollments.FirstOrDefaultAsync(x => x.CourseId == courseId && x.EmployeeId == me.Id, ct);
        if (e is null) return (me, null, module, StatusCode(403, new { message = "Önce eğitime kaydolun" }));
        return (me, e, module, null);
    }

    public record LaunchInput(Guid CourseId, Guid ModuleId);

    /// <summary>
    /// Öğreneni başlatır: paket dosya yoluna kapsamlı, kısa ömürlü HttpOnly çerez yazar (iframe
    /// istekleri Authorization başlığı taşıyamaz) ve kayıtlı çalışma zamanı değerlerini döner.
    /// </summary>
    [HttpPost("launch")]
    public async Task<IActionResult> Launch([FromBody] LaunchInput body, CancellationToken ct)
    {
        var (me, e, module, error) = await LearnerContextAsync(body.CourseId, body.ModuleId, ct);
        if (error is not null) return error;
        var pkg = await _db.ScormPackages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == module!.ScormPackageId, ct);
        if (pkg is null) return NotFound(new { message = "SCORM paketi bulunamadı" });
        var token = ScormLaunchTokens.Issue(_tenant.TenantSlug!, pkg.Id, me!.Id, DateTimeOffset.UtcNow);
        var basePath = $"{PublicPrefix}/scorm/{pkg.Id}/files/";
        Response.Cookies.Append(ScormLaunchTokens.CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = string.Equals(Request.Headers["X-Forwarded-Proto"].FirstOrDefault(), "https", StringComparison.OrdinalIgnoreCase) || Request.IsHttps,
            Path = basePath,
            MaxAge = ScormLaunchTokens.Lifetime,
        });
        var rt = await _db.ScormRuntime.AsNoTracking().FirstOrDefaultAsync(r => r.EnrollmentId == e!.Id && r.ModuleId == module!.Id, ct);
        return Ok(new
        {
            packageId = pkg.Id,
            launchUrl = basePath + pkg.EntryPoint,
            enrollmentStatus = e!.Status.ToString(),
            runtime = RuntimeView(rt),
            studentId = me.Id,
            studentName = $"{me.LastName}, {me.FirstName}",
        });
    }

    /// <summary>
    /// Paket dosyası. İki yoldan yetki: (1) Bearer jeton (İK önizlemesi ya da kayıtlı öğrenen),
    /// (2) /launch'ın yazdığı pakete bağlı imzalı çerez (iframe). Aynı kökenden sunulur; gateway
    /// X-Frame-Options SAMEORIGIN ekler, burada ayrıca frame-ancestors 'self'.
    /// </summary>
    [HttpGet("{packageId:guid}/files/{**path}")]
    [AllowAnonymous]
    public async Task<IActionResult> File(Guid packageId, string? path, CancellationToken ct)
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            if (!_tenant.IsResolved) return NotFound();
            if (!IsHr)
            {
                var me = await _dir.MeAsync(ct);
                var courseIds = await _db.Modules.Where(m => m.ScormPackageId == packageId).Select(m => m.CourseId).ToListAsync(ct);
                if (me is null || !await _db.Enrollments.AnyAsync(e => e.EmployeeId == me.Id && courseIds.Contains(e.CourseId), ct))
                    return NotFound();
            }
        }
        else
        {
            var claim = ScormLaunchTokens.Validate(Request.Cookies[ScormLaunchTokens.CookieName], packageId, DateTimeOffset.UtcNow);
            if (claim is null) return Unauthorized();
            // Jeton yalnızca bu pakete ve kiracıya bağlı; sorgular bu kiracıyla filtrelenir.
            _tenant.TenantSlug = claim.Value.Tenant;
        }

        var safe = ScormPackageReader.SafePath(path ?? "");
        if (safe is null) return NotFound();
        var f = await _db.ScormFiles.AsNoTracking().Where(x => x.PackageId == packageId && x.Path == safe)
            .Select(x => new { x.ContentType, x.Content }).FirstOrDefaultAsync(ct);
        if (f is null) return NotFound();
        Response.Headers["Cache-Control"] = "private, max-age=300";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Security-Policy"] = "frame-ancestors 'self'";
        return File(f.Content, f.ContentType);
    }

    /* ------------------------------------------------------------ çalışma zamanı */

    private static object RuntimeView(ScormRuntime? rt) => new
    {
        lessonStatus = rt?.LessonStatus ?? "not attempted",
        scoreRaw = rt?.ScoreRaw,
        suspendData = rt?.SuspendData ?? "",
        lessonLocation = rt?.LessonLocation ?? "",
        entry = rt is null || rt.SessionCount == 0 ? "ab-initio" : "resume",
        updatedAt = rt?.UpdatedAt,
    };

    [HttpGet("runtime")]
    public async Task<IActionResult> Runtime([FromQuery] Guid courseId, [FromQuery] Guid moduleId, CancellationToken ct)
    {
        var (_, e, module, error) = await LearnerContextAsync(courseId, moduleId, ct);
        if (error is not null) return error;
        var rt = await _db.ScormRuntime.AsNoTracking().FirstOrDefaultAsync(r => r.EnrollmentId == e!.Id && r.ModuleId == module!.Id, ct);
        return Ok(RuntimeView(rt));
    }

    public record RuntimeInput(Guid CourseId, Guid ModuleId, Dictionary<string, string?> Values, bool Finish);

    private static readonly HashSet<string> LessonStatuses = new() { "passed", "completed", "failed", "incomplete", "browsed", "not attempted" };

    /// <summary>
    /// LMSCommit/LMSFinish: cmi.core.lesson_status, cmi.core.score.raw, cmi.suspend_data,
    /// cmi.core.lesson_location kalıcı yazılır. passed/completed modülü tamamlar.
    /// </summary>
    [HttpPut("runtime")]
    public async Task<IActionResult> SaveRuntime([FromBody] RuntimeInput body, CancellationToken ct)
    {
        var (me, e, module, error) = await LearnerContextAsync(body.CourseId, body.ModuleId, ct);
        if (error is not null) return error;
        var values = body.Values ?? new();
        var rt = await _db.ScormRuntime.FirstOrDefaultAsync(r => r.EnrollmentId == e!.Id && r.ModuleId == module!.Id, ct);
        if (rt is null)
        {
            rt = new ScormRuntime { EnrollmentId = e!.Id, ModuleId = module!.Id, PackageId = module.ScormPackageId!.Value, EmployeeId = me!.Id };
            _db.ScormRuntime.Add(rt);
        }
        if (values.TryGetValue("cmi.core.lesson_status", out var status) && status is not null)
        {
            status = status.Trim().ToLowerInvariant();
            if (!LessonStatuses.Contains(status)) return BadRequest(new { message = "Geçersiz cmi.core.lesson_status" });
            // Tamamlanmış bir dersin durumu geri alınmaz (yeniden açıp gezinmek sonucu bozmasın).
            if (!(rt.LessonStatus is "passed" or "completed" && status is "incomplete" or "browsed" or "not attempted"))
                rt.LessonStatus = status;
        }
        if (values.TryGetValue("cmi.core.score.raw", out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            if (!decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var score) || score < 0 || score > 100)
                return BadRequest(new { message = "cmi.core.score.raw 0-100 arasında sayı olmalı" });
            rt.ScoreRaw = Math.Round(score, 2);
        }
        if (values.TryGetValue("cmi.suspend_data", out var sd))
        {
            if (sd is { Length: > 4096 }) return BadRequest(new { message = "cmi.suspend_data en fazla 4096 karakter olabilir (SCORM 1.2)" });
            rt.SuspendData = sd;
        }
        if (values.TryGetValue("cmi.core.lesson_location", out var loc))
        {
            if (loc is { Length: > 255 }) return BadRequest(new { message = "cmi.core.lesson_location en fazla 255 karakter olabilir" });
            rt.LessonLocation = loc;
        }
        if (body.Finish) rt.SessionCount++;
        rt.UpdatedAt = DateTimeOffset.UtcNow;

        Certification? cert = null;
        if (e!.Status is not (EnrollmentStatus.Completed or EnrollmentStatus.Failed or EnrollmentStatus.Dropped))
        {
            var p = await _db.ModuleProgress.FirstOrDefaultAsync(x => x.EnrollmentId == e.Id && x.ModuleId == module!.Id, ct);
            if (p is null)
            {
                p = new ModuleProgress { EnrollmentId = e.Id, ModuleId = module!.Id, EmployeeId = me!.Id };
                _db.ModuleProgress.Add(p);
            }
            if (p.Status != ProgressStatus.Completed)
            {
                p.Status = rt.LessonStatus switch
                {
                    "passed" or "completed" => ProgressStatus.Completed,
                    "failed" => ProgressStatus.Failed,
                    _ => ProgressStatus.InProgress,
                };
                if (p.Status == ProgressStatus.Completed) p.CompletedAt = DateTimeOffset.UtcNow;
            }
            p.Score = rt.ScoreRaw ?? p.Score;
            p.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            cert = await CourseCompletion.EvaluateAsync(_db, _tenant, e, _dir, ct);
        }
        else await _db.SaveChangesAsync(ct);

        return Ok(new { runtime = RuntimeView(rt), enrollmentStatus = e.Status.ToString(), certificateId = cert?.Id });
    }
}

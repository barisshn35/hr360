using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LearningService.Data;
using LearningService.Models;
using LearningService.Services;
using LearningService.Tenancy;

namespace LearningService.Controllers;

/// <summary>
/// Y20 eğitim içeriği: modüller (video bağlantısı, metin, sınav, SCORM), sınav puanlama (sunucuda;
/// doğru cevaplar gönderimden önce ya da sonra istemciye gönderilmez), ilerleme ve sonuçlar.
/// Sonuçları çalışanın kendisi, departman başkanı ve İK görür.
/// </summary>
[ApiController]
[Route("api/courses/{courseId:guid}")]
[Authorize]
public class CourseContentController : ControllerBase
{
    private readonly LearningDbContext _db;
    private readonly LearningDirectory _dir;
    private readonly ITenantContext _tenant;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public CourseContentController(LearningDbContext db, LearningDirectory dir, ITenantContext tenant)
    {
        _db = db; _dir = dir; _tenant = tenant;
    }

    private bool IsHr => User.IsHr();

    /* ------------------------------------------------------------ modüller */

    private static object ModuleView(CourseModule m, int questionCount) => new
    {
        m.Id, m.CourseId, m.Position, m.Title, m.Kind, m.VideoUrl, m.TextBody, m.PassMarkPercent, m.MaxAttempts, m.ScormPackageId,
        questionCount,
    };

    [HttpGet("modules")]
    public async Task<IActionResult> Modules(Guid courseId, CancellationToken ct)
    {
        if (!await _db.Courses.AnyAsync(c => c.Id == courseId, ct)) return NotFound();
        var modules = await _db.Modules.AsNoTracking().Where(m => m.CourseId == courseId)
            .OrderBy(m => m.Position).ThenBy(m => m.CreatedAt).ToListAsync(ct);
        var ids = modules.Select(m => m.Id).ToList();
        var counts = await _db.QuizQuestions.Where(q => ids.Contains(q.ModuleId)).GroupBy(q => q.ModuleId)
            .Select(g => new { g.Key, n = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.n, ct);
        return Ok(modules.Select(m => ModuleView(m, counts.GetValueOrDefault(m.Id))));
    }

    public record ModuleInput(string Title, string Kind, int? Position, string? VideoUrl, string? TextBody,
        int? PassMarkPercent, int? MaxAttempts, Guid? ScormPackageId);

    private async Task<string?> ValidateModule(ModuleInput b, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(b.Title) || b.Title.Length > 200) return "Modül başlığı zorunlu (en fazla 200 karakter)";
        if (!ModuleKind.All.Contains(b.Kind)) return "Geçersiz modül türü";
        switch (b.Kind)
        {
            case ModuleKind.Video:
                if (!Uri.TryCreate(b.VideoUrl?.Trim(), UriKind.Absolute, out var u) || u.Scheme is not ("https" or "http") || b.VideoUrl!.Length > 1000)
                    return "Video için geçerli bir http(s) bağlantısı girin";
                break;
            case ModuleKind.Text:
                if (string.IsNullOrWhiteSpace(b.TextBody)) return "Metin modülünün içeriği boş olamaz";
                if (b.TextBody.Length > 100_000) return "Metin en fazla 100.000 karakter olabilir";
                break;
            case ModuleKind.Quiz:
                if (b.PassMarkPercent is null or < 1 or > 100) return "Geçme notu %1-100 arasında olmalı";
                if (b.MaxAttempts is < 1 or > 20) return "Deneme hakkı 1-20 arasında olmalı (boş: sınırsız)";
                break;
            case ModuleKind.Scorm:
                if (b.ScormPackageId is null || !await _db.ScormPackages.AnyAsync(p => p.Id == b.ScormPackageId, ct))
                    return "SCORM paketi bulunamadı; önce paketi yükleyin";
                break;
        }
        return null;
    }

    private static void Apply(CourseModule m, ModuleInput b)
    {
        m.Title = b.Title.Trim();
        m.Kind = b.Kind;
        m.VideoUrl = b.Kind == ModuleKind.Video ? b.VideoUrl!.Trim() : null;
        m.TextBody = b.Kind == ModuleKind.Text ? b.TextBody : null;
        m.PassMarkPercent = b.Kind == ModuleKind.Quiz ? b.PassMarkPercent : null;
        m.MaxAttempts = b.Kind == ModuleKind.Quiz ? b.MaxAttempts : null;
        m.ScormPackageId = b.Kind == ModuleKind.Scorm ? b.ScormPackageId : null;
    }

    [HttpPost("modules")]
    public async Task<IActionResult> CreateModule(Guid courseId, [FromBody] ModuleInput body, CancellationToken ct)
    {
        if (!IsHr) return Forbid();
        if (!await _db.Courses.AnyAsync(c => c.Id == courseId, ct)) return NotFound();
        var err = await ValidateModule(body, ct);
        if (err is not null) return BadRequest(new { message = err });
        var pos = body.Position ?? ((await _db.Modules.Where(m => m.CourseId == courseId).MaxAsync(m => (int?)m.Position, ct) ?? 0) + 1);
        var m = new CourseModule { CourseId = courseId, Title = body.Title.Trim(), Position = pos };
        Apply(m, body);
        _db.Modules.Add(m);
        await _db.SaveChangesAsync(ct);
        return Ok(ModuleView(m, 0));
    }

    [HttpPut("modules/{moduleId:guid}")]
    public async Task<IActionResult> UpdateModule(Guid courseId, Guid moduleId, [FromBody] ModuleInput body, CancellationToken ct)
    {
        if (!IsHr) return Forbid();
        var m = await _db.Modules.FirstOrDefaultAsync(x => x.Id == moduleId && x.CourseId == courseId, ct);
        if (m is null) return NotFound();
        var err = await ValidateModule(body, ct);
        if (err is not null) return BadRequest(new { message = err });
        if (m.Kind != body.Kind && await _db.ModuleProgress.AnyAsync(p => p.ModuleId == moduleId, ct))
            return Conflict(new { message = "İlerleme kaydı olan modülün türü değiştirilemez; yeni modül ekleyin" });
        Apply(m, body);
        if (body.Position is not null) m.Position = body.Position.Value;
        await _db.SaveChangesAsync(ct);
        return Ok(ModuleView(m, await _db.QuizQuestions.CountAsync(q => q.ModuleId == moduleId, ct)));
    }

    [HttpDelete("modules/{moduleId:guid}")]
    public async Task<IActionResult> DeleteModule(Guid courseId, Guid moduleId, CancellationToken ct)
    {
        if (!IsHr) return Forbid();
        var m = await _db.Modules.FirstOrDefaultAsync(x => x.Id == moduleId && x.CourseId == courseId, ct);
        if (m is null) return NotFound();
        _db.Modules.Remove(m);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    /* ------------------------------------------------------------ sınav */

    public record QuestionInput(string Text, string Kind, List<QuizOption> Options, List<string> Correct);
    public record QuizInput(List<QuestionInput> Questions);

    /// <summary>Soruları (cevap anahtarıyla) değiştirir — İK. Doğru seçenekler ayrı sütunda saklanır.</summary>
    [HttpPut("modules/{moduleId:guid}/quiz")]
    public async Task<IActionResult> SaveQuiz(Guid courseId, Guid moduleId, [FromBody] QuizInput body, CancellationToken ct)
    {
        if (!IsHr) return Forbid();
        var m = await _db.Modules.FirstOrDefaultAsync(x => x.Id == moduleId && x.CourseId == courseId, ct);
        if (m is null) return NotFound();
        if (m.Kind != ModuleKind.Quiz) return BadRequest(new { message = "Bu modül sınav değil" });
        if (body.Questions is null || body.Questions.Count is 0 or > 200) return BadRequest(new { message = "Sınavda 1-200 soru olmalı" });
        var i = 0;
        foreach (var q in body.Questions)
        {
            i++;
            if (string.IsNullOrWhiteSpace(q.Text) || q.Text.Length > 1000) return BadRequest(new { message = $"{i}. soru metni zorunlu (en fazla 1000 karakter)" });
            if (q.Kind is not (QuestionKind.Single or QuestionKind.Multiple)) return BadRequest(new { message = $"{i}. soru türü geçersiz" });
            if (q.Options is null || q.Options.Count is < 2 or > 10) return BadRequest(new { message = $"{i}. soruda 2-10 seçenek olmalı" });
            var ids = q.Options.Select(o => o.Id?.Trim() ?? "").ToList();
            if (ids.Any(x => x.Length is 0 or > 20) || ids.Distinct().Count() != ids.Count || q.Options.Any(o => string.IsNullOrWhiteSpace(o.Text) || o.Text.Length > 500))
                return BadRequest(new { message = $"{i}. sorunun seçenekleri geçersiz (benzersiz kimlik ve metin gerekli)" });
            var correct = (q.Correct ?? new()).Select(x => x.Trim()).Distinct().ToList();
            if (correct.Count == 0 || correct.Any(c => !ids.Contains(c))) return BadRequest(new { message = $"{i}. sorunun doğru cevabı seçeneklerden olmalı" });
            if (q.Kind == QuestionKind.Single && correct.Count != 1) return BadRequest(new { message = $"{i}. soru tek seçimli: tam bir doğru cevap olmalı" });
        }
        _db.QuizQuestions.RemoveRange(_db.QuizQuestions.Where(q => q.ModuleId == moduleId));
        i = 0;
        foreach (var q in body.Questions)
            _db.QuizQuestions.Add(new QuizQuestion
            {
                ModuleId = moduleId, Position = ++i, Text = q.Text.Trim(), Kind = q.Kind,
                OptionsJson = JsonSerializer.Serialize(q.Options.Select(o => new QuizOption(o.Id.Trim(), o.Text.Trim())), Json),
                CorrectJson = JsonSerializer.Serialize(q.Correct.Select(x => x.Trim()).Distinct()),
            });
        await _db.SaveChangesAsync(ct);
        return Ok(new { moduleId, questionCount = body.Questions.Count });
    }

    /// <summary>İK'nın düzenleme ekranı için cevap anahtarı (öğrenene asla açık değil).</summary>
    [HttpGet("modules/{moduleId:guid}/quiz/answer-key")]
    public async Task<IActionResult> AnswerKey(Guid courseId, Guid moduleId, CancellationToken ct)
    {
        if (!IsHr) return Forbid();
        if (!await _db.Modules.AnyAsync(x => x.Id == moduleId && x.CourseId == courseId, ct)) return NotFound();
        var qs = await _db.QuizQuestions.AsNoTracking().Where(q => q.ModuleId == moduleId).OrderBy(q => q.Position).ToListAsync(ct);
        return Ok(qs.Select(q => new { q.Id, q.Text, q.Kind, options = QuizGrader.Options(q.OptionsJson), correct = QuizGrader.Ids(q.CorrectJson) }));
    }

    private async Task<(PersonRow? me, Enrollment? enrollment)> MyEnrollmentAsync(Guid courseId, CancellationToken ct)
    {
        var me = await _dir.MeAsync(ct);
        if (me is null) return (null, null);
        return (me, await _db.Enrollments.FirstOrDefaultAsync(e => e.CourseId == courseId && e.EmployeeId == me.Id, ct));
    }

    /// <summary>Öğrenenin sınavı: sorular ve seçenekler — DOĞRU CEVAP YOK.</summary>
    [HttpGet("modules/{moduleId:guid}/quiz")]
    public async Task<IActionResult> Quiz(Guid courseId, Guid moduleId, CancellationToken ct)
    {
        var m = await _db.Modules.AsNoTracking().FirstOrDefaultAsync(x => x.Id == moduleId && x.CourseId == courseId, ct);
        if (m is null || m.Kind != ModuleKind.Quiz) return NotFound();
        var (_, enrollment) = await MyEnrollmentAsync(courseId, ct);
        if (enrollment is null && !IsHr) return StatusCode(403, new { message = "Sınava girmek için önce eğitime kaydolun" });
        var qs = await _db.QuizQuestions.AsNoTracking().Where(q => q.ModuleId == moduleId).OrderBy(q => q.Position).ToListAsync(ct);
        var attempts = enrollment is null ? new List<QuizAttempt>()
            : await _db.QuizAttempts.AsNoTracking().Where(a => a.EnrollmentId == enrollment.Id && a.ModuleId == moduleId).OrderBy(a => a.AttemptNo).ToListAsync(ct);
        return Ok(new
        {
            moduleId, m.Title, m.PassMarkPercent, m.MaxAttempts,
            attemptsUsed = attempts.Count,
            attemptsLeft = m.MaxAttempts is null ? (int?)null : Math.Max(0, m.MaxAttempts.Value - attempts.Count),
            passed = attempts.Any(a => a.Passed),
            attempts = attempts.Select(a => new { a.AttemptNo, a.ScorePercent, a.Passed, a.SubmittedAt }),
            questions = qs.Select(q => new { q.Id, q.Text, q.Kind, options = QuizGrader.Options(q.OptionsJson) }),
        });
    }

    public record AttemptInput(Dictionary<Guid, List<string>> Answers);

    /// <summary>
    /// Sınav gönderimi: sunucuda puanlanır. Yanıtta yalnızca puan, geçti/kaldı ve soru başına
    /// doğru/yanlış döner — doğru seçenekler hiçbir zaman gönderilmez (tekrar denemede kopya olmasın).
    /// </summary>
    [HttpPost("modules/{moduleId:guid}/quiz/attempts")]
    public async Task<IActionResult> Attempt(Guid courseId, Guid moduleId, [FromBody] AttemptInput body, CancellationToken ct)
    {
        var m = await _db.Modules.FirstOrDefaultAsync(x => x.Id == moduleId && x.CourseId == courseId, ct);
        if (m is null || m.Kind != ModuleKind.Quiz) return NotFound();
        var (me, enrollment) = await MyEnrollmentAsync(courseId, ct);
        if (me is null || enrollment is null) return StatusCode(403, new { message = "Sınava girmek için önce eğitime kaydolun" });
        if (enrollment.Status is EnrollmentStatus.Completed or EnrollmentStatus.Failed or EnrollmentStatus.Dropped)
            return Conflict(new { message = "Bu eğitim kaydı sonuçlanmış; yeni deneme yapılamaz" });
        var used = await _db.QuizAttempts.CountAsync(a => a.EnrollmentId == enrollment.Id && a.ModuleId == moduleId, ct);
        if (await _db.QuizAttempts.AnyAsync(a => a.EnrollmentId == enrollment.Id && a.ModuleId == moduleId && a.Passed, ct))
            return Conflict(new { message = "Bu sınavı zaten geçtiniz" });
        if (m.MaxAttempts is not null && used >= m.MaxAttempts)
            return Conflict(new { message = "Deneme hakkınız bitti", code = "max_attempts" });
        var qs = await _db.QuizQuestions.AsNoTracking().Where(q => q.ModuleId == moduleId).OrderBy(q => q.Position).ToListAsync(ct);
        if (qs.Count == 0) return BadRequest(new { message = "Bu sınavda henüz soru yok" });

        var answers = (body.Answers ?? new()).Where(kv => qs.Any(q => q.Id == kv.Key))
            .ToDictionary(kv => kv.Key, kv => (IReadOnlyCollection<string>)(kv.Value ?? new()).Take(10).Select(x => x.Length > 20 ? x[..20] : x).ToList());
        var grade = QuizGrader.Grade(qs.Select(q => new GradableQuestion(q.Id, q.Kind, QuizGrader.Ids(q.CorrectJson))).ToList(),
            answers, m.PassMarkPercent ?? 100);

        var attempt = new QuizAttempt
        {
            ModuleId = moduleId, EnrollmentId = enrollment.Id, EmployeeId = me.Id, AttemptNo = used + 1,
            AnswersJson = JsonSerializer.Serialize(answers, Json), ScorePercent = grade.ScorePercent, Passed = grade.Passed,
        };
        _db.QuizAttempts.Add(attempt);
        var progress = await UpsertProgressAsync(enrollment, moduleId, me.Id, ct);
        var best = Math.Max(grade.ScorePercent, progress.Score ?? 0);
        progress.Score = best;
        progress.Status = grade.Passed ? ProgressStatus.Completed : ProgressStatus.Failed;
        progress.CompletedAt = grade.Passed ? DateTimeOffset.UtcNow : null;
        progress.UpdatedAt = DateTimeOffset.UtcNow;
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { return Conflict(new { message = "Bu deneme zaten kaydedildi; sayfayı yenileyin" }); }

        var cert = await CourseCompletion.EvaluateAsync(_db, _tenant, enrollment, _dir, ct);
        var attemptsLeft = m.MaxAttempts is null ? (int?)null : Math.Max(0, m.MaxAttempts.Value - attempt.AttemptNo);
        return Ok(new
        {
            attemptNo = attempt.AttemptNo,
            scorePercent = grade.ScorePercent,
            passed = grade.Passed,
            correctCount = grade.CorrectCount,
            total = grade.Total,
            passMarkPercent = m.PassMarkPercent,
            attemptsLeft,
            // Yalnızca doğru/yanlış — hangi seçeneğin doğru olduğu söylenmez.
            perQuestion = grade.PerQuestion.Select(kv => new { questionId = kv.Key, correct = kv.Value }),
            enrollmentStatus = enrollment.Status.ToString(),
            certificateId = cert?.Id,
        });
    }

    private async Task<ModuleProgress> UpsertProgressAsync(Enrollment enrollment, Guid moduleId, Guid employeeId, CancellationToken ct)
    {
        var p = await _db.ModuleProgress.FirstOrDefaultAsync(x => x.EnrollmentId == enrollment.Id && x.ModuleId == moduleId, ct);
        if (p is not null) return p;
        p = new ModuleProgress { EnrollmentId = enrollment.Id, ModuleId = moduleId, EmployeeId = employeeId, Status = ProgressStatus.InProgress };
        _db.ModuleProgress.Add(p);
        return p;
    }

    /// <summary>Video/metin modülü: öğrenen "tamamladım" der (kendi kaydı için).</summary>
    [HttpPost("modules/{moduleId:guid}/complete")]
    public async Task<IActionResult> CompleteModule(Guid courseId, Guid moduleId, CancellationToken ct)
    {
        var m = await _db.Modules.FirstOrDefaultAsync(x => x.Id == moduleId && x.CourseId == courseId, ct);
        if (m is null) return NotFound();
        if (m.Kind is not (ModuleKind.Video or ModuleKind.Text))
            return BadRequest(new { message = "Sınav ve SCORM modülleri sonuçlarıyla tamamlanır" });
        var (me, enrollment) = await MyEnrollmentAsync(courseId, ct);
        if (me is null || enrollment is null) return StatusCode(403, new { message = "Önce eğitime kaydolun" });
        if (enrollment.Status is EnrollmentStatus.Completed or EnrollmentStatus.Failed or EnrollmentStatus.Dropped)
            return Conflict(new { message = "Bu eğitim kaydı sonuçlanmış" });
        var p = await UpsertProgressAsync(enrollment, moduleId, me.Id, ct);
        if (p.Status != ProgressStatus.Completed)
        {
            p.Status = ProgressStatus.Completed;
            p.CompletedAt = DateTimeOffset.UtcNow;
            p.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        var cert = await CourseCompletion.EvaluateAsync(_db, _tenant, enrollment, _dir, ct);
        return Ok(new { moduleId, status = p.Status, enrollmentStatus = enrollment.Status.ToString(), certificateId = cert?.Id });
    }

    /* ------------------------------------------------------------ ilerleme ve sonuçlar */

    private async Task<object> ProgressViewAsync(Guid courseId, Enrollment e, List<CourseModule> modules, CancellationToken ct)
    {
        var progress = await _db.ModuleProgress.AsNoTracking().Where(p => p.EnrollmentId == e.Id).ToListAsync(ct);
        var attempts = await _db.QuizAttempts.AsNoTracking().Where(a => a.EnrollmentId == e.Id).ToListAsync(ct);
        var runtime = await _db.ScormRuntime.AsNoTracking().Where(r => r.EnrollmentId == e.Id).ToListAsync(ct);
        var cert = await _db.Certifications.AsNoTracking().FirstOrDefaultAsync(c => c.EnrollmentId == e.Id, ct);
        return new
        {
            enrollmentId = e.Id, e.EmployeeId, status = e.Status.ToString(), e.Score, e.EnrolledAt, e.CompletedAt,
            completedModules = progress.Count(p => p.Status == ProgressStatus.Completed && modules.Any(m => m.Id == p.ModuleId)),
            totalModules = modules.Count,
            modules = modules.Select(m =>
            {
                var p = progress.FirstOrDefault(x => x.ModuleId == m.Id);
                var rt = runtime.FirstOrDefault(x => x.ModuleId == m.Id);
                return new
                {
                    moduleId = m.Id, m.Title, m.Kind,
                    status = p?.Status ?? ProgressStatus.NotStarted, score = p?.Score, completedAt = p?.CompletedAt,
                    attempts = attempts.Count(a => a.ModuleId == m.Id),
                    lessonStatus = rt?.LessonStatus,
                };
            }),
            certificate = cert is null ? null : new { cert.Id, cert.VerificationCode, cert.IssuedOn, cert.ExpiresOn },
        };
    }

    /// <summary>Bir çalışanın bu eğitimdeki ilerlemesi (kendisi, departman başkanı, İK).</summary>
    [HttpGet("progress")]
    public async Task<IActionResult> Progress(Guid courseId, [FromQuery] Guid? employeeId, CancellationToken ct)
    {
        var me = await _dir.MeAsync(ct);
        var empId = employeeId ?? me?.Id;
        if (empId is null) return NotFound(new { message = "Bu hesaba bağlı çalışan kaydı bulunamadı" });
        if (empId != me?.Id)
        {
            var target = await _dir.FindAsync(empId.Value, ct);
            if (target is null || !LearningDirectory.CanSee(target, me?.Id, IsHr)) return NotFound();
            await _dir.AuditAsync("LearningResult", empId.Value.ToString(), "SensitiveViewed", new { field = "courseProgress", courseId });
        }
        var e = await _db.Enrollments.AsNoTracking().FirstOrDefaultAsync(x => x.CourseId == courseId && x.EmployeeId == empId, ct);
        if (e is null) return Ok(new { enrolled = false });
        var modules = await _db.Modules.AsNoTracking().Where(m => m.CourseId == courseId).OrderBy(m => m.Position).ToListAsync(ct);
        return Ok(new { enrolled = true, progress = await ProgressViewAsync(courseId, e, modules, ct) });
    }

    /// <summary>Eğitim sonuçları listesi: İK herkesi, departman başkanı yalnızca kendi departmanını görür.</summary>
    [HttpGet("results")]
    public async Task<IActionResult> Results(Guid courseId, CancellationToken ct)
    {
        var me = await _dir.MeAsync(ct);
        var enrollments = await _db.Enrollments.AsNoTracking().Where(e => e.CourseId == courseId).ToListAsync(ct);
        var people = (await _dir.ActiveAsync(ct)).ToDictionary(p => p.Id);
        var visible = enrollments.Where(e => people.TryGetValue(e.EmployeeId, out var p)
            ? LearningDirectory.CanSee(p, me?.Id, IsHr) && (IsHr || p.Id != me?.Id)
            : IsHr).ToList();
        if (!IsHr && visible.Count == 0 && !(await _dir.DepartmentsAsync(ct)).Any(d => d.HeadEmployeeId == me?.Id))
            return StatusCode(403, new { message = "Sonuçlar yalnızca İK'ya ve departman yöneticisine açık" });
        var modules = await _db.Modules.AsNoTracking().Where(m => m.CourseId == courseId).OrderBy(m => m.Position).ToListAsync(ct);
        var list = new List<object>();
        foreach (var e in visible)
            list.Add(new { name = people.TryGetValue(e.EmployeeId, out var p) ? p.FullName : "—", progress = await ProgressViewAsync(courseId, e, modules, ct) });
        await _dir.AuditAsync("LearningResult", courseId.ToString(), "SensitiveViewed", new { field = "courseResults", count = list.Count });
        return Ok(list);
    }

    /* ------------------------------------------------------------ yetkinlik etiketleri, ayarlar */

    [HttpGet("competencies")]
    public async Task<IActionResult> Tags(Guid courseId, CancellationToken ct) =>
        Ok(await _db.CourseCompetencies.AsNoTracking().Where(t => t.CourseId == courseId).ToListAsync(ct));

    public record TagInput(Guid CompetencyId, int TargetLevel);

    [HttpPut("competencies")]
    public async Task<IActionResult> SaveTags(Guid courseId, [FromBody] List<TagInput> body, CancellationToken ct)
    {
        if (!IsHr) return Forbid();
        if (!await _db.Courses.AnyAsync(c => c.Id == courseId, ct)) return NotFound();
        body ??= new();
        if (body.Count > 50) return BadRequest(new { message = "En fazla 50 yetkinlik etiketlenebilir" });
        if (body.Any(t => t.TargetLevel is < 1 or > 5)) return BadRequest(new { message = "Hedef seviye 1-5 arasında olmalı" });
        if (body.Select(t => t.CompetencyId).Distinct().Count() != body.Count) return BadRequest(new { message = "Aynı yetkinlik iki kez etiketlenemez" });
        var ids = body.Select(t => t.CompetencyId).ToList();
        if (await _db.Competencies.CountAsync(c => ids.Contains(c.Id), ct) != ids.Count) return BadRequest(new { message = "Yetkinlik bulunamadı" });
        _db.CourseCompetencies.RemoveRange(_db.CourseCompetencies.Where(t => t.CourseId == courseId));
        foreach (var t in body) _db.CourseCompetencies.Add(new CourseCompetency { CourseId = courseId, CompetencyId = t.CompetencyId, TargetLevel = t.TargetLevel });
        await _db.SaveChangesAsync(ct);
        return Ok(await _db.CourseCompetencies.AsNoTracking().Where(t => t.CourseId == courseId).ToListAsync(ct));
    }

    public record CourseSettingsInput(int? CertificateValidityMonths);

    [HttpPut("settings")]
    public async Task<IActionResult> Settings(Guid courseId, [FromBody] CourseSettingsInput body, CancellationToken ct)
    {
        if (!IsHr) return Forbid();
        var c = await _db.Courses.FirstOrDefaultAsync(x => x.Id == courseId, ct);
        if (c is null) return NotFound();
        if (body.CertificateValidityMonths is < 1 or > 120) return BadRequest(new { message = "Geçerlilik 1-120 ay olmalı (boş: süresiz)" });
        c.CertificateValidityMonths = body.CertificateValidityMonths;
        await _db.SaveChangesAsync(ct);
        return Ok(new { c.Id, c.CertificateValidityMonths });
    }
}

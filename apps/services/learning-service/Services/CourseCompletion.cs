using Microsoft.EntityFrameworkCore;
using LearningService.Data;
using LearningService.Models;
using LearningService.Tenancy;

namespace LearningService.Services;

public enum CourseOutcome { NoContent, InProgress, Completed, Failed }

/// <summary>
/// Y20: modül ilerlemesinden eğitim sonucunu çıkarır. Tüm modüller tamamlanınca (sınav geçildi,
/// SCORM passed/completed, video/metin "tamamladım") kayıt Completed olur ve doğrulama kodlu
/// sertifika kaydı üretilir. Sınav hakkı biten ve geçilemeyen sınav kaydı Failed yapar.
/// </summary>
public static class CourseCompletion
{
    /// <summary>Saf karar: modül durumları + "hakkı biten başarısız sınav var mı".</summary>
    public static CourseOutcome Decide(int moduleCount, IReadOnlyCollection<string> statuses, bool exhaustedFailedQuiz)
    {
        if (moduleCount == 0) return CourseOutcome.NoContent;
        if (exhaustedFailedQuiz) return CourseOutcome.Failed;
        if (statuses.Count(s => s == ProgressStatus.Completed) >= moduleCount) return CourseOutcome.Completed;
        return CourseOutcome.InProgress;
    }

    public static async Task<Certification?> EvaluateAsync(LearningDbContext db, ITenantContext tenant, Enrollment enrollment,
        LearningDirectory? directory, CancellationToken ct)
    {
        if (enrollment.Status is EnrollmentStatus.Completed or EnrollmentStatus.Failed or EnrollmentStatus.Dropped) return null;
        var modules = await db.Modules.Where(m => m.CourseId == enrollment.CourseId).ToListAsync(ct);
        var progress = await db.ModuleProgress.Where(p => p.EnrollmentId == enrollment.Id).ToListAsync(ct);
        var moduleIds = modules.Select(m => m.Id).ToHashSet();
        var relevant = progress.Where(p => moduleIds.Contains(p.ModuleId)).ToList();

        var exhausted = false;
        foreach (var quiz in modules.Where(m => m.Kind == ModuleKind.Quiz && m.MaxAttempts is > 0))
        {
            var p = relevant.FirstOrDefault(x => x.ModuleId == quiz.Id);
            if (p?.Status != ProgressStatus.Failed) continue;
            var used = await db.QuizAttempts.CountAsync(a => a.EnrollmentId == enrollment.Id && a.ModuleId == quiz.Id, ct);
            if (used >= quiz.MaxAttempts) exhausted = true;
        }

        var outcome = Decide(modules.Count, relevant.Select(p => p.Status).ToList(), exhausted);
        switch (outcome)
        {
            case CourseOutcome.NoContent:
                return null;
            case CourseOutcome.InProgress:
                if (enrollment.Status == EnrollmentStatus.Enrolled && relevant.Count > 0)
                {
                    enrollment.Status = EnrollmentStatus.InProgress;
                    await db.SaveChangesAsync(ct);
                }
                return null;
            case CourseOutcome.Failed:
                enrollment.Status = EnrollmentStatus.Failed;
                enrollment.CompletedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                return null;
        }

        var scores = relevant.Where(p => p.Score is not null).Select(p => p.Score!.Value).ToList();
        enrollment.Status = EnrollmentStatus.Completed;
        enrollment.Score = scores.Count > 0 ? Math.Round(scores.Average(), 2) : null;
        enrollment.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return await IssueCertificateAsync(db, tenant, enrollment, directory, ct);
    }

    /// <summary>Kayıt başına tek sertifika (EnrollmentId benzersiz).</summary>
    public static async Task<Certification?> IssueCertificateAsync(LearningDbContext db, ITenantContext tenant, Enrollment enrollment,
        LearningDirectory? directory, CancellationToken ct)
    {
        var existing = await db.Certifications.FirstOrDefaultAsync(c => c.EnrollmentId == enrollment.Id, ct);
        if (existing is not null) return existing;
        var course = await db.Courses.FirstOrDefaultAsync(c => c.Id == enrollment.CourseId, ct);
        if (course is null) return null;
        var issuedOn = DateOnly.FromDateTime((enrollment.CompletedAt ?? DateTimeOffset.UtcNow).UtcDateTime);
        var issuer = directory is null ? null : await directory.TenantNameAsync(ct);
        var cert = new Certification
        {
            EmployeeId = enrollment.EmployeeId,
            Name = course.Title,
            Issuer = issuer ?? "HR360",
            CourseId = course.Id,
            EnrollmentId = enrollment.Id,
            VerificationCode = CertificateCodes.New(),
            IsMandatory = course.IsMandatory,
            IssuedOn = issuedOn,
            ExpiresOn = course.CertificateValidityMonths is > 0 ? issuedOn.AddMonths(course.CertificateValidityMonths.Value) : null,
        };
        db.Certifications.Add(cert);
        await db.SaveChangesAsync(ct);
        await LearningDirectory.NotifyAsync(db, tenant.TenantSlug ?? cert.TenantSlug, cert.EmployeeId, "Sertifikanız hazır",
            $"«{course.Title}» eğitimini tamamladınız; sertifikanızı Eğitim > Sertifikalar ekranından yazdırabilirsiniz.",
            "learning.certificate.issued", $"/panel/egitim/sertifika/{cert.Id}", ct);
        return cert;
    }
}

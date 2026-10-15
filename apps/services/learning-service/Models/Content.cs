using LearningService.Tenancy;

namespace LearningService.Models;

public static class ModuleKind
{
    public const string Video = "Video";
    public const string Text = "Text";
    public const string Quiz = "Quiz";
    public const string Scorm = "Scorm";
    public static readonly string[] All = { Video, Text, Quiz, Scorm };
}

public static class ProgressStatus
{
    public const string NotStarted = "NotStarted";
    public const string InProgress = "InProgress";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}

/// <summary>Y20: eğitim modülü (video bağlantısı, metin, sınav, SCORM 1.2 paketi).</summary>
public class CourseModule : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CourseId { get; set; }
    public int Position { get; set; }
    public required string Title { get; set; }
    public string Kind { get; set; } = ModuleKind.Text;
    public string? VideoUrl { get; set; }
    public string? TextBody { get; set; }
    public int? PassMarkPercent { get; set; }
    public int? MaxAttempts { get; set; }
    public Guid? ScormPackageId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class QuestionKind
{
    public const string Single = "Single";
    public const string Multiple = "Multiple";
}

/// <summary>
/// Sınav sorusu. <see cref="CorrectJson"/> doğru seçenek kimliklerini tutar ve hiçbir
/// öğrenen ucunda serileştirilmez (yanıtlar sunucuda puanlanır).
/// </summary>
public class QuizQuestion : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ModuleId { get; set; }
    public int Position { get; set; }
    public required string Text { get; set; }
    public string Kind { get; set; } = QuestionKind.Single;
    /// <summary>[{"id":"a","text":"..."}]</summary>
    public string OptionsJson { get; set; } = "[]";
    /// <summary>["a","c"] — GİZLİ.</summary>
    public string CorrectJson { get; set; } = "[]";
}

public class QuizAttempt : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ModuleId { get; set; }
    public Guid EnrollmentId { get; set; }
    public Guid EmployeeId { get; set; }
    public int AttemptNo { get; set; }
    public string AnswersJson { get; set; } = "{}";
    public decimal ScorePercent { get; set; }
    public bool Passed { get; set; }
    public DateTimeOffset SubmittedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ModuleProgress : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EnrollmentId { get; set; }
    public Guid ModuleId { get; set; }
    public Guid EmployeeId { get; set; }
    public string Status { get; set; } = ProgressStatus.InProgress;
    public decimal? Score { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ScormPackage : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Title { get; set; }
    public string? ManifestIdentifier { get; set; }
    public required string EntryPoint { get; set; }
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }
    public string? UploadedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ScormFile : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PackageId { get; set; }
    public required string Path { get; set; }
    public required string ContentType { get; set; }
    public int Size { get; set; }
    public byte[] Content { get; set; } = Array.Empty<byte>();
}

/// <summary>SCORM 1.2 çalışma zamanı: öğrenenin paket içindeki durumu.</summary>
public class ScormRuntime : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EnrollmentId { get; set; }
    public Guid ModuleId { get; set; }
    public Guid PackageId { get; set; }
    public Guid EmployeeId { get; set; }
    public string LessonStatus { get; set; } = "not attempted";
    public decimal? ScoreRaw { get; set; }
    public string? SuspendData { get; set; }
    public string? LessonLocation { get; set; }
    public int SessionCount { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>G17: gönderilmiş sertifika hatırlatması (tekrar gönderimi engeller).</summary>
public class CertReminder : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CertificationId { get; set; }
    public required string Kind { get; set; }
    public Guid RecipientEmployeeId { get; set; }
    public DateOnly ExpiresOn { get; set; }
    public DateTimeOffset SentAt { get; set; } = DateTimeOffset.UtcNow;
}

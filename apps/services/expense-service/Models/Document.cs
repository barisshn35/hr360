using ExpenseService.Tenancy;

namespace ExpenseService.Models;

public enum DocumentType { Contract, Payslip, IdCard, Diploma, Certificate, Health, Other }

/// <summary>Ozluk dokumani. Dosyanin kendisi MinIO'da, burada yalnizca metadata.</summary>
public class Document : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EmployeeId { get; set; }
    public DocumentType Type { get; set; } = DocumentType.Other;
    public required string FileName { get; set; }
    public required string StorageKey { get; set; }
    public long SizeBytes { get; set; }
    public string? ContentType { get; set; }
    public DateTimeOffset UploadedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? UploadedByEmployeeId { get; set; }
    /// <summary>
    /// Y28: ilk basit e-imza zamani. Doluysa dokuman satiri DEGISTIRILEMEZ (veritabani
    /// tetikleyicisi, scripts/sql/2026-10-09_identity_sign.sql); yeniden imza yeni talep ister.
    /// </summary>
    public DateTimeOffset? SignedAt { get; set; }
}

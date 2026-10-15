using ExpenseService.Tenancy;

namespace ExpenseService.Models;

/// <summary>Imza talebi durumu.</summary>
public static class SignatureStatus
{
    public const string Pending = "Pending";
    public const string Signed = "Signed";
    public const string Cancelled = "Cancelled";
}

/// <summary>
/// Y28: IK'nin bir ozluk dokumanini calisana imzaya gondermesi (talep yasam dongusu). Kod (OTP),
/// imza ve kanit governance-service'teki TEK imza motorundadir (DocumentType "HrDocument");
/// basarili imzada kanitin kimligi <see cref="EvidenceRef"/>'te tutulur.
/// Basit elektronik imza - 5070 sayili Kanun kapsaminda nitelikli (guvenli) e-imza DEGILDIR.
/// </summary>
public class DocumentSignature : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    /// <summary>Imzalayacak calisan = dokumanin sahibi.</summary>
    public Guid EmployeeId { get; set; }
    public Guid? RequestedByEmployeeId { get; set; }
    public string? RequestedByUserId { get; set; }
    public string Status { get; set; } = SignatureStatus.Pending;
    /// <summary>Talep anindaki dokuman ozeti (SHA-256 hex); imzada yeniden hesaplanip karsilastirilir.</summary>
    public required string DocumentHash { get; set; }
    public string? Message { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    // Eski (birlesme oncesi) OTP sutunlari: artik YAZILMAZ; eski satirlar icin modelde duruyor.
    public string? OtpHash { get; set; }
    public string? OtpChannel { get; set; }
    public DateTimeOffset? OtpExpiresAt { get; set; }
    public int OtpAttempts { get; set; }
    public int OtpSentCount { get; set; }
    public DateTimeOffset? OtpLastSentAt { get; set; }
    public DateTimeOffset? SignedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    /// <summary>governance_signatures kanit kimligi (yeni imzalar). Null ise eski kanit expense_signature_evidence'tadir.</summary>
    public Guid? EvidenceRef { get; set; }
}

/// <summary>
/// ESKI imza kaniti (birlesme oncesi imzalar; SALT OKUNUR - yeni kayit yazilmaz). Veritabaninda UPDATE tetikleyiciyle engellenir (degistirilemez); dokuman
/// saklandigi surece saklanir, dokuman imha edildiginde birlikte silinir.
/// </summary>
public class SignatureEvidence : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SignatureId { get; set; }
    public Guid DocumentId { get; set; }
    public Guid SignerEmployeeId { get; set; }
    public DateTimeOffset SignedAt { get; set; }
    public required string DocumentHash { get; set; }
    public string? IpMasked { get; set; }
    public string? UserAgentHash { get; set; }
    public required string OtpChannel { get; set; }
    public required string EvidenceHash { get; set; }
    public string Method { get; set; } = "SimpleElectronicSignature-OTP";
}

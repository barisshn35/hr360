using RecruitmentService.Tenancy;

namespace RecruitmentService.Models;

/// <summary>
/// Teklif yaşam döngüsü: PendingApproval → (Approved | Rejected) → Sent → (Accepted | Declined | Expired).
/// Yalnızca onaylanmış teklif adaya gönderilebilir; yalnızca gönderilmiş teklif yanıtlanabilir.
/// </summary>
public enum OfferStatus { PendingApproval, Approved, Rejected, Sent, Accepted, Declined, Withdrawn, Expired }

/// <summary>Y18: iş teklifi. Brüt ücret yalnızca İK ve teklifin onaycısına gösterilir.</summary>
public class Offer : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ApplicationId { get; set; }
    public required string PositionTitle { get; set; }
    public decimal GrossSalary { get; set; }
    public string Currency { get; set; } = "TRY";
    public DateOnly StartDate { get; set; }
    public string? Benefits { get; set; }
    public DateOnly ExpiresAt { get; set; }
    /// <summary>
    /// Oluşturulan mektup metni (ücreti içerir). Özellik adı denetim kaydının hassas alan maskesine
    /// ("salary") takılsın diye böyledir: audit_log'a değer yerine "***" yazılır. Sütun: "LetterText".
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("letterText")]
    public string SalaryLetterText { get; set; } = "";
    public OfferStatus Status { get; set; } = OfferStatus.PendingApproval;
    /// <summary>workflow-service onay akışı; null ise onaycı bulunamadı ve İK doğrudan karar verir.</summary>
    public Guid? WorkflowRequestId { get; set; }
    public Guid? ApproverEmployeeId { get; set; }
    public Guid? DecidedByEmployeeId { get; set; }
    public string? DecidedByUserId { get; set; }
    public string? DecisionNote { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? RespondedAt { get; set; }
    public string? CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // ---------------------------------------------------------- dalga 11: e-imza (governance tek imza motoru)
    /// <summary>Aday imza bağlantısı jetonunun SHA-256 özeti (jetonun kendisi saklanmaz). Null: bağlantı yok/iptal.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string? SignTokenHash { get; set; }
    public DateTimeOffset? SignTokenCreatedAt { get; set; }
    /// <summary>İmzalanan mektup metninin (SalaryLetterText) SHA-256'sı — governance kanıtındaki belge özeti.</summary>
    public string? LetterSha256 { get; set; }
    /// <summary>İmzalı mektup (HTML, kanıt bloğu ile). Ücreti içerir; ad denetim maskesine ("salary") takılsın diye böyledir. Sütun: "SignedLetterHtml".</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string? SignedSalaryLetterHtml { get; set; }
    /// <summary>İmzalı HTML belgesinin SHA-256'sı (saklanan belgenin bütünlüğü).</summary>
    public string? SignedDocumentSha256 { get; set; }
    /// <summary>governance_signatures kanıt kimliği.</summary>
    public Guid? SignatureEvidenceId { get; set; }
    public DateTimeOffset? SignedAt { get; set; }
}

/// <summary>Kiracının teklif mektubu şablonu ({yer tutucular} ile).</summary>
public class OfferTemplate : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Body { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

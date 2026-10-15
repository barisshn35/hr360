using CompensationService.Tenancy;

namespace CompensationService.Models;

/* ======================================================================
 * Bordro ekosistemi (Dalga 5b): resmî/ödeme/muhasebe dosyaları, avans ve
 * borç taksitleri, esnek yan haklar, zam dönemi.
 * ==================================================================== */

/// <summary>
/// Üretilmiş dışa aktarım dosyası (SGK, banka, muhasebe). İçerik AES-256-GCM ile şifreli
/// saklanır; indirme denetim kaydına yazılır. Banka dosyası bir kez indirilir, sonra içerik
/// silinir; diğerleri en geç 24 saat sonra silinir (yalnızca kayıt kalır).
/// </summary>
public class PayrollExport : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PeriodId { get; set; }
    /// <summary>SgkAphb | SgkHires | Bank | Accounting</summary>
    public string Kind { get; set; } = "";
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "text/plain";
    public byte[]? Cipher { get; set; }
    public int RowCount { get; set; }
    public bool SingleUse { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddHours(24);
    public DateTimeOffset? DownloadedAt { get; set; }
    public string? DownloadedBy { get; set; }
    public int DownloadCount { get; set; }
    public DateTimeOffset? PurgedAt { get; set; }
    /// <summary>Üretilen dosyanın SHA-256 özeti (onaltılık); denetim kaydına da yazılır.</summary>
    public string? ContentSha256 { get; set; }
    /// <summary>Biçim/şablon: xml | txt (SGK), generic | ornek-a | ornek-b | custom (banka), logo/mikro/netsis (muhasebe).</summary>
    public string? Format { get; set; }
}

public enum AdvanceStatus { Pending, Approved, Rejected, Closed, Cancelled }

/// <summary>Maaş avansı ya da şirket borcu; onaylanınca taksitleri bordroda kesinti olarak düşülür.</summary>
public class SalaryAdvance : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EmployeeId { get; set; }
    /// <summary>Advance | Loan</summary>
    public string Kind { get; set; } = "Advance";
    public decimal Amount { get; set; }
    public int Installments { get; set; } = 1;
    /// <summary>İlk taksidin düşüleceği dönem.</summary>
    public int StartYear { get; set; }
    public int StartMonth { get; set; }
    public string? Reason { get; set; }
    public AdvanceStatus Status { get; set; } = AdvanceStatus.Pending;
    public decimal RepaidAmount { get; set; }
    public string? DecidedBy { get; set; }
    public string? DecisionNote { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Yıllık esnek yan hak planı: çalışan başına bütçe ve seçim penceresi.</summary>
public class BenefitPlan : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public int Year { get; set; }
    public decimal BudgetPerEmployee { get; set; }
    public DateOnly WindowStart { get; set; }
    public DateOnly WindowEnd { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Seçilebilir yan hak (yemek, ulaşım, özel sağlık sigortası, spor, eğitim...).</summary>
public class BenefitOption : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PlanId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Meal | Transport | Health | Wellness | Education | Other</summary>
    public string Category { get; set; } = "Other";
    public decimal AnnualCost { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
}

public class BenefitElection : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PlanId { get; set; }
    public Guid EmployeeId { get; set; }
    /// <summary>Seçilen seçenek kimlikleri (JSON dizi).</summary>
    public string OptionIdsJson { get; set; } = "[]";
    public decimal Total { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum RaiseCycleStatus { Draft, Open, Closed }

/// <summary>Zam dönemi: bütçe yüzdesi, yöneticilerin öneri penceresi, İK onayı ve uygulama.</summary>
public class RaiseCycle : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public int Year { get; set; }
    public decimal BudgetPercent { get; set; }
    public DateOnly EffectiveDate { get; set; }
    public RaiseCycleStatus Status { get; set; } = RaiseCycleStatus.Draft;
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? AppliedAt { get; set; }
}

public enum RaiseProposalStatus { Proposed, Approved, Rejected, Applied }

public class RaiseProposal : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CycleId { get; set; }
    public Guid EmployeeId { get; set; }
    public decimal CurrentSalary { get; set; }
    public decimal ProposedPercent { get; set; }
    public decimal ProposedSalary { get; set; }
    public string? Note { get; set; }
    public RaiseProposalStatus Status { get; set; } = RaiseProposalStatus.Proposed;
    public string ProposedBy { get; set; } = "";
    public string? DecidedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

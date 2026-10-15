using CompensationService.Tenancy;

namespace CompensationService.Models;

/* ======================================================================
 * Bordro dalgası 8 (madde 58–65): Türkiye bordrosu — SGK bildirim alanları,
 * bordro ayarları, fark bordrosu, kıdem/ihbar, e-bordro teslim kaydı.
 * Tablolar scripts/sql/2026-10-21_payroll_tr.sql ile oluşturulur.
 * ==================================================================== */

/// <summary>
/// Şirketin bordro ayarları (tek satır): SGK varsayılanları ve eksik gün kodları, muhasebe hesap
/// planı ve masraf merkezi kodları, banka dosyası şablonu. Alanlar JSON (bkz. <see cref="Payroll.PayrollSettingsModel"/>).
/// </summary>
public class PayrollSettings : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public string SgkJson { get; set; } = "{}";
    public string AccountMapJson { get; set; } = "{}";
    public string CostCentersJson { get; set; } = "{}";
    public string BankTemplateJson { get; set; } = "{}";
    public string? UpdatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Çalışanın SGK bildirim bilgileri. KVKK: meslek kodu ve SGDP (emekli çalışan) özel nitelikli veri
/// değildir; yasal bildirim yükümlülüğü için tutulur ve yalnızca bordro yetkilisi görür.
/// </summary>
public class EmployeeSgkInfo : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EmployeeId { get; set; }
    /// <summary>SGK meslek kodu (ör. "2512.01"); boşsa APHB doğrulamasında uyarı.</summary>
    public string? OccupationCode { get; set; }
    /// <summary>Belge türü (boşsa şirket varsayılanı; SGDP'de "02").</summary>
    public string? DocumentType { get; set; }
    /// <summary>Kanun numarası (boşsa şirket varsayılanı, ör. "05510" ya da teşvik kanunu).</summary>
    public string? LawNo { get; set; }
    /// <summary>Sosyal güvenlik destek primi kapsamında (emekli olup çalışan).</summary>
    public bool Sgdp { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Fark bordrosu satırı: kapanmış bir dönemin ücreti sonradan (geriye dönük zamla) değiştiğinde
/// eski ve yeni brüt arasındaki fark. Kapanmış dönem AÇILMAZ; fark, onaylayanın seçtiği açık dönemde
/// "Fark: 2026/03" açıklamalı ek ödeme olarak (kaynağı bu kayıt) bordroya girer.
/// </summary>
public class RetroDiff : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EmployeeId { get; set; }
    public Guid SourcePeriodId { get; set; }
    public int SourceYear { get; set; }
    public int SourceMonth { get; set; }
    public decimal OldBase { get; set; }
    public decimal NewBase { get; set; }
    public decimal OldGross { get; set; }
    public decimal NewGross { get; set; }
    public decimal DiffGross { get; set; }
    /// <summary>Approved | Cancelled (hedef dönemden ek ödeme silindiyse ya da dönem silindiyse).</summary>
    public string Status { get; set; } = "Approved";
    public Guid TargetPeriodId { get; set; }
    public Guid? AdjustmentId { get; set; }
    public string CreatedBy { get; set; } = "";
    public string? CreatedByName { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Kıdem ve ihbar hesabı (işten ayrılış). Hazırlayan kaydeder (Taslak), başka bir bordro yetkilisi onaylar
/// (otomatik karar yok). Onaylı hesaptan ibraname belgesi üretilir (governance belge şablonu).
/// </summary>
public class SeveranceCalc : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EmployeeId { get; set; }
    public Guid? OffboardingCaseId { get; set; }
    public DateOnly HireDate { get; set; }
    public DateOnly LastWorkingDay { get; set; }
    public string Reason { get; set; } = "";
    public string InputJson { get; set; } = "{}";
    public string ResultJson { get; set; } = "{}";
    public decimal TotalNet { get; set; }
    /// <summary>Draft | Approved | Rejected</summary>
    public string Status { get; set; } = "Draft";
    public string PreparedBy { get; set; } = "";
    public string? PreparedByName { get; set; }
    public string? DecidedBy { get; set; }
    public string? DecidedByName { get; set; }
    public string? DecisionNote { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// e-Bordro teslim kaydı: pusula çalışana yayımlandığında içeriğin özeti ve şifreli kopyası; çalışanın
/// ilk/son açma ve "Okudum, teslim aldım" onayı (tarih, IP /24 öneki, onaylanan içerik özeti).
/// </summary>
public class PayslipDelivery : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PayslipId { get; set; }
    public Guid PeriodId { get; set; }
    public Guid EmployeeId { get; set; }
    public string ContentSha256 { get; set; } = "";
    public byte[]? SealedCopy { get; set; }
    public string? PublishedBy { get; set; }
    public DateTimeOffset PublishedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool EmailQueued { get; set; }
    public DateTimeOffset? FirstOpenedAt { get; set; }
    public DateTimeOffset? LastOpenedAt { get; set; }
    public int OpenCount { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public string? AcknowledgedSha256 { get; set; }
    public string? AckIpPrefix { get; set; }
}

/// <summary>
/// Devreden kümülatif gelir vergisi matrahı: yıl ortasında HR360'a geçen şirket ya da yıl içinde başka
/// işverenden gelen çalışan için, o yılın HR360 dışındaki GV matrahı toplamı. Bordro hesabında ve ihbar
/// GV'sinde yılın kümülatif matrahına eklenir (dilimler doğru yerden başlar). Bordro yetkilisi girer;
/// görüntüleme ve değişiklik audit_log'a yazılır.
/// </summary>
public class OpeningTaxBase : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EmployeeId { get; set; }
    public int Year { get; set; }
    public decimal Amount { get; set; }
    public string? Note { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

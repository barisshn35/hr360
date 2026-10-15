using ExpenseService.Tenancy;

namespace ExpenseService.Models;

/// <summary>TCMB döviz kuru önbelleği (gün, para birimi). Kaynak: TCMB ya da İK'nın elle girdiği kur.</summary>
public class FxRate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateOnly Date { get; set; }
    public string Currency { get; set; } = "";
    /// <summary>1 birim yabancı para = Rate TL (TCMB döviz alış).</summary>
    public decimal Rate { get; set; }
    /// <summary>TCMB | Manual</summary>
    public string Source { get; set; } = "TCMB";
    /// <summary>Elle girilen kur kiracıya özeldir; TCMB kuru herkes için ortaktır (boş).</summary>
    public string? TenantSlug { get; set; }
    public DateTimeOffset FetchedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Kiracının masraf politikası: kategori limitleri, fiş zorunluluğu, km ücreti, harcırah.</summary>
public class ExpensePolicy : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>{"Meal":{"perItem":500,"monthly":3000,"receiptAbove":0}, ...}</summary>
    public string LimitsJson { get; set; } = "{}";
    public decimal KmRate { get; set; } = 8m;
    public decimal PerDiemDomestic { get; set; } = 1000m;
    public decimal PerDiemAbroad { get; set; } = 100m;
    public string PerDiemAbroadCurrency { get; set; } = "EUR";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum TravelStatus { Submitted, Approved, Rejected, Cancelled, Completed }

/// <summary>
/// Seyahat talebi ve harcırah. KVKK: pasaport numarası yalnızca yurt dışı seyahatte, şifreli
/// tutulur ve seyahat bitiminden 7 gün sonra silinir; yalnızca çalışan ve İK görür.
/// </summary>
public class TravelRequest : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EmployeeId { get; set; }
    public string Destination { get; set; } = "";
    public bool Abroad { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public string Purpose { get; set; } = "";
    /// <summary>Plane | Bus | Train | Car | Other</summary>
    public string Transport { get; set; } = "Plane";
    public bool NeedsAccommodation { get; set; }
    public int PerDiemDays { get; set; }
    public decimal PerDiemRate { get; set; }
    public string PerDiemCurrency { get; set; } = "TRY";
    public decimal PerDiemTotal { get; set; }
    public decimal? AdvanceRequested { get; set; }
    public string? PassportCipher { get; set; }
    public DateTimeOffset? PassportPurgedAt { get; set; }
    public TravelStatus Status { get; set; } = TravelStatus.Submitted;
    public Guid? WorkflowRequestId { get; set; }
    public Guid? DecidedByEmployeeId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

using WorkflowService.Tenancy;

namespace WorkflowService.Models;

/// <summary>
/// Kiracı başına onay ayarları (tek satır). İK onaycısı: talep edenin üst onaycısı bulunamadığında
/// (ör. departman başının kendi izni, departmanı/başı olmayan çalışan) ya da süre aşımında
/// iletilecek üst yönetici yoksa talep bu kişiye gider. Tanımlı değilse talep askıda kalabilir.
/// </summary>
public class WorkflowSettings : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? HrApproverEmployeeId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

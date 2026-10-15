using NotificationService.Tenancy;

namespace NotificationService.Models;

/// <summary>
/// Dalga 12: kişinin arayüz tercihi (anahtar → JSON). Kişi Keycloak kimliğiyle ("sub") tutulur;
/// çalışan kaydı olmayan hesaplar da tercih saklayabilir. Yazım ham SQL ile (upsert) yapılır:
/// tercih değişiklikleri denetim kaydını (audit_log) gürültüyle doldurmasın.
/// </summary>
public class UiPreference : ITenantOwned
{
    public Guid Id { get; set; }
    public string TenantSlug { get; set; } = "";
    public string UserSub { get; set; } = "";
    public string Key { get; set; } = "";
    public string Value { get; set; } = "{}";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

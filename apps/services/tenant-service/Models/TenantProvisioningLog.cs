namespace TenantService.Models;

public enum ProvisioningStep
{
    TenantRecordCreated,
    KeycloakOrgCreated,
    AdminUserCreated,
    AdminRoleAssigned,
    CompanyRecordCreated,
    PasswordEmailSent,
    Completed,
    Failed
}

/// <summary>
/// Saglama (provisioning) adimlarinin izi. Kayit birden fazla dis sistemi
/// (DB + Keycloak) etkiledigi icin, yarida kalirsa nerede kaldigini bilmek
/// ve elle/otomatik telafi edebilmek gerekir.
/// </summary>
public class TenantProvisioningLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public ProvisioningStep Step { get; set; }
    public bool Success { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
}

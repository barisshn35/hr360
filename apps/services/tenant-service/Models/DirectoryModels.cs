namespace TenantService.Models;

// Dalga 5d (Y26, G28) - dizin saglama ve ozel alan adi kayitlari.
// NOT: TenantDbContext bilincli olarak kiraci filtresi uygulamaz (platform tablolari);
// bu tablolarin tum sorgulari TenantSlug ile ACIKCA filtrelenir.

/// <summary>
/// Kiraci yoneticisinin urettigi SCIM erisim jetonu. Jetonun kendisi ASLA saklanmaz -
/// yalnizca SHA-256 ozeti (TokenHash) ve listede gostermek icin ilk karakterleri.
/// </summary>
public class ScimToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string TenantSlug { get; set; }
    public required string Name { get; set; }
    public required string TokenHash { get; set; }
    public required string TokenPrefix { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>Kiracinin SCIM + LDAP/AD ayarlari (kiraci basina tek satir).</summary>
public class DirectorySettings
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string TenantSlug { get; set; }
    public bool SendInvitations { get; set; } = true;
    public bool LdapEnabled { get; set; }
    public bool LdapAutoSync { get; set; }
    public string? LdapUrl { get; set; }
    public string? LdapBindDn { get; set; }
    public string? LdapBindPasswordEncrypted { get; set; }
    public string? LdapBaseDn { get; set; }
    public string? LdapUserFilter { get; set; }
    public string LdapUsernameAttr { get; set; } = "uid";
    public string LdapEmailAttr { get; set; } = "mail";
    public string? LdapDepartmentAttr { get; set; }
    public string? LdapTitleAttr { get; set; }
    public string? LdapDisabledAttr { get; set; }
    public DateTimeOffset? LastSyncAt { get; set; }
    public string? LastSyncTrigger { get; set; }
    public string? LastSyncStatus { get; set; }
    public string? LastSyncSummary { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class DirectorySources
{
    public const string Scim = "scim";
    public const string Ldap = "ldap";
}

public static class EmployeeLinkStates
{
    /// <summary>Calisan kaydi henuz olusturulmadi.</summary>
    public const string Pending = "Pending";
    public const string Linked = "Linked";
    /// <summary>employee-service kaydi olusturamadi/guncelleyemedi (cakisma, gecici hata...); EmployeeError'da neden.</summary>
    public const string Failed = "Failed";
}

/// <summary>
/// SCIM ya da LDAP ile saglanan kullanici (KVKK: yalnizca kullanici adi, ad, soyad, birincil
/// e-posta, unvan, departman ve etkinlik durumu). SCIM kaynak kimligi (id) bu satirin Id'sidir;
/// Keycloak hesabi ve calisan kaydiyla iliskisi burada tutulur.
/// </summary>
public class DirectoryUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string TenantSlug { get; set; }
    public required string Source { get; set; }
    public string? ExternalId { get; set; }
    public required string UserName { get; set; }
    public string? GivenName { get; set; }
    public string? FamilyName { get; set; }
    public required string Email { get; set; }
    public string? Title { get; set; }
    public string? Department { get; set; }
    public bool Active { get; set; } = true;
    public string? KeycloakUserId { get; set; }
    public Guid? EmployeeId { get; set; }
    public string EmployeeState { get; set; } = EmployeeLinkStates.Pending;
    public string? EmployeeError { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeactivatedAt { get; set; }
}

public static class CustomDomainStatus
{
    public const string Pending = "Pending";
    public const string Verified = "Verified";
}

/// <summary>G28: kiracinin kendi alan adi (orn. ik.acme.com.tr), DNS TXT ile dogrulanir.</summary>
public class CustomDomain
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string TenantSlug { get; set; }
    public required string Domain { get; set; }
    public required string VerificationToken { get; set; }
    public string Status { get; set; } = CustomDomainStatus.Pending;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? VerifiedAt { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
    public string? LastCheckError { get; set; }
}

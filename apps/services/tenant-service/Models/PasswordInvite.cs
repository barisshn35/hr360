namespace TenantService.Models;

/// <summary>
/// HR360'ın kendi parola belirleme/sıfırlama linki (tablo: tenant_password_invites). Token
/// veritabanına yazılmaz; yalnızca SHA-256 özeti (<see cref="TokenHash"/>) tutulur. Link yalnızca
/// üretildiği anda gösterilir; kaybolursa yeniden üretilir (yenisi eskisini iptal eder).
/// </summary>
public class PasswordInvite
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Kiracı; platform hesabı (kiracısız) için null.</summary>
    public string? TenantSlug { get; set; }
    public required string KeycloakUserId { get; set; }
    public required string Email { get; set; }
    public string? FullName { get; set; }
    /// <summary><see cref="PasswordInvitePurposes"/>.</summary>
    public required string Purpose { get; set; }
    /// <summary>Hex SHA-256; null: link hiç üretilmedi (ör. dizin sağlaması, SMTP yok) ya da Keycloak e-postasıyla gitti.</summary>
    public string? TokenHash { get; set; }
    /// <summary><see cref="PasswordInviteChannels"/>.</summary>
    public required string Channel { get; set; }
    /// <summary>none | queued | keycloak | failed.</summary>
    public string EmailStatus { get; set; } = "none";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? CreatedBy { get; set; }
    public string? CreatedByName { get; set; }

    public string StatusAt(DateTimeOffset now) =>
        UsedAt is not null ? "used"
        : RevokedAt is not null ? "revoked"
        : ExpiresAt <= now ? "expired"
        : TokenHash is null && Channel != PasswordInviteChannels.Keycloak ? "pending"
        : "active";
}

public static class PasswordInvitePurposes
{
    /// <summary>İlk parola (yeni hesap).</summary>
    public const string Setup = "setup";
    /// <summary>Parola sıfırlama.</summary>
    public const string Reset = "reset";
}

public static class PasswordInviteChannels
{
    /// <summary>Link yöneticiye gösterildi (e-postayla da gitmiş olabilir).</summary>
    public const string Link = "link";
    /// <summary>Link yalnızca e-postayla gitti (ekranda gösterilmedi).</summary>
    public const string Email = "email";
    /// <summary>Eski yöntem: Keycloak'ın kendi parola e-postası (HR360 linki yok).</summary>
    public const string Keycloak = "keycloak";
}

/// <summary>Parola belirleme yöntemi: HR360 süreli davet linki (varsayılan) ya da Keycloak e-postası.</summary>
public static class PasswordSetupMethods
{
    public const string Invite = "invite";
    public const string Keycloak = "keycloak";
    public static bool Valid(string? m) => m is Invite or Keycloak;
}

/// <summary>
/// Arayüzden değiştirilen platform ayarları (tablo: platform_settings, tek satır Id = 1). Boş
/// alan: .env'deki ilk değer geçerli.
/// </summary>
public class PlatformSetting
{
    public int Id { get; set; } = 1;
    /// <summary>mailpit (paketteki test kutusu, gerçek gönderim yok) | custom.</summary>
    public string? SmtpMode { get; set; }
    public string? SmtpHost { get; set; }
    public int? SmtpPort { get; set; }
    public string? SmtpUser { get; set; }
    public string? SmtpPasswordEncrypted { get; set; }
    public string? SmtpFromAddress { get; set; }
    public string? SmtpFromName { get; set; }
    public bool? SignupEnabled { get; set; }
    public string? PasswordMethod { get; set; }
    public int? InviteTtlHours { get; set; }
    /// <summary>Herkese açık site: işletmeci ve iletişim bilgileri (boş = gösterilmez).</summary>
    public string? SiteOperatorName { get; set; }
    public string? SiteAddress { get; set; }
    public string? SiteEmail { get; set; }
    public string? SitePhone { get; set; }
    public string? SiteKep { get; set; }
    public string? SiteVerbisNo { get; set; }
    /// <summary>Gizlilik politikası / kullanım koşulları (Markdown; boş = varsayılan şablon).</summary>
    public string? PrivacyPolicyMd { get; set; }
    public string? TermsMd { get; set; }
    /// <summary>SSS: [{"q":"…","a":"…"}] (boş = varsayılan sorular).</summary>
    public string? FaqJson { get; set; }
    /// <summary>Arama motorlarında görünür mü (NULL/false = kapalı: robots.txt Disallow, noindex).</summary>
    public bool? SearchIndexing { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

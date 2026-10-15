namespace TenantService.Models;

public enum TenantStatus
{
    /// <summary>Kayit alindi, admin henuz parolasini belirlemedi.</summary>
    Pending,
    Active,
    /// <summary>Platform yoneticisince askiya alindi - giris engellenir.</summary>
    Suspended,
    Cancelled
}

/// <summary>
/// Bir musteri sirketi. Keycloak'taki Organization ile 1-1 eslesir
/// (KeycloakOrgId + Slug). Tum is verisi bu kaydin Id'siyle izole edilir.
/// </summary>
public class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Gorunen ad: "Acme Yazilim A.S."</summary>
    public required string Name { get; set; }

    /// <summary>
    /// URL/claim dostu benzersiz kisa ad: "acme-yazilim".
    /// Keycloak Organization alias'i olarak da kullanilir - JWT'deki
    /// organization claim'i bu degeri tasir.
    /// </summary>
    public required string Slug { get; set; }

    /// <summary>Sirketin e-posta alan adi: "acme.com.tr". Davet dogrulamasinda kullanilir.</summary>
    public string? EmailDomain { get; set; }

    public string? TaxNumber { get; set; }
    public TenantStatus Status { get; set; } = TenantStatus.Pending;

    /// <summary>Parola belirleme yontemi: null = platform varsayilani; "invite" | "keycloak" (bkz. PasswordSetupMethods).</summary>
    public string? PasswordSetupMethod { get; set; }

    /// <summary>Keycloak Organization kimligi - saglama sonrasi doldurulur.</summary>
    public string? KeycloakOrgId { get; set; }

    /// <summary>Ilk yonetici (tenant-admin) - Keycloak kullanici kimligi.</summary>
    public string? AdminUserId { get; set; }
    public required string AdminEmail { get; set; }
    public string? AdminFullName { get; set; }

    // --- Beyaz etiketleme (white-label) ---
    /// <summary>MinIO'da saklanan logo dosyasinin yolu.</summary>
    public string? LogoUrl { get; set; }

    /// <summary>
    /// Sirketin arayuzde kullandigi ana renk, "#c0392b" formatinda hex.
    /// Frontend --primary CSS degiskenini runtime'da bununla override eder;
    /// --primary-foreground (metin rengi) renk parlakligindan otomatik
    /// hesaplanir, tenant ayarlamaz.
    /// </summary>
    public string? PrimaryColorHex { get; set; }

    /// <summary>
    /// Tenant'in kendi SMTP sunucusu (opsiyonel). Bos ise
    /// platformun varsayilan gonderim borusu (Brevo) kullanilir.
    /// SmtpPasswordEncrypted DB'de duz metin TUTULMAZ - bkz. SmtpCredentialProtector.
    /// </summary>
    public string? SmtpHost { get; set; }
    public int? SmtpPort { get; set; }
    public string? SmtpUser { get; set; }
    public string? SmtpPasswordEncrypted { get; set; }
    public string? SmtpFromAddress { get; set; }
    public string? SmtpFromName { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ActivatedAt { get; set; }
    public DateTimeOffset? SuspendedAt { get; set; }

    /// <summary>
    /// NOT: JsonPropertyName olmadan bu alan JSON'a "suspendReason" olarak
    /// cikardi - ama frontend (TenantsPage.tsx / api/tenant.ts) hep
    /// "suspensionReason" okuyordu. Sonuc: askiya alirken girilen sebep
    /// veritabanina DOGRU yaziliyordu (Suspend ucu calisiyordu) ama
    /// platform-admin ekraninda hicbir zaman GORUNMUYORDU, hep "—"
    /// gosteriyordu (hardcore test sirasinda bulundu, 2. tur).
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("suspensionReason")]
    public string? SuspendReason { get; set; }

    /// <summary>
    /// Askiya alma sirasinda BU islemle kapatilan Keycloak hesaplari (JSON dizi).
    /// Yeniden etkinlestirmede yalnizca bunlar acilir; baska bir nedenle kapatilmis
    /// hesaplar kapali kalir. (Keycloak 25'in kullanici profili tanimsiz nitelikleri
    /// sakladigi icin isaret Keycloak'ta degil burada tutulur.)
    /// </summary>
    public string? SuspendedUserIdsJson { get; set; }

    /// <summary>
    /// G22: Bos degilse kiracinin kullanicilari API'ye yalnizca bu adreslerden (CIDR,
    /// virgulle ayrilmis) erisebilir. Tum servislerdeki TenantStatusGate uygular.
    /// </summary>
    public string? IpAllowlist { get; set; }

    /// <summary>
    /// Guvenlik dalgasi 2A: iki adimli dogrulama zorunlulugu. null/"off" = yok, "privileged" =
    /// IK, sirket yoneticisi ve yonetici rolleri, "all" = tum sirket kullanicilari. Bkz.
    /// Security/MfaPolicyRules ve Services/MfaPolicyHostedService.
    /// </summary>
    public string? MfaPolicy { get; set; }
}

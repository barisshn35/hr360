using NotificationService.Tenancy;

namespace NotificationService.Models;

/// <summary>
/// Kiracının SMS sağlayıcısı (Ayarlar › SMS). Yalnızca yurt içi sağlayıcılar (KVKK m.9: telefon numarası
/// ve ileti yurt dışına aktarılmaz). Parola / API gizli anahtarı şifreli saklanır, yanıtta asla dönmez.
/// </summary>
public class SmsSettings : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public bool Enabled { get; set; }
    /// <summary>netgsm | iletimerkezi (bkz. SmsProviders).</summary>
    public string Provider { get; set; } = "netgsm";
    /// <summary>Netgsm: abone kodu (kullanıcı adı); İleti Merkezi: API anahtarı (public key).</summary>
    public string Username { get; set; } = "";
    /// <summary>Netgsm: API parolası; İleti Merkezi: API gizli anahtarı (hash üretiminde kullanılır). KeyRing ile şifreli.</summary>
    public string SecretEnc { get; set; } = "";
    /// <summary>Onaylı gönderici adı (SMS başlığı), ör. FIRMAADI.</summary>
    public string Sender { get; set; } = "";
    /// <summary>Gün başına en fazla gönderim (maliyet sınırı); aşılırsa kalanlar ertesi güne ertelenir.</summary>
    public int DailyLimit { get; set; } = 200;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

using NotificationService.Tenancy;

namespace NotificationService.Models;

/// <summary>
/// Suppressed: kişi bu kategori için e-postayı kapatmış (gönderilmedi, kayıt kalır).
/// DigestQueued: günlük özete alınmayı bekliyor; Digested: özet e-postasıyla iletildi.
/// </summary>
public enum NotificationStatus { Pending, Sent, Failed, Read, Suppressed, DigestQueued, Digested }

public class Notification : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RecipientEmployeeId { get; set; }
    /// <summary>
    /// Olay kaynagi (orn. employee.hired) e-postayi zaten tasiyorsa buraya
    /// kaydedilir. Bos ise, gonderim sirasinda EmailSenderWorker bunu
    /// employee-service'ten RecipientEmployeeId ile sorup doldurur.
    /// </summary>
    public string? RecipientEmail { get; set; }
    public NotificationChannel Channel { get; set; } = NotificationChannel.InApp;
    public string? TemplateCode { get; set; }
    public string? Subject { get; set; }
    public required string Body { get; set; }
    /// <summary>Metnin dili (tr | en); e-posta çerçevesi de bu dilde üretilir.</summary>
    public string Language { get; set; } = "tr";
    public NotificationStatus Status { get; set; } = NotificationStatus.Pending;
    public string? FailureReason { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    /// <summary>E-postadaki eylem düğmesi (ör. tek kullanımlık karar sayfası).</summary>
    public string? ActionUrl { get; set; }
    public string? ActionLabel { get; set; }
    /// <summary>Uygulama içi bildirimin anlık bildirim (Web Push) olarak iletildiği an.</summary>
    public DateTimeOffset? PushedAt { get; set; }
    /// <summary>Sessiz saatler: e-posta / anlık bildirim bu andan önce iletilmez (ertelenir, düşürülmez).</summary>
    public DateTimeOffset? DeferredUntil { get; set; }
}

/// <summary>
/// Bir cihazın Web Push aboneliği. KVKK: uç nokta ve şifreleme anahtarları tarayıcıdan gelir;
/// kişi bildirimi kapatınca ya da oturumu kapatınca silinir.
/// </summary>
public class PushSubscription : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EmployeeId { get; set; }
    public string Endpoint { get; set; } = "";
    public string P256dh { get; set; } = "";
    public string Auth { get; set; } = "";
    public string? Device { get; set; }
    public int FailureCount { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Kurulumun VAPID anahtarı (tek satır; özel anahtar şifreli).</summary>
public class VapidKeyRow
{
    public int Id { get; set; } = 1;
    public string PublicKey { get; set; } = "";
    public string PrivateKeyEnc { get; set; } = "";
}

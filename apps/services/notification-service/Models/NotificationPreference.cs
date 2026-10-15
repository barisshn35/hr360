using NotificationService.Tenancy;

namespace NotificationService.Models;

/// <summary>
/// Çalışanın bildirim dili (tr | en). Arayüzde dil değiştirildiğinde güncellenir;
/// e-posta ve bildirim metinleri bu dilde üretilir. Kayıt yoksa Türkçe.
/// </summary>
public class NotificationPreference : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid EmployeeId { get; set; }
    public string Language { get; set; } = "tr";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // --- Sessiz saatler (Europe/Istanbul yerel saati): e-posta ve anlık bildirim ertelenir, düşürülmez.
    public bool QuietHoursEnabled { get; set; }
    public TimeOnly QuietStart { get; set; } = new(22, 0);
    public TimeOnly QuietEnd { get; set; } = new(8, 0);
    /// <summary>Bit maskesi: 1 &lt;&lt; DayOfWeek (Pazar = 0); pencerenin başladığı gün.</summary>
    public int QuietDays { get; set; } = 127;

    // --- Günlük özet: acil olmayan kategorilerin e-postaları tek e-postada (yalnızca konu satırları).
    public bool DigestEnabled { get; set; }
    public int DigestHour { get; set; } = 18;
    public DateTimeOffset? DigestLastSentAt { get; set; }
}

/// <summary>
/// Kişinin bir bildirim kategorisi için kanal tercihleri. Satır yoksa tüm kanallar açıktır.
/// Zorunlu (yasal) kategorilerde uygulama içi kanal kapatılamaz.
/// </summary>
public class CategoryPreference : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid EmployeeId { get; set; }
    public string Category { get; set; } = "";
    public bool InApp { get; set; } = true;
    public bool Email { get; set; } = true;
    public bool Push { get; set; } = true;
    public bool Chat { get; set; } = true;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

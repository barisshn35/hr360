namespace LearningService.Services;

/// <summary>
/// İş tarihleri (son tarih, sertifika bitişi, hatırlatma günü) Türkiye saatine göredir; sunucu UTC
/// çalışır. 00:00–03:00 arasında UTC tarihi bir gün geride kaldığından "bugün" Europe/Istanbul'a göre alınır.
/// </summary>
public static class TrTime
{
    private static readonly TimeZoneInfo Zone = Find();

    private static TimeZoneInfo Find()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"); }
        catch { return TimeZoneInfo.CreateCustomTimeZone("TRT", TimeSpan.FromHours(3), "TRT", "TRT"); }
    }

    /// <summary>Türkiye saatine göre verilen anın tarihi.</summary>
    public static DateOnly DateOf(DateTime utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
        DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone));

    /// <summary>Türkiye saatine göre bugün.</summary>
    public static DateOnly Today => DateOf(DateTime.UtcNow);
}

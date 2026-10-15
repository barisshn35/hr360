namespace GovernanceService.Infrastructure;

/// <summary>
/// Kullanıcının seçtiği gün (tarih filtresi) Türkiye saatine göredir; veritabanı UTC tutar.
/// "Bugün" filtresi 00:00–03:00 arasında yapılan işlemleri de kapsasın diye gün sınırı
/// Europe/Istanbul'a göre UTC'ye çevrilir.
/// </summary>
public static class TrTime
{
    public static readonly TimeZoneInfo Zone = Find();

    private static TimeZoneInfo Find()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"); }
        catch { return TimeZoneInfo.CreateCustomTimeZone("TRT", TimeSpan.FromHours(3), "TRT", "TRT"); }
    }

    /// <summary>Türkiye saatiyle verilen günün başlangıcı (UTC).</summary>
    public static DateTime StartOfDayUtc(DateOnly day) =>
        TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), Zone);

    /// <summary>UTC anı Türkiye saatine çevirir (metin/dosya içindeki tarihler için).</summary>
    public static DateTime ToTr(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);
}

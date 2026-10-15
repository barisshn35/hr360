namespace LeaveService.Services;

/// <summary>
/// Yasal yıllık ücretli izin süresi (4857 sayılı İş Kanunu m.53):
///   hizmet süresi 1-5 yıl (5 dahil) → en az 14 gün; 5-15 yıl → 20 gün; 15 yıl ve fazlası → 26 gün;
///   18 yaş ve altı ile 50 yaş ve üstü çalışanlara en az 20 gün.
/// Hak, işe giriş yıl dönümünde doğar. KVKK: doğum tarihi yalnızca yaş kuralı için kullanılır,
/// yanıtta gösterilmez.
/// </summary>
public static class LeaveEntitlement
{
    /// <summary>Günlük çalışma saati (saatlik izni güne çevirmek için). LEAVE_DAY_HOURS, varsayılan 7,5.</summary>
    public static decimal DayHours =>
        decimal.TryParse(Environment.GetEnvironmentVariable("LEAVE_DAY_HOURS"), System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var h) && h is > 0 and <= 12 ? h : 7.5m;

    public static DateOnly Anniversary(DateOnly hire, int year)
    {
        var y = Math.Max(year, hire.Year);
        // 29 Şubat'ta işe girenin yıl dönümü artık olmayan yılda 28 Şubat'tır.
        return hire.Month == 2 && hire.Day == 29 && !DateTime.IsLeapYear(y) ? new DateOnly(y, 2, 28) : new DateOnly(y, hire.Month, hire.Day);
    }

    public static int ServiceYears(DateOnly hire, int year) => Math.Max(0, year - hire.Year);

    public static int AgeAt(DateOnly birth, DateOnly on)
    {
        var age = on.Year - birth.Year;
        if (on < birth.AddYears(age)) age--;
        return age;
    }

    /// <summary>Verilen yılda (yıl dönümünde) doğan yasal izin günü; 1 yılı doldurmamışsa 0.</summary>
    public static (int Days, int ServiceYears, bool AgeRule) Statutory(DateOnly hire, DateOnly? birth, int year)
    {
        var years = ServiceYears(hire, year);
        if (years < 1) return (0, years, false);
        var days = years <= 5 ? 14 : years < 15 ? 20 : 26;
        var ageRule = false;
        if (birth is { } b)
        {
            var age = AgeAt(b, Anniversary(hire, year));
            if ((age <= 18 || age >= 50) && days < 20) { days = 20; ageRule = true; }
        }
        return (days, years, ageRule);
    }

    /// <summary>Saatlik izin → gün (2 ondalık).</summary>
    public static decimal HoursToDays(decimal hours) => HoursToDays(hours, DayHours);

    /// <summary>Saatlik izin → gün, şirketin günlük çalışma saatiyle (leave_settings.DayHours).</summary>
    public static decimal HoursToDays(decimal hours, decimal dayHours) =>
        Math.Round(hours / (dayHours is > 0 and <= 12 ? dayHours : 7.5m), 2, MidpointRounding.AwayFromZero);

    /// <summary>Şirket ayarı (geçerliyse) yoksa LEAVE_DAY_HOURS / 7,5.</summary>
    public static decimal EffectiveDayHours(decimal? tenant) => tenant is > 0 and <= 12 ? tenant.Value : DayHours;

    /// <summary>
    /// Hak yıl dönümünde doğar (m.53): verilen yılın yıl dönümü bugün ya da daha önceyse "tahakkuk etmiş".
    /// Önizleme bu bilgiyle İK'ya gösterilir; uygulama isteğe bağlı olarak yalnızca tahakkuk edenlere yapılır.
    /// </summary>
    public static bool Accrued(DateOnly hire, int year, DateOnly today) =>
        ServiceYears(hire, year) >= 1 && Anniversary(hire, year) <= today;

    /// <summary>
    /// İki tarih arası iş günü (bitiş dahil): hafta sonu ve tam gün resmî tatil 0, yarım gün tatil
    /// (arife, 28 Ekim) 0,5 sayılır. <paramref name="holidays"/>: tarih → yarım gün mü.
    /// </summary>
    public static decimal WorkingDays(DateOnly start, DateOnly end, IReadOnlyDictionary<DateOnly, bool> holidays)
    {
        var count = 0m;
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            if (holidays.TryGetValue(d, out var half)) { if (half) count += 0.5m; continue; }
            count++;
        }
        return count;
    }
}

/// <summary>
/// Madde 69: ekip izin çakışması (saf fonksiyon, birim testli). Ekip = talep edenin departmanı (kendisi
/// hariç). Oran = çakışan (onaylı ya da onay bekleyen izni olan) kişi / ekip büyüklüğü.
/// KVKK: talep edene yalnızca sayı gösterilir; ekip 5 kişiden küçükse sayı da gösterilmez (yalnızca eşik
/// aşıldı mı). Adlar yalnızca yöneticiye/İK'ya.
/// </summary>
public static class TeamConflict
{
    public const int MinGroupForCounts = 5;

    public sealed record Result(bool Enabled, int ThresholdPercent, bool Exceeds, int? TeamSize, int? Overlapping, int? Percent);

    public static Result Evaluate(int teamSize, int overlapping, int thresholdPercent, bool enabled, bool revealCounts)
    {
        if (!enabled || teamSize <= 0) return new Result(enabled, thresholdPercent, false, revealCounts ? teamSize : null, revealCounts ? 0 : null, revealCounts ? 0 : null);
        // Talep eden de izne çıkacağı için oran (çakışan + 1) / (ekip + 1) üzerinden: ekibin ne kadarı aynı anda yok.
        var percent = (int)Math.Round(100m * (overlapping + 1) / (teamSize + 1), MidpointRounding.AwayFromZero);
        var exceeds = overlapping > 0 && percent > thresholdPercent;
        var show = revealCounts || teamSize >= MinGroupForCounts;
        return new Result(true, thresholdPercent, exceeds, show ? teamSize : null, show ? overlapping : null, show ? percent : null);
    }
}

/// <summary>
/// İzin talebi metinleri (saf, birim testli). Sunucu metni Türkçe ve Türkçe sayı biçimiyle ("0,5") saklar;
/// İngilizce arayüz "@server:" kalıp anahtarlarıyla (en.json) çevirir. Saat kuralı arayüzdeki
/// hoursProblem (apps/web/src/lib/leaveDays.ts) ile aynıdır.
/// </summary>
public static class LeaveText
{
    private static readonly System.Globalization.CultureInfo Tr = new("tr-TR");

    /// <summary>Türkçe sayı ("0,5", "7,5", "2").</summary>
    public static string Num(decimal v) => v.ToString("0.##", Tr);

    /// <summary>İzin türünün Türkçe adı (onay başlıkları için; arayüzdeki leaveTypeLabels ile aynı, küçük harf).</summary>
    public static string TypeLabel(Models.LeaveType type) => type switch
    {
        Models.LeaveType.Annual => "yıllık izin",
        Models.LeaveType.Sick => "hastalık izni",
        Models.LeaveType.Unpaid => "ücretsiz izin",
        Models.LeaveType.Maternity => "doğum izni",
        Models.LeaveType.Paternity => "babalık izni",
        Models.LeaveType.Marriage => "evlilik izni",
        Models.LeaveType.Bereavement => "vefat izni",
        _ => "izin",
    };

    /// <summary>Onay akışı başlığı: "0,5 günlük yıllık izin talebi (28.10.2026 - 28.10.2026)" / "7,5 saatlik ... talebi (28.10.2026)".</summary>
    public static string Subject(Models.LeaveType type, decimal days, decimal? hours, DateOnly start, DateOnly end) =>
        hours is { } h
            ? $"{Num(h)} saatlik {TypeLabel(type)} talebi ({start.ToString("dd.MM.yyyy", Tr)})"
            : $"{Num(days)} günlük {TypeLabel(type)} talebi ({start.ToString("dd.MM.yyyy", Tr)} - {end.ToString("dd.MM.yyyy", Tr)})";

    /// <summary>
    /// Saatlik izin hatası (null = geçerli): 0,5 saatlik adımlar, 0'dan büyük ve en fazla günlük çalışma saati
    /// × gün payı (yarım gün tatilde 0,5). Günün tamamı (ör. 7,5 saat) 1 gün sayılır.
    /// </summary>
    public static string? HoursError(decimal hours, decimal dayHours, decimal dayCap)
    {
        if (hours <= 0 || hours > dayHours * dayCap || hours * 2 != Math.Floor(hours * 2))
            return $"Saatlik izin 0,5 saatlik adımlarla ve en fazla {Num(dayHours * dayCap)} saat olmalı";
        return null;
    }
}

/// <summary>Onaylı izin iptali kuralları (saf, birim testli).</summary>
public static class LeaveCancellation
{
    /// <summary>Türkiye saatine göre bugün (UTC+3).</summary>
    public static DateOnly TodayTr() => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));

    /// <summary>Gerekçe zorunlu: 5–500 karakter.</summary>
    public static string? ReasonError(string? reason) =>
        string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 5 ? "İptal gerekçesi en az 5 karakter olmalı"
        : reason.Trim().Length > 500 ? "İptal gerekçesi en fazla 500 karakter olabilir" : null;

    /// <summary>Yalnızca onaylı ve henüz başlamamış (başlangıcı bugünden sonra) izin iptal edilir.</summary>
    public static string? ApprovedCancelError(Models.LeaveRequestStatus status, DateOnly start, DateOnly today) =>
        status != Models.LeaveRequestStatus.Approved ? "Yalnızca onaylı izin bu yolla iptal edilir"
        : start <= today ? "Başlamış ya da geçmiş izin iptal edilemez" : null;
}

using System.Globalization;
using TimeShiftService.Models;

namespace TimeShiftService.Services;

/// <summary>Bir çalışanın tek bir vardiya aralığı (işletme saatine göre yerel zaman).</summary>
public sealed record ShiftInterval(Guid? AssignmentId, DateTime Start, DateTime End, int BreakMinutes)
{
    public int NetMinutes => Math.Max(0, (int)(End - Start).TotalMinutes - BreakMinutes);

    public static ShiftInterval Of(Guid? assignmentId, DateOnly date, TimeOnly start, TimeOnly end, int breakMinutes)
    {
        var s = date.ToDateTime(start);
        var e = date.ToDateTime(end);
        if (e <= s) e = e.AddDays(1); // gece yarısını geçen vardiya
        return new ShiftInterval(assignmentId, s, e, breakMinutes);
    }
}

/// <summary>Çalışma süresi kural değerleri (kiracı ayarı; varsayılanlar yasal değerler).</summary>
public sealed record WorkRuleSettings(decimal MinRestHours = 11, decimal WeeklyMaxHours = 45, decimal DailyMaxHours = 11,
    decimal NightMaxHours = 7.5m, int MaxConsecutiveDays = 6)
{
    public static readonly WorkRuleSettings Default = new();

    public static WorkRuleSettings From(TimesheetSettings? s) => s is null ? Default
        : new WorkRuleSettings(s.MinRestHours, s.WeeklyMaxHours, s.DailyMaxHours, s.NightMaxHours, s.MaxConsecutiveDays);
}

/// <summary>Kural uyarısı: kod (overlap, rest, weekly, daily, night, consecutive) ve Türkçe açıklama.</summary>
public sealed record RuleWarning(string Code, string Message);

/// <summary>
/// Dalga 9 / madde 66: çalışma süresi kuralları (saf fonksiyonlar, birim testli). Yalnızca DEĞİŞEN
/// vardiyalar ve bunların komşuları/haftaları denetlenir — eski bir aşım yeni atamayı işaretlemez.
///  - overlap: iki vardiya çakışıyor
///  - rest: iki vardiya arası dinlenme &lt; MinRestHours (Çalışma Süreleri Yön.: 11 saat)
///  - weekly: ISO haftası net çalışma &gt; WeeklyMaxHours (İş K. m.63: 45 saat)
///  - daily: aynı gün başlayan vardiyaların net toplamı &gt; DailyMaxHours (İş K. m.63: 11 saat)
///  - night: gece çalışması (vardiyanın en az yarısı 20:00–06:00 arasında) net &gt; NightMaxHours (m.69: 7,5 saat)
///  - consecutive: ardışık çalışma günü &gt; MaxConsecutiveDays (haftalık dinlenme günü, m.46)
/// Atamada hepsi uyarıdır (karar planlayıcınındır); takasta overlap/rest/weekly engeldir.
/// </summary>
public static class WorkRules
{
    public static readonly string[] Blocking = { "overlap", "rest", "weekly" };

    /// <summary>Vardiyanın 20:00–06:00 gece dilimine düşen dakikası.</summary>
    public static int NightMinutes(ShiftInterval s)
    {
        var total = 0;
        for (var d = s.Start.Date.AddDays(-1); d <= s.End.Date; d = d.AddDays(1))
        {
            var ns = d.AddHours(20);
            var ne = d.AddDays(1).AddHours(6);
            var a = s.Start > ns ? s.Start : ns;
            var b = s.End < ne ? s.End : ne;
            if (b > a) total += (int)(b - a).TotalMinutes;
        }
        return total;
    }

    /// <summary>Gece çalışması: vardiya süresinin en az yarısı gece dilimindeyse (Yargıtay yaklaşımı).</summary>
    public static bool IsNightWork(ShiftInterval s)
    {
        var span = (int)(s.End - s.Start).TotalMinutes;
        return span > 0 && NightMinutes(s) * 2 >= span;
    }

    private static string H(decimal h) => h.ToString("0.#", System.Globalization.CultureInfo.GetCultureInfo("tr-TR"));
    private static string H(double h) => h.ToString("0.#", System.Globalization.CultureInfo.GetCultureInfo("tr-TR"));

    public static List<RuleWarning> Check(IReadOnlyList<ShiftInterval> schedule, IReadOnlyList<ShiftInterval> changed,
        WorkRuleSettings? rules = null, string who = "Çalışan")
    {
        var r = rules ?? WorkRuleSettings.Default;
        var list = new List<RuleWarning>();
        var ordered = schedule.OrderBy(s => s.Start).ToList();
        void Add(string code, string msg) { if (!list.Any(x => x.Code == code && x.Message == msg)) list.Add(new RuleWarning(code, msg)); }

        foreach (var c in changed)
        {
            foreach (var other in ordered)
            {
                if (ReferenceEquals(other, c) || other == c) continue;
                if (other.Start < c.End && c.Start < other.End)
                {
                    Add("overlap", $"{who}: {c.Start:dd.MM HH:mm} vardiyası {other.Start:dd.MM HH:mm} vardiyasıyla çakışıyor");
                    continue;
                }
                var gap = other.Start >= c.End ? other.Start - c.End : c.Start - other.End;
                if (gap < TimeSpan.FromHours((double)r.MinRestHours))
                {
                    // Dinlenme, önceki vardiyanın BİTİŞİ ile sonrakinin BAŞLANGICI arasıdır.
                    var (prevEnd, nextStart) = other.Start >= c.End ? (c.End, other.Start) : (other.End, c.Start);
                    Add("rest", $"{who}: vardiyalar arasında en az {H(r.MinRestHours)} saat dinlenme olmalı ({prevEnd:dd.MM HH:mm} ile {nextStart:dd.MM HH:mm} arası {H(gap.TotalHours)} saat)");
                }
            }
            var sameDay = ordered.Where(s => s.Start.Date == c.Start.Date).Sum(s => s.NetMinutes);
            if (sameDay > r.DailyMaxHours * 60)
                Add("daily", $"{who}: {c.Start:dd.MM} günü çalışma {H(sameDay / 60.0)} saate çıkıyor (günlük en çok {H(r.DailyMaxHours)} saat)");
            if (IsNightWork(c) && c.NetMinutes > r.NightMaxHours * 60)
                Add("night", $"{who}: {c.Start:dd.MM HH:mm} gece vardiyası {H(c.NetMinutes / 60.0)} saat (gece çalışması en çok {H(r.NightMaxHours)} saat)");

            // Ardışık gün: değişen vardiyanın gününü içeren kesintisiz çalışma günleri dizisi.
            var days = ordered.Select(s => DateOnly.FromDateTime(s.Start)).ToHashSet();
            var day = DateOnly.FromDateTime(c.Start);
            var run = 1;
            for (var d = day.AddDays(-1); days.Contains(d); d = d.AddDays(-1)) run++;
            for (var d = day.AddDays(1); days.Contains(d); d = d.AddDays(1)) run++;
            if (run > r.MaxConsecutiveDays)
                Add("consecutive", $"{who}: {run} gün üst üste çalışma (en çok {r.MaxConsecutiveDays} gün; haftada bir gün dinlenme)");
        }
        foreach (var wk in changed.Select(c => SwapRules.IsoWeek(c.Start)).Distinct())
        {
            var minutes = ordered.Where(s => SwapRules.IsoWeek(s.Start) == wk).Sum(s => s.NetMinutes);
            if (minutes > r.WeeklyMaxHours * 60)
                Add("weekly", $"{who}: {wk.Week}. haftada çalışma {H(minutes / 60.0)} saate çıkıyor (haftalık en çok {H(r.WeeklyMaxHours)} saat)");
        }
        return list;
    }
}

/// <summary>
/// G6 takas kuralları: <see cref="WorkRules"/> içindeki engelleyici kurallar (çakışma, dinlenme, haftalık
/// süre). Günlük/gece/ardışık gün aşımları onaycıya uyarı olarak gösterilir (ShiftSwapsController).
/// </summary>
public static class SwapRules
{
    /// <summary>İki vardiya arası en az dinlenme (saat), varsayılan. Çalışma Süreleri Yönetmeliği: 11 saat.</summary>
    public const int MinRestHours = 11;
    /// <summary>Haftalık en çok çalışma (saat), varsayılan. 4857 s. İş Kanunu m.63: 45 saat.</summary>
    public const int WeeklyMaxHours = 45;

    public static (int Year, int Week) IsoWeek(DateTime d) => (ISOWeek.GetYear(d), ISOWeek.GetWeekOfYear(d));

    /// <summary>
    /// Takastan sonraki program için ilk engelleyici kural ihlalinin Türkçe açıklaması; uygunsa null.
    /// <paramref name="schedule"/> çalışanın takas SONRASI tüm vardiyaları (pencere içinde),
    /// <paramref name="changed"/> bunlardan takasla yeni gelenler.
    /// </summary>
    public static string? Validate(IReadOnlyList<ShiftInterval> schedule, IReadOnlyList<ShiftInterval> changed, string who = "Çalışan",
        WorkRuleSettings? rules = null)
    {
        var w = WorkRules.Check(schedule, changed, rules, who);
        foreach (var code in WorkRules.Blocking)
            if (w.FirstOrDefault(x => x.Code == code) is { } hit) return hit.Message;
        return null;
    }
}

/// <summary>G6 tercih uyuşmazlıkları (uyarı niteliğinde; atamayı engellemez).</summary>
public static class PreferenceRules
{
    public sealed record Conflict(string Code, string Message);

    public static string ShiftType(TimeOnly start, TimeOnly end, bool isNight) =>
        isNight || end <= start || start >= new TimeOnly(20, 0) || start < new TimeOnly(5, 0) ? "Night" : "Day";

    public static int IsoDay(DateOnly d) => d.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)d.DayOfWeek;

    private static readonly string[] DayNames = { "", "Pazartesi", "Salı", "Çarşamba", "Perşembe", "Cuma", "Cumartesi", "Pazar" };

    /// <param name="nightsThisWeek">Bu atama HARİÇ, aynı ISO haftasındaki gece vardiyası sayısı.</param>
    public static List<Conflict> Check(ShiftPreference? p, DateOnly date, TimeOnly start, TimeOnly end, bool isNight, int nightsThisWeek)
    {
        var list = new List<Conflict>();
        if (p is null) return list;
        var day = IsoDay(date);
        var type = ShiftType(start, end, isNight);
        if (p.UnavailableDays.Contains(day))
            list.Add(new("unavailable_day", $"Çalışan {DayNames[day]} günleri müsait olmadığını belirtmiş"));
        else if (p.PreferredDays.Length > 0 && !p.PreferredDays.Contains(day))
            list.Add(new("not_preferred_day", $"{DayNames[day]} tercih edilen günler arasında değil"));
        if (p.AvoidShiftTypes.Contains(type))
            list.Add(new("avoided_type", type == "Night" ? "Çalışan gece vardiyası istememiş" : "Çalışan gündüz vardiyası istememiş"));
        else if (p.PreferredShiftTypes.Length > 0 && !p.PreferredShiftTypes.Contains(type))
            list.Add(new("not_preferred_type", type == "Night" ? "Gece vardiyası tercih edilenler arasında değil" : "Gündüz vardiyası tercih edilenler arasında değil"));
        if (type == "Night" && p.MaxNightsPerWeek is { } max && nightsThisWeek + 1 > max)
            list.Add(new("max_nights", $"Haftalık gece vardiyası tercihi aşılıyor ({nightsThisWeek + 1}/{max})"));
        return list;
    }
}

/// <summary>Günlük puantaj sonucu (dakika).</summary>
public sealed record AttendanceDay(
    DateTime? FirstIn, DateTime? LastOut, int LateMinutes, int EarlyLeaveMinutes, int WorkedMinutes,
    int ExpectedMinutes, int OvertimeMinutes, string Status)
{
    /// <summary>Fazla mesai talebi önerisi: yarım saate aşağı yuvarlanır, günde en çok 4 saat.</summary>
    public decimal SuggestedOvertimeHours => Math.Min(4m, Math.Floor(OvertimeMinutes / 30m) * 0.5m);
}

/// <summary>
/// G7: giriş-çıkış hareketleri ile planlanan vardiyanın karşılaştırması (saf fonksiyon, birim testli).
/// Bilgilendirme amaçlıdır; otomatik yaptırım yoktur.
///  - Geç kalma: ilk giriş planlanan başlangıçtan tolerans dakikasından fazla sonraysa tüm gecikme.
///  - Erken çıkış: son çıkış planlanan bitişten tolerans dakikasından fazla önceyse.
///  - Çalışılan süre: ardışık giriş→çıkış çiftleri toplamı; tek çift varsa (mola okutulmamış)
///    planlanan mola düşülür.
///  - Olası fazla mesai: çalışılan − beklenen (planlanan − mola); plan yoksa (hafta tatili) tamamı.
/// </summary>
public static class AttendanceCalc
{
    public static AttendanceDay Compute(DateTime? plannedStart, DateTime? plannedEnd, int breakMinutes,
        IReadOnlyList<(bool In, DateTime At)> punches, int graceMinutes)
    {
        var ordered = punches.OrderBy(p => p.At).ToList();
        var expected = plannedStart is { } ps && plannedEnd is { } pe ? Math.Max(0, (int)(pe - ps).TotalMinutes - breakMinutes) : 0;
        if (ordered.Count == 0)
            return new AttendanceDay(null, null, 0, 0, 0, expected, 0, plannedStart is null ? "Off" : "Absent");

        // Çiftleme: her girişten sonraki ilk çıkış; eşsiz giriş "çıkış eksik".
        var worked = 0;
        var pairs = 0;
        DateTime? openIn = null;
        var missingOut = false;
        foreach (var p in ordered)
        {
            if (p.In)
            {
                if (openIn is not null) missingOut = true; // iki giriş arka arkaya
                openIn = p.At;
            }
            else if (openIn is { } i)
            {
                worked += (int)(p.At - i).TotalMinutes;
                pairs++;
                openIn = null;
            }
        }
        if (openIn is not null) missingOut = true;
        if (pairs == 1 && plannedStart is not null && worked > breakMinutes * 2) worked -= breakMinutes;

        var firstIn = ordered.Where(p => p.In).Select(p => (DateTime?)p.At).FirstOrDefault();
        var lastOut = ordered.Where(p => !p.In).Select(p => (DateTime?)p.At).LastOrDefault();
        var late = 0;
        var early = 0;
        if (plannedStart is { } st && firstIn is { } fi)
        {
            var d = (int)Math.Floor((fi - st).TotalMinutes);
            if (d > graceMinutes) late = d;
        }
        if (plannedEnd is { } en && lastOut is { } lo && !missingOut)
        {
            var d = (int)Math.Floor((en - lo).TotalMinutes);
            if (d > graceMinutes) early = d;
        }
        var overtime = plannedStart is null ? worked : Math.Max(0, worked - expected);
        var status = missingOut ? "MissingOut" : late > 0 ? "Late" : early > 0 ? "EarlyLeave" : plannedStart is null ? "OffDayWork" : "Ok";
        return new AttendanceDay(firstIn, lastOut, late, early, worked, expected, overtime, status);
    }
}

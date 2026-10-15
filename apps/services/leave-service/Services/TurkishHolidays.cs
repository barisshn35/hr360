namespace LeaveService.Services;

/// <summary>
/// Türkiye resmî tatilleri (2429 sayılı Ulusal Bayram ve Genel Tatiller Hakkında Kanun). Saf; birim testli.
///  - Sabit tarihliler her yıl aynıdır; 28 Ekim (Cumhuriyet Bayramı arifesi) öğleden sonra yarım gündür.
///  - Ramazan (3 gün) ve Kurban (4 gün) bayramları hicri takvime göre her yıl değişir; arifeleri öğleden
///    sonra yarım gün tatildir. Tarihler çalışma zamanında İNTERNETTEN ALINMAZ: Diyanet İşleri Başkanlığı
///    "Dini Günler" takviminden (vakithesaplama.diyanet.gov.tr, 2025–2030 yılları; 2028–2030 sayfaları
///    icerik=185/186/187, 2027 icerik=154) elle aktarılmıştır. Diyanet ilanı değişirse bu tablo güncellenir;
///    tablo dışındaki yıllarda İK dini bayramları elle girer.
/// Aynı güne iki tatil denk gelirse tek kayıt olur (adlar birleşir); tam gün tatil yarım günü örter.
/// </summary>
public static class TurkishHolidays
{
    public sealed record Day(DateOnly Date, string Name, bool IsHalfDay);

    private static readonly (int Month, int Day, string Name, bool Half)[] Fixed =
    {
        (1, 1, "Yılbaşı", false),
        (4, 23, "Ulusal Egemenlik ve Çocuk Bayramı", false),
        (5, 1, "Emek ve Dayanışma Günü", false),
        (5, 19, "Atatürk'ü Anma, Gençlik ve Spor Bayramı", false),
        (7, 15, "Demokrasi ve Millî Birlik Günü", false),
        (8, 30, "Zafer Bayramı", false),
        (10, 28, "Cumhuriyet Bayramı arifesi (yarım gün)", true),
        (10, 29, "Cumhuriyet Bayramı", false),
    };

    /// <summary>Yıl → (Ramazan Bayramı arifesi, Kurban Bayramı arifesi). Bayram günleri arifeyi izler (3 ve 4 gün).</summary>
    private static readonly Dictionary<int, (string RamazanEve, string KurbanEve)> Religious = new()
    {
        [2025] = ("2025-03-29", "2025-06-05"),
        [2026] = ("2026-03-19", "2026-05-26"),
        [2027] = ("2027-03-08", "2027-05-15"),
        [2028] = ("2028-02-25", "2028-05-04"),
        [2029] = ("2029-02-13", "2029-04-23"),
        [2030] = ("2030-02-03", "2030-04-12"),
    };

    public static bool HasReligious(int year) => Religious.ContainsKey(year);

    public static IReadOnlyCollection<int> ReligiousYears => Religious.Keys;

    /// <summary>Verilen yılın tatilleri (tarih sırasıyla). <paramref name="includeHalfDays"/> false ise arifeler atlanır.</summary>
    public static List<Day> For(int year, bool includeHalfDays = true)
    {
        var all = new List<Day>();
        foreach (var (m, d, name, half) in Fixed)
            all.Add(new Day(new DateOnly(year, m, d), name, half));
        if (Religious.TryGetValue(year, out var r))
        {
            var re = DateOnly.Parse(r.RamazanEve);
            all.Add(new Day(re, "Ramazan Bayramı arifesi (yarım gün)", true));
            for (var i = 1; i <= 3; i++) all.Add(new Day(re.AddDays(i), $"Ramazan Bayramı ({i}. gün)", false));
            var ke = DateOnly.Parse(r.KurbanEve);
            all.Add(new Day(ke, "Kurban Bayramı arifesi (yarım gün)", true));
            for (var i = 1; i <= 4; i++) all.Add(new Day(ke.AddDays(i), $"Kurban Bayramı ({i}. gün)", false));
        }
        return all.Where(d => includeHalfDays || !d.IsHalfDay)
            .GroupBy(d => d.Date)
            .Select(g => new Day(g.Key, string.Join(" / ", g.OrderBy(x => x.IsHalfDay).Select(x => x.Name)), g.All(x => x.IsHalfDay)))
            .OrderBy(d => d.Date).ToList();
    }
}

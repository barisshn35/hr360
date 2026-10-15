using System.Globalization;

namespace EngagementService.Infrastructure;

/// <summary>
/// G18: anket serbest metinleri için YEREL, sözlük tabanlı Türkçe duygu analizi. Metin hiçbir
/// dış servise gönderilmez (KVKK m.9 yurt dışı aktarım yok, model yok). Kasıtlı olarak basittir:
///  - sözcük kökleri önek eşleşmesiyle bulunur (Türkçe eklemeli: "memnunum", "memnuniyet");
///  - olumsuzluk naif biçimde ele alınır: ardından gelen "değil", olumlu sözcükten sonra "yok",
///    "olmadı/olmuyor/olmaz"; fiil köklerinde "-me/-ma" eki ("sevmiyorum", "beğenmedim");
///    ad/sıfatlarda "-sız/-siz/-suz/-süz" eki ("desteksiz", "huzursuz");
///  - puan = olumlu − olumsuz isabet; &gt;0 olumlu, &lt;0 olumsuz, 0 nötr.
/// Bireysel sonuç dışarıya verilmez; yalnızca toplu sayımlar (en az 5 yanıtta) gösterilir.
/// </summary>
public static class TurkishSentiment
{
    public enum Label { Positive, Negative, Neutral }

    public sealed record Result(Label Label, int Score);

    public sealed record Keyword(string Word, int Count);

    public sealed record Summary(int Positive, int Negative, int Neutral, IReadOnlyList<Keyword> TopKeywords);

    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    // (kök, kutup, fiil mi). Fiil köklerinde "-me/-ma" olumsuzluğu aranır.
    private static readonly (string Stem, int Polarity, bool Verb)[] Lexicon =
    {
        // olumlu
        ("iyi", 1, false), ("güzel", 1, false), ("harika", 1, false), ("mükemmel", 1, false), ("süper", 1, false),
        ("memnun", 1, false), ("mutlu", 1, false), ("başarı", 1, false), ("destek", 1, false), ("teşekkür", 1, false),
        ("keyif", 1, false), ("olumlu", 1, false), ("verimli", 1, false), ("huzur", 1, false), ("adil", 1, false),
        ("esnek", 1, false), ("takdir", 1, false), ("gelişim", 1, false), ("kolay", 1, false), ("şeffaf", 1, false),
        ("motive", 1, false), ("motivasyon", 1, false), ("samimi", 1, false), ("saygı", 1, false), ("uyumlu", 1, false),
        ("rahat", 1, false), ("fırsat", 1, false), ("eğlenceli", 1, false), ("yardımsever", 1, false), ("profesyonel", 1, false),
        ("muhteşem", 1, false), ("başarılı", 1, false), ("sevgi", 1, false), ("hoşnut", 1, false),
        ("sev", 1, true), ("beğen", 1, true), ("öner", 1, true), ("güven", 1, true), ("destekle", 1, true), ("öğren", 1, true),
        // olumsuz
        ("kötü", -1, false), ("berbat", -1, false), ("yorgun", -1, false), ("yorucu", -1, false), ("stres", -1, false),
        ("baskı", -1, false), ("yoğun", -1, false), ("düşük", -1, false), ("adaletsiz", -1, false), ("sorun", -1, false),
        ("problem", -1, false), ("şikayet", -1, false), ("şikâyet", -1, false), ("mutsuz", -1, false), ("yetersiz", -1, false),
        ("zor", -1, false), ("belirsiz", -1, false), ("kaos", -1, false), ("karmaşa", -1, false), ("toksik", -1, false),
        ("eksik", -1, false), ("gergin", -1, false), ("tüken", -1, false), ("haksız", -1, false), ("ilgisiz", -1, false),
        ("dağınık", -1, false), ("yavaş", -1, false), ("sıkıcı", -1, false), ("endişe", -1, false), ("kaygı", -1, false),
        ("korku", -1, false), ("mobbing", -1, false), ("bıkkın", -1, false), ("rezalet", -1, false), ("vasat", -1, false),
        ("zorlan", -1, true), ("yorul", -1, true), ("sıkıl", -1, true), ("bık", -1, true), ("üzül", -1, true),
    };

    /// <summary>Yanlış önek eşleşmesi veren sözcükler (ör. "seviye" ≠ "sev", "zorunlu" ≠ "zor").</summary>
    private static readonly string[] Exclusions =
    {
        "seviye", "sevk", "zorunlu", "iyileş", "destekçi", "öneri", "önerge", "güvenlik", "bıçak", "yoğunlaş", "problemsiz", "sorunsuz",
        "stressiz", "endişesiz", "kaygısız", "öğrenci",
    };

    /// <summary>Kendi başına olumlu olan "-sız" biçimleri (olumsuzun olumsuzu).</summary>
    private static readonly string[] PositiveCompounds = { "sorunsuz", "problemsiz", "stressiz", "endişesiz", "kaygısız" };

    /// <summary>Tam eşleşme isteyen kısa sözcükler.</summary>
    private static readonly Dictionary<string, int> ShortWords = new() { ["az"] = -1, ["hoş"] = 1 };

    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "için", "ama", "fakat", "ancak", "çok", "daha", "gibi", "olan", "olarak", "bir", "biraz", "bu", "şu", "o", "ve", "veya", "ile",
        "de", "da", "ki", "her", "hiç", "bence", "olsun", "olması", "olmalı", "var", "yok", "değil", "şey", "şeyler", "kadar", "sonra",
        "önce", "bizim", "benim", "bize", "bana", "beni", "bizi", "işte", "iş", "en", "ne", "neden", "nasıl", "mı", "mi", "mu", "mü",
        "çünkü", "eğer", "şimdi", "zaman", "hep", "bazı", "bazen", "artık", "tüm", "bütün", "diye", "olur", "oldu", "olduğu", "olduğunu",
        "ise", "yani", "sadece", "hem", "şirket", "şirkette", "şirketin", "burada", "burası", "konusunda", "ilgili", "yeterince",
    };

    private static readonly string[] Negators = { "değil" };
    private static readonly string[] CopulaNegators = { "olmuyor", "olmadı", "olmaz", "olamıyor", "olamadı", "olmayan" };

    public static string Lower(string s) => s.ToLower(Tr);

    public static List<string> Tokenize(string text)
    {
        var list = new List<string>();
        var sb = new System.Text.StringBuilder();
        foreach (var c in Lower(text ?? ""))
        {
            if (char.IsLetter(c)) { sb.Append(c); continue; }
            if (c is '\'' or '’') continue; // "Ayşe'nin" → "ayşenin" (kesme işareti sözcüğü bölmez)
            if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); }
        }
        if (sb.Length > 0) list.Add(sb.ToString());
        return list;
    }

    private static bool StartsWithAny(string token, IEnumerable<string> list) => list.Any(token.StartsWith);

    /// <summary>Sözcüğün kutbu (+1/−1/0), ek olumsuzluğu dahil; sözcük dışı olumsuzluk ayrıca uygulanır.</summary>
    private static int WordPolarity(string token)
    {
        if (ShortWords.TryGetValue(token, out var sp)) return sp;
        if (PositiveCompounds.Any(token.StartsWith)) return 1;
        if (StartsWithAny(token, Exclusions)) return 0;
        (string Stem, int Polarity, bool Verb)? best = null;
        foreach (var e in Lexicon)
            if (token.StartsWith(e.Stem, StringComparison.Ordinal) && (best is null || e.Stem.Length > best.Value.Stem.Length))
                best = e;
        if (best is not { } hit) return 0;
        var suffix = token[hit.Stem.Length..];
        var negated = suffix.StartsWith("sız") || suffix.StartsWith("siz") || suffix.StartsWith("suz") || suffix.StartsWith("süz");
        if (hit.Verb && !negated)
        {
            // "-me/-ma" olumsuzluğu: sev-mi-yor, beğen-me-dim, sev-mem; yeterlik olumsuzu "-eme/-ama":
            // öğren-emi-yor. Mastar (-mek/-mak) olumsuz değildir.
            if (suffix.Length >= 2 && suffix[0] == 'm' && "aeıiuü".Contains(suffix[1])
                && !suffix.StartsWith("mek", StringComparison.Ordinal) && !suffix.StartsWith("mak", StringComparison.Ordinal))
                negated = true;
            else if (suffix.StartsWith("eme", StringComparison.Ordinal) || suffix.StartsWith("ama", StringComparison.Ordinal)
                     || suffix.StartsWith("emi", StringComparison.Ordinal) || suffix.StartsWith("amı", StringComparison.Ordinal))
                negated = true;
        }
        return negated ? -hit.Polarity : hit.Polarity;
    }

    public static Result Analyze(string text)
    {
        var tokens = Tokenize(text);
        var score = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            var p = WordPolarity(tokens[i]);
            if (p == 0) continue;
            // Sözcük dışı olumsuzluk: sonraki 1-2 sözcükte "değil", "olmuyor"; olumlu sözcükten hemen sonra "yok".
            var next = i + 1 < tokens.Count ? tokens[i + 1] : "";
            var next2 = i + 2 < tokens.Count ? tokens[i + 2] : "";
            var flip = StartsWithAny(next, Negators) || StartsWithAny(next, CopulaNegators)
                       || (next is "hiç" or "pek" && (StartsWithAny(next2, Negators) || StartsWithAny(next2, CopulaNegators)))
                       || (p > 0 && next == "yok");
            score += flip ? -p : p;
        }
        return new Result(score > 0 ? Label.Positive : score < 0 ? Label.Negative : Label.Neutral, score);
    }

    /// <summary>
    /// Toplu özet: olumlu/olumsuz/nötr sayıları ve en sık anahtar sözcükler. Bir sözcük ancak en az
    /// <paramref name="minDocs"/> farklı yanıtta geçiyorsa listelenir (tek kişiye özgü ifadeler gösterilmez).
    /// </summary>
    public static Summary Summarize(IEnumerable<string> texts, int topN = 10, int minDocs = 2)
    {
        int pos = 0, neg = 0, neu = 0;
        var df = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in texts)
        {
            switch (Analyze(t).Label)
            {
                case Label.Positive: pos++; break;
                case Label.Negative: neg++; break;
                default: neu++; break;
            }
            foreach (var w in Tokenize(t).Where(w => w.Length >= 4 && !Stopwords.Contains(w)).Distinct())
                df[w] = df.GetValueOrDefault(w) + 1;
        }
        var top = df.Where(kv => kv.Value >= minDocs)
            .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(topN).Select(kv => new Keyword(kv.Key, kv.Value)).ToList();
        return new Summary(pos, neg, neu, top);
    }
}

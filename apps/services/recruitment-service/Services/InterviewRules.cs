using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RecruitmentService.Models;

namespace RecruitmentService.Services;

/// <summary>Y17: puan kartı ağırlıklı puanı ve mülakat planlama kuralları (saf mantık, birim testli).</summary>
public static class ScorecardRules
{
    public const int MinScore = 1, MaxScore = 5, MinWeight = 1, MaxWeight = 5, MaxCriteria = 15;

    /// <summary>
    /// Dalga 11 (77) kör değerlendirme kuralı (GET /interviews/{id}/scorecards ile aynı): çağıran mülakatın
    /// panelindeyse ve panelin tamamı puan kartını göndermediyse başkalarının puanları ve ortalama gizlidir.
    /// Panel dışındaki yönetici/İK için gizlilik yoktur.
    /// </summary>
    public static bool IsBlind(IReadOnlyCollection<Guid> panel, IReadOnlySet<Guid> submitted, Guid? me) =>
        me is { } m && panel.Contains(m) && !(panel.Count > 0 && panel.All(submitted.Contains));

    /// <summary>Varsayılan şablon: ilan için tanım yapılmadıysa.</summary>
    public static List<ScorecardCriterion> DefaultCriteria() => new()
    {
        new("technical", "Teknik yetkinlik", 3),
        new("problem", "Problem çözme", 2),
        new("communication", "İletişim", 2),
        new("teamwork", "Takım çalışması", 1),
        new("motivation", "Pozisyona ilgi ve motivasyon", 1),
    };

    /// <summary>Şablonu doğrular; hata metni ya da null.</summary>
    public static string? Validate(IReadOnlyList<ScorecardCriterion> criteria)
    {
        if (criteria.Count is 0 or > MaxCriteria) return $"1-{MaxCriteria} ölçüt tanımlayın";
        if (criteria.Any(c => string.IsNullOrWhiteSpace(c.Key) || string.IsNullOrWhiteSpace(c.Label) || c.Label.Length > 120 || c.Key.Length > 40))
            return "Her ölçütün anahtarı ve adı olmalı (ad en fazla 120 karakter)";
        if (criteria.Select(c => c.Key.Trim().ToLowerInvariant()).Distinct().Count() != criteria.Count)
            return "Ölçüt anahtarları tekrar edemez";
        if (criteria.Any(c => c.Weight is < MinWeight or > MaxWeight)) return $"Ağırlıklar {MinWeight}-{MaxWeight} arasında olmalı";
        return null;
    }

    /// <summary>
    /// Ağırlıklı ortalama (1-5): Σ(ağırlık × puan) / Σ(ağırlık). Puanı verilmeyen ölçüt hesaba
    /// katılmaz; hiç puan yoksa null. Şablonda olmayan ya da 1-5 dışı puan hata metni döner.
    /// </summary>
    public static (decimal? Overall, string? Error) Weighted(IReadOnlyList<ScorecardCriterion> criteria, IReadOnlyList<CriterionScore> scores)
    {
        var byKey = criteria.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);
        decimal sum = 0, weights = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in scores)
        {
            if (!byKey.TryGetValue(s.Key, out var c)) return (null, $"Şablonda olmayan ölçüt: {s.Key}");
            if (!seen.Add(s.Key)) return (null, $"Ölçüt iki kez puanlanmış: {s.Key}");
            if (s.Score is < MinScore or > MaxScore) return (null, $"Puanlar {MinScore}-{MaxScore} arasında olmalı");
            sum += c.Weight * s.Score;
            weights += c.Weight;
        }
        if (weights == 0) return (null, null);
        return (Math.Round(sum / weights, 2, MidpointRounding.AwayFromZero), null);
    }

    /// <summary>İki zaman aralığı çakışıyor mu? (uç uca değmek çakışma değildir)</summary>
    public static bool Overlaps(DateTimeOffset aStart, int aMinutes, DateTimeOffset bStart, int bMinutes) =>
        aStart < bStart.AddMinutes(bMinutes) && bStart < aStart.AddMinutes(aMinutes);
}

/// <summary>
/// Y17 / KVKK m.6: mülakat notlarında özel nitelikli ya da işe alımla ilgisiz kişisel veri
/// (sağlık, hamilelik, din, siyasi görüş, sendika, etnik köken, engellilik, medeni hal, çocuk,
/// yaş…) yazılmasına karşı UYARI üretir — kaydı engellemez. Metin Türkçe küçük harfe çevrilip
/// harfleri katlanır (ğ→g, ş→s…), kalıplar sözcük sınırıyla aranır ("din" ≠ "dinamik").
/// </summary>
public static class SensitiveNoteDetector
{
    public sealed record Warning(string Category, string Term, string Message);

    private const string B = "(?<![a-z0-9])", E = "(?![a-z0-9])";

    private static readonly (string Category, Regex Pattern)[] Rules = new (string, string)[]
    {
        ("Sağlık", $@"{B}(saglik\w*|hastalig\w*|hastalik\w*|hastalan\w*|ilac\w*|tedavi\w*|ameliyat\w*|teshis\w*|psikiyatri\w*|psikolojik sorun\w*|depresyon\w*|kronik|kanser\w*|diyabet\w*|engel durumu){E}"),
        ("Hamilelik", $@"{B}(hamile\w*|gebe|gebelik\w*|gebeli\w*|emzir\w*|dogum izn\w*|bebek bekl\w*|cocuk planla\w*){E}"),
        ("Din / inanç", $@"{B}(din|dini|dinin|dine|dinde|dinden|dindar\w*|inanc\w*|mezhep\w*|mezheb\w*|alevi\w*|sunni\w*|namaz\w*|oruc\w*|basortu\w*|turban\w*|ibadet\w*|ateist\w*){E}"),
        ("Siyasi görüş", $@"{B}(siyasi\w*|siyaset\w*|parti|partisi\w*|partili\w*|partiye|partiden|oy verd\w*|secmen\w*){E}"),
        ("Sendika", $@"{B}(sendika\w*){E}"),
        ("Etnik köken / ırk", $@"{B}(etnik\w*|irk|irki|irkini|irkc\w*|kurt|kurtce|kurdu|milliyet\w*|soy kutuk\w*){E}"),
        ("Engellilik", $@"{B}(engelli\w*|malul\w*|ozurlu\w*|sakat\w*){E}"),
        ("Medeni hal", $@"{B}(medeni hal\w*|medeni durum\w*|evli|evlilik\w*|evlen\w*|bekar\w*|bosan\w*|nisanli\w*|esi|esinin|dul){E}"),
        ("Çocuk / aile planı", $@"{B}(cocu\w*){E}"),
        ("Yaş", $@"{B}(yas|yasi|yasinda\w*|yasindaki\w*|yasli\w*|yasca|\d{{1,2}} yas\w*){E}"),
        ("Cinsel yönelim", $@"{B}(cinsel yonelim\w*|escinsel\w*|lgbt\w*|cinsel tercih\w*){E}"),
        ("Ceza mahkûmiyeti", $@"{B}(sabika\w*|adli sicil\w*|mahkumiyet\w*|hapis\w*){E}"),
        ("Kılık kıyafet", $@"{B}(kilik kiyafet\w*|tesettur\w*){E}"),
    }.Select(r => (r.Item1, new Regex(r.Item2, RegexOptions.CultureInvariant | RegexOptions.Compiled))).ToArray();

    public static string Fold(string text)
    {
        var lower = text.ToLower(new CultureInfo("tr-TR"));
        var sb = new StringBuilder(lower.Length);
        foreach (var ch in lower)
            sb.Append(ch switch
            {
                'ı' => 'i',
                'ğ' => 'g',
                'ü' => 'u',
                'ş' => 's',
                'ö' => 'o',
                'ç' => 'c',
                'â' => 'a',
                'î' => 'i',
                'û' => 'u',
                '\'' or '’' => ' ',
                _ => ch,
            });
        return Regex.Replace(sb.ToString(), @"\s+", " ");
    }

    /// <summary>Kategori başına en fazla bir uyarı (ilk eşleşen ifadeyle).</summary>
    public static List<Warning> Detect(string? text)
    {
        var list = new List<Warning>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        var folded = Fold(text);
        foreach (var (category, pattern) in Rules)
        {
            var m = pattern.Match(folded);
            if (!m.Success) continue;
            list.Add(new Warning(category, m.Value,
                $"Notta \"{category}\" ile ilgili ifade var (\"{m.Value}\"). Özel nitelikli ya da işe alımla ilgisiz kişisel veri yazmayın (KVKK m.6); değerlendirmeyi işle ilgili yetkinliklere dayandırın."));
        }
        return list;
    }
}

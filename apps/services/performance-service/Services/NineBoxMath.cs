using System.Text.Json;
using System.Text.Json.Serialization;

namespace PerformanceService.Services;

/// <summary>
/// G12 9-kutu eşlemesi — saf hesap (birim testli). Performans ekseni nihai puandan (0-100), puanlama
/// ayarındaki gelişim/takdir eşikleriyle 3 banda; potansiyel ekseni yöneticinin 1-3 değerlendirmesi.
/// Hücre numarası: (potansiyel − 1) × 3 + performans → 1 (sol alt) … 9 (sağ üst).
/// KVKK: tablo bir tartışma aracıdır; otomatik karar üretmez.
/// </summary>
public static class NineBoxMath
{
    public static int PerformanceBand(decimal score, decimal lowThreshold, decimal highThreshold)
    {
        if (score < lowThreshold) return 1;
        if (score >= highThreshold) return 3;
        return 2;
    }

    public static int Cell(int performanceBand, int potentialBand)
    {
        if (performanceBand is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(performanceBand));
        if (potentialBand is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(potentialBand));
        return (potentialBand - 1) * 3 + performanceBand;
    }

    public static (int Performance, int Potential) Bands(int cell) => ((cell - 1) % 3 + 1, (cell - 1) / 3 + 1);

    /// <summary>Damgalayıcı olmayan, gelişim odaklı hücre adları.</summary>
    public static string Label(int cell) => cell switch
    {
        1 => "Gelişim desteği",
        2 => "İstikrarlı katkı",
        3 => "Güvenilir uzman",
        4 => "Yükselen performans",
        5 => "Temel güç",
        6 => "Güçlü performans",
        7 => "Keşfedilmeyi bekleyen potansiyel",
        8 => "Yüksek potansiyel",
        9 => "Gelecek vaat eden lider",
        _ => "",
    };
}

/// <summary>Dönem şablonu yapılandırması (bölümler/sorular, ağırlıklar, ölçek).</summary>
public sealed class CycleConfig
{
    [JsonPropertyName("scale")] public ScaleConfig Scale { get; set; } = new();
    [JsonPropertyName("sections")] public List<SectionConfig> Sections { get; set; } = new();
    [JsonPropertyName("goalWeightPercent")] public int? GoalWeightPercent { get; set; }

    public sealed class ScaleConfig
    {
        [JsonPropertyName("min")] public int Min { get; set; } = 1;
        [JsonPropertyName("max")] public int Max { get; set; } = 5;
        [JsonPropertyName("labels")] public List<string>? Labels { get; set; }
    }

    public sealed class SectionConfig
    {
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("weight")] public int Weight { get; set; }
        [JsonPropertyName("questions")] public List<QuestionConfig> Questions { get; set; } = new();
    }

    public sealed class QuestionConfig
    {
        [JsonPropertyName("text")] public string Text { get; set; } = "";
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static CycleConfig? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<CycleConfig>(json, Json); }
        catch (JsonException) { return null; }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Geçerliyse null, değilse Türkçe hata.</summary>
    public string? Validate()
    {
        if (Scale.Min < 0 || Scale.Max > 10 || Scale.Min >= Scale.Max) return "Ölçek 0-10 aralığında ve en küçük < en büyük olmalı";
        if (Scale.Labels is { } l && (l.Count > Scale.Max - Scale.Min + 1 || l.Any(x => x is null || x.Length > 60)))
            return "Ölçek etiketleri ölçek adım sayısını aşamaz (en fazla 60 karakter)";
        if (GoalWeightPercent is < 0 or > 100) return "Hedef ağırlığı %0-100 olmalı";
        if (Sections.Count > 20) return "En fazla 20 bölüm olabilir";
        if (Sections.Count > 0)
        {
            if (Sections.Any(s => string.IsNullOrWhiteSpace(s.Title) || s.Title.Length > 120)) return "Her bölümün başlığı olmalı (en fazla 120 karakter)";
            if (Sections.Any(s => s.Weight is < 0 or > 100)) return "Bölüm ağırlıkları %0-100 olmalı";
            if (Sections.Sum(s => s.Weight) != 100) return $"Bölüm ağırlıklarının toplamı 100 olmalı (şu an {Sections.Sum(s => s.Weight)})";
            if (Sections.Any(s => s.Questions.Count is 0 or > 50)) return "Her bölümde 1-50 soru olmalı";
            if (Sections.SelectMany(s => s.Questions).Any(q => string.IsNullOrWhiteSpace(q.Text) || q.Text.Length > 500))
                return "Soru metni boş olamaz (en fazla 500 karakter)";
        }
        return null;
    }
}

using System.Text.RegularExpressions;
using PerformanceService.Models;

namespace PerformanceService.Services;

/// <summary>
/// Dalga 11 / 81: anonim 360 geri bildirim kuralları — saf hesap (birim testli).
///  * En az 5 değerlendiren seçilir (5'ten azı hiçbir zaman gösterilemez).
///  * Sonuçlar yalnızca talep KAPANDIKTAN sonra ve en az MinResponses (≥5) yanıt varsa gösterilir:
///    açıkken gösterilseydi her yeni yanıtın etkisi önceki görünümle karşılaştırılıp kişiye bağlanabilirdi.
///  * Yetkinlik başına da en az 5 puan gerekir ("fikrim yok" seçilebildiği için sayılar farklı olabilir).
///  * Yorumlar metne göre sıralanır (yanıt sırası gösterilmez).
/// </summary>
public static class F360Rules
{
    public const int MinGroup = 5;
    public const int MaxReviewers = 40;
    public static readonly string[] Relationships = { "Manager", "Peer", "DirectReport", "Other" };
    private static readonly Regex KeyRx = new("^[a-z0-9_-]{1,40}$", RegexOptions.Compiled);

    public static List<F360Competency> DefaultCompetencies() => new()
    {
        new("communication", "İletişim"),
        new("collaboration", "İş birliği"),
        new("ownership", "Sorumluluk alma"),
        new("problem-solving", "Problem çözme"),
        new("growth", "Gelişime açıklık"),
    };

    public static string? ValidateCompetencies(IReadOnlyList<F360Competency> list)
    {
        if (list.Count is < 1 or > 12) return "1-12 yetkinlik seçin";
        if (list.Any(c => !KeyRx.IsMatch(c.Key ?? ""))) return "Yetkinlik anahtarı geçersiz";
        if (list.Any(c => (c.Label?.Trim().Length ?? 0) is < 2 or > 100)) return "Yetkinlik adı 2-100 karakter olmalı";
        if (list.Select(c => c.Key).Distinct().Count() != list.Count) return "Yetkinlik anahtarları benzersiz olmalı";
        return null;
    }

    public static string? ValidateReviewers(Guid subject, IReadOnlyList<(Guid EmployeeId, string Relationship)> reviewers)
    {
        if (reviewers.Any(r => r.EmployeeId == subject)) return "Kişi kendi 360 değerlendirmesine değerlendiren olarak eklenemez";
        if (reviewers.Select(r => r.EmployeeId).Distinct().Count() != reviewers.Count) return "Aynı kişi iki kez seçilemez";
        if (reviewers.Count < MinGroup) return "Anonimlik için en az 5 değerlendiren seçin";
        if (reviewers.Count > MaxReviewers) return "En fazla 40 değerlendiren seçilebilir";
        if (reviewers.Any(r => !Relationships.Contains(r.Relationship))) return "İlişki türü geçersiz";
        return null;
    }

    /// <summary>Puanlar 1-5; yalnızca talepteki yetkinlikler; en az bir puan. Puansız yetkinlik "fikrim yok"tur.</summary>
    public static string? ValidateRatings(IReadOnlyList<F360Competency> competencies, IReadOnlyDictionary<string, int> ratings, string? comment)
    {
        if (ratings.Count == 0) return "En az bir yetkinliği puanlayın";
        var keys = competencies.Select(c => c.Key).ToHashSet();
        if (ratings.Keys.Any(k => !keys.Contains(k))) return "Bilinmeyen yetkinlik";
        if (ratings.Values.Any(v => v is < 1 or > 5)) return "Puanlar 1-5 arasında olmalı";
        if (comment is { Length: > 2000 }) return "Yorum en fazla 2000 karakter olabilir";
        return null;
    }

    public sealed record CompetencyResult(string Key, string Label, int Ratings, decimal? Average, int[]? Distribution, bool Hidden);

    public sealed record Result(bool Hidden, int Responses, int Threshold, List<CompetencyResult> Competencies, List<string> Comments);

    public static Result Aggregate(IReadOnlyList<F360Competency> competencies, IReadOnlyList<(IReadOnlyDictionary<string, int> Ratings, string? Comment)> responses, int minResponses, bool closed)
    {
        var threshold = Math.Max(MinGroup, minResponses);
        if (!closed || responses.Count < threshold)
            return new Result(true, responses.Count, threshold, new(), new());
        var comps = competencies.Select(c =>
        {
            var vals = responses.Select(r => r.Ratings.TryGetValue(c.Key, out var v) ? v : 0).Where(v => v is >= 1 and <= 5).ToList();
            if (vals.Count < MinGroup) return new CompetencyResult(c.Key, c.Label, vals.Count, null, null, true);
            var dist = new int[5];
            foreach (var v in vals) dist[v - 1]++;
            return new CompetencyResult(c.Key, c.Label, vals.Count, Math.Round((decimal)vals.Average(), 2), dist, false);
        }).ToList();
        var comments = responses.Select(r => r.Comment?.Trim()).Where(c => !string.IsNullOrEmpty(c)).Select(c => c!)
            .OrderBy(c => c, StringComparer.Ordinal).ToList();
        return new Result(false, responses.Count, threshold, comps, comments);
    }
}

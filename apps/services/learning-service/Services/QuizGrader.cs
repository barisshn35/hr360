using System.Security.Cryptography;
using System.Text.Json;

namespace LearningService.Services;

public sealed record QuizOption(string Id, string Text);

public sealed record GradableQuestion(Guid Id, string Kind, IReadOnlyCollection<string> Correct);

public sealed record QuizGrade(decimal ScorePercent, bool Passed, int CorrectCount, int Total, IReadOnlyDictionary<Guid, bool> PerQuestion);

/// <summary>
/// Y20 sınav puanlama — sunucu tarafında. Tek seçimli: tam olarak bir seçenek ve doğru olan;
/// çok seçimli: seçilen küme doğru kümeyle birebir aynı (kısmi puan yok). Yanıtsız soru yanlıştır.
/// </summary>
public static class QuizGrader
{
    public static QuizGrade Grade(IReadOnlyList<GradableQuestion> questions,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<string>> answers, int passMarkPercent)
    {
        var per = new Dictionary<Guid, bool>();
        foreach (var q in questions)
        {
            answers.TryGetValue(q.Id, out var given);
            var picked = (given ?? Array.Empty<string>()).Select(x => x.Trim()).Where(x => x.Length > 0).ToHashSet();
            var correct = q.Correct.ToHashSet();
            bool ok = q.Kind == Models.QuestionKind.Single
                ? picked.Count == 1 && correct.Count == 1 && correct.SetEquals(picked)
                : correct.Count > 0 && correct.SetEquals(picked);
            per[q.Id] = ok;
        }
        var total = questions.Count;
        var right = per.Values.Count(v => v);
        var pct = total == 0 ? 0m : Math.Round(100m * right / total, 2);
        return new QuizGrade(pct, total > 0 && pct >= passMarkPercent, right, total, per);
    }

    public static List<QuizOption> Options(string json) =>
        JsonSerializer.Deserialize<List<QuizOption>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new();

    public static List<string> Ids(string json) => JsonSerializer.Deserialize<List<string>>(json) ?? new();
}

/// <summary>Sertifika doğrulama kodu: karışıklık yaratan harfler (0/O, 1/I/L) olmadan, XXXX-XXXX-XXXX.</summary>
public static class CertificateCodes
{
    private const string Alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";

    public static string New()
    {
        Span<char> c = stackalloc char[12];
        for (var i = 0; i < c.Length; i++) c[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        var s = new string(c);
        return $"{s[..4]}-{s[4..8]}-{s[8..]}";
    }
}

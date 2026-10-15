using LearningService.Models;

namespace LearningService.Services;

public sealed record GapItem(Guid CompetencyId, int Required, int? Current, int Gap, string Basis);

public sealed record RecommendedItem(Guid CompetencyId, int Gap, int From, int To);

public sealed record CourseRecommendation(Guid CourseId, int MaxGap, int TotalClosed, IReadOnlyList<RecommendedItem> Closes);

/// <summary>
/// Y19 yetkinlik açığı ve eğitim önerisi — saf hesap (birim testli).
/// Öneri yalnızca bir öneridir: kayıt açmaz, kişi hakkında karar üretmez.
/// </summary>
public static class CompetencyMath
{
    /// <summary>
    /// Beklenen seviyeler: önce departman tanımları, sonra pozisyon tanımları (aynı yetkinlikte
    /// pozisyon daha özgül olduğu için departmanı geçersiz kılar). Unvan karşılaştırması büyük/küçük
    /// harf ve baştaki/sondaki boşluklardan bağımsızdır.
    /// </summary>
    public static Dictionary<Guid, (int Level, string Basis)> Requirements(
        IEnumerable<RoleProfileEntry> entries, string? positionTitle, Guid? departmentId)
    {
        var result = new Dictionary<Guid, (int, string)>();
        var list = entries.ToList();
        if (departmentId is not null)
            foreach (var e in list.Where(e => e.DepartmentId == departmentId))
                result[e.CompetencyId] = (e.RequiredLevel, "Department");
        var title = Normalize(positionTitle);
        if (title.Length > 0)
            foreach (var e in list.Where(e => e.PositionTitle is not null && Normalize(e.PositionTitle) == title))
                result[e.CompetencyId] = (e.RequiredLevel, "Position");
        return result;
    }

    /// <summary>
    /// Unvan karşılaştırması için normalleştirme. Türkçe I/ı/İ/i farkı (ör. "MÜHENDISI" ile
    /// "Mühendisi") eşleşmeyi bozmasın diye hepsi "i"ye indirgenir; sonra kültürden bağımsız küçük harf.
    /// </summary>
    public static string Normalize(string? s) =>
        string.Join(' ', (s ?? "").Replace('İ', 'i').Replace('I', 'i').Replace('ı', 'i')
            .ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Yetkinlik başına en son değerlendirme ("son kayıt geçerli").</summary>
    public static Dictionary<Guid, CompetencyAssessment> Latest(IEnumerable<CompetencyAssessment> assessments) =>
        assessments.GroupBy(a => a.CompetencyId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.AssessedAt).ThenByDescending(a => a.Id).First());

    /// <summary>
    /// Açık = beklenen − güncel (en az 0). Hiç değerlendirilmemiş yetkinlikte güncel seviye 0 sayılır
    /// (açık = beklenen) ve Current null döner — arayüz "değerlendirilmedi" der. Büyük açık önce.
    /// </summary>
    public static List<GapItem> Gaps(IReadOnlyDictionary<Guid, (int Level, string Basis)> required, IReadOnlyDictionary<Guid, int> current)
    {
        return required.Select(r =>
        {
            int? cur = current.TryGetValue(r.Key, out var c) ? c : null;
            var gap = Math.Max(0, r.Value.Level - (cur ?? 0));
            return new GapItem(r.Key, r.Value.Level, cur, gap, r.Value.Basis);
        })
        .OrderByDescending(g => g.Gap).ThenBy(g => g.CompetencyId)
        .ToList();
    }

    /// <summary>
    /// Açığı kapatan eğitimler: etiketlediği yetkinlikte açık varsa ve hedef seviyesi güncel seviyenin
    /// üstündeyse aday olur; kapattığı miktar min(hedef, beklenen) − güncel. Sıralama: kapattığı en büyük
    /// açık, sonra toplam kapatılan açık.
    /// </summary>
    public static List<CourseRecommendation> Recommend(IEnumerable<GapItem> gaps, IEnumerable<CourseCompetency> tags)
    {
        var byComp = gaps.Where(g => g.Gap > 0).ToDictionary(g => g.CompetencyId);
        var result = new List<CourseRecommendation>();
        foreach (var course in tags.GroupBy(t => t.CourseId))
        {
            var items = new List<RecommendedItem>();
            foreach (var t in course)
            {
                if (!byComp.TryGetValue(t.CompetencyId, out var g)) continue;
                var from = g.Current ?? 0;
                var to = Math.Min(t.TargetLevel, g.Required);
                if (to <= from) continue;
                items.Add(new RecommendedItem(t.CompetencyId, g.Gap, from, to));
            }
            if (items.Count == 0) continue;
            result.Add(new CourseRecommendation(course.Key, items.Max(i => i.Gap), items.Sum(i => i.To - i.From),
                items.OrderByDescending(i => i.Gap).ToList()));
        }
        return result.OrderByDescending(r => r.MaxGap).ThenByDescending(r => r.TotalClosed).ThenBy(r => r.CourseId).ToList();
    }
}

using System.Globalization;

namespace EngagementService.Infrastructure.Coffee;

/// <summary>
/// Kahve eşleştirme (yenilik haritası #27) — saf kurallar. Katılımcılar ikişerli eşlenir; tek sayıda
/// katılımcı varsa bir grup üç kişi olur. Yakın zamanda eşleşmiş ikililer ve (istenirse) aynı departman
/// cezalandırılır; birkaç karıştırma denenir, en düşük cezalı eşleşme seçilir.
/// </summary>
public static class CoffeeMatcher
{
    public const string Weekly = "Weekly", Biweekly = "Biweekly", Monthly = "Monthly";
    public static readonly string[] Cadences = [Weekly, Biweekly, Monthly];
    private const int RepeatPenalty = 10, SameDeptPenalty = 1;

    public sealed record Member(Guid Id, Guid? DepartmentId);

    public static string PairKey(Guid a, Guid b) => a.CompareTo(b) < 0 ? $"{a}:{b}" : $"{b}:{a}";

    /// <summary>Dönem anahtarı (Türkiye tarihiyle): haftalık "2026-W42", iki haftalık "2026-B21", aylık "2026-M10".</summary>
    public static string PeriodKey(string cadence, DateOnly trDate)
    {
        var week = ISOWeek.GetWeekOfYear(trDate.ToDateTime(TimeOnly.MinValue));
        var year = ISOWeek.GetYear(trDate.ToDateTime(TimeOnly.MinValue));
        return cadence switch
        {
            Monthly => $"{trDate.Year}-M{trDate.Month:00}",
            Biweekly => $"{year}-B{(week + 1) / 2:00}",
            _ => $"{year}-W{week:00}",
        };
    }

    private static int Cost(Member a, Member b, IReadOnlySet<string> recent, bool crossDept) =>
        (recent.Contains(PairKey(a.Id, b.Id)) ? RepeatPenalty : 0) +
        (crossDept && a.DepartmentId is not null && a.DepartmentId == b.DepartmentId ? SameDeptPenalty : 0);

    private static int GroupCost(List<Member> g, IReadOnlySet<string> recent, bool crossDept)
    {
        var c = 0;
        for (var i = 0; i < g.Count; i++)
            for (var j = i + 1; j < g.Count; j++) c += Cost(g[i], g[j], recent, crossDept);
        return c;
    }

    /// <summary>Katılımcıları gruplara ayırır (2'li, tek sayıda ise bir 3'lü). 2'den az kişiyle boş döner.</summary>
    public static List<List<Member>> Match(IReadOnlyList<Member> people, IReadOnlySet<string> recent, bool crossDept, Random rng, int attempts = 30)
    {
        if (people.Count < 2) return [];
        List<List<Member>>? best = null;
        var bestCost = int.MaxValue;
        for (var t = 0; t < attempts && bestCost > 0; t++)
        {
            var pool = people.OrderBy(_ => rng.Next()).ToList();
            var groups = new List<List<Member>>();
            while (pool.Count >= 2)
            {
                var a = pool[0];
                pool.RemoveAt(0);
                var bi = 0;
                for (var i = 1; i < pool.Count && Cost(a, pool[bi], recent, crossDept) > 0; i++)
                    if (Cost(a, pool[i], recent, crossDept) < Cost(a, pool[bi], recent, crossDept)) bi = i;
                groups.Add([a, pool[bi]]);
                pool.RemoveAt(bi);
            }
            if (pool.Count == 1)
            {
                // Artan kişi en az ceza getirdiği gruba katılır.
                var x = pool[0];
                var gi = Enumerable.Range(0, groups.Count).MinBy(i => groups[i].Sum(m => Cost(x, m, recent, crossDept)));
                groups[gi].Add(x);
            }
            var cost = groups.Sum(g => GroupCost(g, recent, crossDept));
            if (cost < bestCost) (best, bestCost) = (groups, cost);
        }
        return best!;
    }
}

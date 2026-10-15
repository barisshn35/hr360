namespace GovernanceService.Infrastructure;

/// <summary>
/// Ekip etkileşim ağı (3B ekip ağı görünümü): yalnızca EKİP düzeyinde düğüm ve bağ üretir.
/// Girdi kişi düzeyindeki etkileşimlerin ekip çiftlerine toplanmış hâlidir (takdir, 1:1, ortak hedef);
/// kişi kimliği yanıta hiç çıkmaz.
///
/// KVKK (yeniden tanımlamayı önleme):
///  - 5 kişiden küçük ekipler tek başına gösterilmez; "Diğer" düğümünde birleşir. Birleşik düğümün
///    farklı kişi sayısı da 5'ten küçükse tamamen gizlenir (bağlarıyla birlikte).
///  - Bir bağın (ve ekip içi sayacın) her türü ayrı ayrı 3'ten küçükse sıfırlanır; ağırlık yalnızca
///    gösterilen türlerin toplamıdır. Böylece "toplam − diğer türler" çıkarımıyla küçük sayı geri
///    hesaplanamaz. Hiçbir türü kalmayan bağ gösterilmez.
/// Saf hesap: veritabanı yok, birim testi `Governance.Tests/TeamNetworkTests.cs`.
/// </summary>
public static class TeamNetwork
{
    public const int MinTeamSize = 5;
    public const int MinEdgeCount = 3;
    public const string OtherId = "other";

    public static class Kinds
    {
        public const string Kudos = "kudos";
        public const string OneOnOne = "oneOnOne";
        public const string SharedGoal = "sharedGoal";
    }

    public sealed record TeamRow(Guid Id, string Name, string? Department, IReadOnlyCollection<Guid> MemberIds);

    /// <summary>İki ekip arasındaki (A == B: ekip içi) bir türdeki etkileşim sayısı.</summary>
    public sealed record PairRow(Guid A, Guid B, string Kind, long Count);

    public sealed record Counts(long Kudos, long OneOnOnes, long SharedGoals)
    {
        public long Total => Kudos + OneOnOnes + SharedGoals;
        public static readonly Counts Zero = new(0, 0, 0);
    }

    public sealed record Node(string Id, string Name, string? Department, int Members, Counts Internal, int MergedTeams);

    public sealed record Edge(string Source, string Target, Counts Counts)
    {
        public long Weight => Counts.Total;
    }

    public sealed record Result(List<Node> Nodes, List<Edge> Edges, int HiddenTeams, int HiddenEdges);

    /// <summary>Türü küçük sayıdan arındırır: 3'ten küçük her tür 0 olur.</summary>
    public static Counts Suppress(Counts c) => new(
        c.Kudos >= MinEdgeCount ? c.Kudos : 0,
        c.OneOnOnes >= MinEdgeCount ? c.OneOnOnes : 0,
        c.SharedGoals >= MinEdgeCount ? c.SharedGoals : 0);

    private static Counts Add(Counts c, string kind, long n) => kind switch
    {
        Kinds.Kudos => c with { Kudos = c.Kudos + n },
        Kinds.OneOnOne => c with { OneOnOnes = c.OneOnOnes + n },
        Kinds.SharedGoal => c with { SharedGoals = c.SharedGoals + n },
        _ => c,
    };

    public static Result Build(IEnumerable<TeamRow> teams, IEnumerable<PairRow> pairs, string otherName = "Diğer")
    {
        var list = teams.ToList();
        var big = list.Where(t => t.MemberIds.Distinct().Count() >= MinTeamSize).ToList();
        var small = list.Where(t => t.MemberIds.Distinct().Count() < MinTeamSize).ToList();

        // Küçük ekipler "Diğer"de birleşir; aynı kişi birden çok küçük ekipte olabilir → farklı kişi sayılır.
        var otherPeople = small.SelectMany(t => t.MemberIds).Distinct().Count();
        var otherShown = small.Count > 0 && otherPeople >= MinTeamSize;

        var nodeOf = new Dictionary<Guid, string>();
        foreach (var t in big) nodeOf[t.Id] = t.Id.ToString();
        if (otherShown) foreach (var t in small) nodeOf[t.Id] = OtherId;

        // Ekip çiftlerini düğüm çiftlerine topla (sıra bağımsız).
        var raw = new Dictionary<(string, string), Counts>();
        foreach (var p in pairs)
        {
            if (p.Count <= 0 || !nodeOf.TryGetValue(p.A, out var a) || !nodeOf.TryGetValue(p.B, out var b)) continue;
            var key = string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);
            raw[key] = Add(raw.GetValueOrDefault(key, Counts.Zero), p.Kind, p.Count);
        }

        var internalOf = new Dictionary<string, Counts>();
        var edges = new List<Edge>();
        var hiddenEdges = 0;
        foreach (var ((a, b), c) in raw)
        {
            var s = Suppress(c);
            if (a == b) { internalOf[a] = s; continue; }
            if (s.Total == 0) { hiddenEdges++; continue; }
            edges.Add(new Edge(a, b, s));
        }

        var nodes = big
            .Select(t => new Node(t.Id.ToString(), t.Name, t.Department, t.MemberIds.Distinct().Count(),
                internalOf.GetValueOrDefault(t.Id.ToString(), Counts.Zero), 1))
            .OrderBy(n => n.Department).ThenBy(n => n.Name)
            .ToList();
        if (otherShown)
            nodes.Add(new Node(OtherId, otherName, null, otherPeople, internalOf.GetValueOrDefault(OtherId, Counts.Zero), small.Count));

        return new Result(nodes, edges.OrderByDescending(e => e.Weight).ThenBy(e => e.Source).ThenBy(e => e.Target).ToList(),
            otherShown ? 0 : small.Count, hiddenEdges);
    }
}

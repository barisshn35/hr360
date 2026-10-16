using EngagementService.Infrastructure.Coffee;
using Xunit;

namespace Engagement.Tests;

public class CoffeeMatcherTests
{
    private static readonly Guid D1 = Guid.NewGuid(), D2 = Guid.NewGuid();
    private static List<CoffeeMatcher.Member> People(int n, Func<int, Guid?>? dept = null) =>
        Enumerable.Range(0, n).Select(i => new CoffeeMatcher.Member(Guid.NewGuid(), dept?.Invoke(i))).ToList();
    private static readonly HashSet<string> None = [];

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 1)]
    [InlineData(7, 3)]
    [InlineData(10, 5)]
    public void Everyone_matched_exactly_once_odd_makes_one_trio(int n, int groups)
    {
        var p = People(n);
        var g = CoffeeMatcher.Match(p, None, false, new Random(1));
        Assert.Equal(groups, g.Count);
        if (n >= 2)
        {
            Assert.Equal(n, g.Sum(x => x.Count));
            Assert.Equal(n, g.SelectMany(x => x).Select(x => x.Id).Distinct().Count());
            Assert.All(g, x => Assert.InRange(x.Count, 2, 3));
            Assert.True(g.Count(x => x.Count == 3) == n % 2);
        }
    }

    [Fact]
    public void Prefers_other_departments_when_possible()
    {
        var p = People(8, i => i < 4 ? D1 : D2);
        var g = CoffeeMatcher.Match(p, None, true, new Random(7));
        Assert.All(g, x => Assert.NotEqual(x[0].DepartmentId, x[1].DepartmentId));
    }

    [Fact]
    public void Avoids_recent_pairs_when_possible()
    {
        var p = People(4);
        var recent = new HashSet<string> { CoffeeMatcher.PairKey(p[0].Id, p[1].Id), CoffeeMatcher.PairKey(p[2].Id, p[3].Id) };
        for (var seed = 0; seed < 20; seed++)
        {
            var g = CoffeeMatcher.Match(p, recent, false, new Random(seed));
            Assert.All(g, x => Assert.DoesNotContain(CoffeeMatcher.PairKey(x[0].Id, x[1].Id), recent));
        }
    }

    [Fact]
    public void Repeats_only_when_unavoidable()
    {
        var p = People(2);
        var g = CoffeeMatcher.Match(p, new HashSet<string> { CoffeeMatcher.PairKey(p[0].Id, p[1].Id) }, true, new Random(3));
        Assert.Single(g);
    }

    [Theory]
    [InlineData("Weekly", 2026, 10, 16, "2026-W42")]
    [InlineData("Biweekly", 2026, 10, 16, "2026-B21")]
    [InlineData("Biweekly", 2026, 10, 12, "2026-B21")]
    [InlineData("Monthly", 2026, 10, 16, "2026-M10")]
    [InlineData("Weekly", 2027, 1, 1, "2026-W53")]
    public void Period_keys(string cadence, int y, int m, int d, string key) =>
        Assert.Equal(key, CoffeeMatcher.PeriodKey(cadence, new DateOnly(y, m, d)));
}

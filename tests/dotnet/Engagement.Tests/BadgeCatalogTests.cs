using EngagementService.Infrastructure.Badges;
using Xunit;

namespace Engagement.Tests;

public class BadgeCatalogTests
{
    [Fact]
    public void Ids_are_unique_and_short_enough_for_the_column() =>
        Assert.All(BadgeCatalog.All.GroupBy(b => b.Id), g => { Assert.Single(g); Assert.True(g.Key.Length <= 32); });

    [Fact]
    public void Earned_when_metric_reaches_target_and_progress_is_capped()
    {
        var r = BadgeCatalog.Evaluate(new BadgeCatalog.Metrics { [BadgeCatalog.KudosReceived] = 12 });
        Assert.True(r.Single(x => x.Id == "kudos-received-1").Earned);
        Assert.True(r.Single(x => x.Id == "kudos-received-10").Earned);
        var fifty = r.Single(x => x.Id == "kudos-received-50");
        Assert.False(fifty.Earned);
        Assert.Equal(12, fifty.Value);
        Assert.Equal(1, r.Single(x => x.Id == "kudos-received-1").Value);
        Assert.False(r.Single(x => x.Id == "cert-1").Earned);
    }

    [Theory]
    [InlineData("2020-10-16", "2026-10-16", 6)]
    [InlineData("2020-10-17", "2026-10-16", 5)]
    [InlineData("2026-01-01", "2026-10-16", 0)]
    [InlineData("2024-02-29", "2025-02-27", 0)]
    [InlineData("2024-02-29", "2025-02-28", 1)] // 29 Şubat girişlinin yıl dönümü artık olmayan yılda 28 Şubat
    [InlineData("2024-02-29", "2025-03-01", 1)]
    public void Tenure_counts_full_years(string hire, string today, int years) =>
        Assert.Equal(years, BadgeCatalog.TenureYears(DateOnly.Parse(hire), DateOnly.Parse(today)));
}

using EngagementService.Controllers;
using Xunit;

namespace Engagement.Tests;

/// <summary>1:1 görüşme: planlı görüşme geçmişe kurulamaz (5 dk pay).</summary>
public class OneOnOneRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(-60, true)]
    [InlineData(-6, true)]
    [InlineData(-4, false)]
    [InlineData(0, false)]
    [InlineData(60 * 24, false)]
    public void Gecmis_tarih_reddedilir(int minutes, bool past) =>
        Assert.Equal(past, OneOnOnesController.IsPast(Now.AddMinutes(minutes), Now));
}

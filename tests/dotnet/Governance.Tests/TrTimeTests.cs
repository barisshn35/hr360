using GovernanceService.Infrastructure;
using Xunit;

public class TrTimeTests
{
    [Fact]
    public void Gun_baslangici_Turkiye_saatine_gore_UTC_olur()
    {
        // 7 Ekim 00:00 (UTC+3) = 6 Ekim 21:00 UTC
        var start = TrTime.StartOfDayUtc(new DateOnly(2026, 10, 7));
        Assert.Equal(new DateTime(2026, 10, 6, 21, 0, 0, DateTimeKind.Utc), start);
        Assert.Equal(DateTimeKind.Utc, start.Kind);
    }
}

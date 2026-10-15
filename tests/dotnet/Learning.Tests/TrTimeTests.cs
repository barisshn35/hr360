using LearningService.Services;
using Xunit;

public class TrTimeTests
{
    [Fact]
    public void Gece_yarisindan_sonra_Turkiye_tarihi_UTC_tarihinden_ileridedir()
    {
        // 14 Ekim 22:30 UTC = 15 Ekim 01:30 (UTC+3)
        Assert.Equal(new DateOnly(2026, 10, 15), TrTime.DateOf(new DateTime(2026, 10, 14, 22, 30, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateOnly(2026, 10, 14), TrTime.DateOf(new DateTime(2026, 10, 14, 20, 30, 0, DateTimeKind.Utc)));
    }
}

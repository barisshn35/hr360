using EngagementService.Infrastructure;
using EngagementService.Models;
using Xunit;

namespace Engagement.Tests;

public class RecognitionPointsTests
{
    private static readonly RecognitionSettings On = new() { Enabled = true, MonthlyAllowance = 100, MaxPerKudos = 50 };

    [Fact]
    public void Zero_points_is_always_allowed_even_when_disabled() =>
        Assert.Null(RecognitionPoints.ValidateGive(new RecognitionSettings { Enabled = false }, 0, 999, false));

    [Fact]
    public void Points_require_enabled_setting_and_employee_record()
    {
        Assert.Equal("points_disabled", RecognitionPoints.ValidateGive(new RecognitionSettings { Enabled = false }, 10, 0, true)?.Code);
        Assert.Equal("points_no_employee", RecognitionPoints.ValidateGive(On, 10, 0, false)?.Code);
    }

    [Theory]
    [InlineData(51, 0, "points_max")]
    [InlineData(-5, 0, "points_max")]
    [InlineData(50, 60, "points_allowance")]
    [InlineData(41, 60, "points_allowance")]
    [InlineData(40, 60, null)]
    [InlineData(50, 50, null)]
    public void Per_kudos_cap_and_monthly_allowance(int points, int given, string? code) =>
        Assert.Equal(code, RecognitionPoints.ValidateGive(On, points, given, true)?.Code);

    [Fact]
    public void Month_starts_at_turkey_midnight()
    {
        // 31 Ekim 22:30 UTC = 1 Kasım 01:30 TR → Kasım ayı, başlangıç 31 Ekim 21:00 UTC.
        Assert.Equal(new DateTime(2026, 10, 31, 21, 0, 0, DateTimeKind.Utc), RecognitionPoints.MonthStartUtc(new DateTime(2026, 10, 31, 22, 30, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2026, 9, 30, 21, 0, 0, DateTimeKind.Utc), RecognitionPoints.MonthStartUtc(new DateTime(2026, 10, 31, 20, 59, 0, DateTimeKind.Utc)));
    }
}

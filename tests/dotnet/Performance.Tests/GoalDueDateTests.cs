using PerformanceService.Controllers;
using PerformanceService.Models;
using Xunit;

namespace Performance.Tests;

/// <summary>Hedef son tarihi: boş olabilir, dönem başlangıcı ile bitişi arasında olmalı.</summary>
public class GoalDueDateTests
{
    private static ReviewCycle Cycle() => new()
    {
        Name = "2026 Q3", Year = 2026, Period = CyclePeriod.Q3,
        StartDate = new DateOnly(2026, 7, 1), EndDate = new DateOnly(2026, 9, 30),
    };

    [Fact]
    public void Bos_son_tarih_gecerli() => Assert.Null(GoalsController.DueDateError(null, Cycle()));

    [Theory]
    [InlineData(2026, 7, 1)]
    [InlineData(2026, 8, 15)]
    [InlineData(2026, 9, 30)]
    public void Donem_icindeki_tarih_gecerli(int y, int m, int d) =>
        Assert.Null(GoalsController.DueDateError(new DateOnly(y, m, d), Cycle()));

    [Fact]
    public void Donem_bitisinden_sonrasi_reddedilir() =>
        Assert.Contains("bitişinden", GoalsController.DueDateError(new DateOnly(2026, 10, 1), Cycle()));

    [Fact]
    public void Donem_baslangicindan_oncesi_reddedilir() =>
        Assert.Contains("başlangıcından", GoalsController.DueDateError(new DateOnly(2026, 6, 30), Cycle()));
}

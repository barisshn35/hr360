using TimeShiftService.Controllers;
using TimeShiftService.Models;
using TimeShiftService.Services;
using Xunit;

namespace TimeShift.Tests;

public class TimeClockTests
{
    [Fact]
    public void Uzaklik_hesabi_metre()
    {
        // Taksim Meydanı -> Gezi Parkı girişi ~ 150-250 m
        var d = ClockCore.DistanceMeters(41.0369, 28.9850, 41.0382, 28.9868);
        Assert.InRange(d, 150, 250);
        Assert.Equal(0, ClockCore.DistanceMeters(41, 29, 41, 29), 3);
    }

    [Fact]
    public void QR_jetonu_pencereye_ve_anahtara_bagli()
    {
        var site = new TimeClockSite { QrSecret = "gizli-1" };
        var a = TimeClockController.QrToken(site, 1000);
        Assert.Equal(a, TimeClockController.QrToken(site, 1000));
        Assert.NotEqual(a, TimeClockController.QrToken(site, 1001));
        Assert.NotEqual(a, TimeClockController.QrToken(new TimeClockSite { Id = site.Id, QrSecret = "gizli-2" }, 1000));
        Assert.StartsWith(site.Id.ToString("N") + ".1000.", a);
    }

    [Fact]
    public void Is_gunu_Istanbul_saatine_gore()
    {
        // 22:30 UTC = 01:30 İstanbul (ertesi gün)
        var d = ClockCore.WorkDate(new DateTimeOffset(2026, 3, 9, 22, 30, 0, TimeSpan.Zero));
        Assert.Equal(new DateOnly(2026, 3, 10), d);
    }
}

using LeaveService.Services;
using Xunit;

namespace Leave.Tests;

public class LeaveEntitlementTests
{
    static readonly DateOnly Birth30 = new(1996, 6, 1);

    [Theory]
    [InlineData(2026, 2026, 0)]   // ilk yıl: hak yok
    [InlineData(2025, 2026, 14)]  // 1 yıl
    [InlineData(2021, 2026, 14)]  // 5 yıl (dahil) → 14
    [InlineData(2020, 2026, 20)]  // 6 yıl → 20
    [InlineData(2012, 2026, 20)]  // 14 yıl → 20
    [InlineData(2011, 2026, 26)]  // 15 yıl (dahil) → 26
    [InlineData(1990, 2026, 26)]
    public void Kideme_gore_yasal_izin(int hireYear, int year, int expected)
    {
        var (days, _, _) = LeaveEntitlement.Statutory(new DateOnly(hireYear, 3, 15), Birth30, year);
        Assert.Equal(expected, days);
    }

    [Fact]
    public void Elli_yas_ve_ustu_en_az_yirmi_gun()
    {
        var (days, years, ageRule) = LeaveEntitlement.Statutory(new DateOnly(2023, 3, 15), new DateOnly(1975, 1, 1), 2026);
        Assert.Equal(3, years);
        Assert.Equal(20, days);
        Assert.True(ageRule);
    }

    [Fact]
    public void Onsekiz_yas_ve_alti_en_az_yirmi_gun()
    {
        // Yıl dönümünde 18 yaşında
        var (days, _, ageRule) = LeaveEntitlement.Statutory(new DateOnly(2025, 9, 1), new DateOnly(2008, 1, 10), 2026);
        Assert.Equal(20, days);
        Assert.True(ageRule);
    }

    [Fact]
    public void Yas_kurali_yirmiden_fazlayi_dusurmez()
    {
        var (days, _, ageRule) = LeaveEntitlement.Statutory(new DateOnly(2000, 1, 1), new DateOnly(1960, 1, 1), 2026);
        Assert.Equal(26, days);
        Assert.False(ageRule);
    }

    [Fact]
    public void Dogum_tarihi_yoksa_yalnizca_kidem()
    {
        var (days, _, _) = LeaveEntitlement.Statutory(new DateOnly(2023, 1, 1), null, 2026);
        Assert.Equal(14, days);
    }

    [Fact]
    public void Subat_29_yil_donumu()
    {
        Assert.Equal(new DateOnly(2027, 2, 28), LeaveEntitlement.Anniversary(new DateOnly(2024, 2, 29), 2027));
        Assert.Equal(new DateOnly(2028, 2, 29), LeaveEntitlement.Anniversary(new DateOnly(2024, 2, 29), 2028));
    }

    [Fact]
    public void Saatlik_izin_gune_cevrilir()
    {
        Assert.Equal(0.4m, LeaveEntitlement.HoursToDays(3m));     // 3 / 7,5
        Assert.Equal(0.27m, LeaveEntitlement.HoursToDays(2m));
    }
}

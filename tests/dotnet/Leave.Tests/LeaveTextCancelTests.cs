using LeaveService.Models;
using LeaveService.Services;
using Xunit;

namespace Leave.Tests;

/// <summary>İzin başlıkları (Türkçe sayı biçimi), saatlik izin üst sınırı, onaylı izin iptali kuralları.</summary>
public class LeaveTextCancelTests
{
    [Fact]
    public void Baslik_turkce_ondalik_virgul()
    {
        var d = new DateOnly(2026, 10, 28);
        Assert.Equal("0,5 günlük yıllık izin talebi (28.10.2026 - 28.10.2026)", LeaveText.Subject(LeaveType.Annual, 0.5m, null, d, d));
        Assert.Equal("7,5 saatlik ücretsiz izin talebi (27.10.2026)", LeaveText.Subject(LeaveType.Unpaid, 1m, 7.5m, d.AddDays(-1), d.AddDays(-1)));
        Assert.Equal("3 günlük hastalık izni talebi (05.10.2026 - 07.10.2026)",
            LeaveText.Subject(LeaveType.Sick, 3m, null, new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 7)));
        Assert.Equal("10,25", LeaveText.Num(10.25m));
    }

    [Fact]
    public void Saatlik_izin_gunun_tamami_girilebilir()
    {
        Assert.Null(LeaveText.HoursError(7.5m, 7.5m, 1m));      // önceden "7.5 saatten az olmalı" ile reddediliyordu
        Assert.Null(LeaveText.HoursError(0.5m, 7.5m, 1m));
        Assert.Equal(1m, LeaveEntitlement.HoursToDays(7.5m, 7.5m));
        Assert.Equal("Saatlik izin 0,5 saatlik adımlarla ve en fazla 7,5 saat olmalı", LeaveText.HoursError(8m, 7.5m, 1m));
        Assert.NotNull(LeaveText.HoursError(1.25m, 7.5m, 1m));  // 0,5 adım
        Assert.NotNull(LeaveText.HoursError(0m, 7.5m, 1m));
        Assert.Null(LeaveText.HoursError(3.5m, 7.5m, 0.5m));    // arife: yarım gün
        Assert.Equal("Saatlik izin 0,5 saatlik adımlarla ve en fazla 3,75 saat olmalı", LeaveText.HoursError(4m, 7.5m, 0.5m));
    }

    [Fact]
    public void Onayli_izin_iptali_yalnizca_baslamamis_onayli_izinde()
    {
        var today = new DateOnly(2026, 10, 14);
        Assert.Null(LeaveCancellation.ApprovedCancelError(LeaveRequestStatus.Approved, today.AddDays(1), today));
        Assert.NotNull(LeaveCancellation.ApprovedCancelError(LeaveRequestStatus.Approved, today, today));       // bugün başladı
        Assert.NotNull(LeaveCancellation.ApprovedCancelError(LeaveRequestStatus.Approved, today.AddDays(-3), today));
        Assert.NotNull(LeaveCancellation.ApprovedCancelError(LeaveRequestStatus.Submitted, today.AddDays(5), today));
        Assert.NotNull(LeaveCancellation.ApprovedCancelError(LeaveRequestStatus.Cancelled, today.AddDays(5), today));
        Assert.NotNull(LeaveCancellation.ReasonError("  "));
        Assert.NotNull(LeaveCancellation.ReasonError("abc"));
        Assert.Null(LeaveCancellation.ReasonError("Plan değişti"));
        Assert.NotNull(LeaveCancellation.ReasonError(new string('x', 501)));
    }
}

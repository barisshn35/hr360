using TimeShiftService.Models;
using TimeShiftService.Services;
using Xunit;

namespace TimeShift.Tests;

/// <summary>G6 takas kuralları ve tercih uyuşmazlığı, G7 geç kalma / fazla mesai hesabı.</summary>
public class OpsRulesTests
{
    private static readonly DateOnly Mon = new(2026, 10, 12); // Pazartesi
    private static ShiftInterval S(DateOnly d, int sh, int eh, int brk = 60) => ShiftInterval.Of(Guid.NewGuid(), d, new TimeOnly(sh, 0), new TimeOnly(eh, 0), brk);

    [Fact]
    public void Gece_vardiyasi_ertesi_gune_tasar()
    {
        var s = S(Mon, 22, 6, 30);
        Assert.Equal(Mon.AddDays(1).ToDateTime(new TimeOnly(6, 0)), s.End);
        Assert.Equal(450, s.NetMinutes);
    }

    [Fact]
    public void Uygun_takas_gecer()
    {
        var sched = new List<ShiftInterval> { S(Mon, 8, 16), S(Mon.AddDays(1), 8, 16), S(Mon.AddDays(2), 8, 16) };
        Assert.Null(SwapRules.Validate(sched, new[] { sched[1] }));
    }

    [Fact]
    public void Cakisan_vardiya_reddedilir()
    {
        var a = S(Mon, 8, 16);
        var b = S(Mon, 14, 22);
        var r = SwapRules.Validate(new[] { a, b }, new[] { b });
        Assert.NotNull(r);
        Assert.Contains("çakışıyor", r);
    }

    [Fact]
    public void On_bir_saat_dinlenme_kurali()
    {
        // 16:00-24:00 sonrası ertesi gün 08:00 -> 8 saat dinlenme: red
        var late = ShiftInterval.Of(null, Mon, new TimeOnly(16, 0), new TimeOnly(0, 0), 30);
        var early = S(Mon.AddDays(1), 8, 16);
        var r = SwapRules.Validate(new[] { late, early }, new[] { early });
        Assert.NotNull(r);
        Assert.Contains("11 saat", r);
        // tam 11 saat: kabul (13:00 bitiş -> ertesi 00:00 değil, aynı gün 13:00 + 11 = 24:00)
        var a = S(Mon, 5, 13, 0);
        var b = ShiftInterval.Of(null, Mon.AddDays(1), new TimeOnly(0, 0), new TimeOnly(8, 0), 0);
        Assert.Null(SwapRules.Validate(new[] { a, b }, new[] { b }));
    }

    [Fact]
    public void Haftalik_45_saat_siniri()
    {
        // 6 gün x 8 saat net = 48 saat -> red
        var sched = Enumerable.Range(0, 6).Select(i => S(Mon.AddDays(i), 8, 17)).ToList();
        var r = SwapRules.Validate(sched, new[] { sched[5] });
        Assert.NotNull(r);
        Assert.Contains("45", r);
        // 5 gün x 8 = 40 saat -> kabul
        Assert.Null(SwapRules.Validate(sched.Take(5).ToList(), new[] { sched[4] }));
        // değişmeyen haftadaki eski aşım yeni talebi engellemez
        var nextWeek = S(Mon.AddDays(7), 8, 17);
        Assert.Null(SwapRules.Validate(sched.Append(nextWeek).ToList(), new[] { nextWeek }));
    }

    [Fact]
    public void Tercih_uyusmazliklari()
    {
        var p = new ShiftPreference { UnavailableDays = new[] { 7 }, PreferredDays = new[] { 1, 2, 3, 4, 5 }, AvoidShiftTypes = new[] { "Night" }, MaxNightsPerWeek = 1 };
        var sunday = Mon.AddDays(6);
        var c = PreferenceRules.Check(p, sunday, new TimeOnly(8, 0), new TimeOnly(16, 0), false, 0);
        Assert.Contains(c, x => x.Code == "unavailable_day");
        c = PreferenceRules.Check(p, Mon, new TimeOnly(22, 0), new TimeOnly(6, 0), true, 1);
        Assert.Contains(c, x => x.Code == "avoided_type");
        Assert.Contains(c, x => x.Code == "max_nights");
        Assert.Empty(PreferenceRules.Check(p, Mon, new TimeOnly(9, 0), new TimeOnly(17, 0), false, 0));
        Assert.Empty(PreferenceRules.Check(null, sunday, new TimeOnly(22, 0), new TimeOnly(6, 0), true, 5));
        Assert.Equal("Night", PreferenceRules.ShiftType(new TimeOnly(22, 0), new TimeOnly(6, 0), false));
        Assert.Equal(7, PreferenceRules.IsoDay(sunday));
    }

    private static DateTime T(int h, int m = 0) => Mon.ToDateTime(new TimeOnly(h, m));

    [Fact]
    public void Gec_kalma_tolerans_ve_fazla_mesai()
    {
        // 09:00-18:00, 60 dk mola; 09:20 giriş, 20:00 çıkış -> 20 dk geç, çalışılan 640-60 = 580, fazla mesai 100
        var r = AttendanceCalc.Compute(T(9), T(18), 60, new[] { (true, T(9, 20)), (false, T(20)) }, 5);
        Assert.Equal(20, r.LateMinutes);
        Assert.Equal(0, r.EarlyLeaveMinutes);
        Assert.Equal(580, r.WorkedMinutes);
        Assert.Equal(480, r.ExpectedMinutes);
        Assert.Equal(100, r.OvertimeMinutes);
        Assert.Equal(1.5m, r.SuggestedOvertimeHours);
        Assert.Equal("Late", r.Status);
        // tolerans içinde (09:04) geç sayılmaz
        Assert.Equal(0, AttendanceCalc.Compute(T(9), T(18), 60, new[] { (true, T(9, 4)), (false, T(18)) }, 5).LateMinutes);
    }

    [Fact]
    public void Erken_cikis_mola_okutmasi_ve_eksik_cikis()
    {
        var r = AttendanceCalc.Compute(T(9), T(18), 60, new[] { (true, T(9)), (false, T(12)), (true, T(13)), (false, T(17)) }, 5);
        Assert.Equal(60, r.EarlyLeaveMinutes);
        Assert.Equal(420, r.WorkedMinutes); // iki çift: mola zaten okutulmuş, ayrıca düşülmez
        Assert.Equal(0, r.OvertimeMinutes);
        Assert.Equal("EarlyLeave", r.Status);

        var m = AttendanceCalc.Compute(T(9), T(18), 60, new[] { (true, T(9)) }, 5);
        Assert.Equal("MissingOut", m.Status);
        Assert.Equal(0, m.WorkedMinutes);

        var absent = AttendanceCalc.Compute(T(9), T(18), 60, Array.Empty<(bool, DateTime)>(), 5);
        Assert.Equal("Absent", absent.Status);
    }

    [Fact]
    public void Plansiz_gun_calismasi_tamami_fazla_mesai_ve_ust_sinir()
    {
        var r = AttendanceCalc.Compute(null, null, 0, new[] { (true, T(10)), (false, T(16)) }, 5);
        Assert.Equal(360, r.OvertimeMinutes);
        Assert.Equal("OffDayWork", r.Status);
        Assert.Equal(4m, r.SuggestedOvertimeHours); // günlük en çok 4 saat önerilir
    }
}

using TimeShiftService.Controllers;
using TimeShiftService.Models;
using TimeShiftService.Services;
using Xunit;

namespace TimeShift.Tests;

/// <summary>Dalga 9: çalışma süresi kuralları, konum aralığı, kiosk QR jetonu, dönem kilidi, vardiya önerisi farkı.</summary>
public class Wave9Tests
{
    private static readonly DateOnly Mon = new(2026, 10, 12); // Pazartesi
    private static ShiftInterval S(DateOnly d, int sh, int eh, int brk = 60) => ShiftInterval.Of(Guid.NewGuid(), d, new TimeOnly(sh, 0), new TimeOnly(eh, 0), brk);

    [Fact]
    public void Dinlenme_kurali_kiraci_ayariyla()
    {
        var a = S(Mon, 8, 18);              // 18:00 bitiş
        var b = S(Mon.AddDays(1), 6, 14);   // ertesi 06:00 -> 12 saat dinlenme
        Assert.DoesNotContain(WorkRules.Check(new[] { a, b }, new[] { b }), w => w.Code == "rest");
        var strict = new WorkRuleSettings(MinRestHours: 13);
        var w = Assert.Single(WorkRules.Check(new[] { a, b }, new[] { b }, strict), x => x.Code == "rest");
        Assert.Contains("13 saat", w.Message);
        // Mesaj önceki vardiyanın BİTİŞİNİ ve sonrakinin başlangıcını gösterir (önceden başlangıç yazıyordu).
        Assert.Contains("12.10 18:00 ile 13.10 06:00 arası 12 saat", w.Message);
        // Değişen vardiya öncekiyse de sıra doğru.
        var w2 = Assert.Single(WorkRules.Check(new[] { a, b }, new[] { a }, strict), x => x.Code == "rest");
        Assert.Contains("12.10 18:00 ile 13.10 06:00", w2.Message);
    }

    [Fact]
    public void Gunluk_11_saat_uyarisi()
    {
        var longDay = S(Mon, 7, 20, 60); // 12 saat net
        Assert.Contains(WorkRules.Check(new[] { longDay }, new[] { longDay }), w => w.Code == "daily");
        var normal = S(Mon, 8, 17);
        Assert.Empty(WorkRules.Check(new[] { normal }, new[] { normal }));
    }

    [Fact]
    public void Gece_calismasi_7_5_saat()
    {
        var night8 = S(Mon, 22, 7, 60);     // 8 saat net, tamamı gece diliminde
        Assert.True(WorkRules.IsNightWork(night8));
        Assert.Contains(WorkRules.Check(new[] { night8 }, new[] { night8 }), w => w.Code == "night");
        var night75 = S(Mon, 22, 6, 30);    // 7,5 saat net
        Assert.DoesNotContain(WorkRules.Check(new[] { night75 }, new[] { night75 }), w => w.Code == "night");
        var evening = S(Mon, 14, 23, 60);   // 3 saat gece diliminde / 9 saat: gece çalışması değil
        Assert.False(WorkRules.IsNightWork(evening));
        Assert.Equal(180, WorkRules.NightMinutes(evening));
    }

    [Fact]
    public void Ardisik_gun_siniri()
    {
        var sched = Enumerable.Range(0, 7).Select(i => S(Mon.AddDays(i), 9, 14, 0)).ToList(); // 7 gün x 5 saat = 35 saat
        var w = WorkRules.Check(sched, new[] { sched[6] });
        Assert.Contains(w, x => x.Code == "consecutive");
        Assert.DoesNotContain(w, x => x.Code == "weekly");
        Assert.DoesNotContain(WorkRules.Check(sched, new[] { sched[6] }, new WorkRuleSettings(MaxConsecutiveDays: 7)), x => x.Code == "consecutive");
    }

    [Fact]
    public void Takas_engelleyici_ve_uyari_kurallari_ayrilir()
    {
        // 8 saatlik gece vardiyası: uyarı ("night") ama takası engellemez.
        var n = S(Mon, 22, 7, 60);
        Assert.Null(SwapRules.Validate(new[] { n }, new[] { n }));
        // Haftalık sınır kiracı ayarıyla düşürülünce engeller.
        var sched = Enumerable.Range(0, 5).Select(i => S(Mon.AddDays(i), 8, 17)).ToList(); // 40 saat
        Assert.Null(SwapRules.Validate(sched, new[] { sched[4] }));
        Assert.Contains("37,5", SwapRules.Validate(sched, new[] { sched[4] }, rules: new WorkRuleSettings(WeeklyMaxHours: 37.5m)));
    }

    [Fact]
    public void Ayarlardan_kural_degerleri()
    {
        var r = WorkRuleSettings.From(new TimesheetSettings { MinRestHours = 12, WeeklyMaxHours = 40, NightMaxHours = 7, DailyMaxHours = 10, MaxConsecutiveDays = 5 });
        Assert.Equal(new WorkRuleSettings(12, 40, 10, 7, 5), r);
        Assert.Equal(WorkRuleSettings.Default, WorkRuleSettings.From(null));
    }

    [Theory]
    [InlineData(0, "0-50")]
    [InlineData(49.9, "0-50")]
    [InlineData(50, "50-100")]
    [InlineData(180, "100-250")]
    [InlineData(400, "250-500")]
    [InlineData(999, "500-1000")]
    [InlineData(25000, "1000+")]
    public void Konum_uzaklik_araligi(double meters, string bucket) => Assert.Equal(bucket, ClockCore.DistanceBucket(meters));

    [Fact]
    public void Kiosk_qr_jetonu_30_sn_ve_onceki_pencere()
    {
        var site = new TimeClockSite { Name = "Giriş", QrSecret = "gizli-anahtar-123" };
        var now = TimeClockController.Window(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
        var token = TimeClockController.QrToken(site, now);
        Assert.True(TimeClockController.QrValid(site, token, now));
        Assert.True(TimeClockController.QrValid(site, token, now + 1));   // bir sonraki pencerede hâlâ geçerli
        Assert.False(TimeClockController.QrValid(site, token, now + 2));  // 30-60 sn sonra geçersiz
        Assert.False(TimeClockController.QrValid(site, token[..^2] + "AA", now));
        Assert.False(TimeClockController.QrValid(new TimeClockSite { Id = site.Id, QrSecret = "baska" }, token, now));
        Assert.Equal(30, TimeClockController.QrWindowSeconds);
    }

    [Fact]
    public void Donem_kilidi_ay_dogrulamasi()
    {
        var today = new DateOnly(2026, 10, 15);
        Assert.Null(PeriodLock.ValidateMonth(2026, 10, today));
        Assert.Null(PeriodLock.ValidateMonth(2026, 9, today));
        Assert.NotNull(PeriodLock.ValidateMonth(2026, 11, today));
        Assert.NotNull(PeriodLock.ValidateMonth(2026, 13, today));
        Assert.Contains("10.2026", PeriodLock.Message(new DateOnly(2026, 10, 3)));
    }

    [Fact]
    public void Talep_satirlari_gunlere_acilir()
    {
        var day = Guid.NewGuid();
        var night = Guid.NewGuid();
        var rows = ShiftOptimizerController.ExpandDemand(Mon, Mon.AddDays(6), new[]
        {
            new ShiftOptimizerController.DemandInput(day, 2, new[] { 1, 2, 3, 4, 5 }, null, null),
            new ShiftOptimizerController.DemandInput(night, 1, null, null, " Ekip Lideri "),
            new ShiftOptimizerController.DemandInput(day, 3, null, Mon.AddDays(2), null),
        });
        Assert.Equal(5, rows.Count(r => r.ShiftId == day && r.Required == 2));
        Assert.Equal(7, rows.Count(r => r.ShiftId == night && r.Skill == "Ekip Lideri"));
        Assert.Single(rows, r => r.Required == 3 && r.Date == Mon.AddDays(2));
    }

    [Fact]
    public void Oneri_farki()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var d1 = Guid.NewGuid();
        var n1 = Guid.NewGuid();
        var asg = Guid.NewGuid();
        var current = new[] { (a, Mon, d1, asg), (b, Mon, d1, Guid.NewGuid()), (b, Mon.AddDays(1), d1, Guid.NewGuid()) };
        var proposed = new[]
        {
            new ShiftOptimizerController.ProposalItem(a, Mon, n1),          // change
            new ShiftOptimizerController.ProposalItem(b, Mon, d1),          // same
            new ShiftOptimizerController.ProposalItem(a, Mon.AddDays(1), d1), // add
        };
        var diff = ShiftOptimizerController.Diff(current, proposed);
        Assert.Equal("change", diff.Single(r => r.EmployeeId == a && r.Date == Mon).Kind);
        Assert.Equal(asg, diff.Single(r => r.EmployeeId == a && r.Date == Mon).CurrentAssignmentId);
        Assert.Equal("same", diff.Single(r => r.EmployeeId == b && r.Date == Mon).Kind);
        Assert.Equal("add", diff.Single(r => r.EmployeeId == a && r.Date == Mon.AddDays(1)).Kind);
        Assert.Equal("remove", diff.Single(r => r.EmployeeId == b && r.Date == Mon.AddDays(1)).Kind);
    }
}

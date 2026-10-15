using LeaveService.Controllers;
using LeaveService.Models;
using LeaveService.Services;
using Xunit;

namespace Leave.Tests;

/// <summary>Dalga 9: yarım gün tatil ve iş günü, saatlik izin, ekip çakışması, hak tahakkuku, Türkiye tatilleri.</summary>
public class Wave9LeaveTests
{
    [Fact]
    public void Yarim_gun_tatil_is_gununde_yarim_sayilir()
    {
        // 26-30 Mayıs 2026: 26 arife (yarım), 27-29 bayram, 30 Cumartesi.
        var h = TurkishHolidays.For(2026).ToDictionary(d => d.Date, d => d.IsHalfDay);
        Assert.Equal(1.5m, LeaveEntitlement.WorkingDays(new DateOnly(2026, 5, 25), new DateOnly(2026, 5, 31), h));
        Assert.Equal(5m, LeaveEntitlement.WorkingDays(new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 9), h));
        // 28 Ekim yarım, 29 Ekim tam gün.
        Assert.Equal(3.5m, LeaveEntitlement.WorkingDays(new DateOnly(2026, 10, 26), new DateOnly(2026, 10, 30), h));
    }

    [Fact]
    public void Saatlik_izin_kiraci_gunluk_saatiyle()
    {
        Assert.Equal(0.5m, LeaveEntitlement.HoursToDays(4, 8));
        Assert.Equal(0.27m, LeaveEntitlement.HoursToDays(2, 7.5m));
        Assert.Equal(0.27m, LeaveEntitlement.HoursToDays(2, 0));      // geçersiz ayar → 7,5
        Assert.Equal(9m, LeaveEntitlement.EffectiveDayHours(9));
        Assert.Equal(LeaveEntitlement.DayHours, LeaveEntitlement.EffectiveDayHours(null));
        Assert.Equal(LeaveEntitlement.DayHours, LeaveEntitlement.EffectiveDayHours(20));
    }

    [Fact]
    public void Ekip_cakismasi_esik_ve_kvkk()
    {
        // 10 kişilik ekip, 3 kişi izinli: (3+1)/(10+1) = %36 > %30
        var r = TeamConflict.Evaluate(10, 3, 30, true, revealCounts: false);
        Assert.True(r.Exceeds);
        Assert.Equal(36, r.Percent);
        Assert.Equal(3, r.Overlapping);
        // 10 kişide 1 kişi: %18 → uyarı yok
        Assert.False(TeamConflict.Evaluate(10, 1, 30, true, false).Exceeds);
        // 3 kişilik ekip: talep edene sayı gösterilmez, yalnızca eşik bilgisi
        var small = TeamConflict.Evaluate(3, 2, 30, true, revealCounts: false);
        Assert.True(small.Exceeds);
        Assert.Null(small.Overlapping);
        Assert.Null(small.TeamSize);
        // Yönetici küçük ekipte de sayıyı görür
        Assert.Equal(2, TeamConflict.Evaluate(3, 2, 30, true, revealCounts: true).Overlapping);
        // Kapalıysa uyarı yok; kimse izinli değilse uyarı yok
        Assert.False(TeamConflict.Evaluate(10, 9, 30, false, true).Exceeds);
        Assert.False(TeamConflict.Evaluate(1, 0, 30, true, true).Exceeds);
    }

    [Fact]
    public void Hak_yil_donumunde_tahakkuk_eder()
    {
        var hire = new DateOnly(2020, 11, 15);
        Assert.False(LeaveEntitlement.Accrued(hire, 2026, new DateOnly(2026, 10, 1)));
        Assert.True(LeaveEntitlement.Accrued(hire, 2026, new DateOnly(2026, 11, 15)));
        Assert.False(LeaveEntitlement.Accrued(new DateOnly(2026, 1, 1), 2026, new DateOnly(2026, 12, 31))); // 1 yıl dolmadı
    }

    [Fact]
    public void Yasal_hak_bakiyeyi_dusurmez_devri_korur()
    {
        Assert.Equal(14m, LeaveBalancesController.ProposedEntitled(null, 14));
        Assert.Null(LeaveBalancesController.ProposedEntitled(null, 0));
        var b = new LeaveBalance { EntitledDays = 25, CarriedOverDays = 5 }; // kendi hakkı 20
        Assert.Null(LeaveBalancesController.ProposedEntitled(b, 20));
        Assert.Equal(31m, LeaveBalancesController.ProposedEntitled(b, 26));   // 26 + 5 devir
        var low = new LeaveBalance { EntitledDays = 10 };
        Assert.Equal(20m, LeaveBalancesController.ProposedEntitled(low, 20)); // yaş kuralı (en az 20)
    }

    [Fact]
    public void Turkiye_tatilleri_2025_2030()
    {
        foreach (var y in new[] { 2025, 2026, 2027, 2028, 2029, 2030 })
        {
            Assert.True(TurkishHolidays.HasReligious(y));
            var days = TurkishHolidays.For(y);
            Assert.Equal(2, days.Count(d => d.Name.Contains("arifesi") && d.Name.Contains("Bayramı arifesi (yarım gün)") && !d.Name.Contains("Cumhuriyet")));
            Assert.Equal(3, days.Count(d => d.Name.Contains("Ramazan Bayramı (")));
            Assert.Equal(4, days.Count(d => d.Name.Contains("Kurban Bayramı (")));
            Assert.Contains(days, d => d.Date == new DateOnly(y, 10, 28) && d.IsHalfDay);
        }
        // Diyanet: 2028 Ramazan Bayramı 26 Şubat, Kurban Bayramı 5 Mayıs; 2030 Kurban 13 Nisan.
        Assert.Contains(TurkishHolidays.For(2028), d => d.Date == new DateOnly(2028, 2, 26) && d.Name == "Ramazan Bayramı (1. gün)");
        Assert.Contains(TurkishHolidays.For(2028), d => d.Date == new DateOnly(2028, 5, 5) && d.Name == "Kurban Bayramı (1. gün)");
        Assert.Contains(TurkishHolidays.For(2030), d => d.Date == new DateOnly(2030, 4, 13) && d.Name == "Kurban Bayramı (1. gün)");
        // 2029 Kurban arifesi 23 Nisan'a denk gelir: tam gün tatil yarım günü örter, adlar birleşir.
        var apr23 = TurkishHolidays.For(2029).Single(d => d.Date == new DateOnly(2029, 4, 23));
        Assert.False(apr23.IsHalfDay);
        Assert.Contains("Ulusal Egemenlik", apr23.Name);
        Assert.Contains("Kurban Bayramı arifesi", apr23.Name);
        // Tablo dışı yıl: yalnızca sabit tatiller; yarım günler istenmezse atlanır.
        Assert.False(TurkishHolidays.HasReligious(2031));
        Assert.Equal(7, TurkishHolidays.For(2031, includeHalfDays: false).Count);
    }

    [Fact]
    public void Otomatik_yukleme_bu_ve_gelecek_yil_surum_basina_bir_kez()
    {
        // Bu yıl da tamamlanır (yarım gün desteğinden önce yüklenmiş şirketlerde 28 Ekim / arifeler eksikti).
        Assert.Equal(new[] { 2026, 2027 }, HolidayAutoLoader.TargetYears(new DateOnly(2026, 10, 14)));
        Assert.Equal(new[] { 2026, 2027 }, HolidayAutoLoader.TargetYears(new DateOnly(2026, 12, 1)));
        Assert.True(HolidayAutoLoader.NeedsLoad(null));
        Assert.True(HolidayAutoLoader.NeedsLoad(1));
        Assert.False(HolidayAutoLoader.NeedsLoad(HolidayAutoLoader.DataVersion));   // şirketin sildiği tatil yeniden eklenmez
    }

    [Fact]
    public void Yarim_gun_tatiller_2026_ve_izin_gunu()
    {
        var all = TurkishHolidays.For(2026);
        var half = all.Where(d => d.IsHalfDay).Select(d => d.Date).ToList();
        Assert.Contains(new DateOnly(2026, 10, 28), half);
        Assert.Contains(new DateOnly(2026, 3, 19), half);   // Ramazan arifesi
        Assert.Contains(new DateOnly(2026, 5, 26), half);   // Kurban arifesi
        var h = all.ToDictionary(d => d.Date, d => d.IsHalfDay);
        // 28 Ekim 2026 Çarşamba: tek gün 0,5; 29 Ekim tam gün tatil.
        Assert.Equal(0.5m, LeaveEntitlement.WorkingDays(new DateOnly(2026, 10, 28), new DateOnly(2026, 10, 28), h));
        Assert.Equal(0.5m, LeaveEntitlement.WorkingDays(new DateOnly(2026, 10, 28), new DateOnly(2026, 10, 29), h));
    }
}

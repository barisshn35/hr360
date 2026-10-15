using TimeShiftService.Controllers;
using Xunit;

namespace TimeShift.Tests;

/// <summary>ML dalgası 2: puantaj denetimi için haftalık toplama (takma ad, eksik giriş/çıkış sayımı).</summary>
public class TimesheetAnomalyTests
{
    static readonly Guid A = Guid.Parse("0e879b9e-d72b-489f-aa5b-8291e0bcbefb");
    static readonly DateOnly Today = new(2026, 10, 7); // Çarşamba

    [Fact]
    public void Iso_hafta()
    {
        Assert.Equal("2026-W41", TimesheetAnomalyController.IsoWeek(new DateOnly(2026, 10, 5)));
        Assert.Equal("2026-W53", TimesheetAnomalyController.IsoWeek(new DateOnly(2027, 1, 1)));
    }

    [Fact]
    public void Haftalik_toplam_ve_eksik_kayit()
    {
        var entries = new[]
        {
            new TimesheetAnomalyController.EntryRow(A, new DateOnly(2026, 9, 28), 540, true, true),
            new TimesheetAnomalyController.EntryRow(A, new DateOnly(2026, 9, 29), 480, true, false),  // çıkış yok
            new TimesheetAnomalyController.EntryRow(A, new DateOnly(2026, 10, 5), 600, true, true),
            new TimesheetAnomalyController.EntryRow(A, new DateOnly(2026, 10, 7), 0, true, false),    // bugün: hâlâ içeride
        };
        var weeks = TimesheetAnomalyController.BuildWeeks("demo", entries, Today);
        Assert.Equal(2, weeks.Count);
        Assert.Equal(("2026-W40", 17.0, 2, 1), (weeks[0].Week, weeks[0].WorkedHours, weeks[0].DaysWorked, weeks[0].MissingPunches));
        Assert.Equal(("2026-W41", 10.0, 2, 0), (weeks[1].Week, weeks[1].WorkedHours, weeks[1].DaysWorked, weeks[1].MissingPunches));
        Assert.DoesNotContain(A.ToString("N"), weeks[0].Employee);
        Assert.Equal(TimesheetAnomalyController.Pseudonym("demo", A), weeks[0].Employee);
        Assert.NotEqual(TimesheetAnomalyController.Pseudonym("diger", A), weeks[0].Employee);
    }
}

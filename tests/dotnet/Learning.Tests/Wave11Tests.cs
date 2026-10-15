using LearningService.Models;
using LearningService.Services;
using Xunit;

namespace Learning.Tests;

/// <summary>Dalga 11 (madde 83): kariyer yolu hesapları.</summary>
public class CareerMathTests
{
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), Pasif = Guid.NewGuid();

    private static CareerStep Step(string title, int order, params (Guid Id, int Level)[] reqs) => new()
    {
        PositionTitle = title, StepOrder = order,
        Requirements = reqs.Select(r => new CareerStepRequirement { CompetencyId = r.Id, RequiredLevel = r.Level }).ToList(),
    };

    [Fact]
    public void Basamak_unvanla_bulunur_buyuk_kucuk_harf_ve_turkce_i_farki_onemsiz()
    {
        var steps = new List<CareerStep> { Step("Yazılım Mühendisi", 1), Step("KIDEMLI Yazılım Mühendisi", 2) };
        Assert.Equal(1, CareerMath.CurrentIndex(steps, "  kıdemli yazılım  mühendisi "));
        Assert.Equal(0, CareerMath.CurrentIndex(steps, "yazılım mühendisi"));
        Assert.Null(CareerMath.CurrentIndex(steps, "Muhasebe Uzmanı"));
        Assert.Null(CareerMath.CurrentIndex(steps, null));
    }

    [Fact]
    public void Acik_ve_hazirlik_pasif_yetkinlik_sayilmaz()
    {
        var s = Step("Kıdemli", 2, (A, 4), (B, 2), (Pasif, 5));
        var gaps = CareerMath.StepGaps(s, new Dictionary<Guid, int> { [A] = 2, [B] = 3 }, new HashSet<Guid> { A, B });
        Assert.Equal(2, gaps.Count);
        Assert.Equal(2, gaps.Single(g => g.CompetencyId == A).Gap);
        Assert.Equal(0, gaps.Single(g => g.CompetencyId == B).Gap);
        // (2 + 2) / (4 + 2) = %67
        Assert.Equal(67, CareerMath.Readiness(gaps));
    }

    [Fact]
    public void Beklenti_yoksa_hazirlik_yuzde_yuz_degerlendirilmemis_sifir_sayilir()
    {
        Assert.Equal(100, CareerMath.Readiness(new List<GapItem>()));
        var gaps = CareerMath.StepGaps(Step("X", 1, (A, 3)), new Dictionary<Guid, int>(), new HashSet<Guid> { A });
        Assert.Equal(0, CareerMath.Readiness(gaps));
        Assert.Null(gaps[0].Current);
    }

    [Fact]
    public void Dogrulama()
    {
        var known = new HashSet<Guid> { A };
        CareerStepInput S(string t, params CareerRequirementInput[] r) => new(t, null, null, r.ToList());
        Assert.Null(CareerMath.Validate("Mühendislik", null, new[] { S("Mühendis", new CareerRequirementInput(A, 3)), S("Kıdemli") }, known));
        Assert.NotNull(CareerMath.Validate("M", null, new[] { S("Mühendis") }, known));
        Assert.NotNull(CareerMath.Validate("Mühendislik", null, Array.Empty<CareerStepInput>(), known));
        Assert.NotNull(CareerMath.Validate("Mühendislik", null, new[] { S("Mühendis"), S("MÜHENDIS") }, known));
        Assert.NotNull(CareerMath.Validate("Mühendislik", null, new[] { S("Mühendis", new CareerRequirementInput(B, 3)) }, known));
        Assert.NotNull(CareerMath.Validate("Mühendislik", null, new[] { S("Mühendis", new CareerRequirementInput(A, 6)) }, known));
        Assert.NotNull(CareerMath.Validate("Mühendislik", null, new[] { S("Mühendis", new CareerRequirementInput(A, 2), new CareerRequirementInput(A, 3)) }, known));
    }
}

/// <summary>Dalga 11 (madde 84): son tarih durumu ve hatırlatma metinleri.</summary>
public class DueReminderTests
{
    [Theory]
    [InlineData(-1, "Overdue")]
    [InlineData(0, "DueSoon")]
    [InlineData(30, "DueSoon")]
    [InlineData(31, "Ok")]
    public void Durum(int days, string state) => Assert.Equal(state, DuePlan.State(days));

    [Fact]
    public void Metinler_kaynaga_ve_aliciya_gore()
    {
        var due = new DateOnly(2026, 11, 1);
        var (s1, b1) = DueReminderWorker.Message(DueReminderWorker.Training, ReminderPlan.D30, false, "Bilgi Güvenliği", "Ayşe Yılmaz", due, 20);
        Assert.Contains("yaklaşıyor", s1);
        Assert.Contains("20 gün sonra", b1);
        Assert.DoesNotContain("Ayşe", b1);
        var (s2, b2) = DueReminderWorker.Message(DueReminderWorker.Training, ReminderPlan.Expired, true, "Bilgi Güvenliği", "Ayşe Yılmaz", due, -3);
        Assert.Contains("gecikti", s2);
        Assert.Contains("Ayşe Yılmaz", b2);
        var (s3, b3) = DueReminderWorker.Message(DueReminderWorker.Osh, ReminderPlan.Expired, false, "Yüksekte çalışma", "x", due, 0);
        Assert.Contains("İSG", s3);
        Assert.Contains("bugün", b3);
    }
}
